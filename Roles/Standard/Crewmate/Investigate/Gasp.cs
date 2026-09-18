using System.Collections.Generic;
using static EndKnot.Options;

namespace EndKnot.Roles;

public class Gasp : RoleBase
{
    private const int Id = 702300;

    public static bool On;
    public override bool IsEnable => On;

    private static OptionItem TaskTriggerOpt;

    private bool CanSeeMark;
    private bool AfterAbility;
    private byte KillerPlayerId;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Gasp);

        TaskTriggerOpt = new IntegerOptionItem(Id + 10, "GaspTaskTrigger", new(0, 99, 1), 7, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Gasp])
            .SetValueFormat(OptionFormat.Pieces);

        OverrideTasksData.Create(Id + 20, TabGroup.CrewmateRoles, CustomRoles.Gasp);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        CanSeeMark = false;
        AfterAbility = false;
        KillerPlayerId = byte.MaxValue;
    }

    // OnCheckMurderAsTarget はキル打診 (check: true の下見) でも呼ばれるため、実際に死んだ後の
    // post-murder ディスパッチ (Patches/PlayerControlPatch.cs の MurderPlayerPatch.Postfix) からのみ発火させる。
    public static void OnAnyoneMurder(PlayerControl killer, PlayerControl target)
    {
        if (!On || killer == null || target == null || killer.PlayerId == target.PlayerId) return;
        if (Main.PlayerStates.TryGetValue(target.PlayerId, out PlayerState state) && state.Role is Gasp gasp)
            gasp.Mark(killer, target);
    }

    private void Mark(PlayerControl killer, PlayerControl target)
    {
        if (AfterAbility) return;

        if (target.GetTaskState().CompletedTasksCount >= TaskTriggerOpt.GetInt())
        {
            KillerPlayerId = target.Is(CustomRoles.Madmate) ? PickScapegoat(killer, target) : killer.PlayerId;
            LateTask.New(() =>
            {
                if (!GameStates.IsMeeting)
                {
                    CanSeeMark = true;
                    Utils.NotifyRoles(ForceLoop: true);
                }
                else
                    AfterAbility = true;
            }, 0.1f, "GaspMark");
        }
        else
            AfterAbility = true;
    }

    // マッドメイトのギャスプは★をキラーでなく無実のクルー (インポスター陣営・マッドメイト以外の生存者) に付ける。
    // 該当者がいなければ★は出ない。
    private static byte PickScapegoat(PlayerControl killer, PlayerControl target)
    {
        List<byte> candidates = [];
        foreach (PlayerControl pc in Main.EnumerateAlivePlayerControls())
        {
            if (pc.PlayerId == killer.PlayerId || pc.PlayerId == target.PlayerId) continue;
            if (!pc.IsCrewmate() || pc.IsMadmate()) continue;
            candidates.Add(pc.PlayerId);
        }

        return candidates.Count == 0 ? byte.MaxValue : candidates[IRandom.Instance.Next(candidates.Count)];
    }

    public override void OnReportDeadBody()
    {
        if (CanSeeMark)
        {
            CanSeeMark = false;
            AfterAbility = true;
        }
    }

    // ★は全員可視が仕様 (死に際の告発)。ギャスプは★が出る時点で既に死亡しているので、
    // seer を own id で絞ると本人 (死者) しか見えず役職が無意味になる。seer フィルタを足さないこと。
    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (!CanSeeMark || meeting || target.PlayerId != KillerPlayerId) return string.Empty;
        return "<color=#ab9d44>★</color>";
    }
}
