using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using InnerNet;

namespace EndKnot.Modules;

// 独立した観測レイヤー: heartbeat(状態 + メモリ) と切断/kick イベントを machine-readable な
// EndKnot-Health.log に書き出す。将来の外部ウォッチドッグ(クラッシュ→再起動 / テスト判定)が tail する土台。
// 既存の AutoRehost / EOSReLogin には一切触らない(読み取り専用の観測のみ)。
public static class HealthLog
{
    private const long HeartbeatIntervalSeconds = 5; // 専用ファイルへの heartbeat 間隔
    private const long NormalLogIntervalSeconds = 60; // 通常ログへの要約間隔(普段見る場所・低ノイズ)
    private const long MaxFileBytes = 3 * 1024 * 1024; // .prev 退避に失敗した時のサイズ上限
    private const long MaxTimelineBytes = 8 * 1024 * 1024; // Timeline は 8MB で .prev ローテート

    private static bool Inited;
    public static string FilePath { get; private set; } // ライブ本体(EndKnot_Logs 直下の固定ファイル)。DumpLog がセッションフォルダへ同梱する時に参照。
    private static string TimelinePath; // 横断セッション時系列ログ(EndKnot-Timeline.log)
    private static long StartTs;
    private static long LastBeatTs;
    private static long LastNormalLogTs;
    private static string LastState = "?";
    private static System.Diagnostics.Process Proc;
    private static long _gameStartTime;

    // --- 直近送信リングバッファ (zero I/O) ---
    private struct HostActionEntry
    {
        public string Tag;
        public int Len;
        public string Opt;
        public long Ts;
    }

    private static readonly HostActionEntry[] SendRing = new HostActionEntry[16];
    private static int SendRingIndex; // 次の書き込み位置

    private static void EnsureInit()
    {
        if (Inited) return;
        Inited = true;

        try
        {
            // 実ログと同じ場所に置く: 非 Android は <Desktop>/EndKnot_Logs (Utils.DumpLog の basePath と一致)。
            // セッション毎サブフォルダでなく root に固定ファイル + .prev で置き、将来のウォッチドッグが安定 tail できるように。
            string basePath = OperatingSystem.IsAndroid() ? Main.DataPath : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string dir = Path.Combine(basePath, "EndKnot_Logs");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "EndKnot-Health.log");
            TimelinePath = Path.Combine(dir, "EndKnot-Timeline.log");

            // 前セッションの末尾(クラッシュ直前の heartbeat)を .prev に退避してから新規セッションを開始。
            if (File.Exists(FilePath))
            {
                string prev = Path.Combine(dir, "EndKnot-Health.prev.log");

                try
                {
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(FilePath, prev);
                }
                catch
                {
                    try { if (new FileInfo(FilePath).Length > MaxFileBytes) File.Delete(FilePath); }
                    catch { }
                }
            }

            // Timeline は append-only。セッションをまたいで保持し、8MB で .prev ローテートのみ。
            try
            {
                if (File.Exists(TimelinePath) && new FileInfo(TimelinePath).Length > MaxTimelineBytes)
                {
                    string tlPrev = Path.Combine(dir, "EndKnot-Timeline.prev.log");
                    try
                    {
                        if (File.Exists(tlPrev)) File.Delete(tlPrev);
                        File.Move(TimelinePath, tlPrev);
                    }
                    catch { }
                }
            }
            catch { }

            StartTs = Utils.TimeStamp;
            try { Proc = System.Diagnostics.Process.GetCurrentProcess(); }
            catch { }

            string sessionLine = $"SESSION start ver={Main.PluginVersion} t={StartTs}";
            Write(sessionLine);
            Timeline(sessionLine);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    public static void Tick()
    {
        EnsureInit();

        long now = Utils.TimeStamp;

        // 状態遷移(ロビー入った / ゲーム開始 / メニュー戻り)は heartbeat の grid を待たず即記録。
        string state = GetState();

        if (state != LastState)
        {
            Write($"STATE {LastState}->{state} t={now}");
            LastState = state;
        }

        if (now - LastBeatTs < HeartbeatIntervalSeconds) return;
        LastBeatTs = now;

        try
        {
            bool host = false;
            try { host = AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost; }
            catch { }

            string server = "?";
            try { server = GameStates.CurrentServerType.ToString(); }
            catch { }

            int players = 0;
            try { players = GameData.Instance != null ? GameData.Instance.PlayerCount : 0; }
            catch { }

            long gcMB = GC.GetTotalMemory(false) / (1024 * 1024);
            long wsMB = 0;
            int gen2 = 0;
            try { if (Proc != null) { Proc.Refresh(); wsMB = Proc.WorkingSet64 / (1024 * 1024); } }
            catch { }
            try { gen2 = GC.CollectionCount(2); }
            catch { }

            // 直近送信リングから最新エントリを取得して HB に添付
            string lastSendSuffix = string.Empty;
            try
            {
                // SendRingIndex は「次に書く場所」なので、最新 = (SendRingIndex + 15) % 16
                ref HostActionEntry latest = ref SendRing[(SendRingIndex + 15) % 16];
                if (latest.Tag != null)
                {
                    long ageSec = now - latest.Ts;
                    lastSendSuffix = $" lastSend=\"{latest.Tag}\" lastLen={latest.Len} lastAgeSec={ageSec}";
                }
            }
            catch { }

            string hb = $"t={now} up={now - StartTs} state={state} host={(host ? 1 : 0)} server={server} players={players} wsMB={wsMB} gcMB={gcMB} gc2={gen2}{lastSendSuffix}";
            Write($"HB {hb}");

            // 普段見る通常ログにもメモリ + 状態の要約を低頻度で(最適化余地の把握用)。
            if (now - LastNormalLogTs >= NormalLogIntervalSeconds)
            {
                LastNormalLogTs = now;
                Logger.Info(hb, "Health");
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    public static void RecordDisconnect(DisconnectReasons reason, string stringReason)
    {
        EnsureInit();

        try
        {
            bool wasHost = false;
            try { wasHost = AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost; }
            catch { }

            string server = "?";
            try { server = GameStates.CurrentServerType.ToString(); }
            catch { }

            string str = (stringReason ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            if (str.Length > 200) str = str[..200];

            bool intentional = reason is DisconnectReasons.ExitGame or DisconnectReasons.Destroy
                or DisconnectReasons.Banned or DisconnectReasons.IncorrectVersion;

            long now = Utils.TimeStamp;

            if (!intentional)
            {
                // 異常切断: リングバッファを DCTX としてダンプ (crash前の最終送信コンテキスト)
                try
                {
                    for (int i = 0; i < SendRing.Length; i++)
                    {
                        int idx = (SendRingIndex + i) % SendRing.Length;
                        ref HostActionEntry e = ref SendRing[idx];
                        if (e.Tag == null) continue;
                        long ageSec = now - e.Ts;
                        Write($"DCTX send=\"{e.Tag}\" len={e.Len} opt={e.Opt} ageSec={ageSec}");
                    }
                }
                catch { }

                // log.html のバッファも flush (crash前の詳細ログを保全)
                Logger.FlushNow();
            }

            string line = $"DC reason={reason} intentional={(intentional ? 1 : 0)} wasHost={(wasHost ? 1 : 0)} server={server} t={now} str=\"{str}\"";
            Write(line);
            Timeline(line);

            // kick は通常ログにも目立たせる(診断・将来のウォッチドッグ判定用)。
            if (reason == DisconnectReasons.Hacking)
                Logger.Warn($"HACKING kick detected: {line}", "Health");
            else
                Logger.Info($"disconnect: {line}", "Health");
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    /// <summary>ホストが送信した RPC/パケットをゼロ I/O でリングバッファに記録。</summary>
    public static void RecordHostAction(string tag, int len, string opt)
    {
        // len <= 3 は空パケットノイズとして無視 (CustomRpcSender の既存しきい値に合わせる)
        if (len <= 3) return;

        // 送信ホットパス(CustomRpcSender.SendMessage)から呼ばれるので、観測が送信を絶対に壊さないよう全体を包む。
        try
        {
            ref HostActionEntry entry = ref SendRing[SendRingIndex];
            entry.Tag = tag;
            entry.Len = len;
            entry.Opt = opt;
            entry.Ts = Utils.TimeStamp;
            SendRingIndex = (SendRingIndex + 1) % SendRing.Length;
        }
        catch { }
    }

    /// <summary>横断セッション Timeline ログへの即時追記。sid= プレフィックスを自動付与。</summary>
    public static void Timeline(string line)
    {
        if (TimelinePath == null) return;
        try { File.AppendAllText(TimelinePath, $"sid={StartTs} {line}\n"); }
        catch { }
    }

    public static void RecordGameStart(CustomGameMode mode, int players, string rolesStr)
    {
        EnsureInit();

        try
        {
            _gameStartTime = Utils.TimeStamp;
            Logger.ResetExceptionTags();

            string line = $"GAMESTART gm={mode} players={players} roles=[{rolesStr}]";
            Write(line);
            Timeline(line);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    public static void RecordGameEnd(
        CustomGameMode mode,
        CustomWinner winnerTeam,
        System.Collections.Generic.HashSet<byte> winnerIds,
        System.Collections.Generic.HashSet<CustomRoles> winnerRoles,
        System.Collections.Generic.HashSet<AdditionalWinners> additionalWinnerTeams,
        int meetings,
        int players,
        bool allDead,
        bool isTimerEnd)
    {
        EnsureInit();

        try
        {
            long now = Utils.TimeStamp;
            long dur = _gameStartTime > 0 ? now - _gameStartTime : 0;

            // 異常フラグ計算
            bool flagShort = mode == CustomGameMode.Standard && dur < 30;
            bool flagNoWinner = winnerTeam is CustomWinner.None or CustomWinner.Draw or CustomWinner.Error or CustomWinner.Default;
            bool flagError = winnerTeam == CustomWinner.Error;
            bool flagAllDead = allDead && !isTimerEnd;
            bool flagUnattributed = !flagNoWinner && (winnerIds == null || winnerIds.Count == 0) && (winnerRoles == null || winnerRoles.Count == 0);

            string flags = string.Join(",", new[] {
                flagShort ? "short" : null,
                flagNoWinner ? "nowinner" : null,
                flagError ? "error" : null,
                flagAllDead ? "alldead" : null,
                flagUnattributed ? "unattributed" : null
            }.Where(f => f != null));

            string exTagsSummary = Logger.GetExceptionTagsSummary();
            string winIdsStr = winnerIds != null ? string.Join(",", winnerIds) : string.Empty;
            string winRolesStr = winnerRoles != null ? string.Join(",", winnerRoles) : string.Empty;
            string addStr = additionalWinnerTeams != null ? string.Join(",", additionalWinnerTeams) : string.Empty;

            string line = $"GAMEEND gm={mode} winner={winnerTeam} winIds=[{winIdsStr}] winRoles=[{winRolesStr}] add=[{addStr}] dur={dur} meetings={meetings} players={players} exTags=[{exTagsSummary}] flags=[{flags}]";
            Write(line);
            Timeline(line);

            bool anyBadFlag = flagShort || flagNoWinner || flagError || flagAllDead || flagUnattributed;
            if (anyBadFlag)
            {
                string anomLine = $"ANOM game winner={winnerTeam} dur={dur} flags=[{flags}]";
                Write(anomLine);
                Timeline(anomLine);
                Logger.Warn(anomLine, "Health");
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // 観測レイヤーの外部利用者(UiAnomalyWatch 等)が EndKnot-Health.log に 1 行追記するための公開窓口。
    // クラッシュ/切断行のすぐ隣に異常行を並べてウォッチドッグが tail できるようにする。
    public static void Note(string line)
    {
        EnsureInit();
        Write(line);
    }

    private static string GetState()
    {
        try
        {
            if (GameStates.InGame) return GameStates.IsMeeting ? "Meeting" : "InTask";
            if (GameStates.IsLobby) return "Lobby";
            if (GameStates.IsNotJoined) return "Menu";
            return AmongUsClient.Instance != null ? AmongUsClient.Instance.GameState.ToString() : "?";
        }
        catch { return "?"; }
    }

    private static void Write(string line)
    {
        if (FilePath == null) return;
        try { File.AppendAllText(FilePath, line + "\n"); }
        catch { }
    }
}

// 切断 / kick の理由を観測する自前パッチ(既存の AutoRehost / DisconnectInternalPatch とは独立・並走)。
[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.DisconnectInternal))]
internal static class HealthLogDisconnectPatch
{
    // ReSharper disable once UnusedMember.Global
    public static void Prefix(DisconnectReasons reason, string stringReason)
    {
        HealthLog.RecordDisconnect(reason, stringReason);
    }
}
