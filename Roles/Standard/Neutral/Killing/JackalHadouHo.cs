using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class JackalHadouHo : RoleBase
{
    private const int Id = 704400;
    public static bool On;
    public static List<JackalHadouHo> Instances = [];

    // Tama 昇格時に新しい JHH の SK 機能を無効化するためのフラグ
    public static bool NextNoSideKick;

    private static OptionItem KillCooldown;
    private static OptionItem ChargeDuration;
    private static OptionItem SuperChargeDuration;
    private static OptionItem WarningDuration;
    private static OptionItem FiringDuration;
    private static OptionItem NormalBeamThickness;
    private static OptionItem SuperBeamThickness;
    private static OptionItem SelfDestructOnMissOpt;
    private static OptionItem KillJackalOpt;
    private static OptionItem CanVentOpt;
    private static OptionItem CanSabotageOpt;
    private static OptionItem HasImpostorVisionOpt;
    private static OptionItem CanMakeSidekickOpt;
    private static OptionItem SidekickCooldownOpt;
    private static OptionItem BeamColorModeOpt;
    public static OptionItem TamaCanLoad;
    public static OptionItem TamaLoadCooldown;

    private const int BeamCharCount = 20;
    private const int BeamSizeUnit = 30;
    private const float BeamHalfWidthUnit = 0.075f;
    private const float BeamLengthUnit = 0.015f;
    private const float BeamHitboxMargin = 0.5f;
    private const int WarningCharCount = 20;
    private const float GateForwardOffset = 1.5f;
    private const float GateUpOffset = 0.3f;
    private const float GateRadius = 0.5f;
    private const float BeamBackwardReach = GateForwardOffset + GateRadius;

    private enum Phase { Idle, Charging, Warning, Firing }

    private byte JhhId;
    private PlayerControl JhhPC;
    private Phase CurrentPhase;
    private long PhaseEndTS;
    private bool IsSuperShot;
    private Vector2 StartPosition;
    private Vector2 Direction = Vector2.right;
    private Vector2 LastTrackedPos;
    private NetworkedPlayerInfo.PlayerOutfit OriginalOutfit;
    private readonly HashSet<byte> AlreadyKilled = [];
    private bool PhaseEntryDone;
    private bool HasHit;
    private float ShakePhaseOffset;
    private WaveCannonWarning WarningCNO;
    private WaveCannonBeamSegment BeamCNO;
    private WaveCannonGate GateCNO;

    public bool IsLoaded;
    public bool CanSideKick;

    private bool SkMode;
    private byte SkCandidateId = byte.MaxValue;
    private float SkNearTimer;
    private float SkCooldownTimer;
    private float SkSpawnWaitTimer = -1f;
    private bool SkCanApproach => SkSpawnWaitTimer >= 3f;

    public override bool IsEnable => Instances.Count > 0;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.NeutralRoles, CustomRoles.JackalHadouHo);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo])
            .SetValueFormat(OptionFormat.Seconds);
        ChargeDuration = new FloatOptionItem(Id + 11, "JackalHadouHoChargeTime", new(0.5f, 10f, 0.5f), 3f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo])
            .SetValueFormat(OptionFormat.Seconds);
        SuperChargeDuration = new FloatOptionItem(Id + 12, "JackalHadouHoSuperChargeTime", new(0.5f, 15f, 0.5f), 5f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo])
            .SetValueFormat(OptionFormat.Seconds);
        WarningDuration = new FloatOptionItem(Id + 13, "JackalHadouHoWarningTime", new(0.5f, 5f, 0.5f), 1f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo])
            .SetValueFormat(OptionFormat.Seconds);
        FiringDuration = new FloatOptionItem(Id + 14, "JackalHadouHoFiringTime", new(0.5f, 10f, 0.5f), 3f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo])
            .SetValueFormat(OptionFormat.Seconds);
        NormalBeamThickness = new IntegerOptionItem(Id + 15, "JackalHadouHoBeamThickness", new(1, 8, 1), 2, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo])
            .SetValueFormat(OptionFormat.Times);
        SuperBeamThickness = new IntegerOptionItem(Id + 16, "JackalHadouHoSuperBeamThickness", new(1, 8, 1), 4, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo])
            .SetValueFormat(OptionFormat.Times);
        SelfDestructOnMissOpt = new BooleanOptionItem(Id + 17, "JackalHadouHoSelfDestruct", false, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo]);
        KillJackalOpt = new BooleanOptionItem(Id + 18, "JackalHadouHoKillJackal", false, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo]);
        BeamColorModeOpt = new StringOptionItem(Id + 19, "JackalHadouHoBeamColorMode", new[] { "JackalHadouHoBeamColorRainbow", "JackalHadouHoBeamColorSingle" }, 0, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo]);
        CanVentOpt = new BooleanOptionItem(Id + 20, "CanVent", true, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo]);
        CanSabotageOpt = new BooleanOptionItem(Id + 21, "CanSabotage", false, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo]);
        HasImpostorVisionOpt = new BooleanOptionItem(Id + 22, "ImpostorVision", true, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo]);
        CanMakeSidekickOpt = new BooleanOptionItem(Id + 23, "JackalHadouHoCanMakeSidekick", true, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.JackalHadouHo]);
        SidekickCooldownOpt = new FloatOptionItem(Id + 24, "JackalHadouHoSidekickCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.NeutralRoles)
            .SetParent(CanMakeSidekickOpt)
            .SetValueFormat(OptionFormat.Seconds);
        TamaCanLoad = new BooleanOptionItem(Id + 25, "TamaCanLoad", true, TabGroup.NeutralRoles)
            .SetParent(CanMakeSidekickOpt);
        TamaLoadCooldown = new FloatOptionItem(Id + 26, "TamaLoadCooldown", new(0f, 60f, 0.5f), 10f, TabGroup.NeutralRoles)
            .SetParent(TamaCanLoad)
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        On = false;
        Instances = [];
        NextNoSideKick = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        Instances.Add(this);
        JhhId = playerId;
        JhhPC = playerId.GetPlayer();
        CanSideKick = !NextNoSideKick && CanMakeSidekickOpt.GetBool();
        NextNoSideKick = false;
        IsLoaded = false;
        ResetState();
        SkSpawnWaitTimer = -1f;
        SkCooldownTimer = 0f;
        SkNearTimer = 0f;
        SkCandidateId = byte.MaxValue;
        SkMode = false;
    }

    public override void Remove(byte playerId)
    {
        Instances.RemoveAll(x => x.JhhId == playerId);
        if (Instances.Count == 0) On = false;
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        if (CurrentPhase != Phase.Idle) return false;
        return CanVentOpt.GetBool();
    }

    public override bool CanUseSabotage(PlayerControl pc)
    {
        return base.CanUseSabotage(pc) || (CanSabotageOpt.GetBool() && pc.IsAlive());
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetVision(HasImpostorVisionOpt.GetBool());
        AURoleOptions.PhantomDuration = 0.1f;
        // 超チャージ全シーケンスを上限として cooldown を確保
        AURoleOptions.PhantomCooldown =
            SuperChargeDuration.GetFloat() + WarningDuration.GetFloat() + FiringDuration.GetFloat()
            + KillCooldown.GetFloat();
    }

    public override bool OnVanish(PlayerControl pc)
    {
        TriggerCharge(pc);
        return false;
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return true;
        TriggerCharge(shapeshifter);
        return false;
    }

    private void TriggerCharge(PlayerControl pc)
    {
        if (!pc.IsAlive() || CurrentPhase != Phase.Idle || !GameStates.IsInTask) return;
        StartPosition = pc.GetTruePosition();
        IsSuperShot = IsLoaded;
        EnterCharging(pc);
    }

    private void EnterCharging(PlayerControl pc)
    {
        CurrentPhase = Phase.Charging;
        float dur = IsSuperShot ? SuperChargeDuration.GetFloat() : ChargeDuration.GetFloat();
        PhaseEndTS = Utils.TimeStamp + (long)dur;
        PhaseEntryDone = false;

        byte id = pc.PlayerId;
        if (Camouflage.PlayerSkins.TryGetValue(id, out var original))
        {
            OriginalOutfit = original;
            int chargeColor = original.ColorId == 5 ? 4 : 5;
            var chargeOutfit = new NetworkedPlayerInfo.PlayerOutfit()
                .Set(original.PlayerName, chargeColor, "", "", "", "", "");
            Camouflage.PlayerSkins[id] = chargeOutfit;
            Camouflage.BlockCamouflage = true;
            if (!Camouflage.IsCamouflage)
                Utils.RpcChangeSkin(pc, chargeOutfit);
        }

        Vector2 gatePos = GatePosition();
        Utils.CombineSendTimeLowering(() =>
        {
            GateCNO = new WaveCannonGate(gatePos);
        });

        // 全プレイヤーにキルフラッシュ
        FlashAll();

        // 超チャージ中は 0.1s 周期でキルフラッシュ連打
        if (IsSuperShot)
        {
            int count = (int)(SuperChargeDuration.GetFloat() / 0.1f);
            for (int i = 1; i <= count; i++)
            {
                float t = i * 0.1f;
                LateTask.New(() =>
                {
                    if (CurrentPhase != Phase.Charging || !IsSuperShot || !pc.IsAlive()) return;
                    FlashAll();
                }, t, log: false);
            }
        }
    }

    private static void FlashAll()
    {
        foreach (PlayerControl p in Main.AllAlivePlayerControls)
            p.KillFlash();
    }

    private void RestoreSkin(PlayerControl pc, bool deathPreserve = false)
    {
        if (OriginalOutfit == null) return;
        Camouflage.PlayerSkins[pc.PlayerId] = OriginalOutfit;
        Camouflage.BlockCamouflage = false;
        if (!deathPreserve && !Camouflage.IsCamouflage)
            Utils.RpcChangeSkin(pc, OriginalOutfit);
        OriginalOutfit = null;
    }

    private void ResetState()
    {
        CurrentPhase = Phase.Idle;
        PhaseEndTS = 0;
        IsSuperShot = false;
        StartPosition = Vector2.zero;
        LastTrackedPos = Vector2.zero;
        AlreadyKilled.Clear();
        PhaseEntryDone = false;
        HasHit = false;
        DespawnAllCNOs();
    }

    private void DespawnAllCNOs()
    {
        WarningCNO?.Despawn();
        WarningCNO = null;
        BeamCNO?.Despawn();
        BeamCNO = null;
        GateCNO?.Despawn();
        GateCNO = null;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsInTask) return;

        // SK タイマー
        if (pc.IsAlive() && CanSideKick)
        {
            if (SkSpawnWaitTimer >= 0f && SkSpawnWaitTimer < 3f)
                SkSpawnWaitTimer += Time.fixedDeltaTime;
            if (SkCooldownTimer > 0f)
                SkCooldownTimer -= Time.fixedDeltaTime;

            if (SkCandidateId != byte.MaxValue && SkCanApproach && SkCooldownTimer <= 0f)
            {
                PlayerControl skTarget = SkCandidateId.GetPlayer();
                if (skTarget == null || !skTarget.IsAlive())
                {
                    SkCandidateId = byte.MaxValue;
                    SkNearTimer = 0f;
                }
                else
                {
                    float dist = Vector2.Distance(pc.GetTruePosition(), skTarget.GetTruePosition());
                    if (dist <= 1.5f)
                    {
                        SkNearTimer += Time.fixedDeltaTime;
                        if (SkNearTimer >= 1.5f)
                        {
                            DoSideKick(skTarget);
                            SkCandidateId = byte.MaxValue;
                            SkNearTimer = 0f;
                        }
                    }
                    else
                    {
                        SkNearTimer = 0f;
                    }
                }
            }
        }

        if (!pc.IsAlive())
        {
            if (CurrentPhase != Phase.Idle)
            {
                DespawnAllCNOs();
                RestoreSkin(pc, deathPreserve: true);
                CurrentPhase = Phase.Idle;
            }
            return;
        }

        if (CurrentPhase == Phase.Idle)
        {
            Vector2 pos = pc.GetTruePosition();
            if (LastTrackedPos == Vector2.zero) { LastTrackedPos = pos; return; }
            float deltaX = pos.x - LastTrackedPos.x;
            if (Mathf.Abs(deltaX) > 0.05f)
                Direction = deltaX > 0 ? Vector2.right : Vector2.left;
            LastTrackedPos = pos;
            return;
        }

        pc.TP(StartPosition, log: false);

        switch (CurrentPhase)
        {
            case Phase.Charging:
                if (Utils.TimeStamp >= PhaseEndTS)
                {
                    CurrentPhase = Phase.Warning;
                    PhaseEndTS = Utils.TimeStamp + WarningDuration.GetInt();
                    PhaseEntryDone = false;
                }
                break;

            case Phase.Warning:
                if (!PhaseEntryDone)
                {
                    SpawnWarning();
                    PhaseEntryDone = true;
                }
                if (Utils.TimeStamp >= PhaseEndTS)
                {
                    CurrentPhase = Phase.Firing;
                    PhaseEndTS = Utils.TimeStamp + FiringDuration.GetInt();
                    PhaseEntryDone = false;
                    ShakePhaseOffset = UnityEngine.Random.Range(0f, 100f);
                }
                break;

            case Phase.Firing:
                if (!PhaseEntryDone)
                {
                    DespawnWarning();
                    SpawnBeam();
                    AlreadyKilled.Clear();
                    HasHit = false;
                    if (IsSuperShot) ConsumeTama();
                    PhaseEntryDone = true;
                }

                if (BeamCNO != null)
                {
                    float t = Time.time + ShakePhaseOffset;
                    float wave = Mathf.Sin(t * 47f) * 0.7f + Mathf.Sin(t * 113f + 1.3f) * 0.3f;
                    int thick = IsSuperShot ? SuperBeamThickness.GetInt() : NormalBeamThickness.GetInt();
                    float amp = BeamHalfWidthUnit * thick * 0.2f;
                    Vector2 perp = new(-Direction.y, Direction.x);
                    BeamCNO.Position = BeamCNOPosition() + perp * (wave * amp);
                }

                CheckBeamKills(pc);

                if (Utils.TimeStamp >= PhaseEndTS)
                {
                    DespawnAllCNOs();
                    RestoreSkin(pc);
                    pc.SetKillCooldown();
                    if (!HasHit && SelfDestructOnMissOpt.GetBool())
                    {
                        pc.Suicide();
                    }
                    CurrentPhase = Phase.Idle;
                    PhaseEntryDone = false;
                    IsSuperShot = false;
                }
                break;
        }
    }

    private void ConsumeTama()
    {
        foreach (PlayerControl p in Main.AllAlivePlayerControls)
        {
            if (Main.PlayerStates[p.PlayerId].Role is Tama tama && tama.OwnerId == JhhId && tama.HasLoaded)
            {
                p.Suicide(PlayerState.DeathReason.Bombed);
                IsLoaded = false;
                tama.HasLoaded = false;
                break;
            }
        }
    }

    private Vector2 GatePosition() => StartPosition + new Vector2(Direction.x * GateForwardOffset, GateUpOffset);
    private Vector2 BeamStartPoint() => GatePosition() + Direction * GateRadius;
    private Vector2 BeamCNOPosition() => BeamStartPoint();
    private Vector2 WarningCNOPosition() => BeamStartPoint();

    private void SpawnWarning()
    {
        Vector2 pos = WarningCNOPosition();
        string sprite = WarningSprite();
        Utils.CombineSendTimeLowering(() => { WarningCNO = new WaveCannonWarning(pos, sprite); });
    }

    private void DespawnWarning()
    {
        WarningCNO?.Despawn();
        WarningCNO = null;
    }

    private void SpawnBeam()
    {
        Vector2 pos = BeamCNOPosition();
        string sprite = BeamSprite();
        Utils.CombineSendTimeLowering(() => { BeamCNO = new WaveCannonBeamSegment(pos, sprite); });
    }

    private string WarningSprite()
    {
        string chars = string.Join("     ", Enumerable.Repeat("⚠", WarningCharCount));
        bool firingRight = Direction.x > 0;
        int thick = IsSuperShot ? SuperBeamThickness.GetInt() : NormalBeamThickness.GetInt();
        int size = thick * BeamSizeUnit;
        return firingRight
            ? $"<size={size}><alpha=#00>{chars}<color=#FFFF00FF>{chars}</color></size>"
            : $"<size={size}><color=#FFFF00FF>{chars}</color><alpha=#00>{chars}</size>";
    }

    private string BeamSprite()
    {
        bool firingRight = Direction.x > 0;
        int thick = IsSuperShot ? SuperBeamThickness.GetInt() : NormalBeamThickness.GetInt();
        int size = thick * BeamSizeUnit;
        string row = new('━', BeamCharCount);
        string transparent = $"<alpha=#00>{row}";
        string visible = BuildBeamVisible(row);
        return firingRight
            ? $"<size={size}>{transparent}{visible}</size>"
            : $"<size={size}>{visible}{transparent}</size>";
    }

    private string BuildBeamVisible(string row)
    {
        // 超ビーム: 必ず赤単色
        if (IsSuperShot) return $"<color=#ff0000>{row}</color>";

        // 通常ビーム: BeamColorMode で切替
        bool rainbow = BeamColorModeOpt.GetValue() == 0;
        if (!rainbow) return $"<color=#00b4eb>{row}</color>";

        // レインボー: 7 色を BeamCharCount 文字に均等配分
        string[] colors = { "#ff0000", "#ff7f00", "#ffff00", "#00ff00", "#0000ff", "#4b0082", "#8b00ff" };
        var sb = new System.Text.StringBuilder();
        int per = Mathf.Max(1, BeamCharCount / colors.Length);
        int written = 0;
        for (int i = 0; i < colors.Length; i++)
        {
            int len = (i == colors.Length - 1) ? BeamCharCount - written : per;
            if (len <= 0) continue;
            sb.Append($"<color={colors[i]}>").Append(new string('━', len)).Append("</color>");
            written += len;
        }
        return sb.ToString();
    }

    private void CheckBeamKills(PlayerControl shooter)
    {
        int thick = IsSuperShot ? SuperBeamThickness.GetInt() : NormalBeamThickness.GetInt();
        int size = thick * BeamSizeUnit;
        Vector2 eyePos = BeamStartPoint();
        float halfWidth = BeamHalfWidthUnit * thick + BeamHitboxMargin;
        float beamLength = BeamLengthUnit * size * BeamCharCount;

        foreach (PlayerControl target in Main.EnumerateAlivePlayerControls())
        {
            if (target.PlayerId == shooter.PlayerId) continue;
            if (AlreadyKilled.Contains(target.PlayerId)) continue;
            if (!KillJackalOpt.GetBool() && target.GetCountTypes() == CountTypes.Jackal) continue;
            if (Pelican.IsEaten(target.PlayerId)) continue;
            if (target.Is(CustomRoles.Pestilence)) continue;

            Vector2 delta = target.GetTruePosition() - eyePos;
            float along = Vector2.Dot(delta, Direction);
            if (along < -BeamBackwardReach || along > beamLength + BeamHitboxMargin) continue;
            Vector2 lateral = delta - Direction * along;
            if (lateral.magnitude > halfWidth) continue;

            AlreadyKilled.Add(target.PlayerId);
            target.RpcExileV2();
            RPC.PlaySoundRPC(shooter.PlayerId, Sounds.KillSound);
            PlayerState state = Main.PlayerStates[target.PlayerId];
            state.deathReason = PlayerState.DeathReason.Kill;
            state.RealKiller = (DateTime.Now, shooter.PlayerId);
            state.SetDead();
            Utils.AfterPlayerDeathTasks(target);
            HasHit = true;
        }
    }

    public override void OnReportDeadBody()
    {
        if (CurrentPhase != Phase.Idle)
        {
            DespawnAllCNOs();
            if (JhhPC != null) RestoreSkin(JhhPC);
            CurrentPhase = Phase.Idle;
            PhaseEntryDone = false;
            AlreadyKilled.Clear();
            IsSuperShot = false;
            HasHit = false;
        }
        SkMode = false;
    }

    public override void AfterMeetingTasks()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (JhhPC == null || !JhhPC.IsAlive()) return;
        SkSpawnWaitTimer = 0f;
        SkCooldownTimer = SidekickCooldownOpt.GetFloat();
        SkNearTimer = 0f;
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (voter.PlayerId != JhhId) return false;
        if (!CanSideKick || !voter.IsAlive()) return false;
        if (target == null) return false;

        // 自投票: モード切替
        if (target.PlayerId == JhhId)
        {
            if (!SkMode)
            {
                SkMode = true;
                SkCandidateId = byte.MaxValue;
                Utils.SendMessage(GetString("JackalHadouHoSkModeOn"), JhhId);
            }
            else
            {
                SkMode = false;
                SkCandidateId = byte.MaxValue;
                Utils.SendMessage(GetString("JackalHadouHoSkModeOff"), JhhId);
            }
            return true;
        }

        // モード ON 時の候補指定
        if (SkMode)
        {
            if (!target.IsAlive() || !Jackal.CanBeSidekick(target))
            {
                Utils.SendMessage(GetString("JackalHadouHoSkInvalid"), JhhId);
                SkMode = false;
                return true;
            }

            SkCandidateId = target.PlayerId;
            SkMode = false;
            Utils.SendMessage(string.Format(GetString("JackalHadouHoSkCandidate"), target.GetRealName()), JhhId);
            return true;
        }

        return false;
    }

    private void DoSideKick(PlayerControl target)
    {
        if (!Jackal.CanBeSidekick(target))
        {
            Utils.SendMessage(GetString("JackalHadouHoSkInvalid"), JhhId);
            return;
        }

        CanSideKick = false;
        target.RpcSetCustomRole(CustomRoles.Tama);
        target.RpcChangeRoleBasis(CustomRoles.Tama);

        if (Main.PlayerStates[target.PlayerId].Role is Tama tama)
            tama.SetOwner(JhhId);

        Utils.SendMessage(string.Format(GetString("JackalHadouHoSkSuccess"), target.GetRealName()), JhhId);
        Utils.NotifyRoles();
    }

    public void SetLoaded(bool loaded)
    {
        IsLoaded = loaded;
        if (JhhPC != null && loaded)
            JhhPC.Notify(GetString("JackalHadouHoLoaded"));
    }
}
