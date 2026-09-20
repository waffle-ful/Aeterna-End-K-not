using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

public class VentOpener : RoleBase
{
    private const int Id = 701400;
    private static List<byte> PlayerIdList = [];

    private static OptionItem OptionCount;
    private static OptionItem OptionCooldown;
    private static OptionItem OptionFuhatu;
    private static OptionItem OptionImp;
    private static OptionItem OptionMad;
    private static OptionItem OptionCrew;
    private static OptionItem OptionNeutral;
    private static OptionItem OptionCanTaskcount;
    public static OptionItem OptionBlockKill;

    public static Dictionary<byte, int> CurrentVent = [];

    private byte VentOpenerId;
    private bool Defo;
    private int count;
    private List<byte> expelledPlayers = [];
    private bool NotifiedUnlock;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.VentOpener);

        OptionCount = new IntegerOptionItem(Id + 10, "VentOpenerCount", new(0, 30, 1), 3, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.VentOpener])
            .SetValueFormat(OptionFormat.Times);

        OptionCooldown = new IntegerOptionItem(Id + 11, "Cooldown", new(0, 999, 1), 30, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.VentOpener])
            .SetValueFormat(OptionFormat.Seconds);

        OptionFuhatu = new BooleanOptionItem(Id + 12, "VentOpenerFuhatu", false, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.VentOpener]);

        OptionImp = new BooleanOptionItem(Id + 13, "VentOpenerImp", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.VentOpener]);

        OptionMad = new BooleanOptionItem(Id + 14, "VentOpenerMad", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.VentOpener]);

        OptionCrew = new BooleanOptionItem(Id + 15, "VentOpenerCrew", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.VentOpener]);

        OptionNeutral = new BooleanOptionItem(Id + 16, "VentOpenerNeutral", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.VentOpener]);

        OptionCanTaskcount = new IntegerOptionItem(Id + 17, "VentOpenerCanTaskcount", new(0, 99, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.VentOpener])
            .SetValueFormat(OptionFormat.Times);

        OptionBlockKill = new BooleanOptionItem(Id + 18, "VentOpenerBlockKill", false, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.VentOpener]);
    }

    public override void Init()
    {
        PlayerIdList = [];
        CurrentVent = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        VentOpenerId = playerId;
        count = OptionCount.GetInt();
        Defo = count == 0;
        expelledPlayers = [];
        // 必要タスク数0=最初から解放済みなので「解放された」通知の対象外。
        NotifiedUnlock = OptionCanTaskcount.GetInt() <= 0;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        AURoleOptions.EngineerCooldown = OptionCooldown.GetInt();
        AURoleOptions.EngineerInVentMaxTime = 1f;
    }

    public override bool CanUseVent(PlayerControl pc, int ventId)
    {
        if (pc == null) return true;
        if (pc.PlayerId != VentOpenerId) return true;
        if (!IsThisRole(pc)) return true;
        if (pc.Is(CustomRoles.Nimble)) return true;

        bool taskDone = pc.GetTaskState().CompletedTasksCount >= OptionCanTaskcount.GetInt();
        return (Defo || count > 0) && taskDone;
    }

    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        bool fuhatu = OptionFuhatu.GetBool();
        bool imp = OptionImp.GetBool();
        bool mad = OptionMad.GetBool();
        bool crew = OptionCrew.GetBool();
        bool neutral = OptionNeutral.GetBool();

        bool booted = false;
        bool madOpener = pc.Is(CustomRoles.Madmate);
        List<byte> madLeaked = [];

        foreach ((byte playerId, int ventId) in CurrentVent.ToList())
        {
            if (playerId == VentOpenerId) continue;

            PlayerControl target = Utils.GetPlayerById(playerId);
            if (target == null || !target.IsAlive()) continue;
            if (!target.inVent) continue;
            if (target.MyPhysics == null) continue;
            if (target.Is(CustomRoles.Nimble)) continue;

            CustomRoles role = target.GetCustomRole();
            bool match = (role.IsImpostor() && imp)
                || (role.IsMadmate() && mad)
                || (role.IsCrewmate() && crew)
                || (role.IsNeutral() && neutral);

            if (!match) continue;
            // マッドメイトのベント開放者はインポスター陣営を追い出さない。
            if (madOpener && target.Is(Team.Impostor)) continue;

            target.MyPhysics.RpcBootFromVent(ventId);
            expelledPlayers.Add(target.PlayerId);
            booted = true;
            if (madOpener) madLeaked.Add(target.PlayerId);
        }

        if (booted)
            pc.KillFlash();

        // 追い出したクルーの名前は、生存インポスター全員にも伝わる。
        if (madLeaked.Count > 0)
        {
            string msg = string.Format(Translator.GetString("VentOpenerMadLeak"), string.Join(", ", madLeaked.Select(id => id.ColoredPlayerName())));
            foreach (PlayerControl impostor in Main.EnumerateAlivePlayerControls())
            {
                if (impostor.PlayerId == pc.PlayerId || !impostor.Is(CustomRoleTypes.Impostor)) continue;
                impostor.KillFlash();
                impostor.Notify(msg, 5f);
            }
        }

        if ((booted || fuhatu) && count > 0)
        {
            count--;
            if (!CanUseAbility(pc))
                pc.SyncSettings();
        }

        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    private bool CanUseAbility(PlayerControl pc)
    {
        bool taskDone = pc.GetTaskState().CompletedTasksCount >= OptionCanTaskcount.GetInt();
        return (Defo || count > 0) && taskDone;
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (NotifiedUnlock) return;
        if (completedTaskCount + 1 >= OptionCanTaskcount.GetInt())
        {
            NotifiedUnlock = true;

            // TaskState.Update はこのフックを CompletedTasksCount++ より前に呼ぶ。ここで同期に送ると
            // まだ未解放の値が飛んでしまうので、加算が済んだ次の tick へずらす。
            LateTask.New(() => Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc), 0.1f, "VentOpener.UnlockNotify");
        }
    }

    public static void OnAnyoneEnterVent(PlayerControl pc, Vent vent)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        CurrentVent[pc.PlayerId] = vent.Id;
    }

    public static void OnAnyoneExitVent(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || pc == null) return;
        CurrentVent.Remove(pc.PlayerId);
    }

    /// <summary>
    ///     締め出した相手からのキルだけ弾く (相手依存の絞り込みは OnCheckMurderAsTarget 側)
    /// </summary>
    public override int? GetDefensePower(PlayerControl target, AttackKind kind)
    {
        return MurderOnly(kind, OptionBlockKill.GetBool() && expelledPlayers.Count > 0 ? (int?)AttackDefense.Powerful : null);
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target, bool check = false)
    {
        if (OptionBlockKill.GetBool() && expelledPlayers.Contains(killer.PlayerId))
        {
            if (check) return false;

            killer.SetKillCooldown();
            return false;
        }
        return true;
    }

    public override void OnReportDeadBody()
    {
        expelledPlayers.Clear();
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.AbilityButton?.OverrideText(Translator.GetString("VentOpenerAbility"));
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != VentOpenerId || Defo) return string.Empty;

        PlayerControl pc = playerId.GetPlayer();
        if (pc == null) return string.Empty;

        bool taskDone = pc.GetTaskState().CompletedTasksCount >= OptionCanTaskcount.GetInt();
        Color color;
        if (count > 0 && taskDone) color = Utils.GetRoleColor(CustomRoles.VentOpener);
        else if (taskDone) color = Color.red;
        else color = Color.gray;

        return Utils.ColorString(color, $"({count})");
    }
}
