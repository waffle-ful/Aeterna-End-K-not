using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

// 暴走鬼: ペット (ファントムの基底ならファントム) のボタンで暴走する。暴走の間は本編のかくれんぼの鬼の体 (マスク) になって
// 全員に正体が見えるが、キルクールが短く、足が速く、視界が広い。決めた時間が過ぎるか、会議が始まるか、もう一度ボタンを押す (鎮静化) と元に戻る。
// 1 回の暴走で決めた人数をキルすると忘我になり、仲間のインポスターもキルできるようになる。
// 役職の発想は Nebula on the Ship のバーサーカー (コードは自前)。
public class Rampager : RoleBase
{
    private const int Id = 710400;
    public static bool On;
    public override bool IsEnable => On;

    private static OptionItem KillCooldown;
    public static OptionItem AbilityCooldown;
    private static OptionItem AbilityDuration;
    private static OptionItem RampageKillCooldown;
    private static OptionItem RampageSpeed;
    private static OptionItem RampageVision;
    private static OptionItem CanCalmDown;
    private static OptionItem KillsForTrance;

    // 暗転が明けてから、名前の下の「背筋が凍った」を消すまで
    private const float ChillLinger = 2f;

    private byte RampagerId;
    private bool Rampaging;
    // 暴走が終わる時刻 (Time.realtimeSinceStartup)。鬼の体を付け終えてから数え始める
    private float EndAt = float.PositiveInfinity;
    private float LastKillAt = float.NegativeInfinity;
    private int LastShownSeconds = -1;
    // 変身の暗転の間 (と明けた少し後) か。暴走鬼以外の人の名前の下に「背筋が凍った」を出す
    private bool Chilling;
    private float ChillUntil = float.PositiveInfinity;
    // 今の暴走でキルした数
    private int RampageKills;
    // 忘我か (仲間もキルできる)
    private bool Trance;
    // 忘我の見え方を、窓が明けてから送る予約
    private bool TranceSendPending;
    // 忘我で見え方を替えた仲間と、替える前の見え方 (自分の画面で)
    private readonly Dictionary<byte, RoleTypes> TranceViews = [];
    // 暴走の前の足の速さと、暴走で上げた後の足の速さ
    private float SavedSpeed;
    private float BoostedSpeed;

    /// <summary>暴走の間か。暴走の間はボタンのクールを付けない (もう一度押して鎮静化できるように)</summary>
    public bool IsRampaging => Rampaging;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 30f, new FloatValueRule(2.5f, 180f, 2.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityCooldown, 40, new IntegerValueRule(5, 180, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityDuration, 15, new IntegerValueRule(5, 60, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref RampageKillCooldown, 5f, new FloatValueRule(0f, 60f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref RampageSpeed, 1.5f, new FloatValueRule(1f, 3f, 0.1f), OptionFormat.Multiplier)
            .AutoSetupOption(ref RampageVision, 2f, new FloatValueRule(0.25f, 5f, 0.25f), OptionFormat.Multiplier)
            .AutoSetupOption(ref CanCalmDown, true)
            // 0 で忘我にならない
            .AutoSetupOption(ref KillsForTrance, 2, new IntegerValueRule(0, 5, 1), OptionFormat.Players);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        RampagerId = playerId;
        Rampaging = false;
        EndAt = float.PositiveInfinity;
        LastKillAt = float.NegativeInfinity;
        LastShownSeconds = -1;
        Chilling = false;
        ChillUntil = float.PositiveInfinity;
        RampageKills = 0;
        Trance = false;
        TranceSendPending = false;
        TranceViews.Clear();
    }

    public override void Remove(byte playerId)
    {
        EndRampage(release: true);
    }

    private PlayerControl Player => Utils.GetPlayerById(RampagerId);

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = Rampaging ? RampageKillCooldown.GetFloat() : KillCooldown.GetFloat();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        if (Options.UsePhantomBasis.GetBool()) AURoleOptions.PhantomCooldown = AbilityCooldown.GetInt();
        if (Rampaging) opt.SetFloat(FloatOptionNames.ImpostorLightMod, RampageVision.GetFloat());
    }

    public override void OnPet(PlayerControl pc)
    {
        Trigger(pc);
    }

    public override bool OnVanish(PlayerControl pc)
    {
        Trigger(pc);
        return false;
    }

    // 暴走の間に押すと鎮静化する (変身中と窓の間は何もしない)
    private void Trigger(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !pc.IsAlive() || !GameStates.IsInTask) return;

        if (Rampaging)
        {
            if (CanCalmDown.GetBool() && !float.IsPositiveInfinity(EndAt) && !SeekerBody.InWindow)
            {
                Logger.Info($"鎮静化: {pc.GetNameWithRole()}", nameof(Rampager));
                EndRampage(release: true);
            }

            return;
        }

        Rampaging = true;
        RampageKills = 0;
        Trance = false;
        EndAt = float.PositiveInfinity;
        LastShownSeconds = -1;
        Chilling = true;
        ChillUntil = float.PositiveInfinity;
        // 暗転が始まる前に名前を送る (暗い間も自分の名前の下の文字は見える)
        Utils.NotifyRoles();
        SavedSpeed = Main.AllPlayerSpeed.GetValueOrDefault(RampagerId, Main.RealOptionsData?.GetFloat(FloatOptionNames.PlayerSpeedMod) ?? 1f);
        BoostedSpeed = SavedSpeed * RampageSpeed.GetFloat();
        Main.AllPlayerSpeed[RampagerId] = BoostedSpeed;
        pc.ResetKillCooldown();
        pc.MarkDirtySettings();
        SeekerBody.Apply(pc, OnApplied, () => EndRampage(release: false));
        Logger.Info($"暴走: {pc.GetNameWithRole()}", nameof(Rampager));
    }

    // 鬼の体を付けた窓の後。窓で SetRole を送り直したので、キルクールを付け直す
    private void OnApplied()
    {
        PlayerControl pc = Player;
        if (!Rampaging || pc == null || !pc.IsAlive()) return;
        float now = Time.realtimeSinceStartup;

        if (float.IsPositiveInfinity(EndAt))
        {
            EndAt = now + AbilityDuration.GetInt();
            ChillUntil = now + ChillLinger;
            Utils.NotifyRoles();
        }

        float remaining = Mathf.Max(0.01f, RampageKillCooldown.GetFloat() - (now - LastKillAt));
        pc.SetKillCooldown(remaining);
        if (Trance) SendTranceViews();
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (killer == null || killer.PlayerId != RampagerId || target == null || target.PlayerId == killer.PlayerId) return;
        if (!Rampaging) return;
        LastKillAt = Time.realtimeSinceStartup;
        RampageKills++;
        int need = KillsForTrance.GetInt();
        if (Trance || need <= 0 || RampageKills < need) return;
        Trance = true;
        Logger.Info($"忘我: {killer.GetNameWithRole()}", nameof(Rampager));
        // 忘我の間は、周りの人を見た目で見分けられなくなる
        SeerCamouflage.Begin(killer);
        SendTranceViews();
        Utils.NotifyRoles(SpecifySeer: killer, SpecifyTarget: killer);
    }

    // 自分の画面で、生きている仲間 (キルを持つ人に見えている人) を、キルの対象にできる見え方にする。
    // 本編の客の画面では仲間をキルの対象にできないので、キルを持たない役職に見せる
    private void SendTranceViews()
    {
        PlayerControl pc = Player;
        if (!Trance || pc == null || !pc.IsAlive()) return;

        if (SeekerBody.InWindow)
        {
            TranceSendPending = true;
            return;
        }

        TranceSendPending = false;
        var sent = false;

        foreach (PlayerControl ally in Main.AllAlivePlayerControls)
        {
            if (ally.PlayerId == RampagerId || TranceViews.ContainsKey(ally.PlayerId)) continue;
            if (!ally.Is(CustomRoleTypes.Impostor)) continue;
            if (!StartGameHostPatch.RpcSetRoleReplacer.RoleMap.TryGetValue((RampagerId, ally.PlayerId), out var view)) continue;
            if (view.RoleType is not (RoleTypes.Impostor or RoleTypes.Shapeshifter or RoleTypes.Phantom or RoleTypes.Viper)) continue;
            TranceViews[ally.PlayerId] = view.RoleType;
            ally.RpcSetRoleDesync(RoleTypes.Scientist, pc.OwnerId);
            sent = true;
        }

        if (sent) SeekerBody.BeginSettling();
    }

    // 忘我の見え方を戻す。窓の間は送れないので、窓が明けてから (OnFixedUpdate) 戻す
    private void RestoreTranceViews()
    {
        if (TranceViews.Count == 0 || SeekerBody.InWindow) return;
        PlayerControl pc = Player;

        if (pc != null && pc.Data != null && !pc.Data.Disconnected)
        {
            foreach ((byte id, RoleTypes role) in TranceViews)
            {
                PlayerControl ally = Utils.GetPlayerById(id);
                if (ally == null || !ally.IsAlive()) continue;
                RoleTypes now = StartGameHostPatch.RpcSetRoleReplacer.RoleMap.TryGetValue((RampagerId, id), out var view) ? view.RoleType : role;
                ally.RpcSetRoleDesync(now, pc.OwnerId);
            }

            SeekerBody.BeginSettling();
        }

        TranceViews.Clear();
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        if (!Rampaging)
        {
            if (TranceViews.Count > 0) RestoreTranceViews();
            return;
        }

        if (!pc.IsAlive())
        {
            EndRampage(release: true);
            return;
        }

        if (SeekerBody.InWindow) return;
        if (TranceSendPending) SendTranceViews();
        float now = Time.realtimeSinceStartup;

        if (Chilling && now >= ChillUntil)
        {
            Chilling = false;
            Utils.NotifyRoles();
        }

        float left = EndAt - now;

        if (left <= 0f)
        {
            EndRampage(release: true);
            return;
        }

        if (float.IsPositiveInfinity(left)) return;
        var seconds = (int)System.Math.Ceiling(left);
        if (seconds == LastShownSeconds) return;
        LastShownSeconds = seconds;
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    private void EndRampage(bool release)
    {
        if (!Rampaging) return;
        Rampaging = false;
        Chilling = false;
        Trance = false;
        TranceSendPending = false;
        EndAt = float.PositiveInfinity;
        RestoreTranceViews();
        SeerCamouflage.End(Player);

        // 暴走の間に他の効果が足の速さを決め直していなければ元へ、決め直していれば上げた分だけ戻す
        if (Main.AllPlayerSpeed.TryGetValue(RampagerId, out float speed))
            Main.AllPlayerSpeed[RampagerId] = Mathf.Approximately(speed, BoostedSpeed) ? SavedSpeed : speed / RampageSpeed.GetFloat();

        PlayerControl pc = Player;
        if (release) SeekerBody.Release(pc);

        if (pc != null && pc.IsAlive())
        {
            pc.ResetKillCooldown();
            pc.MarkDirtySettings();

            if (release && GameStates.IsInTask && !ReportDeadBodyPatch.MeetingStarted)
            {
                pc.SetKillCooldown();

                // 暴走の間はボタンのクールを付けていないので、ここで始める
                if (Options.UsePhantomBasis.GetBool()) pc.RpcResetAbilityCooldown();
                else pc.AddAbilityCD();
            }
        }

        Utils.NotifyRoles();
        Logger.Info($"暴走が終わった: {pc?.GetNameWithRole() ?? RampagerId.ToString()}", nameof(Rampager));
    }

    public override void OnReportDeadBody()
    {
        EndRampage(release: true);
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting || seer == null || target == null || seer.PlayerId != target.PlayerId) return string.Empty;

        // 暴走鬼以外の人の画面で、自分の名前の下に出す
        if (seer.PlayerId != RampagerId)
            return Chilling && seer.IsAlive() ? $"<#a8d8ff>{GetString("RampagerChill")}</color>" : string.Empty;

        if (!Rampaging) return string.Empty;

        if (float.IsPositiveInfinity(EndAt))
            return Chilling ? $"<#ff3030>{GetString("RampagerTransform")}</color>" : string.Empty;

        int seconds = Mathf.Max(0, Mathf.CeilToInt(EndAt - Time.realtimeSinceStartup));
        string text = string.Format(GetString("RampagerRemaining"), seconds);
        int need = KillsForTrance.GetInt();
        if (Trance) text += " " + GetString("RampagerTrance");
        else if (need > 0) text += $" ({RampageKills}/{need})";
        return text;
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        if (Options.UsePets.GetBool() && !Options.UsePhantomBasis.GetBool())
            hud.PetButton?.OverrideText(GetString("RampagerButtonText"));
        else
            hud.AbilityButton?.OverrideText(GetString("RampagerButtonText"));
    }
}
