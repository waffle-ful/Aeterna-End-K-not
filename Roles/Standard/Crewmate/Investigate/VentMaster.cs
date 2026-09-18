using System.Collections.Generic;
using AmongUs.GameOptions;

namespace EndKnot.Roles;

public class VentMaster : RoleBase
{
    private const int Id = 701300;
    private static List<byte> PlayerIdList = [];
    private static Dictionary<byte, long> LastMadLeak = [];
    private static long LastMadLeakAny;

    public static OptionItem CanUseVentOption;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.VentMaster);

        CanUseVentOption = new BooleanOptionItem(Id + 10, "CanVent", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.VentMaster]);
    }

    public override void Init()
    {
        PlayerIdList = [];
        LastMadLeak = [];
        LastMadLeakAny = 0;
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        if (!CanUseVentOption.GetBool()) return;
        AURoleOptions.EngineerCooldown = 0f;
        AURoleOptions.EngineerInVentMaxTime = 0f;
    }

    public static void OnAnyoneEnterVent(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsInTask) return;
        // 入った本人が別のベントマスターなら、他のベントマスターへも通知しない。
        if (pc.Is(CustomRoles.VentMaster)) return;

        bool madWatching = false;
        foreach (PlayerControl vm in Main.AllAlivePlayerControlsToList)
        {
            if (vm.PlayerId == pc.PlayerId) continue;
            if (!vm.Is(CustomRoles.VentMaster)) continue;
            vm.KillFlash();
            if (vm.Is(CustomRoles.Madmate)) madWatching = true;
        }

        // マッドメイトのベントマスターがいると、インポスター以外のベント使用がインポスター全員にも伝わる (名前付き)。
        // 出入りの連打で通知が溢れないよう、同じ人の通知は数秒に1回、全体でも1秒に1回に絞る。
        if (!madWatching || pc.Is(CustomRoleTypes.Impostor)) return;

        long now = Utils.TimeStamp;
        if (LastMadLeak.TryGetValue(pc.PlayerId, out long last) && now - last < 5) return;
        if (now - LastMadLeakAny < 1) return;
        LastMadLeak[pc.PlayerId] = now;
        LastMadLeakAny = now;

        string msg = string.Format(Translator.GetString("VentMasterMadLeak"), pc.PlayerId.ColoredPlayerName());
        foreach (PlayerControl imp in Main.EnumerateAlivePlayerControls())
        {
            if (imp.PlayerId == pc.PlayerId || !imp.Is(CustomRoleTypes.Impostor)) continue;
            imp.KillFlash();
            imp.Notify(msg, 4f);
        }
    }
}
