using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

// ============================================================
// Safecracker (金庫破り) — Neutral/Benign  ※ SuperNewRoles からの移植
//
// コンセプト:
//   割り当てられるタスクが「金庫を開ける」1 種類だけになる第三陣営。
//   全タスクを終えた時点で生存していれば単独勝利。
//   タスク進捗が設定%に達するごとに能力が段階的に解禁される。
//
// タスク配布は Patches/TaskAssignPatch.cs の RpcSetTasksPatch 内で
// 専用分岐に差し替える (同一タスク Index を TaskCount 個並べて配る)。
// 金庫タスク (TaskTypes.UnlockSafe) は Airship にしか存在しないため、
// この役職は CustomRoleSelector で Airship 限定スポーンにしている。
//
// 【サボタージュ解禁の実現方法】バニラのサボボタンはインポスター基底にしか
// 生えないが、この役職をインポスター基底へ desync するとタスクが開けなくなる
// (バニラ Console.CanUse がインポスターを弾き、EHR 側も Utils.HasTasks が
// 「インポスター基底 = タスク無し」で揃えている)。金庫が開けなければ役職ごと死ぬ。
// そのため「ボタンを生やす」のではなく Alchemist と同じホスト駆動の
// ShipStatus.RpcUpdateSystem でサボタージュを起こす方式を採り、
// トリガーはペット押下にしている (非モッドクライアントにもそのまま届く)。
// ※ ペットが無効なロビーではこの能力だけ発動手段が無い。
// ============================================================
public class Safecracker : RoleBase
{
    private const int Id = 705400;

    public static bool On;

    public static OptionItem TaskCount;
    private static OptionItem KillGuardTaskRate;
    private static OptionItem MaxKillGuardCount;
    private static OptionItem ExiledGuardTaskRate;
    private static OptionItem MaxExiledGuardCount;
    private static OptionItem UseVentTaskRate;
    private static OptionItem UseSaboTaskRate;
    private static OptionItem ImpostorVisionTaskRate;
    private static OptionItem KnowImpostorsTaskRate;
    private static OptionItem SabotageCooldown;

    private byte SafecrackerId;
    private int KillGuardUsed;
    private int ExiledGuardUsed;

    // 解禁は「達成済みタスク数」から毎回計算する (ラッチしない)。
    // ラッチフラグは /revive で役職インスタンスが再生成されないため
    // 蘇生後も残り続ける — 計算で出せる物は計算する方が安全。
    // ただしベント基底の付与と視界の再同期は「一度だけ撃つ RPC」なので、
    // 送信済みかどうかだけはフラグで覚える。
    private bool VentBasisGranted;
    private bool VisionSynced;
    private bool BasisRestoreScheduled;

    // 追放阻止は投票集計中に起きる。その場で通知しても会議終了処理に流されるので会議明けに出す。
    private bool PendingExiledGuardNotice;
    private bool ExiledGuardUsedThisMeeting;
    private long LastSabotageTS;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref TaskCount, 3, new IntegerValueRule(1, 15, 1), OptionFormat.Pieces)
            .AutoSetupOption(ref KillGuardTaskRate, 50f, new FloatValueRule(0f, 100f, 5f), OptionFormat.Percent)
            .AutoSetupOption(ref MaxKillGuardCount, 1, new IntegerValueRule(1, 15, 1), OptionFormat.Times, overrideParent: KillGuardTaskRate)
            .AutoSetupOption(ref ExiledGuardTaskRate, 50f, new FloatValueRule(0f, 100f, 5f), OptionFormat.Percent)
            .AutoSetupOption(ref MaxExiledGuardCount, 1, new IntegerValueRule(1, 15, 1), OptionFormat.Times, overrideParent: ExiledGuardTaskRate)
            .AutoSetupOption(ref UseVentTaskRate, 50f, new FloatValueRule(0f, 100f, 5f), OptionFormat.Percent)
            .AutoSetupOption(ref UseSaboTaskRate, 50f, new FloatValueRule(0f, 100f, 5f), OptionFormat.Percent)
            .AutoSetupOption(ref ImpostorVisionTaskRate, 50f, new FloatValueRule(0f, 100f, 5f), OptionFormat.Percent)
            .AutoSetupOption(ref KnowImpostorsTaskRate, 50f, new FloatValueRule(0f, 100f, 5f), OptionFormat.Percent)
            .AutoSetupOption(ref SabotageCooldown, 30f, new FloatValueRule(5f, 180f, 5f), OptionFormat.Seconds, overrideParent: UseSaboTaskRate);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        SafecrackerId = playerId;
        KillGuardUsed = 0;
        ExiledGuardUsed = 0;
        VentBasisGranted = false;
        VisionSynced = false;
        BasisRestoreScheduled = false;
        PendingExiledGuardNotice = false;
        ExiledGuardUsedThisMeeting = false;
        LastSabotageTS = 0;
    }

    public override void OnReportDeadBody()
    {
        ExiledGuardUsedThisMeeting = false;
    }

    public override void Remove(byte playerId)
    {
        if (SafecrackerId == playerId) On = false;
    }

    /// <summary>現在のマップから金庫タスクを探す。見つからなければ null。</summary>
    public static NormalPlayerTask GetSafeTask()
    {
        if (!ShipStatus.Instance) return null;

        NormalPlayerTask found = FindIn(ShipStatus.Instance.LongTasks);
        found ??= FindIn(ShipStatus.Instance.ShortTasks);
        found ??= FindIn(ShipStatus.Instance.CommonTasks);
        return found;

        static NormalPlayerTask FindIn(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<NormalPlayerTask> tasks)
        {
            if (tasks == null) return null;

            foreach (NormalPlayerTask task in tasks)
                if (task != null && task.TaskType == TaskTypes.UnlockSafe)
                    return task;

            return null;
        }
    }

    private (int Completed, int Total) GetProgress()
    {
        PlayerControl pc = Utils.GetPlayerById(SafecrackerId);
        if (pc == null) return (0, 0);

        TaskState state = pc.GetTaskState();
        return state == null ? (0, 0) : (state.CompletedTasksCount, state.AllTasksCount);
    }

    /// <summary>設定%に達しているか。0% は「その能力を使わせない」の意味 (SNR 準拠)。</summary>
    private static bool IsUnlocked(OptionItem rate, int completed, int total)
    {
        float required = rate.GetFloat();
        if (required <= 0f || total <= 0) return false;

        return completed >= Mathf.CeilToInt(total * (required / 100f));
    }

    private bool IsUnlocked(OptionItem rate)
    {
        (int completed, int total) = GetProgress();
        return IsUnlocked(rate, completed, total);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        // ベント解禁後に Engineer へ desync するので、その時のために先に入れておく
        AURoleOptions.EngineerCooldown = 0f;
        AURoleOptions.EngineerInVentMaxTime = 0f;

        opt.SetVision(IsUnlocked(ImpostorVisionTaskRate));
    }

    public override bool CanUseKillButton(PlayerControl pc) => false;

    public override bool CanUseImpostorVentButton(PlayerControl pc) => pc.IsAlive() && IsUnlocked(UseVentTaskRate);

    public override bool CanUseVent(PlayerControl pc, int ventId) => pc.IsAlive() && IsUnlocked(UseVentTaskRate);

    public override bool CanUseSabotage(PlayerControl pc) => pc.IsAlive() && IsUnlocked(UseSaboTaskRate);

    /// <summary>
    /// サボタージュ解禁後はペット押下でホストがサボタージュを起こす。
    /// クライアント側にサボボタンを生やすにはインポスター基底へ desync するしかないが、
    /// それをやるとバニラの Console.CanUse がタスクコンソールを弾き
    /// (EHR 側も Utils.HasTasks が「インポスター基底 = タスク無し」で揃えている)、
    /// 金庫を開けられなくなって役職ごと死ぬ。Alchemist と同じホスト駆動の
    /// RpcUpdateSystem なら非モッドクライアントにもそのまま届く。
    /// </summary>
    public override void OnPet(PlayerControl pc)
    {
        if (pc.PlayerId != SafecrackerId || !pc.IsAlive()) return;

        if (!IsUnlocked(UseSaboTaskRate))
        {
            // 無言で return すると「押しても何も起きない」だけになり、未解禁なのか
            // 壊れているのか本人に区別できない。理由は必ず返す。
            pc.Notify(GetString("SafecrackerSabotageLocked"));
            return;
        }

        long now = Utils.TimeStamp;
        if (LastSabotageTS != 0 && now - LastSabotageTS < (long)SabotageCooldown.GetFloat())
        {
            pc.Notify(GetString("SafecrackerSabotageCooldown"));
            return;
        }

        if (!TriggerSabotage())
        {
            pc.Notify(GetString("SafecrackerSabotageUnavailable"));
            return;
        }

        LastSabotageTS = now;
        pc.Notify(GetString("SafecrackerSabotageTriggered"));
    }

    private static bool TriggerSabotage()
    {
        if (!ShipStatus.Instance) return false;

        SystemTypes[] candidates = [SystemTypes.Comms, SystemTypes.Electrical, SystemTypes.LifeSupp, SystemTypes.Reactor, SystemTypes.Laboratory, SystemTypes.HeliSabotage];

        var available = new List<SystemTypes>();

        foreach (SystemTypes sabo in candidates)
        {
            if (!ShipStatus.Instance.Systems.ContainsKey(sabo)) continue;
            if (Utils.IsActive(sabo)) continue;

            available.Add(sabo);
        }

        if (available.Count == 0) return false;

        SystemTypes picked = available[IRandom.Instance.Next(available.Count)];
        ShipStatus.Instance.RpcUpdateSystem(picked, 128);
        Logger.Info($"Sabotage triggered: {picked}", "Safecracker");
        return true;
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        // 未解禁のうちは既定の「ペット」表示のままにする。解禁前から「サボタージュ」と
        // 出ていると、押しても何も起きない時間が長く続いて壊れて見える。
        if (id != SafecrackerId || !IsUnlocked(UseSaboTaskRate)) return;

        hud.PetButton?.OverrideText(GetString("SafecrackerPetButtonText"));
    }

    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if (base.KnowRole(seer, target)) return true;

        return seer.PlayerId == SafecrackerId && target.Is(CustomRoleTypes.Impostor) && IsUnlocked(KnowImpostorsTaskRate);
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target)
    {
        if (killer == null || target == null || target.PlayerId != SafecrackerId) return true;
        if (KillGuardUsed >= MaxKillGuardCount.GetInt()) return true;
        if (!IsUnlocked(KillGuardTaskRate)) return true;

        KillGuardUsed++;
        killer.SetKillCooldown();
        target.Notify(GetString("SafecrackerKillGuardActivated"));
        Logger.Info($"Kill blocked ({KillGuardUsed}/{MaxKillGuardCount.GetInt()})", "Safecracker");
        return false;
    }

    /// <summary>
    /// 追放の決定処理から呼ばれる。true を返すとこのプレイヤーは追放されない。
    /// 呼び出し位置は Dad.OnVotedOut と同じ (最多得票候補になった時点)。
    /// </summary>
    public static bool OnVotedOut(byte id)
    {
        if (!On) return false;
        if (!Main.PlayerStates.TryGetValue(id, out PlayerState state) || state.Role is not Safecracker safecracker) return false;
        if (safecracker.ExiledGuardUsed >= MaxExiledGuardCount.GetInt()) return false;
        if (!safecracker.IsUnlocked(ExiledGuardTaskRate)) return false;

        // 集計 Prefix は1つの会議で複数回走りうる。回数制限のある能力なので、
        // 同じ会議での2回目以降は「守るが消費しない」に倒す (1会議の追放は高々1人)。
        if (safecracker.ExiledGuardUsedThisMeeting) return true;

        safecracker.ExiledGuardUsedThisMeeting = true;
        safecracker.ExiledGuardUsed++;
        safecracker.PendingExiledGuardNotice = true;
        Logger.Info($"Ejection prohibited ({safecracker.ExiledGuardUsed}/{MaxExiledGuardCount.GetInt()})", "Safecracker");
        return true;
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (pc.PlayerId != SafecrackerId) return;

        // OnTaskComplete は TaskState のカウンタが増える前に呼ばれる (GameState.cs:504 / :542)
        int completed = completedTaskCount + 1;

        if (completed >= totalTaskCount)
        {
            if (!pc.IsAlive()) return;

            Logger.Info("All safes cracked — Safecracker wins", "Safecracker");
            CustomWinnerHolder.ResetAndSetWinner(CustomWinner.Safecracker);
            CustomWinnerHolder.WinnerIds.Add(SafecrackerId);
            return;
        }

        RefreshUnlocks(pc, completed, totalTaskCount);
    }

    private void RefreshUnlocks(PlayerControl pc, int completed, int total)
    {
        var unlocked = new List<string>();
        var needsSync = false;

        if (!VentBasisGranted && IsUnlocked(UseVentTaskRate, completed, total))
        {
            VentBasisGranted = true;
            GrantVentBasis(pc);
            needsSync = true;
            unlocked.Add(GetString("SafecrackerUnlock.Vent"));
        }

        if (!VisionSynced && IsUnlocked(ImpostorVisionTaskRate, completed, total))
        {
            VisionSynced = true;
            needsSync = true;
            unlocked.Add(GetString("SafecrackerUnlock.Vision"));
        }

        // ベントと視界のしきい値が同じだと同じタスクで両方跨ぐ。設定の内容は同一なので送信は1本にまとめる。
        if (needsSync) pc.SyncSettings();

        if (WasJustUnlocked(KillGuardTaskRate)) unlocked.Add(GetString("SafecrackerUnlock.KillGuard"));
        if (WasJustUnlocked(ExiledGuardTaskRate)) unlocked.Add(GetString("SafecrackerUnlock.ExiledGuard"));
        if (WasJustUnlocked(UseSaboTaskRate)) unlocked.Add(GetString("SafecrackerUnlock.Sabotage"));
        if (WasJustUnlocked(KnowImpostorsTaskRate)) unlocked.Add(GetString("SafecrackerUnlock.KnowImpostors"));

        if (unlocked.Count > 0)
        {
            pc.Notify(string.Format(GetString("SafecrackerAbilityUnlocked"), string.Join(", ", unlocked)), 8f);
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }

        return;

        // 今回のタスクで初めて条件を満たしたものだけを拾う
        bool WasJustUnlocked(OptionItem rate) => IsUnlocked(rate, completed, total) && !IsUnlocked(rate, completed - 1, total);
    }

    private static void GrantVentBasis(PlayerControl pc)
    {
        // 基底は Crewmate のまま配役している (タスクを確実に受け取るため)。
        // ベント解禁時に自分のクライアントだけ Engineer へ差し替える (Missioneer と同型)。
        // SyncSettings は呼び出し側でまとめて撃つ (同フレームの二重送信を避けるため)。
        pc.RpcSetRoleDesync(RoleTypes.Engineer, pc.OwnerId);
    }

    public override void AfterMeetingTasks()
    {
        if (!AmongUsClient.Instance.AmHost) return;

        PlayerControl pc = Utils.GetPlayerById(SafecrackerId);
        if (pc == null || !pc.IsAlive()) return;

        bool notice = PendingExiledGuardNotice;
        PendingExiledGuardNotice = false;

        if (notice) pc.Notify(GetString("SafecrackerExiledGuardActivated"), 8f);

        // AntiBlackout.RevertToActualRoleTypes() が会議後に本来の RoleType へ戻すため、
        // 解禁済みのベント基底は毎会議後に張り直す必要がある (Missioneer と同じ理由)。
        // 遅延は AntiBlackout の会議明けバースト (T+3.4s に全役職の AfterMeetingTasks が集中) を外すため。
        // AfterMeetingTasks は1回の会議で複数回呼ばれる経路があるので、飛行中フラグで二重予約を防ぐ。
        if (!VentBasisGranted || BasisRestoreScheduled) return;

        BasisRestoreScheduled = true;

        LateTask.New(() =>
        {
            BasisRestoreScheduled = false;
            PlayerControl player = Utils.GetPlayerById(SafecrackerId);
            if (player != null && player.IsAlive() && !GameStates.IsEnded)
            {
                GrantVentBasis(player);
                player.SyncSettings();
            }
        }, 3f, "Safecracker Restore Engineer Basis");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != SafecrackerId || seer.PlayerId != target.PlayerId) return string.Empty;
        if (!hud && !seer.IsModdedClient()) return string.Empty;

        (int completed, int total) = GetProgress();

        if (meeting)
        {
            // 会議中に本人が一番知りたいのは「追放されても残れるか」だけ。
            int left = MaxExiledGuardCount.GetInt() - ExiledGuardUsed;
            if (!IsUnlocked(ExiledGuardTaskRate, completed, total) || left <= 0) return string.Empty;

            return $"<size=40%>{Utils.ColorString(Utils.GetRoleColor(CustomRoles.Safecracker), string.Format(GetString("SafecrackerExiledGuardLeft"), left))}</size>";
        }

        var abilities = new List<string>();
        if (IsUnlocked(KillGuardTaskRate, completed, total) && KillGuardUsed < MaxKillGuardCount.GetInt()) abilities.Add(GetString("SafecrackerUnlock.KillGuard"));
        if (IsUnlocked(ExiledGuardTaskRate, completed, total) && ExiledGuardUsed < MaxExiledGuardCount.GetInt()) abilities.Add(GetString("SafecrackerUnlock.ExiledGuard"));
        if (IsUnlocked(UseVentTaskRate, completed, total)) abilities.Add(GetString("SafecrackerUnlock.Vent"));
        if (IsUnlocked(UseSaboTaskRate, completed, total)) abilities.Add(GetString("SafecrackerUnlock.Sabotage"));
        if (IsUnlocked(ImpostorVisionTaskRate, completed, total)) abilities.Add(GetString("SafecrackerUnlock.Vision"));
        if (IsUnlocked(KnowImpostorsTaskRate, completed, total)) abilities.Add(GetString("SafecrackerUnlock.KnowImpostors"));

        if (abilities.Count == 0) return string.Empty;

        return $"<size=60%>{Utils.ColorString(Utils.GetRoleColor(CustomRoles.Safecracker), string.Join(" / ", abilities))}</size>";
    }
}
