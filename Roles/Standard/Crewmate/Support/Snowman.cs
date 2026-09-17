using System.Collections.Generic;
using AmongUs.GameOptions;
using UnityEngine;

namespace EndKnot.Roles;

public class Snowman : RoleBase
{
    private const int Id = 701900;
    private static List<byte> PlayerIdList = [];

    private static OptionItem OptionFirstVision;
    private static OptionItem OptionMinVision;
    private static OptionItem OptionMeltedSteps;
    private static OptionItem OptionElectricalIgnoreMelt;

    private byte SnowmanId;
    private float FirstVision;
    private float MinVision;
    private float MeltedSteps;
    private bool ElectricalIgnoreMelt;

    private float NowVision;
    private float NowWalkCount;
    private float OldProportion;
    private Vector2 OldPosition;
    private float stoptimer;
    private bool WasLightsOut;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Snowman);

        OptionFirstVision = new FloatOptionItem(Id + 10, "SnowmanFirstVision", new(0.05f, 5f, 0.05f), 1.25f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Snowman])
            .SetValueFormat(OptionFormat.Multiplier);

        OptionMinVision = new FloatOptionItem(Id + 11, "SnowmanMinVision", new(0f, 5f, 0.05f), 0.15f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Snowman])
            .SetValueFormat(OptionFormat.Multiplier);

        OptionMeltedSteps = new FloatOptionItem(Id + 12, "SnowmanMeltedSteps", new(100f, 3000f, 100f), 1800f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Snowman]);

        OptionElectricalIgnoreMelt = new BooleanOptionItem(Id + 13, "SnowmanElectricalIgnoreMelt", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Snowman]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        SnowmanId = playerId;
        FirstVision = OptionFirstVision.GetFloat();
        MinVision = OptionMinVision.GetFloat();
        MeltedSteps = OptionMeltedSteps.GetFloat();
        ElectricalIgnoreMelt = OptionElectricalIgnoreMelt.GetBool();

        NowVision = FirstVision;
        OldProportion = 0f;
        NowWalkCount = 0f;
        OldPosition = new Vector2(50f, 50f);
        stoptimer = 0f;
        WasLightsOut = false;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetFloat(FloatOptionNames.CrewLightMod, NowVision);

        // マッドメイトの雪だるまは、停電中だけ溶ける前の視界で見渡せる。
        if (Utils.IsActive(SystemTypes.Electrical) && playerId.GetPlayer() is { } pc && pc.Is(CustomRoles.Madmate))
            opt.SetFloat(FloatOptionNames.CrewLightMod, FirstVision * 5);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !pc.IsAlive()) return;
        if (GameStates.IsLobby || GameStates.IsMeeting) return; // 会議中は Pos() が凍結し静止判定が誤発火 → ビジョン融解が進むため除外

        bool lightsOut = Utils.IsActive(SystemTypes.Electrical);

        // 停電の開始/復旧で全員へ設定を送り直す経路はこの役職を含まないので、マッドメイト時の視界切り替えは自分で送る。
        if (lightsOut != WasLightsOut)
        {
            WasLightsOut = lightsOut;
            if (pc.Is(CustomRoles.Madmate)) pc.MarkDirtySettings();
        }

        if (pc.GetTaskState().IsTaskFinished) return;
        if (lightsOut && ElectricalIgnoreMelt) return;

        Vector2 currentPos = pc.Pos();

        if (currentPos == OldPosition)
        {
            stoptimer += Time.fixedDeltaTime;
            if (stoptimer > 1f)
            {
                NowWalkCount += 5f;
                stoptimer = 0f;
                CheckVision(pc);
            }
            return;
        }

        if (OldPosition == new Vector2(50f, 50f)
            || pc.inVent || pc.MyPhysics.Animations.IsPlayingEnterVentAnimation()
            || pc.MyPhysics.Animations.IsPlayingAnyLadderAnimation() || pc.inMovingPlat
            || !pc.CanMove)
        {
            OldPosition = currentPos;
            return;
        }

        float distance = Vector2.Distance(OldPosition, currentPos);
        OldPosition = currentPos;
        if (distance > 5f) return;

        NowWalkCount += distance;
        stoptimer = 0f;
        CheckVision(pc);
    }

    private void CheckVision(PlayerControl pc)
    {
        float proportion = NowWalkCount / MeltedSteps;
        if (proportion > 1f) proportion = 1f;
        float meltRange = FirstVision - MinVision;
        float vision = meltRange - meltRange * proportion + MinVision;

        if (proportion - OldProportion > 0.05f)
        {
            OldProportion = proportion;
            NowVision = vision;
            pc.MarkDirtySettings();
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != SnowmanId) return string.Empty;
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Snowman), $"({NowVision:F2}x)");
    }
}
