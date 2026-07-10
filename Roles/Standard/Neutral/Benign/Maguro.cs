using AmongUs.GameOptions;
using UnityEngine;

namespace EndKnot.Roles;

internal class Maguro : RoleBase
{
    public static bool On;

    private static OptionItem StopTimeLimit;
    private static OptionItem GraceTime;

    private float StopTimer;
    private float GraceTimer;
    private Vector2 LastPosition;
    private bool IsMoving;
    private bool MoveStarted;

    public override bool IsEnable => On;

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        StopTimer = 0f;
        GraceTimer = GraceTime.GetFloat();
        IsMoving = true;
        MoveStarted = false;
    }

    public override void AfterMeetingTasks()
    {
        StopTimer = 0f;
        GraceTimer = GraceTime.GetFloat();
        IsMoving = true;
        MoveStarted = false;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!Main.IntroDestroyed || !GameStates.InGame || GameStates.IsMeeting || ExileController.Instance || AntiBlackout.SkipTasks || !pc.IsAlive()) return;

        Vector2 currentPosition = pc.Pos();

        if (GraceTimer > 0f)
        {
            GraceTimer -= Time.fixedDeltaTime;
            LastPosition = currentPosition;
            return;
        }

        bool isCurrentlyMoving = Vector2.Distance(LastPosition, currentPosition) > 0.0001f;

        if (!MoveStarted && isCurrentlyMoving) MoveStarted = true;

        if (!MoveStarted)
        {
            LastPosition = currentPosition;
            return;
        }

        if (isCurrentlyMoving != IsMoving)
        {
            if (isCurrentlyMoving) StopTimer = 0f;
            IsMoving = isCurrentlyMoving;
        }

        if (!IsMoving)
        {
            StopTimer += Time.fixedDeltaTime;

            if (StopTimer >= StopTimeLimit.GetFloat())
                pc.Suicide(PlayerState.DeathReason.Stopped);
        }

        LastPosition = currentPosition;
    }

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(705100, TabGroup.NeutralRoles, CustomRoles.Maguro);

        StopTimeLimit = new FloatOptionItem(705102, "MaguroStopTimeLimit", new(0f, 180f, 1f), 3f, TabGroup.NeutralRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Maguro])
            .SetValueFormat(OptionFormat.Seconds);

        GraceTime = new FloatOptionItem(705103, "MaguroGraceTime", new(0f, 60f, 1f), 5f, TabGroup.NeutralRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Maguro])
            .SetValueFormat(OptionFormat.Seconds);

        Options.OverrideTasksData.Create(705110, TabGroup.NeutralRoles, CustomRoles.Maguro);
    }
}
