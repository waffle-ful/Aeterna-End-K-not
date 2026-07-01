using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.Data;
using EndKnot.Patches;
using EndKnot.Roles;
using HarmonyLib;
using Hazel;
using InnerNet;
using UnityEngine;

namespace EndKnot;

[HarmonyPatch(typeof(ChatController), nameof(ChatController.Update))]
internal static class ChatControllerUpdatePatch
{
    public static int CurrentHistorySelection = -1;

    // 分割ペースト送信の安全パラメータ。AU 2026 anti-cheat は 1 メッセージが ~1KB(1024byte) を超えると
    // host 側で Hacking kick する (Utils.SendMessage が同理由で 700byte に clamp 済)。よって文字数ではなく
    // UTF-8 バイト数で分割し、1 チャンク = 700byte 上限にする (base64 で 700 字 / 日本語で ~230 字)。
    private const int ChatChunkByteBudget = 700;   // 1 メッセージの本文 UTF-8 上限 (~1KB 閾値に対し余裕)
    private const int MaxPasteChunks = 10;          // 最大 10 チャンク = 約 7KB。超過分は破棄 (spam/flood 防止)
    private const float PasteChunkInterval = 0.6f;  // 送信間隔 (10 個でも 5.4s = anti-cheat flood 閾値の遥か下)

    // Ctrl+V ハンドラ。巨大クリップボード (EKM マップコード等 = 数十万文字) をそのままチャット欄へ流し込むと
    // TMP のメッシュ化 + 巨大 string alloc でフリーズ / native クラッシュするため、安全バイト数 (700byte) 以内は
    // フィールドへ、超過時はバイト分割して遅延送信する。
    private static void HandleChatPaste(ChatController chat)
    {
        if (chat?.freeChatField?.textArea == null) return;

        var area = chat.freeChatField.textArea;

        string clip = (GUIUtility.systemCopyBuffer ?? string.Empty).Trim();
        if (clip.Length == 0) return;

        string combined = area.text + clip;

        // 安全バイト数以内: 従来どおりフィールドへ (1 メッセージで送っても anti-cheat に引っかからない)
        if (Utf8ByteCount(combined) <= ChatChunkByteBudget)
        {
            TextBoxPatch.SetChatFieldText(area, combined);
            return;
        }

        // 超過: UTF-8 バイト基準で分割して遅延送信。上限チャンク数を超えた分は破棄。
        PlayerControl lp = PlayerControl.LocalPlayer;
        if (lp == null) return;

        TextBoxPatch.SetChatFieldText(area, string.Empty); // フィールドはクリア (巨大文字列を TMP に残さない)

        List<string> chunks = SplitByUtf8Bytes(combined, ChatChunkByteBudget);
        bool truncated = chunks.Count > MaxPasteChunks;
        int sendChunks = Math.Min(chunks.Count, MaxPasteChunks);

        for (int i = 0; i < sendChunks; i++)
        {
            string chunk = chunks[i];
            LateTask.New(() =>
            {
                PlayerControl p = PlayerControl.LocalPlayer;
                if (p != null && !string.IsNullOrWhiteSpace(chunk)) p.RpcSendChat(chunk);
            }, i * PasteChunkInterval, "ChatPasteChunk", log: false);
        }

        string notice = truncated
            ? $"長文を {sendChunks} 個に分割して送信します（上限 {MaxPasteChunks} 個を超えた分は破棄しました）。"
            : $"長文を {sendChunks} 個に分割して順次送信します。";
        Utils.SendMessage(notice, lp.PlayerId);
    }

    // 文字列の UTF-8 バイト数 (サロゲートは 1 文字 3byte で概算=やや過大評価=安全側)。
    private static int Utf8ByteCount(string s)
    {
        int bytes = 0;
        foreach (char c in s) bytes += c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
        return bytes;
    }

    // UTF-8 バイト予算ごとに文字列を分割 (文字境界は割らない)。
    private static List<string> SplitByUtf8Bytes(string s, int budget)
    {
        List<string> chunks = [];
        System.Text.StringBuilder sb = new();
        int bytes = 0;
        foreach (char c in s)
        {
            int cb = c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
            if (bytes + cb > budget && sb.Length > 0)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
                bytes = 0;
            }

            sb.Append(c);
            bytes += cb;
        }

        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }

    private static SpriteRenderer QuickChatIcon;
    private static SpriteRenderer OpenBanMenuIcon;
    private static SpriteRenderer OpenKeyboardIcon;

    public static void Prefix()
    {
        if (AmongUsClient.Instance.AmHost && DataManager.Settings.Multiplayer.ChatMode == QuickChatModes.QuickChatOnly)
            DataManager.Settings.Multiplayer.ChatMode = QuickChatModes.FreeChatOrQuickChat;
    }

    public static void Postfix(ChatController __instance)
    {
        if (Main.DarkTheme.Value)
        {
            __instance.freeChatField.background.color = new Color32(40, 40, 40, byte.MaxValue);

            if (!TextBoxPatch.IsInvalidCommand)
                __instance.freeChatField.textArea.outputText.color = Color.white;

            __instance.quickChatField.background.color = new Color32(40, 40, 40, byte.MaxValue);
            __instance.quickChatField.text.color = Color.white;

            if (!QuickChatIcon)
                QuickChatIcon = GameObject.Find("QuickChatIcon")?.transform.GetComponent<SpriteRenderer>();
            else
                QuickChatIcon.sprite = Utils.LoadSprite("EndKnot.Resources.Images.DarkQuickChat.png", 100f);

            if (!OpenBanMenuIcon)
                OpenBanMenuIcon = GameObject.Find("OpenBanMenuIcon")?.transform.GetComponent<SpriteRenderer>();
            else
                OpenBanMenuIcon.sprite = Utils.LoadSprite("EndKnot.Resources.Images.DarkReport.png", 100f);

            if (!OpenKeyboardIcon)
                OpenKeyboardIcon = GameObject.Find("OpenKeyboardIcon")?.transform.GetComponent<SpriteRenderer>();
            else
                OpenKeyboardIcon.sprite = Utils.LoadSprite("EndKnot.Resources.Images.DarkKeyboard.png", 100f);
        }
        else __instance.freeChatField.textArea.outputText.color = Color.black;

        if (!__instance.freeChatField.textArea.hasFocus) return;

        __instance.freeChatField.textArea.characterLimit = 1000;

        if (Input.GetKeyDown(KeyCode.Tab)) TextBoxPatch.OnTabPress(__instance);

        if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.C))
            ClipboardHelper.PutClipboardString(TextBoxPatch.SafeChatText(__instance.freeChatField.textArea));

        if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.V))
            HandleChatPaste(__instance);

        if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.X))
        {
            ClipboardHelper.PutClipboardString(TextBoxPatch.SafeChatText(__instance.freeChatField.textArea));
            TextBoxPatch.SetChatFieldText(__instance.freeChatField.textArea, "");
        }

        if (Input.GetKeyDown(KeyCode.UpArrow) && ChatCommands.ChatHistory.Count > 0)
        {
            CurrentHistorySelection = Mathf.Clamp(--CurrentHistorySelection, 0, ChatCommands.ChatHistory.Count - 1);
            TextBoxPatch.SetChatFieldText(__instance.freeChatField.textArea, ChatCommands.ChatHistory[CurrentHistorySelection]);
        }

        if (Input.GetKeyDown(KeyCode.DownArrow) && ChatCommands.ChatHistory.Count > 0)
        {
            CurrentHistorySelection++;
            TextBoxPatch.SetChatFieldText(__instance.freeChatField.textArea, CurrentHistorySelection < ChatCommands.ChatHistory.Count ? ChatCommands.ChatHistory[CurrentHistorySelection] : string.Empty);
        }
    }
}

[HarmonyPatch(typeof(UrlFinder), nameof(UrlFinder.TryFindUrl))]
internal static class UrlFinderPatch
{
    public static bool Prefix(ref bool __result)
    {
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(ChatController), nameof(ChatController.ForceClosed))]
internal static class ChatControllerForceClosedPatch
{
    public static bool Prefix() => !Utils.TempReviveHostRunning || GameStates.IsEnded || !GameStates.InGame;
}

[HarmonyPatch(typeof(ChatController), nameof(ChatController.SetVisible))]
internal static class ChatControllerSetVisiblePatch
{
    public static bool Prefix([HarmonyArgument(0)] bool visible) => visible || !Utils.TempReviveHostRunning || GameStates.IsEnded || !GameStates.InGame;
}

public static class ChatManager
{
    private const int MaxHistorySize = 20;
    private static readonly List<string> ChatHistory = [];

    public static void ResetHistory()
    {
        ChatHistory.Clear();
    }

    private static bool CheckCommand(ref string msg, string command, bool exact = true)
    {
        Utils.CheckServerCommand(ref msg, out _);
        string[] comList = command.Split('|');

        foreach (string str in comList)
        {
            if (exact)
            {
                if (msg == "/" + str) return true;
            }
            else
            {
                if (msg.StartsWith("/" + str))
                {
                    msg = msg.Replace("/" + str, string.Empty);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool CheckName(ref string msg, string command, bool exact = true)
    {
        string[] comList = command.Split('|');

        foreach (string com in comList)
        {
            if (exact)
            {
                if (msg.Contains(com)) return true;
            }
            else
            {
                int index = msg.IndexOf(com, StringComparison.Ordinal);

                if (index != -1)
                {
                    msg = msg.Remove(index, com.Length);
                    return true;
                }
            }
        }

        return false;
    }

    public static void SendMessage(PlayerControl player, string message)
    {
        string playername = player.GetNameWithRole();
        string originalMessage = message.Trim();
        message = message.ToLower().Trim();

        if (!AmongUsClient.Instance.AmHost || !player.IsAlive() || Silencer.ForSilencer.Contains(player.PlayerId)) return;

        int operate = message switch
        {
            { } str when CheckCommand(ref str, "id|guesslist|gl编号|玩家编号|玩家id|id列表|玩家列表|列表|所有id|全部id|shoot|guess|bet|st|gs|bt|猜|赌|sp|jj|tl|trial|审判|判|审|xp|效颦|效|颦|sw|换票|换|swap", false) || CheckName(ref playername, "系统消息", false) => 1,
            { } str when CheckCommand(ref str, "up|ask|target|vote|chat|check|decree|assume|note|whisper|w|summon|fabricate|select|retribute|imitate|choose|forge|daybreak|jailtalk|jt", false) => 2,
            { } str when CheckCommand(ref str, "r|role|m|myrole|n|now") => 4,
            _ => 3
        };

        switch (operate)
        {
            case 1: // Guessing Command & Such
            {
                Logger.Info("Special Command", "ChatManager");
                if (player.AmOwner) break;

                LateTask.New(() =>
                {
                    if (!ChatCommands.LastSentCommand.ContainsKey(player.PlayerId))
                    {
                        GuessManager.GuesserMsg(player, message);
                        Logger.Info("Delayed Guess", "ChatManager");
                    }
                    else
                        Logger.Info("Delayed Guess was not necessary", "ChatManager");
                }, 0.3f, "Trying Delayed Guess");

                break;
            }
            case 2: // /up and role ability commands
            case 4: // /r, /n, /m
            {
                Logger.Info($"Command: {message}", "ChatManager");
                break;
            }
            case 3: // In Lobby & Evertything Else
            {
                AddChatHistory(player, originalMessage);
                
                if (AmongUsClient.Instance.AmHost)
                    TemplateManager.SendTemplateForMessage(originalMessage, player.PlayerId);
                    
                if (GameStates.IsMeeting && player.Is(CustomRoles.Talkative))
                    Talkative.OnMessageSend(player);
                
                break;
            }
        }

        if (Options.CurrentGameMode is CustomGameMode.FFA or CustomGameMode.SoloPVP or CustomGameMode.NaturalDisasters or CustomGameMode.Mingle or CustomGameMode.HideAndSeek && GameStates.InGame && !message.StartsWith('/'))
            Main.EnumerateAlivePlayerControls().NotifyPlayers(string.Format(Utils.ColorString(Main.GameModeColors.GetValueOrDefault(Options.CurrentGameMode, new(1,1,1)), Translator.GetString("FFAChatMessageNotify")), player.PlayerId.ColoredPlayerName(), message));
    }

    public static void AddChatHistory(PlayerControl player, string message)
    {
        var chatEntry = $"{player.PlayerId}: {message}";
        ChatHistory.Add(chatEntry);
        if (ChatHistory.Count > MaxHistorySize) ChatHistory.RemoveAt(0);
    }

    private static readonly StringBuilder TitleText = new();
    public static void SendPreviousMessagesToAll()
    {
        if (!AmongUsClient.Instance.AmHost || !HudManager.InstanceExists) return;

        Logger.Info(" Sending Previous Messages To Everyone", "ChatManager");

        var aapc = Main.CachedAlivePlayerControls();
        if (aapc.Count == 0) return;

        if (GameStates.CurrentServerType == GameStates.ServerType.Vanilla)
        {
            ClearChat();

            TitleText.Clear();
            ChatHistory.ForEach(x =>
            {
                string[] split = x.Split(':');
                byte id = byte.Parse(split[0].Trim());
                string msg = string.Join(':', split[1..]).Trim();
                TitleText.Append(id.ColoredPlayerName())
                    .Append(':')
                    .Append(' ')
                    .AppendLine(msg);
            });
            LateTask.New(() => Utils.SendMessage("\n", title: TitleText.ToString().Trim()), 0.2f);
            
            return;
        }

        string[] filtered = ChatHistory.Where(a => Utils.GetPlayerById(Convert.ToByte(a.Split(':')[0].Trim())).IsAlive()).ToArray();
        ChatController chat = HudManager.Instance.Chat;
        var writer = CustomRpcSender.Create("SendPreviousMessagesToAll", SendOption.Reliable);
        var hasValue = false;

        if (filtered.Length < 20) ClearChat(aapc);

        foreach (string str in filtered)
        {
            string[] entryParts = str.Split(':');
            string senderId = entryParts[0].Trim();
            string senderMessage = entryParts[1].Trim();
            for (var j = 2; j < entryParts.Length; j++) senderMessage += ':' + entryParts[j].Trim();

            PlayerControl senderPlayer = Utils.GetPlayerById(Convert.ToByte(senderId));
            if (!senderPlayer) continue;

            chat.AddChat(senderPlayer, senderMessage);
            SendRPC(writer, senderPlayer, senderMessage);
            hasValue = true;

            if (writer.stream.Length > 500)
            {
                writer.SendMessage();
                writer = CustomRpcSender.Create("SendPreviousMessagesToAll", SendOption.Reliable);
                hasValue = false;
            }
        }

        hasValue |= ChatUpdatePatch.SendLastMessages(ref writer);
        writer.SendMessage(!hasValue);
    }

    private static void SendRPC(CustomRpcSender writer, PlayerControl senderPlayer, string senderMessage, int targetClientId = -1)
    {
        if (GameStates.IsLobby && senderPlayer.AmOwner)
            senderMessage = senderMessage.Insert(0, new('\n', PlayerControl.LocalPlayer.name.Count(x => x == '\n')));

        writer.AutoStartRpc(senderPlayer.NetId, RpcCalls.SendChat, targetClientId)
            .Write(senderMessage)
            .EndRpc();
    }

    // Base from https://github.com/Rabek009/MoreGamemodes/blob/master/Modules/Utils.cs
    public static void ClearChat(params IReadOnlyList<PlayerControl> targets)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        // 公式鯖でも他プレイヤー名義の発言が許可されたため、vanilla 特例 (host 名義固定) を撤去
        PlayerControl player = Main.EnumerateAlivePlayerControls().MinBy(x => x.PlayerId) ?? Main.EnumeratePlayerControls().MinBy(x => x.PlayerId) ?? PlayerControl.LocalPlayer;
        if (!player) return;
        if (targets.Count == 0 || targets.Count >= Main.AllAlivePlayerControlsCount) SendEmptyMessage(null);
        else targets.Do(SendEmptyMessage);
        return;

        void SendEmptyMessage(PlayerControl receiver)
        {
            bool toEveryone = !receiver;
            bool toLocalPlayer = !toEveryone && receiver.AmOwner;
            if (HudManager.InstanceExists && (toLocalPlayer || toEveryone)) HudManager.Instance.Chat.AddChat(player, "<size=32767>.");
            if (toLocalPlayer) return;

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(player.NetId, (byte)RpcCalls.SendChat, SendOption.Reliable, toEveryone ? -1 : receiver.OwnerId);
            writer.Write("<size=32767>.");
            writer.Write(true);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
    }
}