using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EndKnot.Modules;

// incremental GC を実行時に無効化する (NoS = Nebula on the Ship の Nebula.Utilities.IncrementalGcInvalidator と同手法の移植)。
// Based on https://github.com/Dolly1016/Nebula-Public/blob/master/NebulaPluginNova/Utilities/IncrementalGcInvalidator.cs (GPL-3.0)
//
// 背景 (2026-08-15 実測で確定): このビルドでは `Among Us_Data/boot.config` から `gc-max-time-slice` を
// 除去しても incremental GC は切れない。行が無くても既定の 3ms スライスで走る
// (行を消した状態のセッションが `gcIncremental=True sliceNs=3000000` を出力)。
// つまり上流 EHR / 当 fork の RemoveGcMaxTimeSlice プラグインはこのビルドでは何もしていない。
// incremental を本当に切れるのは、Boehm の実行時フラグ (`il2cpp_gc_is_incremental()` が読む
// グローバル変数) を直接 0 にするこの方式だけ。
//
// いつ切るか:
//   - barrier 無しの参照書き込みは incremental GC 中に書いた先を回収させる (GC UAF = coreclr AV のプロセス即死)。
//     同梱の BepInEx/core/Il2CppInterop.Runtime.dll は tools/InteropWbPatch で barrier 化した版で、
//     その目印 IL2CPP.WbStore があり 64bit プロセスなら incremental は ON のまま残す
//     (incremental ON・ScanMethodRefs=false の interop で 3 経路とも 64/64 回収 → 0/64 を実測)。
//     ON だと定期の収集が 3ms ずつに分かれ、試合中の最長フレームが ~30ms → ~24ms (実測)。
//   - それ以外 (元の Runtime のまま・32bit・ForceDisableIncrementalGc=true) は切る。incremental が切れていれば
//     barrier 無し interop でも GC UAF は原理的に起きない。x86 は ScanMethodRefs=true がブリックを起こすため
//     自己修復が降りる設計で、これが無いと防御が残らない。
//   - シーン遷移の強制フル収集 (ゲーム終了 ~360ms・ロビー復帰 ~370ms) はどちらでも同じ
//     (incremental モードが元々カバーしない領域)。
//
// フラグアドレスの特定手順 (NoS と同一):
//   GetProcAddress("il2cpp_gc_is_incremental") → jmp thunk (E9) を辿る →
//   関数先頭 24 バイトから `8B 05 disp32` (x64: mov eax,[rip+disp32]) / `A1 abs32` (x86) を探す →
//   見つからなければ最初の `E8 rel32` (call) を 1 段だけ掘って同じ走査を行う。
//
// NoS 版からの差分 (2点・いずれも安全側):
//   - 書き込み前に VirtualQuery でページが書込可能か確認する (読み取り専用ページへの書き込みは
//     .NET では catch できないアクセス違反 = プロセス即死になるため、事前に降りる)。
//   - 書き込み後に il2cpp_gc_is_incremental() が false になったことを検証し、
//     ならなければ元の値へロールバックする (誤ったアドレスを掴んでいた場合の被害を戻す)。
public static unsafe class IncrementalGcInvalidator
{
    private const byte OpCallRel32 = 0xE8;
    private const byte OpJmpRel32 = 0xE9;
    private const byte OpMovEaxRipRel = 0x8B; // 8B 05 disp32 = mov eax, [rip+disp32] (x64)
    private const byte ModRmRipRel = 0x05;
    private const byte OpMovEaxAbs32 = 0xA1;  // A1 abs32   = mov eax, [abs32]      (x86)

    private const int ScanBytes = 24;
    private const int MaxThunkHops = 5;

    // VirtualQuery の Protect 値のうち「書き込み可能」なもの。
    private const uint PageReadOnly = 0x02;
    private const uint PageReadWrite = 0x04;
    private const uint PageWriteCopy = 0x08;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteWriteCopy = 0x80;
    private const uint PageGuard = 0x100;
    private const uint MemCommit = 0x1000;

    private static bool _applied;

    // EndKnot.Logger ではなく BepInEx 側へ出す。Main.Load() の途中 (Main.cs:1237) で
    // CustomLogger.ClearLog() が走るため、それより前に EndKnot.Logger へ書いた行は log.html から消える。
    // 起動時の事実は LogOutput.log 側が正典 (前例: Modules/EmbeddedDeps.cs:13)。
    private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("EndKnot.IncrementalGc");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation lpBuffer, UIntPtr dwLength);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl, EntryPoint = "il2cpp_gc_is_incremental")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool IsIncrementalNative();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    // Main.Load() から一度だけ呼ばれる。ON のまま残す条件がそろっていれば現在の状態をログするだけで何もしない。
    // 全 Windows ユーザーが毎起動でここを通る。Main.Load() のこの位置には外側の
    // try/catch が無く、ここから例外が漏れると mod 丸ごと (役職もパッチも) ロードされないため、
    // この一機能の不具合が不釣り合いな被害にならないよう本体ごと保護する。
    public static void ApplyIfConfigured()
    {
        if (_applied) return;
        _applied = true;

        try { Apply(); }
        catch (Exception e)
        {
            Log.LogWarning($"INCGC: unexpected failure ({e.GetType().Name}: {e.Message})");

            // barrier の無い環境で ON のまま残すのが最悪なので、そこだけは切るのを 1 回だけ試し直す。
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (!Environment.Is64BitProcess || !HasBarrierRuntime()) && IsIncrementalNative())
                    Log.LogInfo($"INCGC: retry disable after failure: result={Disable()}");
            }
            catch (Exception e2) { Log.LogWarning($"INCGC: retry failed ({e2.GetType().Name}: {e2.Message}) — leaving the GC mode untouched"); }
        }
    }

    private static void Apply()
    {
        // アドレス特定と保護確認は kernel32 (GetModuleHandleW / GetProcAddress / VirtualQuery) に依存するため
        // Windows 専用。Android ホストではここへ来ると毎起動 DllNotFoundException になるので明示的に降りる
        // (既定 on で全環境が通る経路なので、握り潰さず理由をログに残す)。
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Log.LogInfo("INCGC: skipped (Windows-only; the flag lookup needs kernel32)");
            return;
        }

        bool before;
        try { before = IsIncrementalNative(); }
        catch (Exception e)
        {
            Log.LogWarning($"INCGC: il2cpp_gc_is_incremental unavailable ({e.Message}) — skipped");
            return;
        }

        string reason;
        if (Main.ForceDisableIncrementalGc.Value) reason = "ForceDisableIncrementalGc is on";
        else if (!Environment.Is64BitProcess) reason = "32-bit process";
        else if (!HasBarrierRuntime()) reason = "Il2CppInterop.Runtime is not the write-barrier build (no IL2CPP.WbStore)";
        else
        {
            Log.LogInfo(before
                ? "INCGC: incremental=True (left as-is; write-barrier Il2CppInterop.Runtime detected)"
                : "INCGC: incremental=False although the write-barrier Il2CppInterop.Runtime is present — it was turned off before the mod loaded (e.g. GC_DISABLE_INCREMENTAL=1 under [Environment] in doorstop_config.ini) and cannot be turned back on at runtime");
            return;
        }

        if (!before)
        {
            Log.LogInfo($"INCGC: incremental already off — nothing to do ({reason})");
            return;
        }

        Log.LogInfo($"INCGC: disabling incremental GC (before={before}; {reason})");
        Log.LogInfo($"INCGC: result={Disable()}");
    }

    // tools/InteropWbPatch が足す IL2CPP.WbStore(IntPtr, IntPtr) の有無で書き換え版かを判定する。
    // 元の Runtime が載っている (BepInEx/core が配布物と食い違う) 環境では false。
    private static bool HasBarrierRuntime()
    {
        try
        {
            return typeof(Il2CppInterop.Runtime.IL2CPP).GetMethod("WbStore", BindingFlags.Public | BindingFlags.Static, null, [typeof(IntPtr), typeof(IntPtr)], null) != null;
        }
        catch { return false; }
    }

    private static string Disable()
    {
        IntPtr flagAddr;
        try { flagAddr = DiscoverFlagAddress(); }
        catch (Exception e) { return $"FAILED (scan threw: {e.Message})"; }

        if (flagAddr == IntPtr.Zero)
            return "FAILED (flag address not found — instruction pattern did not match on this build)";

        if (!IsWritable(flagAddr))
            return $"FAILED (page at 0x{flagAddr.ToInt64():X} is not writable — refusing to write)";

        int* flag = (int*)flagAddr;
        int original = *flag;

        // 掴んだグローバルが本当に incremental フラグかの一致確認。
        // native の戻り値と読み値の真偽が食い違うなら別の変数を指している。
        if (original != 0 != IsIncrementalNative())
            return $"FAILED (sanity check mismatch at 0x{flagAddr.ToInt64():X}: value={original})";

        *flag = 0;

        bool after;
        try { after = IsIncrementalNative(); }
        catch (Exception e)
        {
            *flag = original;
            return $"FAILED (verification threw: {e.Message}; rolled back)";
        }

        if (after)
        {
            *flag = original;
            return "FAILED (still incremental after write; rolled back)";
        }

        return $"OK (flag at 0x{flagAddr.ToInt64():X}, {original} -> 0)";
    }

    private static bool HasProtection(IntPtr addr, uint mask)
    {
        if (VirtualQuery(addr, out MemoryBasicInformation mbi, (UIntPtr)(uint)sizeof(MemoryBasicInformation)) == UIntPtr.Zero)
            return false;

        if (mbi.State != MemCommit) return false;
        if ((mbi.Protect & PageGuard) != 0) return false;

        return (mbi.Protect & mask) != 0;
    }

    private static bool IsWritable(IntPtr addr)
    {
        return HasProtection(addr, PageReadWrite | PageWriteCopy | PageExecuteReadWrite | PageExecuteWriteCopy);
    }

    // 走査は生ポインタの追跡なので、読む前に必ず通す。バイト列一致 (0xE8 等) は命令境界を見ないため、
    // 別命令の即値が偶然一致すると無効アドレスを指しうる — 無効アドレスの逆参照は
    // .NET では catch できないアクセス違反 = プロセス即死になる (起動不能ブリックと同じ障害クラス)。
    // ページ境界をまたぐ場合があるので先頭と末尾の両方を確認する。
    private static bool CanRead(IntPtr addr, int length)
    {
        if (addr == IntPtr.Zero || length <= 0) return false;

        const uint readable = PageReadOnly | PageReadWrite | PageWriteCopy | PageExecuteRead | PageExecuteReadWrite | PageExecuteWriteCopy;

        return HasProtection(addr, readable) && HasProtection((IntPtr)((long)addr + length - 1), readable);
    }

    private static IntPtr DiscoverFlagAddress()
    {
        IntPtr module = GetModuleHandleW("GameAssembly.dll");
        if (module == IntPtr.Zero) return IntPtr.Zero;

        IntPtr export = GetProcAddress(module, "il2cpp_gc_is_incremental");
        if (export == IntPtr.Zero) return IntPtr.Zero;

        IntPtr func = ResolveJmpThunk(export);

        IntPtr direct = FindGlobalReadTarget(func);
        if (direct != IntPtr.Zero) return direct;

        // インライン化されていない場合、本体は最初の call の先に居る (1 段だけ掘る)。
        IntPtr callee = FindFirstCallTarget(func);
        if (callee == IntPtr.Zero) return IntPtr.Zero;

        return FindGlobalReadTarget(ResolveJmpThunk(callee));
    }

    // インポートサンクの E9 (jmp rel32) を辿って実体へ降りる。
    private static IntPtr ResolveJmpThunk(IntPtr addr)
    {
        for (int i = 0; i < MaxThunkHops; i++)
        {
            if (!CanRead(addr, 5)) return IntPtr.Zero;

            var p = (byte*)addr;
            if (*p != OpJmpRel32) break;
            addr = (IntPtr)((long)addr + 5 + *(int*)(p + 1));
        }

        return addr;
    }

    private static IntPtr FindFirstCallTarget(IntPtr funcAddr)
    {
        if (!CanRead(funcAddr, ScanBytes)) return IntPtr.Zero;

        var b = (byte*)funcAddr;

        for (int i = 0; i < ScanBytes - 4; i++)
        {
            if (b[i] != OpCallRel32) continue;
            return (IntPtr)((long)funcAddr + i + 5 + *(int*)(b + i + 1));
        }

        return IntPtr.Zero;
    }

    // グローバル変数読み出し命令のオペランドから、変数そのもののアドレスを割り出す。
    private static IntPtr FindGlobalReadTarget(IntPtr funcAddr)
    {
        if (!CanRead(funcAddr, ScanBytes)) return IntPtr.Zero;

        var b = (byte*)funcAddr;

        for (int i = 0; i < ScanBytes - 6; i++)
        {
            if (b[i] == OpMovEaxRipRel && b[i + 1] == ModRmRipRel)
            {
                int disp = *(int*)(b + i + 2);
                // x64 は RIP 相対 (次命令の先頭が基準)、x86 は絶対アドレスがそのまま入る。
                return Environment.Is64BitProcess ? (IntPtr)((long)funcAddr + i + 6 + disp) : (IntPtr)disp;
            }

            if (!Environment.Is64BitProcess && b[i] == OpMovEaxAbs32)
                return (IntPtr)(*(int*)(b + i + 1));
        }

        return IntPtr.Zero;
    }
}
