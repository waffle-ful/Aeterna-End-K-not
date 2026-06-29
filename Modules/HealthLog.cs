using System;
using System.IO;
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

    private static bool Inited;
    public static string FilePath { get; private set; } // ライブ本体(EndKnot_Logs 直下の固定ファイル)。DumpLog がセッションフォルダへ同梱する時に参照。
    private static long StartTs;
    private static long LastBeatTs;
    private static long LastNormalLogTs;
    private static string LastState = "?";
    private static System.Diagnostics.Process Proc;

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

            StartTs = Utils.TimeStamp;
            try { Proc = System.Diagnostics.Process.GetCurrentProcess(); }
            catch { }

            Write($"SESSION start ver={Main.PluginVersion} t={StartTs}");
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

            string hb = $"t={now} up={now - StartTs} state={state} host={(host ? 1 : 0)} server={server} players={players} wsMB={wsMB} gcMB={gcMB} gc2={gen2}";
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

            string line = $"DC reason={reason} wasHost={(wasHost ? 1 : 0)} server={server} t={Utils.TimeStamp} str=\"{str}\"";
            Write(line);

            // kick は通常ログにも目立たせる(診断・将来のウォッチドッグ判定用)。
            if (reason == DisconnectReasons.Hacking)
                Logger.Warn($"HACKING kick detected: {line}", "Health");
            else
                Logger.Info($"disconnect: {line}", "Health");
        }
        catch (Exception e) { Utils.ThrowException(e); }
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
