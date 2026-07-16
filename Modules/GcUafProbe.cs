using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace EndKnot.Modules;

// GC use-after-free 検出プローブ (docs/coreclr-av-chat-uaf-resume.md §1e が正典)。
// incremental GC のマークサイクル中に interop フィールド setter (write barrier 無し) で書いた
// il2cpp string が、後続のサイクルで回収されてしまうかを起動時に決定的に判定する。
// interop 再生成 (barrier 有効化) の前後で VULNERABLE→SAFE に変わることを確認する 1-bit テスト器。
// RunOnce() (手動診断ログ用) はフラグ Debug.GcUafBootProbe が true の時だけメインメニュー到達後に一度呼ばれる。
// Probe() 本体は GcUafSelfHeal (自己修復オーケストレータ) からもフラグ無条件で呼ばれる — 通常運用の主経路はこちら。
//
// v2: v1 (書き込みと判定を同一フレーム・同一ラウンドで完結させる方式) は2つの理由で再現に失敗した:
//   1. 保守的スタックスキャン — strPtr/weak のローカルがスタック/レジスタに残ったまま GC を回すと
//      Boehm がその残骸を root として string を延命させてしまう。
//   2. サイクル中の新規割当は black 扱い — 「書いたその場のサイクルで殺す」は原理的に成立しにくい。
// v2 は「書き込みフレームを確実に畳んでからスタックを掃除し、後続の GC で評価する」バッチ方式に変更。
public static class GcUafProbe
{
    private const int Batch = 64;
    private const int ChurnCount = 20000;

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern void il2cpp_gc_start_incremental_collection();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern int il2cpp_gc_collect_a_little();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern void il2cpp_gc_collect(int maxGenerations);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool il2cpp_gc_is_incremental();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern long il2cpp_gc_get_max_time_slice_ns();

    private static bool _done;

    public static void RunOnce()
    {
        if (_done) return;
        _done = true;

        Probe();
    }

    // v2 probe 本体。GcUafSelfHeal (自己修復オーケストレータ) から結果を判定に使うため
    // RunOnce から切り出したもの — アルゴリズム自体は無変更、戻り値 (collected 数 / 失敗時 null) を追加しただけ。
    internal static int? Probe()
    {
        try
        {
            Logger.Info($"gcIncremental={il2cpp_gc_is_incremental()} sliceNs={il2cpp_gc_get_max_time_slice_ns()}", "GcUafProbe");

            // Phase 0: バッチ生成。各 wrapper が自前の gchandle で outfit 自体を root するので
            // outfit の生存は保証されたまま、フィールドの中身 (string) だけを狙い撃ちできる。
            var outfits = new NetworkedPlayerInfo.PlayerOutfit[Batch]; // CLR 配列は Boehm 非スキャン。raw ptr の保持ではなく wrapper 参照なので安全
            for (int k = 0; k < Batch; k++) outfits[k] = new NetworkedPlayerInfo.PlayerOutfit();

            IntPtr klass = IL2CPP.il2cpp_object_get_class(IL2CPP.Il2CppObjectBaseToPtrNotNull(outfits[0]));
            IntPtr hatField = IL2CPP.GetIl2CppField(klass, "HatId");
            int hatOffset = (int)IL2CPP.il2cpp_field_get_offset(hatField);

            il2cpp_gc_collect(2); // outfit たちを「old で marked 済み」の落ち着いた状態にする

            // Phase 1: 書き込み。NoInlining ヘルパーでフレームを都度畳み、strPtr/weak をスタックに残さない。
            var weakHandles = new nint[Batch];
            var strPtrs = new IntPtr[Batch];

            for (int k = 0; k < Batch; k++)
                WriteOne(outfits[k], hatOffset, k, out weakHandles[k], out strPtrs[k]);

            // Phase 2: スタック浄化。Phase 1 の残骸 (ポインタのコピー) をゼロ埋め+演算で上書きする。
            ScrubStack();

            // Phase 3a: in-flight incremental サイクルを完走させて狙う。
            il2cpp_gc_start_incremental_collection();
            int guardA = 10000;
            while (il2cpp_gc_collect_a_little() != 0 && guardA-- > 0) { }

            // 中間評価 (戦略Aでの回収数)。ハンドルはまだ解放しない — 最終評価まで生死判定に使う。
            int a = EvaluateDead(outfits, hatOffset, weakHandles, strPtrs);

            // Phase 3b: 大量の使い捨て il2cpp 割当で churn を起こし、次の(minor/incremental)サイクルを誘発する。
            // ここでは full collect (maxGenerations=2) を評価前に呼ばない — root からの完全再走査は
            // outfit を辿り直して string を救済してしまい、テストが常に SAFE に倒れるため。
            for (int m = 0; m < ChurnCount; m++)
            {
                IL2CPP.ManagedStringToIl2Cpp("gcuaf_churn_" + m);
                if (m % 500 == 0) il2cpp_gc_collect_a_little();
            }

            // Phase 4: 最終評価。
            int collected = EvaluateDead(outfits, hatOffset, weakHandles, strPtrs);
            int b = collected - a;

            foreach (nint h in weakHandles) IL2CPP.il2cpp_gchandle_free(h);

            il2cpp_gc_collect(2); // 後始末 (評価は終わっているので安全)

            Logger.Warn($"GCUAF-PROBE: {(collected > 0 ? "VULNERABLE" : "SAFE")} collected={collected}/{Batch} (strategyA={a}, strategyB={b})", "GcUafProbe");

            return collected;
        }
        catch (Exception e)
        {
            Logger.Error($"GcUafProbe failed: {e.Message}", "GcUafProbe");
            return null;
        }
    }

    // フィールド書き込み + strPtr/weakref の取得を専用フレームに閉じ込める。呼び出し元に戻る時点で
    // ローカル (outfitPtr 等) のフレームは畳まれる — 生存させたい値だけ out で持ち帰る。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteOne(NetworkedPlayerInfo.PlayerOutfit outfit, int hatOffset, int k, out nint weakHandle, out IntPtr strPtr)
    {
        outfit.HatId = "gcuaf_probe_" + k; // ★テスト対象 = 実際に生成された interop setter
        IntPtr outfitPtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(outfit);
        strPtr = Marshal.ReadIntPtr(outfitPtr, hatOffset); // setter が書いた il2cpp string
        weakHandle = IL2CPP.il2cpp_gchandle_new_weakref(strPtr, false);
    }

    // Phase 1 が残した可能性のあるスタック/レジスタ上のポインタ残骸を明示的に上書きする。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ScrubStack()
    {
        Span<byte> buf = stackalloc byte[32768];
        buf.Clear();

        // レジスタ待避域も動かしておく (定数畳み込みされないよう実行時値を種にする)。
        long acc = Environment.TickCount64;
        for (int i = 0; i < 4096; i++) acc = acc * 1103515245 + 12345 + i;
        if (acc == long.MinValue) Logger.Info("scrub-dummy", "GcUafProbe"); // 到達しない: 最適化除去防止のダミー分岐
    }

    private static int EvaluateDead(NetworkedPlayerInfo.PlayerOutfit[] outfits, int hatOffset, nint[] weakHandles, IntPtr[] strPtrs)
    {
        int n = 0;
        for (int k = 0; k < outfits.Length; k++)
        {
            bool dead = IL2CPP.il2cpp_gchandle_get_target(weakHandles[k]) == IntPtr.Zero;
            bool fieldStillPointsThere = Marshal.ReadIntPtr(IL2CPP.Il2CppObjectBaseToPtrNotNull(outfits[k]), hatOffset) == strPtrs[k];
            if (dead && fieldStillPointsThere) n++;
        }

        return n;
    }

    // HealthLog のセッション開始行専用: プローブフラグと無関係に常時呼べる軽量チェック。
    internal static bool IsIncrementalGc()
    {
        try { return il2cpp_gc_is_incremental(); }
        catch { return false; }
    }
}
