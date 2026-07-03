using System;
using System.Collections.Generic;
using System.Linq;

namespace EndKnot.Modules;

// 早期警報テレメトリ: 送信パケットサイズ・SnapTo枯渇・例外洪水を閾値監視し、
// HealthLog(Health+Timeline 両書き) + log.html + (critical 時のみ)ホスト通知の3段で可視化する。
// ネットワーク送信は一切増やさない観測専用レイヤー(HealthLog と同じ設計方針)。
public static class EarlyWarning
{
    private const long WarnDedupeSeconds = 30; // 種別ごとの Health/Timeline 再記録抑制
    private const long NotifyDedupeSeconds = 60; // 種別ごとのホスト通知レートリミット
    private const long ExceptionFloodWindowSeconds = 60;

    private static readonly Dictionary<string, long> LastWarned = new();
    private static readonly Dictionary<string, long> LastNotified = new();

    // --- SnapTo 消耗トラッキング ---
    private static int _lastSnapToValue;
    private static long _lastSnapToTickTs;

    // --- 例外洪水トラッキング(60秒周期スナップショット比較。Logger.ExceptionTags 自体は非破壊) ---
    private static readonly Dictionary<string, int> LastExceptionSnapshot = new();
    private static long _lastExceptionSnapshotTs;
    private static int _lastExceptionTotal;

    /// <summary>送信直前のパケットサイズ監視。CustomRpcSender.SendMessage / Utils.EndRPC から呼ぶ。</summary>
    public static void OnPacket(string name, int totalLen, int maxChunkLen, string sendOption)
    {
        try
        {
            // anti-cheat が存在しないカスタム鯖では kick リスクが無いので監視しない(Utils.cs の TP() と同じ判定式)。
            bool isOfficialServer = GameStates.CurrentServerType == GameStates.ServerType.Vanilla;
            if (!isOfficialServer) return;

            if (maxChunkLen >= 1000)
                Warn("packet", $"kind=packet name=\"{name}\" total={totalLen} maxChunk={maxChunkLen} opt={sendOption}", critical: true, "EarlyWarning.PacketNearKick");
            else if (maxChunkLen >= 900)
                Warn("packet", $"kind=packet name=\"{name}\" total={totalLen} maxChunk={maxChunkLen} opt={sendOption}", critical: false, null);
        }
        catch { }
    }

    /// <summary>1/sec 呼び出し(HealthLog.Tick から)。SnapTo 枯渇・例外洪水を判定。</summary>
    public static void Tick()
    {
        try { TickSnapTo(); } catch { }
        try { TickExceptionFlood(); } catch { }
    }

    private static void TickSnapTo()
    {
        if (GameStates.CurrentServerType != GameStates.ServerType.Vanilla) return; // 公式鯖のみ意味を持つ閾値

        int cur = Utils.NumSnapToCallsThisRound;
        long now = Utils.TimeStamp;

        int perSec = 0;
        if (_lastSnapToTickTs != 0 && now > _lastSnapToTickTs)
            perSec = (int)((cur - _lastSnapToValue) / (double)(now - _lastSnapToTickTs));

        _lastSnapToValue = cur;
        _lastSnapToTickTs = now;

        if (cur < 60) return;

        // 毎フレ TP 系の間引き漏れが疑われる急増ペース
        string suspect = perSec >= 10 ? " suspect=frame-tp-throttle-missing" : string.Empty;

        if (cur >= 80)
            Warn("snapto", $"kind=snapto count={cur} perSec={perSec} threshold=80{suspect}", critical: true, "EarlyWarning.SnapToExhausted");
        else
            Warn("snapto", $"kind=snapto count={cur} perSec={perSec} threshold=60{suspect}", critical: false, null);
    }

    private static void TickExceptionFlood()
    {
        long now = Utils.TimeStamp;
        if (_lastExceptionSnapshotTs != 0 && now - _lastExceptionSnapshotTs < ExceptionFloodWindowSeconds) return;

        string summary = Logger.GetExceptionTagsSummary();
        var current = new Dictionary<string, int>();

        if (!string.IsNullOrEmpty(summary))
        {
            foreach (string part in summary.Split(','))
            {
                int idx = part.LastIndexOf(':');
                if (idx <= 0) continue;
                if (int.TryParse(part[(idx + 1)..], out int cnt)) current[part[..idx]] = cnt;
            }
        }

        bool firstSnapshot = _lastExceptionSnapshotTs == 0;
        _lastExceptionSnapshotTs = now;

        // 検知トリガーは top10 集計(summary)ではなく全タグ合計で見る —
        // 累積の大きい古タグが top10 を占有して新規バーストタグが検知から漏れるのを防ぐ。top10 差分はラベル表示のみ。
        int total = Logger.GetExceptionTagsTotal();

        if (!firstSnapshot)
        {
            var deltaParts = new List<string>();

            foreach (KeyValuePair<string, int> kv in current)
            {
                int prev = LastExceptionSnapshot.GetValueOrDefault(kv.Key);
                // 減少 = ResetExceptionTags(GAMESTART 等)による初期化なので増分と誤認しない
                int delta = kv.Value < prev ? kv.Value : kv.Value - prev;
                if (delta > 0) deltaParts.Add($"{kv.Key}:+{delta}");
            }

            // 減少 = ResetExceptionTags 跨ぎなので新基準からの増分として扱う
            int totalDelta = total < _lastExceptionTotal ? total : total - _lastExceptionTotal;

            if (totalDelta >= 15)
            {
                string top = string.Join(",", deltaParts.Take(5));
                Warn("exflood", $"kind=exflood delta{ExceptionFloodWindowSeconds}s={totalDelta} top=[{top}]", critical: true, "EarlyWarning.ExceptionFlood");
            }
        }

        _lastExceptionTotal = total;
        LastExceptionSnapshot.Clear();
        foreach (KeyValuePair<string, int> kv in current) LastExceptionSnapshot[kv.Key] = kv.Value;
    }

    private static void Warn(string kind, string body, bool critical, string notifyLangKey)
    {
        long now = Utils.TimeStamp;

        if (LastWarned.TryGetValue(kind, out long last) && now - last < WarnDedupeSeconds) return;
        LastWarned[kind] = now;

        HealthLog.NoteAnom($"WARN {body} t={now}");
        Logger.Warn($"Early warning: {body}", "EarlyWarning");

        if (!critical || notifyLangKey == null) return;

        try
        {
            if (Main.EnableEarlyWarningNotify is { Value: false }) return;
        }
        catch { return; }

        if (LastNotified.TryGetValue(kind, out long lastN) && now - lastN < NotifyDedupeSeconds) return;
        LastNotified[kind] = now;

        try
        {
            if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost && PlayerControl.LocalPlayer != null)
                Utils.SendMessage(Translator.GetString(notifyLangKey), PlayerControl.LocalPlayer.PlayerId);
        }
        catch { }
    }
}
