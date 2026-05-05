using System.Collections.Generic;
using AmongUs.GameOptions;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Tama : RoleBase
{
    public static bool On;
    public static List<Tama> Instances = [];

    private byte TamaId;
    public byte OwnerId;
    public bool HasLoaded;
    private bool IsLoading;

    public override bool IsEnable => Instances.Count > 0;

    public override void SetupCustomOption() { }

    public override void Init()
    {
        On = false;
        Instances = [];
    }

    public override void Add(byte playerId)
    {
        On = true;
        Instances.Add(this);
        TamaId = playerId;
        OwnerId = byte.MaxValue;
        HasLoaded = false;
        IsLoading = false;
    }

    public override void Remove(byte playerId)
    {
        Instances.RemoveAll(x => x.TamaId == playerId);
        if (Instances.Count == 0) On = false;
    }

    public void SetOwner(byte ownerId)
    {
        OwnerId = ownerId;
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = JackalHadouHo.TamaLoadCooldown.GetFloat();
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        if (!JackalHadouHo.TamaCanLoad.GetBool()) return false;
        if (HasLoaded || IsLoading) return false;
        return pc.IsAlive() && IsOwnerAlive();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        opt.SetVision(true);
    }

    private bool IsOwnerAlive()
    {
        if (OwnerId == byte.MaxValue) return false;
        PlayerControl owner = OwnerId.GetPlayer();
        return owner != null && owner.IsAlive();
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        // Tama のキルボタン → owner への装填専用
        if (!JackalHadouHo.TamaCanLoad.GetBool()) return false;
        if (HasLoaded || IsLoading) return false;
        if (target.PlayerId != OwnerId) return false;

        IsLoading = true;
        HasLoaded = true;

        if (Main.PlayerStates[target.PlayerId].Role is JackalHadouHo jhh)
            jhh.SetLoaded(true);

        killer.Notify(GetString("TamaLoaded"));
        return false;
    }

    public override void OnFixedUpdate(PlayerControl tamaPlayer)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsInTask) return;
        if (OwnerId == byte.MaxValue) return;

        PlayerControl owner = OwnerId.GetPlayer();

        // 自分が死亡し、装填中だった場合の解除
        if (!tamaPlayer.IsAlive() && HasLoaded)
        {
            HasLoaded = false;
            IsLoading = false;
            if (owner != null && Main.PlayerStates[owner.PlayerId].Role is JackalHadouHo jhh1)
                jhh1.SetLoaded(false);
            return;
        }

        // オーナー死亡 or 転職 → JackalHadouHo へ昇格
        if (tamaPlayer.IsAlive() && (owner == null || !owner.IsAlive() || owner.GetCustomRole() != CustomRoles.JackalHadouHo))
        {
            OwnerId = byte.MaxValue;
            JackalHadouHo.NextNoSideKick = true;
            tamaPlayer.RpcSetCustomRole(CustomRoles.JackalHadouHo);
            tamaPlayer.RpcChangeRoleBasis(CustomRoles.JackalHadouHo);
            return;
        }

        // 装填済みの間、オーナーに追従
        if (HasLoaded && owner != null && owner.IsAlive())
        {
            tamaPlayer.TP(owner.GetTruePosition(), log: false);
        }
    }

    public override void OnReportDeadBody()
    {
        if (HasLoaded || IsLoading)
        {
            HasLoaded = false;
            IsLoading = false;
            PlayerControl owner = OwnerId.GetPlayer();
            if (owner != null && Main.PlayerStates[owner.PlayerId].Role is JackalHadouHo jhh)
                jhh.SetLoaded(false);
        }
    }
}
