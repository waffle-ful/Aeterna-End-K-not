using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;
using InnerNet;
using UnityEngine;

namespace EndKnot.Modules;

/// <summary>
/// 通常モードの試合で、人の体を本編のかくれんぼの鬼の体 (マスク) にする (ホストのみ)。
///
/// 本編の客は SetRole を受けた時、役職を付けた直後に <c>GameManager.Instance.GetBodyType(自分)</c> で体を決める。
/// かくれんぼの GameManager は「その客の画面で対象がインポスター系なら鬼の体」を返し、通常の GameManager は普通の体しか返さない。
/// そこで GameManager を Despawn → かくれんぼの物を Spawn → 対象の SetRole を送る → Despawn → 通常の物を Spawn し直す (窓)。
/// 試合中の SetRole はどれも上書き可で送っているので、客の画面の役職は固まらず、試合中いつでも通る。
/// 通常の GameManager の部品の OnGameStart は空なので、出し直した物に StartGame が無くても困らない。
///
/// 限り:
/// - 鬼の体はその客の画面で対象がインポスター系の時しか出ない。クルーの客には対象をインポスターとして送る。
///   RoleMap には書かない (会議の前に必ず外すので、会議の配り直しは元の見え方のままでよい)。
///   自分の画面でキルを持つ客 (デシンクのキル役) には送らない。仲間に見えてキルできなくなるため。
/// - 後で対象の SetRole がその客へ届くと、通常の GameManager の下で体が決め直されて外れる。
///   SetRole の送り口を通った後に、窓を開け直して付け直す。
/// - 窓の間 (約 3 秒) は客の画面で設定が引けず、全員が歩けない。窓の間と、窓の後の少しの間は通報を預かり、
///   キル・勝敗判定・設定の送信・役職の基底の変更を止める。
///   止まっている所を見せないよう、窓の少し前に全員の視界を自分の周りだけまで狭め、窓が閉じるまで暗くする
///   (0 にすると自分の姿と名前の下の文字も見えなくなる)。
/// - Despawn と Spawn の間は空ける。客の手元で同じフレームに重なると、新しい GameManager は Awake で自分を消す。
/// </summary>
public static class SeekerBody
{
    // Despawn から Spawn までの間 (客の手元で同じフレームに重ならないように)
    private const float SpawnGap = 0.8f;
    // かくれんぼの GameManager を出してから SetRole を送るまで
    private const float SendDelay = 0.2f;
    // SetRole を送ってから、かくれんぼの GameManager を消すまで (客が SetRole を処理し終えるまで)
    private const float Hold = 0.8f;
    // 窓の後、次の窓を開けるまでの最短の間
    private const float BetweenWindows = 1f;
    // 暗転させてから GameManager を消すまで (視界が暗くなり終えるまで)
    private const float CoverLead = 0.4f;
    // SetRole を送ってから会議を開けるようになるまで (本編の客は SetRole の直後に会議が来ると会議の画面が壊れる)
    private const float SettleTime = 2.5f;

    private enum Phase { Idle, Cover, SpawnHns, Send, DespawnHns, SpawnNormal }

    private sealed class Entry
    {
        public Action OnApplied;
        public Action OnReleased;
        // マスクを送った見る人と、送る前の見え方
        public readonly Dictionary<byte, RoleTypes> Original = new();
    }

    private static Phase phase;
    private static float nextAt;
    private static float idleSince;
    private static float settleUntil;
    private static readonly Dictionary<byte, Entry> masked = new();
    private static readonly Dictionary<byte, float> applyAt = new();
    private static readonly HashSet<byte> releaseAfterWindow = new();
    private static readonly List<byte> sending = new();
    private static readonly List<byte> work = new();
    /// <summary>窓の間の視界の倍率 (自分の周りだけが見える)</summary>
    public const float DimVision = 0.15f;
    // 窓の暗さを掛けている人
    private static readonly HashSet<byte> dimmed = new();
    // 失敗から戻そうとした回数 (窓ごと)
    private static int recoverTries;
    // 自分で送っている間は、送り口の記録で付け直しを予約しない
    private static bool selfSending;

    /// <summary>窓の最中か (GameManager を差し替えている間)。この間は GameManager.Instance が無いか、かくれんぼの物</summary>
    public static bool InWindow => phase != Phase.Idle;
    public static bool IsMasked(byte playerId) => masked.Count > 0 && masked.ContainsKey(playerId);
    /// <summary>窓の暗さを掛けている人か (人ごとの設定を組む時に視界を狭める)</summary>
    public static bool IsDimmed(byte playerId) => dimmed.Count > 0 && dimmed.Contains(playerId);
    /// <summary>通報を預かる間か (窓の間と、SetRole を送った直後)</summary>
    public static bool HoldsReports => InWindow || Time.realtimeSinceStartup < settleUntil;

    /// <summary>
    /// 本人へ SetRole を送ると鬼の体が外れるか、窓の中へ SetRole が入る間か。
    /// ファントムの透明化の取り消しで、本人へ SetRole を送らないために見る
    /// </summary>
    public static bool KeepsRoleFor(PlayerControl pc) => pc != null && (InWindow || IsMasked(pc.PlayerId));

    public static void BeginSettling() => settleUntil = Mathf.Max(settleUntil, Time.realtimeSinceStartup + SettleTime);

    /// <summary>
    /// クルーの視界の倍率に掛けると、停電中でもインポスターの視界 (停電の影響を受けない) と同じ半径になる値。
    /// 本編の客の視界の半径は、クルーが Lerp(MinLightRadius, MaxLightRadius, 明るさ) × CrewLightMod、
    /// インポスターが MaxLightRadius × ImpostorLightMod
    /// </summary>
    public static float CrewLightCompensation()
    {
        var ship = ShipStatus.Instance;
        if (!ship || !ship.Systems.TryGetValue(SystemTypes.Electrical, out var system)) return 1f;
        var lights = system.TryCast<SwitchSystem>();
        if (lights == null) return 1f;
        float radius = Mathf.Lerp(ship.MinLightRadius, ship.MaxLightRadius, lights.Level);
        if (radius <= 0.01f) return 1f;
        return Mathf.Clamp(ship.MaxLightRadius / radius, 1f, 10f);
    }

    public static void Init()
    {
        phase = Phase.Idle;
        nextAt = 0f;
        idleSince = 0f;
        settleUntil = 0f;
        masked.Clear();
        applyAt.Clear();
        releaseAfterWindow.Clear();
        sending.Clear();
        dimmed.Clear();
        recoverTries = 0;
        selfSending = false;
    }

    /// <summary>
    /// pc に鬼の体を付ける。次に開ける窓で送る。onApplied は送った窓の後に、onReleased は会議などでこちらから外した時に呼ぶ
    /// (<see cref="Release"/> を自分で呼んだ時は呼ばない)
    /// </summary>
    public static void Apply(PlayerControl pc, Action onApplied = null, Action onReleased = null)
    {
        if (pc == null || !AmongUsClient.Instance.AmHost) return;
        if (!masked.TryGetValue(pc.PlayerId, out var entry)) masked[pc.PlayerId] = entry = new();
        entry.OnApplied = onApplied;
        entry.OnReleased = onReleased;
        releaseAfterWindow.Remove(pc.PlayerId);
        RequestApply(pc.PlayerId, 0f);
        Logger.Info($"鬼の体を付ける: {pc.GetNameWithRole()}", nameof(SeekerBody));
    }

    /// <summary>pc の鬼の体を外す (見え方を元に戻す)。窓の最中なら窓の後で外す</summary>
    public static void Release(PlayerControl pc)
    {
        if (pc == null || !masked.ContainsKey(pc.PlayerId)) return;
        applyAt.Remove(pc.PlayerId);
        // 窓の間は SetRole を送れない (GameManager が無いか、かくれんぼの物) ので、窓の後で外す
        if (InWindow)
        {
            releaseAfterWindow.Add(pc.PlayerId);
            return;
        }
        Restore(pc.PlayerId, notify: false);
    }

    /// <summary>
    /// 通報を受けた時に呼ぶ (ReportDeadBodyPatch)。鬼の体を付けている人がいれば全員から外し、
    /// 外した SetRole が落ち着くまで通報を預からせる。true なら今の通報は預かる
    /// </summary>
    public static bool HoldReport()
    {
        if (!AmongUsClient.Instance.AmHost) return false;
        if (masked.Count > 0)
        {
            applyAt.Clear();
            work.Clear();
            work.AddRange(masked.Keys);
            foreach (var id in work)
            {
                if (InWindow) releaseAfterWindow.Add(id);
                else Restore(id, notify: true);
            }
            BeginSettling();
        }
        return HoldsReports;
    }

    /// <summary>SetRole の送り口で target の見え方が送られた時。鬼の体を付けている人なら付け直しを予約する</summary>
    public static void OnRoleSent(PlayerControl target)
    {
        if (selfSending || masked.Count == 0 || target == null || !masked.ContainsKey(target.PlayerId)) return;
        RequestApply(target.PlayerId, 0.5f);
    }

    private static void RequestApply(byte id, float delay)
    {
        float at = Time.realtimeSinceStartup + delay;
        if (!applyAt.TryGetValue(id, out var cur) || cur < at) applyAt[id] = at;
    }

    private static bool CanStart(float now)
    {
        if (now - idleSince < BetweenWindows) return false;
        if (!GameStates.IsInTask || GameStates.IsMeeting || MeetingHud.Instance || ExileController.Instance) return false;
        if (!Main.IntroDestroyed || !GameManager.Instance || !ShipStatus.Instance) return false;
        if (AntiBlackout.SkipTasks || ReportDeadBodyPatch.MeetingStarted) return false;
        if (GameEndChecker.Ended || GameStates.IsEnded || AmongUsClient.Instance.IsGameOver) return false;
        if (Options.CurrentGameMode != CustomGameMode.Standard) return false;
        return GameOptionsManager.Instance.currentGameMode == GameModes.Normal;
    }

    private static void Update()
    {
        var client = AmongUsClient.Instance;
        if (!client || !client.AmHost || !GameStates.IsInGame) return;
        if (phase == Phase.Idle && masked.Count == 0) return;
        float now = Time.realtimeSinceStartup;

        if (phase == Phase.Idle)
        {
            DropDead();
            if (applyAt.Count == 0 || !CanStart(now)) return;
            sending.Clear();
            foreach (var (id, at) in applyAt) if (now >= at) sending.Add(id);
            if (sending.Count == 0) return;
            foreach (var id in sending) applyAt.Remove(id);
            Step(client, StartWindow);
            return;
        }
        if (now < nextAt) return;
        Step(client, () => Advance(client));
    }

    // 窓の 1 段を進める。失敗したら通常の GameManager を必ず戻す
    private static void Step(InnerNetClient client, Action act)
    {
        try { act(); }
        catch (Exception ex)
        {
            Logger.Exception(ex, nameof(SeekerBody));
            Recover(client);
        }
    }

    private static void StartWindow()
    {
        Logger.Info($"窓を開ける: [{string.Join(",", sending)}]", nameof(SeekerBody));
        BeginSettling();
        // 暗転の設定は窓に入る前に送る (窓の間は送れない)
        foreach (var pc in Main.AllPlayerControls)
            if (pc.Data != null && !pc.Data.Disconnected) dimmed.Add(pc.PlayerId);
        Utils.MarkEveryoneDirtySettings();
        phase = Phase.Cover;
        nextAt = Time.realtimeSinceStartup + CoverLead;
    }

    private static void Advance(InnerNetClient client)
    {
        float now = Time.realtimeSinceStartup;
        BeginSettling();
        switch (phase)
        {
            case Phase.Cover:
                client.Despawn(GameManager.Instance);
                GameManager.Instance = null;
                phase = Phase.SpawnHns;
                nextAt = now + SpawnGap;
                break;
            case Phase.SpawnHns:
                client.Spawn(GameManagerCreator.CreateGameManager(GameModes.HideNSeek), -2, SpawnFlags.None);
                phase = Phase.Send;
                nextAt = now + SendDelay;
                break;
            case Phase.Send:
                foreach (var id in sending) SendMask(id);
                phase = Phase.DespawnHns;
                nextAt = now + Hold;
                break;
            case Phase.DespawnHns:
                client.Despawn(GameManager.Instance);
                GameManager.Instance = null;
                phase = Phase.SpawnNormal;
                nextAt = now + SpawnGap;
                break;
            case Phase.SpawnNormal:
                SpawnNormal(client);
                FinishWindow();
                break;
        }
    }

    private static void SpawnNormal(InnerNetClient client)
    {
        var gm = GameManagerCreator.CreateGameManager(GameModes.Normal);
        client.Spawn(gm, -2, SpawnFlags.None);
        gm.LogicOptions?.SyncOptions();
    }

    private static void FinishWindow()
    {
        phase = Phase.Idle;
        recoverTries = 0;
        idleSince = Time.realtimeSinceStartup;
        Logger.Info("窓を閉じた", nameof(SeekerBody));
        // 暗転を明ける。出し直した GameManager は全員に同じ設定を配ったので、人ごとの設定を送り直す
        dimmed.Clear();
        Utils.MarkEveryoneDirtySettings();
        BeginSettling();
        foreach (var id in sending)
        {
            if (releaseAfterWindow.Contains(id)) continue;
            if (masked.TryGetValue(id, out var entry))
            {
                try { entry.OnApplied?.Invoke(); }
                catch (Exception ex) { Logger.Exception(ex, nameof(SeekerBody)); }
            }
        }
        sending.Clear();
        if (releaseAfterWindow.Count > 0)
        {
            work.Clear();
            work.AddRange(releaseAfterWindow);
            releaseAfterWindow.Clear();
            foreach (var id in work) Restore(id, notify: true);
        }
    }

    // 失敗した時: 通常の GameManager を戻して窓を閉じる。かくれんぼの物が残っていれば消し、間を空けてから出す
    // (同じフレームに Despawn と Spawn を送ると、客の手元で新しい GameManager が Awake で自分を消す)
    private static void Recover(InnerNetClient client)
    {
        try
        {
            if (GameManager.Instance && GameManager.Instance.TryCast<NormalGameManager>() != null)
            {
                FinishWindow();
                return;
            }
            if (recoverTries++ < 2)
            {
                if (GameManager.Instance) client.Despawn(GameManager.Instance);
                GameManager.Instance = null;
                phase = Phase.SpawnNormal;
                nextAt = Time.realtimeSinceStartup + SpawnGap;
                return;
            }
        }
        catch (Exception ex) { Logger.Exception(ex, nameof(SeekerBody)); }
        FinishWindow();
    }

    // 死んだ人・抜けた人の印を消す (死ぬ時の幽霊の役職で体は戻っている)
    private static void DropDead()
    {
        if (masked.Count == 0) return;
        work.Clear();
        foreach (var id in masked.Keys)
        {
            var pc = Utils.GetPlayerById(id);
            if (pc == null || pc.Data == null || pc.Data.Disconnected || !pc.IsAlive()) work.Add(id);
        }
        foreach (var id in work)
        {
            var entry = masked[id];
            masked.Remove(id);
            applyAt.Remove(id);
            try { entry.OnReleased?.Invoke(); }
            catch (Exception ex) { Logger.Exception(ex, nameof(SeekerBody)); }
        }
    }

    private static bool IsKillerRole(RoleTypes role) => role is RoleTypes.Impostor or RoleTypes.Shapeshifter or RoleTypes.Phantom or RoleTypes.Viper;

    private static bool TryGetView(byte seerId, byte targetId, out RoleTypes role)
    {
        if (StartGameHostPatch.RpcSetRoleReplacer.RoleMap.TryGetValue((seerId, targetId), out var pair))
        {
            role = pair.RoleType;
            return true;
        }
        role = RoleTypes.Crewmate;
        return false;
    }

    private static void SendMask(byte id)
    {
        var target = Utils.GetPlayerById(id);
        if (target == null || !target.IsAlive() || !masked.TryGetValue(id, out var entry)) return;
        selfSending = true;
        try
        {
            foreach (var seer in Main.AllPlayerControls)
            {
                if (seer.Data == null || seer.Data.Disconnected) continue;
                if (seer.AmOwner)
                {
                    // ホストの画面は手元で体だけ替える (ホストの手元の役職は試合の始めに固まっていて、SetRole は通らない。
                    // 手元の役職がインポスター系とは限らないので、かくれんぼの GameManager に決めさせずに鬼の体を付ける)
                    target.MyPhysics.SetBodyType(PlayerBodyTypes.Seeker);
                    continue;
                }
                if (seer.OwnerId < 0 || !TryGetView(seer.PlayerId, target.PlayerId, out var current)) continue;
                RoleTypes send;
                if (seer.PlayerId == target.PlayerId || IsKillerRole(current)) send = current;
                // 自分の画面でキルを持つ人には送らない (インポスターに見せると仲間になってキルできなくなる)
                else if (seer.IsAlive() && TryGetView(seer.PlayerId, seer.PlayerId, out var self) && IsKillerRole(self)) continue;
                else send = RoleTypes.Impostor;
                entry.Original.TryAdd(seer.PlayerId, current);
                target.RpcSetRoleDesync(send, seer.OwnerId);
            }
        }
        finally { selfSending = false; }
    }

    // 見え方を送る前の物に戻す。通常の GameManager の下で SetRole が走るので体も普通に戻る
    private static void Restore(byte id, bool notify)
    {
        if (!masked.Remove(id, out var entry)) return;
        applyAt.Remove(id);
        var target = Utils.GetPlayerById(id);
        if (target != null && target.IsAlive() && target.Data != null && !target.Data.Disconnected)
        {
            selfSending = true;
            try
            {
                if (GameManager.Instance) target.MyPhysics.SetBodyType(GameManager.Instance.GetBodyType(target));
                foreach (var (seerId, role) in entry.Original)
                {
                    var seer = Utils.GetPlayerById(seerId);
                    if (seer == null || seer.Data == null || seer.Data.Disconnected || seer.OwnerId < 0) continue;
                    // 窓の間に役職の見え方が変わっていれば、今の見え方で戻す
                    target.RpcSetRoleDesync(TryGetView(seerId, id, out var now) ? now : role, seer.OwnerId);
                }
            }
            finally { selfSending = false; }
            BeginSettling();
        }
        Logger.Info($"鬼の体を外す: {target?.GetNameWithRole() ?? id.ToString()}", nameof(SeekerBody));
        if (!notify) return;
        try { entry.OnReleased?.Invoke(); }
        catch (Exception ex) { Logger.Exception(ex, nameof(SeekerBody)); }
    }

    [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.FixedUpdate))]
    private static class TickPatch
    {
        public static void Postfix() => Update();
    }

    // 窓の間はホストの手元でも本編がキルの距離を引けない。キルの対象は無いことにする
    [HarmonyPatch(typeof(ImpostorRole), nameof(ImpostorRole.FindClosestTarget))]
    private static class FindClosestTargetPatch
    {
        public static bool Prefix(ref PlayerControl __result)
        {
            if (!InWindow) return true;
            __result = null;
            return false;
        }
    }
}
