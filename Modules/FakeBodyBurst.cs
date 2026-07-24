using System.IO;

namespace EndKnot.Modules;

// 偽死体バースト (Overkiller / Butcher 系の「1 キルで大量の偽死体を撒く」演出) の
// spawn 数とレートを一元管理する緩和 + 計器レイヤー。
//
// 背景 (2026-07-24): 公式鯖で Overkiller の 30 体高速 spawn が host を reason=Hacking で
// 自己 DC させる事象を確認。判別の結果 サイズは無罪 (PacketSplitPatch は無発火・2800B の別
// パケットは分割で無事通過)。真因候補は「偽 PlayerControl を 1 フレーム 4 体ペースで高速
// spawn+murder+despawn する行為そのもの」を anti-cheat が咎める線 (5〜6 体出た時点で即 DC)。
// => checkLength / 分割は一切触らず (それらは無罪)、spawn の「数」と「間隔」だけを絞る。
//    rate 機序にも semantic (偽 spawn 検知) 機序にも安全に効く緩和。
//
// A/B 用 kill switch: EndKnot_DATA/disable_overkill_body_cap.txt が存在する間は旧挙動
// (30 体・高速バースト) に戻す。キル毎に素読みするので再ビルド不要。
internal static class FakeBodyBurst
{
    private const int CappedCount = 8;  // 緩和後の体数 (旧 30 から大幅減・総露出を下げる軸)
    private const int LegacyCount = 30; // 旧挙動の体数

    // 緩和時の 1 体ずつの送出間隔 (秒)。
    // ⚠️ RpcCreateDeadBody は DataFlagRateLimiter(Reliable 23/s ÷ calls:4 ≈ 5.75 体/s = 0.174s 間隔)で
    // 既にワイヤ整流される。ここを drain 間隔 (0.174s) 以下にすると limiter が律速のままで no-op になる
    // (0.2s だと 5/s ≈ drain と同率で効かない)。drain より確実に遅い 0.5s を取り、enqueue を律速に
    // することでワイヤ率を 2/s (発症時 5.75/s の 1/3 以下) に落とす。
    public const float SpacingSeconds = 0.5f;

    private static bool CapDisabled => File.Exists($"{Main.DataPath}/EndKnot_DATA/disable_overkill_body_cap.txt");

    // 緩和が有効か (= kill switch 不在)。false のとき呼び出し側は旧挙動を使う。
    public static bool Gentle => !CapDisabled;

    // 撒く偽死体の数。緩和時は削減、kill switch 有効時は旧 30 体。
    public static int BodyCount => CapDisabled ? LegacyCount : CappedCount;

    // バースト開始時に 1 行だけ計器ログを出す。将来キック再発時に spawn 数/ペースを突合できる。
    public static void LogBurst(string role, int count)
    {
        Logger.Info($"FakeBodyBurst: role={role} count={count} gentle={Gentle} (spacing={SpacingSeconds}s)", "FakeBodyBurst");
    }
}
