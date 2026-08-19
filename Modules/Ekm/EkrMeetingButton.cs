using System;
using Hazel;
using UnityEngine;

namespace EndKnot.Modules.Ekm;

// EKN Wave 2 (docs/ekn-wave2-contract.md §1.2 on_meeting_pick — 会議ボタン)。
//
// Judge.cs:240-281 (SendRPC/ReceiveRPC/JudgeOnClick/CreateJudgeButton) と**同型**の複製。Judge 本体は
// fork 規則 (BackroomsLobby と同じ「既存コードのリファクタ禁止」精神を role コードにも適用) でリファクタ
// できないため、EKR 側にゼロから新規に書く (契約が求める「汎用 CreateEkrMeetingButton ヘルパーへ共通化」は
// 見送り — 既存 Judge には一切触れない)。
//
// 「on_meeting_pick ルールを持つ生存ホルダーの画面にだけ」ボタンを出す (契約 §1.2)。クリック→発火は
// EkrManager.HandleMeetingPickButton (= /pick と同じ関所 TryGateMeetingPick を通る「チャットコマンドの
// 糖衣」) へ合流する。
internal static class EkrMeetingButton
{
    // 2026-08-11 ご主人様裁定: 見送りを取り消し、CustomRPC.EkrMeetingPick (Modules/RPC.cs 末尾・255) を
    // 新規予約してホスト以外のモッドクライアントもボタンから発火できるようにする。
    private static void SendRPC(byte targetPlayerId)
    {
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.EkrMeetingPick, SendOption.Reliable, AmongUsClient.Instance.HostId);
        writer.Write(targetPlayerId);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    // ホストのみが処理する (RPCHandlerPatch は AmHost で無ければここへ来る前提のロールが多いが、二重に
    // 明示チェックする — §6 裁定「送信者を信用しない」の一環)。sender は RPC を投げてきた実際のオーナー
    // (Judge.ReceiveRPC と同じ引数付け)。
    public static void ReceiveRPC(MessageReader reader, PlayerControl sender)
    {
        byte targetId = reader.ReadByte();
        if (!AmongUsClient.Instance.AmHost) return;

        // §6: 送信者が実際に on_meeting_pick を持つ生存ホルダーであることをホスト側で再検証する
        // (クライアント申告=RPC 送信そのものを信用しない) — EkrManager.HandleMeetingPickButton 内の
        // TryGateMeetingPick がロール/生存/会議フェーズ/デデュープを全部やり直す。
        EkrManager.HandleMeetingPickButton(sender, targetId);
    }

    private static void OnClick(byte targetPlayerId)
    {
        Logger.Msg($"Click: ID {targetPlayerId}", "EKR Pick UI");

        PlayerControl target = Utils.GetPlayerById(targetPlayerId);
        if (!target || !target.IsAlive() || !GameStates.IsVoting) return;

        if (AmongUsClient.Instance.AmHost)
            EkrManager.HandleMeetingPickButton(PlayerControl.LocalPlayer, targetPlayerId);
        else
            SendRPC(targetPlayerId);
    }

    // Judge.CreateJudgeButton と同型 (テンプレ=CancelButton の複製・GameObject 名は既存 "ShootButton" を
    // 踏襲)。「既存 (ShootButton) と衝突しないか」の検証結果: Judge/Guesser 系/Swapper/Councillor/Nemesis の
    // 全てが同じ literal 名を使っているが、1クライアントは常に1役職しか持たないため、この
    // StartMeetingPatch 自体が「自分が on_meeting_pick 持ちの EKR ホルダーであるとき」しか呼ばれない
    // (下記 StartMeetingPatch 参照) — 同一クライアントで2つの CreateXButton が同時に走ることは構造的に
    // 無い。会議明けの掃除 (MeetingHudPatch.cs の ClearShootButton) はこの共有 literal 名前規約に乗っている
    // ため、専用の掃除コードを新設する必要は無い (死亡ターゲットの掃除は既存の汎用パスがそのまま拾う)。
    private static void CreateButton(MeetingHud __instance)
    {
        foreach (PlayerVoteArea pva in __instance.playerStates)
        {
            PlayerControl pc = Utils.GetPlayerById(pva.PlayerId);
            if (!pc || !pc.IsAlive()) continue;

            GameObject template = pva.Buttons.transform.Find("CancelButton").gameObject;
            GameObject targetBox = UnityEngine.Object.Instantiate(template, pva.transform);
            targetBox.name = "ShootButton";
            targetBox.transform.localPosition = new(-0.35f, 0.03f, -1.31f);
            var renderer = targetBox.GetComponent<SpriteRenderer>();
            // 裁定 #7 (2026-08-11): 新規画像リソースは追加しない — 既存アイコンの使い回し。
            renderer.sprite = CustomButton.Get("JudgeIcon");
            var button = targetBox.GetComponent<PassiveButton>();
            button.OnClick.RemoveAllListeners();
            button.OnClick.AddListener((Action)(() => OnClick(pva.PlayerId)));
        }
    }

    // MeetingHudPatch.cs の StartMeetingPatch ディスパッチ列 (Judge.StartMeetingPatch.Postfix 等と同じ帯)
    // から1行で呼ぶ。契約 §1.2「on_meeting_pick ルールを持つ生存ホルダーの画面にだけ出す」。
    //
    // 2026-08-11 ご主人様裁定 (a): 非ホストのクライアントには役職コード (EkrDefinition/Bound) が同期
    // されない (Bind() の書き込み元は TryAssign/ReloadLibrary/RestoreBindings 全てホスト限定・ディスク
    // 由来 — 同期 RPC が存在しない)。よって非ホストで HasOnMeetingPickLogic 相当の判定をすると常に
    // false になり、ボタンが永久に出ない (SendRPC が到達不能コードになる)。表示ゲートだけをホスト/
    // 非ホストで分ける:
    //   - ホスト: Bound を読めるので厳格判定を維持 (on_meeting_pick を持つ定義のときだけ表示 = 契約どおり)。
    //   - 非ホスト: 「EKR スロット役職を持つ生存者」まで緩和 (SetCustomRole で自分のスロットは分かる)。
    //     on_meeting_pick を持たない定義の保持者にもボタンが出てしまいうるが、クリックしても
    //     EkrManager.TryGateMeetingPick (ホスト側再検証) が無条件に落とすので実害は無い no-op。
    // ホスト側の最終検証 (TryGateMeetingPick/HandleMeetingPickButton) 自体は一切緩めない — ここは
    // 「ボタンを出すかどうか」という見た目の判定だけの分岐。
    public static void StartMeetingPatch(MeetingHud __instance)
    {
        CustomRoles myRole = PlayerControl.LocalPlayer.GetCustomRole();
        if (!EkrManager.IsEkrRole(myRole) || !PlayerControl.LocalPlayer.IsAlive()) return;

        bool shouldShow = AmongUsClient.Instance.AmHost
            ? EkrManager.HasOnMeetingPickLogic(myRole) // ホスト: 厳格判定 (Bound を読める)
            : true; // 非ホスト: スロット役職を持つ生存者なら表示 (実発火はホスト側で再検証)

        if (shouldShow) CreateButton(__instance);
    }
}
