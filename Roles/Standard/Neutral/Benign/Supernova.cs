using System;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

internal class Supernova : RoleBase
{
    public static bool On;

    private static OptionItem StopTimeLimit;
    private static OptionItem GraceTime;
    private static OptionItem ExplosionRadius;

    private float StopTimer;
    private float GraceTimer;
    private Vector2 LastPosition;
    private bool IsMoving;
    private bool MoveStarted;
    private bool Exploded;
    private float RearmTimer;

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
        Exploded = false;
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

        if (Exploded)
        {
            // 自爆の LateTask (0.2s) 待ちの間の再発火防止。蘇生などで生き残った場合は一定時間後に再武装する
            RearmTimer -= Time.fixedDeltaTime;
            if (RearmTimer > 0f) return;

            Exploded = false;
            StopTimer = 0f;
            GraceTimer = GraceTime.GetFloat();
            IsMoving = true;
            MoveStarted = false;
            LastPosition = pc.Pos();
            return;
        }

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
            {
                Exploded = true;
                RearmTimer = 1f;
                Explode(pc);
                return;
            }
        }

        LastPosition = currentPosition;
    }

    private static void Explode(PlayerControl pc)
    {
        float radius = ExplosionRadius.GetFloat();

        foreach (PlayerControl tg in Main.EnumeratePlayerControls())
        {
            try
            {
                if (tg.PlayerId == pc.PlayerId || !tg.IsAliveWithConditions() || Medic.ProtectList.Contains(tg.PlayerId) || tg.inVent || tg.Is(CustomRoles.Pestilence)) continue;
                if (!FastVector2.DistanceWithinRange(pc.Pos(), tg.Pos(), radius)) continue;

                if (!tg.IsModdedClient()) tg.KillFlash();
                tg.Suicide(PlayerState.DeathReason.Bombed, pc);
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        LateTask.New(() =>
        {
            if (!GameStates.IsEnded && pc.IsAlive())
                pc.Suicide(PlayerState.DeathReason.Stopped);
        }, 0.2f, "Supernova Suicide");
    }

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(705200, TabGroup.NeutralRoles, CustomRoles.Supernova);

        StopTimeLimit = new FloatOptionItem(705202, "SupernovaStopTimeLimit", new(0f, 180f, 1f), 3f, TabGroup.NeutralRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Supernova])
            .SetValueFormat(OptionFormat.Seconds);

        GraceTime = new FloatOptionItem(705203, "SupernovaGraceTime", new(0f, 60f, 1f), 5f, TabGroup.NeutralRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Supernova])
            .SetValueFormat(OptionFormat.Seconds);

        ExplosionRadius = new FloatOptionItem(705204, "SupernovaExplosionRadius", new(0.5f, 15f, 0.5f), 4f, TabGroup.NeutralRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Supernova])
            .SetValueFormat(OptionFormat.Multiplier);

        Options.OverrideTasksData.Create(705210, TabGroup.NeutralRoles, CustomRoles.Supernova);
    }
}
