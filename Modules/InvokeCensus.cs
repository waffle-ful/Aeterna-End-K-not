using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
#if !ANDROID
using BepInEx.Unity.IL2CPP.Hook;
#endif
using Il2CppInterop.Runtime;

namespace EndKnot.Modules;

// il2cpp_runtime_invoke の通過をメソッド別に数える診断計器。
// 生成済みラッパーはゲーム本体のメソッドを全部この入口から呼び、値型の戻り値は 1 回ごとにゲーム側ヒープへ
// 箱詰めされる。どのメソッドが何回呼ばれ、うち何回が箱を出したかを数えれば、GC の燃料の出どころが
// 系統ブラケットの外も含めて順位で出る。呼び出し元は一定確率でスタックを採り、この DLL 内の最初のフレームで集計する。
// フックは最初に使った時に 1 回だけ掛け、以後は数えるかどうかのフラグだけを切り替える。
// フックは PC ではローダーの detour、Android ではランチャーのネイティブ層が公開している hook 関数で掛ける。
public static unsafe class InvokeCensus
{
    private const string Tag = "InvokeCensus";
    private const int TableSize = 8192; // 2 の冪。通過するメソッドは数百種なので十分に疎
    private const int SampleMask = 63;  // 平均 64 回に 1 回スタックを採る

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr RuntimeInvokeFn(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc);

    private static RuntimeInvokeFn _hook; // GC に回収させないため保持
    private static bool _installed;
#if ANDROID
    private static IntPtr _originalPtr;

    [DllImport("fusion", EntryPoint = "hook", ExactSpelling = true)]
    private static extern IntPtr FusionHook(IntPtr target, IntPtr detour, sbyte specialReturnBuffer);
#else
    private static INativeDetour _detour;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> _original;
#endif

    private static volatile bool _armed;
    private static int _mainThread;
    private static bool _inSample;
    private static uint _rng = 2463534242;

    private static readonly IntPtr[] Keys = new IntPtr[TableSize];
    private static readonly int[] Counts = new int[TableSize];
    private static int _used;
    private static long _overflow;
    private static long _offThread;
    private static readonly Dictionary<string, int> Sites = new(256);
    private static readonly System.Reflection.Assembly Self = typeof(InvokeCensus).Assembly;

    // ラッパーがゲーム側ヒープに文字列・配列・オブジェクト・箱を作る入口の確保量。
    // ゲーム本体の中からの確保はこの入口を通らないので、ここに出る量は mod 側のコードが作らせた分だけになる。
    private const int AllocSampleMask = 15; // 平均 16 回に 1 回、確保量を重みにして呼び出し元を採る
    private static readonly string[] AllocKindNames = ["str", "arr", "obj", "box"];
    private static readonly long[] AllocBytes = new long[4];
    private static readonly long[] AllocCalls = new long[4];
    private static readonly Dictionary<string, long> AllocSites = new(256);
    private static bool _inAllocSample;

    public static bool Running => _armed;

    /// <summary>seconds 秒間数えて、結果の各行を report へ渡す。</summary>
    public static string Start(float seconds, Action<string> report)
    {
        if (_armed) return "ERR invcensus already running";

        if (!_installed)
        {
#if ANDROID
            if (!NativeLibrary.TryLoad("libil2cpp.so", Self, null, out IntPtr lib)) return "ERR invcensus no libil2cpp";
            IntPtr target = NativeLibrary.GetExport(lib, "il2cpp_runtime_invoke");

            // export が本体へ飛ぶだけの 1 命令 (無条件分岐) なら、書き換える余地のある飛び先の本体へ掛ける
            uint insn = *(uint*)target;
            if ((insn & 0xFC000000) == 0x14000000) target += (nint)(((int)(insn << 6) >> 6) * 4L);

            _hook = Hook;
            IntPtr trampoline = FusionHook(target, Marshal.GetFunctionPointerForDelegate(_hook), 0);
            if (trampoline == IntPtr.Zero) return "ERR invcensus no trampoline";
            Volatile.Write(ref _originalPtr, trampoline);
#else
            IntPtr lib = NativeLibrary.Load("GameAssembly", typeof(InvokeCensus).Assembly, null);
            IntPtr target = NativeLibrary.GetExport(lib, "il2cpp_runtime_invoke");
            _hook = Hook;
            // 掛けた瞬間から別スレッドの呼び出しがフックへ入り得るので、元の処理への戻り口を先に確定させてから掛ける
            INativeDetour detour = INativeDetour.Create(target, _hook);
            detour.GenerateTrampoline<RuntimeInvokeFn>();
            if (detour.TrampolinePtr == IntPtr.Zero) return "ERR invcensus no trampoline";
            _original = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)detour.TrampolinePtr;
            detour.Apply();
            _detour = detour;
            InstallAllocHooks(lib);
#endif
            _installed = true;
        }

        Array.Clear(Keys, 0, TableSize);
        Array.Clear(Counts, 0, TableSize);
        Sites.Clear();
        AllocSites.Clear();
        Array.Clear(AllocBytes, 0, AllocBytes.Length);
        Array.Clear(AllocCalls, 0, AllocCalls.Length);
        _used = 0;
        _overflow = 0;
        _offThread = 0;
        _mainThread = Environment.CurrentManagedThreadId;

        long il0 = GcPrepass.BoehmUsedBytes();
        int gc0 = GcPrepass.BoehmCollectionCount();
        long t0 = Stopwatch.GetTimestamp();
        _armed = true;

        LateTask.New(() =>
        {
            _armed = false;
            double sec = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
            try { Report(sec, GcPrepass.BoehmUsedBytes() - il0, GcPrepass.BoehmCollectionCount() - gc0, report); }
            catch (Exception e) { Utils.ThrowException(e); report("ERR invcensus report failed"); }
        }, seconds, "InvokeCensus", false);

        return $"OK invcensus started {seconds:F0}s";
    }

    private static IntPtr Hook(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc)
    {
        if (_armed)
        {
            if (Environment.CurrentManagedThreadId != _mainThread) _offThread++;
            else Count(method);
        }

#if ANDROID
        // 掛けた後に戻り口が返る仕組みなので、その隙に入った別スレッドの呼び出しは戻り口が決まるまで待つ
        IntPtr original = Volatile.Read(ref _originalPtr);

        while (original == IntPtr.Zero)
        {
            Thread.SpinWait(32);
            original = Volatile.Read(ref _originalPtr);
        }

        return ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)original)(method, obj, parameters, exc);
#else
        return _original(method, obj, parameters, exc);
#endif
    }

    private static void Count(IntPtr method)
    {
        int i = (int)(((ulong)method >> 4) & (TableSize - 1));

        for (int probe = 0; probe < 64; probe++)
        {
            IntPtr k = Keys[i];

            if (k == method)
            {
                Counts[i]++;
                break;
            }

            if (k == IntPtr.Zero)
            {
                if (_used >= TableSize / 2)
                {
                    _overflow++;
                    return;
                }

                Keys[i] = method;
                Counts[i] = 1;
                _used++;
                break;
            }

            i = (i + 1) & (TableSize - 1);
            if (probe == 63) _overflow++;
        }

        // xorshift32。固定間隔だとループの周期と同期して特定の呼び出し元ばかり拾う
        uint x = _rng;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rng = x;
        if ((x & SampleMask) != 0 || _inSample) return;

        _inSample = true;

        try
        {
            string site = "(outside)";
            var st = new StackTrace(2, false);

            for (int f = 0; f < st.FrameCount; f++)
            {
                System.Reflection.MethodBase mb = st.GetFrame(f)?.GetMethod();
                Type dt = mb?.DeclaringType;
                if (dt == null || dt.Assembly != Self) continue;
                site = $"{dt.Name}.{mb.Name}";
                break;
            }

            string key = $"{site} <- {Describe(method, out bool boxed)}{(boxed ? " [box]" : "")}";
            Sites.TryGetValue(key, out int c);
            Sites[key] = c + 1;
        }
        catch { }
        finally { _inSample = false; }
    }

#if !ANDROID
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr StringNewUtf16Fn(IntPtr text, int length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ArrayNewFn(IntPtr klass, ulong length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ObjectNewFn(IntPtr klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ValueBoxFn(IntPtr klass, IntPtr data);

    // GC に回収させないため保持
    private static StringNewUtf16Fn _strHook;
    private static ArrayNewFn _arrHook, _arrSpecHook;
    private static ObjectNewFn _objHook;
    private static ValueBoxFn _boxHook;
    private static readonly List<INativeDetour> AllocDetours = [];

    private static delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr> _strOriginal;
    private static delegate* unmanaged[Cdecl]<IntPtr, ulong, IntPtr> _arrOriginal, _arrSpecOriginal;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _objOriginal;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> _boxOriginal;

    private static IntPtr Detour<T>(IntPtr lib, string export, T hook) where T : Delegate
    {
        INativeDetour detour = INativeDetour.Create(NativeLibrary.GetExport(lib, export), hook);
        detour.GenerateTrampoline<T>();
        if (detour.TrampolinePtr == IntPtr.Zero) throw new InvalidOperationException($"no trampoline for {export}");
        AllocDetours.Add(detour);
        return detour.TrampolinePtr;
    }

    // 戻り口を全部確定させてから掛ける (掛けた瞬間から他の呼び出しが入り得る)
    private static void InstallAllocHooks(IntPtr lib)
    {
        _strHook = StrHook;
        _arrHook = ArrHook;
        _arrSpecHook = ArrSpecHook;
        _objHook = ObjHook;
        _boxHook = BoxHook;
        _strOriginal = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)Detour(lib, "il2cpp_string_new_utf16", _strHook);
        _arrOriginal = (delegate* unmanaged[Cdecl]<IntPtr, ulong, IntPtr>)Detour(lib, "il2cpp_array_new", _arrHook);
        _arrSpecOriginal = (delegate* unmanaged[Cdecl]<IntPtr, ulong, IntPtr>)Detour(lib, "il2cpp_array_new_specific", _arrSpecHook);
        _objOriginal = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Detour(lib, "il2cpp_object_new", _objHook);
        _boxOriginal = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)Detour(lib, "il2cpp_value_box", _boxHook);
        foreach (INativeDetour d in AllocDetours) d.Apply();
    }

    private static IntPtr StrHook(IntPtr text, int length)
    {
        // 64bit の文字列 = 見出し 16B + 長さ 4B + UTF-16 の本体と終端
        if (_armed) NoteAlloc(0, 20L + 2L * (length + 1), IntPtr.Zero);
        return _strOriginal(text, length);
    }

    private static IntPtr ArrHook(IntPtr elementClass, ulong length)
    {
        // 64bit の配列 = 見出し 16B + 境界 8B + 長さ 8B + 要素
        if (_armed) NoteAlloc(1, 32L + (long)length * IL2CPP.il2cpp_class_array_element_size(elementClass), elementClass);
        return _arrOriginal(elementClass, length);
    }

    private static IntPtr ArrSpecHook(IntPtr arrayClass, ulong length)
    {
        if (_armed) NoteAlloc(1, 32L + (long)length * IL2CPP.il2cpp_array_element_size(arrayClass), arrayClass);
        return _arrSpecOriginal(arrayClass, length);
    }

    private static IntPtr ObjHook(IntPtr klass)
    {
        if (_armed) NoteAlloc(2, IL2CPP.il2cpp_class_instance_size(klass), klass);
        return _objOriginal(klass);
    }

    private static IntPtr BoxHook(IntPtr klass, IntPtr data)
    {
        if (_armed) NoteAlloc(3, IL2CPP.il2cpp_class_instance_size(klass), klass);
        return _boxOriginal(klass, data);
    }

    private static void NoteAlloc(int kind, long bytes, IntPtr klass)
    {
        if (Environment.CurrentManagedThreadId != _mainThread)
        {
            _offThread++;
            return;
        }

        AllocBytes[kind] += bytes;
        AllocCalls[kind]++;

        uint x = _rng;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rng = x;
        if ((x & AllocSampleMask) != 0 || _inAllocSample) return;

        _inAllocSample = true;

        try
        {
            string site = "(outside)";
            var st = new StackTrace(2, false);

            for (int f = 0; f < st.FrameCount; f++)
            {
                System.Reflection.MethodBase mb = st.GetFrame(f)?.GetMethod();
                Type dt = mb?.DeclaringType;
                // ラッパー経由の確保はこの計器の runtime_invoke フックを挟むので、計器自身の枠は飛ばす
                if (dt == null || dt.Assembly != Self || dt == typeof(InvokeCensus)) continue;
                site = $"{dt.Name}.{mb.Name}";
                break;
            }

            string type = klass == IntPtr.Zero ? "string" : Marshal.PtrToStringUTF8(IL2CPP.il2cpp_class_get_name(klass));
            string key = $"{site} <- {AllocKindNames[kind]}:{type}";
            AllocSites.TryGetValue(key, out long b);
            AllocSites[key] = b + bytes;
        }
        catch { }
        finally { _inAllocSample = false; }
    }
#endif

    private static string Describe(IntPtr method, out bool boxed)
    {
        boxed = false;

        try
        {
            IntPtr ret = IL2CPP.il2cpp_method_get_return_type(method);
            int t = IL2CPP.il2cpp_type_get_type(ret);

            // 0x02..0x0d = 組み込みの数値/真偽、0x11 = 構造体/enum、0x18/0x19 = nint/nuint、0x15 = ジェネリック (値型かは型を見て決める)
            boxed = t is >= 0x02 and <= 0x0d or 0x11 or 0x18 or 0x19 || (t == 0x15 && IL2CPP.il2cpp_class_is_valuetype(IL2CPP.il2cpp_class_from_type(ret)));

            IntPtr klass = IL2CPP.il2cpp_method_get_class(method);
            return $"{Marshal.PtrToStringUTF8(IL2CPP.il2cpp_class_get_name(klass))}.{Marshal.PtrToStringUTF8(IL2CPP.il2cpp_method_get_name(method))}";
        }
        catch
        {
            return $"0x{(long)method:X}";
        }
    }

    private static void Report(double sec, long il2Delta, int boehmGcs, Action<string> report)
    {
        var rows = new List<(string Name, int Count, bool Boxed)>(_used);
        long total = 0, boxedCalls = 0;

        for (int i = 0; i < TableSize; i++)
        {
            if (Keys[i] == IntPtr.Zero) continue;
            string name = Describe(Keys[i], out bool boxed);
            rows.Add((name, Counts[i], boxed));
            total += Counts[i];
            if (boxed) boxedCalls += Counts[i];
        }

        report($"OK invcensus sec={sec:F1} methods={rows.Count} calls={total} ({total / sec:F0}/s) boxed={boxedCalls} ({boxedCalls / sec:F0}/s = {boxedCalls * 32 / 1024.0 / sec:F0}KB/s) il2DeltaKB={(boehmGcs == 0 ? (il2Delta / 1024).ToString() : "n/a")} bgc={boehmGcs} offThread={_offThread} overflow={_overflow}");

        foreach ((string name, int count, bool boxed) in rows.OrderByDescending(r => r.Count).Take(30))
            report($"  M {count / sec,7:F0}/s {(boxed ? "box" : "   ")} {name}");

        foreach (KeyValuePair<string, int> kv in Sites.OrderByDescending(k => k.Value).Take(40))
            report($"  S {kv.Value * (SampleMask + 1) / sec,7:F0}/s {kv.Key}");

        long allocTotal = AllocBytes.Sum();
        report($"OK alloc via exports {allocTotal / 1024.0 / sec:F0}KB/s " + string.Join(" ", AllocKindNames.Select((n, k) => $"{n}={AllocBytes[k] / 1024.0 / sec:F0}KB/s({AllocCalls[k] / sec:F0}/s)")));

        foreach (KeyValuePair<string, long> kv in AllocSites.OrderByDescending(k => k.Value).Take(40))
            report($"  A {kv.Value * (AllocSampleMask + 1) / 1024.0 / sec,7:F1}KB/s {kv.Key}");

        report("OK invcensus end");
    }
}
