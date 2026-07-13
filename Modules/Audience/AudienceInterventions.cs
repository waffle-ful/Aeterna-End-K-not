using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Gamemodes;
using UnityEngine;

namespace EndKnot.Modules.Audience;

// Tier 1 干渉の実行部。すべて既存 RPC / サボタージュ経路のみ使用しホストローカルで完結する。
public static class AudienceInterventions
{
    // playerId -> 期限切れ TimeStamp。ApplyGameOptions の per-player 注入フックから毎回参照する。
    private static readonly Dictionary<byte, long> CursedVisionUntil = [];
    private static readonly Dictionary<byte, long> BlessedVisionUntil = [];

    private const float CursedVision = 0.5f;
    private const float BlessedVision = 2f;
    private const float BlessedSpeedIncrease = 0.2f;
    private const float CurseKillCooldownPenalty = 5f;

    // DoBless の速度復元 LateTask がゲームを跨がないようにする世代トークン。
    private static int GameGeneration;

    // ---- !大地震 の分散 TP 状態 ----
    // 全員一斉 TP は SnapTo バースト+cap 枯渇の二重リスクなので、キューに積んで
    // OnTick から「interval + per-tick 人数上限 + 発動予算」の3点セット (SuperCannonShot.PullTick と同型) で分散実行する。
    private static readonly Queue<byte> PendingQuakeShakes = [];
    private static float NextQuakeShakeTime;

    private const int QuakeMaxTargets = 12; // 1発動の TP 総数上限
    private const int QuakeShakesPerTick = 4; // 1 tick の TP 人数上限
    private const float QuakeShakeInterval = 0.3f;
    private const float QuakeOffsetMin = 0.5f;
    private const float QuakeOffsetMax = 0.8f;
    private const int QuakeSnapToBudgetFloor = 60; // ラウンド消費がここを超えていたら TP 部分ごと諦める (他役職の分を残す)
    private const float QuakeCameraShakeDuration = 3.5f; // TP ドレイン (最大 12人×0.3s≈3.6s) とほぼ同じ長さ
    private const float QuakeCameraShakeSeverity = 0.2f;

    // ---- !偽死体 の状態 (通報インターセプト用) ----
    private static readonly HashSet<byte> FakeBodyIds = [];

    public static void ResetForNewGame()
    {
        CursedVisionUntil.Clear();
        BlessedVisionUntil.Clear();
        PendingQuakeShakes.Clear();
        FakeBodyIds.Clear();
        GameGeneration++;
    }

    // Modules/GameOptionsSender/PlayerGameOptionsSender.cs の per-player 注入ブロックから呼ばれる。
    public static void OnAnyoneApplyGameOptions(IGameOptions opt, byte playerId)
    {
        long now = Utils.TimeStamp;

        if (CursedVisionUntil.TryGetValue(playerId, out long curseExpire))
        {
            if (now < curseExpire)
            {
                opt.SetVision(false);
                opt.SetFloat(FloatOptionNames.CrewLightMod, CursedVision);
                opt.SetFloat(FloatOptionNames.ImpostorLightMod, CursedVision);
            }
            else
                CursedVisionUntil.Remove(playerId);
        }

        if (BlessedVisionUntil.TryGetValue(playerId, out long blessExpire))
        {
            if (now < blessExpire)
            {
                opt.SetVision(false);
                opt.SetFloat(FloatOptionNames.CrewLightMod, BlessedVision);
                opt.SetFloat(FloatOptionNames.ImpostorLightMod, BlessedVision);
            }
            else
                BlessedVisionUntil.Remove(playerId);
        }
    }

    public static bool DoBlackout()
    {
        if (!TrySabotage(SystemTypes.Electrical)) return false;
        return true;
    }

    public static bool DoReactor()
    {
        MapNames map = Main.CurrentMap;
        SystemTypes sabo = map switch
        {
            MapNames.Polus => SystemTypes.Laboratory,
            MapNames.Airship => SystemTypes.HeliSabotage,
            _ => SystemTypes.Reactor
        };

        return TrySabotage(sabo);
    }

    public static bool DoComms()
    {
        return TrySabotage(SystemTypes.Comms);
    }

    private static bool TrySabotage(SystemTypes sabo)
    {
        if (!ShipStatus.Instance) return false;
        if (Utils.IsActive(sabo)) return false;

        ShipStatus.Instance.RpcUpdateSystem(sabo, 128);
        return true;
    }

    public static bool DoDoors()
    {
        if (!ShipStatus.Instance) return false;
        if (!ShipStatus.Instance.Systems.ContainsKey(SystemTypes.Doors)) return false;

        List<OpenableDoor> doors = ShipStatus.Instance.AllDoors.ToList();
        if (doors.Count == 0) return false;

        SystemTypes room = doors.RandomElement().Room;
        List<OpenableDoor> roomDoors = doors.Where(d => d.Room == room).ToList();
        if (roomDoors.Count == 0) roomDoors = doors;

        foreach (OpenableDoor door in roomDoors)
            ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Doors, (byte)door.Id);

        return true;
    }

    public static bool DoCurse(byte targetId)
    {
        PlayerControl target = Utils.GetPlayerById(targetId);
        if (!target || !target.IsAlive()) return false;

        float duration = AudienceOptions.CurseDuration.GetFloat();
        CursedVisionUntil[targetId] = Utils.TimeStamp + (long)duration;
        target.MarkDirtySettings();

        if (target.CanUseKillButton() && Main.AllPlayerKillCooldown.TryGetValue(targetId, out float kcd))
        {
            float newKcd = kcd + CurseKillCooldownPenalty;
            Main.AllPlayerKillCooldown[targetId] = newKcd;
            LateTask.New(() => target.SetKillCooldown(newKcd), 0.2f, log: false);
        }

        target.Notify(Translator.GetString("Audience.Notify.Cursed"));
        return true;
    }

    public static bool DoBless(byte targetId)
    {
        PlayerControl target = Utils.GetPlayerById(targetId);
        if (!target || !target.IsAlive()) return false;

        float duration = AudienceOptions.BlessDuration.GetFloat();
        BlessedVisionUntil[targetId] = Utils.TimeStamp + (long)duration;

        float baseSpeed = Main.AllPlayerSpeed.TryGetValue(targetId, out float currentSpeed)
            ? currentSpeed
            : Main.RealOptionsData.GetFloat(FloatOptionNames.PlayerSpeedMod);

        Main.AllPlayerSpeed[targetId] = baseSpeed + BlessedSpeedIncrease;
        target.MarkDirtySettings();

        int gen = GameGeneration;
        LateTask.New(() =>
        {
            if (!target || gen != GameGeneration || !GameStates.InGame) return;
            Main.AllPlayerSpeed[targetId] = Main.RealOptionsData.GetFloat(FloatOptionNames.PlayerSpeedMod);
            target.MarkDirtySettings();
        }, duration, log: false);

        target.Notify(Translator.GetString("Audience.Notify.Blessed"));
        return true;
    }

    public static bool DoMeteor()
    {
        if (Options.CurrentGameMode != CustomGameMode.NaturalDisasters && !Options.IntegrateNaturalDisasters.GetBool()) return false;
        if (!ShipStatus.Instance) return false;

        List<PlayerControl> aapc = Main.AllAlivePlayerControlsToList;
        if (aapc.Count == 0) return false;

        Vector2 position = aapc.RandomElement().Pos();
        NaturalDisasters.FixedUpdatePatch.AddPreparingDisaster(position, "Meteor", null);
        return true;
    }

    // ---- !大地震: 全ドア閉鎖 + 停電 + 全員小距離ランダム TP (分散実行) ----

    public static bool DoEarthquake()
    {
        if (!ShipStatus.Instance) return false;

        DoorsReset.CloseAllDoors();

        // 停電は既に発生中なら黙ってスキップ (ドア+揺れだけでも地震としては成立する)。
        if (!Utils.IsActive(SystemTypes.Electrical)) TrySabotage(SystemTypes.Electrical);

        // ホスト画面ローカルのカメラシェイク (バニラ FollowerCamera.ShakeScreen、送信ゼロ)。
        // 位置ジャンプ (0.5-0.8u × 1回) は体感的にほぼ見えないため、ホストの「揺れ」はこちらが主演出。
        // OverrideScreenShakeEnabled はアクセシビリティ設定で画面揺れ無効でも演出を通すバニラ側フラグ。
        try
        {
            if (HudManager.InstanceExists && HudManager.Instance.PlayerCam)
            {
                HudManager.Instance.PlayerCam.OverrideScreenShakeEnabled = true;
                HudManager.Instance.PlayerCam.ShakeScreen(QuakeCameraShakeDuration, QuakeCameraShakeSeverity);
            }
        }
        catch (Exception ex) { Logger.Warn($"Earthquake camera shake failed: {ex.Message}", "Audience"); }

        PendingQuakeShakes.Clear();

        // 公式鯖で SnapTo 予算が既に減っているときは TP 部分を丸ごと諦める。
        // cap 枯渇は「そのラウンドの他役職 TP まで無音失敗」という最悪の副作用があるため、演出より予算を優先する。
        if (GameStates.CurrentServerType == GameStates.ServerType.Vanilla && Utils.NumSnapToCallsThisRound >= QuakeSnapToBudgetFloor)
        {
            Logger.Warn($"Earthquake shake skipped: SnapTo budget low ({Utils.NumSnapToCallsThisRound})", "Audience");
            return true;
        }

        foreach (PlayerControl pc in Main.AllAlivePlayerControlsToList.Shuffle().Take(QuakeMaxTargets))
            PendingQuakeShakes.Enqueue(pc.PlayerId);

        NextQuakeShakeTime = 0f;
        return true;
    }

    // AudienceManager.Tick から毎 tick 呼ばれる。地震の TP をフレーム分散でドレインする。
    public static void OnTick(bool enabled)
    {
        if (PendingQuakeShakes.Count == 0) return;

        // 会議割込・ゲーム終了・機能無効化で即打ち切り (位置が古くなったキューを持ち越さない)。
        if (!enabled || !GameStates.InGame || GameStates.IsMeeting || ExileController.Instance)
        {
            PendingQuakeShakes.Clear();
            return;
        }

        if (Time.time < NextQuakeShakeTime) return;
        NextQuakeShakeTime = Time.time + QuakeShakeInterval;

        int done = 0;

        while (done < QuakeShakesPerTick && PendingQuakeShakes.Count > 0)
        {
            // 実行中も予算を再チェック (他役職が並行して TP を消費している可能性がある)。
            if (GameStates.CurrentServerType == GameStates.ServerType.Vanilla && Utils.NumSnapToCallsThisRound >= QuakeSnapToBudgetFloor + QuakeMaxTargets)
            {
                Logger.Warn($"Earthquake shake aborted mid-run: SnapTo budget low ({Utils.NumSnapToCallsThisRound})", "Audience");
                PendingQuakeShakes.Clear();
                return;
            }

            PlayerControl pc = Utils.GetPlayerById(PendingQuakeShakes.Dequeue());
            if (!pc || !pc.IsAlive()) continue;
            if (TryQuakeShake(pc)) done++;
        }
    }

    private static bool TryQuakeShake(PlayerControl pc)
    {
        Vector2 pos = pc.Pos();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            float angle = IRandom.Instance.Next(0, 360) * Mathf.Deg2Rad;
            float dist = QuakeOffsetMin + (IRandom.Instance.Next(0, 101) / 100f * (QuakeOffsetMax - QuakeOffsetMin));
            Vector2 candidate = pos + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dist);

            // 壁越え/壁めり込み防止 (Car/CustomNetObject と同じ判定)。塞がっていたら方向を変えてリトライ。
            if (PhysicsHelpers.AnythingBetween(pos, candidate, Constants.ShipOnlyMask, false)) continue;

            // 移動量 <1.5 ユニットは Utils.TP 側で自動的に SendOption.None (unreliable) へ降格される。
            // inVent/ladder 等の TP 不能状態も Utils.TP 側の既存チェックに任せる。
            return Utils.TP(pc.NetTransform, candidate, log: false);
        }

        return false;
    }

    // ---- !天の声: 視聴者メッセージを「天の声」名義で全員にチャット送信 ----

    private const int VoiceMaxLength = 60;
    private static string[] NgWords;
    private static readonly string NgWordsPath = $"{Main.DataPath}/EndKnot_DATA/AudienceNgWords.txt";

    // ホストが編集できる NG ワードファイル。無ければデフォルトで生成する (1行1語、# はコメント)。
    private static readonly string[] DefaultNgWords = ["死ね", "殺すぞ", "きもい", "キモい", "うざい", "カス", "クズ", "kys", "kill yourself", "fuck", "shit"];

    private static string[] LoadNgWords()
    {
        if (NgWords != null) return NgWords;

        try
        {
            if (!Directory.Exists($"{Main.DataPath}/EndKnot_DATA")) Directory.CreateDirectory($"{Main.DataPath}/EndKnot_DATA");

            if (!File.Exists(NgWordsPath))
                File.WriteAllLines(NgWordsPath, new[] { "# !天の声 で弾く NG ワード (1行1語、# 行はコメント)。部分一致・大文字小文字無視。" }.Concat(DefaultNgWords));

            NgWords = File.ReadAllLines(NgWordsPath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToArray();
        }
        catch (Exception ex)
        {
            Logger.Warn($"AudienceNgWords load failed, using defaults: {ex.Message}", "Audience");
            NgWords = DefaultNgWords;
        }

        return NgWords;
    }

    public static bool DoVoice(string author, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        // タグ注入と改行を無効化してから長さを詰める (AudienceCutscene.Sanitize と同一手法)。
        message = message.Replace("<", "＜").Replace("\r", " ").Replace("\n", " ").Trim();
        if (message.Length > VoiceMaxLength) message = message[..VoiceMaxLength];
        if (message.Length == 0) return false;

        if (LoadNgWords().Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            Logger.Info($"Audience voice rejected by NG word filter (author={author})", "Audience");
            return false;
        }

        // バニラクライアントはタスク中チャットを開けないため、頭上 Notify (全員) を主表示にし、
        // チャット送信は記録用 (次の会議でログとして読める) として併用する。
        string title = Utils.ColorString(new Color32(255, 215, 0, 255), Translator.GetString("Audience.VoiceTitle"));
        string notifyText = Utils.ColorString(new Color32(255, 215, 0, 255), $"{Translator.GetString("Audience.VoiceTitle")}: {message}");

        foreach (PlayerControl pc in Main.AllAlivePlayerControlsToList)
            pc.Notify(notifyText, 8f, log: false);

        Utils.SendMessage("\n" + message, byte.MaxValue, title);
        return true;
    }

    // ---- !偽死体: 生存プレイヤーの偽死体を別の生存者の近くにスポーン ----

    public static bool DoFakeBody()
    {
        if (!ShipStatus.Instance) return false;

        List<PlayerControl> aapc = Main.AllAlivePlayerControlsToList;
        if (aapc.Count == 0) return false;

        // 死体の見た目 = ランダムな生存者、置き場所 = 別のランダム生存者の現在地そば。
        // 「歩いていたら生きているはずの人の死体を見つける」状況を作る。
        // 未通報の偽死体が既にあるプレイヤーは除外する — FakeBodyIds は「1人1体」前提の HashSet なので、
        // 同一人物に2体目を許すと1回の通報でフラグが消え、2体目が本物の死体として会議を開いてしまう。
        List<PlayerControl> candidates = aapc.Where(p => !FakeBodyIds.Contains(p.PlayerId)).ToList();
        if (candidates.Count == 0) return false;

        PlayerControl identity = candidates.RandomElement();
        // 生存者が identity 1人だけ (ソロテスト等) のときは自分のそばに自分の偽死体を出す。
        List<PlayerControl> anchors = aapc.Where(p => p.PlayerId != identity.PlayerId).ToList();
        PlayerControl anchor = anchors.Count > 0 ? anchors.RandomElement() : identity;

        Vector2 basePos = anchor.Pos();
        Vector2 pos = basePos;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            float angle = IRandom.Instance.Next(0, 360) * Mathf.Deg2Rad;
            float dist = 0.6f + (IRandom.Instance.Next(0, 101) / 100f * 0.8f);
            Vector2 candidate = basePos + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dist);
            if (PhysicsHelpers.AnythingBetween(basePos, candidate, Constants.ShipOnlyMask, false)) continue;

            pos = candidate;
            break;
        }

        FakeBodyIds.Add(identity.PlayerId);
        Utils.RpcCreateDeadBody(pos, (byte)identity.CurrentOutfit.ColorId, identity);
        Logger.Info($"Audience fake body spawned: identity={identity.GetRealName()} near {anchor.GetRealName()}", "Audience");
        return true;
    }

    // PlayerControlPatch の CheckReportDeadBody 列 (Trapster の隣) から呼ばれる。false で通報キャンセル。
    public static bool OnAnyoneCheckReportDeadBody(PlayerControl reporter, NetworkedPlayerInfo target)
    {
        if (target == null || !FakeBodyIds.Contains(target.PlayerId)) return true;

        PlayerControl owner = Utils.GetPlayerById(target.PlayerId);

        // 本人が実際に死んでいたら本物の死体 — いたずらフラグを外して通常の通報に任せる (実死体の誤ブロック防止)。
        if (!owner || !owner.IsAlive())
        {
            FakeBodyIds.Remove(target.PlayerId);
            return true;
        }

        FakeBodyIds.Remove(target.PlayerId);
        reporter.Notify(Translator.GetString("Audience.Notify.FakeBody"));

        string ownerName = Main.AllPlayerNames.TryGetValue(target.PlayerId, out string raw) ? raw : target.PlayerName;
        string title = Utils.ColorString(new Color32(255, 215, 0, 255), Translator.GetString("Audience.VoiceTitle"));
        Utils.SendMessage(string.Format(Translator.GetString("Audience.Chat.FakeBodyReveal"), ownerName), byte.MaxValue, title);

        Logger.Info($"Audience fake body reported by {reporter.GetRealName()} - meeting canceled", "Audience");
        return false;
    }
}
