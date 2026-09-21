using System;
using System.Collections.Generic;
using EndKnot.Modules;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Bodyguard : RoleBase
{
    public static bool On;
    private static List<Bodyguard> Instances = [];
    private PlayerControl BodyguardPC;
    public override bool IsEnable => On;

    public override void Init()
    {
        On = false;
        Instances = [];
    }

    public override void Add(byte playerId)
    {
        On = true;
        Instances.Add(this);
        BodyguardPC = Utils.GetPlayerById(playerId);
    }

    public override void Remove(byte playerId)
    {
        Instances.Remove(this);
    }

    public override void SetupCustomOption()
    {
        SetupRoleOptions(8400, TabGroup.CrewmateRoles, CustomRoles.Bodyguard);

        BodyguardProtectRadius = new FloatOptionItem(8410, "BodyguardProtectRadius", new(0.5f, 5f, 0.5f), 1.5f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Bodyguard])
            .SetValueFormat(OptionFormat.Multiplier);

        BodyguardKillsKiller = new BooleanOptionItem(8411, "BodyguardKillsKiller", false, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Bodyguard]);
    }

    public static bool OnAnyoneCheckMurder(PlayerControl killer, PlayerControl target, bool check = false)
    {
        if (killer.IsCrewmate() || killer.PlayerId == target.PlayerId || killer.Is(CustomRoles.Bodyguard)) return true;

        foreach (Bodyguard bodyguard in Instances)
        {
            try
            {
                // Instances は役職変更でしか掃除されないので、死んだ本人も残り続ける。
                // 生死を見ないと死体や幽霊の座標で半径判定が通り、Suicide が no-op のまま
                // キルだけキャンセルされる (= 誰も死なない永久バリア)。
                if (bodyguard.BodyguardPC == null || !bodyguard.BodyguardPC.IsAliveWithConditions() || bodyguard.BodyguardPC.PlayerId == target.PlayerId) continue;

                if (!FastVector2.DistanceWithinRange(bodyguard.BodyguardPC.Pos(), target.Pos(), BodyguardProtectRadius.GetFloat())) continue;

                if (bodyguard.BodyguardPC.IsMadmate() && killer.Is(Team.Impostor))
                {
                    Logger.Info($"{bodyguard.BodyguardPC.GetRealName()} is a madmate, so they chose to ignore the murder scene", "Bodyguard");
                    continue;
                }

                // 身代わりが成立するかどうかだけを答え、打診では誰も死なせない。
                if (check) return false;

                // 撃ち返しは他の反撃役職 (ベテラン / ゴッデス / 呪狼 / 十字軍) と同じ入口を通す。
                if (BodyguardKillsKiller.GetBool() && AttackDefense.Retaliate(bodyguard.BodyguardPC, killer))
                    bodyguard.BodyguardPC.Kill(killer);
                else
                    killer.SetKillCooldown();

                bodyguard.BodyguardPC.Suicide(PlayerState.DeathReason.Sacrifice, killer);
                Logger.Info($"{bodyguard.BodyguardPC.GetRealName()} stood up and died for {target.GetRealName()}", "Bodyguard");

                if (bodyguard.BodyguardPC.AmOwner && target.Is(CustomRoles.President))
                    Achievements.Type.GetDownMrPresident.Complete();

                return false;
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        return true;
    }
}