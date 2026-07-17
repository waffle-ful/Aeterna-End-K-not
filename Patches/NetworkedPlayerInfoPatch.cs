using System.IO;
using HarmonyLib;
using UnityEngine;

namespace EndKnot;

// TOHK の GameDataSerializePatch (Patches/NetworkedPlayrInfoPatch.cs) 移植: 会議中は NetworkedPlayerInfo の
// 自動 dirty シリアライズ (バニラの broadcast 経路) を保留する write-barrier。会議中にゲッサー射殺や
// 役職死などで Data が dirty になっても、vanilla クライアントへ「生の IsDead/Disconnected」入りロスターが
// 漏れて MeetingHud の状態機械を壊す (= 会議中発症の暗転, BUG-20260715-11 / -05 残差) のを発生側で防ぐ。
//
// TOHK との差分 (意図的な適合):
// - TOHK は遮断時に ClearDirtyBits() で dirty を捨てる (会議明けに AntiBlackout.SendGameData で全員分を
//   再送する前提)。フォークには会議明けの全 Data 再送が無いため、dirty bit は「保持したまま」送信だけ
//   ブロックする — 会議終了 (MeetingHud 破棄) と同時にバニラの自動送信が溜まった変更を自然にフラッシュする。
// - 意図的送信 (Utils.SendGameData / SendGameDataTo / 単体 SendGameData / CNO・DummySpawner の名前復元) は
//   TOHK の SerializeMessageCount と同じ方式で IntentionalSends カウンタを立てて通す。カウンタ無しで
//   遮断すると手動 writer に空の Data ブロックが書かれ、クライアント側の Deserialize が壊れるため必須。
[HarmonyPatch(typeof(NetworkedPlayerInfo), nameof(NetworkedPlayerInfo.Serialize))]
internal static class NetworkedPlayerInfoSerializePatch
{
    // 意図的送信の囲い (TOHK: GameDataSerializePatch.SerializeMessageCount 相当)。
    // 送信側は必ず try/finally で ++/-- すること。
    public static int IntentionalSends;

    private static int _blockedThisMeeting;

    // kill switch (再ビルド不要のロールバック手段): EndKnot_DATA/disable_meeting_barrier.txt が存在すると
    // 本バリアと AntiBlackout.OnDisconnect の切断即応を丸ごと無効化し、導入前の挙動に戻す。
    // 30秒毎に再チェックするので、配信中でもファイルを置くだけで反映される (ゲーム再起動不要)。
    private static bool _killSwitch;
    private static float _killSwitchCheckedAt = float.MinValue;

    public static bool KillSwitchActive
    {
        get
        {
            if (Time.realtimeSinceStartup - _killSwitchCheckedAt > 30f)
            {
                _killSwitchCheckedAt = Time.realtimeSinceStartup;
                bool exists = File.Exists($"{Main.DataPath}/EndKnot_DATA/disable_meeting_barrier.txt");
                if (exists != _killSwitch) Logger.Warn($"Meeting write-barrier kill switch {(exists ? "ENGAGED — barrier and dummy-imp reassignment disabled" : "released — barrier re-enabled")}", "MeetingDataBarrier");
                _killSwitch = exists;
            }

            return _killSwitch;
        }
    }

    public static bool Prefix(NetworkedPlayerInfo __instance, [HarmonyArgument(1)] bool initialState, ref bool __result)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || !GameStates.InGame) return true;
        if (initialState) return true; // スポーン初期化 (initialState=true) は常に通す (偽会議トリック等が使う)
        if (IntentionalSends > 0) return true;
        if (Options.CurrentGameMode != CustomGameMode.Standard) return true;
        if (!GameStates.IsMeeting) return true;
        if (KillSwitchActive) return true;

        // 会議中: dirty は保持したまま送信だけ保留 (caller は false を見て CancelMessage する)
        if (_blockedThisMeeting++ == 0) Logger.Info("Meeting write-barrier engaged — deferring auto Data broadcast until the meeting ends", "MeetingDataBarrier");
        __result = false;
        return false;
    }

    public static void OnMeetingEnd()
    {
        if (_blockedThisMeeting > 0) Logger.Info($"Meeting write-barrier released — {_blockedThisMeeting} deferred Serialize calls will flush via the vanilla dirty path", "MeetingDataBarrier");
        _blockedThisMeeting = 0;
    }
}
