namespace EndKnot.Roles;

public class Evader : RoleBase
{
    public static bool On;

    // Meeting-end spawn SnapTos (and the Airship spawn picker) overwrite the (50,50) hide, so the
    // per-second resync waits this long after the meeting before hiding the Evader again.
    private const float HideDelayAfterMeeting = 3f;

    private long LastCheck;
    private float HideAllowedAt;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(708700);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        LastCheck = 0;
        HideAllowedAt = 0f;
    }

    public override void AfterMeetingTasks()
    {
        HideAllowedAt = UnityEngine.Time.time + HideDelayAfterMeeting;
    }

    // Safety net for the sabotage start/repair hooks: sabotages that end without UpdateSystem
    // (e.g. the Fungle mushroom mixup timing out), a sabotage that vanilla rejected after our Prefix,
    // and re-hiding after a meeting while the sabotage is still active.
    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !pc.IsAlive() || !GameStates.IsInTask || ExileController.Instance || AntiBlackout.SkipTasks) return;

        long now = Utils.TimeStamp;
        if (now == LastCheck) return;
        LastCheck = now;

        bool active = IsVanillaSabotageActive();
        bool hidden = Main.Invisible.Contains(pc.PlayerId);

        if (active && !hidden)
        {
            if (UnityEngine.Time.time < HideAllowedAt) return;
            if (Main.CurrentMap == MapNames.Airship && !RandomSpawn.CustomNetworkTransformHandleRpcPatch.HasSpawned.Contains(pc.PlayerId)) return;
            pc.RpcMakeInvisible();
        }
        else if (!active && hidden)
            pc.RpcMakeVisible();
    }

    // Invisibility is only granted by vanilla sabotages (custom sabotages bypass CheckSabotage),
    // so the "still active" checks must ignore custom sabotages or the Evader would stay invisible
    // with nothing left to repair.
    public static bool IsVanillaSabotageActive()
    {
        SystemTypes[] all = ShipStatusSystem.AllSabotage;

        for (var i = 0; i < all.Length; i++)
            if (Utils.IsActive(all[i]))
                return true;

        return false;
    }
}
