using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Roles;
using HarmonyLib;
using UnityEngine;

namespace EndKnot.Modules;

/// <summary>
/// 通常モードの試合で、本編のかくれんぼの懐中電灯 (向いた先だけが見える円錐と、体の周りの小さな円) を持たせる (ホストのみ)。
///
/// 本編の客の PlayerControl.IsFlashlightEnabled は、手元の設定がかくれんぼ型か (と UseFlashlight) だけを見て、
/// 試合の種類も役職も見ない。懐中電灯を点ける・消すのは AdjustLighting で、これは視界の半径が変わった時・死んだ時・
/// イントロの終わりにしか呼ばれない。そこで客ごとに次の順で送る:
///   1. かくれんぼ型の設定 (UseFlashlight と大きさ・視界の半径を少しずらす)。半径が変わるので客の手元で懐中電灯が点く
///   2. 少し後に、1 と同じ半径の通常の設定。半径が変わらないので懐中電灯はそのまま残る
/// 1 のままにすると客の手元の通常の設定が空になり、毎フレームの例外で歩けなくなるので、すぐ 2 で戻す。
/// 戻した後も半径を動かすと消えるので、ずらした半径のまま送り続け、点け直すたびにずらす向きを替える。
/// 会議明け・停電・暗転などで半径が変わると消えるので、その後に点け直す。点け直しの間 (HoldSeconds) は客が止まる。
/// ホスト自身は手元の判定を書き換えて点ける (電文は要らない)。
/// </summary>
public static class Flashlight
{
    // 点け直すたびに足す・外す視界の倍率。半径が本編の Approximately で別の値になれば足りる
    private const float Shift = 0.02f;
    // かくれんぼ型の設定のままにしておく秒数 (送った時から)。この間に客の FixedUpdate が少なくとも 1 回回ればよい
    private const float HoldSeconds = 0.4f;
    // かくれんぼ型を送る予約をしたのに送れないまま、この秒数が過ぎたら戻しの送りを諦めずに続ける目安
    private const float SendTimeout = 3f;
    // イントロ・会議明けの後、視界の送り直し (暗転対策など) が落ち着くまで待つ秒数
    private const float AfterPhaseDelay = 2f;
    // 停電が始まった・直った後、視界が絞られ終わるまで待つ秒数
    private const float LightsDelay = 4f;
    // 送る視界の値が変わった後に点け直すまでの秒数 (続けて変わる時にまとめる)
    private const float AfterChangeDelay = 1f;
    // 点け直しを人ごとにずらす間隔と、同じ人を続けて点け直さない最短の間隔。
    // 1 回の点け直しは同じ客へ設定の全文を 2 本送るので、全員を同じ瞬間に送らず、同じ客へも詰めて送らない
    private const float ArmStagger = 0.15f;
    private const float MinArmInterval = 1.5f;

    private static readonly HashSet<byte> shifted = [];
    private static readonly Dictionary<byte, float> armAt = [];
    // 点け直しの最中の人と、通常の設定へ戻す時刻 (かくれんぼ型がまだ送られていなければ +∞)
    private static readonly Dictionary<byte, float> holdUntil = [];
    private static readonly Dictionary<byte, float> holdSince = [];
    // 点け直しの番号。列に並んだかくれんぼ型の送りが、戻した後に遅れて出ないようにする
    private static readonly Dictionary<byte, int> holdGen = [];
    private static readonly Dictionary<byte, (float Crew, float Imp)> lastBase = [];
    private static readonly Dictionary<byte, float> lastArmAt = [];
    private static readonly List<byte> work = [];
    private static float nextArmSlot;
    private static bool wasInTask;
    private static float inTaskSince;
    private static bool lightsWere;
    private static float nextLightsCheck;
    // 試合が始まって最初の Tick で決める (設定は試合中に変わらない)。毎フレームの判定を軽くする
    private static bool activeThisGame;
    private static bool activeKnown;

    private static bool GlobalOn => Options.FlashlightMode.GetBool();
    private static bool TorchOn => Options.TorchFlashlight.GetBool() && CustomRoles.Torch.IsEnable();

    /// <summary>この試合で懐中電灯を使うか (通常モードで、全員かトーチのどちらかが有効)。</summary>
    private static bool Active
        => Options.CurrentGameMode == CustomGameMode.Standard
           && GameOptionsManager.Instance != null && GameOptionsManager.Instance.currentGameMode == GameModes.Normal
           && (GlobalOn || TorchOn);

    private static bool Has(PlayerControl pc)
        => pc != null && pc.Data != null && !pc.Data.IsDead && !pc.Data.Disconnected
           && (GlobalOn || (TorchOn && pc.Is(CustomRoles.Torch)));

    private static (float Crew, float Imp) Sizes(PlayerControl pc)
    {
        if (TorchOn && pc.Is(CustomRoles.Torch))
        {
            float size = Options.TorchFlashlightSize.GetFloat();
            return (size, size);
        }

        return (Options.FlashlightCrewSize.GetFloat(), Options.FlashlightImpostorSize.GetFloat());
    }

    /// <summary>試合の始めに呼ぶ (OnGameStartedPatch)。</summary>
    public static void Init()
    {
        shifted.Clear();
        armAt.Clear();
        holdUntil.Clear();
        holdGen.Clear();
        holdSince.Clear();
        lastBase.Clear();
        lastArmAt.Clear();
        nextArmSlot = 0f;
        wasInTask = false;
        inTaskSince = 0f;
        lightsWere = false;
        nextLightsCheck = 0f;
        activeThisGame = false;
        activeKnown = false;
    }

    // 試合が終わった時。点け直しの途中の印を残すと、ロビーでその人の設定が送られなくなる
    private static void Clear()
    {
        holdUntil.Clear();
        holdSince.Clear();
        armAt.Clear();
        activeThisGame = false;
        activeKnown = true;
    }

    // 点け直しの最中か (かくれんぼ型を送る予約をしてから、通常へ戻すまで)
    private static bool IsHolding(byte playerId) => holdUntil.Count > 0 && holdUntil.ContainsKey(playerId);

    /// <summary>
    /// ふだんの設定の送信を止めるか。かくれんぼ型を送った後、通常へ戻すまでの間だけ止める (送ったかくれんぼ型を上書きしない)。
    /// 送る予約をしただけの間は止めない (列が詰まっても、その人の速さや視界の変更を遅らせない)
    /// </summary>
    public static bool SuppressesSend(byte playerId)
        => holdUntil.Count > 0 && holdUntil.TryGetValue(playerId, out float until) && !float.IsPositiveInfinity(until);

    /// <summary>誰かの点け直しの最中か</summary>
    public static bool AnyHolding => holdUntil.Count > 0;

    /// <summary>懐中電灯を持つ全員の点け直しを予約する (GameManager を出し直して設定が配り直された時など)</summary>
    public static void RequestAll()
    {
        if (!activeThisGame) return;

        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
            if (Has(pc)) RequestArm(pc.PlayerId, AfterChangeDelay);
    }

    private static void RequestArm(byte playerId, float delay)
    {
        float at = Time.realtimeSinceStartup + delay;
        if (!armAt.TryGetValue(playerId, out float cur) || cur < at) armAt[playerId] = at;
    }

    /// <summary>
    /// 1 人分の設定を組み終えた所で呼ぶ (PlayerGameOptionsSender.BuildGameOptions の最後)。
    /// 視界の値が変わったら点け直しを予約し、ずらしている人には半径のずれを足す。
    /// </summary>
    public static void ApplyGameOptions(PlayerControl player, IGameOptions opt)
    {
        if (!activeThisGame || !AmongUsClient.Instance.AmHost || !Has(player)) return;
        byte id = player.PlayerId;
        float crew = opt.GetFloat(FloatOptionNames.CrewLightMod);
        float imp = opt.GetFloat(FloatOptionNames.ImpostorLightMod);

        if (!lastBase.TryGetValue(id, out var b) || !Mathf.Approximately(b.Crew, crew) || !Mathf.Approximately(b.Imp, imp))
        {
            lastBase[id] = (crew, imp);
            RequestArm(id, AfterChangeDelay);
        }

        // 暗転中 (視界 0) は真っ暗のままにする
        if (crew <= 0f && imp <= 0f) return;

        if (shifted.Contains(id))
        {
            opt.SetFloat(FloatOptionNames.CrewLightMod, crew + Shift);
            opt.SetFloat(FloatOptionNames.ImpostorLightMod, imp + Shift);
        }
    }

    private static bool InTaskNow()
        => GameStates.IsInTask && Main.IntroDestroyed && ShipStatus.Instance && !ExileController.Instance && !MeetingHud.Instance;

    // 設定の電文が今送れるか (送れない間に送ると、捨てられるか古い GameManager へ届く)
    private static bool CanSendNow() => !AntiBlackout.SkipTasks && !SeekerBody.InWindow && GameManager.Instance;

    private static void Tick()
    {
        if (!activeKnown)
        {
            activeThisGame = Active;
            activeKnown = true;
        }

        if (!activeThisGame || !AmongUsClient.Instance.AmHost) return;
        float now = Time.realtimeSinceStartup;

        if (holdUntil.Count > 0)
        {
            work.Clear();
            foreach ((byte id, float until) in holdUntil)
                if (now >= until || now - holdSince.GetValueOrDefault(id) >= SendTimeout) work.Add(id);

            // 送れない間が長く続いたら、客を止めたままにしないよう列を通さず戻す
            foreach (byte id in work) FinishHold(id, direct: now - holdSince.GetValueOrDefault(id) >= SendTimeout * 2f);
        }

        bool inTask = InTaskNow();

        if (inTask && !wasInTask)
        {
            // イントロの後・会議明け。視界が配り直されるので、落ち着いてから点け直す
            inTaskSince = now;
            RequestAllAfter(AfterPhaseDelay);
        }

        wasInTask = inTask;

        if (now >= nextLightsCheck)
        {
            nextLightsCheck = now + 0.25f;
            bool lights = inTask && Utils.IsActive(SystemTypes.Electrical);

            if (lights != lightsWere)
            {
                lightsWere = lights;
                RequestAllAfter(LightsDelay);
            }
        }

        if (armAt.Count == 0 || !inTask || now - inTaskSince < AfterPhaseDelay || now < nextArmSlot || !CanSendNow()) return;
        if (ReportDeadBodyPatch.MeetingStarted || SeekerBody.HoldsReports) return;

        work.Clear();
        foreach ((byte id, float at) in armAt)
            if (now >= at) work.Add(id);

        foreach (byte id in work)
        {
            PlayerControl pc = Utils.GetPlayerById(id);

            if (pc == null || !Has(pc))
            {
                armAt.Remove(id);
                continue;
            }

            if (IsHolding(id)) continue;
            if (Main.PlayerStates.TryGetValue(id, out PlayerState state) && state.IsBlackOut) continue;
            if (lastArmAt.TryGetValue(id, out float last) && now - last < MinArmInterval) continue;
            armAt.Remove(id);
            lastArmAt[id] = now;
            // 1 回の Tick で点け直すのは 1 人まで。次の人は ArmStagger 後
            nextArmSlot = now + ArmStagger;
            Arm(pc);
            break;
        }
    }

    private static void RequestAllAfter(float delay)
    {
        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
            if (Has(pc)) RequestArm(pc.PlayerId, delay);
    }

    private static PlayerGameOptionsSender SenderOf(byte playerId)
    {
        foreach (GameOptionsSender s in GameOptionsSender.AllSenders)
            if (s is PlayerGameOptionsSender ps && ps.player != null && ps.player.PlayerId == playerId) return ps;

        return null;
    }

    private static void Arm(PlayerControl pc)
    {
        if (pc.AmOwner)
        {
            // ホストは手元の判定 (下のパッチ) で点くので、点け直しを促すだけ
            pc.AdjustLighting();
            return;
        }

        if (pc.OwnerId < 0) return;
        PlayerGameOptionsSender sender = SenderOf(pc.PlayerId);
        IGameOptions normal = sender?.BuildGameOptions();
        if (normal == null) return;

        byte id = pc.PlayerId;
        bool nowShifted = shifted.Contains(id);
        float crew = normal.GetFloat(FloatOptionNames.CrewLightMod) - (nowShifted ? Shift : 0f);
        float imp = normal.GetFloat(FloatOptionNames.ImpostorLightMod) - (nowShifted ? Shift : 0f);
        if (crew <= 0f && imp <= 0f) return;
        float next = nowShifted ? 0f : Shift;
        (float crewSize, float impSize) = Sizes(pc);

        var hns = new HideNSeekGameOptionsV12(new InnerNet.UnityLogger().CastFast<Hazel.ILogger>());
        int count = GameData.Instance ? GameData.Instance.PlayerCount : 4;
        hns.SetRecommendations(Mathf.Max(count, 4), true);
        hns.MapId = normal.MapId;
        // 通常の設定の送りと同じ範囲に切る (公式鯖へは範囲の外の値を送らない。切るのは通常型の送りの中だけなので、ここで切る)
        hns.NumImpostors = Mathf.Clamp(normal.NumImpostors, 1, 3);
        hns.PlayerSpeedMod = Mathf.Clamp(normal.GetFloat(FloatOptionNames.PlayerSpeedMod), Main.MinSpeed, 3f);
        hns.KillCooldown = normal.GetFloat(FloatOptionNames.KillCooldown);
        hns.KillDistance = Mathf.Clamp(normal.GetInt(Int32OptionNames.KillDistance), 0, 2);
        hns.CrewLightMod = crew + next;
        hns.ImpostorLightMod = imp + next;
        hns.useFlashlight = true;
        hns.CrewmateFlashlightSize = crewSize;
        hns.ImpostorFlashlightSize = impSize;

        // 戻す時刻は実際に送れた時に決める (送りは送る数の制限の列に並ぶ)
        holdUntil[id] = float.PositiveInfinity;
        holdSince[id] = Time.realtimeSinceStartup;

        int gen = holdGen.GetValueOrDefault(id) + 1;
        holdGen[id] = gen;

        sender.SendOptionsQueued(hns.CastFast<IGameOptions>(),
            () => holdUntil.ContainsKey(id) && holdGen.GetValueOrDefault(id) == gen && CanSendNow() && !ReportDeadBodyPatch.MeetingStarted && !MeetingHud.Instance,
            () => holdUntil[id] = Time.realtimeSinceStartup + HoldSeconds);

        Logger.Info($"{pc.GetNameWithRole()}: 懐中電灯を点ける (視界 {crew:0.00}/{imp:0.00} + {next})", nameof(Flashlight));
    }

    // かくれんぼ型を送った後、同じ半径の通常の設定へ戻す。戻しは列を通さずすぐ送る (列から出る時に送れなくなっていると、
    // 戻しが消えて客が歩けないままになる)。1 人あたり点け直し 1 回に 1 本なので、送る数は増えすぎない。
    // direct なら送れない間 (暗転対策・鬼の体の差し替え) でも送る (会議の直前と、送れない間が長く続いた時)
    private static void FinishHold(byte id, bool direct)
    {
        PlayerGameOptionsSender sender = SenderOf(id);
        PlayerControl pc = Utils.GetPlayerById(id);

        if (sender == null || pc == null || pc.Data == null || pc.Data.Disconnected || !GameStates.InGame)
        {
            holdUntil.Remove(id);
            holdSince.Remove(id);
            return;
        }

        // 送れない間に戻しを送ると捨てられて、客が歩けないままになる。送れるようになるまで待つ (会議の直前だけは送る)
        if (!direct && !CanSendNow()) return;

        // まだ送っていないかくれんぼ型は送らせない
        holdGen[id] = holdGen.GetValueOrDefault(id) + 1;
        bool sent = !float.IsPositiveInfinity(holdUntil[id]);
        holdUntil.Remove(id);
        holdSince.Remove(id);
        // 送っていなければ半径もずらしていないので、ずらす向きは替えない (戻しの送りも、止めていた分の送りとして要る)
        if (sent && !shifted.Remove(id)) shifted.Add(id);

        // 止めていた間に溜まった変更もこれで送られる
        IGameOptions opt = sender.BuildGameOptions();
        if (opt == null) return;

        sender.SendOptionsNow(opt);
    }

    /// <summary>
    /// 会議を開く前に呼ぶ (SeekerBody.HoldReport と同じ所)。かくれんぼ型のまま会議に入ると客の会議の画面が壊れるので、
    /// 先に通常へ戻す。戻しは列を通さずすぐ送るので、会議の電文より先に届く。
    /// </summary>
    public static void BeforeMeeting()
    {
        if (holdUntil.Count == 0) return;
        work.Clear();
        work.AddRange(holdUntil.Keys);

        foreach (byte id in work)
        {
            try { FinishHold(id, direct: true); }
            catch (Exception ex) { Logger.Exception(ex, nameof(Flashlight)); }
        }
    }

    [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.FixedUpdate))]
    private static class TickPatch
    {
        public static void Postfix()
        {
            try { Tick(); }
            catch (Exception ex) { Logger.Exception(ex, nameof(Flashlight)); }
        }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
    private static class GameEndPatch
    {
        public static void Postfix() => Clear();
    }

    // ホスト自身: 本編は設定の GameMode で判定するので、ホストの分はここで点ける
    private static bool HostHas
        => activeThisGame && AmongUsClient.Instance && AmongUsClient.Instance.AmHost && !LobbyBehaviour.Instance && Has(PlayerControl.LocalPlayer);

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.IsFlashlightEnabled))]
    private static class HostEnabledPatch
    {
        public static void Postfix(ref bool __result)
        {
            if (!__result && HostHas) __result = true;
        }
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.AdjustLighting))]
    private static class HostLightingPatch
    {
        public static bool Prefix(PlayerControl __instance)
        {
            if (!HostHas || __instance != PlayerControl.LocalPlayer || !__instance.lightSource) return true;
            (float crewSize, float impSize) = Sizes(__instance);
            float size = __instance.Data.Role != null && __instance.Data.Role.IsImpostor ? impSize : crewSize;
            __instance.SetFlashlightInputMethod();
            __instance.lightSource.SetupLightingForGameplay(true, size, __instance.TargetFlashlight ? __instance.TargetFlashlight.transform : null);
            return false;
        }
    }
}
