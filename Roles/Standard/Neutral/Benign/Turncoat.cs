using System.Collections.Generic;
using System.Linq;
using EndKnot.Modules;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Turncoat : RoleBase
{
    private const int Id = 703900;
    public static bool On;
    public static List<Turncoat> Instances = [];

    private static OptionItem CanTargetImpostor;
    private static OptionItem CanTargetNeutral;
    private static OptionItem CanTargetMadmate;
    private static OptionItem KnowTargetRole;
    private static OptionItem CanDisguise;
    private static OptionItem DisguiseDuration;
    private static OptionItem DisguiseCooldown;

    private byte TurncoatId = byte.MaxValue;
    public byte TargetId = byte.MaxValue;
    public bool IsTargetDied;

    private bool IsDisguised;
    private long DisguiseEndTimeStamp;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref CanTargetImpostor, false)
            .AutoSetupOption(ref CanTargetNeutral, false)
            .AutoSetupOption(ref CanTargetMadmate, false)
            .AutoSetupOption(ref KnowTargetRole, true)
            .AutoSetupOption(ref CanDisguise, false)
            .AutoSetupOption(ref DisguiseDuration, 10, new IntegerValueRule(1, 60, 1), OptionFormat.Seconds, overrideParent: CanDisguise)
            .AutoSetupOption(ref DisguiseCooldown, 30, new IntegerValueRule(5, 180, 5), OptionFormat.Seconds, overrideParent: CanDisguise);
    }

    public override void Init()
    {
        On = false;
        Instances = [];
        TurncoatId = byte.MaxValue;
        TargetId = byte.MaxValue;
        IsTargetDied = false;
        IsDisguised = false;
        DisguiseEndTimeStamp = 0;
    }

    public override void Add(byte playerId)
    {
        On = true;
        Instances.Add(this);
        TurncoatId = playerId;
        TargetId = byte.MaxValue;
        IsTargetDied = false;
        IsDisguised = false;
        DisguiseEndTimeStamp = 0;

        LateTask.New(() => AssignTarget(playerId), 3f, "Turncoat.AssignTarget");
    }

    public override void Remove(byte playerId)
    {
        // 役職が入れ替わる (ターゲット切断でオポチュニストへ変わる等) と以後この instance は更新されないので、
        // 変身したままの見た目が恒久的に残る。台帳から外す前に必ず戻す。
        if (playerId == TurncoatId) RevertDisguise(Utils.GetPlayerById(playerId));

        Instances.RemoveAll(x => x.TurncoatId == playerId);
        if (Instances.Count == 0) On = false;
    }

    private void AssignTarget(byte playerId)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        List<PlayerControl> candidates = Main.EnumeratePlayerControls()
            .Where(pc =>
            {
                if (pc.PlayerId == playerId) return false;
                if (pc.Is(CustomRoles.GM)) return false;
                if (pc.Is(CustomRoles.Turncoat)) return false;

                // EHR の CustomRoleTypes に Madmate は無い (アドオン扱い)。マッドメイトは
                // 素の役職としてはクルーに化けるので、先にここで拾わないと
                // CanTargetMadmate がマッドメイトではなくカヴンに掛かってしまう。
                if (pc.Is(CustomRoles.Madmate) || pc.GetCustomRole().IsMadmate()) return CanTargetMadmate.GetBool();

                CustomRoleTypes roleType = pc.GetCustomRole().GetCustomRoleTypes();
                return roleType switch
                {
                    CustomRoleTypes.Crewmate => true,
                    CustomRoleTypes.Impostor => CanTargetImpostor.GetBool(),
                    CustomRoleTypes.Neutral => CanTargetNeutral.GetBool(),
                    _ => false
                };
            })
            .ToList();

        if (candidates.Count == 0)
            candidates = Main.EnumeratePlayerControls().Where(pc => pc.PlayerId != playerId && !pc.Is(CustomRoles.GM)).ToList();

        if (candidates.Count == 0) return;

        PlayerControl chosen = candidates[IRandom.Instance.Next(candidates.Count)];
        TargetId = chosen.PlayerId;

        Logger.Info($"Turncoat {playerId} target: {chosen.GetNameWithRole().RemoveHtmlTags()}", "Turncoat");

        PlayerControl turncoat = Utils.GetPlayerById(playerId);
        if (turncoat != null) Utils.NotifyRoles(SpecifySeer: turncoat, SpecifyTarget: turncoat);
    }

    public override bool CanUseKillButton(PlayerControl pc) => false;

    public override bool CanUseImpostorVentButton(PlayerControl pc) => false;

    public override void OnPet(PlayerControl pc)
    {
        if (!CanDisguise.GetBool())
        {
            base.OnPet(pc);
            return;
        }

        if (!AmongUsClient.Instance.AmHost || !pc.IsAlive()) return;

        if (IsDisguised)
        {
            pc.Notify(GetString("TurncoatAlreadyDisguised"));
            return;
        }

        // 変身先は「近くに居る生存者」。死亡者・切断者・装飾オブジェクト (PlayerId >= 200) は除く。
        if (!FastVector2.TryGetClosestPlayerInRangeTo(pc, pc.GetKillDistance(), out PlayerControl target, x => x.PlayerId < 200 && x.Data is { Disconnected: false, IsDead: false }))
        {
            pc.Notify(GetString("TurncoatNoDisguiseTarget"));
            return;
        }

        IsDisguised = true;
        DisguiseEndTimeStamp = Utils.TimeStamp + DisguiseDuration.GetInt();
        pc.RpcShapeshift(target, !DisableAllShapeshiftAnimations.GetBool());

        // 変身が解けてから充填が始まるようにする (変身中の時間は待ち時間に含めない)。
        pc.AddAbilityCD(DisguiseCooldown.GetInt() + DisguiseDuration.GetInt());

        pc.Notify(string.Format(GetString("TurncoatDisguised"), target.GetRealName()));
    }

    /// <summary>
    ///     変身を解いて元の見た目に戻す。解除の animate は変身時と同じ値でなければならない —
    ///     名前の書き戻しが animate 付きの経路にしか乗っていないため。
    /// </summary>
    private void RevertDisguise(PlayerControl pc)
    {
        if (!IsDisguised) return;

        IsDisguised = false;
        DisguiseEndTimeStamp = 0;

        // 本人が抜けた後 (切断経路でも Remove が呼ばれる) と試合終了後は撃たない。
        // 終了後の復元は RestoreOnGameEnd が OutroPatch から受け持つ。
        if (pc == null || pc.Data == null || pc.Data.Disconnected || GameStates.IsEnded) return;

        pc.RpcShapeshift(pc, !DisableAllShapeshiftAnimations.GetBool());
    }

    /// <summary>
    ///     試合終了時の見た目の復元。CheckGameEndPatch の Camouflage.RpcSetSkin は
    ///     Camouflager 不在 + コミュサボ変装 OFF という典型設定では先頭ガードで降りるので、
    ///     変身したまま決着すると終了画面に相手の姿が残ったままになる。
    /// </summary>
    public void RestoreOnGameEnd(byte id)
    {
        if (!IsDisguised) return;

        IsDisguised = false;
        DisguiseEndTimeStamp = 0;

        PlayerControl pc = Utils.GetPlayerById(id);
        if (pc == null || pc.Data == null || pc.Data.Disconnected) return;

        pc.RpcShapeshift(pc, !DisableAllShapeshiftAnimations.GetBool());
    }

    public override void OnReportDeadBody()
    {
        RevertDisguise(Utils.GetPlayerById(TurncoatId));
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        // 下の早期 return より手前で見ること — ターゲットが死んだ後や未割り当ての間も変身は解かなければならない。
        if (IsDisguised && (!pc.IsAlive() || Utils.TimeStamp >= DisguiseEndTimeStamp))
        {
            bool alive = pc.IsAlive();
            RevertDisguise(pc);
            if (alive) pc.Notify(GetString("TurncoatDisguiseEnded"));
        }

        if (IsTargetDied || TargetId == byte.MaxValue) return;
        if (!pc.IsAlive()) return;

        PlayerControl target = Utils.GetPlayerById(TargetId);
        if (target == null || target.Data.Disconnected)
        {
            // Target disconnected — change to Opportunist
            pc.RpcSetCustomRole(CustomRoles.Opportunist);
            return;
        }

        if (!target.IsAlive())
        {
            IsTargetDied = true;
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }
    }

    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if (base.KnowRole(seer, target)) return true;
        if (!KnowTargetRole.GetBool()) return false;
        // 原典はターゲットが死んで初めて役職を明かす。開幕から見えると
        // 「どの陣営を負けさせればいいか」が初手で確定してしまう。
        if (!IsTargetDied) return false;
        return seer.PlayerId == TurncoatId && target.PlayerId == TargetId;
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != TurncoatId) return string.Empty;
        if (seer.PlayerId == target.PlayerId) return string.Empty;
        if (target.PlayerId != TargetId) return string.Empty;
        if (meeting) return string.Empty;
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Turncoat), "★");
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != TurncoatId) return string.Empty;
        if (TargetId == byte.MaxValue) return string.Empty;
        PlayerControl target = Utils.GetPlayerById(TargetId);
        if (target == null) return string.Empty;
        string targetName = target.GetRealName();
        if (IsTargetDied && Main.PlayerStates.TryGetValue(TargetId, out PlayerState ps))
        {
            CustomRoles role = ps.MainRole;
            targetName += Utils.ColorString(Utils.GetRoleColor(role), $"({GetString($"{role}")})");
        }
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Turncoat), $"[{targetName}]");
    }
}
