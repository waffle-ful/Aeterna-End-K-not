using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EndKnot.Modules;

// GC 先撃ち (preemptive full GC): stop-the-world のフル GC (CoreCLR 側実測 ~80ms) を「演出中で止まっても
// 見えない瞬間」(開始カウントダウンの頭 / ローディングバー表示中) に意図的に打ち、プレイ中・操作中の
// 自然発生ヒッチを減らす。NoS (Nebula) がプリロード完了時 GC.Collect(2) / コスメ画面クローズ時
// RefreshMemory でやっているのと同じ流儀。
//
// EnableAggressiveGcCleanup (既定OFF・毎会議/ゲーム終了の三連打) と違い:
//  - Resources.UnloadUnusedAssets を含まない (設定メニューキャッシュ ~41k GameObjects の全走査で
//    3-4 秒ストールする実証済みの主犯 — Main.cs の同フラグの説明参照)
//  - CoreCLR (mod 側) と il2cpp Boehm (ゲーム側) の両ヒープを打つ (三連打は CoreCLR のみだった)
//  - プレイ中・会議明けには打たない (演出中限定)
// 効果測定は Health.log の GCPRE 行 (この先撃ちの実測) と HITCH 行 (自然発生ヒッチ) の突き合わせで行う。
public static class GcPrepass
{
    // il2cpp_gc_collect / used_size は GcUafProbe と同じ GameAssembly エクスポート群 (起動時 probe で使用実績あり)。
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern void il2cpp_gc_collect(int maxGenerations);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern long il2cpp_gc_get_used_size();

    // reason 別 debounce: 同一トリガーの連打 (開始→キャンセル→開始等) だけを抑止する。全 reason 共有の
    // 単一窓にすると、countdown→loading の間隔 (手動 5s / autostart 下限 10s) が将来 3s 未満に変更された時に
    // loading 側が無音でスキップされる暗黙依存が生まれる — 別窓なら両方必ず撃てる。
    private static readonly System.Collections.Generic.Dictionary<string, long> LastRunTs = [];

    // Boehm (il2cpp 側) ヒープの使用バイト数。エクスポート欠落時は -1 (HITCH 計器のフォールバック値と一致)。
    public static long BoehmUsedBytes()
    {
        try { return il2cpp_gc_get_used_size(); }
        catch { return -1; }
    }

    // Boehm 側の GC 実行回数。used_size の増減だけでは「マークは走ったが解放が無かった」GC が
    // 見えない (生存オブジェクトばかりだと delta が正のまま) — incremental GC を UAF 対策で
    // 無効化しているため、100MB 級ヒープの stop-the-world マークがサブ秒ヒッチの盲点になる。
    // 取得不能な環境では -1 (呼び出し側は差分計算をスキップする)。
    public static int BoehmCollectionCount()
    {
        try { return Il2CppSystem.GC.CollectionCount(0); }
        catch { return -1; }
    }

    public static void Collect(string reason)
    {
        if (Main.EnablePreemptiveGc == null || !Main.EnablePreemptiveGc.Value) return;

        long now = Utils.TimeStamp;
        if (LastRunTs.TryGetValue(reason, out long last) && now - last < 3) return; // 同一トリガーの重複発火で二連打しない
        LastRunTs[reason] = now;

        try
        {
            var sw = Stopwatch.StartNew();
            long boehmBefore = BoehmUsedBytes();
            long clrBefore = GC.GetTotalMemory(false);
            GC.Collect();
            long clrMs = sw.ElapsedMilliseconds;
            // ここは il2cpp_gc_collect で必ず Boehm GC が1回走る「既知陽性」。bgc の前後が動かなければ
            // BoehmCollectionCount() が (例外ではなく) 定数を返す死んだ計器だと判別できる — HITCH 行の
            // bgc が動かないのを「Boehm 無罪」と誤読しないための較正アンカー。
            int bgcBefore = BoehmCollectionCount();
            try { il2cpp_gc_collect(2); }
            catch { /* エクスポート欠落環境では CoreCLR 側のみで縮退 */ }
            int bgcAfter = BoehmCollectionCount();
            long boehmAfter = BoehmUsedBytes();
            HealthLog.Note($"GCPRE reason={reason} clrMs={clrMs} totalMs={sw.ElapsedMilliseconds} clrMB={clrBefore / 1048576}->{GC.GetTotalMemory(false) / 1048576} boehmMB={(boehmBefore < 0 ? -1 : boehmBefore / 1048576)}->{(boehmAfter < 0 ? -1 : boehmAfter / 1048576)} bgc={bgcBefore}->{bgcAfter} t={now}");
            TransitionTimeline.Mark($"GCPRE:{reason}({sw.ElapsedMilliseconds}ms,boehm{(boehmBefore < 0 ? -1 : boehmBefore / 1048576)}->{(boehmAfter < 0 ? -1 : boehmAfter / 1048576)}MB)");
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }
}
