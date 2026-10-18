using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using Hazel;
using static EndKnot.Translator;

namespace EndKnot.Roles;

// ネクロジャッカル — ジャッカル陣営のキラー。自分がキルした死体を通報した人は呪われ、
// その会議でネクロジャッカルが追放されなければ会議後に死ぬ。
// 呪いはネクロジャッカル自身・ジャッカル陣営の通報には付かない。通報の時点でネクロジャッカルが死んでいれば付かず、
// 会議中に死ねば解ける (執行する本人がいないため)。
// 呪いの印 (†) は会議中だけ全員に見える (モッドクライアントの描画用に呪いの一覧を SyncRoleData で送る)。
public class NecroJackal : RoleBase
{
    public static bool On;
    private static List<NecroJackal> Instances = [];

    private static OptionItem KillCooldown;
    private static OptionItem CanVent;
    private static OptionItem CanSabotage;
    private static OptionItem ImpostorVision;

    private byte NecroJackalId;

    // この人がキルした死体の PlayerId (ホストだけが持つ)
    private HashSet<byte> KilledBodies = [];

    // 今の会議で呪われている人
    private HashSet<byte> Cursed = [];

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(709700)
            .AutoSetupOption(ref KillCooldown, 22.5f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref CanVent, true)
            .AutoSetupOption(ref CanSabotage, true)
            .AutoSetupOption(ref ImpostorVision, true);
    }

    public override void Init()
    {
        On = false;
        Instances = [];
    }

    public override void Add(byte playerId)
    {
        On = true;
        Instances.Add(this);
        NecroJackalId = playerId;
        KilledBodies = [];
        Cursed = [];
    }

    public override void Remove(byte playerId)
    {
        Instances.Remove(this);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return CanVent.GetBool();
    }

    public override bool CanUseSabotage(PlayerControl pc)
    {
        return base.CanUseSabotage(pc) || (CanSabotage.GetBool() && pc.IsAlive());
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.SabotageButton?.ToggleVisible(CanSabotage.GetBool());
    }

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        opt.SetVision(ImpostorVision.GetBool());
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (killer.PlayerId == NecroJackalId) KilledBodies.Add(target.PlayerId);
    }

    // 通報の直後 (各役職の OnReportDeadBody より前) にホストが呼ぶ
    public static void OnAnyoneReport(PlayerControl reporter, NetworkedPlayerInfo target)
    {
        if (!On || !reporter || target == null) return;
        if (reporter.GetCustomRole().IsJackalTeamMember()) return;

        foreach (NecroJackal nj in Instances)
        {
            if (!nj.KilledBodies.Contains(target.PlayerId)) continue;

            PlayerControl necro = nj.NecroJackalId.GetPlayer();
            if (!necro || !necro.IsAlive()) continue;

            if (nj.Cursed.Add(reporter.PlayerId))
                Utils.SendRPC(CustomRPC.SyncRoleData, nj.NecroJackalId, reporter.PlayerId);
        }
    }

    public override void OnReportDeadBody()
    {
        // 告知は 1 回だけ (ネクロジャッカルが複数いても先頭のインスタンスがまとめて出す)
        if (Instances.Count == 0 || Instances[0] != this) return;

        HashSet<byte> cursed = [];
        foreach (NecroJackal nj in Instances) cursed.UnionWith(nj.Cursed);
        if (cursed.Count == 0) return;

        LateTask.New(() =>
        {
            if (!GameStates.IsMeeting) return;
            string names = string.Join(", ", cursed.Select(x => x.ColoredPlayerName()));
            string text = string.Format(GetString("NecroJackalCursedPlayersMessage"), names, CustomRoles.NecroJackal.ToColoredString());
            Utils.SendMessage(text, title: GetString("MessageTitle.Attention"), importance: MessageImportance.High);
        }, 10f, "NecroJackal Cursed Players Notify");
    }

    // 投票の結果が出た時 (追放の死因 = Vote) に呼ばれる。追放されなかったネクロジャッカルの呪いを執行する
    public static void OnCheckForEndVoting(params byte[] exileIds)
    {
        if (!On) return;

        try
        {
            List<byte> deaths = [];

            foreach (NecroJackal nj in Instances)
            {
                if (nj.Cursed.Count == 0) continue;

                PlayerControl necro = nj.NecroJackalId.GetPlayer();
                bool lifted = exileIds.Contains(nj.NecroJackalId) || !necro || !necro.IsAlive();

                if (!lifted)
                {
                    foreach (byte id in nj.Cursed)
                    {
                        PlayerControl pc = id.GetPlayer();
                        if (!pc || !pc.IsAlive() || exileIds.Contains(id) || Main.AfterMeetingDeathPlayers.ContainsKey(id) || deaths.Contains(id)) continue;

                        pc.SetRealKiller(necro);
                        deaths.Add(id);
                    }
                }

                nj.ClearCurses();
            }

            CheckForEndVotingPatch.TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.Curse, [.. deaths]);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    public override void AfterMeetingTasks()
    {
        // 投票の集計を通らずに会議が終わった時の取り残しを消す
        if (Cursed.Count > 0) ClearCurses();
    }

    private void ClearCurses()
    {
        Cursed.Clear();
        Utils.SendRPC(CustomRPC.SyncRoleData, NecroJackalId, byte.MaxValue);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        byte id = reader.ReadByte();
        if (id == byte.MaxValue) Cursed.Clear();
        else Cursed.Add(id);
    }

    public static string GetCurseMark(byte target, bool isMeeting)
    {
        if (!isMeeting || !On) return string.Empty;

        foreach (NecroJackal nj in Instances)
        {
            if (nj.Cursed.Contains(target))
                return Utils.ColorString(Utils.GetRoleColor(CustomRoles.NecroJackal), "†");
        }

        return string.Empty;
    }
}
