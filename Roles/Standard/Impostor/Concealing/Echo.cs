using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;

namespace EndKnot.Roles;

public class Echo : RoleBase
{
    public static bool On;
    public static List<Echo> Instances = [];

    private static OptionItem ShapeshiftCooldown;
    private static OptionItem ShapeshiftDuration;
    private static OptionItem DisableVentingWhileControlling;

    public PlayerControl EchoPC;
    private bool SkipCheck;
    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        var id = 647600;
        const TabGroup tab = TabGroup.ImpostorRoles;
        const CustomRoles role = CustomRoles.Echo;
        Options.SetupRoleOptions(id++, tab, role);
        StringOptionItem parent = Options.CustomRoleSpawnChances[role];

        ShapeshiftCooldown = new FloatOptionItem(++id, "ShapeshiftCooldown", new(0f, 180f, 0.5f), 30f, tab)
            .SetParent(parent)
            .SetValueFormat(OptionFormat.Seconds);

        ShapeshiftDuration = new FloatOptionItem(++id, "ShapeshiftDuration", new(0f, 180f, 0.5f), 10f, tab)
            .SetParent(parent)
            .SetValueFormat(OptionFormat.Seconds);

        DisableVentingWhileControlling = new BooleanOptionItem(++id, "Echo.DisableVentingWhileControlling", true, tab)
            .SetParent(parent);
    }

    public override void Init()
    {
        Instances = [];
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        EchoPC = Utils.GetPlayerById(playerId);
        SkipCheck = false;
        Instances.Add(this);
    }

    public override void Remove(byte playerId)
    {
        Instances.Remove(this);
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        bool shifted = id.IsPlayerShifted();
        string text = !shifted ? "EchoShapeshiftButtonText" : "KillButtonText";
        hud.AbilityButton.OverrideText(Translator.GetString(text));
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        return false;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        AURoleOptions.ShapeshifterCooldown = ShapeshiftCooldown.GetFloat();
        AURoleOptions.ShapeshifterDuration = ShapeshiftDuration.GetFloat();
        AURoleOptions.ShapeshifterLeaveSkin = false;
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return !DisableVentingWhileControlling.GetBool() || !pc.IsShifted();
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (Thanos.IsImmune(target)) return false;
        
        if (shapeshifting)
        {
            Vector2 pos = target.Pos();
            if (!CheckMurderPatch.PassesGate(shapeshifter, target) || !target.TP(shapeshifter)) return false;

            target.RpcShapeshift(shapeshifter, false);
            Main.AllPlayerSpeed[target.PlayerId] = Main.MinSpeed;
            target.MarkDirtySettings();
            shapeshifter.TP(pos);

            if (target.AmOwner)
                Achievements.Type.TooCold.CompleteAfterGameEnd();
        }
        else
        {
            target = Utils.GetPlayerById(shapeshifter.shapeshiftTargetPlayerId);
            if (target == null) return true;

            RevertSwap(shapeshifter, target);
            LateTask.New(() => target.Suicide(PlayerState.DeathReason.Echoed, shapeshifter), 0.2f, "Echo Unshift Kill");
        }

        return true;
    }

    private static void RevertSwap(PlayerControl echo, PlayerControl target)
    {
        target.RpcShapeshift(target, false);
        Vector2 pos = echo.Pos();
        echo.TP(target);
        target.TP(pos);
    }

    /// <summary>
    ///     入れ替わり相手への身代わり
    /// </summary>
    public override int? GetDefensePower(PlayerControl target, AttackKind kind)
    {
        return MurderOnly(kind, !SkipCheck && target.IsShifted() ? (int?)AttackDefense.Powerful : null);
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target, bool check = false)
    {
        if (SkipCheck || !target.IsShifted()) return true;

        // 入れ替わった相手への問い合わせがここへ戻ってくるのを防ぐ。
        // 打診なら、答えを出した時点で元に戻して何も残さない。
        SkipCheck = true;
        if (!check) LateTask.New(() => SkipCheck = false, 3f, log: false);

        PlayerControl ssTarget = Utils.GetPlayerById(target.shapeshiftTargetPlayerId);
        if (ssTarget == null || !CheckMurderPatch.PassesGate(killer, ssTarget, check))
        {
            if (check) SkipCheck = false;
            return true;
        }

        if (check)
        {
            SkipCheck = false;
            return false;
        }

        RevertSwap(target, ssTarget);
        LateTask.New(() => killer.Kill(ssTarget), 0.2f, log: false);
        return false;
    }

    public void OnTargetCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (SkipCheck) return;
        SkipCheck = true;
        LateTask.New(() => SkipCheck = false, 3f, log: false);
        
        RevertSwap(EchoPC, target);

        LateTask.New(() =>
        {
            killer.RpcCheckAndMurder(EchoPC);
            target.Suicide(PlayerState.DeathReason.Kill, EchoPC);
        }, 0.2f, log: false);
    }

    public override void OnReportDeadBody()
    {
        if (!EchoPC.IsShifted()) return;

        PlayerControl ssTarget = ((byte)EchoPC.shapeshiftTargetPlayerId).GetPlayer();
        if (ssTarget == null || !ssTarget.IsAlive()) return;

        ssTarget.Suicide(PlayerState.DeathReason.Kill, EchoPC);
    }
}