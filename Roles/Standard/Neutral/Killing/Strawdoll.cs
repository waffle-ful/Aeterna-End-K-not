using AmongUs.GameOptions;
using EndKnot.Modules;
using Hazel;
using System.Collections.Generic;
using UnityEngine;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Strawdoll : RoleBase
{
    private const int Id = 704100;
    public static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem ShapeCooldown;
    private static OptionItem WinKilledCountOpt;
    private static OptionItem CanVent;
    private static OptionItem TpToVent;
    private static OptionItem NonSnapTarget;
    private static OptionItem ReprisalDistance;
    private static OptionItem StopTime;

    private byte _strawdollId;
    private byte TargetId;
    private bool IsShapeshifted;
    private int KilledCount;
    private Vector2 ShapePosition;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.NeutralRoles, CustomRoles.Strawdoll);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 20f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Strawdoll])
            .SetValueFormat(OptionFormat.Seconds);

        WinKilledCountOpt = new IntegerOptionItem(Id + 11, "StrawdollWinKilledCount", new(1, 10, 1), 3, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Strawdoll]);

        CanVent = new BooleanOptionItem(Id + 12, "CanVent", false, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Strawdoll]);

        TpToVent = new BooleanOptionItem(Id + 13, "StrawdollTpToVent", false, TabGroup.NeutralRoles)
            .SetParent(CanVent);

        ReprisalDistance = new FloatOptionItem(Id + 14, "StrawdollReprisalDistance", new(0f, 5f, 0.5f), 1f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Strawdoll])
            .SetValueFormat(OptionFormat.Multiplier);

        StopTime = new FloatOptionItem(Id + 15, "StrawdollStopTime", new(0f, 10f, 0.5f), 3f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Strawdoll])
            .SetValueFormat(OptionFormat.Seconds);

        ShapeCooldown = new FloatOptionItem(Id + 16, "StrawdollShapeCooldown", new(0f, 180f, 0.5f), 10f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Strawdoll])
            .SetValueFormat(OptionFormat.Seconds);

        NonSnapTarget = new BooleanOptionItem(Id + 17, "StrawdollNonSnapTarget", false, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Strawdoll]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        _strawdollId = playerId;
        TargetId = byte.MaxValue;
        IsShapeshifted = false;
        KilledCount = 0;
        ShapePosition = Vector2.zero;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        opt.SetFloat(FloatOptionNames.CrewLightMod, Main.DefaultCrewmateVision);
        opt.SetFloat(FloatOptionNames.ImpostorLightMod, Main.DefaultCrewmateVision);
        opt.SetVision(false);

        float cd = ShapeCooldown.GetFloat();

        // イントロ直後の PreventKill 窓では OnVanish が呼ばれず CD だけリセットされる (初回押下が無音で不発)。
        // 窓の長さは固定 10 秒ではなく Options.StartingKillCooldown なので、それに合わせてクランプする。
        if (IntroCutsceneDestroyPatch.PreventKill)
            cd = Mathf.Max(cd, (Options.StartingKillCooldown?.GetFloat() ?? 10f) + 2f);

        AURoleOptions.PhantomCooldown = cd;
        AURoleOptions.PhantomDuration = 0.1f;
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return CanVent.GetBool();
    }

    // OnPet: Phase 1 — set cursed target to nearest alive player
    public override void OnPet(PlayerControl pc)
    {
        if (IsShapeshifted)
        {
            pc.Notify(GetString("StrawdollAlreadyWatching"));
            return;
        }

        if (!FastVector2.TryGetClosestPlayerInRangeTo(pc, pc.GetKillDistance(), out PlayerControl target))
        {
            pc.Notify(GetString("StrawdollNoTarget"));
            return;
        }

        TargetId = target.PlayerId;

        // 呪いの指定が成ったときだけクールダウンを張る (射程に誰も居ない空振りで封じない)。
        // Phantom 基底の役職は汎用のペット CD 経路が Utils.AddAbilityCD の早期 return に落ちるので
        // ここで直接張る。張った時点で HasAbilityCD() が真になり、ペット側の無条件付与も通らない。
        pc.AddAbilityCD((int)KillCooldown.GetFloat());

        SendRPC();
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        pc.Notify(string.Format(GetString("StrawdollTargetSet"), target.GetRealName()));
    }

    // Phantom button: Phase 2 — shapeshift into target (activate watch mode)
    public override bool OnVanish(PlayerControl pc)
    {
        if (TargetId == byte.MaxValue)
        {
            pc.Notify(GetString("StrawdollNoTargetSet"));
            return false;
        }

        if (IsShapeshifted)
        {
            pc.Notify(GetString("StrawdollAlreadyWatching"));
            return false;
        }

        PlayerControl curseTarget = Utils.GetPlayerById(TargetId);
        if (curseTarget == null || !curseTarget.IsAlive())
        {
            TargetId = byte.MaxValue;
            SendRPC();
            pc.Notify(GetString("StrawdollTargetDead"));
            return false;
        }

        IsShapeshifted = true;
        ShapePosition = pc.Pos();
        pc.RpcShapeshift(curseTarget, !DisableAllShapeshiftAnimations.GetBool());

        if (TpToVent.GetBool() && CanVent.GetBool())
            pc.TPToRandomVent();

        SendRPC();
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        return false;
    }

    // When Strawdoll is attacked while shapeshifted: reprisal kill the curse target
    /// <summary>
    ///     呪い返し (キルは通らない)
    /// </summary>
    public override int? GetDefensePower(PlayerControl target, AttackKind kind)
    {
        PlayerControl curseTarget = Utils.GetPlayerById(TargetId);

        return MurderOnly(kind, IsShapeshifted && curseTarget != null && curseTarget.IsAlive() ? (int?)AttackDefense.Powerful : null);
    }

    // 原典のキルボタンは呪い対象を指す照準で、キル自体は起こさない。
    // 呪いの指定は EHR ではペットに載っているので、キルボタンは使わない。
    public override bool CanUseKillButton(PlayerControl pc) => false;

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target, bool check = false)
    {
        if (killer.PlayerId == target.PlayerId) return true;
        if (!IsShapeshifted) return true;

        PlayerControl curseTarget = Utils.GetPlayerById(TargetId);
        if (curseTarget == null || !curseTarget.IsAlive()) return true;

        if (check) return false;

        // 身代わり死体は藁人形が立っていた場所に出す (対象本人の居場所は明かさない)。
        if (!NonSnapTarget.GetBool())
            curseTarget.TP(target.Pos(), log: false);

        LateTask.New(() =>
        {
            PlayerControl ct = Utils.GetPlayerById(TargetId);
            if (ct == null || !ct.IsAlive())
            {
                TargetId = byte.MaxValue;
                IsShapeshifted = false;
                SendRPC();
                Utils.NotifyRoles(SpecifySeer: target, SpecifyTarget: target);
                return;
            }

            ct.Suicide(PlayerState.DeathReason.Spell, killer);

            // 相手の守りで身代わりが成立しなかったときは数えない。Suicide は関所で弾かれると
            // 何もせず return するので、成否は相手の生死で見る。呪い自体は原典と同じく消費する。
            if (ct.IsAlive())
            {
                Logger.Info($"Strawdoll reprisal blocked by {ct.GetNameWithRole()}", "Strawdoll");
            }
            else
            {
                KilledCount++;
                Logger.Info($"Strawdoll reprisal #{KilledCount}: killed {ct.GetNameWithRole()}", "Strawdoll");

                if (KilledCount >= WinKilledCountOpt.GetInt() && GameStates.IsInTask)
                {
                    CustomWinnerHolder.ResetAndSetWinner(CustomWinner.Strawdoll);
                    CustomWinnerHolder.WinnerIds.Add(target.PlayerId);
                    SendRPC();
                    return;
                }
            }

            IsShapeshifted = false;
            TargetId = byte.MaxValue;
            SendRPC();

            float stopDuration = StopTime.GetFloat();
            if (stopDuration > 0f)
            {
                float tmpSpeed = Main.AllPlayerSpeed[target.PlayerId];
                Main.AllPlayerSpeed[target.PlayerId] = Main.MinSpeed;
                target.MarkDirtySettings();
                LateTask.New(() =>
                {
                    Main.AllPlayerSpeed[target.PlayerId] = tmpSpeed;
                    target.MarkDirtySettings();
                }, stopDuration, log: false);
            }

            target.RpcShapeshift(target, !DisableAllShapeshiftAnimations.GetBool());
            target.TP(ShapePosition, log: false);

            Utils.NotifyRoles(SpecifySeer: target, SpecifyTarget: target);
        }, 0.1f, "Strawdoll Reprisal", true);

        return false;
    }

    // Distance check: if Strawdoll gets too close to the cursed target while watching, self-destruct
    public override void OnFixedUpdate(PlayerControl pc)
    {
        float reprisalDist = ReprisalDistance.GetFloat();
        if (reprisalDist <= 0f || !pc.IsAlive() || !IsShapeshifted) return;

        PlayerControl curseTarget = Utils.GetPlayerById(TargetId);
        if (curseTarget == null || !curseTarget.IsAlive()) return;

        if (Vector2.Distance(pc.Pos(), curseTarget.Pos()) < reprisalDist)
        {
            IsShapeshifted = false;
            TargetId = byte.MaxValue;
            SendRPC();
            pc.Suicide(PlayerState.DeathReason.Spell);
        }
    }

    public override void OnReportDeadBody()
    {
        if (IsShapeshifted)
        {
            PlayerControl pc = Utils.GetPlayerById(_strawdollId);
            if (pc != null && pc.IsAlive())
                pc.RpcShapeshift(pc, false);
        }

        IsShapeshifted = false;
        TargetId = byte.MaxValue;
        ShapePosition = Vector2.zero;
        SendRPC();
    }

    private void SendRPC()
    {
        Utils.SendRPC(CustomRPC.SyncRoleData, _strawdollId, KilledCount, IsShapeshifted ? 1 : 0, TargetId);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        KilledCount = reader.ReadPackedInt32();
        IsShapeshifted = reader.ReadPackedInt32() == 1;
        TargetId = reader.ReadByte();
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        // 呪い対象の★は藁人形本人にだけ見える (変身前のみ)
        if (seer.PlayerId == _strawdollId && target.PlayerId != seer.PlayerId)
        {
            if (!IsShapeshifted && TargetId != byte.MaxValue && target.PlayerId == TargetId)
                return "<color=#7b4122>★</color>";
            return string.Empty;
        }

        if (seer.PlayerId != _strawdollId || seer.PlayerId != target.PlayerId) return string.Empty;
        if (!hud && !seer.IsModdedClient()) return string.Empty;

        if (IsShapeshifted)
            return string.Format(GetString("StrawdollWatchingHUD"), KilledCount, WinKilledCountOpt.GetInt());

        if (TargetId != byte.MaxValue)
        {
            PlayerControl t = Utils.GetPlayerById(TargetId);
            return string.Format(GetString("StrawdollTargetHUD"), t != null ? t.GetRealName() : "?");
        }

        return GetString("StrawdollPetToSetTarget");
    }
}
