using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using EndKnot.Modules.YouTubeChat;

namespace EndKnot.Modules.Audience;

// 視聴者干渉システム「Audience」の中枢。
// YouTubeChatManager.OnMessage は worker thread から発火するので queue に積むだけにし、
// 実処理は Tick (main thread, FixedUpdateCaller 経由) で drain する。
public static class AudienceManager
{
    private readonly record struct QueuedChatMessage(string Author, string Text);

    private enum InterventionKind
    {
        Blackout,
        Reactor,
        Comms,
        Doors,
        Curse,
        Bless,
        Meteor
    }

    private readonly record struct QueuedIntervention(InterventionKind Kind, string Author, byte TargetId);

    private static readonly ConcurrentQueue<QueuedChatMessage> IncomingMessages = new();
    private static readonly Queue<QueuedIntervention> InterventionQueue = new();
    private static readonly Dictionary<byte, long> TargetCooldownUntil = [];

    private static bool Subscribed;
    private static long LastInterventionTS;
    private static bool WasInGame;

    public static int QueuedInterventionCount => InterventionQueue.Count;

    // ゲーム跨ぎで残ると前ゲームの PlayerId が次ゲームの別人に誤爆するため、新ゲーム開始時に必ず呼ぶ。
    public static void ResetForNewGame()
    {
        // ロビー中に積まれたコマンドはここで破棄される。無言で消えると「機能していない」に見えるため件数を残す。
        if (InterventionQueue.Count > 0) Logger.Info($"Audience queue discarded on game start: {InterventionQueue.Count} pending", "Audience");

        InterventionQueue.Clear();
        TargetCooldownUntil.Clear();
    }

    // 英語/日本語のエイリアスをまとめて 1 コマンドにマップする。
    private static readonly Dictionary<string, InterventionKind> CommandAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["停電"] = InterventionKind.Blackout,
        ["blackout"] = InterventionKind.Blackout,
        ["リアクター"] = InterventionKind.Reactor,
        ["reactor"] = InterventionKind.Reactor,
        ["通信"] = InterventionKind.Comms,
        ["comms"] = InterventionKind.Comms,
        ["ドア"] = InterventionKind.Doors,
        ["doors"] = InterventionKind.Doors,
        ["呪い"] = InterventionKind.Curse,
        ["curse"] = InterventionKind.Curse,
        ["祝福"] = InterventionKind.Bless,
        ["bless"] = InterventionKind.Bless,
        ["隕石"] = InterventionKind.Meteor,
        ["meteor"] = InterventionKind.Meteor
    };

    public static void EnsureSubscribed()
    {
        if (Subscribed) return;
        Subscribed = true;
        YouTubeChatManager.OnMessage += OnChatMessage;
    }

    // worker thread から呼ばれる。ここでは queue に積むだけ。
    private static void OnChatMessage(string author, string text)
    {
        IncomingMessages.Enqueue(new QueuedChatMessage(author, text));
    }

    public static void Tick()
    {
        EnsureSubscribed();

        bool enabled = AudienceOptions.Enabled != null && AudienceOptions.Enabled.GetBool();

        // queue が溜まり続けないよう、無効時も drain だけはしておく(コマンドは処理しない)。
        while (IncomingMessages.TryDequeue(out QueuedChatMessage msg))
        {
            if (!enabled) continue;
            try { HandleIncomingMessage(msg.Author, msg.Text); }
            catch (Exception ex) { Logger.Exception(ex, "AudienceManager.HandleIncomingMessage"); }
        }

        if (!enabled)
        {
            InterventionQueue.Clear();
            AudienceEconomy.OnTick();
            return;
        }

        // ゲーム終了(InGame: true -> false)を検知したら強制保存。
        bool inGameNow = GameStates.InGame;
        if (WasInGame && !inGameNow) AudienceEconomy.SaveNow();
        WasInGame = inGameNow;

        ProcessInterventionQueue();
        AudienceEconomy.OnTick();
    }

    private static void HandleIncomingMessage(string author, string text)
    {
        if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(text)) return;
        if (AudienceEconomy.IsBanned(author)) return;

        AudienceEconomy.TryGrantForMessage(author);

        // 日本語IMEの視聴者は「！停電」「!呪い　名前」のように全角の ! / スペースで打ちがちなので半角に正規化する。
        text = text.Trim().Replace('！', '!').Replace('　', ' ');
        if (!text.StartsWith('!')) return;

        string[] parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;

        if (!CommandAliases.TryGetValue(parts[0], out InterventionKind kind)) return;

        switch (kind)
        {
            case InterventionKind.Curse:
            case InterventionKind.Bless:
            {
                if (parts.Length < 2) return;
                string nameQuery = string.Join(' ', parts, 1, parts.Length - 1);
                PlayerControl target = FindPlayerByNamePart(nameQuery);

                if (!target)
                {
                    Logger.Info($"Audience command ignored: target not found (author={author}, cmd={kind}, query={nameQuery})", "Audience");
                    return;
                }

                InterventionQueue.Enqueue(new QueuedIntervention(kind, author, target.PlayerId));
                break;
            }
            default:
                InterventionQueue.Enqueue(new QueuedIntervention(kind, author, byte.MaxValue));
                break;
        }

        Logger.Info($"Audience intervention queued: {kind} (author={author}, queue={InterventionQueue.Count})", "Audience");
    }

    private static PlayerControl FindPlayerByNamePart(string namePart)
    {
        namePart = namePart.Trim();
        if (namePart.Length == 0) return null;

        return Main.AllPlayerNames
            .Where(kvp => kvp.Value.Contains(namePart, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => Utils.GetPlayerById(kvp.Key))
            .FirstOrDefault(pc => pc);
    }

    private static void ProcessInterventionQueue()
    {
        // 会議中はキューを保持したまま何もしない。ゲーム進行中のみ 1 件ずつ実行。
        if (!GameStates.InGame || GameStates.IsMeeting) return;
        if (InterventionQueue.Count == 0) return;

        long now = Utils.TimeStamp;
        float interval = AudienceOptions.GlobalInterventionInterval.GetFloat();
        if (now - LastInterventionTS < (long)interval) return;

        QueuedIntervention item = InterventionQueue.Dequeue();
        if (TryExecute(item)) LastInterventionTS = now;
    }

    private static bool TryExecute(QueuedIntervention item)
    {
        (OptionItem enabledOpt, OptionItem priceOpt) = item.Kind switch
        {
            InterventionKind.Blackout => (AudienceOptions.BlackoutEnabled, AudienceOptions.BlackoutPrice),
            InterventionKind.Reactor => (AudienceOptions.ReactorEnabled, AudienceOptions.ReactorPrice),
            InterventionKind.Comms => (AudienceOptions.CommsEnabled, AudienceOptions.CommsPrice),
            InterventionKind.Doors => (AudienceOptions.DoorsEnabled, AudienceOptions.DoorsPrice),
            InterventionKind.Curse => (AudienceOptions.CurseEnabled, AudienceOptions.CursePrice),
            InterventionKind.Bless => (AudienceOptions.BlessEnabled, AudienceOptions.BlessPrice),
            InterventionKind.Meteor => (AudienceOptions.MeteorEnabled, AudienceOptions.MeteorPrice),
            _ => (null, null)
        };

        if (enabledOpt == null || !enabledOpt.GetBool())
        {
            Logger.Info($"Audience intervention rejected: disabled (author={item.Author}, cmd={item.Kind})", "Audience");
            return false;
        }

        bool isTargeted = item.Kind is InterventionKind.Curse or InterventionKind.Bless;
        if (isTargeted)
        {
            long now = Utils.TimeStamp;
            if (TargetCooldownUntil.TryGetValue(item.TargetId, out long until) && now < until)
            {
                Logger.Info($"Audience intervention rejected: target on cooldown (author={item.Author}, cmd={item.Kind})", "Audience");
                return false;
            }
        }

        int price = priceOpt.GetInt();
        if (!AudienceEconomy.TrySpend(item.Author, price))
        {
            Logger.Info($"Audience intervention rejected: insufficient points (author={item.Author}, cmd={item.Kind})", "Audience");
            return false;
        }

        bool success = item.Kind switch
        {
            InterventionKind.Blackout => AudienceInterventions.DoBlackout(),
            InterventionKind.Reactor => AudienceInterventions.DoReactor(),
            InterventionKind.Comms => AudienceInterventions.DoComms(),
            InterventionKind.Doors => AudienceInterventions.DoDoors(),
            InterventionKind.Curse => AudienceInterventions.DoCurse(item.TargetId),
            InterventionKind.Bless => AudienceInterventions.DoBless(item.TargetId),
            InterventionKind.Meteor => AudienceInterventions.DoMeteor(),
            _ => false
        };

        if (!success)
        {
            // 実行条件を満たせなかった場合はポイントを返す(隕石の gamemode 条件など)。
            AudienceEconomy.AddPoints(item.Author, price);
            Logger.Info($"Audience intervention rejected: execution failed (author={item.Author}, cmd={item.Kind})", "Audience");
            return false;
        }

        if (isTargeted) TargetCooldownUntil[item.TargetId] = Utils.TimeStamp + (long)AudienceOptions.TargetCooldown.GetFloat();

        Logger.Info($"Audience intervention executed: {item.Kind} (author={item.Author}, price={price})", "Audience");
        return true;
    }
}
