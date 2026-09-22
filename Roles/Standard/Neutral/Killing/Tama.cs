using System.Collections.Generic;
using AmongUs.GameOptions;
using UnityEngine;
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

    // 装填中の追従 TP 間引き用。毎フレ TP は公式鯖の per-round SnapTo 上限
    // (Utils.NumSnapToCallsThisRound, 80でNone降格/100で停止) を枯渇させ desync する。
    // ガチョウ/ペンギンと同様、一定間隔 + オーナーが動いた時だけ snap する。
    private float LastFollowSnapTime;
    private Vector2 LastFollowSnapPos = new(-9999f, -9999f);
    private const float FollowSnapInterval = 0.2f; // ~5 snaps/s

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

        // 装填できない設定のときだけ基底が Engineer になり、この 2 つが効く (それ以外は Impostor 基底で無効)。
        // 弾は会議後の変換でしか生まれないので、基底は RpcChangeRoleBasis の経路だけを通る。
        AURoleOptions.EngineerCooldown = JackalHadouHo.TamaVentCooldown.GetFloat();
        AURoleOptions.EngineerInVentMaxTime = JackalHadouHo.TamaMaxInVentTime.GetFloat();
    }

    // 原典の弾は常にベントを使える。装填できない設定にしたとき、これが無いと
    // キルボタンもベントも無い完全な無力役職になる。
    public override bool CanUseImpostorVentButton(PlayerControl pc) => true;

    // 原典は弾とジャッカル陣営が互いの役職を見える。弾の唯一の能力は「主に装填する」ことなので、
    // 誰が主か分からないと総当たりするしかない (装填の打診は主以外へは無言で false を返す)。
    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if (base.KnowRole(seer, target)) return true;
        if (OwnerId == byte.MaxValue) return false;
        return (seer.PlayerId == TamaId && target.PlayerId == OwnerId) || (seer.PlayerId == OwnerId && target.PlayerId == TamaId);
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
            Vector2 pos = owner.GetTruePosition();
            if (Time.time - LastFollowSnapTime >= FollowSnapInterval && Vector2.Distance(pos, LastFollowSnapPos) > 0.3f)
            {
                LastFollowSnapTime = Time.time;
                LastFollowSnapPos = pos;
                tamaPlayer.TP(pos, log: false);
            }
        }
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting || seer.PlayerId != TamaId || seer.PlayerId != target.PlayerId || !seer.IsAlive()) return string.Empty;

        if (!JackalHadouHo.TamaCanLoad.GetBool()) return $"<color=#5e5e5e>{GetString("Tama.HudLoadDisabled")}</color>";
        if (HasLoaded) return $"<color=#00b4eb>{GetString("Tama.HudLoaded")}</color>";
        if (!IsOwnerAlive()) return $"<color=#5e5e5e>{GetString("Tama.HudOwnerDead")}</color>";
        return $"<color=#00b4eb>{GetString("Tama.HudReady")}</color>";
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
