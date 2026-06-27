using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;
using static EndKnot.Options;

namespace EndKnot.Roles;

public class UltraStar : RoleBase
{
    private const int Id = 702500;

    public static bool On;
    public override bool IsEnable => On;

    private static OptionItem SpeedBonus;
    private static OptionItem CanseeAllplayer;
    public static OptionItem CanKillOpt;
    private static OptionItem KillCooldownOpt;
    public static OptionItem PetCooldownOpt;

    private byte UltraStarId;
    private int OriginalColorId;
    private float ColorTimer;
    private float KillCoolRemaining;
    private int LastColorId;
    internal bool StarActive;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.UltraStar);

        SpeedBonus = new FloatOptionItem(Id + 9, "UltraStarAddSpeed", new(0f, 5f, 0.25f), 2.0f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.UltraStar])
            .SetValueFormat(OptionFormat.Multiplier);

        CanseeAllplayer = new BooleanOptionItem(Id + 14, "UltraStarCanseeallplayer", false, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.UltraStar]);

        CanKillOpt = new BooleanOptionItem(Id + 10, "UltraStarCankill", false, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.UltraStar]);

        KillCooldownOpt = new FloatOptionItem(Id + 13, "KillCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.CrewmateRoles)
            .SetParent(CanKillOpt)
            .SetValueFormat(OptionFormat.Seconds);

        PetCooldownOpt = new FloatOptionItem(Id + 15, "Cooldown", new(1f, 60f, 0.5f), 15f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.UltraStar])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        UltraStarId = playerId;
        ColorTimer = 0f;
        LastColorId = -1;
        KillCoolRemaining = 0f;
        StarActive = false;
        OriginalColorId = Camouflage.PlayerSkins.TryGetValue(playerId, out var skin) ? skin.ColorId : 0;
    }

    public override void Remove(byte playerId)
    {
        if (playerId.GetPlayer() is { } pc) pc.RpcChangeColor((byte)OriginalColorId); // 公式鯖では spoof RPC ではなく正規 serialize で色を同期 (anti-cheat 修正後)
    }

    public override void OnPet(PlayerControl pc)
    {
        StarActive = !StarActive;

        if (StarActive)
        {
            KillCoolRemaining = KillCooldownOpt.GetFloat();
        }
        else
        {
            pc.RpcChangeColor((byte)OriginalColorId); // 公式鯖では spoof RPC ではなく正規 serialize で色を同期 (anti-cheat 修正後)
            LastColorId = OriginalColorId;
        }

        pc.MarkDirtySettings();
        pc.AddAbilityCD();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        Main.AllPlayerSpeed[playerId] = Main.RealOptionsData.GetFloat(FloatOptionNames.PlayerSpeedMod)
            + (StarActive ? SpeedBonus.GetFloat() : 0f);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !GameStates.InGame || !pc.IsAlive() || GameStates.IsMeeting || !StarActive) return;

        // Color cycling
        ColorTimer %= 18f;
        int colorId = ColorTimer switch
        {
            >= 0 and < 1 => 8,
            >= 1 and < 2 => 1,
            >= 2 and < 3 => 10,
            >= 3 and < 4 => 2,
            >= 4 and < 5 => 11,
            >= 5 and < 6 => 14,
            >= 6 and < 7 => 5,
            >= 7 and < 8 => 4,
            >= 8 and < 9 => 17,
            >= 9 and < 10 => 0,
            >= 10 and < 11 => 3,
            >= 11 and < 12 => 13,
            >= 12 and < 13 => 7,
            >= 13 and < 14 => 15,
            >= 14 and < 15 => 6,
            >= 15 and < 16 => 12,
            >= 16 and < 17 => 9,
            _ => 16
        };

        if (colorId != LastColorId)
        {
            pc.RpcChangeColor((byte)colorId); // 公式鯖では spoof RPC ではなく正規 serialize で色を同期 (anti-cheat 修正後)
            LastColorId = colorId;
        }

        ColorTimer += Time.fixedDeltaTime * 1.5f;

        if (!CanKillOpt.GetBool()) return;

        KillCoolRemaining = Mathf.Max(KillCoolRemaining - Time.fixedDeltaTime, 0f);
        if (KillCoolRemaining > 0) return;

        Vector2 pos = pc.Pos();
        PlayerControl target = null;
        foreach (PlayerControl other in Main.AllAlivePlayerControlsToList)
        {
            if (other.PlayerId == pc.PlayerId) continue;
            if (!pc.CanMove || !other.CanMove) continue;
            if (FastVector2.DistanceWithinRange(pos, other.Pos(), 0.4f))
            {
                target = other;
                break;
            }
        }

        if (target == null) return;
        KillCoolRemaining = KillCooldownOpt.GetFloat();
        pc.Kill(target);
        pc.MarkDirtySettings();
    }

    public override void OnReportDeadBody()
    {
        StarActive = false;
        if (UltraStarId.GetPlayer() is { } pc) pc.RpcChangeColor((byte)OriginalColorId); // 公式鯖では spoof RPC ではなく正規 serialize で色を同期 (anti-cheat 修正後)
        LastColorId = OriginalColorId;
    }

    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        return base.KnowRole(seer, target) || (target.Is(CustomRoles.UltraStar) && CanseeAllplayer.GetBool());
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != UltraStarId) return string.Empty;
        var color = StarActive ? Utils.GetRoleColor(CustomRoles.UltraStar) : Color.gray;
        if (!StarActive) return Utils.ColorString(color, "☆");
        return CanKillOpt.GetBool()
            ? Utils.ColorString(color, $"★({KillCoolRemaining:F1}s)")
            : Utils.ColorString(color, "★");
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.PetButton?.OverrideText(Translator.GetString(StarActive ? "UltraStarDeactivate" : "UltraStarActivate"));
    }
}
