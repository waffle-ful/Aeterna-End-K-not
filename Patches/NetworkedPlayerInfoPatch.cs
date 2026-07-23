using System.IO;
using HarmonyLib;
using UnityEngine;

namespace EndKnot;

// TOHK の GameDataSerializePatch (Patches/NetworkedPlayrInfoPatch.cs) 移植: 会議中は NetworkedPlayerInfo の
// 自動 dirty シリアライズ (バニラの broadcast 経路) を保留する write-barrier。会議中にゲッサー射殺や
// 役職死などで Data が dirty になっても、vanilla クライアントへ「生の IsDead/Disconnected」入りロスターが
// 漏れて MeetingHud の状態機械を壊す (= 会議中発症の暗転, BUG-20260715-11 / -05 残差) のを発生側で防ぐ。
//
// TOHK と同じく、遮断時は ClearDirtyBits() で dirty を「捨てる」(保持しない):
// - 当初は「dirty 保持→会議明けにバニラ自然フラッシュ」の独自適合だったが、初実戦 (2026-07-23) で
//   会議クローズ=フラッシュ瞬間に公式鯖 Hacking キックと完全同時刻の相関を観測 (BUG-20260723-01)。
//   バニラ dirty フラッシュは PacketRateGate/分割保護を全迂回するため、TOHK 実戦実証済みの破棄式へ回帰。
// - 破棄しても実害はない: フォークは TOHK と違い Data.IsDead をマスクしない (ジャグリングは SetRole RPC 方式)
//   ため会議明けの全 Data 再送を必要とせず、死亡状態の真実は追放画面明けの RpcExiled 一斉送信
//   (AntiBlackout.RevertToActualRoleTypes) が保証する。会議後に再度 dirty になれば通常経路で自然に再同期する。
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

        // 会議中: TOHK 式に dirty を捨てて送信をブロック (caller は false を見て CancelMessage する)
        __instance.ClearDirtyBits();
        if (_blockedThisMeeting++ == 0) Logger.Info("Meeting write-barrier engaged — discarding auto Data broadcasts until the meeting ends (TOHK-style)", "MeetingDataBarrier");
        __result = false;
        return false;
    }

    public static void OnMeetingEnd()
    {
        if (_blockedThisMeeting > 0) Logger.Info($"Meeting write-barrier released — {_blockedThisMeeting} auto Data broadcasts were discarded during the meeting (no flush; roster truth is re-sent by the post-exile RpcExiled sweep)", "MeetingDataBarrier");
        _blockedThisMeeting = 0;
    }
}
