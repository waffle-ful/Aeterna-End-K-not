using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AmongUs.GameOptions;
using Assets.CoreScripts;
using EndKnot.Gamemodes;
using EndKnot.Modules;
using EndKnot.Modules.Ekm;
using EndKnot.Modules.YouTubeChat;
using EndKnot.Patches;
using EndKnot.Roles;
using HarmonyLib;
using Hazel;
using InnerNet;
using UnityEngine;
using static EndKnot.Translator;

// ReSharper disable InconsistentNaming

namespace EndKnot;

internal class Command(string key, string arguments, Command.UsageLevels usageLevel, Command.UsageTimes usageTime, Action<PlayerControl, string, string[]> action, bool isCanceled, bool alwaysHidden, string[] argsDescriptions = null)
{
    public enum UsageLevels
    {
        Everyone,
        Modded,
        Host,
        HostOrModerator,
        HostOrAdmin,
        HostOrDev
    }

    public enum UsageTimes
    {
        Always,
        InLobby,
        InGame,
        InMeeting,
        AfterDeath,
        AfterDeathOrLobby
    }

    public static List<Command> AllCommands = [];

    public string[] CommandForms = GetString($"CommandForms.{key}").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    public string Key => key;
    public string Arguments => arguments;
    public string Description => GetString($"CommandDescription.{key}");
    public string[] ArgsDescriptions => argsDescriptions ?? [];
    public UsageLevels UsageLevel => usageLevel;
    public UsageTimes UsageTime => usageTime;
    public Action<PlayerControl, string, string[]> Action => action;
    public bool IsCanceled => isCanceled;
    public bool AlwaysHidden => alwaysHidden;

    public bool IsThisCommand(string text)
    {
        if (!text.StartsWith('/')) return false;

        text = text.ToLower().Trim().TrimStart('/');
        return CommandForms.Any(text.Split(' ')[0].Equals);
    }

    public bool CanUseCommand(PlayerControl pc, bool checkTime = true, bool sendErrorMessage = false)
    {
        if (UsageLevel == UsageLevels.Everyone && UsageTime == UsageTimes.Always && !Lovers.PrivateChat.GetBool()) return true;

        if (Lovers.PrivateChat.GetBool() && GameStates.IsInTask && pc.IsAlive()) return false;

        switch (UsageLevel)
        {
            case UsageLevels.Host when !pc.IsHost():
            case UsageLevels.Modded when !pc.IsModdedClient():
            case UsageLevels.HostOrModerator when !pc.IsHost() && (AmongUsClient.Instance.AmHost && !ChatCommands.IsPlayerModerator(pc.FriendCode)):
            case UsageLevels.HostOrAdmin when !pc.IsHost() && AmongUsClient.Instance.AmHost && !ChatCommands.IsPlayerAdmin(pc.FriendCode):
            case UsageLevels.HostOrDev when !pc.IsHost() && AmongUsClient.Instance.AmHost && !pc.FriendCode.GetDevUser().up && !pc.FriendCode.IsLocalDev():
                if (sendErrorMessage) Utils.SendMessage("\n", pc.PlayerId, GetString($"Commands.NoAccess.Level.{UsageLevel}"));
                return false;
        }

        if (!checkTime) return true;

        switch (UsageTime)
        {
            case UsageTimes.InLobby when !GameStates.IsLobby:
            case UsageTimes.InGame when !GameStates.InGame:
            case UsageTimes.InMeeting when !GameStates.IsMeeting:
            case UsageTimes.AfterDeath when pc.IsAlive():
            case UsageTimes.AfterDeathOrLobby when pc.IsAlive() && !GameStates.IsLobby:
                if (sendErrorMessage) Utils.SendMessage("\n", pc.PlayerId, GetString($"Commands.NoAccess.Time.{UsageTime}"));
                return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(ChatController), nameof(ChatController.SendChat))]
internal static class ChatCommands
{
    public static readonly List<string> ChatHistory = [];
    public static readonly Dictionary<byte, long> LastSentCommand = [];

    private static readonly Dictionary<char, int> PollVotes = [];
    private static readonly Dictionary<char, string> PollAnswers = [];
    private static readonly List<byte> PollVoted = [];
    private static float PollTimer = 45f;
    private static List<CustomGameMode> GMPollGameModes = [];
    private static List<MapNames> MPollMaps = [];

    public static readonly Dictionary<byte, (long MuteTimeStamp, int Duration, long LastMessageTimeStamp)> MutedPlayers = [];

    public static Dictionary<byte, List<CustomRoles>> DraftRoles = [];
    public static Dictionary<byte, CustomRoles> DraftResult = [];

    public static readonly HashSet<byte> Spectators = [];
    public static readonly HashSet<byte> LastSpectators = [];
    public static readonly HashSet<byte> ForcedSpectators = [];

    private static HashSet<byte> ReadyPlayers = [];
    // 停止には StartCoroutine が返す Coroutine ハンドルが要る (Main.StopCoroutine 参照)。
    private static Coroutine ReadyCheckCountdown;
    public static HashSet<byte> VotedToStart = [];

    private static string CurrentAnagram = string.Empty;

    public static bool HasMessageDuringEjectionScreen;

    private static bool WaitingToSend;

    private static long LastSetNameInLobby;

    // バレたら致命的なコマンドは /cmd を付け忘れても自動でステルス扱い
    // AlwaysHidden=true のものは元々警告対象なので暗黙的に含める
    private static readonly HashSet<string> AutoHiddenCommandKeys =
    [
        "ImpostorChat", "JackalChat", "LoversChat", "Guess"
    ];

    private static bool ShouldAutoHide(string text)
    {
        string head = text.ToLower().TrimStart('/').Split(' ')[0];
        Command match = Command.AllCommands.Find(c => c.CommandForms.Contains(head));
        return match != null && (match.AlwaysHidden || AutoHiddenCommandKeys.Contains(match.Key));
    }

    public static void LoadCommands()
    {
        Command.AllCommands =
        [
            new("LT", "", Command.UsageLevels.Everyone, Command.UsageTimes.InLobby, LTCommand, false, false),
            new("Dump", "", Command.UsageLevels.Modded, Command.UsageTimes.Always, DumpCommand, false, false),
            new("Version", "", Command.UsageLevels.Modded, Command.UsageTimes.Always, VersionCommand, false, false),
            new("ChangeSetting", "{name} {?} [?]", Command.UsageLevels.Host, Command.UsageTimes.InLobby, ChangeSettingCommand, true, false, [GetString("CommandArgs.ChangeSetting.Name"), GetString("CommandArgs.ChangeSetting.UnknownValue"), GetString("CommandArgs.ChangeSetting.UnknownValue")]),
            new("Winner", "", Command.UsageLevels.Everyone, Command.UsageTimes.InLobby, WinnerCommand, true, false),
            new("LastResult", "", Command.UsageLevels.Everyone, Command.UsageTimes.InLobby, LastResultCommand, true, false),
            new("Rename", "{name}", Command.UsageLevels.Everyone, Command.UsageTimes.InLobby, RenameCommand, true, false, [GetString("CommandArgs.Rename.Name")]),
            new("HideName", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, HideNameCommand, true, false),
            new("Level", "{level}", Command.UsageLevels.Host, Command.UsageTimes.InLobby, LevelCommand, true, false, [GetString("CommandArgs.Level.Level")]),
            new("Now", "", Command.UsageLevels.Everyone, Command.UsageTimes.Always, NowCommand, true, false),
            new("Disconnect", "{team}", Command.UsageLevels.Host, Command.UsageTimes.InGame, DisconnectCommand, true, false, [GetString("CommandArgs.Disconnect.Team")]),
            new("R", "[role]", Command.UsageLevels.Everyone, Command.UsageTimes.Always, RCommand, true, false, [GetString("CommandArgs.R.Role")]),
            new("Up", "{role}", Command.UsageLevels.Host, Command.UsageTimes.InLobby, UpCommand, true, false, [GetString("CommandArgs.Up.Role")]),
            new("SetRole", "{id} {role}", Command.UsageLevels.HostOrDev, Command.UsageTimes.InLobby, SetRoleCommand, true, false, [GetString("CommandArgs.SetRole.Id"), GetString("CommandArgs.SetRole.Role")]),
            new("Help", "", Command.UsageLevels.Everyone, Command.UsageTimes.Always, HelpCommand, true, false),
            new("KCount", "", Command.UsageLevels.Everyone, Command.UsageTimes.InGame, KCountCommand, true, false),
            new("AddMod", "{id}", Command.UsageLevels.Host, Command.UsageTimes.Always, AddModCommand, true, false, [GetString("CommandArgs.AddMod.Id")]),
            new("DeleteMod", "{id}", Command.UsageLevels.Host, Command.UsageTimes.Always, DeleteModCommand, true, false, [GetString("CommandArgs.DeleteMod.Id")]),
            new("Combo", "{mode} {role} {addon} [all]", Command.UsageLevels.Everyone, Command.UsageTimes.Always, ComboCommand, true, false, [GetString("CommandArgs.Combo.Mode"), GetString("CommandArgs.Combo.Role"), GetString("CommandArgs.Combo.Addon"), GetString("CommandArgs.Combo.All")]),
            new("Effect", "{effect}", Command.UsageLevels.Host, Command.UsageTimes.InGame, EffectCommand, true, false, [GetString("CommandArgs.Effect.Effect")]),
            new("AFKExempt", "{id}", Command.UsageLevels.HostOrAdmin, Command.UsageTimes.Always, AFKExemptCommand, true, false, [GetString("CommandArgs.AFKExempt.Id")]),
            new("MyRole", "", Command.UsageLevels.Everyone, Command.UsageTimes.InGame, MyRoleCommand, true, false),
            new("ImpostorChat", "{message}", Command.UsageLevels.Everyone, Command.UsageTimes.InGame, ImpostorChatCommand, true, false, [GetString("CommandArgs.ImpostorChat.Message")]),
            new("JackalChat", "{message}", Command.UsageLevels.Everyone, Command.UsageTimes.InGame, JackalChatCommand, true, false, [GetString("CommandArgs.JackalChat.Message")]),
            new("LoversChat", "{message}", Command.UsageLevels.Everyone, Command.UsageTimes.InGame, LoversChatCommand, true, false, [GetString("CommandArgs.LoversChat.Message")]),
            new("TPOut", "", Command.UsageLevels.Everyone, Command.UsageTimes.InLobby, TPOutCommand, true, false),
            new("TPIn", "", Command.UsageLevels.Everyone, Command.UsageTimes.InLobby, TPInCommand, true, false),
            new("BBDiag", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBDiagCommand, true, true),
            new("BBToggle", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBToggleCommand, true, true),
            new("BBSpawn", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBSpawnCommand, true, true),
            new("BBClear", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBClearCommand, true, true),
            new("BBGen", "[seed]", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBGenCommand, true, true),
            new("BBEnter", "[seed]", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBEnterCommand, true, true),
            new("BBExit", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBExitCommand, true, true),
            new("BBShadowDiag", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBShadowDiagCommand, true, true),
            new("BBVisToggle", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBVisToggleCommand, true, true),
            new("BBLightProbe", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBLightProbeCommand, true, true),
            new("BBVisDiag", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBVisDiagCommand, true, true),
            new("BBCullInfo", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBCullInfoCommand, true, true),
            new("BBShipDiag", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBShipDiagCommand, true, true),
            new("BBNoCDiag", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBNoCDiagCommand, true, true),
            new("BBPerf", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBPerfCommand, true, true),
            new("BBWallDark", "[value]", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBWallDarkCommand, true, true),
            new("BBStreamBudget", "[spawn] [destroy]", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBStreamBudgetCommand, true, true),
            new("BBShadow", "[on|off|radius <r>|dark <v> [blur]|status]", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBShadowCommand, true, true),
            new("BBZone", "[status|ratio <hall%> <gallery%>|merge <maze%> <hall%>|pillar <p%>]", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBZoneCommand, true, true),
            new("BBRange", "[<chunkR> [cullR]]", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBRangeCommand, true, true),
            new("BBZoom", "[<3-50>|reset]", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBZoomCommand, true, true),
            new("BBTestRoom", "[edge|box|both|off]", Command.UsageLevels.Host, Command.UsageTimes.InLobby, BBTestRoomCommand, true, true),
            new("Rehost", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, RehostCommand, true, true),
            new("Template", "{tag}", Command.UsageLevels.Everyone, Command.UsageTimes.Always, TemplateCommand, true, false, [GetString("CommandArgs.Template.Tag")]),
            new("MessageWait", "{duration}", Command.UsageLevels.Host, Command.UsageTimes.Always, MessageWaitCommand, true, false, [GetString("CommandArgs.MessageWait.Duration")]),
            new("Death", "[id]", Command.UsageLevels.Everyone, Command.UsageTimes.AfterDeath, DeathCommand, true, false, [GetString("CommandArgs.Death.Id")]),
            new("Say", "{message}", Command.UsageLevels.HostOrModerator, Command.UsageTimes.Always, SayCommand, true, false, [GetString("CommandArgs.Say.Message")]),
            new("Vote", "{id}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, VoteCommand, true, true, [GetString("CommandArgs.Vote.Id")]),
            new("Exo", "", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, ExoCommand, true, true),
            new("Reroll", "", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, RerollCommand, true, true),
            new("Ask", "{number1} {number2}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, AskCommand, true, true, [GetString("CommandArgs.Ask.Number1"), GetString("CommandArgs.Ask.Number2")]),
            new("Answer", "{number}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, AnswerCommand, true, false, [GetString("CommandArgs.Answer.Number")]),
            new("QA", "{letter}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, QACommand, true, false, [GetString("CommandArgs.QA.Letter")]),
            new("QS", "", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, QSCommand, true, false),
            new("Target", "{id}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, TargetCommand, true, true, [GetString("CommandArgs.Target.Id")]),
            new("Chat", "{message}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, ChatCommand, true, true, [GetString("CommandArgs.Chat.Message")]),
            new("Check", "{id} {role}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, CheckCommand, true, true, [GetString("CommandArgs.Check.Id"), GetString("CommandArgs.Check.Role")]),
            new("Ban", "{id} [reason]", Command.UsageLevels.HostOrModerator, Command.UsageTimes.Always, BanKickCommand, true, false, [GetString("CommandArgs.Ban.Id"), GetString("CommandArgs.Ban.Reason")]),
            new("Exe", "{id}", Command.UsageLevels.HostOrAdmin, Command.UsageTimes.Always, ExeCommand, true, false, [GetString("CommandArgs.Exe.Id")]),
            new("Kill", "{id}", Command.UsageLevels.Host, Command.UsageTimes.Always, KillCommand, true, false, [GetString("CommandArgs.Kill.Id")]),
            new("Colour", "{color}", Command.UsageLevels.Everyone, Command.UsageTimes.InLobby, ColorCommand, true, false, [GetString("CommandArgs.Colour.Color")]),
            new("ID", "", Command.UsageLevels.Everyone, Command.UsageTimes.Always, IDCommand, true, false),
            new("ChangeRole", "{role}", Command.UsageLevels.Host, Command.UsageTimes.InGame, ChangeRoleCommand, true, false, [GetString("CommandArgs.ChangeRole.Role")]),
            new("End", "", Command.UsageLevels.HostOrAdmin, Command.UsageTimes.InGame, EndCommand, true, false),
            new("CosID", "[id]", Command.UsageLevels.Modded, Command.UsageTimes.Always, CosIDCommand, true, false, [GetString("CommandArgs.CosID.Id")]),
            new("MTHY", "", Command.UsageLevels.Host, Command.UsageTimes.InGame, MTHYCommand, true, false),
            new("CSD", "{sound}", Command.UsageLevels.Modded, Command.UsageTimes.Always, CSDCommand, true, false, [GetString("CommandArgs.CSD.Sound")]),
            new("SD", "{sound}", Command.UsageLevels.Modded, Command.UsageTimes.Always, SDCommand, true, false, [GetString("CommandArgs.SD.Sound")]),
            new("GNO", "{number}", Command.UsageLevels.Everyone, Command.UsageTimes.AfterDeathOrLobby, GNOCommand, true, false, [GetString("CommandArgs.GNO.Number")]),
            new("Poll", "{question} {answerA} {answerB} [answerC] [answerD]", Command.UsageLevels.HostOrModerator, Command.UsageTimes.Always, PollCommand, true, false, [GetString("CommandArgs.Poll.Question"), GetString("CommandArgs.Poll.AnswerA"), GetString("CommandArgs.Poll.AnswerB"), GetString("CommandArgs.Poll.AnswerC"), GetString("CommandArgs.Poll.AnswerD")]),
            new("PV", "{vote}", Command.UsageLevels.Everyone, Command.UsageTimes.Always, PVCommand, false, false, [GetString("CommandArgs.PV.Vote")]),
            new("HM", "{id}", Command.UsageLevels.Everyone, Command.UsageTimes.AfterDeath, HMCommand, true, false, [GetString("CommandArgs.HM.Id")]),
            new("Decree", "{number}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, DecreeCommand, true, true, [GetString("CommandArgs.Decree.Number")]),
            new("AddVIP", "{id}", Command.UsageLevels.Host, Command.UsageTimes.Always, AddVIPCommand, true, false, [GetString("CommandArgs.AddVIP.Id")]),
            new("DeleteVIP", "{id}", Command.UsageLevels.Host, Command.UsageTimes.Always, DeleteVIPCommand, true, false, [GetString("CommandArgs.DeleteVIP.Id")]),
            new("Assume", "{id} {number}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, AssumeCommand, true, true, [GetString("CommandArgs.Assume.Id"), GetString("CommandArgs.Assume.Number")]),
            new("Note", "{action} [?]", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, NoteCommand, true, true, [GetString("CommandArgs.Note.Action"), GetString("CommandArgs.Note.UnknownValue")]),
            new("OS", "{chance} {role}", Command.UsageLevels.HostOrAdmin, Command.UsageTimes.InLobby, OSCommand, true, false, [GetString("CommandArgs.OS.Chance"), GetString("CommandArgs.OS.Role")]),
            new("Negotiation", "{number}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, NegotiationCommand, true, false, [GetString("CommandArgs.Negotiation.Number")]),
            new("Mute", "{id} [duration]", Command.UsageLevels.HostOrModerator, Command.UsageTimes.AfterDeathOrLobby, MuteCommand, true, false, [GetString("CommandArgs.Mute.Id"), GetString("CommandArgs.Mute.Duration")]),
            new("Unmute", "{id}", Command.UsageLevels.HostOrAdmin, Command.UsageTimes.Always, UnmuteCommand, true, false, [GetString("CommandArgs.Unmute.Id")]),
            new("DraftStart", "", Command.UsageLevels.HostOrModerator, Command.UsageTimes.InLobby, DraftStartCommand, true, false),
            new("DraftDescription", "{index}", Command.UsageLevels.Everyone, Command.UsageTimes.InLobby, DraftDescriptionCommand, false, false, [GetString("CommandArgs.DraftDescription.Index")]),
            new("Draft", "{number}", Command.UsageLevels.Everyone, Command.UsageTimes.InLobby, DraftCommand, false, false, [GetString("CommandArgs.Draft.Number")]),
            new("ReadyCheck", "", Command.UsageLevels.HostOrModerator, Command.UsageTimes.InLobby, ReadyCheckCommand, true, false),
            new("Ready", "", Command.UsageLevels.Everyone, Command.UsageTimes.InLobby, ReadyCommand, true, false),
            new("EnableAllRoles", "", Command.UsageLevels.Host, Command.UsageTimes.InLobby, EnableAllRolesCommand, true, false),
            new("Achievements", "", Command.UsageLevels.Modded, Command.UsageTimes.Always, AchievementsCommand, true, false),
            new("DeathNote", "{name}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, DeathNoteCommand, true, true, [GetString("CommandArgs.DeathNote.Name")]),
            new("Word", "{word}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, WordCommand, true, true, [GetString("CommandArgs.Word.Word")]),
            new("Whisper", "{ids} {message}", Command.UsageLevels.Everyone, Command.UsageTimes.Always, WhisperCommand, true, true, [GetString("CommandArgs.Whisper.Ids"), GetString("CommandArgs.Whisper.Message")]),
            new("HWhisper", "{id} {message}", Command.UsageLevels.Host, Command.UsageTimes.Always, HWhisperCommand, true, false, [GetString("CommandArgs.HWhisper.Id"), GetString("CommandArgs.HWhisper.Message")]),
            new("Spectate", "[id]", Command.UsageLevels.Everyone, Command.UsageTimes.InLobby, SpectateCommand, false, false, [GetString("CommandArgs.Spectate.Id")]),
            new("Anagram", "", Command.UsageLevels.Everyone, Command.UsageTimes.AfterDeathOrLobby, AnagramCommand, true, false),
            new("RoleList", "", Command.UsageLevels.Everyone, Command.UsageTimes.Always, RoleListCommand, true, false),
            new("JailTalk", "{message}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, JailTalkCommand, true, true, [GetString("CommandArgs.JailTalk.Message")]),
            new("GameModeList", "", Command.UsageLevels.Everyone, Command.UsageTimes.Always, GameModeListCommand, true, false),
            new("GameModePoll", "", Command.UsageLevels.HostOrModerator, Command.UsageTimes.InLobby, GameModePollCommand, true, false),
            new("MapPoll", "", Command.UsageLevels.HostOrModerator, Command.UsageTimes.InLobby, MapPollCommand, true, false),
            new("EightBall", "[question]", Command.UsageLevels.Everyone, Command.UsageTimes.Always, EightBallCommand, false, false, [GetString("CommandArgs.EightBall.Question")]),
            new("AddTag", "{id} {color} {tag}", Command.UsageLevels.Host, Command.UsageTimes.Always, AddTagCommand, true, false, [GetString("CommandArgs.AddTag.Id"), GetString("CommandArgs.AddTag.Color"), GetString("CommandArgs.AddTag.Tag")]),
            new("DeleteTag", "{id}", Command.UsageLevels.Host, Command.UsageTimes.InLobby, DeleteTagCommand, true, false, [GetString("CommandArgs.DeleteTag.Id")]),
            new("DayBreak", "", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, DayBreakCommand, true, true),
            new("Fix", "{id}", Command.UsageLevels.HostOrModerator, Command.UsageTimes.InGame, FixCommand, true, false, [GetString("CommandArgs.Fix.Id")]),
            new("KillFlash", "", Command.UsageLevels.HostOrModerator, Command.UsageTimes.InGame, KillFlashCommand, true, false),
            new("Abort", "", Command.UsageLevels.HostOrModerator, Command.UsageTimes.InGame, AbortCommand, true, false),
            new("XOR", "{role} {role}", Command.UsageLevels.Everyone, Command.UsageTimes.Always, XORCommand, true, false, [GetString("CommandArgs.XOR.Role"), GetString("CommandArgs.XOR.Role")]),
            new("ChemistInfo", "", Command.UsageLevels.Everyone, Command.UsageTimes.Always, ChemistInfoCommand, true, false),
            new("Forge", "{id} {role}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, ForgeCommand, true, true, [GetString("CommandArgs.Forge.Id"), GetString("CommandArgs.Forge.Role")]),
            new("Choose", "{role}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, ChooseCommand, true, true, [GetString("CommandArgs.Choose.Role")]),
            new("Mark", "{id}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, MarkCommand, true, true, [GetString("CommandArgs.Mark.Id")]),
            new("CopyPreset", "{sourcepreset} {targetpreset}", Command.UsageLevels.Host, Command.UsageTimes.InLobby, CopyPresetCommand, true, false, [GetString("CommandArgs.CopyPreset.SourcePreset"), GetString("CommandArgs.CopyPreset.TargetPreset")]),
            new("AddAdmin", "{id}", Command.UsageLevels.Host, Command.UsageTimes.Always, AddAdminCommand, true, false, [GetString("CommandArgs.AddAdmin.Id")]),
            new("DeleteAdmin", "{id}", Command.UsageLevels.Host, Command.UsageTimes.Always, DeleteAdminCommand, true, false, [GetString("CommandArgs.DeleteAdmin.Id")]),
            new("VoteStart", "", Command.UsageLevels.Everyone, Command.UsageTimes.InLobby, VoteStartCommand, true, false),
            new("Imitate", "{id}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, ImitateCommand, true, true, [GetString("CommandArgs.Imitate.Id")]),
            new("Retribute", "{id}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, RetributeCommand, true, true, [GetString("CommandArgs.Retribute.Id")]),
            new("Revive", "{id}", Command.UsageLevels.HostOrDev, Command.UsageTimes.Always, ReviveCommand, true, false, [GetString("CommandArgs.Revive.Id")]),
            new("Select", "{id} {role}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, SelectCommand, true, true, [GetString("CommandArgs.Select.Id"), GetString("CommandArgs.Select.Role")]),
            new("UIScale", "{scale}", Command.UsageLevels.Modded, Command.UsageTimes.Always, UIScaleCommand, true, false, [GetString("CommandArgs.UIScale.Scale")]),
            new("Fabricate", "{deathreason}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, FabricateCommand, true, true, [GetString("CommandArgs.Fabricate.DeathReason")]),
            new("Start", "", Command.UsageLevels.HostOrModerator, Command.UsageTimes.InLobby, StartCommand, false, false),
            new("StartNow", "", Command.UsageLevels.HostOrAdmin, Command.UsageTimes.InLobby, StartNowCommand, false, false),
            new("Summon", "{id}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, SummonCommand, true, true, [GetString("CommandArgs.Summon.Id")]),
            new("CovenInfo", "", Command.UsageLevels.Everyone, Command.UsageTimes.Always, CovenInfoCommand, true, false),
            new("NeutralInfo", "", Command.UsageLevels.Everyone, Command.UsageTimes.Always, NeutralInfoCommand, true, false),
            new("PlayerInfo", "[id]", Command.UsageLevels.Everyone, Command.UsageTimes.Always, PlayerInfoCommand, true, false, [GetString("CommandArgs.PlayerInfo.Id")]),
            new("TimeLimit", "", Command.UsageLevels.Everyone, Command.UsageTimes.InGame, TimeLimitCommand, true, false),
            new("YT", "{action}", Command.UsageLevels.Host, Command.UsageTimes.Always, YTCommand, true, false, [GetString("CommandArgs.YT.Action")]),
            new("YTPost", "{text}", Command.UsageLevels.Host, Command.UsageTimes.Always, YTPostCommand, true, false, [GetString("CommandArgs.YTPost.Text")]),
            new("Audience", "{action} [args]", Command.UsageLevels.Host, Command.UsageTimes.Always, AudienceCommand, true, false, [GetString("CommandArgs.Audience.Action")]),
            new("Yaminabe", "", Command.UsageLevels.Everyone, Command.UsageTimes.Always, YaminabeCommand, true, false),
            new("ServerInfo", "", Command.UsageLevels.Everyone, Command.UsageTimes.AfterDeathOrLobby, ServerInfoCommand, true, false),
            // alwaysHidden: a non-modded sender's raw text is already broadcast by
            // vanilla before the host sees it, so cancelling isn't enough — the hidden
            // flag is what routes it through the flood-clear. Without it, reporting a
            // griefer would show the griefer the report.
            new("Report", "{message}", Command.UsageLevels.Everyone, Command.UsageTimes.Always, ReportCommand, true, true, [GetString("CommandArgs.Report.Message")]),
            
            // Commands with action handled elsewhere
            new("Guess", "{id} {role}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, (_, _, _) => { }, true, false, [GetString("CommandArgs.Guess.Id"), GetString("CommandArgs.Guess.Role")]),
            // EKR Wave 2 (docs/ekn-wave2-contract.md §1.2 on_meeting_pick): dispatch は EkrManager.PickMsg
            // ("||" 早期チェイン、Command.AllCommands の通常ディスパッチより前) — このエントリは /help 表示専用。
            new("Pick", "{id}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, (_, _, _) => { }, true, false, [GetString("CommandArgs.Pick.Id")]),
            new("Trial", "{id}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, (_, _, _) => { }, true, false, [GetString("CommandArgs.Trial.Id")]),
            new("Swap", "{id}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, (_, _, _) => { }, true, false, [GetString("CommandArgs.Swap.Id")]),
            new("Compare", "{id1} {id2}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, (_, _, _) => { }, true, false, [GetString("CommandArgs.Compare.Id1"), GetString("CommandArgs.Compare.Id2")]),
            new("Interview", "{1|2}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, (_, _, _) => { }, true, false, [GetString("CommandArgs.Interview.Mode")]),
            new("Medium", "{answer}", Command.UsageLevels.Everyone, Command.UsageTimes.InMeeting, (_, _, _) => { }, true, false, [GetString("CommandArgs.Medium.Answer")]),
            new("Revenge", "{id}", Command.UsageLevels.Everyone, Command.UsageTimes.AfterDeath, (_, _, _) => { }, true, false, [GetString("CommandArgs.Revenge.Id")]),
            new("GiveKill", "{id}", Command.UsageLevels.Host, Command.UsageTimes.InLobby, GiveKillCommand, true, false, [GetString("CommandArgs.GiveKill.Id")]),
            new("LobbyKillAction", "{targetId}", Command.UsageLevels.Modded, Command.UsageTimes.InLobby, LobbyKillActionCommand, true, true),

            // Dev-only debug commands
            new("Inspect", "[id]", Command.UsageLevels.Host, Command.UsageTimes.InGame, InspectCommand, true, true, [GetString("CommandArgs.Inspect.Id")]),
            new("OptDump", "[tab]", Command.UsageLevels.Host, Command.UsageTimes.Always, OptDumpCommand, true, true, [GetString("CommandArgs.OptDump.Tab")]),
            new("Cd", "[id] {seconds}", Command.UsageLevels.Host, Command.UsageTimes.InGame, CdCommand, true, true, [GetString("CommandArgs.Cd.Id"), GetString("CommandArgs.Cd.Seconds")]),
            new("DevTp", "{x} {y} [id]", Command.UsageLevels.Host, Command.UsageTimes.InGame, DevTpCommand, true, true, [GetString("CommandArgs.DevTp.X"), GetString("CommandArgs.DevTp.Y"), GetString("CommandArgs.DevTp.Id")]),
            new("DevTpTo", "{srcId} {dstId}", Command.UsageLevels.Host, Command.UsageTimes.InGame, DevTpToCommand, true, true, [GetString("CommandArgs.DevTpTo.SrcId"), GetString("CommandArgs.DevTpTo.DstId")]),
            new("Dummy", "[count]", Command.UsageLevels.Host, Command.UsageTimes.InGame, DummyCommand, true, true, [GetString("CommandArgs.Dummy.Count")]),
            new("UnDummy", "", Command.UsageLevels.Host, Command.UsageTimes.InGame, UnDummyCommand, true, true),
            new("DummyFree", "", Command.UsageLevels.Host, Command.UsageTimes.InGame, DummyFreeCommand, true, true),
            new("SizeTest", "", Command.UsageLevels.Host, Command.UsageTimes.InGame, SizeTestCommand, true, true),
            new("Hitbox", "", Command.UsageLevels.Host, Command.UsageTimes.InGame, HitboxCommand, true, true),
            new("WcDbg", "[mask]", Command.UsageLevels.Host, Command.UsageTimes.Always, WcDbgCommand, true, true),
            new("TpDbg", "[set <n>|official <0|1>]", Command.UsageLevels.Host, Command.UsageTimes.Always, TpDbgCommand, true, true),
            new("TpBurst", "{rate} {sec} [rel|none] [tgt=<id>] [gated] | stop", Command.UsageLevels.Host, Command.UsageTimes.Always, TpBurstCommand, true, true),
            new("Census", "", Command.UsageLevels.Host, Command.UsageTimes.Always, CensusCommand, true, true),
            new("SizeClean", "", Command.UsageLevels.Host, Command.UsageTimes.InGame, SizeCleanCommand, true, true),
            new("RipSize", "[size]", Command.UsageLevels.Host, Command.UsageTimes.InGame, RipSizeCommand, true, true),
            new("Burst", "{count} [direct]", Command.UsageLevels.Host, Command.UsageTimes.Always, BurstCommand, true, true, [GetString("CommandArgs.Burst.Count"), GetString("CommandArgs.Burst.Direct")]),
            new("Nest", "{total} [options]", Command.UsageLevels.Host, Command.UsageTimes.Always, NestCommand, true, true, [GetString("CommandArgs.Nest.Total"), GetString("CommandArgs.Nest.Options")]),
            new("Map", "[list|load <file>|reload|exit|import|export|info]", Command.UsageLevels.Host, Command.UsageTimes.InLobby, MapCommand, true, true),
            new("Role", "[list | import | set [n] [slot] | unset [slot/all]]", Command.UsageLevels.Host, Command.UsageTimes.InLobby, RoleCommand, true, true)
        ];
    }

    private static string[] ModsFileCache = [];
    private static string[] VIPsFileCache = [];
    private static string[] AdminsFileCache = [];
    private static long LastModFileUpdate;
    private static long LastVIPFileUpdate;
    private static long LastAdminFileUpdate;

    // Function to check if a Player is Moderator
    public static bool IsPlayerModerator(string friendCode)
    {
        if (IsPlayerAdmin(friendCode)) return true;
        
        friendCode = friendCode.Replace(':', '#');

        if (friendCode == "" || friendCode == string.Empty || !Options.ApplyModeratorList.GetBool()) return false;

        if (Main.UserData.TryGetValue(friendCode, out Options.UserData userData) && userData.Moderator)
            return true;

        long now = Utils.TimeStamp;
        string[] friendCodes;

        if (LastModFileUpdate + 5 > now)
            friendCodes = ModsFileCache;
        else
        {
            var friendCodesFilePath = $"{Main.DataPath}/EndKnot_DATA/Moderators.txt";

            if (!File.Exists(friendCodesFilePath))
            {
                File.WriteAllText(friendCodesFilePath, string.Empty);
                return false;
            }

            friendCodes = ModsFileCache = File.ReadAllLines(friendCodesFilePath);
            LastModFileUpdate = now;
        }

        return friendCodes.Any(code => code.Contains(friendCode, StringComparison.OrdinalIgnoreCase));
    }

    // Function to check if a player is a VIP
    public static bool IsPlayerVIP(string friendCode)
    {
        if (IsPlayerModerator(friendCode)) return true;
        
        friendCode = friendCode.Replace(':', '#');

        if (friendCode == "" || friendCode == string.Empty || !Options.ApplyVIPList.GetBool()) return false;

        if (Main.UserData.TryGetValue(friendCode, out Options.UserData userData) && userData.Vip)
            return true;

        long now = Utils.TimeStamp;
        string[] friendCodes;

        if (LastVIPFileUpdate + 5 > now)
            friendCodes = VIPsFileCache;
        else
        {
            var friendCodesFilePath = $"{Main.DataPath}/EndKnot_DATA/VIPs.txt";

            if (!File.Exists(friendCodesFilePath))
            {
                File.WriteAllText(friendCodesFilePath, string.Empty);
                return false;
            }

            friendCodes = VIPsFileCache = File.ReadAllLines(friendCodesFilePath);
            LastVIPFileUpdate = now;
        }

        return friendCodes.Any(code => code.Contains(friendCode, StringComparison.OrdinalIgnoreCase));
    }

    // Function to check if a player is an Admin
    public static bool IsPlayerAdmin(string friendCode)
    {
        // Devs sit at the top of the chain (Dev > Admin > Moderator > VIP), regardless of the list options.
        if (friendCode.GetDevUser().up || friendCode.IsLocalDev()) return true;

        friendCode = friendCode.Replace(':', '#');

        if (friendCode == "" || friendCode == string.Empty || !Options.ApplyAdminList.GetBool()) return false;

        if (Main.UserData.TryGetValue(friendCode, out Options.UserData userData) && userData.Admin)
            return true;

        long now = Utils.TimeStamp;
        string[] friendCodes;

        if (LastAdminFileUpdate + 5 > now)
            friendCodes = AdminsFileCache;
        else
        {
            var friendCodesFilePath = $"{Main.DataPath}/EndKnot_DATA/Admins.txt";

            if (!File.Exists(friendCodesFilePath))
            {
                File.WriteAllText(friendCodesFilePath, string.Empty);
                return false;
            }

            friendCodes = AdminsFileCache = File.ReadAllLines(friendCodesFilePath);
            LastAdminFileUpdate = now;
        }

        return friendCodes.Any(code => code.Contains(friendCode, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Prefix(ChatController __instance)
    {
        if (__instance.quickChatField.visible) return true;

        // Read via the crash-safe mirror (never IL2CPP get_text, which fatally 0x80131506's on the chat
        // field's dangling `text` String*), then write the cleaned value back as a FRESH managed string so
        // vanilla SendChat's own get_text read that follows is also safe.
        TextBoxTMP chatArea = __instance.freeChatField.textArea;
        string cleaned = TextBoxPatch.SafeChatText(chatArea).Replace("\b", string.Empty).Replace("\r", string.Empty).Replace("<size=-", "<size=");
        if (chatArea) chatArea.text = cleaned;

        __instance.timeSinceLastMessage = 3f;

        string text = cleaned.Trim();
        var cancelVal = string.Empty;

        // Reject leaked overlay names before they reach the network. A stale IL2CPP wrapper's get_text()
        // can return the literal GameObject name ("PlaceHolderText" etc.) which would otherwise be sent as chat.
        if (TextBoxPatch.IsOverlayLeakName(text)) goto Canceled;

        switch (Options.CurrentGameMode)
        {
            case CustomGameMode.TheMindGame when AmongUsClient.Instance.AmHost:
                TheMindGame.OnChat(PlayerControl.LocalPlayer, text.ToLower());
                break;
            case CustomGameMode.TheMindGame:
                MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.TMGSync, SendOption.Reliable, AmongUsClient.Instance.HostId);
                w.WriteNetObject(PlayerControl.LocalPlayer);
                w.Write(text);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                break;
            case CustomGameMode.BedWars when AmongUsClient.Instance.AmHost:
                BedWars.OnChat(PlayerControl.LocalPlayer, text);
                break;
        }

        if (GameStates.InGame && (Silencer.ForSilencer.Contains(PlayerControl.LocalPlayer.PlayerId) || (Main.PlayerStates[PlayerControl.LocalPlayer.PlayerId].Role is Dad { IsEnable: true } dad && dad.UsingAbilities.Contains(Dad.Ability.GoForMilk))) && PlayerControl.LocalPlayer.IsAlive()) goto Canceled;

        if (GameStates.IsMeeting && Exorcist.AbilityEndTS > Utils.TimeStamp && !text.StartsWith("/cmd") && !PlayerControl.LocalPlayer.Is(CustomRoles.Pestilence))
        {
            LateTask.New(() =>
            {
                PlayerControl.LocalPlayer.RpcGuesserMurderPlayer();
                PlayerControl.LocalPlayer.SetRealKiller(Main.EnumeratePlayerControls().FirstOrDefault(x => x.Is(CustomRoles.Exorcist)));
            }, 0.1f);
        }

        if (AmongUsClient.Instance.AmHost) WordKiller.OnAnyoneChat(PlayerControl.LocalPlayer, text);

        CheckAnagramGuess(PlayerControl.LocalPlayer.PlayerId, text);

        if (ChatHistory.Count == 0 || ChatHistory[^1] != text)
            ChatHistory.Add(text);

        ChatControllerUpdatePatch.CurrentHistorySelection = ChatHistory.Count;

        var canceled = false;
        Main.IsChatCommand = true;

        Logger.Info(text, "SendChat");

        if (!Starspawn.IsDayBreak)
        {
            if (GuessManager.GuesserMsg(PlayerControl.LocalPlayer, text)) goto Canceled;
            if (Judge.TrialMsg(PlayerControl.LocalPlayer, text)) goto Canceled;
            if (Swapper.SwapMsg(PlayerControl.LocalPlayer, text)) goto Canceled;
            if (Inspector.InspectorCheckMsg(PlayerControl.LocalPlayer, text)) goto Canceled;
            if (Councillor.MurderMsg(PlayerControl.LocalPlayer, text)) goto Canceled;
            if (Newscaster.InterviewMsg(PlayerControl.LocalPlayer, text)) goto Canceled;
            if (Medium.MsMsg(PlayerControl.LocalPlayer, text)) goto Canceled;
            if (Nemesis.NemesisMsgCheck(PlayerControl.LocalPlayer, text)) goto Canceled;
            // EKR Wave 2 (docs/ekn-wave2-contract.md §1.2): ローカル側 (ホスト自身の /pick) ディスパッチ。
            if (EndKnot.Modules.Ekm.EkrManager.PickMsg(PlayerControl.LocalPlayer, text)) goto Canceled;
        }

        Main.IsChatCommand = false;

        if (text.StartsWith('/'))
        {
            Utils.CheckServerCommand(ref text, out bool spamRequired);
            if (spamRequired && ShouldAutoHide(text)) spamRequired = false;
            string[] args = text.Split(' ');

            foreach (Command command in Command.AllCommands)
            {
                if (!command.IsThisCommand(text)) continue;

                Logger.Info($" Recognized command: {text}", "ChatCommand");
                Main.IsChatCommand = true;

                if (!command.CanUseCommand(PlayerControl.LocalPlayer, sendErrorMessage: true))
                    goto Canceled;

                if (!AmongUsClient.Instance.AmHost && command.UsageLevel != Command.UsageLevels.Modded && command.Key is not ("ChemistInfo" or "NeutralInfo" or "CovenInfo" or "PlayerInfo"))
                {
                    RequestCommandProcessingFromHost(text, command.Key);
                    if (command.IsCanceled || command.AlwaysHidden || !spamRequired) goto Canceled;
                    break;
                }
                
                command.Action(PlayerControl.LocalPlayer, text, args);

                if (command.IsCanceled || command.AlwaysHidden || !spamRequired) goto Canceled;
                break;
            }

            Statistics.HasUsedAnyCommand = true;
        }

        if (!Main.IsChatCommand && Astral.On && !PlayerControl.LocalPlayer.Is(CustomRoles.Astral))
            LateTask.New(() => Main.PlayerStates.Values.DoIf(x => !x.IsDead && x.Role is Astral { Timer: not null } && x.Player, x => ChatManager.ClearChat(x.Player)), 0.2f, log: false);

        if (CheckMute(PlayerControl.LocalPlayer.PlayerId))
            goto Canceled;

        if (string.IsNullOrWhiteSpace(text))
            goto Canceled;

        goto Skip;
        Canceled:
        Main.IsChatCommand = false;
        canceled = true;
        Skip:

        if (ExileController.Instance)
            canceled = true;

        if (canceled)
        {
            Logger.Info("Command Canceled", "ChatCommand");
            __instance.freeChatField.textArea.Clear();
            __instance.freeChatField.textArea.SetText(cancelVal);
        }
        else if (GameStates.IsLobby && AmongUsClient.Instance.AmHost)
        {
            long now = Utils.TimeStamp;

            if (LastSetNameInLobby + 3 < now)
            {
                // Only broadcast when ApplySuffix produced a name; a false return leaves it null/empty
                // and would blank the host label on every client (see PlayerJoinAndLeftPatch.cs guard).
                if (Utils.ApplySuffix(PlayerControl.LocalPlayer, out string name))
                    PlayerControl.LocalPlayer.RpcSetName(name);
                LastSetNameInLobby = now;
            }
        }

        if (text.Contains("666") && PlayerControl.LocalPlayer.Is(CustomRoles.Demon))
            Achievements.Type.WhatTheHell.Complete();

        if (!canceled && AmongUsClient.Instance.AmHost && Utils.TempReviveHostRunning)
        {
            if (!WaitingToSend) Main.Instance.StartCoroutine(Wait());
            return false;
            
            IEnumerator Wait()
            {
                WaitingToSend = true;
                while (Utils.TempReviveHostRunning && AmongUsClient.Instance.AmHost) yield return null;
                yield return new WaitForSecondsRealtime(0.5f);
                WaitingToSend = false;
                if (GameStates.IsEnded || GameStates.IsLobby) yield break;
                if (HudManager.InstanceExists) HudManager.Instance.Chat.SendChat();
            }
        }
        
        if (!canceled)
            ChatManager.SendMessage(PlayerControl.LocalPlayer, text);

        return !canceled;
    }

    private static void CheckAnagramGuess(byte id, string text)
    {
        if (CurrentAnagram != string.Empty && text.Contains(CurrentAnagram))
        {
            Utils.SendMessage("\n", title: string.Format(GetString("Anagram.CorrectGuess"), id.ColoredPlayerName(), CurrentAnagram), importance: MessageImportance.High);
            CurrentAnagram = string.Empty;
        }
    }

    public static void RequestCommandProcessingFromHost(string text, string commandKey)
    {
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.RequestCommandProcessing, SendOption.Reliable, AmongUsClient.Instance.HostId);
        writer.Write(commandKey);
        writer.Write(text);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------

    public static void ExoCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (!Main.PlayerStates.TryGetValue(player.PlayerId, out PlayerState state) || state.IsDead || state.MainRole != CustomRoles.Exorcist || player.GetAbilityUseLimit() < 1) return;

        int duration = Exorcist.AbilityDuration.GetInt();
        Exorcist.AbilityEndTS = Utils.TimeStamp + duration;
        player.RpcRemoveAbilityUse();

        Utils.SendMessage(string.Format(GetString("Exorcist.AbilityUsedMsg"), duration), title: CustomRoles.Exorcist.ToColoredString());

        LateTask.New(() =>
        {
            if (!GameStates.IsMeeting) return;
            Utils.SendMessage(GetString("Exorcist.AbilityEnded"), title: CustomRoles.Exorcist.ToColoredString());
        }, duration);

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void RerollCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (!Reroll.TryQueueCommandTrigger(player)) return;

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void TimeLimitCommand(PlayerControl player, string text, string[] args)
    {
        Utils.SendMessage("\n", player.PlayerId, Options.EnableGameTimeLimit.GetBool() ? $"{Options.GameTimeLimit.GetInt() - Main.GameTimer.Elapsed.TotalSeconds:N0}s {GetString("RemainingText.Suffix")}" : "<size=4>∞</size>");
    }

    private static void YTCommand(PlayerControl player, string text, string[] args)
    {
        // /yt <url>     -> ストリーム開始
        // /yt off       -> 停止
        // /yt status    -> 現在の状態
        // /yt clear     -> overlay クリア（履歴削除）
        if (!YouTubeChatOptions.Enabled.GetBool())
        {
            Utils.SendMessage(GetString("YouTubeChat.NotEnabled"), player.PlayerId);
            return;
        }

        if (args.Length < 2)
        {
            Utils.SendMessage(GetString("YouTubeChat.Usage"), player.PlayerId);
            return;
        }

        string sub = args[1].Trim();
        switch (sub.ToLowerInvariant())
        {
            case "off":
            case "stop":
                YouTubeChatManager.Stop();
                YouTubeChatOverlay.Reset();
                Main.YouTubeStreamUrl.Value = "";
                Utils.SendMessage(GetString("YouTubeChat.Stopped"), player.PlayerId);
                return;

            case "status":
                if (YouTubeChatManager.IsActive)
                    Utils.SendMessage(string.Format(GetString("YouTubeChat.Status.Active"), YouTubeChatManager.CurrentVideoId), player.PlayerId);
                else
                    Utils.SendMessage(GetString("YouTubeChat.Status.Inactive"), player.PlayerId);
                return;

            case "clear":
                YouTubeChatOverlay.Reset();
                return;
        }

        // それ以外は URL として扱う。args[1..] を結合（URL に空白は無いはずだが念のため）
        string url = string.Join(' ', args, 1, args.Length - 1).Trim();
        string err = YouTubeChatManager.Start(url);
        if (err != null)
        {
            Utils.SendMessage(GetString($"YouTubeChat.Error.{err}"), player.PlayerId);
            return;
        }

        // 永続化
        Main.YouTubeStreamUrl.Value = url;

        // 初回利用警告（ConfigEntry で記憶、一度出したら出さない）
        if (!Main.YouTubeChatWarned.Value)
        {
            Main.YouTubeChatWarned.Value = true;
            Utils.SendMessage(GetString("YouTubeChat.FirstTimeWarning"), player.PlayerId);
        }

        Utils.SendMessage(string.Format(GetString("YouTubeChat.Started"), YouTubeChatManager.CurrentVideoId), player.PlayerId);
        YouTubeChatOverlay.EnsureSubscribed();
    }

    private static void YTPostCommand(PlayerControl player, string text, string[] args)
    {
        // /ytpost <text> -> YouTube ライブチャットへの手動投稿（疎通テスト用）
        if (args.Length < 2)
        {
            Utils.SendMessage(GetString("YouTubePost.Usage"), player.PlayerId);
            return;
        }

        string message = string.Join(' ', args, 1, args.Length - 1).Trim();
        YouTubeChatPoster.PostRaw(message);
        Utils.SendMessage(GetString("YouTubePost.Posted"), player.PlayerId);
    }

    private static void AudienceCommand(PlayerControl player, string text, string[] args)
    {
        // /audience ban <author>       -> BAN
        // /audience unban <author>     -> BAN 解除
        // /audience points <author> <n> -> ポイント付与(負数で減算)
        // /audience status             -> 稼働状態 + キュー長 + 上位ポイント
        if (args.Length < 2)
        {
            Utils.SendMessage(GetString("Audience.Usage"), player.PlayerId);
            return;
        }

        string sub = args[1].Trim().ToLowerInvariant();

        switch (sub)
        {
            case "ban":
            {
                if (args.Length < 3)
                {
                    Utils.SendMessage(GetString("Audience.Usage"), player.PlayerId);
                    return;
                }

                string author = string.Join(' ', args, 2, args.Length - 2).Trim();
                EndKnot.Modules.Audience.AudienceEconomy.Ban(author);
                Utils.SendMessage(string.Format(GetString("Audience.Banned"), author), player.PlayerId);
                return;
            }

            case "unban":
            {
                if (args.Length < 3)
                {
                    Utils.SendMessage(GetString("Audience.Usage"), player.PlayerId);
                    return;
                }

                string author = string.Join(' ', args, 2, args.Length - 2).Trim();
                EndKnot.Modules.Audience.AudienceEconomy.Unban(author);
                Utils.SendMessage(string.Format(GetString("Audience.Unbanned"), author), player.PlayerId);
                return;
            }

            case "points":
            {
                if (args.Length < 4 || !int.TryParse(args[^1], out int amount))
                {
                    Utils.SendMessage(GetString("Audience.Usage"), player.PlayerId);
                    return;
                }

                string author = string.Join(' ', args, 2, args.Length - 3).Trim();
                EndKnot.Modules.Audience.AudienceEconomy.AddPoints(author, amount);
                Utils.SendMessage(string.Format(GetString("Audience.PointsGranted"), amount, author, EndKnot.Modules.Audience.AudienceEconomy.GetPoints(author)), player.PlayerId);
                return;
            }

            case "status":
            {
                bool enabled = EndKnot.Modules.Audience.AudienceOptions.Enabled != null && EndKnot.Modules.Audience.AudienceOptions.Enabled.GetBool();
                string top = string.Join('\n', EndKnot.Modules.Audience.AudienceEconomy.TopPoints(5).Select(kvp => $"{kvp.Key}: {kvp.Value}"));
                if (string.IsNullOrEmpty(top)) top = GetString("Audience.Status.NoPoints");

                Utils.SendMessage(string.Format(GetString("Audience.Status"), enabled ? GetString("Audience.Status.On") : GetString("Audience.Status.Off"), EndKnot.Modules.Audience.AudienceManager.QueuedInterventionCount, top), player.PlayerId);
                return;
            }

            default:
                Utils.SendMessage(GetString("Audience.Usage"), player.PlayerId);
                return;
        }
    }

    private static void PlayerInfoCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2)
        {
            Utils.SendMessage("<size=80%>" + string.Join('\n', Main.CachedAllPlayerControls().Select(x =>
            {
                ClientData client = x.GetClient();
                string name = Main.AllPlayerNames.GetValueOrDefault(x.PlayerId, string.Empty);
                string id = string.IsNullOrEmpty(name) ? $"ID {x.PlayerId}" : $" (ID {x.PlayerId})";
                return $"{name}{id} - {x.FriendCode} | {client?.GetHashedPuid()} | {client?.PlatformData.Platform}";
            })) + "</size>", player.PlayerId);
        }
        else if (byte.TryParse(args[1], out byte playerId))
        {
            PlayerControl pc = playerId.GetPlayer();
            if (!pc) return;
            ClientData client = pc.GetClient();
            string name = Main.AllPlayerNames.GetValueOrDefault(pc.PlayerId, string.Empty);
            string id = string.IsNullOrEmpty(name) ? $"ID {pc.PlayerId}" : $" (ID {pc.PlayerId})";
            Utils.SendMessage($"<b>{name}{id}:</b>\n{pc.FriendCode}\n{client?.GetHashedPuid()}\n{client?.PlatformData.Platform}", player.PlayerId);
        }
    }
    
    private static void NeutralInfoCommand(PlayerControl player, string text, string[] args)
    {
        Utils.SendMessage(GetString("NeutralInfoDescription"), player.PlayerId);
    }

    private static void YaminabeCommand(PlayerControl player, string text, string[] args)
    {
        ChaosPotSupport.SendChatToPlayer(player);
    }
    
    private static void CovenInfoCommand(PlayerControl player, string text, string[] args)
    {
        Utils.SendMessage(GetString("CovenInfoDescription"), player.PlayerId);
    }

    private static void ServerInfoCommand(PlayerControl player, string text, string[] args)
    {
        if (!Options.ServerInviteCommandEnabled.GetBool()) return;
        Utils.SendMessage(ServerInviteOverlay.GetServerInfoMessage(), player.PlayerId, GetString("ServerInvite.Title"));
    }

    // Every reply here goes to the reporter alone — nothing about a report is shown to
    // the host or the lobby, so reporting a player in the room stays invisible to them.
    private static void ReportCommand(PlayerControl player, string text, string[] args)
    {
        // Silent when disabled, same as ServerInfoCommand — the host turned the
        // channel off, so the reporter shouldn't get a reply either way.
        if (!Options.ReportCommandEnabled.GetBool()) return;

        string title = GetString("LobbyShare.ReportTitle");
        string message = args.Length > 1 ? text[(text.IndexOf(' ') + 1)..] : string.Empty;

        switch (LobbyShare.SubmitReport(player, message))
        {
            case LobbyShare.ReportResult.Empty:
                Utils.SendMessage(GetString("LobbyShare.ReportUsage"), player.PlayerId, title);
                break;
            case LobbyShare.ReportResult.OnCooldown:
                Utils.SendMessage(GetString("LobbyShare.ReportCooldown"), player.PlayerId, title);
                break;
            default:
                Utils.SendMessage(GetString("LobbyShare.ReportAck"), player.PlayerId, title);
                break;
        }
    }
    
    public static void SummonCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;
        
        if (!Main.PlayerStates.TryGetValue(player.PlayerId, out PlayerState state) || state.IsDead || state.Role is not Summoner sum || player.GetAbilityUseLimit() < 1) return;
        if (args.Length < 2 || !byte.TryParse(args[1], out byte targetId) || !Main.PlayerStates.TryGetValue(targetId, out var targetState) || !targetState.IsDead || targetState.MainRole == CustomRoles.GM) return;

        bool reSummoned = !Summoner.AlreadySummoned.Add(targetId);

        if (reSummoned && !Summoner.AllowSummoningTheSamePlayerTwice.GetBool())
        {
            Utils.SendMessage("\n", player.PlayerId, GetString("Summoner.CantSummonSamePlayerTwice"));
            return;
        }
        
        sum.SummonedPlayerId = targetId;
        
        if (!reSummoned || Summoner.ReSummonTakesAbilityUse.GetBool())
            player.RpcRemoveAbilityUse();
        
        Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("Summoner.SummonSuccessMessage"), targetId.ColoredPlayerName()));
        
        MeetingManager.SendCommandUsedMessage(args[0]);
    }
    
    private static void StartCommand(PlayerControl player, string text, string[] args)
    {
        VotedToStart.UnionWith(Main.EnumeratePlayerControls().Select(x => x.PlayerId));
    }

    private static void StartNowCommand(PlayerControl player, string text, string[] args)
    {
        if (!GameStartManager.InstanceExists) return;

        if (!GameStates.IsCountDown)
        {
            Main.EnumeratePlayerControls().DoIf(p => p.Data.DefaultOutfit.ColorId < 0 || Palette.PlayerColors.Length <= p.Data.DefaultOutfit.ColorId, p => AmongUsClient.Instance.KickPlayer(p.OwnerId, false));

            GameStartManagerPatch.UpdateSpriteStartButton = true;

            if (Options.RandomMapsMode.GetBool())
            {
                Main.NormalOptions.MapId = GameStartRandomMap.SelectRandomMap();
                GameOptionsMapPickerPatch.SetDleks = Main.CurrentMap == MapNames.Dleks;
            }
            else if (GameOptionsMapPickerPatch.SetDleks) Main.NormalOptions.MapId = 3;
            else if (GameOptionsMapPickerPatch.SetSubmerged) Main.NormalOptions.MapId = 6;

            if (Options.OverrideSpeedForEachMap.GetBool() && Options.MapSpeeds.TryGetValue(Main.CurrentMap, out var option))
                Main.NormalOptions.PlayerSpeedMod = option.GetFloat();

            if (Main.CurrentMap == MapNames.Dleks || Main.NormalOptions.MapId == 6)
            {
                var opt = Main.NormalOptions.CastFast<IGameOptions>();

                Options.DefaultKillCooldown = Main.NormalOptions.KillCooldown;
                Main.LastKillCooldown.Value = Main.NormalOptions.KillCooldown;
                Main.NormalOptions.KillCooldown = 0f;
                AURoleOptions.SetOpt(opt);
                Main.LastShapeshifterCooldown.Value = AURoleOptions.ShapeshifterCooldown;
                AURoleOptions.ShapeshifterCooldown = 0f;
                AURoleOptions.ImpostorsCanSeeProtect = false;

                GameManager.Instance.LogicOptions.SetDirty();
                OptionItem.SyncAllOptions();
            }

            GameStartManager.Instance.startState = GameStartManager.StartingStates.Countdown;
            GameStartManager.Instance.countDownTimer = Options.AutoStartTimer.GetInt();
            GameStartManager.Instance.StartButton.gameObject.SetActive(false);

            if (HudManager.InstanceExists)
                HudManager.Instance.Dialogue.Hide();
        }

        GameStartManager.Instance.countDownTimer = 0;
    }

    private static void FabricateCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;
        
        if (!Main.PlayerStates.TryGetValue(player.PlayerId, out PlayerState state) || state.IsDead || state.Role is not Fabricator fab) return;
        
        if (args.Length < 2 || !PlayerState.AllDeathReason.FindFirst(x => GetString($"DeathReason.{x}").Replace(" ", string.Empty).Equals(args[1].Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase), out PlayerState.DeathReason newDeathReason))
        {
            Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("Fabricator.InvalidDeathReason"), args.Length >= 2 ? args[1] : ""));
            return;
        }

        fab.NextDeathReason = newDeathReason;
        
        Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("Fabricator.SetDeathReason"), GetString($"DeathReason.{newDeathReason}")));
        
        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void UIScaleCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !float.TryParse(args[1], out float scale) || scale == 0f) return;
        HudManagerStartPatch.TryResizeUI(scale);
    }
    
    private static void SelectCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (!Main.PlayerStates.TryGetValue(player.PlayerId, out PlayerState state) || state.IsDead || state.Role is not Loner loner || loner.Done) return;
        if (args.Length < 3 || !GuessManager.MsgToPlayerAndRole(text[7..], out byte targetId, out CustomRoles pickedRole, out _) || targetId == player.PlayerId) return;
        if (!pickedRole.IsImpostor() || pickedRole.IsVanilla() || CustomRoleSelector.RoleResult.ContainsValue(pickedRole) || pickedRole.GetMode() == 0) return;
        if (!Main.PlayerStates.TryGetValue(targetId, out PlayerState ts) || ts.IsDead) return;

        loner.PickedPlayer = targetId;
        loner.PickedRole = pickedRole;

        Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("Loner.Picked"), targetId.ColoredPlayerName(), pickedRole.ToColoredString()));

        MeetingManager.SendCommandUsedMessage(args[0]);
    }
    
    private static void ReviveCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !byte.TryParse(args[1], out byte targetId)) return;

        PlayerControl target = Utils.GetPlayerById(targetId);
        if (target == null) return;

        if (GameStates.IsLobby)
        {
            if (target.Data == null || !target.Data.IsDead) return;
            target.Data.IsDead = false;
            target.Data.SetDirtyBit(0b_1u << target.PlayerId);
            AmongUsClient.Instance.SendAllStreamedObjects();
            Utils.SendMessage(string.Format(GetString("Message.Revived"), target.Data.PlayerName));
            return;
        }

        if (!Options.NoGameEnd.GetBool() && !player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        target.RpcRevive();
    }
    
    private static void GiveKillCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !byte.TryParse(args[1], out byte targetId))
        {
            Utils.SendMessage(Translator.GetString("LobbyKill.InvalidTarget"), player.PlayerId);
            return;
        }

        PlayerControl target = Utils.GetPlayerById(targetId);
        if (target == null)
        {
            Utils.SendMessage(Translator.GetString("LobbyKill.InvalidTarget"), player.PlayerId);
            return;
        }

        Main.LobbyKillers.Add(targetId);
        EndKnot.Modules.RPC.SyncLobbyState();
        Utils.SendMessage(string.Format(Translator.GetString("LobbyKill.Granted"), target.GetRealName()), player.PlayerId);
    }

    private static void LobbyKillActionCommand(PlayerControl killer, string text, string[] args)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (args.Length < 1 || !byte.TryParse(args[0], out byte targetId)) return;
        if (!Main.LobbyKillers.Contains(killer.PlayerId)) return;
        if (Main.LobbyDead.Contains(killer.PlayerId) || Main.LobbyDead.Contains(targetId)) return;

        LobbyKillSystem.ProcessLobbyKill(killer, targetId);
    }

    public static void RetributeCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (!player.IsAlive() || !Main.PlayerStates.TryGetValue(player.PlayerId, out PlayerState state) || state.Role is not Retributionist { Notified: true } rb || rb.Camping == byte.MaxValue) return;

        PlayerControl campTarget = Utils.GetPlayerById(rb.Camping);
        if (campTarget == null || campTarget.IsAlive() || !Main.PlayerStates.TryGetValue(campTarget.PlayerId, out PlayerState campState)) return;

        if (args.Length < 2 || !byte.TryParse(args[1], out byte targetId)) return;

        byte realKiller = campState.GetRealKiller();

        if (realKiller != targetId)
        {
            rb.Notified = false;
            RPC.PlaySoundRPC(player.PlayerId, Sounds.SabotageSound);
            Utils.SendMessage("\n", player.PlayerId, GetString("Retributionist.Fail"), importance: MessageImportance.High);
        }
        else
        {
            PlayerControl killer = Utils.GetPlayerById(realKiller);

            if (killer == null || !killer.IsAlive())
            {
                rb.Notified = false;
                Utils.SendMessage("\n", player.PlayerId, GetString("Retributionist.KillerDead"));
            }
            else if (!killer.Is(CustomRoles.Pestilence))
            {
                killer.SetRealKiller(player);
                Main.PlayerStates[killer.PlayerId].deathReason = PlayerState.DeathReason.Retribution;
                Medic.IsDead(killer);
                killer.RpcGuesserMurderPlayer();
                Utils.AfterPlayerDeathTasks(killer, true);
                Utils.SendMessage("\n", title: CustomRoles.Retributionist.ColoredTextByRole(string.Format(GetString("Retributionist.SuccessOthers"), targetId.ColoredPlayerName(), CustomRoles.Retributionist.ToColoredString())), importance: MessageImportance.High);
                Utils.SendMessage("\n", player.PlayerId, GetString("Retributionist.Success"));
            }
        }

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    public static void ImitateCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (!Imitator.PlayerIdList.Contains(player.PlayerId) || !player.IsAlive() || args.Length < 2 || !byte.TryParse(args[1], out byte targetId) || !Main.PlayerStates.TryGetValue(targetId, out PlayerState targetState)) return;

        if (!targetState.IsDead)
        {
            RPC.PlaySoundRPC(player.PlayerId, Sounds.SabotageSound);
            Utils.SendMessage("\n", player.PlayerId, GetString("Imitator.TargetMustBeDead"));
            return;
        }

        if (!targetState.MainRole.Is(Team.Crewmate) || targetState.MainRole == CustomRoles.GM)
        {
            RPC.PlaySoundRPC(player.PlayerId, Sounds.SabotageSound);
            Utils.SendMessage("\n", player.PlayerId, GetString("Imitator.TargetMustBeCrew"), importance: MessageImportance.High);
            return;
        }

        Imitator.ImitatingRole[player.PlayerId] = targetState.MainRole;
        RPC.PlaySoundRPC(player.PlayerId, Sounds.TaskComplete);
        Logger.Info($"{player.GetRealName()} will be imitating as {targetState.MainRole}", "Imitator");
        Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("Imitator.Success"), targetId.ColoredPlayerName()), importance: MessageImportance.High);

        MeetingManager.SendCommandUsedMessage(args[0]);
    }
    
    private static void VoteStartCommand(PlayerControl player, string text, string[] args)
    {
        if (Options.DisableVoteStartCommand.GetBool())
        {
            Utils.SendMessage("\n", player.PlayerId, GetString("VoteStartDisabled"));
            return;
        }

        if (VotedToStart.Add(player.PlayerId))
        {
            int voteCount = VotedToStart.Count;
            int playerCount = PlayerControl.AllPlayerControls.Count;
            var percentage = (int)Math.Round(voteCount / (float)playerCount * 100f);
            var required = (int)Math.Ceiling(playerCount / 2f);
            Utils.SendMessage(string.Format(GetString("VotedToStart"), voteCount, playerCount, percentage, required), title: string.Format(GetString("VotedToStart.Title"), player.PlayerId.ColoredPlayerName()));
        }
    }
    
    private static void DeleteAdminCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !byte.TryParse(args[1], out byte remAdminId)) return;

        PlayerControl remAdminPc = Utils.GetPlayerById(remAdminId);
        if (remAdminPc == null) return;

        string remFc = remAdminPc.FriendCode.Replace(':', '#');

        if (!IsPlayerAdmin(remFc))
        {
            Utils.SendMessage(GetString("PlayerNotAdmin"), player.PlayerId);
            return;
        }

        File.WriteAllLines($"{Main.DataPath}/EndKnot_DATA/Admins.txt", File.ReadAllLines($"{Main.DataPath}/EndKnot_DATA/Admins.txt").Where(x => !x.Contains(remFc)));
        Utils.SendMessage(GetString("PlayerRemovedFromAdminList"), player.PlayerId);
    }

    private static void AddAdminCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !byte.TryParse(args[1], out byte newAdminId)) return;

        PlayerControl newAdminPc = Utils.GetPlayerById(newAdminId);
        if (newAdminPc == null) return;

        string fc = newAdminPc.FriendCode.Replace(':', '#');
        if (IsPlayerModerator(fc)) Utils.SendMessage(GetString("PlayerAlreadyAdmin"), player.PlayerId);

        File.AppendAllText($"{Main.DataPath}/EndKnot_DATA/Admins.txt", $"\n{fc}");
        Utils.SendMessage(GetString("PlayerAddedToAdminList"), player.PlayerId);
    }
    
    private static void CopyPresetCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[1], out int sourcePresetId) || sourcePresetId is < 1 or > 20 || (!int.TryParse(args[2], out int targetPreset) && targetPreset is < 1 or > 20)) return;

        Prompt.Show(string.Format(GetString("Promt.CopyPreset"), sourcePresetId, targetPreset), Copy, () => { });
        return;

        void Copy()
        {
            sourcePresetId--;
            targetPreset--;

            foreach (OptionItem optionItem in OptionItem.AllOptions)
            {
                if (optionItem.IsSingleValue) continue;
                optionItem.AllValues[targetPreset] = optionItem.AllValues[sourcePresetId];
            }

            OptionItem.SyncAllOptions();
            OptionSaver.Save();
        }
    }
    
    public static void ChooseCommand(PlayerControl player, string text, string[] args)
    {
        if (!Main.PlayerStates.TryGetValue(player.PlayerId, out var state) || state.IsDead) return;

        if (args.Length < 2 || !GetRoleByName(string.Join(' ', args[1..]), out var role) || !role.IsEnable())
        {
            Utils.SendMessage("\n", player.PlayerId, GetString("PawnChooseFail"));
            return;
        }

        if (state.Role is Pawn pawn)
        {
            pawn.ChosenRole = role;
            Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("PawnChosenRole"), role.ToColoredString()));
        }
        else if (state.Role is Changeling changeling)
        {
            if (!Changeling.Roles.Contains(role))
            {
                Utils.SendMessage("\n", player.PlayerId, GetString("ChangelingChooseFail"));
                return;
            }
            changeling.CurrentRole = role;
            changeling.UsedCommand = true;
            Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("ChangelingChosenRole"), role.ToColoredString()));
        }
        else
            return;

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    public static void MarkCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (!player.IsAlive() || Main.PlayerStates[player.PlayerId].Role is not Markseeker { IsEnable: true } ms || ms.MarkedId != byte.MaxValue) return;

        ms.MarkedId = args.Length < 2 ? byte.MaxValue : byte.TryParse(args[1], out byte targetId) ? targetId : byte.MaxValue;

        player.RPCPlayCustomSound("Line");
        Utils.SendRPC(CustomRPC.SyncRoleData, player.PlayerId, ms.MarkedId);

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void ForgeCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (!player.IsAlive() || !player.Is(CustomRoles.Forger) || player.GetAbilityUseLimit() < 1) return;
        if (args.Length < 3 || !GuessManager.MsgToPlayerAndRole(text[6..], out byte targetId, out CustomRoles forgeRole, out _)) return;

        player.RpcRemoveAbilityUse();

        Forger.Forges[targetId] = forgeRole;
        Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("ForgeSuccess"), (int)Math.Round(player.GetAbilityUseLimit(), 1), targetId.ColoredPlayerName(), forgeRole.ToColoredString()));

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void ChemistInfoCommand(PlayerControl player, string text, string[] args)
    {
        Utils.SendMessage(Chemist.GetProcessesInfo(), player.PlayerId, CustomRoles.Chemist.ToColoredString());
    }

    private static void XORCommand(PlayerControl player, string text, string[] args)
    {
        if ((!player.IsHost() && !IsPlayerAdmin(player.FriendCode)) || args.Length < 3 || !GetRoleByName(args[1], out CustomRoles role1) || !GetRoleByName(args[2], out CustomRoles role2))
        {
            Utils.SendMessage(string.Join('\n', Main.XORRoles.ConvertAll(x => $"{x.Item1.ToColoredString()} ⊕ {x.Item2.ToColoredString()}")), player.PlayerId, GetString("XORListTitle"));
            return;
        }

        if (Main.XORRoles.Remove((role1, role2)) || Main.XORRoles.Remove((role2, role1)))
        {
            Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("XORRemoved"), role1.ToColoredString(), role2.ToColoredString()));
            return;
        }

        if (role1 == role2)
        {
            Utils.SendMessage("\n", player.PlayerId, GetString("XORSameRole"));
            return;
        }

        if (role1.IsAdditionRole() || role2.IsAdditionRole())
        {
            Utils.SendMessage("\n", player.PlayerId, GetString("XORAdditionRole"));
            return;
        }

        Main.XORRoles.Add((role1, role2));
        Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("XORAdded"), role1.ToColoredString(), role2.ToColoredString()));
    }

    private static void FixCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !byte.TryParse(args[1], out byte id)) return;

        var pc = id.GetPlayer();
        if (pc == null) return;

        pc.FixBlackScreen();

        if (Main.EnumeratePlayerControls().All(x => x.IsAlive()))
            Logger.SendInGame(GetString("FixBlackScreenWaitForDead"), Color.yellow);
    }

    // TOHK の /kf 相当: 全バニラクライアントへリアクター desync フラッシュを撃ち、固まった
    // 黒画面を HUD 再構築で強制復帰させる (BUG-20260716-09 の手動レスキュー)。/fix {id} と違い
    // FixBlackScreen の会議ゲートを通らないので、いつでも即座に全員へ撃てる。
    private static void KillFlashCommand(PlayerControl player, string text, string[] args)
    {
        Logger.Info($"/kf manual all-player reactor flash requested by {player.GetNameWithRole()}", "KillFlash");

        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
        {
            if (!pc || pc.IsModdedClient()) continue;
            pc.ReactorFlash();
        }
    }

    // 廃村 (Shift+L+Enter 相当) のチャット版: ホスト画面が固まっている/ホスト不在でも
    // モデレーターがチャットからゲームを引き分けで畳めるようにする (2026-07-17 配信中の要望)
    private static void AbortCommand(PlayerControl player, string text, string[] args)
    {
        Logger.Info($"/abort force end game (廃村) requested by {player.GetNameWithRole()}", "Abort");
        CustomWinnerHolder.ResetAndSetWinner(CustomWinner.Draw);
        GameEndChecker.CheckCustomEndCriteria();
    }

    public static void DayBreakCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.IsAlive() || Main.PlayerStates[player.PlayerId].Role is not Starspawn sp || sp.HasUsedDayBreak) return;

        Starspawn.IsDayBreak = true;
        sp.HasUsedDayBreak = true;

        player.RPCPlayCustomSound("Line");
        Utils.SendMessage("\n", title: string.Format(GetString("StarspawnUsedDayBreak"), CustomRoles.Starspawn.ToColoredString()), importance: MessageImportance.High);
    }

    private static void AddTagCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 4 || !byte.TryParse(args[1], out byte id)) return;

        PlayerControl pc = id.GetPlayer();
        if (pc == null) return;

        Color color = ColorUtility.TryParseHtmlString($"#{args[2].ToLower()}", out Color c) ? c : Color.red;
        string tag = Utils.ColorString(color, string.Join(' ', args[3..]) + " ");
        PrivateTagManager.AddTag(pc.FriendCode, tag);

        Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("AddTagSuccess"), tag, id.ColoredPlayerName(), id));
    }

    private static void DeleteTagCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !byte.TryParse(args[1], out byte id)) return;

        PlayerControl pc = id.GetPlayer();
        if (pc == null) return;

        PrivateTagManager.DeleteTag(pc.FriendCode);
        Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("DeleteTagSuccess"), id.ColoredPlayerName()));
        Utils.DirtyName.Add(pc.PlayerId);
    }

    private static void EightBallCommand(PlayerControl player, string text, string[] args)
    {
        if (Options.Disable8ballCommand.GetBool())
        {
            Utils.SendMessage("\n", player.PlayerId, GetString("EightBallDisabled"), importance: MessageImportance.Low);
            return;
        }

        Utils.SendMessage(GetString($"8BallResponse.{IRandom.Instance.Next(Options.EightballCommandIndexes.GetInt())}"), player.IsAlive() ? byte.MaxValue : player.PlayerId, GetString("8BallResponseTitle"));
    }

    public static void GameModePollCommand(PlayerControl player, string text, string[] args)
    {
        GMPollGameModes = Main.CustomGameModeValues[..^1].Where(x => Options.GMPollGameModesSettings[x].GetBool()).ToList();
        string gmNames = string.Join(' ', GMPollGameModes.Select(x => GetString(x.ToString()).Replace(' ', '_')));
        var msg = $"/poll {GetString("GameModePoll.Question").TrimEnd('?')}? {gmNames}";
        PollCommand(player, msg, msg.Split(' '));
    }
    
    public static void MapPollCommand(PlayerControl player, string text, string[] args)
    {
        MPollMaps = Main.MapNamesValues.Where(x => Options.MPollMapsSettings[x].GetBool()).ToList();
        string mNames = string.Join(' ', MPollMaps.Select(x => GetString(x.ToString()).Replace(' ', '_')));
        var msg = $"/poll {GetString("MapPoll.Question").TrimEnd('?')}? {mNames}";
        PollCommand(player, msg, msg.Split(' '));
    }

    private static void GameModeListCommand(PlayerControl player, string text, string[] args)
    {
        string info = string.Join("\n\n", Main.CustomGameModeValues[1..^1]
            .Select(x => (GameMode: x, Color: Utils.GetRoleColorCode(CustomRoleSelector.GameModeRoles.TryGetValue(x, out CustomRoles role) ? role : x == CustomGameMode.HideAndSeek ? CustomRoles.Hider : CustomRoles.Witness, "#000000")))
            .Select(x => $"<{x.Color}><u><b>{GetString($"{x.GameMode}")}</b></u></color><size=75%>\n{GetString($"ModeDescribe.{x.GameMode}").Split("\n\n")[0]}</size>"));

        Utils.SendMessage(info, player.PlayerId, GetString("GameModeListTitle"));
    }

    private static void JailTalkCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !player.IsAlive()) return;

        Jailor jailor = Main.PlayerStates[player.PlayerId].Role as Jailor ?? Main.PlayerStates.Select(x => x.Value.Role as Jailor).FirstOrDefault(x => x != null);
        if (jailor == null || jailor.JailorTarget == byte.MaxValue) return;

        bool amJailor = Jailor.PlayerIdList.Contains(player.PlayerId);
        bool amJailed = player.PlayerId == jailor.JailorTarget;
        if (!amJailor && !amJailed) return;

        string title = CustomRoles.Jailor.ColoredTextByRole(GetString("JailTalkTitle"));

        string message = string.Join(' ', args[1..]);

        if (amJailor) Utils.SendMessage(message, jailor.JailorTarget, title, importance: MessageImportance.High);
        else Jailor.PlayerIdList.ForEach(x => Utils.SendMessage(message, x, title, importance: MessageImportance.Low));

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void RoleListCommand(PlayerControl player, string text, string[] args)
    {
        StringBuilder sb = new("<size=70%>");

        Dictionary<Team, RoleOptionType[]> rot = Main.RoleOptionTypeValues
            .Without(RoleOptionType.Coven_Miscellaneous)
            .GroupBy(x => x.ToString().Split('_')[0])
            .ToDictionary(x => Enum.Parse<Team>(x.Key), x => x.ToArray());

        foreach (Team team in Main.TeamValues[1..])
        {
            sb.Append("<u>");
            sb.Append(Utils.ColorString(team.GetColor(), GetString(team.ToString()).ToUpper()));
            sb.Append("</u>");

            int factionMin;
            int factionMax;

            if (Options.FactionMinMaxSettings.TryGetValue(team, out (OptionItem MinSetting, OptionItem MaxSetting) factionLimits))
            {
                factionMin = factionLimits.MinSetting.GetInt();
                factionMax = factionLimits.MaxSetting.GetInt();
            }
            else
            {
                factionMin = Math.Max(0, Main.NormalOptions.MaxPlayers - Options.FactionMinMaxSettings[Team.Neutral].MaxSetting.GetInt() - Options.FactionMinMaxSettings[Team.Impostor].MaxSetting.GetInt() - Options.FactionMinMaxSettings[Team.Coven].MaxSetting.GetInt());
                factionMax = Math.Max(0, Main.NormalOptions.MaxPlayers - Options.FactionMinMaxSettings[Team.Neutral].MinSetting.GetInt() - Options.FactionMinMaxSettings[Team.Impostor].MinSetting.GetInt() - Options.FactionMinMaxSettings[Team.Coven].MinSetting.GetInt());
            }

            sb.Append(' ');
            sb.Append(factionMin);
            sb.Append(" - ");
            sb.Append(factionMax);
            sb.Append("\n\n");

            if (team == Team.Neutral)
            {
                sb.Append(Options.MinNNKs.GetInt());
                sb.Append('-');
                sb.Append(Options.MaxNNKs.GetInt());
                sb.Append(' ');
                sb.Append(GetString("NeutralNonKillingRoles"));
                sb.Append("\n\n");
            }

            if (rot.TryGetValue(team, out RoleOptionType[] subCategories))
            {
                foreach (RoleOptionType subCategory in subCategories)
                {
                    if (Options.RoleSubCategoryLimits.TryGetValue(subCategory, out OptionItem[] limits) && limits[0].GetBool())
                    {
                        int min = limits[1].GetInt();
                        int max = limits[2].GetInt();

                        factionMin -= max;
                        factionMax -= min;

                        sb.Append(min);
                        sb.Append('-');
                        sb.Append(max);
                        sb.Append(' ');
                        sb.Append(Utils.ColorString(subCategory.GetRoleOptionTypeColor(), GetString($"ROT.{subCategory}")[2..]));
                        sb.Append('\n');
                    }
                }

                if (team != Team.Neutral && factionMax > 0)
                {
                    sb.Append(Math.Max(0, factionMin));
                    sb.Append('-');
                    sb.Append(factionMax);
                    sb.Append(' ');
                    sb.Append(GetString("RoleRateNoColor"));
                    sb.Append(' ');
                    sb.Append(GetString("Roles"));
                    sb.Append('\n');
                }

                sb.Append("\n\n");
            }
        }

        Utils.SendMessage("\n", player.PlayerId, sb.ToString().Trim() + "</size>");
    }

    private static void AnagramCommand(PlayerControl player, string text, string[] args)
    {
        if (!Options.EnableAnagramCommand.GetBool())
        {
            Utils.SendMessage("\n", player.PlayerId, GetString("AnagramDisabled"), importance: MessageImportance.Low);
            return;
        }

        Main.Instance.StartCoroutine(Main.GetRandomWord(CreateAnagram));
        return;

        void CreateAnagram(string word)
        {
            string scrambled = new(word.ToLower().ToCharArray().Shuffle());
            CurrentAnagram = word;
            byte sendTo = GameStates.InGame && !player.IsAlive() ? player.PlayerId : byte.MaxValue;
            Utils.SendMessage(string.Format(GetString("Anagram"), scrambled, word[0]), sendTo, GetString("AnagramTitle"));
        }
    }

    private static void SpectateCommand(PlayerControl player, string text, string[] args)
    {
        if (player.IsHost() && args.Length > 1 && byte.TryParse(args[1], out byte targetId))
        {
            PlayerControl pc = targetId.GetPlayer();
            if (pc == null) return;

            if (ForcedSpectators.Remove(targetId))
                Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("SpectateCommand.RemovedForcedSpectator"), targetId.ColoredPlayerName()));

            if (ForcedSpectators.Add(targetId))
                Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("SpectateCommand.ForcedSpectator"), targetId.ColoredPlayerName()));
            return;
        }

        if (Options.DisableSpectateCommand.GetBool())
        {
            Utils.SendMessage("\n", player.PlayerId, GetString("SpectateDisabled"), importance: MessageImportance.Low);
            return;
        }

        if (LastSpectators.Contains(player.PlayerId))
        {
            Utils.SendMessage("\n", player.PlayerId, GetString("SpectateCommand.WasSpectatingLastRound"));
            return;
        }

        if (Spectators.Remove(player.PlayerId))
        {
            Utils.SendMessage("\n", player.PlayerId, GetString("SpectateCommand.Removed"));
            return;
        }

        if (Spectators.Add(player.PlayerId))
            Utils.SendMessage("\n", player.PlayerId, GetString("SpectateCommand.Success"));
    }

    // 公式(Vanilla)鯖: ロビー whisper の unicast パケットが flood-clear の ClearChat broadcast と
    // 同一フレームで飛ぶと、host 発の broadcast + unicast = 2 reliable パケット/1frame を anti-cheat が
    // Hacking と誤検知して蹴る (2026-07-02 実測: 非モッドの単発 /w でホスト自身が落ちる)。flood title と
    // 同じ発想で共有タイムラインに乗せ、最低 0.2s ずらして ClearBroadcast のフレームから必ず外し、
    // 複数宛先 (/w 0,1,2) も 0.3s 間隔に直列化して 1frame 集中を防ぐ。
    private const float LobbyWhisperGapSeconds = 0.3f;
    private static float _nextLobbyWhisperSlot;

    private static void SendLobbyWhisper(byte targetId, string title, string msg)
    {
        float slot = Math.Max(Time.realtimeSinceStartup + 0.2f, _nextLobbyWhisperSlot);
        float delay = Math.Max(0.05f, slot - Time.realtimeSinceStartup);
        _nextLobbyWhisperSlot = slot + LobbyWhisperGapSeconds;

        LateTask.New(() =>
        {
            PlayerControl target = Utils.GetPlayerById(targetId);
            if (target == null || target.Data == null) return;
            int targetClientId = target.OwnerId;
            if (targetClientId < 0) return;

            PlayerControl sender = PlayerControl.LocalPlayer;
            if (sender == null || sender.Data == null) return;

            // 宛先がホスト自身だと自分宛 tag6 エンベロープを撃つことになる。RPC を組まずローカル表示だけ行う
            // (ChatUpdatePatch.SendMessage の clientId==-1 分岐 / Utils.cs:2118 の receiver.AmOwner 分岐と同型)。
            if (target.AmOwner)
            {
                if (!HudManager.InstanceExists) return;

                string selfName = Main.AllPlayerNames.GetValueOrDefault(sender.PlayerId, string.Empty);
                if (selfName.Length == 0) selfName = Utils.SafePlayerName(sender);

                if (selfName.Length == 0)
                    HudManager.Instance.Chat.AddChat(sender, msg);
                else
                {
                    sender.SetName(title);
                    HudManager.Instance.Chat.AddChat(sender, msg);
                    sender.SetName(selfName);
                }

                return;
            }

            try
            {
                CustomRpcSender w = CustomRpcSender.Create("WhisperCommand.Lobby", SendOption.Reliable);
                w.AutoStartRpc(sender.NetId, (byte)RpcCalls.SetName, targetClientId)
                    .Write(sender.Data.NetId)
                    .Write(title)
                    .EndRpc();
                w.AutoStartRpc(sender.NetId, (byte)RpcCalls.SendChat, targetClientId)
                    .Write(msg)
                    .EndRpc();
                w.AutoStartRpc(sender.NetId, (byte)RpcCalls.SetName, targetClientId)
                    .Write(sender.Data.NetId)
                    .Write(Main.AllPlayerNames.GetValueOrDefault(sender.PlayerId, string.Empty))
                    .EndRpc();
                w.SendMessage();

                // 生名戻しで消えた装飾名 (ホストタグ/虹色 Developer★) を flush 後に決定論的に再送して復元
                // する (Utils.SendMessage と同型の穴・同じ経路で対処)。
                Utils.ScheduleDecoratedNameRestore(sender);
            }
            catch (Exception ex) { Logger.Warn($"SendLobbyWhisper failed: {ex.Message}", "Whisper"); }
        }, delay, "SendLobbyWhisper", log: false);
    }

    private static void WhisperCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.IsAlive() || Silencer.ForSilencer.Contains(player.PlayerId)) return;

        if (Options.DisableWhisperCommand.GetBool())
        {
            Utils.SendMessage("\n", player.PlayerId, GetString("WhisperDisabled"), importance: MessageImportance.Low);
            return;
        }

        if (Magistrate.CallCourtNextMeeting)
        {
            Utils.SendMessage("\n", player.PlayerId, GetString("NoWhisperDuringCourt"), importance: MessageImportance.Low);
            return;
        }

        if (player.Is(CustomRoles.God))
        {
            Utils.SendMessage("\n", player.PlayerId, GetString("NoWhisperAsRole"), importance: MessageImportance.Low);
            return;
        }

        if (args.Length < 3) return;

        string coloredRole = CustomRoles.Listener.ToColoredString();
        PlayerControl[] listeners = CustomRoles.Listener.IsEnable() ? Main.EnumerateAlivePlayerControls().Where(x => x.Is(CustomRoles.Listener)).ToArray() : [];
        string[] ids = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        CustomRpcSender batchWriter = null;

        foreach (string id in ids)
        {
            if (!byte.TryParse(id, out byte targetId)) continue;

            if (Main.PlayerStates.TryGetValue(targetId, out PlayerState state)
                && (state.IsDead || state.SubRoles.Contains(CustomRoles.Shy))) continue;

            string fromName = player.PlayerId.ColoredPlayerName();
            string toName = targetId.ColoredPlayerName();

            string msg = args[2..].Join(delimiter: " ");
            string title = string.Format(GetString("WhisperTitle"), fromName, player.PlayerId);

            if (GameStates.IsLobby)
            {
                SendLobbyWhisper(targetId, title, msg);
                ChatUpdatePatch.LastMessages.Add((msg, targetId, title, Utils.TimeStamp));
                continue;
            }

            batchWriter = Utils.SendMessage(msg, targetId, title, writer: batchWriter, multiple: true, importance: MessageImportance.High);
            ChatUpdatePatch.LastMessages.Add((msg, targetId, title, Utils.TimeStamp));

            foreach (PlayerControl listener in listeners)
            {
                if (IRandom.Instance.Next(100) >= Listener.WhisperHearChance.GetInt()) continue;
                string message = IRandom.Instance.Next(100) < Listener.FullMessageHearChance.GetInt() ? string.Format(GetString("Listener.FullMessage"), coloredRole, fromName, toName, msg) : string.Format(GetString("Listener.FromTo"), coloredRole, fromName, toName);
                batchWriter = Utils.SendMessage("\n", listener.PlayerId, message, writer: batchWriter, multiple: true);

                if (listener.AmOwner && ++Listener.LocalPlayerHeardMessagesThisMeeting >= 3)
                    Achievements.Type.Eavesdropper.Complete();
            }
        }

        if (batchWriter != null && batchWriter.CurrentState != CustomRpcSender.State.Finished)
            batchWriter.SendMessage();

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void HWhisperCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 3 || !byte.TryParse(args[1], out byte targetId)) return;

        string msg = args[2..].Join(delimiter: " ");
        string title = string.Format(GetString("HWhisperTitle"), player.PlayerId.ColoredPlayerName());

        if (GameStates.IsLobby)
        {
            SendLobbyWhisper(targetId, title, msg);
            ChatUpdatePatch.LastMessages.Add((msg, targetId, title, Utils.TimeStamp));
            return;
        }

        Utils.SendMessage(msg, targetId, title, importance: MessageImportance.High);
        ChatUpdatePatch.LastMessages.Add((msg, targetId, title, Utils.TimeStamp));
    }

    private static void WordCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;
        WordKiller.SetWord(player, args);
    }

    private static void DeathNoteCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (!player.Is(CustomRoles.NoteKiller) || args.Length < 2) return;

        if (!NoteKiller.CanGuess)
        {
            Utils.SendMessage(GetString("DeathNoteCommand.CanNotGuess"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        string guess = args[1].ToLower();
        guess = char.ToUpper(guess[0]) + guess[1..];
        byte deadPlayer = NoteKiller.RealNames.GetKeyByValue(guess);

        if (deadPlayer == 0 && (!NoteKiller.RealNames.TryGetValue(0, out string name) || name != guess))
        {
            NoteKiller.CanGuess = false;
            RPC.PlaySoundRPC(player.PlayerId, Sounds.SabotageSound);
            Utils.SendMessage(GetString("DeathNoteCommand.WrongName"), player.PlayerId);
            return;
        }

        PlayerControl pc = deadPlayer.GetPlayer();

        if (pc == null || !pc.IsAlive())
        {
            NoteKiller.CanGuess = false;
            Utils.SendMessage(GetString("DeathNoteCommand.PlayerNotFoundOrDead"), player.PlayerId);
            return;
        }

        PlayerState state = Main.PlayerStates[pc.PlayerId];
        state.deathReason = PlayerState.DeathReason.Kill;
        state.RealKiller.ID = player.PlayerId;
        
        pc.RpcGuesserMurderPlayer();
        Utils.AfterPlayerDeathTasks(pc, true);

        string coloredName = deadPlayer.ColoredPlayerName();
        Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("DeathNoteCommand.Success"), coloredName), importance: MessageImportance.Low);
        Utils.SendMessage(string.Format(GetString("DeathNoteCommand.SuccessForOthers"), coloredName), importance: MessageImportance.High);

        NoteKiller.Kills++;
        
        if (player.AmOwner && NoteKiller.Kills >= 3)
            Achievements.Type.IKnowYourNames.CompleteAfterGameEnd();

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void AchievementsCommand(PlayerControl player, string text, string[] args)
    {
        Func<Achievements.Type, string> ToAchievementString = x => $"<b>{GetString($"Achievement.{x}")}</b> - {GetString($"Achievement.{x}.Description")}";

        Achievements.Type[] allAchievements = Enum.GetValues<Achievements.Type>();
        Achievements.Type[] union = Achievements.CompletedAchievements.Union(Achievements.WaitingAchievements).ToArray();
        var completedAchievements = $"<size=70%>{union.Join(ToAchievementString, "\n")}</size>";
        var incompleteAchievements = $"<size=70%>{allAchievements.Except(union).Join(ToAchievementString, "\n")}</size>";

        Utils.SendMessage(incompleteAchievements, player.PlayerId, GetString("IncompleteAchievementsTitle"));
        Utils.SendMessage(completedAchievements, player.PlayerId, GetString("CompletedAchievementsTitle") + $" <#00a5ff>(<#00ffa5>{union.Length}</color>/{allAchievements.Length})</color>");
    }

    private static void EnableAllRolesCommand(PlayerControl player, string text, string[] args)
    {
        Prompt.Show(
            GetString("Promt.EnableAllRoles"),
            () => Options.CustomRoleSpawnChances.Values.DoIf(x => x.GetValue() == 0, x => x.SetValue(1)),
            () => Utils.EnterQuickSetupRoles(false));
    }

    public static void ReadyCheckCommand(PlayerControl player, string text, string[] args)
    {
        Utils.SendMessage(GetString("ReadyCheckMessage"), title: GetString("ReadyCheckTitle"));
        ReadyPlayers = [player.PlayerId];
        ReadyPlayers.UnionWith(Spectators);
        if (ReadyCheckCountdown != null) Main.Instance.StopCoroutine(ReadyCheckCountdown);
        ReadyCheckCountdown = Main.Instance.StartCoroutine(Countdown());
        return;

        IEnumerator Countdown()
        {
            var timer = 30f;

            while (timer > 0f)
            {
                if (!GameStates.IsLobby) yield break;

                if (Main.EnumeratePlayerControls().Select(x => x.PlayerId).All(ReadyPlayers.Contains)) break;

                timer -= Time.deltaTime;
                yield return null;
            }

            byte[] notReadyPlayers = Main.EnumeratePlayerControls().Select(x => x.PlayerId).Except(ReadyPlayers).ToArray();

            if (notReadyPlayers.Length == 0)
                Utils.SendMessage("\n", player.PlayerId, GetString("EveryoneReadyTitle"));
            else
                Utils.SendMessage(string.Join(", ", notReadyPlayers.Select(x => x.ColoredPlayerName())), player.PlayerId, string.Format(GetString("PlayersNotReadyTitle"), notReadyPlayers.Length));

            if (Spectators.Count > 0) Utils.SendMessage(string.Join(", ", Spectators.Select(x => x.ColoredPlayerName())), player.PlayerId, string.Format(GetString("SpectatorsList"), Spectators.Count));
        }
    }

    private static void ReadyCommand(PlayerControl player, string text, string[] args)
    {
        ReadyPlayers.Add(player.PlayerId);
    }
    
    public static void DraftStartCommand(PlayerControl player, string text, string[] args)
    {
        CustomGameMode gameMode = Options.CurrentGameMode;
        
        if (gameMode is not (CustomGameMode.Standard or CustomGameMode.HideAndSeek)) return;

        DraftResult = [];

        byte[] allPlayerIds = Main.EnumeratePlayerControls().Select(x => x.PlayerId).ToArray();
        int maxRolesPerPlayer = Options.DraftMaxRolesPerPlayer.GetInt();

        if (gameMode == CustomGameMode.HideAndSeek)
        {
            List<(CustomRoles Role, IHideAndSeekRole Interface)> hnsRoles = CustomHnS.GetAllHnsRoleTypes().Select(x => (Role: Enum.Parse<CustomRoles>(ignoreCase: true, value: x.Name), Interface: (IHideAndSeekRole)Activator.CreateInstance(x))).Where(x => x.Role is CustomRoles.Seeker or CustomRoles.Hider || x.Role.GetMode() != 0).ToList();
            Dictionary<Team, int> memberNum = new()
            {
                [Team.Impostor] = Main.NormalOptions.NumImpostors,
                [Team.Neutral] = CustomHnS.RandomNeutralsNum
            };

            foreach ((Team team, int num) in memberNum)
            {
                var suitableRoles = hnsRoles.FindAll(x => x.Interface.Team == team);
                if (suitableRoles.Count == 0) continue;
                allPlayerIds.Shuffle().Where(x => !DraftRoles.ContainsKey(x)).Take(num).Do(x => DraftRoles[x] = [suitableRoles.RandomElement().Role]);
            }
            
            var hiderRoles = hnsRoles.FindAll(x => x.Interface.Team == Team.Crewmate);
            if (hiderRoles.Count == 0) return;

            while (true)
            {
                if (DraftRoles.Values.FindFirst(x => x.Count < maxRolesPerPlayer, out List<CustomRoles> roles))
                    roles.Add(hiderRoles.RandomElement().Role);
                else if (allPlayerIds.FindFirst(x => !DraftRoles.ContainsKey(x), out byte id))
                    DraftRoles[id] = [hiderRoles.RandomElement().Role];
                else
                    break;
            }

            foreach (List<CustomRoles> rolesList in DraftRoles.Values)
            {
                HashSet<CustomRoles> seen = [];
                rolesList.RemoveAll(r => !seen.Add(r));
            }
            
            Main.Instance.StartCoroutine(RepeatedlySendMessage());
            return;
        }
        
        bool rollSpawnChance = Options.DraftAffectedByRoleSpawnChances.GetBool();
        bool includeNonCrew = Options.DraftIncludesNonCrewRoles.GetBool();
        List<CustomRoles> allRoles = Main.CustomRoleValues.Where(x => x < CustomRoles.NotAssigned && x.IsEnable() && !x.IsForOtherGameMode() && !CustomHnS.AllHnSRoles.Contains(x) && !x.IsVanilla() && x is not CustomRoles.GM && !ShouldNotSpawn(x) && (!rollSpawnChance || IRandom.Instance.Next(100) < x.GetMode())).Shuffle();

        if (allRoles.Count < allPlayerIds.Length)
        {
            Utils.SendMessage(GetString("DraftNotEnoughRoles"), player.PlayerId);
            return;
        }

        // NK サブカテゴリ上限は Enable 時のみ効かせる (無効時は Neutral 陣営 Max のみで制御)。
        OptionItem[] nkLimits = Options.RoleSubCategoryLimits[RoleOptionType.Neutral_Killing];
        int neutralFactionMax = Options.FactionMinMaxSettings[Team.Neutral].MaxSetting.GetInt();
        int nkReserved = nkLimits[0].GetBool() ? nkLimits[2].GetInt() : 0;

        List<CustomRoles> impRoles = includeNonCrew ? allRoles.Where(x => x.IsImpostor()).Shuffle().Take(Options.FactionMinMaxSettings[Team.Impostor].MaxSetting.GetInt()).ToList() : [];
        List<CustomRoles> nkRoles = includeNonCrew ? allRoles.Where(x => x.IsNK()).Shuffle().Take(Math.Min(neutralFactionMax, Options.GetNeutralKillingMaxLimit())).ToList() : [];
        List<CustomRoles> nnkRoles = includeNonCrew ? allRoles.Where(x => x.IsNonNK()).Shuffle().Take(Math.Min(neutralFactionMax - nkReserved, Options.MaxNNKs.GetInt())).ToList() : [];
        List<CustomRoles> covenRoles = includeNonCrew ? allRoles.Where(x => x.IsCoven()).Shuffle().Take(Options.FactionMinMaxSettings[Team.Coven].MaxSetting.GetInt()).ToList() : [];

        allRoles.RemoveAll(x => x.IsImpostor());
        allRoles.RemoveAll(x => x.IsNK());
        allRoles.RemoveAll(x => x.IsNonNK());
        allRoles.RemoveAll(x => x.IsCoven());

        int factionCount = impRoles.Count + nkRoles.Count + nnkRoles.Count + covenRoles.Count;
        DraftRoles = allRoles
            .Take(allPlayerIds.Length * maxRolesPerPlayer - factionCount)
            .CombineWith(impRoles, nkRoles, nnkRoles, covenRoles)
            .Shuffle()
            .Partition(allPlayerIds.Length)
            .Zip(allPlayerIds)
            .ToDictionary(x => x.Second, x => x.First.Take(maxRolesPerPlayer).ToList());

        Main.Instance.StartCoroutine(RepeatedlySendMessage());
        return;

        IEnumerator RepeatedlySendMessage()
        {
            for (var index = 0; index < 3; index++)
            {
                if (Options.CurrentGameMode is not (CustomGameMode.Standard or CustomGameMode.HideAndSeek))
                {
                    DraftRoles = [];
                    DraftResult = [];
                    yield break;
                }
                
                List<Message> messages = [];

                foreach ((byte id, List<CustomRoles> roles) in DraftRoles)
                {
                    if (DraftResult.ContainsKey(id)) continue;
                    IEnumerable<string> roleList = roles.Select((x, i) => $"{i + 1}. {x.ToColoredString()}");
                    string msg = string.Format(GetString(index == 0 ? "DraftStart" : "DraftResend"), string.Join('\n', roleList));
                    messages.Add(new Message(msg, id, GetString("DraftTitle")));
                }

                messages.SendMultipleMessages(index == 0 ? MessageImportance.Medium : MessageImportance.Low);

                yield return new WaitForSecondsRealtime(20f);
                if (DraftResult.Count >= DraftRoles.Count || !GameStates.IsLobby || GameStates.InGame) yield break;
            }
        }
        
        static bool ShouldNotSpawn(CustomRoles role)
        {
            return role switch
            {
                CustomRoles.Weatherman when Main.LIMap || GameStates.CurrentServerType == GameStates.ServerType.Vanilla => true,

                CustomRoles.RoomRusher when Main.LIMap => true,
                CustomRoles.Doctor when Options.EveryoneSeesDeathReasons.GetBool() => true,
                CustomRoles.Commander when Main.NormalOptions.NumImpostors <= 1 && Commander.CannotSpawnAsSoloImp.GetBool() => true,
                CustomRoles.Changeling when Changeling.GetAvailableRoles(true).Count == 0 => true,
                _ => false
            };
        }
    }

    private static void DraftDescriptionCommand(PlayerControl player, string text, string[] args)
    {
        CustomRoles role;

        try // Sometimes a System.ArgumentOutOfRangeException occurs here
        {
            if (DraftRoles.Count == 0 || !DraftRoles.TryGetValue(player.PlayerId, out List<CustomRoles> roles) || args.Length < 2 || !int.TryParse(args[1], out int chosenIndex) || chosenIndex <= 0 || roles.Count < chosenIndex) return;

            role = roles[chosenIndex - 1];
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return;
        }

        string coloredString = role.ToColoredString();
        string roleName = GetString(role.ToString());
        StringBuilder sb = new();
        StringBuilder settings = new();
        var title = $"{coloredString} {Utils.GetRoleMode(role)}";
        sb.Append(GetString($"{role}InfoLong").FixRoleName(role).TrimStart());
        if (Options.CustomRoleSpawnChances.TryGetValue(role, out StringOptionItem chance)) AddSettings(chance);
        if (role is CustomRoles.LovingCrewmate or CustomRoles.LovingImpostor && Options.CustomRoleSpawnChances.TryGetValue(CustomRoles.Lovers, out chance)) AddSettings(chance);
        string txt = sb.ToString().Replace(roleName, coloredString).Replace(roleName.ToLower(), coloredString);
        sb.Clear().Append(txt);
        if (settings.Length > 0) Utils.SendMessage("\n", player.PlayerId, settings.ToString());
        Utils.SendMessage(sb.ToString(), player.PlayerId, title);
        return;

        void AddSettings(StringOptionItem stringOptionItem)
        {
            settings.AppendLine($"<size=70%><u>{GetString("SettingsForRoleText")} <{Utils.GetRoleColorCode(role)}>{roleName}</color>:</u>");
            Utils.ShowChildrenSettings(stringOptionItem, settings, disableColor: false);
            settings.Append("</size>");
        }
    }

    private static void DraftCommand(PlayerControl player, string text, string[] args)
    {
        if (DraftRoles.Count == 0 || !DraftRoles.TryGetValue(player.PlayerId, out List<CustomRoles> roles) || args.Length < 2 || !int.TryParse(args[1], out int chosenIndex)) return;

        if (roles.Count < chosenIndex || chosenIndex < 1)
        {
            DraftResult.Remove(player.PlayerId);
            Utils.SendMessage(string.Format(GetString("DraftChosen"), GetString("pet_RANDOM_FOR_EVERYONE")), player.PlayerId, GetString("DraftTitle"));
            return;
        }

        CustomRoles role = roles[chosenIndex - 1];
        DraftResult[player.PlayerId] = role;
        Utils.SendMessage(string.Format(GetString("DraftChosen"), role.ToColoredString()), player.PlayerId, GetString("DraftTitle"));

        if (DraftResult.Count >= DraftRoles.Count) Utils.SendMessage("\n", PlayerControl.LocalPlayer.PlayerId, GetString("EveryoneDrafted"));
    }

    private static void MuteCommand(PlayerControl player, string text, string[] args)
    {
        bool host = player.IsHost();
        if (!host && (GameStates.InGame || MutedPlayers.ContainsKey(player.PlayerId))) return;
        if (!byte.TryParse(args[1], out byte id) || id.IsHost() || (!host && IsPlayerModerator(id.GetPlayer()?.FriendCode))) return;

        long now = Utils.TimeStamp;
        int duration = args.Length < 3 || !int.TryParse(args[2], out int dur) ? 60 : dur;
        MutedPlayers[id] = (now, duration, now);

        List<Message> messages =
        [
            new("\n", player.PlayerId, string.Format(GetString("PlayerMuted"), id.ColoredPlayerName(), duration)),
            new("\n", id, string.Format(GetString("YouMuted"), player.PlayerId.ColoredPlayerName(), duration))
        ];
        if (!host) messages.Add(new Message("\n", 0, string.Format(GetString("ModeratorMuted"), player.PlayerId.ColoredPlayerName(), id.ColoredPlayerName(), duration)));
        messages.SendMultipleMessages();
    }

    private static void UnmuteCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !byte.TryParse(args[1], out byte id)) return;

        MutedPlayers.Remove(id);
        Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("PlayerUnmuted"), id.ColoredPlayerName()));
        Utils.SendMessage("\n", id, string.Format(GetString("YouUnmuted"), player.PlayerId.ColoredPlayerName()));
        if (!player.IsHost()) Utils.SendMessage("\n", 0, string.Format(GetString("AdminUnmuted"), player.PlayerId.ColoredPlayerName(), id.ColoredPlayerName()));
    }

    private static void NegotiationCommand(PlayerControl player, string text, string[] args)
    {
        if (!Negotiator.On || !player.IsAlive() || args.Length < 2 || !int.TryParse(args[1], out int index)) return;

        Negotiator.ReceiveCommand(player, index);

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void OSCommand(PlayerControl player, string text, string[] args)
    {
        if (!GameStates.IsLobby || args.Length < 3 || !byte.TryParse(args[1], out byte chance) || chance > 100 || chance % 5 != 0 || !GetRoleByName(string.Join(' ', args[2..]), out CustomRoles role) || !Options.CustomRoleSpawnChances.TryGetValue(role, out StringOptionItem option)) return;

        if (role.IsAdditionRole())
        {
            option.SetValue(chance == 0 ? 0 : 1);
            if (!Options.CustomAdtRoleSpawnRate.TryGetValue(role, out IntegerOptionItem adtOption)) return;
            adtOption.SetValue(chance / 5);
        }
        else
            option.SetValue(chance / 5);
    }

    private static void NoteCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (!player.Is(CustomRoles.Journalist) || !player.IsAlive()) return;

        Journalist.OnReceiveCommand(player, args);

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void AssumeCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (args.Length < 3 || !byte.TryParse(args[1], out byte id) || !int.TryParse(args[2], out int num) || !player.Is(CustomRoles.Assumer) || !player.IsAlive()) return;

        Assumer.Assume(player.PlayerId, id, num);

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void DeleteVIPCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !byte.TryParse(args[1], out byte VIPId)) return;

        PlayerControl VIPPc = Utils.GetPlayerById(VIPId);
        if (VIPPc == null) return;

        string fc = VIPPc.FriendCode.Replace(':', '#');
        if (!IsPlayerVIP(fc)) Utils.SendMessage(GetString("PlayerNotVIP"), player.PlayerId);

        string[] lines = File.ReadAllLines($"{Main.DataPath}/EndKnot_DATA/VIPs.txt").Where(line => !line.Contains(fc)).ToArray();
        File.WriteAllLines($"{Main.DataPath}/EndKnot_DATA/VIPs.txt", lines);
        Utils.SendMessage(GetString("PlayerRemovedFromVIPList"), player.PlayerId);
    }

    private static void AddVIPCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !byte.TryParse(args[1], out byte newVIPId)) return;

        PlayerControl newVIPPc = Utils.GetPlayerById(newVIPId);
        if (newVIPPc == null) return;

        string fc = newVIPPc.FriendCode.Replace(':', '#');
        if (IsPlayerVIP(fc)) Utils.SendMessage(GetString("PlayerAlreadyVIP"), player.PlayerId);

        File.AppendAllText($"{Main.DataPath}/EndKnot_DATA/VIPs.txt", $"\n{fc}");
        Utils.SendMessage(GetString("PlayerAddedToVIPList"), player.PlayerId);
    }

    private static void DecreeCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (!player.Is(CustomRoles.President)) return;

        LateTask.New(() =>
        {
            if (args.Length < 2)
            {
                Utils.SendMessage(President.GetHelpMessage(), player.PlayerId, importance: MessageImportance.High);
                return;
            }

            President.UseDecree(player, args[1]);
        }, 0.2f, log: false);
    }

    private static void HMCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.Is(CustomRoles.Messenger) || Messenger.Sent.Contains(player.PlayerId) || args.Length < 2 || !int.TryParse(args[1], out int id) || id is > 3 or < 1) return;

        Main.Instance.StartCoroutine(SendOnMeeting());
        return;

        IEnumerator SendOnMeeting()
        {
            bool meeting = GameStates.IsMeeting;
            while (!GameStates.IsMeeting && GameStates.InGame) yield return null;
            if (!GameStates.InGame) yield break;

            if (!meeting) yield return new WaitForSecondsRealtime(7f);

            PlayerControl killer = player.GetRealKiller();
            if (!killer && id != 3) yield break;

            Team team = player.GetTeam();

            string message = id switch
            {
                1 => string.Format(GetString("MessengerMessage.1"), GetString((Main.PlayerStates[killer.PlayerId].LastRoom?.RoomId ?? SystemTypes.Outside).ToString())),
                2 => string.Format(GetString("MessengerMessage.2"), killer.GetCustomRole().ToColoredString()),
                _ => string.Format(GetString("MessengerMessage.3"), Utils.ColorString(team.GetColor(), GetString($"{team}")))
            };

            Utils.SendMessage(message, title: string.Format(GetString("MessengerTitle"), player.PlayerId.ColoredPlayerName()), importance: MessageImportance.High);
            Messenger.Sent.Add(player.PlayerId);
        }
    }

    // Credit: Drakos for the base code
    private static void PollCommand(PlayerControl player, string text, string[] args)
    {
        PollVotes.Clear();
        PollAnswers.Clear();
        PollVoted.Clear();

        if (!args.Any(x => x.Contains('?')))
        {
            Utils.SendMessage(GetString("PollUsage"), player.PlayerId);
            return;
        }

        int splitIndex = Array.IndexOf(args, args.First(x => x.Contains('?'))) + 1;
        string[] answers = args[splitIndex..];

        string msg = string.Join(" ", args[1..splitIndex]) + "\n";
        bool gmPoll = msg.Contains(GetString("GameModePoll.Question"));
        bool mPoll = msg.Contains(GetString("MapPoll.Question"));
        
        if (gmPoll && GMPollGameModes.Count > 6) msg += "<size=70%>";

        PollTimer = gmPoll ? 60f : 45f;
        Color[] gmPollColors = gmPoll ? Main.GameModeColors.Where(x => GMPollGameModes.Contains(x.Key)).Select(x => x.Value).ToArray() : [];
        

        for (var i = 0; i < Math.Max(answers.Length, 2); i++)
        {
            var choiceLetter = (char)(i + 65);
            msg += Utils.ColorString(gmPoll ? gmPollColors[i] : RandomColor(), $"{char.ToUpper(choiceLetter)}) {answers[i]}\n");
            PollVotes[choiceLetter] = 0;
            PollAnswers[choiceLetter] = $"〖 {answers[i]} 〗";
        }

        msg += $"\n{GetString("Poll.Begin")}\n<size=60%><i>";
        string title = GetString("Poll.Title");
        Utils.SendMessage(msg + $"{string.Format(GetString("Poll.TimeInfo"), (int)Math.Round(PollTimer))}</i></size>", title: title);

        Main.Instance.StartCoroutine(StartPollCountdown());
        return;

        IEnumerator StartPollCountdown()
        {
            if (PollVotes.Count == 0) yield break;

            bool notEveryoneVoted = PlayerControl.AllPlayerControls.Count - 1 > PollVotes.Values.Sum();

            var resendTimer = 0f;

            while ((notEveryoneVoted || gmPoll || mPoll) && PollTimer > 0f)
            {
                if (!GameStates.IsLobby) yield break;

                notEveryoneVoted = PlayerControl.AllPlayerControls.Count - 1 > PollVotes.Values.Sum();
                PollTimer -= Time.deltaTime;
                resendTimer += Time.deltaTime;

                if (resendTimer > 23f)
                {
                    resendTimer = 0f;
                    Utils.SendMessage(msg + $"{string.Format(GetString("Poll.TimeInfo"), (int)Math.Round(PollTimer))}</i></size>", title: title, importance: MessageImportance.Low);
                }

                yield return null;
            }

            DetermineResults();
        }

        void DetermineResults()
        {
            int maxVotes = PollVotes.Values.Max();
            KeyValuePair<char, int>[] winners = PollVotes.Where(x => x.Value == maxVotes).ToArray();

            string result = winners.Length == 1
                ? string.Format(GetString("Poll.Winner"), winners[0].Key, PollAnswers[winners[0].Key], winners[0].Value) +
                  PollVotes.Where(x => x.Key != winners[0].Key).Aggregate("<size=70%>", (s, t) => s + $"{t.Key} - {t.Value} {PollAnswers[t.Key]}\n")
                : string.Format(GetString("Poll.Tie"), string.Join(" & ", winners.Select(x => $"{x.Key}{PollAnswers[x.Key]}")), maxVotes);

            Utils.SendMessage(result, title: Utils.ColorString(new(0, 255, 165, 255), GetString("PollResultTitle")));

            PollVotes.Clear();
            PollAnswers.Clear();
            PollVoted.Clear();

            if (winners.Length is > 0 and < 4 && GameStates.IsLobby)
            {
                int winnerIndex = (winners.Length == 1 ? winners[0].Key : winners.RandomElement().Key) - 65;
                if (gmPoll) Options.GameMode.SetValue((int)GMPollGameModes[winnerIndex] - 1, doSave: true, doSync: true);
                if (mPoll) Main.NormalOptions.MapId = (byte)winnerIndex;
            }
        }

        static Color32 RandomColor()
        {
            byte[] colors = IRandom.Sequence(3, 0, 160).Select(x => (byte)x).ToArray();
            return new(colors[0], colors[1], colors[2], 255);
        }
    }

    private static void PVCommand(PlayerControl player, string text, string[] args)
    {
        if (PollVotes.Count == 0)
        {
            Utils.SendMessage(GetString("Poll.Inactive"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        if (PollVoted.Contains(player.PlayerId))
        {
            Utils.SendMessage(GetString("Poll.AlreadyVoted"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        if (args.Length != 2 || !char.TryParse(args[1], out char vote) || !PollVotes.ContainsKey(char.ToUpper(vote)))
        {
            Utils.SendMessage(GetString("Poll.VotingInfo"), player.PlayerId);
            return;
        }

        vote = char.ToUpper(vote);

        PollVoted.Add(player.PlayerId);
        PollVotes[vote]++;
        Utils.SendMessage(string.Format(GetString("Poll.YouVoted"), vote, PollVotes[vote]), player.PlayerId);
    }

    private static void HelpCommand(PlayerControl player, string text, string[] args)
    {
        if (TryRoleSearchSubCommand(player, args)) return;

        Utils.ShowHelp(player.PlayerId);
    }

    private static void DumpCommand(PlayerControl player, string text, string[] args)
    {
        Utils.DumpLog();
    }

    private static void GNOCommand(PlayerControl player, string text, string[] args)
    {
        if (!GameStates.IsLobby && player.IsAlive())
        {
            Utils.SendMessage(GetString("GNoCommandInfo"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        string subArgs = args.Length != 2 ? "" : args[1];

        if (subArgs == "" || !int.TryParse(subArgs, out int guessedNo) || guessedNo is < 0 or > 99)
        {
            Utils.SendMessage(GetString("GNoCommandInfo"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        int targetNumber = Main.GuessNumber[player.PlayerId][0];

        if (Main.GuessNumber[player.PlayerId][0] == -1)
        {
            var rand = IRandom.Instance;
            Main.GuessNumber[player.PlayerId][0] = rand.Next(0, 100);
            targetNumber = Main.GuessNumber[player.PlayerId][0];
        }

        Main.GuessNumber[player.PlayerId][1]--;

        if (Main.GuessNumber[player.PlayerId][1] == 0 && guessedNo != targetNumber)
        {
            Main.GuessNumber[player.PlayerId][0] = -1;
            Main.GuessNumber[player.PlayerId][1] = 7;
            Utils.SendMessage(string.Format(GetString("GNoLost"), targetNumber), player.PlayerId);
            return;
        }

        if (guessedNo < targetNumber)
        {
            Utils.SendMessage(string.Format(GetString("GNoLow"), Main.GuessNumber[player.PlayerId][1]), player.PlayerId);
            return;
        }

        if (guessedNo > targetNumber)
        {
            Utils.SendMessage(string.Format(GetString("GNoHigh"), Main.GuessNumber[player.PlayerId][1]), player.PlayerId);
            return;
        }

        Utils.SendMessage(string.Format(GetString("GNoWon"), Main.GuessNumber[player.PlayerId][1]), player.PlayerId);
        Main.GuessNumber[player.PlayerId][0] = -1;
        Main.GuessNumber[player.PlayerId][1] = 7;
    }

    private static void SDCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[1], out int sound1)) return;
        RPC.PlaySound(player.PlayerId, (Sounds)sound1);
    }

    private static void CSDCommand(PlayerControl player, string text, string[] args)
    {
        string subArgs = text.Remove(0, 3);
        CustomSoundsManager.Play(subArgs.Trim());
    }

    private static void MTHYCommand(PlayerControl player, string text, string[] args)
    {
        if (GameStates.IsMeeting)
        {
            MeetingHudRpcClosePatch.AllowClose = true;
            MeetingHud.Instance.RpcClose();
        }
        else
        {
            // /mt <playerId> はそのプレイヤーの死体が通報されたことにして会議を開く (生死不問)。
            // 通報会議限定の能力 (ShrineMaiden 等) をホストだけでテストするための入り口。
            NetworkedPlayerInfo reportTarget = null;
            if (args.Length >= 2 && byte.TryParse(args[1], out byte reportId)) reportTarget = Utils.GetPlayerById(reportId)?.Data;
            player.NoCheckStartMeeting(reportTarget, true);
        }
    }

    private static void CosIDCommand(PlayerControl player, string text, string[] args)
    {
        PlayerControl target = player;
        if (args.Length >= 2 && byte.TryParse(args[1], out byte targetId))
        {
            PlayerControl resolved = Utils.GetPlayerById(targetId);
            if (resolved != null) target = resolved;
        }

        NetworkedPlayerInfo.PlayerOutfit of = target.Data.DefaultOutfit;
        Logger.Warn($"[{target.Data.PlayerName}] ColorId: {of.ColorId}", "Get Cos Id");
        Logger.Warn($"PetId: {of.PetId}", "Get Cos Id");
        Logger.Warn($"HatId: {of.HatId}", "Get Cos Id");
        Logger.Warn($"SkinId: {of.SkinId}", "Get Cos Id");
        Logger.Warn($"VisorId: {of.VisorId}", "Get Cos Id");
        Logger.Warn($"NamePlateId: {of.NamePlateId}", "Get Cos Id");
        Utils.SendMessage(
            $"[{target.Data.PlayerName}]\nColorId: {of.ColorId}\nPetId: {of.PetId}\nHatId: {of.HatId}\nSkinId: {of.SkinId}\nVisorId: {of.VisorId}\nNamePlateId: {of.NamePlateId}",
            player.PlayerId);
    }

    private static void EndCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.IsHost() && !IsPlayerAdmin(player.FriendCode)) return;

        CustomWinnerHolder.ResetAndSetWinner(CustomWinner.Draw);
        GameManager.Instance.LogicFlow.CheckEndCriteria();
    }

    private static void ChangeRoleCommand(PlayerControl player, string text, string[] args)
    {
        if (GameStates.IsLobby || (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev())) return;

        string subArgs = string.Join(' ', args[1..]);
        if (!GetRoleByName(subArgs, out CustomRoles rl)) return;

        if (!rl.IsAdditionRole()) player.SetRole(rl.GetRoleTypes());

        player.RpcSetCustomRole(rl);
        player.RpcChangeRoleBasis(rl);

        if (rl.IsGhostRole()) GhostRolesManager.SpecificAssignGhostRole(player.PlayerId, rl, true);

        Main.PlayerStates[player.PlayerId].RemoveSubRole(CustomRoles.NotAssigned);
    }

    private static void IDCommand(PlayerControl player, string text, string[] args)
    {
        string msgText = GetString("PlayerIdList");
        msgText = Main.EnumeratePlayerControls().Aggregate(msgText, (current, pc) => $"{current}\n{pc.PlayerId} \u2192 {pc.GetRealName()}");

        Utils.SendMessage(msgText, player.PlayerId);
    }

    private static void ColorCommand(PlayerControl player, string text, string[] args)
    {
        if (GameStates.IsInGame)
        {
            Utils.SendMessage(GetString("Message.OnlyCanUseInLobby"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        if (!player.IsHost() && !Options.PlayerCanSetColor.GetBool() && !IsPlayerVIP(player.FriendCode) && !player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev())
        {
            Utils.SendMessage(GetString("DisableUseCommand"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        string subArgs = args.Length < 2 ? string.Empty : args[1];
        byte color = Utils.MsgToColor(subArgs, player.IsHost());

        if (color == byte.MaxValue)
        {
            Utils.SendMessage(GetString("IllegalColor"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        player.RpcChangeColor(color); // 公式鯖では spoof RPC ではなく正規 serialize で色を同期 (anti-cheat 修正後)
        Utils.SendMessage(string.Format(GetString("Message.SetColor"), subArgs), player.PlayerId, importance: MessageImportance.Low);
    }

    private static void KillCommand(PlayerControl player, string text, string[] args)
    {
        if (GameStates.IsLobby)
        {
            Utils.SendMessage(GetString("Message.CanNotUseInLobby"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        if (args.Length < 2 || !int.TryParse(args[1], out int id2)) return;

        PlayerControl target = Utils.GetPlayerById(id2);

        if (target != null)
        {
            target.Kill(target);

            if (target.AmOwner)
                Utils.SendMessage(GetString("HostKillSelfByCommand"), title: $"<color=#ff0000>{GetString("DefaultSystemMessageTitle")}</color>");
            else
                Utils.SendMessage(string.Format(GetString("Message.Executed"), target.Data.PlayerName));
        }
    }

    private static void ExeCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int id)) return;

        PlayerControl pc = Utils.GetPlayerById(id);
        if (pc == null) return;

        if (GameStates.IsLobby)
        {
            if (pc.AmOwner)
            {
                Utils.SendMessage(GetString("HostKillSelfByCommand"), player.PlayerId, title: $"<color=#ff0000>{GetString("DefaultSystemMessageTitle")}</color>");
                return;
            }

            pc.RpcExileV2();
            pc.Data.IsDead = true;
            Utils.SendMessage(string.Format(GetString("Message.Executed"), pc.Data.PlayerName));
            return;
        }

        Main.PlayerStates[pc.PlayerId].deathReason = PlayerState.DeathReason.etc;
        pc.RpcExileV2();
        pc.Data.IsDead = true;
        Main.PlayerStates[pc.PlayerId].SetDead();
        Utils.AfterPlayerDeathTasks(pc, GameStates.IsMeeting);

        if (pc.AmOwner)
            Utils.SendMessage(GetString("HostKillSelfByCommand"), title: $"<color=#ff0000>{GetString("DefaultSystemMessageTitle")}</color>");
        else
            Utils.SendMessage(string.Format(GetString("Message.Executed"), pc.Data.PlayerName));
    }

    private static void BanKickCommand(PlayerControl player, string text, string[] args)
    {
        // Check if the Kick command is enabled in the settings
        if (!Options.ApplyModeratorList.GetBool() && !player.IsHost())
        {
            Utils.SendMessage(GetString("KickCommandDisabled"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        // Check if the Player has the necessary privileges to use the command
        if (!IsPlayerModerator(player.FriendCode) && !player.IsHost())
        {
            Utils.SendMessage(GetString("KickCommandNoAccess"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        string subArgs = args.Length < 2 ? string.Empty : args[1];

        if (string.IsNullOrWhiteSpace(subArgs) || !byte.TryParse(subArgs, out byte kickPlayerId))
        {
            Utils.SendMessage(GetString("KickCommandInvalidID"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        if (kickPlayerId.IsHost())
        {
            Utils.SendMessage(GetString("KickCommandKickHost"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        PlayerControl kickedPlayer = Utils.GetPlayerById(kickPlayerId);

        if (kickedPlayer == null)
        {
            Utils.SendMessage(GetString("KickCommandInvalidID"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        // Prevent Moderators from kicking other Moderators
        if (IsPlayerModerator(kickedPlayer.FriendCode) && !player.IsHost())
        {
            Utils.SendMessage(GetString("KickCommandKickMod"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        try
        {
            string kickedPlayerName = kickedPlayer.GetRealName();
            var textToSend = $"{kickedPlayerName} {GetString("KickCommandKicked")}";
            if (GameStates.IsInGame) textToSend += string.Format(GetString("KickCommandKickedRole"), kickedPlayer.GetCustomRole().ToColoredString());
            if (args.Length >= 3) textToSend += $"\n{GetString("KickCommandKickedReason")} {string.Join(' ', args[2..])}";

            Utils.SendMessage(textToSend, importance: GameStates.IsInGame ? MessageImportance.Medium : MessageImportance.Low);
        
            string modLogFilePath = $"{Main.DataPath}/EndKnot_DATA/ModLogs/{DateTime.Now:yyyy-MM-dd}.txt";
        
            if (!File.Exists(modLogFilePath))
            {
                string directoryName = Path.GetDirectoryName(modLogFilePath);
                if (!string.IsNullOrWhiteSpace(directoryName)) Directory.CreateDirectory(directoryName);
                File.WriteAllText(modLogFilePath, "=== Moderation Log ===\n");
            }
        
            string logEntry = $"[{DateTime.Now:HH:mm:ss}] {player.GetRealName()} {(args[0] == "/ban" ? "banned" : "kicked")} {kickedPlayerName} [{kickedPlayer.FriendCode}|{kickedPlayer.GetClient().GetHashedPuid()}] for {(args.Length >= 3 ? string.Join(' ', args[2..]) : "[no reason provided]")}\n";
            File.AppendAllText(modLogFilePath, logEntry);
        }
        catch (Exception e) { Utils.ThrowException(e); }
        
        AmongUsClient.Instance.KickPlayer(kickedPlayer.OwnerId, args[0] == "/ban");
    }

    private static void CheckCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (!player.IsAlive() || !player.Is(CustomRoles.Inquirer) || player.GetAbilityUseLimit() < 1) return;

        if (args.Length < 3 || !GuessManager.MsgToPlayerAndRole(text[6..], out byte checkId, out CustomRoles checkRole, out _)) return;

        bool hasRole = Utils.GetPlayerById(checkId).Is(checkRole);
        if (IRandom.Instance.Next(100) < Inquirer.FailChance.GetInt()) hasRole = !hasRole;

        LateTask.New(() => Utils.SendMessage(GetString(hasRole ? "Inquirer.MessageTrue" : "Inquirer.MessageFalse"), player.PlayerId, importance: MessageImportance.High), 0.2f, log: false);
        player.RpcRemoveAbilityUse();

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void ChatCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (!Ventriloquist.On || !player.IsAlive() || !player.Is(CustomRoles.Ventriloquist) || player.PlayerId.GetAbilityUseLimit() < 1) return;

        var vl2 = (Ventriloquist)Main.PlayerStates[player.PlayerId].Role;
        if (vl2.Target == byte.MaxValue) return;

        PlayerControl tg = Utils.GetPlayerById(vl2.Target);
        string msg = text[6..];
        LateTask.New(() => tg?.RpcSendChat(msg), 0.2f, log: false);
        ChatManager.AddChatHistory(tg, msg);

        player.RpcRemoveAbilityUse();

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    public static void TargetCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (!Ventriloquist.On || !player.IsAlive() || !player.Is(CustomRoles.Ventriloquist) || player.PlayerId.GetAbilityUseLimit() < 1) return;

        var vl = (Ventriloquist)Main.PlayerStates[player.PlayerId].Role;
        vl.Target = args.Length < 2 ? byte.MaxValue : byte.TryParse(args[1], out byte targetId) ? targetId : byte.MaxValue;

        player.RPCPlayCustomSound("Line");

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void QSCommand(PlayerControl player, string text, string[] args)
    {
        if (!QuizMaster.On || !player.IsAlive()) return;

        var qm2 = (QuizMaster)Main.PlayerStates.Values.First(x => x.Role is QuizMaster).Role;
        if (qm2.Target != player.PlayerId || !QuizMaster.MessagesToSend.TryGetValue(player.PlayerId, out string msg)) return;

        Utils.SendMessage(msg, player.PlayerId, GetString("QuizMaster.QuestionSample.Title"), importance: MessageImportance.High);

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void QACommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !QuizMaster.On || !player.IsAlive()) return;

        var qm = (QuizMaster)Main.PlayerStates.Values.First(x => x.Role is QuizMaster).Role;
        if (qm.Target != player.PlayerId) return;

        qm.Answer(args[1].ToUpper());

        MeetingManager.SendCommandUsedMessage(args[0]);
    }

    private static void AnswerCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2) return;
        Mathematician.Reply(player, args[1]);
    }

    private static void AskCommand(PlayerControl player, string text, string[] args)
    {
        if (Starspawn.IsDayBreak) return;

        if (args.Length < 3 || !player.Is(CustomRoles.Mathematician)) return;

        Mathematician.Ask(player, args[1], args[2]);
    }

    private static void VoteCommand(PlayerControl player, string text, string[] args)
    {
        if (text.Length < 6 || !GameStates.IsMeeting) return;

        string toVote = text[6..].Replace(" ", string.Empty);
        if (!byte.TryParse(toVote, out byte voteId) || MeetingHud.Instance.playerStates?.FirstOrDefault(x => x.TargetPlayerId == player.PlayerId)?.DidVote is true or null) return;

        if (voteId > PlayerControl.AllPlayerControls.Count) return;

        PlayerControl votedPlayer = voteId.GetPlayer();
        if (!player.UsesMeetingShapeshift() && Main.PlayerStates.TryGetValue(player.PlayerId, out PlayerState state) && votedPlayer != null && state.Role.OnVote(player, votedPlayer)) return;

        MeetingHud.Instance.CastVote(player.PlayerId, voteId);
    }

    private static void SayCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.IsHost() && !IsPlayerModerator(player.FriendCode)) return;
        if (args.Length > 1) Utils.SendMessage(args[1..].Join(delimiter: " "), title: $"<color=#ff0000>{GetString(player.IsHost() ? "MessageFromTheHost" : "SayTitle")}</color>", importance: player.IsHost() ? MessageImportance.High : MessageImportance.Medium);
    }

    private static void DeathCommand(PlayerControl player, string text, string[] args)
    {
        if (!GameStates.IsInGame) return;
        if (Main.DiedThisRound.Contains(player.PlayerId) && Utils.IsRevivingRoleAlive()) return;

        PlayerControl target = args.Length < 2 || !byte.TryParse(args[1], out byte targetId) ? player : targetId.GetPlayer();
        if (target == null) return;

        PlayerControl killer = target.GetRealKiller();

        if (killer == null)
        {
            Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("DeathCommandFail"), GetString($"DeathReason.{Main.PlayerStates[target.PlayerId].deathReason}")), importance: MessageImportance.Low);
            return;
        }

        Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("DeathCommand"), killer.PlayerId.ColoredPlayerName(), (killer.Is(CustomRoles.Bloodlust) ? $"{CustomRoles.Bloodlust.ToColoredString()} " : string.Empty) + killer.GetCustomRole().ToColoredString()));
    }

    private static void MessageWaitCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length > 1 && int.TryParse(args[1], out int sec))
        {
            Main.MessageWait.Value = sec;
            Utils.SendMessage(string.Format(GetString("Message.SetToSeconds"), sec), 0);
        }
        else
            Utils.SendMessage($"{GetString("Message.MessageWaitHelp")}\n{GetString("ForExample")}:\n{args[0]} 3", 0);
    }

    private static void TemplateCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length > 1)
        {
            if (player.AmOwner)
                TemplateManager.SendTemplate(args[1]);
            else
                TemplateManager.SendTemplate(args[1], player.PlayerId);
        }
        else
        {
            HashSet<string> tags = TemplateManager.GetAllTags();
            string message = tags.Count > 0 
            ? string.Format(GetString("Message.TemplateList"), string.Join("\n", tags)) : GetString("Message.NoTemplatesFound");

            if (player.AmOwner)
                HudManager.Instance.Chat.AddChat(player, (player.FriendCode.GetDevUser().HasTag() ? "\n" : string.Empty) + message);
            else
                Utils.SendMessage(message, player.PlayerId, importance: MessageImportance.Low);
        }
    }

    private static void TPInCommand(PlayerControl player, string text, string[] args)
    {
        if (!GameStates.IsLobby) return;

        if (!Options.PlayerCanTPInAndOut.GetBool() && !IsPlayerVIP(player.FriendCode) && !player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev())
        {
            Utils.SendMessage(GetString("Message.OnlyVIPCanUse"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        player.TP(new Vector2(-0.2f, 1.3f));
    }

    private static void TPOutCommand(PlayerControl player, string text, string[] args)
    {
        if (!GameStates.IsLobby) return;

        if (!Options.PlayerCanTPInAndOut.GetBool() && !IsPlayerVIP(player.FriendCode) && !player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev())
        {
            Utils.SendMessage(GetString("Message.OnlyVIPCanUse"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        player.TP(new Vector2(0.1f, 3.8f));
    }

    private static void BBDiagCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.DumpLobbyColliders(player.PlayerId);
    }

    private static void BBToggleCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.ToggleShipColliders(player.PlayerId);
    }

    private static void BBSpawnCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.SpawnTestPattern(player.PlayerId);
    }

    private static void BBClearCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.ClearTiles(player.PlayerId);
    }

    private static void BBGenCommand(PlayerControl player, string text, string[] args)
    {
        // procgen 系コマンドはカスタムマップを自動解除してから動く (§11)
        EkmapLoader.ClearActiveSource();

        uint seed = args.Length >= 2 && uint.TryParse(args[1], out uint parsedSeed)
            ? parsedSeed
            : (uint)UnityEngine.Random.Range(1, int.MaxValue);

        BackroomsLobby.GenerateLobby(seed, player.PlayerId);
    }

    private static void BBEnterCommand(PlayerControl player, string text, string[] args)
    {
        // procgen 系コマンドはカスタムマップを自動解除してから動く (§11)
        EkmapLoader.ClearActiveSource();

        uint seed = args.Length >= 2 && uint.TryParse(args[1], out uint parsedSeed)
            ? parsedSeed
            : (uint)UnityEngine.Random.Range(1, int.MaxValue);

        BackroomsLobby.EnterBackrooms(seed, player.PlayerId);
    }

    private static void BBExitCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.ExitBackrooms(player.PlayerId);
    }

    private static void BBShadowCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.ShadowCommand(args, player.PlayerId);
    }

    private static void BBZoneCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.ZoneCommand(args, player.PlayerId);
    }

    private static void BBRangeCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.RangeCommand(args, player.PlayerId);
    }

    private static void BBZoomCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.ZoomCommand(args, player.PlayerId);
    }

    private static void BBTestRoomCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.TestRoomCommand(args, player.PlayerId);
    }

    private static void MapCommand(PlayerControl player, string text, string[] args)
    {
        string sub = args.Length >= 2 ? args[1].ToLower() : "";
        byte pid = player.PlayerId;

        switch (sub)
        {
            case "list":
            {
                var maps = EkmapLoader.ListMaps();
                if (maps.Count == 0)
                {
                    Utils.SendMessage($"{GetString("EkMap.List.Empty")}\n<size=70%>{EkmapLoader.EKMapsPath}</size>", pid);
                    return;
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine(GetString("EkMap.List.Header"));
                sb.AppendLine($"<size=70%>{EkmapLoader.EKMapsPath}</size>");
                foreach ((string fn, long bytes) in maps)
                    sb.AppendLine($"  {fn}  ({bytes / 1024}KB)");
                Utils.SendMessage(sb.ToString().TrimEnd(), pid);
                return;
            }

            case "load":
            {
                if (args.Length < 3)
                {
                    Utils.SendMessage(GetString("EkMap.Usage"), pid);
                    return;
                }

                // スペース含みファイル名に対応 (args[2..] を join)
                string filename = string.Join(" ", args, 2, args.Length - 2);

                if (!EkmapLoader.TryLoad(filename, out string err))
                {
                    Utils.SendMessage($"{GetString("EkMap.LoadError")}: {err}", pid);
                    return;
                }

                // procgen Backrooms 滞在中なら exit→enter
                BackroomsLobby.EnterCustomMap(pid);
                return;
            }

            case "reload":
            {
                if (EkmapLoader.ActiveSource == null)
                {
                    Utils.SendMessage(GetString("EkMap.ReloadNone"), pid);
                    return;
                }

                string filename = EkmapLoader.ActiveSource.Filename;

                if (!EkmapLoader.TryLoad(filename, out string err))
                {
                    Utils.SendMessage($"{GetString("EkMap.LoadError")}: {err}", pid);
                    return;
                }

                BackroomsLobby.EnterCustomMap(pid);
                return;
            }

            case "exit":
            {
                EkmapLoader.ClearActiveSource();
                BackroomsLobby.ExitBackrooms(pid, silent: true);
                Utils.SendMessage(GetString("EkMap.Exit"), pid);
                return;
            }

            case "import":
            {
                // クリップボード (EKM1.… コード) を直読みする。チャット欄は文字数上限で長大コードを受けられないため。
                string code = UnityEngine.GUIUtility.systemCopyBuffer?.Trim() ?? "";
                if (!EkmapLoader.TryImportCode(code, out string saved, out string impErr))
                {
                    Utils.SendMessage($"{GetString("EkMap.ImportError")}: {impErr}", pid);
                    return;
                }

                BackroomsLobby.EnterCustomMap(pid);
                Utils.SendMessage(string.Format(GetString("EkMap.Imported"), saved), pid);
                return;
            }

            case "export":
            {
                if (!EkmapLoader.TryExportCurrentCode(out string code, out string expErr))
                {
                    Utils.SendMessage($"{GetString("EkMap.ExportError")}: {expErr}", pid);
                    return;
                }

                UnityEngine.GUIUtility.systemCopyBuffer = code;
                int kb = System.Text.Encoding.UTF8.GetByteCount(code) / 1024;
                string warn = code.Length > 512 * 1024 ? "\n" + GetString("EkMap.ExportSizeWarn") : "";
                Utils.SendMessage(string.Format(GetString("EkMap.Exported"), kb) + warn, pid);
                return;
            }

            case "info":
            {
                var src = EkmapLoader.ActiveSource;
                if (src == null)
                {
                    Utils.SendMessage(GetString("EkMap.ReloadNone"), pid);
                    return;
                }

                Utils.SendMessage(string.Format(GetString("EkMap.Info"),
                    src.Name, src.Author, src.Width, src.Height, src.IsV3 ? 3 : src.IsV2 ? 2 : 1, src.Filename), pid);
                return;
            }

            case "diag":
            {
                BackroomsLobby.DumpEkmStreamDiag(pid);
                return;
            }

            case "shadow": // 影レイヤーの遮蔽線をシアンで可視化トグル (placement vs 影本体の切り分け用)
            {
                EkmShadow.Visualize = !EkmShadow.Visualize;
                if (EkmapLoader.ActiveSource != null) EkmShadow.Spawn(EkmapLoader.ActiveSource);
                Utils.SendMessage($"Shadow line visualize: {(EkmShadow.Visualize ? "ON" : "OFF")} (lines={EkmShadow.Count})", pid);
                return;
            }

            default:
                Utils.SendMessage(GetString("EkMap.Usage"), pid);
                return;
        }
    }

    // EKN ノーコード役職メーカー (計画正典: docs/ekn-api-plan.md、実体: Modules/Ekm/EkrManager)。
    private static void RoleCommand(PlayerControl player, string text, string[] args)
    {
        string sub = args.Length >= 2 ? args[1].ToLower() : "";
        byte pid = player.PlayerId;

        switch (sub)
        {
            case "list":
            {
                EkrManager.ReloadLibrary();
                var lib = EkrManager.ListLibrary();
                var sb = new System.Text.StringBuilder();

                if (lib.Count == 0)
                {
                    sb.AppendLine(GetString("EkRole.List.Empty"));
                    sb.AppendLine($"<size=70%>{EkrManager.RolesPath}</size>");
                }
                else
                {
                    sb.AppendLine(GetString("EkRole.List.Header"));
                    sb.AppendLine($"<size=70%>{EkrManager.RolesPath}</size>");

                    for (var i = 0; i < lib.Count; i++)
                    {
                        (string fn, EkrDefinition def) = lib[i];
                        string author = def.Author.Length > 0 ? $" ({def.Author})" : "";
                        // R2: 陣営を併記する (入れられるスロットが陣営で決まるため — 事故防止)。
                        sb.AppendLine($"  {i + 1}. {def.Name}{author} <{EkrManager.TeamLabel(def.ParsedTeam)}>  <size=70%>{fn}</size>");
                    }
                }

                sb.AppendLine(GetString("EkRole.List.SlotHeader"));
                var anyBound = false;

                // R2: スロットは陣営ごとに分かれているので、陣営ごとに「番号の範囲」と束縛済みを並べる。
                foreach (EkrTeam team in new[] { EkrTeam.Crewmate, EkrTeam.Impostor, EkrTeam.Neutral })
                {
                    (int first, int last) = EkrManager.SlotRange(team);
                    if (first == 0) continue;

                    var bound = new System.Text.StringBuilder();

                    for (int i = first - 1; i < last; i++)
                    {
                        EkrDefinition def = EkrManager.GetDefinition(EkrManager.Slots[i]);
                        if (def == null) continue;

                        anyBound = true;
                        if (bound.Length > 0) bound.Append(" / ");
                        bound.Append($"[{i + 1}] {def.Name}");
                    }

                    if (bound.Length == 0) bound.Append(GetString("EkRole.List.NoBound"));

                    sb.AppendLine($"  {EkrManager.TeamLabel(team)} [{first}-{last}]: {bound}");
                }

                if (!anyBound) sb.AppendLine($"  <size=70%>{GetString("EkRole.List.SlotHint")}</size>");

                Utils.SendMessage(sb.ToString().TrimEnd(), pid);
                return;
            }

            case "import":
            {
                // クリップボード直読み (EKR1.… コード)。チャット欄は文字数上限で長いコードを受けられないため /map import と同方式。
                string code = UnityEngine.GUIUtility.systemCopyBuffer?.Trim() ?? "";

                if (!EkrManager.TryImportCode(code, out string saved, out string impErr))
                {
                    Utils.SendMessage($"{GetString("EkRole.ImportError")}: {impErr}", pid);
                    return;
                }

                Utils.SendMessage(string.Format(GetString("EkRole.Imported"), saved), pid);
                return;
            }

            case "set":
            {
                // ゲーム再起動後に /role list を経ず直接 set した場合でもライブラリを見つけられるよう、
                // 割り当て前に毎回ディスクから再読込する (数ファイルの JSON 読みなので常時実行してよい)。
                EkrManager.ReloadLibrary();

                if (args.Length < 3 || !int.TryParse(args[2], out int libIdx))
                {
                    Utils.SendMessage(GetString("EkRole.Usage"), pid);
                    return;
                }

                int slot;

                if (args.Length >= 4)
                {
                    if (!int.TryParse(args[3], out slot))
                    {
                        Utils.SendMessage(GetString("EkRole.Usage"), pid);
                        return;
                    }
                }
                else
                {
                    // R2: 省略時は「その役職コードの陣営の」空きスロットを探す。
                    var setLib = EkrManager.ListLibrary();
                    EkrTeam wantTeam = libIdx >= 1 && libIdx <= setLib.Count ? setLib[libIdx - 1].Def.ParsedTeam : EkrTeam.Crewmate;

                    slot = EkrManager.FirstFreeSlotNumber(wantTeam);

                    if (slot == 0)
                    {
                        Utils.SendMessage(GetString("EkRole.NoFreeSlot"), pid);
                        return;
                    }
                }

                if (!EkrManager.TryAssign(libIdx, slot, out string err))
                {
                    Utils.SendMessage($"{GetString("EkRole.AssignError")}: {err}", pid);
                    return;
                }

                EkrDefinition def = EkrManager.GetDefinition(EkrManager.Slots[slot - 1]);
                Utils.SendMessage(string.Format(GetString("EkRole.Assigned"), def.Name, slot), pid);
                return;
            }

            case "unset":
            {
                string sel = args.Length >= 3 ? args[2].ToLower() : "";

                if (sel == "all")
                {
                    for (var i = 1; i <= EkrManager.Slots.Length; i++) EkrManager.TryUnassign(i, out _);

                    Utils.SendMessage(GetString("EkRole.UnassignedAll"), pid);
                    return;
                }

                if (!int.TryParse(sel, out int slot))
                {
                    Utils.SendMessage(GetString("EkRole.Usage"), pid);
                    return;
                }

                if (!EkrManager.TryUnassign(slot, out string err))
                {
                    Utils.SendMessage($"{GetString("EkRole.UnassignError")}: {err}", pid);
                    return;
                }

                Utils.SendMessage(string.Format(GetString("EkRole.Unassigned"), slot), pid);
                return;
            }

            default:
                Utils.SendMessage(GetString("EkRole.Usage"), pid);
                return;
        }
    }

    private static void BBShadowDiagCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.DumpShadowSystem(player.PlayerId);
    }

    private static void BBVisToggleCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.ToggleVisionPaused(player.PlayerId);
    }

    private static void BBLightProbeCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.ProbeLightSystem(player.PlayerId);
    }

    private static void BBVisDiagCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.DumpVisionDiagCurrentSeed(player.PlayerId);
    }

    private static void BBCullInfoCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.DumpCullInfo(player.PlayerId);
    }

    private static void BBShipDiagCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.DumpSceneRenderers(player.PlayerId);
    }

    private static void BBNoCDiagCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.DumpNoClipDiag(player.PlayerId);
    }

    private static void BBPerfCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.TogglePerfLog(player.PlayerId);
    }

    private static void BBWallDarkCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.SetWallDark(player.PlayerId, args);
    }

    private static void BBStreamBudgetCommand(PlayerControl player, string text, string[] args)
    {
        BackroomsLobby.SetStreamBudget(player.PlayerId, args);
    }

    // /rehost — 実際に kick されなくても自動部屋立て直しの一連を試せるデバッグ用 (host-only, hidden)。
    // 「キック/エラー切断」を疑似発火するだけ。AutoRehostAfterKick オプションが ON のときに動く。
    private static void RehostCommand(PlayerControl player, string text, string[] args)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        if (!(Options.AutoRehostAfterKick?.GetBool() ?? false))
        {
            Utils.SendMessage("DebugRehost: AutoRehostAfterKick option is OFF — enable it first, then /rehost.", player.PlayerId, "DebugRehost");
            return;
        }

        Utils.SendMessage("DebugRehost: forcing a real disconnect now. The lobby should re-host shortly.", player.PlayerId, "DebugRehost");
        // 実際の kick と同じ経路を通すため、本物の ExitGame で切断する。
        // ExitGamePatch.Prefix が自然に AutoRehost.OnDisconnect(Error) を呼ぶ (_pending ガードで二重発火しない)。
        try { AmongUsClient.Instance.ExitGame(DisconnectReasons.Error); }
        catch (Exception ex) { Logger.Warn($"/rehost ExitGame failed: {ex.Message}", "DebugRehost"); }
    }

    private static void MyRoleCommand(PlayerControl player, string text, string[] args)
    {
        CustomRoles role = player.GetCustomRole();

        if (GameStates.IsInGame)
        {
            StringBuilder sb = new();
            StringBuilder titleSb = new();
            StringBuilder settings = new();
            settings.Append("<size=70%>");
            titleSb.Append($"{role.ToColoredString()} {Utils.GetRoleMode(role)}");

            sb.Append(player.GetRoleInfo(true).TrimStart());
            
            if (Options.CustomRoleSpawnChances.TryGetValue(role, out StringOptionItem opt))
                Utils.ShowChildrenSettings(opt, settings, disableColor: false);

            settings.Append("</size>");
            
            if (role.PetActivatedAbility())
                sb.Append($"<size=1>{GetString("SupportsPetMessage")}</size>");

            string searchStr = GetString(role.ToString());
            sb.Replace(searchStr, role.ToColoredString());
            sb.Replace(searchStr.ToLower(), role.ToColoredString());

            foreach (CustomRoles subRole in Main.PlayerStates[player.PlayerId].SubRoles)
            {
                sb.Append($"\n\n{subRole.ToColoredString()} {Utils.GetRoleMode(subRole)} {GetString($"{subRole}InfoLong").FixRoleName(subRole)}");
                string searchSubStr = GetString(subRole.ToString());
                sb.Replace(searchSubStr, subRole.ToColoredString());
                sb.Replace(searchSubStr.ToLower(), subRole.ToColoredString());
            }

            if (settings.Length > 0) Utils.SendMessage("\n", player.PlayerId, settings.ToString());

            Utils.SendMessage(sb.ToString(), player.PlayerId, titleSb.ToString(), importance: MessageImportance.High);
            if (role.UsesPetInsteadOfKill()) Utils.SendMessage("\n", player.PlayerId, GetString("UsesPetInsteadOfKillNotice"));
            if (player.UsesMeetingShapeshift()) Utils.SendMessage("\n", player.PlayerId, GetString("UsesMeetingShapeshiftNotice"));
        }
        else
            Utils.SendMessage((player.FriendCode.GetDevUser().HasTag() ? "\n" : string.Empty) + GetString("Message.CanNotUseInLobby"), player.PlayerId);
    }

    private static void AFKExemptCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !byte.TryParse(args[1], out byte afkId)) return;

        AFKDetector.ExemptedPlayers.Add(afkId);
        Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("PlayerExemptedFromAFK"), afkId.ColoredPlayerName()));
    }

    private static void EffectCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !GameStates.IsInTask || !Randomizer.Exists) return;

        if (Enum.TryParse(args[1], true, out Randomizer.Effect effect)) effect.Apply(player);
    }

    private static void ComboCommand(PlayerControl player, string text, string[] args)
    {
        if ((!player.IsHost() && !IsPlayerAdmin(player.FriendCode)) || args.Length < 4)
        {
            if (Main.AlwaysSpawnTogetherCombos.Count == 0 && Main.NeverSpawnTogetherCombos.Count == 0) return;

            StringBuilder sb = new();
            sb.Append("<size=70%>");

            if (Main.AlwaysSpawnTogetherCombos.TryGetValue(OptionItem.CurrentPreset, out Dictionary<CustomRoles, List<CustomRoles>> alwaysList) && alwaysList.Count > 0)
            {
                sb.AppendLine(GetString("AlwaysComboListTitle"));
                sb.AppendLine(alwaysList.Join(x => $"{x.Key.ToColoredString()} \u00a7 {x.Value.Join(r => r.ToColoredString())}", "\n"));
                sb.AppendLine();
            }

            if (Main.NeverSpawnTogetherCombos.TryGetValue(OptionItem.CurrentPreset, out Dictionary<CustomRoles, List<CustomRoles>> neverList) && neverList.Count > 0)
            {
                sb.AppendLine(GetString("NeverComboListTitle"));
                sb.AppendLine(neverList.Join(x => $"{x.Key.ToColoredString()} \u2194 {x.Value.Join(r => r.ToColoredString())}", "\n"));
                sb.AppendLine();
            }

            sb.Append(GetString("ComboUsage"));

            Utils.SendMessage("\n", player.PlayerId, sb.ToString());
            return;
        }

        switch (args[1])
        {
            case "add":
            case "ban":
                if (GetRoleByName(args[2], out CustomRoles mainRole) && GetRoleByName(args[3], out CustomRoles addOn))
                {
                    if (mainRole.IsAdditionRole() || !addOn.IsAdditionRole() || (addOn == CustomRoles.Lovers && args[1] == "add")) break;

                    if (args[1] == "add")
                    {
                        if (!Main.AlwaysSpawnTogetherCombos.ContainsKey(OptionItem.CurrentPreset)) Main.AlwaysSpawnTogetherCombos[OptionItem.CurrentPreset] = [];

                        if (!Main.AlwaysSpawnTogetherCombos[OptionItem.CurrentPreset].TryGetValue(mainRole, out List<CustomRoles> list1))
                            Main.AlwaysSpawnTogetherCombos[OptionItem.CurrentPreset][mainRole] = [addOn];
                        else if (!list1.Contains(addOn)) list1.Add(addOn);

                        if (text.EndsWith(" all"))
                        {
                            for (var preset = 0; preset < OptionItem.NumPresets; preset++)
                            {
                                if (preset == OptionItem.CurrentPreset) continue;

                                if (!Main.AlwaysSpawnTogetherCombos.ContainsKey(preset)) Main.AlwaysSpawnTogetherCombos[preset] = [];

                                if (!Main.AlwaysSpawnTogetherCombos[preset].TryGetValue(mainRole, out List<CustomRoles> list2))
                                    Main.AlwaysSpawnTogetherCombos[preset][mainRole] = [addOn];
                                else if (!list2.Contains(addOn)) list2.Add(addOn);
                            }
                        }
                    }
                    else
                    {
                        if (!Main.NeverSpawnTogetherCombos.ContainsKey(OptionItem.CurrentPreset)) Main.NeverSpawnTogetherCombos[OptionItem.CurrentPreset] = [];

                        if (!Main.NeverSpawnTogetherCombos[OptionItem.CurrentPreset].TryGetValue(mainRole, out List<CustomRoles> list2))
                            Main.NeverSpawnTogetherCombos[OptionItem.CurrentPreset][mainRole] = [addOn];
                        else if (!list2.Contains(addOn)) list2.Add(addOn);

                        if (text.EndsWith(" all"))
                        {
                            for (var preset = 0; preset < OptionItem.NumPresets; preset++)
                            {
                                if (preset == OptionItem.CurrentPreset) continue;

                                if (!Main.NeverSpawnTogetherCombos.ContainsKey(preset)) Main.NeverSpawnTogetherCombos[preset] = [];

                                if (!Main.NeverSpawnTogetherCombos[preset].TryGetValue(mainRole, out List<CustomRoles> list3))
                                    Main.NeverSpawnTogetherCombos[preset][mainRole] = [addOn];
                                else if (!list3.Contains(addOn)) list3.Add(addOn);
                            }
                        }
                    }

                    Utils.SendMessage(string.Format(args[1] == "add" ? GetString("ComboAdd") : GetString("ComboBan"), GetString(mainRole.ToString()), GetString(addOn.ToString())), player.PlayerId);
                    Utils.SaveComboInfo();
                }

                break;
            case "remove":
            case "allow":
                if (GetRoleByName(args[2], out CustomRoles mainRole2) && GetRoleByName(args[3], out CustomRoles addOn2))
                {
                    if (mainRole2.IsAdditionRole() || !addOn2.IsAdditionRole()) break;

                    if (text.EndsWith(" all"))
                    {
                        for (var preset = 0; preset < OptionItem.NumPresets; preset++)
                        {
                            if (Main.AlwaysSpawnTogetherCombos.TryGetValue(preset, out Dictionary<CustomRoles, List<CustomRoles>> list1))
                            {
                                if (list1.TryGetValue(mainRole2, out List<CustomRoles> list2))
                                {
                                    list2.Remove(addOn2);
                                    if (list2.Count == 0) list1.Remove(mainRole2);

                                    if (list1.Count == 0) Main.AlwaysSpawnTogetherCombos.Remove(preset);
                                }
                            }

                            if (Main.NeverSpawnTogetherCombos.TryGetValue(preset, out Dictionary<CustomRoles, List<CustomRoles>> list3))
                            {
                                if (list3.TryGetValue(mainRole2, out List<CustomRoles> list4))
                                {
                                    list4.Remove(addOn2);
                                    if (list4.Count == 0) list3.Remove(mainRole2);

                                    if (list3.Count == 0) Main.NeverSpawnTogetherCombos.Remove(preset);
                                }
                            }
                        }

                        Utils.SendMessage(string.Format(GetString("ComboRemove"), GetString(mainRole2.ToString()), GetString(addOn2.ToString())), player.PlayerId);
                        Utils.SaveComboInfo();
                    }
                    else
                    {
                        if (args[1] == "remove" && Main.AlwaysSpawnTogetherCombos.TryGetValue(OptionItem.CurrentPreset, out Dictionary<CustomRoles, List<CustomRoles>> alwaysList) && alwaysList.TryGetValue(mainRole2, out List<CustomRoles> list3))
                        {
                            list3.Remove(addOn2);
                            if (list3.Count == 0) alwaysList.Remove(mainRole2);

                            Utils.SendMessage(string.Format(GetString("ComboRemove"), GetString(mainRole2.ToString()), GetString(addOn2.ToString())), player.PlayerId);
                            Utils.SaveComboInfo();
                        }
                        else if (Main.NeverSpawnTogetherCombos.TryGetValue(OptionItem.CurrentPreset, out Dictionary<CustomRoles, List<CustomRoles>> neverList) && neverList.TryGetValue(mainRole2, out List<CustomRoles> list4))
                        {
                            list4.Remove(addOn2);
                            if (list4.Count == 0) neverList.Remove(mainRole2);

                            Utils.SendMessage(string.Format(GetString("ComboAllow"), GetString(mainRole2.ToString()), GetString(addOn2.ToString())), player.PlayerId);
                            Utils.SaveComboInfo();
                        }
                    }
                }

                break;
        }
    }

    private static void DeleteModCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !byte.TryParse(args[1], out byte remModId)) return;

        PlayerControl remModPc = Utils.GetPlayerById(remModId);
        if (remModPc == null) return;

        string remFc = remModPc.FriendCode.Replace(':', '#');

        if (!IsPlayerModerator(remFc))
        {
            Utils.SendMessage(GetString("PlayerNotMod"), player.PlayerId);
            return;
        }

        File.WriteAllLines($"{Main.DataPath}/EndKnot_DATA/Moderators.txt", File.ReadAllLines($"{Main.DataPath}/EndKnot_DATA/Moderators.txt").Where(x => !x.Contains(remFc)));
        Utils.SendMessage(GetString("PlayerRemovedFromModList"), player.PlayerId);
    }

    private static void AddModCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2 || !byte.TryParse(args[1], out byte newModId)) return;

        PlayerControl newModPc = Utils.GetPlayerById(newModId);
        if (newModPc == null) return;

        string fc = newModPc.FriendCode.Replace(':', '#');

        if (IsPlayerModerator(fc))
        {
            Utils.SendMessage(GetString("PlayerAlreadyMod"), player.PlayerId);
            return;
        }

        File.AppendAllText($"{Main.DataPath}/EndKnot_DATA/Moderators.txt", $"\n{fc}");
        Utils.SendMessage(GetString("PlayerAddedToModList"), player.PlayerId);
    }

    // ── Dev-only debug commands ──────────────────────────────────────────────────

    private static void InspectCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        byte targetId = player.PlayerId;
        if (args.Length >= 2 && byte.TryParse(args[1], out byte parsed))
            targetId = parsed;

        PlayerControl target = Utils.GetPlayerById(targetId);
        if (target == null) { Utils.SendMessage("[Inspect] Player not found", player.PlayerId); return; }

        if (!Main.PlayerStates.TryGetValue(targetId, out PlayerState state) || state.Role == null)
        {
            Utils.SendMessage($"[Inspect] {target.GetRealName()}: no role state", player.PlayerId);
            return;
        }

        RoleBase role = state.Role;
        Type type = role.GetType();
        var sb = new StringBuilder();
        sb.AppendLine($"[Inspect] {target.GetRealName()} / {type.Name}");

        for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            if (t == typeof(RoleBase)) break;
            foreach (FieldInfo fi in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                sb.AppendLine($"  {fi.Name}: {FormatInspectValue(fi.GetValue(role))}");
        }

        sb.AppendLine($"  [SubRoles] {(state.SubRoles.Count > 0 ? string.Join(", ", state.SubRoles) : "none")}");

        string output = sb.ToString();
        if (output.Length > 2000)
        {
            Logger.Warn(output, "DevInspect");
            output = output[..1990] + "\n...(→ log)";
        }

        Logger.Info($"DevCmd /inspect: {target.GetRealName()} / {type.Name}", "DevCmd");
        Utils.SendMessage(output, player.PlayerId);
    }

    private static string FormatInspectValue(object val)
    {
        if (val == null) return "<null>";
        if (val is string s) return $"\"{s}\"";
        if (val is IEnumerable en)
        {
            var items = new List<string>();
            int total = 0;
            foreach (object item in en)
            {
                total++;
                if (items.Count < 3) items.Add(item?.ToString() ?? "<null>");
            }
            return $"Count={total} [{string.Join(", ", items)}{(total > 3 ? ", ..." : "")}]";
        }
        return val.ToString();
    }

    private static void OptDumpCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        TabGroup tab = TabGroup.SystemSettings;
        if (args.Length >= 2) Enum.TryParse(args[1], ignoreCase: true, out tab);

        var sb = new StringBuilder();
        sb.AppendLine($"[OptDump] Tab: {tab}");

        foreach (OptionItem root in OptionItem.AllOptions.Where(o => o.Tab == tab && o.Parent == null))
            AppendOptionTree(sb, root, 0);

        string output = sb.ToString();
        if (output.Length > 5000)
        {
            Logger.Warn(output, "DevOptDump");
            output = output[..4990] + "\n...(→ log)";
        }

        Logger.Info($"DevCmd /optdump: {tab}", "DevCmd");
        Utils.SendMessage(output, player.PlayerId);
    }

    private static void AppendOptionTree(StringBuilder sb, OptionItem item, int depth)
    {
        string indent = new(' ', depth * 2);
        string prefix = item.IsCurrentlyHidden() ? "※" : "●";
        sb.AppendLine($"{indent}{prefix} {item.GetName(disableColor: true)}: {item.GetString()}");
        foreach (OptionItem child in item.Children)
            AppendOptionTree(sb, child, depth + 1);
    }

    private static void CdCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        PlayerControl target;
        float kcd;
        if (args.Length >= 3 && byte.TryParse(args[1], out byte tid) && float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out kcd))
            target = Utils.GetPlayerById(tid) ?? player;
        else if (args.Length >= 2 && float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out kcd))
            target = player;
        else return;

        kcd = Math.Max(0.1f, kcd);

        if (IntroCutsceneDestroyPatch.PreventKill)
            Utils.SendMessage("[warn] PreventKill is active — KCD may be ignored until intro ends", player.PlayerId);

        Main.AllPlayerKillCooldown[target.PlayerId] = kcd;
        target.SetKillCooldown(kcd);

        bool isPet = target.GetCustomRole().UsesPetInsteadOfKill();
        Logger.Info($"DevCmd /cd: {target.GetRealName()} -> {kcd}s{(isPet ? " (as ability CD)" : "")}", "DevCmd");
        Utils.SendMessage($"[cd] {target.GetRealName()} -> {kcd}s{(isPet ? " (ability CD)" : "")}", player.PlayerId);
    }

    private static void DevTpCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;
        if (args.Length < 3) return;
        if (!float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)) return;
        if (!float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y)) return;

        PlayerControl target = player;
        if (args.Length >= 4 && byte.TryParse(args[3], out byte tid))
            target = Utils.GetPlayerById(tid) ?? player;

        target.TP(new Vector2(x, y), noCheckState: true);
        Logger.Info($"DevCmd /devtp: {target.GetRealName()} -> ({x},{y})", "DevCmd");
        Utils.SendMessage($"[tp] {target.GetRealName()} -> ({x:F2}, {y:F2})", player.PlayerId);
    }

    private static void DevTpToCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;
        if (args.Length < 3) return;
        if (!byte.TryParse(args[1], out byte srcId) || !byte.TryParse(args[2], out byte dstId)) return;

        PlayerControl src = Utils.GetPlayerById(srcId);
        PlayerControl dst = Utils.GetPlayerById(dstId);
        if (src == null || dst == null) return;

        src.TP(dst.GetTruePosition(), noCheckState: true);
        Logger.Info($"DevCmd /devtpto: {src.GetRealName()} -> {dst.GetRealName()}", "DevCmd");
        Utils.SendMessage($"[tpto] {src.GetRealName()} -> {dst.GetRealName()}", player.PlayerId);
    }

    private static void DummyCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        int count = 1;
        if (args.Length >= 2 && int.TryParse(args[1], out int parsed))
            count = Math.Clamp(parsed, 1, 3);

        Vector2 origin = player.GetTruePosition() + new Vector2(0.8f, 0f);
        int spawned = DummyPlayer.SpawnBatch(count, origin);

        Logger.Info($"DevCmd /dummy: spawned={spawned}", "DevCmd");
        Utils.SendMessage($"[dummy] Spawned {spawned}. Total active: {DummyPlayer.ActiveDummies.Count}", player.PlayerId);
    }

    private static void UnDummyCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        int removed = DummyPlayer.DespawnAll();
        Logger.Info($"DevCmd /undummy: removed={removed}", "DevCmd");
        Utils.SendMessage($"[undummy] Despawned {removed} dummy marker(s).", player.PlayerId);
    }

    private static void DummyFreeCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        DummyPlayer.LockPosition = !DummyPlayer.LockPosition;
        string state = DummyPlayer.LockPosition ? "LOCKED (default)" : "FREE (eject-testable)";
        Logger.Info($"DevCmd /dummyfree: LockPosition={DummyPlayer.LockPosition}", "DevCmd");
        Utils.SendMessage($"[dummyfree] Dummy position: {state}", player.PlayerId);
    }

    private static void SizeTestCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        // 第2引数 "percent" でパーセント mode、それ以外 (or 引数無し) は絶対 mode
        bool absolute = args.Length < 2 || !args[1].Equals("percent", System.StringComparison.OrdinalIgnoreCase);

        Vector2 origin = player.GetTruePosition() + new Vector2(2f, 0f);
        int spawned = SizeTestCNO.SpawnRow(origin, absolute);

        string sizes = absolute ? "20/40/60/80/100 (absolute)" : "600/800/1000/1200/1500% (percent)";
        Logger.Info($"DevCmd /sizetest absolute={absolute}: spawned={spawned} at {origin}", "DevCmd");
        Utils.SendMessage($"[sizetest] Spawned {spawned} ○ at sizes {sizes}. Walk right to see each.", player.PlayerId);
    }

    private static void HitboxCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        HitboxDebug.Enabled = !HitboxDebug.Enabled;
        if (!HitboxDebug.Enabled) HitboxDebug.Clear();
        Logger.Info($"DevCmd /hitbox: Enabled={HitboxDebug.Enabled}", "DevCmd");
        Utils.SendMessage($"[hitbox] Hitbox visualization: {(HitboxDebug.Enabled ? "ON" : "OFF")} (host-local only)", player.PlayerId);
    }

    // SnapTo cap 実験計器: /tpdbg = 現在値表示, /tpdbg set <n> = カウンタ直接セット,
    // /tpdbg official <0|1> = ローカル鯖でも公式鯖の cap 経路 (80/100) を発火させる。送信ゼロ・ホストローカル。
    // /tpdbg refill <sec> = トークン回復レート (秒/1回復) の実験変更。0 = 回復無効 (旧・会議境界リセットのみ)。
    private static void TpDbgCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        if (args.Length >= 3 && args[1].Equals("set", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[2], out int newCount))
        {
            Utils.NumSnapToCallsThisRound = newCount;
            Logger.Info($"DevCmd /tpdbg set: NumSnapToCallsThisRound={newCount}", "DevCmd");
        }
        else if (args.Length >= 3 && args[1].Equals("official", StringComparison.OrdinalIgnoreCase))
        {
            Utils.TpCapDebugForceOfficial = args[2] == "1";
            Logger.Info($"DevCmd /tpdbg official: ForceOfficial={Utils.TpCapDebugForceOfficial}", "DevCmd");
        }
        else if (args.Length >= 3 && args[1].Equals("refill", StringComparison.OrdinalIgnoreCase) && float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float newRate))
        {
            // 旧レートで回復を清算し基準時刻を now 側へ寄せてからレート変更 (無効→有効切替時の巨大まとめ回復を防ぐ)
            _ = Utils.NumSnapToCallsThisRound;
            // 0 以下 = 回復無効 (旧挙動への切り戻し)。正の極小値は cap 実質無効化になるため 0.1s を下限にクランプ
            Utils.SnapToRefillSecondsPerToken = newRate <= 0f ? 0f : Math.Max(newRate, 0.1f);
            Logger.Info($"DevCmd /tpdbg refill: SnapToRefillSecondsPerToken={Utils.SnapToRefillSecondsPerToken}", "DevCmd");
        }

        Utils.SendMessage($"[tpdbg] count={Utils.NumSnapToCallsThisRound} gross={Utils.NumSnapToGrossThisRound} refill={Utils.SnapToRefillSecondsPerToken}s/token forceOfficial={Utils.TpCapDebugForceOfficial} server={GameStates.CurrentServerType} {TpDeliveryProbe.GetStatsForTpDbg()}", player.PlayerId);
    }

    // ── SnapTo レート実験プローブ (/tpburst) ─────────────────────────────────────────────
    // 上流 EHR の SnapTo cap (80/100) の設計意図は「ホストが短時間に Reliable SnapTo を連射すると
    // 公式鯖に蹴られる」という想定である (考古学で確定):
    //   ① 100 側の警告文が "Too many Total SnapTo calls this round **and this second**" なのに、
    //      実装には秒の要素が一切ない (リセットは会議境界のみ) = 作者の意図は短窓レート、実装が累積。
    //   ② 80 側は Reliable **だけ**を数えて Reliable→None に降格させる (ec794df9)。回数そのものが
    //      問題なら None でも同じはずで、作者は「Reliable 送信のレート」を疑っていたことになる。
    // 一方このフォークの実キック調査 (P1〜P5 / fan-out ブラケット) では SnapTo は一度も容疑に
    // 上がっていない。つまり「作者がそう信じていた」と「サーバーが実際にそう振る舞う」は未分離で、
    // それを 1 ビットで測るのがこのプローブ。
    //
    // 実験行列 (同一ワイヤレートで sendOption だけを振るのが本命の 1 ビット分離):
    //   /tpburst 50 4        → Reliable 50/s × 4s (200発)   ← cap 100 超域から開始 (100以下は
    //   /tpburst 100 4       → Reliable 100/s                  2026-07-21 に「キック無し」実測済み)
    //   /tpburst 200 4       → Reliable 200/s
    //   /tpburst 200 4 none  → 同レートの Unreliable 対照。Reliable 側だけ死ねば上流の想定が正しい。
    //                          両方無傷なら SnapTo レート説は死に、cap は desync 対策と確定できる。
    //
    // ⚠️ 陽性コントロール (`/nest 1 thin` = 100% キック) は**全アーム生還後の最後**に撃つこと。
    //    先に撃つと蹴られて実験が始まらない (陰性を「サーバーが見ていなかった」と誤読しないため)。
    // ⚠️ Utils.TP は通さない — cap / minInterval / AntiTP / 1.5u 降格 / TpDeliveryProbe が全部
    //    交絡要因になる。ワイヤ形式だけ Utils.TP と同形に手書きする。NumSnapToCallsThisRound は
    //    消費しない (cap を測るのではなく、cap が防ごうとしている当のものを測るプローブなので)。
    // ⚠️ Reliable アームは PacketRateGate (25/秒) を必ず bypass する。しないと submitted 200/s が
    //    ワイヤでは 25/s に整形され、「200/s 撃って無傷」が黙って偽陰性になる (/nest で踏んだ罠)。
    private static bool TpBurstRunning;
    private static bool TpBurstStopRequested;
    private static object TpBurstConnection;

    private static void TpBurstCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        if (args.Length >= 2 && args[1].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            if (!TpBurstRunning)
            {
                Utils.SendMessage("[tpburst] not running.", player.PlayerId);
                return;
            }

            TpBurstStopRequested = true;
            Utils.SendMessage("[tpburst] stop requested.", player.PlayerId);
            return;
        }

        if (TpBurstRunning)
        {
            Utils.SendMessage("[tpburst] already running — '/tpburst stop' first.", player.PlayerId);
            return;
        }

        if (args.Length < 3 || !int.TryParse(args[1], out int rate) || !float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seconds) || rate <= 0 || seconds <= 0f)
        {
            Utils.SendMessage("[tpburst] Usage: /tpburst <rate/s> <seconds> [rel|none] [tgt=<playerId>] [gated] | /tpburst stop", player.PlayerId);
            return;
        }

        // 暴走防止のクランプ (実験行列の最大は 200/s × 4s = 800発)
        rate = Math.Min(rate, 500);
        seconds = Math.Min(seconds, 20f);
        if (rate * seconds > 4000) seconds = 4000f / rate;

        SendOption option = args.Any(a => a.Equals("none", StringComparison.OrdinalIgnoreCase)) ? SendOption.None : SendOption.Reliable;
        bool gated = args.Any(a => a.Equals("gated", StringComparison.OrdinalIgnoreCase));

        byte targetId = player.PlayerId;
        string tgtArg = args.FirstOrDefault(a => a.StartsWith("tgt=", StringComparison.OrdinalIgnoreCase));
        if (tgtArg != null && byte.TryParse(tgtArg[4..], out byte parsedTgt)) targetId = parsedTgt;

        PlayerControl target = Utils.GetPlayerById(targetId);

        if (!target || !target.NetTransform)
        {
            Utils.SendMessage($"[tpburst] target playerId={targetId} not found (or has no NetTransform).", player.PlayerId);
            return;
        }

        Main.Instance.StartCoroutine(TpBurstRun(rate, seconds, option, targetId, gated, player.PlayerId));
    }

    private static IEnumerator TpBurstRun(int rate, float seconds, SendOption option, byte targetId, bool gated, byte reporter)
    {
        // ⚠️ コマンド受付からコルーチン開始までの 1 フレームで対象が切断していることがある。
        //    ここで弾かないと以降のセットアップが NRE を投げ、TpBurstRunning が立つ前でも後でも
        //    実験が不能になる (立った後だと「already running」で二度と撃てなくなる) — 監査 2026-08-11。
        PlayerControl target = Utils.GetPlayerById(targetId);

        if (!target || !target.NetTransform)
        {
            Utils.SendMessage($"[tpburst] target playerId={targetId} vanished before the burst started — aborted.", reporter);
            yield break;
        }

        TpBurstRunning = true;
        TpBurstStopRequested = false;

        float start = Time.realtimeSinceStartup;
        var sent = 0;
        var lost = false;
        var total = 0;
        var pendingBefore = 0;
        var bypass = false;
        var lostReason = string.Empty;
        var bypassStolen = false;
        Vector2 lastDest = target.Pos();

        // 🔴 切断検出の裏取り用。`ReferenceEquals(connection, ...)` は本 repo の確立慣習
        // (`PacketRateGate.DetectReconnect` / `/nest dummy`) だが、Il2CppInterop のラッパーが
        // 同一 native 接続に対して作り直されると理屈上は偽陽性になりうる。このプローブの成果物は
        // 「切断したか否か」の二値そのものなので、偽陽性は**探している結論を無実験で捏造する**
        // 最悪の失敗モードになる。よって identity 単独では断定せず、null 化と ClientId の変化を
        // 併記して読み手が値で裏を取れるようにする (監査 2026-08-11)。
        int startClientId = AmongUsClient.Instance ? AmongUsClient.Instance.ClientId : -1;

        // ⚠️ StartWindowBypass は本来「ゲーム開始の復元シーケンス専用」の単一グローバル bool で、既存の
        // 消費者は無条件 false で閉じている。任意タイミングで撃てる dev コマンドはその排他前提を満たさない
        // ので、呼び出し前の値を保存/復元する (無条件 false は他窓の早期クローズ = 暗転バグ級の再導入)。
        // try の外で採る — try 内で採ると、採る前に投げた例外で finally が未初期化の値を書き戻す。
        bool prevBypass = PacketRateGate.StartWindowBypass;

        // 🔴 セットアップも含めて try で覆う (フラグ固着・bypass 漏れ・免除の付けっぱなしを一括で防ぐ)。
        try
        {
            TpBurstConnection = AmongUsClient.Instance.connection;
            CustomNetworkTransform nt = target.NetTransform;

            // 2u 離れた 2 点を往復させる: 1 発ごとが「本物の TP」相当の距離 (1.5u 超) でありながら、
            // ホストの居場所は開始地点から 1u 以内に留まるので壁抜け・場外落下の事故を作らない。
            Vector2 basePos = target.Pos();
            Vector2 posA = basePos + new Vector2(-1f, 0f);
            Vector2 posB = basePos + new Vector2(1f, 0f);
            lastDest = basePos;

            total = (int)(rate * seconds);
            pendingBefore = PacketRateGate.PendingCount;

            // Reliable のみゲート対象 (TryGate は Unreliable を素通しする) なので、bypass も Reliable 時だけ。
            bypass = !gated && option == SendOption.Reliable;

            // 自前の AFK / 不正移動検知が実験を汚さないようにする (Utils.TP と同じ免除。ただし 1 発ごとに
            // LateTask を張ると 800 本積むので、バースト全体を 1 回で覆う)。
            CheckInvalidMovementPatch.ExemptedPlayers.Add(targetId);
            AFKDetector.TempIgnoredPlayers.Add(targetId);

            string startLine = $"TPBURST start rate={rate}/s dur={seconds:F2}s total={total} opt={option} tgt={targetId} gated={gated} bypass={bypass} queued={pendingBefore} phase={(GameStates.IsLobby ? "lobby" : "ingame")} server={GameStates.CurrentServerType}";
            HealthLog.NoteAnom(startLine);
            Logger.Info(startLine, "DevCmd");
            Utils.SendMessage($"[tpburst] firing {total} SnapTo ({option}) @ {rate}/s for {seconds:F1}s at player {targetId}. '/tpburst stop' to halt.", reporter);

            start = Time.realtimeSinceStartup;

            if (bypass) PacketRateGate.StartWindowBypass = true;

            while (sent < total && !TpBurstStopRequested)
            {
                // 切断 = 実験の主要な観測イベント。「何発目・何秒目で落ちたか」が結果そのもの。
                if (!AmongUsClient.Instance)
                {
                    lost = true;
                    lostReason = "noclient";
                    break;
                }

                if (AmongUsClient.Instance.connection == null)
                {
                    lost = true;
                    lostReason = "nullconn";
                    break;
                }

                if (!ReferenceEquals(AmongUsClient.Instance.connection, TpBurstConnection))
                {
                    lost = true;
                    // ClientId が動いていれば本物の再接続。動いていなければラッパー churn の疑いがあるので
                    // 断定せず DCRING と突き合わせろ、と結果行に書かせる。
                    lostReason = AmongUsClient.Instance.ClientId != startClientId ? "identity+clientid" : "identity-only";
                    break;
                }

                // 🔴 bypass の横取り検出: MeetingStartWire / OnGameStartedPatch は StartWindowBypass を
                // 無条件 false で閉じる所有者なので、バースト窓の隙間で走るとワイヤレートが黙って
                // 25/s に崩壊する。PendingCount の前後比較では検出できない (崩壊後に整形された
                // パケットは素直にキューを流れるため) ので、フラグ自体を毎フレーム見張る。
                if (bypass && !PacketRateGate.StartWindowBypass) bypassStolen = true;

                if (!target || !nt) break;

                float elapsed = Time.realtimeSinceStartup - start;
                int due = Math.Min(total, (int)(elapsed * rate));
                var batchBytes = 0;

                while (sent < due)
                {
                    Vector2 dest = (sent & 1) == 0 ? posB : posA;

                    // ワイヤ形式は Utils.TP と同形に保つ (sid +328 / 本体 +8)。
                    nt.SnapTo(dest, (ushort)(nt.lastSequenceId + 328));
                    nt.SetDirtyBit(uint.MaxValue);
                    var newSid = (ushort)(nt.lastSequenceId + 8);
                    MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(nt.NetId, (byte)RpcCalls.SnapTo, option);
                    NetHelpers.WriteVector2(dest, w);
                    w.Write(newSid);
                    batchBytes += w.Length; // ⚠️ Finish 前に読む (Finish 後は Recycle 済み)。EndMessage 分だけ過少。
                    AmongUsClient.Instance.FinishRpcImmediately(w);
                    sent++;
                    lastDest = dest;
                }

                // 切断の per-name 内訳に出す (StartRpcImmediately 直呼び経路は CustomRpcSender を通らず、
                // 自分で RecordHostAction を呼ばないと DCTX/DCTAG の内訳から丸ごと消える)。
                if (batchBytes > 0) HealthLog.RecordHostAction("TpBurst", batchBytes, option.ToString());

                yield return null;
            }
        }
        finally
        {
            if (bypass) PacketRateGate.StartWindowBypass = prevBypass;

            // ⚠️ 順序は Utils.TP と同じく「LastPosition 更新 → 免除解除」。逆にすると
            // 「免除が外れた直後、まだ古い位置のまま無防備な 1 フレーム」が生まれる。
            // target が消えていても最後の転送先で更新しておく (古い座標のまま放置しない)。
            CheckInvalidMovementPatch.LastPosition[targetId] = lastDest;
            CheckInvalidMovementPatch.ExemptedPlayers.Remove(targetId);
            AFKDetector.TempIgnoredPlayers.Remove(targetId);
            TpBurstRunning = false;
        }

        float took = Time.realtimeSinceStartup - start;
        int pendingAfter = PacketRateGate.PendingCount;
        float achieved = took > 0f ? sent / took : 0f;

        // 陰性を「証拠」と誤読しないための無効化条件は結果に必ず併記する。
        var warn = string.Empty;
        if (bypass && pendingBefore > 0) warn += $" ⚠ gate queue was not empty ({pendingBefore}) — bypass only works while the queue is empty, so the wire rate may be far below the submitted rate.";
        if (pendingAfter > 0) warn += $" ⚠ {pendingAfter} packet(s) still queued — submitted != wire.";
        if (gated && option == SendOption.Reliable) warn += " ⚠ 'gated' arm: PacketRateGate shaped this to ~25/s — this is NOT a high-rate arm.";
        if (achieved < rate * 0.8f && !lost && !TpBurstStopRequested) warn += $" ⚠ achieved rate {achieved:F0}/s is well below the requested {rate}/s (frame-rate bound) — read the ladder by achieved, not requested.";
        if (bypassStolen) warn += " 🔴 StartWindowBypass was cleared mid-burst by another owner (MeetingStartWire / game start) — the wire rate collapsed to ~25/s partway through. ARM INVALID, re-run it.";
        // SetDirtyBit(uint.MaxValue) が誘発する位置ストリーム更新は batchBytes にも "TpBurst" 帰属にも
        // 乗らない。バーストは最大 4000 発なので、この付帯分が DCTX/DCTAG 内訳から丸ごと落ちると
        // 「犯人は別に居る」と誤読される (fan-out 調査で踏んだのと同型の取りこぼし)。
        warn += " ℹ bytes exclude SetDirtyBit-driven position-stream traffic (not attributed to \"TpBurst\" in DCTX/DCTAG).";

        string endLine = $"TPBURST end rate={rate}/s opt={option} tgt={targetId} submitted={sent}/{total} elapsed={took:F2}s achieved={achieved:F0}/s queued={pendingBefore}->{pendingAfter} lost={lost} lostReason={(lost ? lostReason : "-")} clientId={startClientId}->{(AmongUsClient.Instance ? AmongUsClient.Instance.ClientId : -1)} stopped={TpBurstStopRequested} phase={(GameStates.IsLobby ? "lobby" : "ingame")} server={GameStates.CurrentServerType}{warn}";
        HealthLog.NoteAnom(endLine);
        Logger.Info(endLine, "DevCmd");

        if (lost)
        {
            // ⚠️ identity-only (接続オブジェクトの同一性だけが変わり ClientId は据え置き) は
            // Il2CppInterop のラッパー作り直しでも起きうる形。ここで「レートが述語だ」と断定すると
            // 探している結論を捏造することになるので、断定は裏取り済みの場合だけにする。
            string verdict = lostReason == "identity-only"
                ? "⚠ identity-only change (ClientId unchanged) — this may be an Il2CPP wrapper churn false positive. Do NOT treat as a kick unless DCRING shows a real disconnect."
                : "Hacking with KICKRISK silent => SnapTo rate is a real predicate.";
            string dcLine = $"TPBURST CONNECTION LOST after {sent} SnapTo ({option}) in {took:F2}s (achieved {achieved:F0}/s) reason={lostReason} — check DCRING reason + KICKRISK. {verdict}";
            HealthLog.NoteAnom(dcLine);
            Logger.Warn(dcLine, "DevCmd");
            yield break;
        }

        Utils.SendMessage($"[tpburst] done: submitted {sent}/{total} {option} SnapTo in {took:F2}s (achieved {achieved:F0}/s), queued={pendingBefore}->{pendingAfter}, survived.{warn}", reporter);
    }

    private static WaveCannonWarning WcDbgProbeCno;

    private static void WcDbgCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        // 判別プローブ: /wcdbg rawname <chars> — 既存プローブCNOへ生 SetName RPC 1本だけ (outfit 無し・最小パケット・長い名前)
        if (args.Length >= 3 && args[1].Equals("rawname", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[2], out int rawChars))
        {
            if (WcDbgProbeCno?.playerControl == null)
            {
                Utils.SendMessage("[wcdbg] no probe CNO — run /wcdbg gate <n> first", player.PlayerId);
                return;
            }

            rawChars = Math.Clamp(rawChars, 1, 2000);
            string rawName = "<size=252%><color=#7a00ff>" + new string('█', rawChars);
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(WcDbgProbeCno.playerControl.NetId, (byte)RpcCalls.SetName, SendOption.Reliable);
            w.Write(WcDbgProbeCno.playerControl.Data.NetId);
            w.Write(rawName);
            w.Write(false);
            int rawLen = w.Length;
            AmongUsClient.Instance.FinishRpcImmediately(w);
            Logger.Info($"DevCmd /wcdbg rawname: SetName-raw {rawChars} chars, packet {rawLen}B", "DevCmd");
            Utils.SendMessage($"[wcdbg] raw SetName sent: {rawChars} chars, packet {rawLen}B", player.PlayerId);
            return;
        }

        // 判別プローブ: /wcdbg name <chars> — 既存プローブCNOへ SetName 単独送信 (spawn 無し・小パケット・長い名前)
        if (args.Length >= 3 && args[1].Equals("name", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[2], out int nameChars))
        {
            if (WcDbgProbeCno == null)
            {
                Utils.SendMessage("[wcdbg] no probe CNO — run /wcdbg gate <n> first", player.PlayerId);
                return;
            }

            nameChars = Math.Clamp(nameChars, 1, 2000);
            CustomNetObject.SpriteBudgetBypass = true;
            try { WcDbgProbeCno.RpcChangeSprite("<size=252%><color=#00ff7a>" + new string('█', nameChars)); }
            finally { CustomNetObject.SpriteBudgetBypass = false; }
            Logger.Info($"DevCmd /wcdbg name: SetName-only {nameChars} chars", "DevCmd");
            Utils.SendMessage($"[wcdbg] SetName-only sent: {nameChars} chars", player.PlayerId);
            return;
        }

        // 公式鯖キック閾値の二分探索プローブ: /wcdbg gate <chars> で指定文字数の █ 名前を持つ CNO をその場にスポーン
        if (args.Length >= 3 && args[1].Equals("gate", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[2], out int chars))
        {
            chars = Math.Clamp(chars, 1, 2000);
            Vector2 pos = player.GetTruePosition() + new Vector2(2f, 0f);
            // split モード: 同じ総量を 2 CNO に割って 1 パケットで送る (チャンク総量制限 vs 名前単体長制限の判別用)
            bool split = args.Length >= 4 && args[3].Equals("split", StringComparison.OrdinalIgnoreCase);
            CustomNetObject.SpriteBudgetBypass = true; // 境界計測用プローブなので公式鯖クランプを外す
            try
            {
                if (split)
                {
                    int half = Math.Max(1, chars / 2);
                    string s1 = "<size=252%><color=#ff7a00>" + new string('█', half);
                    Utils.CombineSendTimeLowering(() =>
                    {
                        _ = new WaveCannonWarning(pos, s1);
                        _ = new WaveCannonWarning(pos + new Vector2(0f, 1.5f), s1);
                    });
                }
                else
                {
                    string sprite = "<size=252%><color=#ff7a00>" + new string('█', chars);
                    Utils.CombineSendTimeLowering(() => WcDbgProbeCno = new WaveCannonWarning(pos, sprite));
                }
            }
            finally { CustomNetObject.SpriteBudgetBypass = false; }
            Logger.Info($"DevCmd /wcdbg gate: probe CNO {chars} chars split={split}", "DevCmd");
            Utils.SendMessage($"[wcdbg] probe CNO spawned: {chars} chars split={split}", player.PlayerId);
            return;
        }

        if (args.Length >= 2 && int.TryParse(args[1], out int mask))
            WaveCannon.DebugSkipMask = mask;

        int m = WaveCannon.DebugSkipMask;
        Logger.Info($"DevCmd /wcdbg: DebugSkipMask={m}", "DevCmd");
        Utils.SendMessage($"[wcdbg] WaveCannon DebugSkipMask={m} (1=sequence off, 2=skin off, 4=CNO off, 8=speed off)", player.PlayerId);
    }

    // メモリリーク調査用: 任意タイミングで Unity オブジェクト census を Health.log に記録 (BUG-20260706-01)
    private static void CensusCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        MemCensus.RunNow("manual");
        ManagedCensus.RunNow("manual");
        Logger.Info("DevCmd /census: snapshot requested", "DevCmd");
        Utils.SendMessage("[census] snapshot written to Health.log (CENSUS/CENSUSTOP/MHEAP*)", player.PlayerId);
    }

    private static void SizeCleanCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        int removed = SizeTestCNO.DespawnAll();
        removed += RiptideWaveSizeTestCNO.DespawnAll();
        Logger.Info($"DevCmd /sizeclean: removed={removed}", "DevCmd");
        Utils.SendMessage($"[sizeclean] Despawned {removed} size-test marker(s).", player.PlayerId);
    }

    private static void RipSizeCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        Vector2 origin = player.GetTruePosition() + new Vector2(2f, 0f);

        // 第2引数 mode:
        //   /ripsize            → 絶対モード row (20/30/40/50/80)
        //   /ripsize percent    → パーセントモード row (100%/150%/200%/250%/300%)
        //   /ripsize default    → タグ無しデフォルト 5 個並び
        //   /ripsize 200%       → 単発 percent 指定
        //   /ripsize 50         → 単発 absolute 指定
        if (args.Length >= 2)
        {
            string arg = args[1].Trim();
            if (arg.Equals("percent", System.StringComparison.OrdinalIgnoreCase))
            {
                int n1 = RiptideWaveSizeTestCNO.SpawnRow(origin, RiptideWaveSizeTestCNO.Mode.Percent);
                Logger.Info($"DevCmd /ripsize percent: spawned={n1}", "DevCmd");
                Utils.SendMessage($"[ripsize] Spawned {n1} Riptide sprites at 100%/150%/200%/250%/300% (25u apart). /sizeclean to remove.", player.PlayerId);
                return;
            }
            if (arg.Equals("default", System.StringComparison.OrdinalIgnoreCase))
            {
                int n2 = RiptideWaveSizeTestCNO.SpawnRow(origin, RiptideWaveSizeTestCNO.Mode.Default);
                Logger.Info($"DevCmd /ripsize default: spawned={n2}", "DevCmd");
                Utils.SendMessage($"[ripsize] Spawned {n2} Riptide sprites with NO size tag (TMP default, 8u apart). /sizeclean to remove.", player.PlayerId);
                return;
            }
            // "200%" / "50" 単発
            if (arg.EndsWith("%") && int.TryParse(arg.TrimEnd('%'), out int pct))
            {
                pct = Math.Clamp(pct, 10, 1000);
                RiptideWaveSizeTestCNO.SpawnOne(origin, pct + "%");
                Logger.Info($"DevCmd /ripsize: single percent={pct}%", "DevCmd");
                Utils.SendMessage($"[ripsize] Spawned 1 Riptide sprite at size={pct}%. Walk around to measure world width.", player.PlayerId);
                return;
            }
            if (int.TryParse(arg, out int abs))
            {
                abs = Math.Clamp(abs, 10, 2000);
                RiptideWaveSizeTestCNO.SpawnOne(origin, abs.ToString());
                Logger.Info($"DevCmd /ripsize: single absolute={abs}", "DevCmd");
                Utils.SendMessage($"[ripsize] Spawned 1 Riptide sprite at size={abs} (absolute). Walk around to measure world width.", player.PlayerId);
                return;
            }
        }

        int spawned = RiptideWaveSizeTestCNO.SpawnRow(origin, RiptideWaveSizeTestCNO.Mode.Absolute);
        Logger.Info($"DevCmd /ripsize absolute row: spawned={spawned}", "DevCmd");
        Utils.SendMessage($"[ripsize] Spawned {spawned} Riptide sprites at sizes 20/30/40/50/80 absolute (30u apart). Try '/ripsize percent' or '/ripsize default' too. /sizeclean to remove.", player.PlayerId);
    }

    // 公式鯖 anti-cheat のレート閾値がゲームフェーズで変わるかの実験、および PacketRateGate の実機検証用。
    // 非モッドクライアント1名へ idempotent なダミー SetRole RPC を N 発撃つ (実ゲーム状態は変えない)。
    // 'direct' は DataFlagRateLimiter の事前スロットルだけを迂回する (旧 InjectDebugBurst と同じ意味)。
    // PacketSplitPatch の関所 (グローバル Reliable レートゲート) は SendOrDisconnect を通る全パケットに
    // 効くため、direct 指定でもそちらは素通りできない — 旧版との挙動差そのものが今回のゲート検証の目的。
    private static void BurstCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        if (args.Length < 2 || !int.TryParse(args[1], out int count) || count <= 0)
        {
            Utils.SendMessage("[burst] Usage: /burst <count> [direct]", player.PlayerId);
            return;
        }

        bool direct = args.Length >= 3 && args[2].Equals("direct", System.StringComparison.OrdinalIgnoreCase);

        PlayerControl target = Main.EnumeratePlayerControls().FirstOrDefault(p => p != null && !p.AmOwner && p.OwnerId >= 0 && !p.IsModdedClient());
        if (target == null)
        {
            Utils.SendMessage("[burst] No non-modded client found to target (need a vanilla joiner).", player.PlayerId);
            return;
        }

        RoleTypes role = target.GetRoleTypes();
        Logger.Info($"DevCmd /burst: injecting {count} dummy SetRole RPCs to client {target.OwnerId} (direct={direct})", "PacketRateGate");

        CustomRpcSender sender = CustomRpcSender.Create("Burst", SendOption.Reliable, log: false).StartMessage(target.OwnerId);
        for (var i = 0; i < count; i++) sender.RpcSetRole(target, role, target.OwnerId, noRpcForSelf: false);
        sender.EndMessage();

        if (direct)
            sender.SendMessage();
        else
            DataFlagRateLimiter.Enqueue(() => sender.SendMessage(), SendOption.Reliable, count, cleanup: () => sender.SendMessage(dispose: true));

        Utils.SendMessage($"[burst] Sent {count} dummy SetRole RPC(s) to {target.GetRealName()} ({(direct ? "direct" : "throttled")}).", player.PlayerId);
    }

    // 公式鯖 anti-cheat の「1パケットに詰めた子メッセージの個数が効いている」説の検証プローブ。
    // CNO の per-player fan-out (CustomNetObject.cs:606-678) と同じ形の t26 エンベロープを合成し、
    // 宛先を自分自身の client id にして送る。ソロ(1人ホスト)でも撃てるのが本コマンドの存在意義 —
    // 実 fan-out は「ホスト以外の全員」をループするのでソロでは 1 本も出ず、実経路では再現できない。
    // バニラ自身が GetMaxMessagePackingLimit() を人数比例で持っている (= サーバ側に per-packet の
    // メッセージ数上限が存在する状況証拠) ため、人数が少ないほど予算が小さく感度が高い。
    //
    // 実験行列 (バイト数をほぼ一定に保って1変数ずつ動かす):
    //   A 個数 vs サイズ      : /nest 30 safe   ←→ /nest 3 safe pad=200
    //   B パケット内 vs 秒あたり: /nest 30 per=30 ←→ /nest 30 per=1 raw
    //   C 個数 vs 内容クラス   : /nest 20 real   ←→ /nest 20 safe   (中央の RPC だけ入れ替え)
    //
    // ⚠️ 自分宛の複製なので「宛先が異なる N クライアント」は模擬できない。陰性の意味は
    //    「per-packet の個数単独では不十分」であって仮説の否定ではない。
    //    実在しない client id を宛先にするとそれ自体が別のキック要因になるので使わない。
    // ── 実験用 spawn プローブ (2026-08-01) ────────────────────────────────────────
    // 「実プレイヤーの PlayerControl 宛 Data だけが違法・CNO の PlayerControl は合法」を説明できる
    // 単一変数モデルが3つあり、どれも「CNO の spawn」と「実プレイヤーの spawn」の差でしか動かせない:
    //   M-owner  : spawn の ownerId が実クライアント id か -2 か
    //   M-slot   : spawn 本体に書く PlayerId が実スロット (<200) か CNO 枠 (>=200) か
    //   M-retype : spawn 直後の「3本の再登録 spawn」(CustomNetObject.cs:563-575 / Vanilla 鯖限定) の有無
    // CustomNetObject 経由ではこの3つが全部固定されているので、ここで手組みして1つずつ振る。
    // 副次利得: DataFlagRateLimiter 待ちと `Standard && !InGame` の yield break を通らないので**ロビーで撃てる**。
    private static PlayerControl NestXProbe;
    private static string NestXProbeSpec = string.Empty;

    // /nest dummy (累計スポーン表オーバーフロー実験) の状態。dummies は「この接続で spawn を送った」
    // ものだけを保持し、接続オブジェクトが変わったら despawn を送らずローカル破棄する (P5 回避)。
    private static readonly List<PlayerControl> NestDummies = [];
    private static object NestDummyConnection;
    private static bool NestDummyRunning;
    private static bool NestDummyStopRequested;

    // GameData に登録されていない PlayerControl (手組み spawn / CNO) では `PlayerControl.Data` の
    // ゲッターが例外を投げる。プローブ系は必ずこれを通す。
    private static uint SafeDataNetId(PlayerControl pc)
    {
        try { return pc.Data != null ? pc.Data.NetId : 0U; }
        catch (Exception) { return 0U; }
    }

    /// <summary>その netId が「生きているプレイヤーの持ち物」(PlayerControl / NetworkedPlayerInfo /
    /// PlayerPhysics / CustomNetworkTransform) かどうか。/nest の破壊的アームの事故防止ゲート用。</summary>
    private static bool NestIsLivePlayerNetId(uint netId)
    {
        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
        {
            if (!pc || pc.OwnerId < 0) continue;
            if (pc.NetId == netId || SafeDataNetId(pc) == netId) return true;

            try
            {
                if (pc.MyPhysics && pc.MyPhysics.NetId == netId) return true;
                if (pc.NetTransform && pc.NetTransform.NetId == netId) return true;
            }
            catch (Exception) { /* best-effort */ }
        }

        return false;
    }

    // /nest dummy の 1 体分。xspawn の合法アーム (owner=-2 / pid固定 / CNO と同形の再登録付き) と同じ形で、
    // 見た目系の送信 (outfit / SetName / fan-out) を一切伴わない。
    private static PlayerControl NestSpawnDummy(bool noReg)
    {
        PlayerControl xpc = UnityEngine.Object.Instantiate(AmongUsClient.Instance.PlayerPrefab, Vector2.zero, Quaternion.identity);
        xpc.PlayerId = 201;
        xpc.isNew = false;
        xpc.notRealPlayer = true;

        try { xpc.NetTransform.SnapTo(new Vector2(50f, 50f)); }
        catch (Exception e) { Utils.ThrowException(e); }

        AmongUsClient.Instance.NetIdCnt += 1U;
        MessageWriter spawn = MessageWriter.Get(SendOption.Reliable);
        spawn.StartMessage(5);
        spawn.Write(AmongUsClient.Instance.GameId);
        spawn.StartMessage(4);
        var spawnMsg = AmongUsClient.Instance.CreateSpawnMessage(xpc, -2, SpawnFlags.None);
        spawnMsg.SerializeValues(spawn);
        spawn.EndMessage();

        if (!noReg && GameStates.CurrentServerType == GameStates.ServerType.Vanilla)
        {
            // CustomNetObject.cs の再登録 spawn と同一 (本番 CNO とバイト同形にするため既定で送る)
            for (uint i = 1; i <= 3; ++i)
            {
                spawn.StartMessage(4);
                spawn.WritePacked(2U);
                spawn.WritePacked(-2);
                spawn.Write((byte)SpawnFlags.None);
                spawn.WritePacked(1);
                spawn.WritePacked(AmongUsClient.Instance.NetIdCnt - i);
                spawn.StartMessage(1);
                spawn.EndMessage();
                spawn.EndMessage();
            }
        }

        spawn.EndMessage();
        HealthLog.RecordHostAction("NestDummy", spawn.Length, "Reliable");
        AmongUsClient.Instance.SendOrDisconnect(spawn);
        spawn.Recycle();

        if (PlayerControl.AllPlayerControls.Contains(xpc)) PlayerControl.AllPlayerControls.Remove(xpc);
        xpc.cosmetics.colorBlindText.color = Color.clear;
        xpc.OwnerId = -2;
        return xpc;
    }

    private static IEnumerator NestDummyRun(int total, float per, int stepSize, float pause, bool noReg, byte reporter)
    {
        uint startNetIdCnt = AmongUsClient.Instance.NetIdCnt;
        string startLine = $"NEST dummy start total={total} per={per}s step={stepSize} pause={pause}s noreg={noReg} netIdCnt={startNetIdCnt} already={NestDummies.Count} phase={(GameStates.IsLobby ? "lobby" : "ingame")} server={GameStates.CurrentServerType}";
        HealthLog.NoteAnom(startLine);
        Logger.Info(startLine, "DevCmd");
        Utils.SendMessage($"[nest] dummy run: {total} naked dummies @ {per}s, checkpoint every {stepSize} (pause {pause}s). '/nest dummy stop' to halt, '/nest dummy clear' to clean up.", reporter);

        var spawnedThisRun = 0;

        while (spawnedThisRun < total && !NestDummyStopRequested)
        {
            if (!AmongUsClient.Instance || !ReferenceEquals(AmongUsClient.Instance.connection, NestDummyConnection))
            {
                // 切断 = 実験の主要な観測イベント。何体目で落ちたかが結果そのもの。
                string dcLine = $"NEST dummy CONNECTION LOST after {NestDummies.Count} dummies (run={spawnedThisRun}) — check DCRING reason + KICKRISK. Hacking with KICKRISK silent => table-cap / 6th-rule evidence.";
                HealthLog.NoteAnom(dcLine);
                Logger.Warn(dcLine, "DevCmd");
                break;
            }

            PlayerControl dummy = NestSpawnDummy(noReg);
            if (dummy) NestDummies.Add(dummy);
            spawnedThisRun++;

            if (NestDummies.Count % stepSize == 0)
            {
                string cp = $"NEST dummy checkpoint spawned={NestDummies.Count} netIdCnt={AmongUsClient.Instance.NetIdCnt} pending={PacketRateGate.PendingCount}";
                HealthLog.NoteAnom(cp);
                Logger.Info(cp, "DevCmd");
                Utils.SendMessage($"[nest] dummy checkpoint: {NestDummies.Count} spawned, netIdCnt={AmongUsClient.Instance.NetIdCnt}, pending={PacketRateGate.PendingCount}.", reporter);
                yield return new WaitForSecondsRealtime(pause);
            }
            else
                yield return new WaitForSecondsRealtime(per);
        }

        NestDummyRunning = false;
        string endLine = $"NEST dummy run ended spawned={NestDummies.Count} run={spawnedThisRun} stopRequested={NestDummyStopRequested}";
        HealthLog.NoteAnom(endLine);
        Logger.Info(endLine, "DevCmd");

        if (AmongUsClient.Instance && ReferenceEquals(AmongUsClient.Instance.connection, NestDummyConnection))
            Utils.SendMessage($"[nest] dummy run ended at {NestDummies.Count}. Run '/nest dummy clear' when done observing.", reporter);
    }

    private static IEnumerator NestDummyClear(byte reporter)
    {
        // 実行中の run に stop を伝えてから 1 フレーム待って合流する
        yield return null;

        int count = NestDummies.Count;
        var sent = 0;
        // ⚠️ 接続が変わっていたら despawn を「送らない」。新しい接続のサーバーはこの netId を知らないので、
        // t5 ブロードキャスト Despawn は P5 (未 spawn netId × t5 = 100% Hacking キック) になる。
        bool sameConnection = AmongUsClient.Instance && ReferenceEquals(AmongUsClient.Instance.connection, NestDummyConnection);

        foreach (PlayerControl dummy in NestDummies.ToArray())
        {
            if (!dummy) continue;

            if (sameConnection && AmongUsClient.Instance && ReferenceEquals(AmongUsClient.Instance.connection, NestDummyConnection))
            {
                MessageWriter dsp = MessageWriter.Get(SendOption.Reliable);
                dsp.StartMessage(5);
                dsp.Write(AmongUsClient.Instance.GameId);
                dsp.StartMessage(5);
                dsp.WritePacked(dummy.NetId);
                dsp.EndMessage();
                dsp.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(dsp);
                dsp.Recycle();
                sent++;

                // ゲート予算 (25 Reliable/秒) を despawn で食い潰さないよう小刻みに休む
                if (sent % 20 == 0) yield return new WaitForSecondsRealtime(1f);
            }

            try
            {
                if (dummy.Data != null) dummy.Data.ClearDirtyBits();
            }
            catch (Exception) { /* GameData 未登録のダミーでは Data ゲッターが投げる */ }

            AmongUsClient.Instance?.RemoveNetObject(dummy);
            UnityEngine.Object.Destroy(dummy.gameObject);
        }

        NestDummies.Clear();
        NestDummyRunning = false;
        string line = $"NEST dummy clear count={count} despawnsSent={sent} sameConnection={sameConnection}";
        HealthLog.NoteAnom(line);
        Logger.Info(line, "DevCmd");
        Utils.SendMessage($"[nest] dummy cleared: {count} destroyed, {sent} despawns sent{(sameConnection ? string.Empty : " (connection changed — local destroy only)")}.", reporter);
    }

    private static void NestCommand(PlayerControl player, string text, string[] args)
    {
        if (!player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev()) return;

        // PacketSplitPatch の DetectThreshold(1000B) 未満に保ち、サイズ交絡と関所の再分割を排除する
        const int SizeCap = 900;

        int packingLimit = AmongUsClient.Instance.GetMaxMessagePackingLimit();
        int playerCount = GameData.Instance ? GameData.Instance.PlayerCount : -1;

        // /nest limit — バニラ側の per-packet メッセージ数上限を実測する (梯子の基準値)
        if (args.Length >= 2 && args[1].Equals("limit", StringComparison.OrdinalIgnoreCase))
        {
            string limitLine = $"NEST limit packing={packingLimit} players={playerCount} pcs={PlayerControl.AllPlayerControls.Count} phase={(GameStates.IsLobby ? "lobby" : "ingame")} server={GameStates.CurrentServerType}";
            HealthLog.NoteAnom(limitLine);
            Logger.Info(limitLine, "DevCmd");
            Utils.SendMessage($"[nest] GetMaxMessagePackingLimit()={packingLimit} (players={playerCount}, pcs={PlayerControl.AllPlayerControls.Count})", player.PlayerId);
            return;
        }

        // /nest info — netId / ownerId / spawnId の実測ダンプ。パケットは 1 本も出さない。
        // 「実プレイヤーの PlayerControl 宛 Data だけが違法」を説明する候補モデルのうち、
        // 所有権 (M-owner) が原理的に成立しうるかを、実験を撃つ前に実値で絞るための計器。
        if (args.Length >= 2 && args[1].Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            PlayerControl me = PlayerControl.LocalPlayer;
            var sb = new StringBuilder();

            void Describe(string label, InnerNetObject ino)
            {
                if (!ino) sb.Append($" | {label}=<null>");
                else sb.Append($" | {label} net={ino.NetId} own={ino.OwnerId} spawn={ino.SpawnId}");
            }

            sb.Append($"NEST info client={AmongUsClient.Instance.ClientId} host={AmongUsClient.Instance.HostId} game={AmongUsClient.Instance.GameId} netIdCnt={AmongUsClient.Instance.NetIdCnt} server={GameStates.CurrentServerType} phase={(GameStates.IsLobby ? "lobby" : "ingame")}");
            Describe("self.pc", me);
            Describe("self.data", me.Data);
            Describe("self.nt", me.NetTransform);
            Describe("self.phys", me.MyPhysics);

            foreach (PlayerControl pc in Main.EnumeratePlayerControls())
            {
                if (pc.AmOwner) continue;

                Describe($"other{pc.PlayerId}.pc", pc);
                Describe($"other{pc.PlayerId}.data", pc.Data);
            }

            PlayerControl cno = WcDbgProbeCno?.playerControl;

            if (cno)
            {
                Describe($"cno(pid{cno.PlayerId}).pc", cno);
                Describe("cno.nt", cno.NetTransform);
                sb.Append($" | cno.dataNet={SafeDataNetId(cno)}");
            }

            if (NestXProbe)
            {
                Describe($"xprobe(pid{NestXProbe.PlayerId}).pc", NestXProbe);
                Describe("xprobe.nt", NestXProbe.NetTransform);
                sb.Append($" | xprobe.dataNet={SafeDataNetId(NestXProbe)} xprobeSpec={NestXProbeSpec}");
            }

            string info = sb.ToString();
            HealthLog.NoteAnom(info);
            Logger.Info(info, "DevCmd");
            // チャットは 1 通の上限があるので 2 通に割る (self 系 / プローブ系)。
            // `.Data` は GameData 未登録だとゲッターが投げるので、チャット表示側も SafeDataNetId を通す
            // (LocalPlayer なら実害は無いが、診断ツール自身が例外で落ちるのは本末転倒)。
            Utils.SendMessage($"[nest] self pc=net{me.NetId}/own{me.OwnerId}/pid{me.PlayerId} data=net{SafeDataNetId(me)} nt=net{me.NetTransform.NetId}/own{me.NetTransform.OwnerId} phys=net{me.MyPhysics.NetId}/own{me.MyPhysics.OwnerId} | client={AmongUsClient.Instance.ClientId} netIdCnt={AmongUsClient.Instance.NetIdCnt}", player.PlayerId);
            Utils.SendMessage($"[nest] cno={(cno ? $"net{cno.NetId}/own{cno.OwnerId}/pid{cno.PlayerId}" : "-")} xprobe={(NestXProbe ? $"net{NestXProbe.NetId}/own{NestXProbe.OwnerId}/pid{NestXProbe.PlayerId} [{NestXProbeSpec}]" : "-")} (full dump in Health/Timeline)", player.PlayerId);
            return;
        }

        // /nest xspawn [owner=none|self|<int>] [pid=<0-255>|self] [noreg]
        // CustomNetObject を通さずに PlayerControl プレハブを手組み spawn する。CNO と唯一違うのは
        // 「ownerId」「spawn 本体の PlayerId」「3本の再登録 spawn の有無」を任意に振れる点だけ。
        // ⚠️ 再登録 spawn (`noreg` を付けない既定) は CNO / 偽死体と**バイト単位で同形**にするために要る。
        //    省いた形を既定にすると全アームが「CNO 無傷」セルとの1ビット分離でなくなる。
        if (args.Length >= 2 && args[1].Equals("xspawn", StringComparison.OrdinalIgnoreCase))
        {
            if (NestXProbe)
            {
                Utils.SendMessage($"[nest] xprobe already exists ({NestXProbeSpec}, net={NestXProbe.NetId}) — run '/nest xdespawn' first.", player.PlayerId);
                return;
            }

            int spawnOwner = -2;
            byte spawnPid = 201;
            var noReg = false;

            for (var i = 2; i < args.Length; i++)
            {
                string a = args[i];

                if (a.Equals("noreg", StringComparison.OrdinalIgnoreCase)) noReg = true;
                else if (a.StartsWith("owner=", StringComparison.OrdinalIgnoreCase))
                {
                    string v = a[6..];
                    if (v.Equals("self", StringComparison.OrdinalIgnoreCase)) spawnOwner = PlayerControl.LocalPlayer.OwnerId;
                    else if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) spawnOwner = -2;
                    else if (int.TryParse(v, out int o)) spawnOwner = o;
                }
                else if (a.StartsWith("pid=", StringComparison.OrdinalIgnoreCase))
                {
                    string v = a[4..];
                    if (v.Equals("self", StringComparison.OrdinalIgnoreCase)) spawnPid = PlayerControl.LocalPlayer.PlayerId;
                    else if (byte.TryParse(v, out byte p)) spawnPid = p;
                }
            }

            PlayerControl xpc = UnityEngine.Object.Instantiate(AmongUsClient.Instance.PlayerPrefab, Vector2.zero, Quaternion.identity);
            xpc.PlayerId = spawnPid;
            xpc.isNew = false;
            xpc.notRealPlayer = true;

            try { xpc.NetTransform.SnapTo(new Vector2(50f, 50f)); }
            catch (Exception e) { Utils.ThrowException(e); }

            AmongUsClient.Instance.NetIdCnt += 1U;
            MessageWriter spawn = MessageWriter.Get(SendOption.Reliable);
            spawn.StartMessage(5);
            spawn.Write(AmongUsClient.Instance.GameId);
            spawn.StartMessage(4);
            var spawnMsg = AmongUsClient.Instance.CreateSpawnMessage(xpc, spawnOwner, SpawnFlags.None);
            spawnMsg.SerializeValues(spawn);
            spawn.EndMessage();

            if (!noReg && GameStates.CurrentServerType == GameStates.ServerType.Vanilla)
            {
                // CustomNetObject.cs:563-575 / Utils.cs:5575-5586 と同一。直前に払い出した3つの netId
                // (PlayerControl / PlayerPhysics / CustomNetworkTransform) を spawnId=2・ownerId=-2 の
                // 別 spawn として再宣言する。**公式鯖 (Vanilla) のときだけ**送っている点が重要な手掛かり。
                for (uint i = 1; i <= 3; ++i)
                {
                    spawn.StartMessage(4);
                    spawn.WritePacked(2U);
                    spawn.WritePacked(-2);
                    spawn.Write((byte)SpawnFlags.None);
                    spawn.WritePacked(1);
                    spawn.WritePacked(AmongUsClient.Instance.NetIdCnt - i);
                    spawn.StartMessage(1);
                    spawn.EndMessage();
                    spawn.EndMessage();
                }
            }

            spawn.EndMessage();
            HealthLog.RecordHostAction("NestXSpawn", spawn.Length, "Reliable");
            AmongUsClient.Instance.SendOrDisconnect(spawn);
            int spawnLen = spawn.Length;
            spawn.Recycle();

            // CNO と同じ後始末。PlayerId が実スロット (<200) のアームでは、これを怠ると
            // Main.EnumeratePlayerControls() が偽物を「実プレイヤー」として拾い、他系統が壊れる。
            if (PlayerControl.AllPlayerControls.Contains(xpc)) PlayerControl.AllPlayerControls.Remove(xpc);
            xpc.cosmetics.colorBlindText.color = Color.clear;

            // ⚠️ owner=self アームの交絡除去: ローカルの OwnerId をそのままにすると AmOwner=true になり、
            // PlayerControl.FixedUpdate のローカルプレイヤー分岐とエンジン側 dirty walk の送信が走る。
            // 実験対象は「ワイヤに書いた ownerId」なので、ローカルは CNO と同じ -2 に戻して挙動を揃える。
            xpc.OwnerId = -2;

            NestXProbe = xpc;
            NestXProbeSpec = $"owner={spawnOwner} pid={spawnPid} reg={!noReg}";
            string xline = $"NEST xspawn {NestXProbeSpec} pcNet={xpc.NetId} physNet={xpc.MyPhysics.NetId} ntNet={xpc.NetTransform.NetId} dataNet={SafeDataNetId(xpc)} len={spawnLen} phase={(GameStates.IsLobby ? "lobby" : "ingame")} server={GameStates.CurrentServerType}";
            HealthLog.NoteAnom(xline);
            Logger.Info(xline, "DevCmd");
            Utils.SendMessage($"[nest] xprobe spawned: {NestXProbeSpec} net={xpc.NetId} ({spawnLen}B). Now fire e.g. '/nest 1 thin tgt=xprobe'.", player.PlayerId);
            return;
        }

        if (args.Length >= 2 && args[1].Equals("xdespawn", StringComparison.OrdinalIgnoreCase))
        {
            if (!NestXProbe)
            {
                NestXProbe = null;
                Utils.SendMessage("[nest] no xprobe to despawn.", player.PlayerId);
                return;
            }

            PlayerControl dead = NestXProbe;
            uint deadNet = dead.NetId;
            MessageWriter dsp = MessageWriter.Get(SendOption.Reliable);
            dsp.StartMessage(5);
            dsp.Write(AmongUsClient.Instance.GameId);
            dsp.StartMessage(5);
            dsp.WritePacked(deadNet);
            dsp.EndMessage();
            dsp.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(dsp);
            dsp.Recycle();

            // Utils.RpcCreateDeadBody と同じ後始末 (RemoveNetObject(Data) は in-flight 参照を壊すので使わない)
            try
            {
                if (dead.Data != null) dead.Data.ClearDirtyBits();
            }
            catch (Exception) { /* GameData 未登録のプローブでは Data ゲッターが投げる */ }

            AmongUsClient.Instance.RemoveNetObject(dead);
            UnityEngine.Object.Destroy(dead.gameObject);
            NestXProbe = null;
            HealthLog.NoteAnom($"NEST xdespawn net={deadNet} spec={NestXProbeSpec}");
            Utils.SendMessage($"[nest] xprobe despawned (net={deadNet}).", player.PlayerId);
            NestXProbeSpec = string.Empty;
            return;
        }

        // /nest dummy <count|stop|clear> [per=0.4] [step=50] [pause=10] [noreg]
        // 累計スポーン表オーバーフロー説 (docs/official-server-model.md §5-2 残渣) の計器。
        // xspawn と同形の raw spawn (owner=-2 / pid=201 / 裸 = outfit/SetName/fan-out ゼロ) を per 秒間隔で
        // count 体積み、step 体ごとに pause 秒停止して netIdCnt を記帳する。ワイヤに出るのは spawn 電文
        // だけなので、動く変数は「サーバーの表に登録された netId の累計」のみ。KICKRISK は常時稼働。
        if (args.Length >= 2 && args[1].Equals("dummy", StringComparison.OrdinalIgnoreCase))
        {
            string sub = args.Length >= 3 ? args[2].ToLowerInvariant() : string.Empty;

            if (sub == "stop")
            {
                NestDummyStopRequested = true;
                Utils.SendMessage($"[nest] dummy run stop requested (spawned so far: {NestDummies.Count}).", player.PlayerId);
                return;
            }

            if (sub == "clear")
            {
                NestDummyStopRequested = true;
                Main.Instance.StartCoroutine(NestDummyClear(player.PlayerId));
                return;
            }

            if (NestDummyRunning)
            {
                Utils.SendMessage($"[nest] dummy run already active ({NestDummies.Count} spawned) — '/nest dummy stop' first.", player.PlayerId);
                return;
            }

            if (!int.TryParse(sub, out int dummyTotal) || dummyTotal < 1)
            {
                Utils.SendMessage("[nest] Usage: /nest dummy <count|stop|clear> [per=0.4] [step=50] [pause=10] [noreg]", player.PlayerId);
                return;
            }

            // ホスト操作不能リスク (実測: 50〜100体で危険域) を考え、1回の総数はハードキャップする。
            const int HardCap = 300;

            if (dummyTotal > HardCap)
            {
                Utils.SendMessage($"[nest] count capped at {HardCap}.", player.PlayerId);
                dummyTotal = HardCap;
            }

            var dummyPer = 0.4f;
            var stepSize = 50;
            var stepPause = 10f;
            var dummyNoReg = false;

            for (var i = 3; i < args.Length; i++)
            {
                string a = args[i];

                if (a.Equals("noreg", StringComparison.OrdinalIgnoreCase)) dummyNoReg = true;
                else if (a.StartsWith("per=", StringComparison.OrdinalIgnoreCase) && float.TryParse(a[4..], out float p) && p >= 0.1f) dummyPer = p;
                else if (a.StartsWith("step=", StringComparison.OrdinalIgnoreCase) && int.TryParse(a[5..], out int s) && s >= 1) stepSize = s;
                else if (a.StartsWith("pause=", StringComparison.OrdinalIgnoreCase) && float.TryParse(a[6..], out float w) && w >= 0f) stepPause = w;
            }

            NestDummyRunning = true;
            NestDummyStopRequested = false;
            NestDummyConnection = AmongUsClient.Instance.connection;
            Main.Instance.StartCoroutine(NestDummyRun(dummyTotal, dummyPer, stepSize, stepPause, dummyNoReg, player.PlayerId));
            return;
        }

        // /nest retype <netId|self|selfnt|selfphys|xprobe>
        // CNO / 偽死体が spawn 直後に送っている「再登録 spawn」(spawnId=2 / ownerId=-2 / 1コンポーネント /
        // 本体 0 バイト) を**任意の netId に対して単発で**送る。M-retype の**逆向き**テスト:
        //   `/nest retype self` → その後 `/nest 1 thin` が**無傷になったら M-retype 確定**
        //   (100% 蹴られていたアームが、対象 netId の種別を上書きしただけで通るということ)。
        if (args.Length >= 2 && args[1].Equals("retype", StringComparison.OrdinalIgnoreCase))
        {
            PlayerControl me = PlayerControl.LocalPlayer;
            string what = args.Length >= 3 ? args[2].ToLowerInvariant() : "self";
            uint target;

            switch (what)
            {
                case "self":
                    target = me.NetId;
                    break;
                case "selfnt":
                    target = me.NetTransform.NetId;
                    break;
                case "selfphys":
                    target = me.MyPhysics.NetId;
                    break;
                case "xprobe":
                    if (!NestXProbe)
                    {
                        Utils.SendMessage("[nest] retype xprobe needs '/nest xspawn' first.", player.PlayerId);
                        return;
                    }

                    target = NestXProbe.NetId;
                    break;
                default:
                    if (!uint.TryParse(what, out target))
                    {
                        Utils.SendMessage("[nest] Usage: /nest retype <netId|self|selfnt|selfphys|xprobe>", player.PlayerId);
                        return;
                    }

                    break;
            }

            // ⚠️ 既知の 100% 自爆ターゲット: 生きているプレイヤーの 4 オブジェクト
            // (PlayerControl / PlayerPhysics / CustomNetworkTransform / NetworkedPlayerInfo)。
            // これらの再宣言は P3 = 25B で即 Hacking キック。`op=despawn tgt=self` が force を要求するのに
            // こちらが素通りだと、事故で 1 ロビー潰す非対称ができるので同じゲートを張る。
            // ⚠️ 自分だけでなく**全プレイヤー**を見る。`/nest info` が他人の netId をダンプするので、
            // 「info で他人の netId を調べる → retype で狙う」の2手経路が無警告で通ってしまう (2026-08-01 監査)。
            bool lethal = NestIsLivePlayerNetId(target);

            if (lethal && !args.Any(a => a.Equals("force", StringComparison.OrdinalIgnoreCase)))
            {
                Utils.SendMessage($"[nest] retype netId={target} is a LIVE player object — this is a known 100% Hacking kick (P3). Add 'force' if that is intended.", player.PlayerId);
                return;
            }

            MessageWriter rt = MessageWriter.Get(SendOption.Reliable);
            rt.StartMessage(5);
            rt.Write(AmongUsClient.Instance.GameId);
            rt.StartMessage(4);
            rt.WritePacked(2U);
            rt.WritePacked(-2);
            rt.Write((byte)SpawnFlags.None);
            rt.WritePacked(1);
            rt.WritePacked(target);
            rt.StartMessage(1);
            rt.EndMessage();
            rt.EndMessage();
            rt.EndMessage();
            int retypeLen = rt.Length;
            HealthLog.RecordHostAction("NestRetype", retypeLen, "Reliable");
            AmongUsClient.Instance.SendOrDisconnect(rt);
            rt.Recycle();
            string rline = $"NEST retype target={what} netId={target} len={retypeLen} phase={(GameStates.IsLobby ? "lobby" : "ingame")} server={GameStates.CurrentServerType}";
            HealthLog.NoteAnom(rline);
            Logger.Info(rline, "DevCmd");
            Utils.SendMessage($"[nest] retype sent for netId={target} ({what}, {retypeLen}B). Wait ~15s, then re-fire the arm you want to test.", player.PlayerId);
            return;
        }

        if (args.Length < 2 || !int.TryParse(args[1], out int total) || total <= 0)
        {
            Utils.SendMessage("[nest] Usage: /nest <total> [real|safe|thin|none] [via=t6self|t5|bare6] [tgt=self|cno|other|selfdata|xprobe|bogus|selfnt|selfphys] [dst=self|real|spread] [op=data|despawn] [body=<0-255>] [per=<k>] [pad=<chars>] [spoof] [raw] [force]  |  /nest limit|info|xspawn|xdespawn", player.PlayerId);
            return;
        }

        total = Math.Clamp(total, 1, 200);
        var payload = "real";
        // 子の「乗り物」— 2026-07-31 実測で t6self は子1個・26B でも即 Hacking キックされることが判明したので、
        // 個数軸を測るには合法な乗り物 (t5=ブロードキャスト GameData) が要る。bare6 は t26 包装の有無の切り分け用。
        var via = "t6self";
        // Data の対象 NetObject。thin/none は既定で自分の PlayerControl、real/safe は常にプローブ CNO。
        // `tgt=cno` で thin もプローブ CNO を対象にできる (「Data 1枚が違法」か「自分宛 Data が違法」かの分離用)。
        // `tgt=other` は**実在する他プレイヤーの PlayerControl** を対象にする (2026-08-01 追加)。
        // 既に取れている 2 本 (self=100%キック / CNO=無傷) は「自分のか否か」と「実プレイヤーのか否か」が
        // 交絡したままなので、この 3 本目で分離する:
        //   蹴られる → 実プレイヤーの PlayerControl 全般が保護対象 (自分固有ではない)
        //   無傷     → 自分固有の詐称防止ルール (自分の netId / 自クライアント所有の netId)
        // ⚠️ 対象は tag1 の中身であって宛先ではない。`dst=self` 固定なのでパケットは他人の端末へ飛ばない。
        var tgt = "self";
        // `tgt=` が明示指定されたか (既定の "self" と区別する)。`dst=spread` の陽性コントロール専用。
        var tgtExplicit = false;
        // 宛先の配り方 — 残る2軸のうち「宛先が相異なる数 (fan-out 幅)」用 (もう1軸は spoof のマスカレード)。
        //   self   = 全子が自分の OwnerId (幅1。個数だけを動かす既定)
        //   real   = 実在する非ホストクライアントへ順に配る (実 fan-out と同じ形。実プレイヤーが要る)
        //   spread = 存在しない client id を1つずつずらして配る (ソロで幅だけを動かす。
        //            ⚠️ 使う前に必ず N=1 の陰性コントロールを撃つこと — 存在しない宛先自体が
        //            違法なら幅の測定にならない)
        var dst = "self";
        int per = total;
        var pad = 0;
        var raw = false;
        var force = false;
        // spoof: 各子の先頭 Data に「その子の宛先プレイヤーの実 PlayerId」を書く (最後の Data で CNO 自身の値へ復元)。
        // = 本番の CNO per-player fan-out (`CustomNetObject.cs:647-660` の可視性マスカレード) と完全同形。
        // これが無いと「幅」アームは本番と形が違い、真犯人がマスカレードだった場合に偽陰性を出す (2026-08-01 に発見)。
        var spoof = false;
        // Data 本体に書く 1 バイト (= その PlayerControl が指すプレイヤー枠)。既定 -1 は「対象の実 PlayerId」。
        // spawn 本体の PlayerId (`xspawn pid=`) と本体バイトは**別の変数**なので、明示的に固定できないと
        // Arm S (spawn の PlayerId を振る) が「本体バイトも一緒に動いた」で交絡する。
        var body = -1;
        // op=despawn : Data(tag1) ではなく Despawn(tag5 inner) を撃つ。「保護は Data 限定か、netId 全体か」の判別用。
        var op = "data";

        for (var i = 2; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("real", StringComparison.OrdinalIgnoreCase)) payload = "real";
            else if (a.Equals("safe", StringComparison.OrdinalIgnoreCase)) payload = "safe";
            else if (a.Equals("thin", StringComparison.OrdinalIgnoreCase)) payload = "thin";
            else if (a.Equals("none", StringComparison.OrdinalIgnoreCase)) payload = "none";
            else if (a.StartsWith("via=", StringComparison.OrdinalIgnoreCase)) via = a[4..].ToLowerInvariant();
            else if (a.StartsWith("tgt=", StringComparison.OrdinalIgnoreCase))
            {
                tgt = a[4..].ToLowerInvariant();
                tgtExplicit = true;
            }
            else if (a.StartsWith("dst=", StringComparison.OrdinalIgnoreCase)) dst = a[4..].ToLowerInvariant();
            else if (a.Equals("spoof", StringComparison.OrdinalIgnoreCase)) spoof = true;
            else if (a.Equals("raw", StringComparison.OrdinalIgnoreCase)) raw = true;
            else if (a.Equals("force", StringComparison.OrdinalIgnoreCase)) force = true;
            else if (a.StartsWith("per=", StringComparison.OrdinalIgnoreCase) && int.TryParse(a[4..], out int p)) per = Math.Clamp(p, 1, total);
            else if (a.StartsWith("pad=", StringComparison.OrdinalIgnoreCase) && int.TryParse(a[4..], out int pd)) pad = Math.Clamp(pd, 0, 400);
            else if (a.StartsWith("body=", StringComparison.OrdinalIgnoreCase) && int.TryParse(a[5..], out int bd)) body = Math.Clamp(bd, 0, 255);
            else if (a.StartsWith("op=", StringComparison.OrdinalIgnoreCase)) op = a[3..].ToLowerInvariant();
        }

        // real/safe の子メッセージの参照先はプローブ CNO に固定する (自分の PlayerControl を書き換えないため)。
        // thin は自分自身の Data を冪等に書くだけなので CNO 不要 = ロビーでも撃てる。
        // ⚠️ 宛先が自分以外のときは必ずプローブ CNO を参照先にする — 自分以外へ飛ぶパケットの中身が
        // 「使い捨てのプローブ」以外を指すことが構造的に起きないようにするための不変条件
        // (現行の thin ペイロードは冪等で無害だが、それは偶然であって設計ではない)。
        // `dst=spread` の**陽性コントロール**専用の例外 (2026-08-01)。
        // 通常 `dst != "self"` では Data の対象をプローブ CNO へ強制的に差し替えるが、それだと
        // spread アームは合法な中身しか運べず、「サーバは存在しない宛先の子をそもそもパースしているのか」を
        // 判定できない = 幅の梯子が全部偽陰性になりうる (2026-08-01 に実際に詰まった)。
        // spread の宛先は selfClient+100000 起点で接続中 id を明示除外した**存在しないクライアント**であり、
        // thin の中身は自分の Data を冪等に書くだけなので、実在の第三者へ意味のある中身が飛ぶことはない
        // = 3748-3750 の不変条件は保たれる。
        //   `/nest 1 thin tgt=self dst=spread` が蹴られる → 無効宛先の子も検査されている = 梯子は有効
        //   無傷 → 同じロビーで `/nest 1 thin` を撃ち、蹴られることを確認する (アームの生存確認)。
        //          そこも無傷ならビルドかサーバ側規則が変わっている。
        var spreadSelfControl = tgtExplicit && tgt == "self" && dst == "spread" && payload == "thin";
        // 対象 NetObject を明示指定するアーム群。プローブ CNO を必要とせず、既存キックアーム
        // (`/nest 1 thin`) との差分を「対象 netId (と、その netId の作られ方)」1点だけに保つ。
        var explicitTarget = tgt is "other" or "selfdata" or "xprobe" or "bogus" or "selfnt" or "selfphys";
        var needsProbe = !spreadSelfControl && !explicitTarget && (payload is "real" or "safe" || tgt == "cno" || dst != "self");
        PlayerControl probe = null;
        PlayerControl otherPc = null;

        if (explicitTarget)
        {
            // 乗り物・宛先・ペイロードは固定する。real/safe は RPC 本体がプローブ CNO を参照するので対象がぶれる。
            if (payload is not ("thin" or "none") || dst != "self")
            {
                Utils.SendMessage($"[nest] tgt={tgt} is restricted to 'thin'/'none' with dst=self (keeps the diff against /nest 1 thin minimal, and keeps another player's Data off the wire to third parties).", player.PlayerId);
                return;
            }

            // ⚠️ `via` も固定しないと不変条件が破れる (2026-08-01 監査で発見)。
            // `dst=self` を強制しても `via=t5` にすると BuildEnvelope が dests を無視して
            // **真のブロードキャスト**を組むので、`tgt=other` では他プレイヤーの netId が第三者の配線に乗る。
            // しかもログ上は「対象 netId だけを変えた1変数アーム」に見えるのに乗り物も動いている
            // (`dst=spread` が7アーム分の偽陰性を生んだのと同型の罠)。
            // 自分の物 / 未使用 netId を対象にするアーム (selfdata/selfnt/selfphys/bogus/xprobe) は
            // 第三者に何も晒さないので via を振ってよい — P5 の発見はまさに `tgt=bogus via=t5` だった。
            if (tgt == "other" && via != "t6self")
            {
                Utils.SendMessage("[nest] tgt=other requires via=t6self — any other vehicle broadcasts another player's netId to third parties and silently makes this a 2-variable arm.", player.PlayerId);
                return;
            }

            if (tgt == "other")
            {
                // PlayerId>=200 は CNO なので「実プレイヤー」から除外する (それは既に取れている tgt=cno アーム)。
                otherPc = Main.EnumeratePlayerControls().FirstOrDefault(pc => !pc.AmOwner && pc.OwnerId >= 0 && pc.PlayerId < 200 && pc.Data != null);

                if (otherPc == null)
                {
                    Utils.SendMessage("[nest] tgt=other needs at least one other real client in the lobby.", player.PlayerId);
                    return;
                }
            }

            if (tgt == "xprobe" && !NestXProbe)
            {
                Utils.SendMessage("[nest] tgt=xprobe needs a probe — run '/nest xspawn [owner=..] [pid=..] [noreg]' first.", player.PlayerId);
                return;
            }
        }

        // ⚠️ tgt= はタイポを既定値に落とさない。未知の値を黙って握り潰すと、三項演算子チェーンの
        // 末尾にある `self` (= 既知の 100% キックアーム) が引かれる = 計器としても事故防止としても最悪。
        if (tgt is not ("self" or "cno" or "other" or "selfdata" or "xprobe" or "bogus" or "selfnt" or "selfphys"))
        {
            Utils.SendMessage($"[nest] unknown tgt={tgt}. Valid: self|cno|other|selfdata|xprobe|bogus|selfnt|selfphys", player.PlayerId);
            return;
        }

        if (op is not ("data" or "despawn"))
        {
            Utils.SendMessage($"[nest] unknown op={op}. Valid: data|despawn", player.PlayerId);
            return;
        }

        if (needsProbe)
        {
            probe = WcDbgProbeCno?.playerControl;

            // ⚠️ 「まだ生成コルーチンが走っている (playerControl が真の null)」と「Despawn 済み (fake-null)」を
            // 区別しないと、案内に従って早く撃ち直したときに 2 体目を生やし、1 体目が誰にも Despawn されない
            // 孤児 CNO として残る (定期 SnapTo 再送で計測窓にノイズを足す)。ReferenceEquals で生成中を弾く。
            if (WcDbgProbeCno != null && ReferenceEquals(probe, null))
            {
                Utils.SendMessage("[nest] probe CNO is still spawning — wait a moment and run the same command again.", player.PlayerId);
                return;
            }

            if (probe == null || probe.Data == null)
            {
                // ⚠️ Standard モードのロビーでは CreateNetObject のコルーチンが即 yield break する
                // (CustomNetObject.cs:538) → いつまでも "still spawning" になる。先に弾いて理由を出す。
                if (!GameStates.InGame && Options.CurrentGameMode == CustomGameMode.Standard)
                {
                    Utils.SendMessage("[nest] 'real'/'safe' need a probe CNO, which cannot spawn in a Standard-mode lobby. Start a solo game first, or use 'thin' (needs no CNO).", player.PlayerId);
                    return;
                }

                // 生成はコルーチンで netId が非同期に決まるので、スポーン通信を計測窓に混ぜないためにも
                // 「スポーンして戻る → 撃ち直し」の2段構えにする。
                Vector2 probePos = player.GetTruePosition() + new Vector2(2f, 0f);
                WcDbgProbeCno = new WaveCannonWarning(probePos, "<size=100%><color=#00c8ff>█");
                Utils.SendMessage("[nest] probe CNO spawned — run the same command again (spawn traffic is kept out of the probe window).", player.PlayerId);
                return;
            }
        }

        PlayerControl self = PlayerControl.LocalPlayer;
        int gameId = AmongUsClient.Instance.GameId;
        int selfClient = self.OwnerId;
        // ⚠️ 対象 NetObject は `needsProbe` ではなく `tgt` で明示的に選ぶこと。
        // needsProbe 経由にすると tgt=other が黙って self.NetId (= 既知の 100% キックアーム) に落ち、
        // 「他人の PlayerControl も違法」という偽陽性を掴む (しかもログにも tgt=self としか出ない)。
        PlayerControl tgtPc = tgt == "other" ? otherPc : tgt == "xprobe" ? NestXProbe : needsProbe ? probe : self;
        uint probeNetId = tgtPc.NetId;
        // ⚠️ xprobe は GameData に登録されないので `PlayerControl.Data` の**ゲッター自体が例外を投げる**
        // (2026-08-01 実測: `!= null` では防げない — 参照比較の前に落ちる)。try/catch が要る。
        // probeDataNetId は `safe` ペイロード (SetName) 専用で、explicitTarget 系は thin/none 限定なので 0 で足りる。
        uint probeDataNetId = SafeDataNetId(tgtPc);
        byte probeId = tgtPc.PlayerId;

        // 本体の書き方と対象 netId をアームごとに確定する。
        //   pid   : tag1{ packed netId, byte PlayerId } — 非初期 PlayerControl Data の**バニラと同形**の本体 (既定)
        //   npi   : 自分の NetworkedPlayerInfo を丸ごと (tgt=selfdata。本番 Utils.SendGameData と同クラス・期待は無傷)
        //   nt    : 自分の CustomNetworkTransform をゲーム自身のシリアライザで (tgt=selfnt。バニラが毎 tick 出す種別)
        //   empty : 本体 0 バイト (tgt=selfphys。PlayerPhysics は Serialize が常に false = 合法な本体が存在しない
        //           ⇒ 蹴られても「保護対象」と「本体不正」を区別できない交絡アーム。モデル構築に使わないこと)
        var bodyKind = "pid";
        var tgtNetIdDesc = "PlayerControl";

        switch (tgt)
        {
            case "selfdata":
                bodyKind = "npi";
                probeNetId = self.Data.NetId;
                tgtNetIdDesc = "NetworkedPlayerInfo";
                break;
            case "selfnt":
                bodyKind = "nt";
                probeNetId = self.NetTransform.NetId;
                tgtNetIdDesc = "CustomNetworkTransform";
                break;
            case "selfphys":
                bodyKind = "empty";
                probeNetId = self.MyPhysics.NetId;
                tgtNetIdDesc = "PlayerPhysics(confounded)";
                break;
            case "bogus":
                // 一度も spawn していない netId。サーバの表が「保護集合に載っていたら切る」型か
                // 「既知の安全集合に無ければ切る」型かを分ける (前者なら無傷・後者ならキック)。
                probeNetId = AmongUsClient.Instance.NetIdCnt + 5000U;
                tgtNetIdDesc = "never-spawned";
                break;
        }

        // 本体バイトの明示指定。spawn 本体の PlayerId (`xspawn pid=`) とは別変数なので、
        // これが無いと Arm S (spawn の PlayerId を振る) が本体バイトと一緒に動いて交絡する。
        if (body >= 0) probeId = (byte)body;

        // ⚠️ Despawn の事故防止ゲートは **netId が確定してから** 掛ける。
        // 旧版は `tgt == "self"` だけを見ていたため、`tgt=other op=despawn` が
        // **実在プレイヤーの PlayerControl を無確認で Despawn** できてしまっていた
        // (`selfdata`/`selfnt`/`selfphys` も同様に素通り)。対象 netId で判定すれば全アームを一様に守れる。
        if (op == "despawn" && !force && NestIsLivePlayerNetId(probeNetId))
        {
            Utils.SendMessage($"[nest] op=despawn targets netId={probeNetId}, which belongs to a LIVE player — it destroys that object (and is a confirmed Hacking kick for PlayerControl/NetworkedPlayerInfo). Add 'force' if that is intended.", player.PlayerId);
            return;
        }

        // tgt=selfnt はゲーム自身に本体を書かせる。dirty でないと Serialize が false を返して
        // 0 バイト本体になり、意図せず「空 Data」アームを撃つことになるので先に dirty にする。
        var ntSerialized = true;

        if (bodyKind == "nt")
        {
            // ⚠️ Serialize は dirty を書き出すと同時に消費するので、1 発の SetDirtyBit で作れる
            // 「本物の CNT 本体」は 1 個だけ。total>1 だと 2 個目以降が 0 バイト本体に化けて
            // アームの意味が変わる (ntSerialized 警告は出るが、読み飛ばすと誤解釈する)。構造的に禁じる。
            if (total > 1)
            {
                Utils.SendMessage("[nest] tgt=selfnt is restricted to total=1 (Serialize consumes the dirty bit, so later copies would silently become 0-byte bodies).", player.PlayerId);
                return;
            }

            try
            {
                self.NetTransform.SnapTo(self.transform.position);
                self.NetTransform.SetDirtyBit(uint.MaxValue);
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }
        var real = payload == "real";
        var thin = payload == "thin";
        var empty = payload == "none";
        var packed = via != "bare6";
        var broadcast = via == "t5";
        string padName = pad > 0 ? new string('█', pad) : string.Empty;

        var dests = new List<int>();
        // dests と同じ添字で「その宛先プレイヤーの実 PlayerId」を保持する (spoof 用)。dst=real のときだけ埋まる。
        var destPlayerIds = new List<byte>();

        switch (dst)
        {
            case "real":
                foreach (PlayerControl pc in Main.EnumeratePlayerControls())
                    if (!pc.AmOwner && pc.OwnerId >= 0)
                    {
                        dests.Add(pc.OwnerId);
                        destPlayerIds.Add(pc.PlayerId);
                    }

                if (dests.Count == 0)
                {
                    Utils.SendMessage("[nest] dst=real needs at least one other client in the lobby. Use dst=self or dst=spread.", player.PlayerId);
                    return;
                }

                break;
            case "spread":
                // ⚠️ 実在クライアントの id と衝突させない。AU の client id は小さい整数が近接して割り当てられる
                // ため `selfClient + 1 + i` だと人が居るロビーで本物に当たりうる = 「存在しない宛先を試す」
                // という前提そのものが壊れ、陰性コントロールの解釈も交絡する。十分遠くへ飛ばした上で
                // 現に接続中の id を明示的に除外する。
                var live = new HashSet<int> { selfClient };
                foreach (PlayerControl pc in Main.EnumeratePlayerControls())
                    if (pc.OwnerId >= 0)
                        live.Add(pc.OwnerId);

                for (var i = 0; dests.Count < total && i < total * 4; i++)
                {
                    int candidate = selfClient + 100000 + i;
                    if (!live.Contains(candidate)) dests.Add(candidate);
                }

                break;
            default:
                dests.Add(selfClient);
                break;
        }

        int distinct = Math.Min(dests.Count, total);

        // real/safe は実 fan-out と同じ [Data / RPC / Data] の3枚構成で、中央の RPC だけを
        // real=MurderPlayer(FailedError, 実物と同一) / safe=SetName(無害) で入れ替える。
        // thin は Data 1枚だけ = 「子(tag6)の個数」と「葉メッセージの個数」を分離するためのアーム
        // (例: 12 thin と 4 safe はどちらも 12 メッセージだが、子の数は 12 と 4 で違う)。
        // Data の PlayerId は回転させず自分の値を書くので、自分宛にエコーバックしても局所的に無害。
        // 対象 NetObject への 1 メッセージを書く。op=data なら tag1(Data)、op=despawn なら tag5(Despawn)。
        void WriteTargetMessage(MessageWriter w, byte bodyByte)
        {
            if (op == "despawn")
            {
                w.StartMessage(5);
                w.WritePacked(probeNetId);
                w.EndMessage();
                return;
            }

            w.StartMessage(1);

            switch (bodyKind)
            {
                case "npi":
                    w.WritePacked(probeNetId);
                    // 会議中 write-barrier を意図的送信として通過する囲い (本番経路と同じ作法)
                    NetworkedPlayerInfoSerializePatch.IntentionalSends++;
                    try { self.Data.Serialize(w, false); }
                    finally { NetworkedPlayerInfoSerializePatch.IntentionalSends--; }

                    break;
                case "nt":
                    w.WritePacked(probeNetId);
                    // ⚠️ false なら 1 バイトも書いていない = 意図せず「0 バイト本体」アームに化ける。必ず判定する。
                    if (!self.NetTransform.Serialize(w, false)) ntSerialized = false;

                    break;
                case "empty":
                    w.WritePacked(probeNetId);
                    break;
                default:
                    w.WritePacked(probeNetId);
                    w.Write(bodyByte);
                    break;
            }

            w.EndMessage();
        }

        MessageWriter BuildEnvelope(int startIndex, int childCount)
        {
            MessageWriter s = MessageWriter.Get(SendOption.Reliable);

            if (packed)
            {
                s.StartMessage(26);
                s.WritePacked(gameId);
            }

            for (var c = 0; c < childCount; c++)
            {
                int di = (startIndex + c) % dests.Count;
                // spoof 時は本番同様「この子の宛先プレイヤーの PlayerId」を先頭 Data に書く (末尾 Data で復元)
                byte firstDataId = spoof && destPlayerIds.Count > 0 ? destPlayerIds[di % destPlayerIds.Count] : probeId;

                if (broadcast)
                {
                    s.StartMessage(5);
                    s.Write(gameId);
                }
                else
                {
                    s.StartMessage(6);
                    s.Write(gameId);
                    s.WritePacked(dests[di]);
                }

                if (empty)
                {
                    s.EndMessage();
                    continue;
                }

                WriteTargetMessage(s, firstDataId);

                if (thin)
                {
                    s.EndMessage();
                    continue;
                }

                s.StartMessage(2);
                s.WritePacked(probeNetId);

                if (real)
                {
                    s.Write((byte)RpcCalls.MurderPlayer);
                    s.WriteNetObject(probe);
                    s.Write((int)MurderResultFlags.FailedError);
                }
                else
                {
                    s.Write((byte)RpcCalls.SetName);
                    s.Write(probeDataNetId);
                    s.Write(padName);
                    s.Write(false);
                }

                s.EndMessage();

                WriteTargetMessage(s, probeId);

                s.EndMessage();
            }

            if (packed) s.EndMessage();
            return s;
        }

        MessageWriter first = BuildEnvelope(0, Math.Min(per, total));

        if (first.Length > SizeCap && !force)
        {
            int rejected = first.Length;
            first.Recycle();
            Utils.SendMessage($"[nest] aborted before sending: envelope would be {rejected}B (cap {SizeCap}B). Lower per=/pad=, or add 'force'.", player.PlayerId);
            return;
        }

        var envelopes = 0;
        var sentChildren = 0;
        var maxLen = 0;
        // ⚠️ SendOrDisconnect は TryGate がキューに積んだだけでも戻る = 「送った」≠「ワイヤに出た」。
        // キック時刻との時間相関を取るために、ゲート待ち本数を前後で測って結果に併記する。
        int pendingBefore = PacketRateGate.PendingCount;

        // ⚠️ StartWindowBypass は「ゲーム開始の復元シーケンス専用」の単一グローバル bool で、
        // 既存の消費者 (OnGameStartedPatch / MeetingStartWire) は無条件 false で閉じている。
        // それはフェーズ上お互い排他だから成立している規約で、任意タイミングで撃てる dev コマンドは
        // その前提を満たさない — 呼び出し前の値を保存/復元して、他窓を早期クローズしないようにする。
        // (DataFlagRateLimiter 側は触らない: 本コマンドは SendOrDisconnect 直呼びで Enqueue を通らないため無効)
        bool prevBypass = PacketRateGate.StartWindowBypass;

        try
        {
            if (raw) PacketRateGate.StartWindowBypass = true;

            for (var i = 0; i < total; i += per)
            {
                int childCount = Math.Min(per, total - i);
                MessageWriter s = i == 0 ? first : BuildEnvelope(i, childCount);
                maxLen = Math.Max(maxLen, s.Length);
                HealthLog.RecordHostAction("NestProbe", s.Length, "Reliable");
                AmongUsClient.Instance.SendOrDisconnect(s);
                s.Recycle();
                envelopes++;
                sentChildren += childCount;
            }
        }
        finally
        {
            if (raw) PacketRateGate.StartWindowBypass = prevBypass;
        }

        int pendingAfter = PacketRateGate.PendingCount;

        // 実験を無効化しうる条件は結果に明示する (陰性を「証拠」と誤読しないため)
        var warn = string.Empty;
        if (raw && pendingBefore > 0) warn += $" ⚠ gate queue was not empty ({pendingBefore}) — 'raw' only bypasses while the queue is empty.";
        if (maxLen > 1000) warn += " ⚠ envelope exceeded 1000B — PacketSplitPatch re-split it into ≤800B chunks, so this is NOT a single large packet.";
        if (pendingAfter > 0) warn += $" ⚠ {pendingAfter} packet(s) still queued — not on the wire yet.";

        // 梯子の基準は「子の個数」ではなく「葉メッセージの個数」— 実 fan-out も messages += 3 で数え、
        // GetMaxMessagePackingLimit() と比較している (CustomNetObject.cs:626,663)。両方出す。
        int msgsPerChild = empty ? 0 : thin ? 1 : 3;
        int msgsPerEnvelope = Math.Min(per, total) * msgsPerChild;

        // 事後解析は恒久チャネル (Health + Timeline) に残す — log.html は約10分でローテートするため
        // tgt は生の指定値 + 実際に書いた netId を出す (どのアームを撃ったかを事後に取り違えないため)。
        if (bodyKind == "nt" && !ntSerialized) warn += " ⚠ NetTransform.Serialize returned false — the body was 0 bytes, so this arm is NOT the CNT arm.";

        string tgtLabel = tgt == "other" ? $"other:{probeId}" : explicitTarget ? tgt : needsProbe ? "cno" : "self";
        string tgtNetIdLabel = $"{probeNetId}({tgtNetIdDesc})";
        string xspec = tgt == "xprobe" ? $" xspec=[{NestXProbeSpec}]" : string.Empty;
        string line = $"NEST probe total={total} per={per} msgsPerEnv={msgsPerEnvelope} envelopes={envelopes} payload={payload} op={op} via={via} tgt={tgtLabel} tgtNetId={tgtNetIdLabel} body={(body >= 0 ? body.ToString() : $"auto:{probeId}")} bodyKind={bodyKind}{xspec} dst={dst} spoof={spoof} distinct={distinct} pad={pad} raw={raw} maxLen={maxLen} queued={pendingBefore}->{pendingAfter} packing={packingLimit} players={playerCount} phase={(GameStates.IsLobby ? "lobby" : "ingame")} server={GameStates.CurrentServerType}{warn}";
        HealthLog.NoteAnom(line);
        Logger.Info(line, "DevCmd");
        Utils.SendMessage($"[nest] submitted {sentChildren} children ({msgsPerEnvelope} msgs/envelope) in {envelopes} envelope(s), maxLen={maxLen}B, payload={payload}/{op}/{via}, tgt={tgtLabel}→net{probeNetId}({tgtNetIdDesc}){xspec}{(pad > 0 ? $", pad={pad}" : string.Empty)}{(raw ? ", raw" : string.Empty)}, queued={pendingBefore}->{pendingAfter}. packing limit={packingLimit}{warn}", player.PlayerId);
    }

    private static void KCountCommand(PlayerControl player, string text, string[] args)
    {
        if (GameStates.IsLobby || !Options.EnableKillerLeftCommand.GetBool() || Main.AllAlivePlayerControlsToList.Count < Options.MinPlayersForGameStateCommand.GetInt())
        {
            Utils.SendMessage(GetString("Message.CommandUnavailable"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        Utils.SendMessage("\n", player.PlayerId, Utils.GetGameStateData(), importance: MessageImportance.High);
    }

    private static void SetRoleCommand(PlayerControl player, string text, string[] args)
    {
        string subArgs = string.Join(' ', args[1..]);

        if (!GuessManager.MsgToPlayerAndRole(subArgs, out byte resultId, out CustomRoles roleToSet, out _))
        {
            Utils.SendMessage(GetString("InvalidArguments"), player.PlayerId);
            return;
        }

        PlayerControl targetPc = Utils.GetPlayerById(resultId);
        if (targetPc == null) return;

        if (roleToSet.IsAdditionRole())
        {
            if (!Main.SetAddOns.ContainsKey(resultId)) Main.SetAddOns[resultId] = [];

            if (Main.SetAddOns[resultId].Contains(roleToSet))
                Main.SetAddOns[resultId].Remove(roleToSet);
            else
                Main.SetAddOns[resultId].Add(roleToSet);
        }
        else
            Main.SetRoles[targetPc.PlayerId] = roleToSet;

        Utils.SendMessage("\n", player.PlayerId, string.Format(GetString("RoleSelected"), resultId.ColoredPlayerName(), roleToSet.ToColoredString()));

        if (roleToSet.OnlySpawnsWithPets() && !Options.UsePets.GetBool())
            Prompt.Show(GetString("Promt.SetRoleRequiresPets"), () => Options.UsePets.SetValue(1), () => { });
    }

    private static void UpCommand(PlayerControl player, string text, string[] args)
    {
        Utils.SendMessage($"{GetString("UpReplacedMessage")}", player.PlayerId);
    }

    private static void RCommand(PlayerControl player, string text, string[] args)
    {
        // 先頭 2 文字決め打ち (text.Remove(0, 2)) だと 1 文字以外の別名を足した瞬間に壊れるので args 基準で読む
        SendRoleInfo(player, args.Length < 2 ? string.Empty : string.Join(' ', args[1..]));
    }

    private static void SendRoleInfo(PlayerControl player, string subArgs)
    {
        byte to = player.PlayerId;

        // トグルの消費はホスト本人の入力に限る (他人の /r ・ /n r ・ /h r がホストの Broadcast 予約を無音で食い潰さないように)
        if (player.AmOwner)
        {
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || ClientControlGUI.BroadcastRoleInfo) to = byte.MaxValue;
            ClientControlGUI.BroadcastRoleInfo = false;
        }

        SendRolesInfo(subArgs, to);
    }

    // TOHK 出身プレイヤーの指クセ救済: /n r <役職名> ・ /h r <役職名> も /r <役職名> と同じ役職検索として扱う。
    // 役職名が付いていない素の /n r ・ /h r は従来通りの動作を維持する — SendRolesInfo は非 Standard モードで
    // モード説明を返して早期 return するため、無条件に流すと有効役職一覧が出なくなる。
    private static bool TryRoleSearchSubCommand(PlayerControl player, string[] args)
    {
        string[] parts = args.Where(x => x.Length > 0).ToArray(); // 「/n  r ○○」のような連続スペースで空要素が挟まっても拾えるように
        if (parts.Length < 3 || parts[1].ToLower() is not ("r" or "roles")) return false;

        string role = string.Join(' ', parts[2..]).Trim();
        if (role.Length == 0) return false;

        SendRoleInfo(player, role);
        return true;
    }

    private static void DisconnectCommand(PlayerControl player, string text, string[] args)
    {
        string subArgs = args.Length < 2 ? string.Empty : args[1].ToLower();

        switch (subArgs)
        {
            case "crew":
            case "crewmate": // TOHK 形
                GameManager.Instance.enabled = false;
                GameManager.Instance.ShouldCheckForGameEnd = false;
                MessageWriter msg = AmongUsClient.Instance.StartEndGame();
                msg.Write((byte)6);
                msg.Write(false);
                AmongUsClient.Instance.FinishEndGame(msg);
                break;

            case "imp":
            case "impostor": // TOHK 形
                GameManager.Instance.enabled = false;
                GameManager.Instance.ShouldCheckForGameEnd = false;
                MessageWriter msg2 = AmongUsClient.Instance.StartEndGame();
                msg2.Write((byte)5);
                msg2.Write(false);
                AmongUsClient.Instance.FinishEndGame(msg2);
                break;

            default:
                if (!HudManager.InstanceExists) break;
                HudManager.Instance.Chat.AddChat(player, "crew(crewmate) | imp(impostor)");
                break;
        }

        ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Admin, 0);
    }

    private static void NowCommand(PlayerControl player, string text, string[] args)
    {
        if (TryRoleSearchSubCommand(player, args)) return;

        string subArgs = args.Length < 2 ? string.Empty : args[1].ToLower();

        switch (subArgs)
        {
            case "r":
            case "roles":
                Utils.ShowActiveRoles(player.PlayerId);
                break;
            case "a":
            case "all":
                Utils.ShowAllActiveSettings(player.PlayerId);
                break;
            default:
                Utils.ShowActiveSettings(player.PlayerId);
                break;
        }
    }

    private static void LevelCommand(PlayerControl player, string text, string[] args)
    {
        string subArgs = args.Length < 2 ? string.Empty : args[1];
        Utils.SendMessage(string.Format(GetString("Message.SetLevel"), subArgs), player.PlayerId);
        _ = int.TryParse(subArgs, out int input);

        if (input is < 1 or > 999)
        {
            Utils.SendMessage(GetString("Message.AllowLevelRange"), player.PlayerId);
            return;
        }

        var number = Convert.ToUInt32(input);
        player.RpcSetLevel(number - 1);
    }

    private static void HideNameCommand(PlayerControl player, string text, string[] args)
    {
        Main.HideName.Value = args.Length > 1 ? string.Join(' ', args[1..]) : Main.HideName.DefaultValue.ToString();

        GameStartManagerPatch.GameStartManagerStartPatch.HideName.text =
            ColorUtility.TryParseHtmlString(Main.HideColor.Value, out _)
                ? $"<color={Main.HideColor.Value}>{Main.HideName.Value}</color>"
                : $"<color={Main.ModColor}>{Main.HideName.Value}</color>";
    }

    private static void RenameCommand(PlayerControl player, string text, string[] args)
    {
        if (args.Length < 2) return;

        string name = Regex.Replace(string.Join(' ', args[1..]), "<size=[^>]*>", string.Empty).Trim();

        if (name.RemoveHtmlTags().Length is > 15 or < 1)
            Utils.SendMessage(GetString("Message.AllowNameLength"), player.PlayerId, importance: MessageImportance.Low);
        else
        {
            if (player.AmOwner)
                Main.NickName = name;
            else
            {
                if (BanManager.CheckDenyNamePlayer(player, name)) return;

                if (!Options.PlayerCanSetName.GetBool() && !IsPlayerVIP(player.FriendCode) && !player.FriendCode.GetDevUser().up && !player.FriendCode.IsLocalDev())
                {
                    Utils.SendMessage(GetString("Message.OnlyVIPCanUse"), player.PlayerId, importance: MessageImportance.Low);
                    return;
                }

                if (GameStates.IsInGame)
                {
                    Utils.SendMessage(GetString("Message.OnlyCanUseInLobby"), player.PlayerId, importance: MessageImportance.Low);
                    return;
                }

                Main.AllPlayerNames[player.PlayerId] = name;
                player.RpcSetName(name);
            }
        }
    }

    private static void LastResultCommand(PlayerControl player, string text, string[] args)
    {
        Utils.ShowKillLog(player.PlayerId);
        Utils.ShowLastAddOns(player.PlayerId);
        Utils.ShowLastRoles(player.PlayerId);
        Utils.ShowLastResult(player.PlayerId);
    }

    private static void WinnerCommand(PlayerControl player, string text, string[] args)
    {
        if (Main.WinnerNameList.Count == 0)
            Utils.SendMessage(GetString("NoInfoExists"), importance: MessageImportance.Low);
        else
            Utils.SendMessage("<b><u>Winners:</b></u>\n" + string.Join(", ", Main.WinnerNameList));
    }

    private static void ChangeSettingCommand(PlayerControl player, string text, string[] args)
    {
        string subArgs = args.Length < 2 ? "" : args[1];

        switch (subArgs)
        {
            case "map":
                subArgs = args.Length < 3 ? "" : args[2];

                switch (subArgs)
                {
                    case "skeld":
                    case "theskeld":
                        GameOptionsManager.Instance.CurrentGameOptions.SetByte(ByteOptionNames.MapId, 0);
                        break;
                    case "mira":
                    case "mirahq":
                        GameOptionsManager.Instance.CurrentGameOptions.SetByte(ByteOptionNames.MapId, 1);
                        break;
                    case "polus":
                        GameOptionsManager.Instance.CurrentGameOptions.SetByte(ByteOptionNames.MapId, 2);
                        break;
                    case "dleks":
                    case "dlekseht":
                        GameOptionsManager.Instance.CurrentGameOptions.SetByte(ByteOptionNames.MapId, 3);
                        break;
                    case "airship":
                        GameOptionsManager.Instance.CurrentGameOptions.SetByte(ByteOptionNames.MapId, 4);
                        break;
                    case "fungle":
                    case "thefungle":
                        GameOptionsManager.Instance.CurrentGameOptions.SetByte(ByteOptionNames.MapId, 5);
                        break;
                    case "submerged" when SubmergedCompatibility.Loaded:
                        GameOptionsManager.Instance.CurrentGameOptions.SetByte(ByteOptionNames.MapId, 6);
                        break;
                    case "custom":
                        subArgs = args.Length < 4 ? "" : args[3];
                        GameOptionsManager.Instance.CurrentGameOptions.SetByte(ByteOptionNames.MapId, byte.Parse(subArgs));
                        break;
                }

                break;
            case "impostors":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.currentNormalGameOptions.SetInt(Int32OptionNames.NumImpostors, int.Parse(subArgs));
                AmongUsClient.Instance.StartGame();
                break;
            case "players":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetInt(Int32OptionNames.MaxPlayers, int.Parse(subArgs));
                AmongUsClient.Instance.StartGame();
                break;
            case "recommended":
                subArgs = args.Length < 3 ? "" : args[2];

                switch (subArgs)
                {
                    case "on":
                        GameOptionsManager.Instance.CurrentGameOptions.SetBool(BoolOptionNames.IsDefaults, true);
                        break;
                    case "off":
                        GameOptionsManager.Instance.CurrentGameOptions.SetBool(BoolOptionNames.IsDefaults, false);
                        break;
                }

                break;
            case "confirmejects":
                subArgs = args.Length < 3 ? "" : args[2];

                switch (subArgs)
                {
                    case "on":
                        GameOptionsManager.Instance.currentNormalGameOptions.SetBool(BoolOptionNames.ConfirmImpostor, true);
                        break;
                    case "off":
                        GameOptionsManager.Instance.currentNormalGameOptions.SetBool(BoolOptionNames.ConfirmImpostor, false);
                        break;
                }

                break;
            case "emergencymeetings":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.currentNormalGameOptions.SetInt(Int32OptionNames.NumEmergencyMeetings, int.Parse(subArgs));
                break;
            case "anonymousvotes":
                subArgs = args.Length < 3 ? "" : args[2];

                switch (subArgs)
                {
                    case "on":
                        GameOptionsManager.Instance.currentNormalGameOptions.SetBool(BoolOptionNames.AnonymousVotes, true);
                        break;
                    case "off":
                        GameOptionsManager.Instance.currentNormalGameOptions.SetBool(BoolOptionNames.AnonymousVotes, false);
                        break;
                }

                break;
            case "emergencycooldown":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.currentNormalGameOptions.SetInt(Int32OptionNames.EmergencyCooldown, int.Parse(subArgs));
                break;
            case "discussiontime":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.currentNormalGameOptions.SetInt(Int32OptionNames.DiscussionTime, int.Parse(subArgs));
                break;
            case "votingtime":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.currentNormalGameOptions.SetInt(Int32OptionNames.VotingTime, int.Parse(subArgs));
                break;
            case "playerspeed":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.currentNormalGameOptions.SetFloat(FloatOptionNames.PlayerSpeedMod, float.Parse(subArgs));
                break;
            case "crewmatevision":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.currentNormalGameOptions.SetFloat(FloatOptionNames.CrewLightMod, float.Parse(subArgs));
                break;
            case "impostorvision":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.currentNormalGameOptions.SetFloat(FloatOptionNames.ImpostorLightMod, float.Parse(subArgs));
                break;
            case "killcooldown":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.currentNormalGameOptions.SetFloat(FloatOptionNames.KillCooldown, float.Parse(subArgs));
                break;
            case "killdistance":
                subArgs = args.Length < 3 ? "" : args[2];

                switch (subArgs)
                {
                    case "short":
                        GameOptionsManager.Instance.currentNormalGameOptions.SetInt(Int32OptionNames.KillDistance, 0);
                        break;
                    case "medium":
                        GameOptionsManager.Instance.currentNormalGameOptions.SetInt(Int32OptionNames.KillDistance, 1);
                        break;
                    case "long":
                        GameOptionsManager.Instance.currentNormalGameOptions.SetInt(Int32OptionNames.KillDistance, 2);
                        break;
                    case "custom":
                        subArgs = args.Length < 4 ? "" : args[3];
                        GameOptionsManager.Instance.currentNormalGameOptions.SetInt(Int32OptionNames.KillDistance, int.Parse(subArgs));
                        break;
                }

                break;
            case "taskbarupdates":
                subArgs = args.Length < 3 ? "" : args[2];

                GameOptionsManager.Instance.currentNormalGameOptions.TaskBarMode = subArgs switch
                {
                    "always" => AmongUs.GameOptions.TaskBarMode.Normal,
                    "meetings" => AmongUs.GameOptions.TaskBarMode.MeetingOnly,
                    "never" => AmongUs.GameOptions.TaskBarMode.Invisible,
                    _ => GameOptionsManager.Instance.currentNormalGameOptions.TaskBarMode
                };

                break;
            case "visualtasks":
                subArgs = args.Length < 3 ? "" : args[2];

                switch (subArgs)
                {
                    case "on":
                        GameOptionsManager.Instance.currentNormalGameOptions.SetBool(BoolOptionNames.VisualTasks, true);
                        break;
                    case "off":
                        GameOptionsManager.Instance.currentNormalGameOptions.SetBool(BoolOptionNames.VisualTasks, false);
                        break;
                }

                break;
            case "commontasks":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetInt(Int32OptionNames.NumCommonTasks, int.Parse(subArgs));
                break;
            case "longtasks":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetInt(Int32OptionNames.NumLongTasks, int.Parse(subArgs));
                break;
            case "shorttasks":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetInt(Int32OptionNames.NumShortTasks, int.Parse(subArgs));
                break;
            case "scientistcount":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Scientist, int.Parse(subArgs), GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetChancePerGame(RoleTypes.Scientist));
                break;
            case "scientistchance":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Scientist, GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetNumPerGame(RoleTypes.Scientist), int.Parse(subArgs));
                break;
            case "vitalsdisplaycooldown":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.ScientistCooldown, float.Parse(subArgs));
                break;
            case "batteryduration":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.ScientistBatteryCharge, float.Parse(subArgs));
                break;
            case "engineercount":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.currentNormalGameOptions.RoleOptions.SetRoleRate(RoleTypes.Engineer, int.Parse(subArgs), GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetChancePerGame(RoleTypes.Engineer));
                break;
            case "engineerchance":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Engineer, GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetNumPerGame(RoleTypes.Engineer), int.Parse(subArgs));
                break;
            case "ventusecooldown":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.EngineerCooldown, float.Parse(subArgs));
                break;
            case "maxtimeinvents":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.EngineerInVentMaxTime, float.Parse(subArgs));
                break;
            case "guardianangelcount":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.GuardianAngel, int.Parse(subArgs), GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetChancePerGame(RoleTypes.GuardianAngel));
                break;
            case "guardianangelchance":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.GuardianAngel, GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetNumPerGame(RoleTypes.GuardianAngel), int.Parse(subArgs));
                break;
            case "protectcooldown":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.GuardianAngelCooldown, float.Parse(subArgs));
                break;
            case "protectduration":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.ProtectionDurationSeconds, float.Parse(subArgs));
                break;
            case "protectvisibletoimpostors":
                subArgs = args.Length < 3 ? "" : args[2];

                switch (subArgs)
                {
                    case "on":
                        GameOptionsManager.Instance.CurrentGameOptions.SetBool(BoolOptionNames.ImpostorsCanSeeProtect, true);
                        break;
                    case "off":
                        GameOptionsManager.Instance.CurrentGameOptions.SetBool(BoolOptionNames.ImpostorsCanSeeProtect, false);
                        break;
                }

                break;
            case "shapeshiftercount":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Shapeshifter, int.Parse(subArgs), GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetChancePerGame(RoleTypes.Shapeshifter));
                break;
            case "shapeshifterchance":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Shapeshifter, GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetNumPerGame(RoleTypes.Shapeshifter), int.Parse(subArgs));
                break;
            case "shapeshiftduration":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.ShapeshifterDuration, float.Parse(subArgs));
                break;
            case "shapeshiftcooldown":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.ShapeshifterCooldown, float.Parse(subArgs));
                break;
            case "leaveshapeshiftevidence":
                subArgs = args.Length < 3 ? "" : args[2];

                switch (subArgs)
                {
                    case "on":
                        GameOptionsManager.Instance.CurrentGameOptions.SetBool(BoolOptionNames.ShapeshifterLeaveSkin, true);
                        break;
                    case "off":
                        GameOptionsManager.Instance.CurrentGameOptions.SetBool(BoolOptionNames.ShapeshifterLeaveSkin, false);
                        break;
                }

                break;
            case "phantomcount":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Phantom, int.Parse(subArgs), GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetChancePerGame(RoleTypes.Phantom));
                break;
            case "phantomchance":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Phantom, GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetNumPerGame(RoleTypes.Phantom), int.Parse(subArgs));
                break;
            case "invisduration":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.PhantomDuration, float.Parse(subArgs));
                break;
            case "inviscooldown":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.PhantomCooldown, float.Parse(subArgs));
                break;
            case "noisemakercount":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Noisemaker, int.Parse(subArgs), GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetChancePerGame(RoleTypes.Noisemaker));
                break;
            case "noisemakerchance":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Noisemaker, GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetNumPerGame(RoleTypes.Noisemaker), int.Parse(subArgs));
                break;
            case "noisemakerimpostoralert":
                subArgs = args.Length < 3 ? "" : args[2];

                switch (subArgs)
                {
                    case "on":
                        GameOptionsManager.Instance.CurrentGameOptions.SetBool(BoolOptionNames.NoisemakerImpostorAlert, true);
                        break;
                    case "off":
                        GameOptionsManager.Instance.CurrentGameOptions.SetBool(BoolOptionNames.NoisemakerImpostorAlert, false);
                        break;
                }

                break;
            case "noisemakeralertduration":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.NoisemakerAlertDuration, int.Parse(subArgs));
                break;
            case "trackercount":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Tracker, int.Parse(subArgs), GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetChancePerGame(RoleTypes.Tracker));
                break;
            case "trackerchance":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Tracker, GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetNumPerGame(RoleTypes.Tracker), int.Parse(subArgs));
                break;
            case "trackduration":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.TrackerDuration, float.Parse(subArgs));
                break;
            case "trackcooldown":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.TrackerCooldown, float.Parse(subArgs));
                break;
            case "trackdelay":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.TrackerDelay, float.Parse(subArgs));
                break;
            case "vipercount":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Viper, int.Parse(subArgs), GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetChancePerGame(RoleTypes.Viper));
                break;
            case "viperchance":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Viper, GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetNumPerGame(RoleTypes.Viper), int.Parse(subArgs));
                break;
            case "viperdissolvetime":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.ViperDissolveTime, float.Parse(subArgs));
                break;
            case "detectivecount":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Detective, int.Parse(subArgs), GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetChancePerGame(RoleTypes.Detective));
                break;
            case "detectivechance":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.SetRoleRate(RoleTypes.Detective, GameOptionsManager.Instance.CurrentGameOptions.RoleOptions.GetNumPerGame(RoleTypes.Detective), int.Parse(subArgs));
                break;
            case "detectivesuspectlimit":
                subArgs = args.Length < 3 ? "" : args[2];
                GameOptionsManager.Instance.CurrentGameOptions.SetFloat(FloatOptionNames.DetectiveSuspectLimit, float.Parse(subArgs));
                break;
            default:
                Utils.SendMessage(GetString("Commands.ChangeSettingHelp"), player.PlayerId);
                break;
        }

        GameOptionsManager.Instance.GameHostOptions = GameOptionsManager.Instance.CurrentGameOptions;
        GameManager.Instance.LogicOptions.SyncOptions();
    }

    private static void VersionCommand(PlayerControl player, string text, string[] args)
    {
        string versionText = Main.PlayerVersion.OrderBy(pair => pair.Key).Aggregate(string.Empty, (current, kvp) => current + $"{kvp.Key}: ({Main.AllPlayerNames[kvp.Key]}) {kvp.Value.forkId}/{kvp.Value.version}({kvp.Value.tag})\n");
        if (versionText != string.Empty && HudManager.InstanceExists) HudManager.Instance.Chat.AddChat(player, (player.FriendCode.GetDevUser().HasTag() ? "\n" : string.Empty) + versionText);
    }

    private static void LTCommand(PlayerControl player, string text, string[] args)
    {
        if (!GameStates.IsLobby) return;

        float timer = GameStartManagerPatch.Timer;
        int minutes = (int)timer / 60;
        int seconds = (int)timer % 60;
        string lt = string.Format(GetString("LobbyCloseTimer"), $"{minutes:00}:{seconds:00}");
        if (timer <= 60) lt = Utils.ColorString(Color.red, lt);

        Utils.SendMessage(lt, player.PlayerId);
    }

    // -------------------------------------------------------------------------------------------------------------------------

    private static bool CheckMute(byte id)
    {
        if (!MutedPlayers.TryGetValue(id, out (long MuteTimeStamp, int Duration, long LastMessageTimeStamp) mute)) return false;

        long now = Utils.TimeStamp;
        long timeLeft = mute.Duration - (now - mute.MuteTimeStamp);

        if (timeLeft <= 0)
        {
            MutedPlayers.Remove(id);
            return false;
        }

        if (now - mute.LastMessageTimeStamp < 5) return true;
        mute.LastMessageTimeStamp = now;
        MutedPlayers[id] = mute;
        Utils.SendMessage("\n", id, string.Format(GetString("MuteMessage"), timeLeft));
        return true;
    }

    public static bool GetRoleByName(string name, out CustomRoles role)
    {
        role = new();
        if (name == "") return false;

        if ((TranslationController.InstanceExists ? TranslationController.Instance.currentLanguage.languageID : SupportedLangs.SChinese) == SupportedLangs.SChinese)
        {
            Regex r = new("[\u4e00-\u9fa5]+$");
            MatchCollection mc = r.Matches(name);
            var result = string.Empty;

            for (var i = 0; i < mc.Count; i++)
            {
                if (mc[i].ToString() == "是") continue;

                result += mc[i]; //匹配结果是完整的数字，此处可以不做拼接的
            }

            name = result.Replace("是", string.Empty).Trim().Replace("着", "者");
        }
        else
            name = name.Trim().ToLower();
        
        string nameWithoutId = Regex.Replace(name.Replace(" ", string.Empty), @"^\d+", string.Empty);

        foreach (CustomRoles rl in Main.CustomRoleValues)
        {
            if (rl.IsVanilla()) continue;
            
            string roleName = Regex.Replace(GetString(rl.ToString()).RemoveHtmlTags().ToLower(), @"[^\p{L}-]+", string.Empty);

            if (nameWithoutId == roleName)
            {
                role = rl;
                return true;
            }
        }

        return false;
    }

    private static void SendRolesInfo(string role, byte playerId, bool isDev = false, bool isUp = false)
    {
        if (Options.CurrentGameMode != CustomGameMode.Standard)
        {
            string text = $"{GetString($"ModeDescribe.{Options.CurrentGameMode}")}\n\n<size=70%>{GetString("NToCheckGameModeSettings")}</size>";
            Utils.SendMessage(text, playerId, importance: MessageImportance.Low);
            if (Options.CurrentGameMode != CustomGameMode.HideAndSeek) return;
        }

        role = role.Trim().ToLower();

        if (role == "")
        {
            Utils.ShowActiveRoles(playerId);
            return;
        }

        string originalInput = role;
        role = role.Replace("着", "者").ToLower().Trim().Replace(" ", string.Empty);

        foreach (CustomRoles rl in Main.CustomRoleValues)
        {
            if (rl.IsVanilla()) continue;

            string roleName = Regex.Replace(GetString(rl.ToString()).RemoveHtmlTags().ToLower().Trim().TrimStart('*'), @"[^\p{L}-]+", string.Empty);

            if (role == roleName || (originalInput is "schrodingers cat" or "schrodingerscat" or "cat" && rl == CustomRoles.SchrodingersCat))
            {
                if ((isDev || isUp) && GameStates.IsLobby)
                {
                    var devMark = "▲";
                    if (rl.IsAdditionRole() || rl is CustomRoles.GM) devMark = string.Empty;
                    if (rl.GetCount() < 1 || rl.GetMode() == 0) devMark = string.Empty;
                    if (isUp) Utils.SendMessage(devMark == "▲" ? string.Format(GetString("Message.YTPlanSelected"), roleName) : string.Format(GetString("Message.YTPlanSelectFailed"), roleName), playerId, importance: MessageImportance.Low);
                    if (isUp) return;
                }

                string coloredString = rl.ToColoredString();
                StringBuilder sb = new();
                StringBuilder settings = new();
                var title = $"{coloredString} {Utils.GetRoleMode(rl)}";
                sb.Append(GetString($"{rl}InfoLong").FixRoleName(rl).TrimStart());
                
                if (Options.CustomRoleSpawnChances.TryGetValue(rl, out StringOptionItem chance)) AddSettings(chance);
                if (rl is CustomRoles.LovingCrewmate or CustomRoles.LovingImpostor && Options.CustomRoleSpawnChances.TryGetValue(CustomRoles.Lovers, out chance)) AddSettings(chance);

                string txt = sb.ToString().Replace(roleName, coloredString, StringComparison.OrdinalIgnoreCase);
                sb.Clear().Append(txt);

                if (rl.PetActivatedAbility()) sb.Append($"<size=1>{GetString("SupportsPetMessage")}</size>");

                if (settings.Length > 0) Utils.SendMessage("\n", playerId, settings.ToString());
                if (rl.UsesPetInsteadOfKill()) Utils.SendMessage("\n", playerId, GetString("UsesPetInsteadOfKillNotice"));
                if (rl.UsesMeetingShapeshift()) Utils.SendMessage("\n", playerId, GetString("UsesMeetingShapeshiftNotice"));

                Utils.SendMessage(sb.ToString(), playerId, title, importance: MessageImportance.High);
                return;

                void AddSettings(StringOptionItem stringOptionItem)
                {
                    settings.AppendLine($"<size=70%><u>{GetString("SettingsForRoleText")} {rl.ToColoredString()}:</u>");
                    Utils.ShowChildrenSettings(stringOptionItem, settings, disableColor: false);
                    settings.Append("</size>");
                }
            }
        }

        foreach (CustomGameMode gameMode in Main.CustomGameModeValues)
        {
            string gmString = GetString(gameMode.ToString());
            string match = gmString.ToLower().Trim().TrimStart('*').Replace(" ", string.Empty);

            if (role.Equals(match, StringComparison.OrdinalIgnoreCase))
            {
                string text = $"{GetString($"ModeDescribe.{gameMode}")}\n\n<size=70%>{GetString("NToCheckGameModeSettings")}</size>";
                Utils.SendMessage(text, playerId, gmString, importance: MessageImportance.Low);
                return;
            }
        }

        Utils.SendMessage(isUp ? GetString("Message.YTPlanCanNotFindRoleThePlayerEnter") : GetString("Message.CanNotFindRoleThePlayerEnter"), playerId, importance: MessageImportance.Low);
    }

    // -------------------------------------------------------------------------------------------------------------------------

    public static void OnReceiveChat(PlayerControl player, string text, out bool canceled)
    {
        canceled = false;
        if (player.AmOwner) return;

        if (!AmongUsClient.Instance.AmHost)
        {
            // 非ホストモッドクライアント: 非モッド送信者の /command 生テキストを隠す
            if (text.StartsWith('/') && !player.IsModdedClient())
                canceled = true;
            return;
        }

        long now = Utils.TimeStamp;

        // 禁止ワード判定は以降のどの早期 return よりも先に通す。下のコマンド連投スロットル (2秒) は
        // 非モッド送信者にだけ効くため、ここより後ろに置くと「直前に何かコマンドを打っておけば
        // 禁止ワードを言っても死なない」という抜け道になる (逆にモッド客だけ死ぬ非対称も生む)。
        // 判定は文字列の Contains のみで送信を伴わないので、スロットルの目的 (スパム抑制) とも衝突しない。
        WordKiller.OnAnyoneChat(player, text);

        if (LastSentCommand.TryGetValue(player.PlayerId, out long ts) && ts + 2 >= now && !player.IsModdedClient())
        {
            Logger.Warn("Chat message ignored, it was sent too soon after their last message", "ReceiveChat");
            return;
        }

        // 捕食中の赤ずきんは本当に死んでいるため、下の生存者向けブロックには入らない。
        // 塞がないとゴーストチャットで死者から情報を仕入れたまま生き返れてしまうので、別枠で先に落とす。
        if (GameStates.InGame && Akazukin.IsPseudoDead(player.PlayerId))
        {
            ChatManager.SendPreviousMessagesToAll();
            canceled = true;
            LastSentCommand[player.PlayerId] = now;
            return;
        }

        if (GameStates.InGame && (Silencer.ForSilencer.Contains(player.PlayerId) || (Main.PlayerStates[player.PlayerId].Role is Dad { IsEnable: true } dad && dad.UsingAbilities.Contains(Dad.Ability.GoForMilk))) && player.IsAlive())
        {
            ChatManager.SendPreviousMessagesToAll();
            canceled = true;
            LastSentCommand[player.PlayerId] = now;
            return;
        }

        if (text.StartsWith("\n")) text = text[1..];

        if (GameStates.IsMeeting && Exorcist.AbilityEndTS > now && player.IsAlive() && !text.StartsWith("/cmd") && !player.Is(CustomRoles.Pestilence))
        {
            player.RpcGuesserMurderPlayer();
            player.SetRealKiller(Main.EnumeratePlayerControls().FirstOrDefault(x => x.Is(CustomRoles.Exorcist)));
        }

        switch (Options.CurrentGameMode)
        {
            case CustomGameMode.TheMindGame when !player.IsModdedClient():
                TheMindGame.OnChat(player, text.ToLower());
                break;
            case CustomGameMode.BedWars:
                BedWars.OnChat(player, text);
                break;
        }

        CheckAnagramGuess(player.PlayerId, text.ToLower());

        foreach (PlayerState state in Main.PlayerStates.Values)
        {
            if (state.Role is Astral { Timer: not null } && state.Player && state.Player.PlayerId != player.PlayerId)
            {
                if (state.Player.AmOwner) canceled = true;
                else ChatManager.ClearChat(state.Player);
            }
        }

        if (!Starspawn.IsDayBreak)
        {
            if (GuessManager.GuesserMsg(player, text) ||
                Judge.TrialMsg(player, text) ||
                Swapper.SwapMsg(player, text) ||
                Inspector.InspectorCheckMsg(player, text) ||
                Councillor.MurderMsg(player, text) ||
                Newscaster.InterviewMsg(player, text) ||
                // EKR Wave 2 (docs/ekn-wave2-contract.md §1.2): ホスト側 (バニラ客含む全員) ディスパッチ。
                EndKnot.Modules.Ekm.EkrManager.PickMsg(player, text))
            {
                canceled = true;
                LastSentCommand[player.PlayerId] = now;
                return;
            }

            if (Medium.MsMsg(player, text) || Nemesis.NemesisMsgCheck(player, text))
            {
                LastSentCommand[player.PlayerId] = now;
                return;
            }
        }

        var commandEntered = false;

        if (text.StartsWith('/') && !player.IsModdedClient() && (!GameStates.IsMeeting || MeetingHud.Instance.state is not MeetingHud.VoteStates.Results and not MeetingHud.VoteStates.Proceeding))
        {
            Utils.CheckServerCommand(ref text, out bool spamRequired); // spamRequired == true は /cmd 未使用

            // ハイブリッド: /cmd 無し + 秘匿コマンド (Whisper 等の AlwaysHidden / AutoHidden) は
            // 送信側 vanilla が既に生 broadcast 済みなので、旧来の flood-clear (巨大空白 + 履歴サマリー再表示) で画面外へ押し出す。
            // /cmd 有りは +25 routing が他クライアントへ届けないので flood 不要 (巨大空白を出さない)。
            // flood は command 実行 *前* に走らせる (後だと正規出力が履歴再送で塗り潰される)。
            if (spamRequired && ShouldAutoHide(text))
            {
                canceled = true;
                ChatManager.SendPreviousMessagesToAll();
            }

            string[] args = text.Split(' ');

            foreach (Command command in Command.AllCommands)
            {
                if (!command.IsThisCommand(text)) continue;

                Logger.Info($" Recognized command: {text}", "ReceiveChat");
                commandEntered = true;

                if (!command.CanUseCommand(player, sendErrorMessage: true))
                {
                    canceled = true;
                    break;
                }

                command.Action(player, text, args);
                if (command.IsCanceled) canceled |= command.AlwaysHidden || !Options.HostSeesCommandsEnteredByOthers.GetBool();
                break;
            }
        }

        if (!commandEntered && Astral.On && !player.Is(CustomRoles.Astral))
            Main.PlayerStates.Values.DoIf(x => !x.IsDead && x.Role is Astral { Timer: not null } && x.Player, x => ChatManager.ClearChat(x.Player));

        if (CheckMute(player.PlayerId))
        {
            canceled = true;
            ChatManager.SendPreviousMessagesToAll();
            return;
        }

        if (ExileController.Instance)
        {
            canceled = true;
            HasMessageDuringEjectionScreen = true;
        }

        if (!canceled) ChatManager.SendMessage(player, text);

        switch (commandEntered)
        {
            case true:
                LastSentCommand[player.PlayerId] = now;
                break;
            case false:
                SpamManager.CheckSpam(player, text);
                break;
        }
    }

    private static void SendFactionChat(
        PlayerControl sender,
        string[] args,
        Func<PlayerControl, bool> isInFaction,
        OptionItem enabledOption,
        Color factionColor,
        char mark,
        string disabledKey)
    {
        if (!enabledOption.GetBool())
        {
            Utils.SendMessage("\n", sender.PlayerId, GetString(disabledKey));
            return;
        }

        if (!isInFaction(sender) || !sender.IsAlive())
        {
            Utils.SendMessage("\n", sender.PlayerId, GetString("Commands.FactionChat.NotInFaction"));
            return;
        }

        if (args.Length < 2)
        {
            Utils.SendMessage("\n", sender.PlayerId, GetString("Commands.FactionChat.NoMessage"));
            return;
        }

        string message = string.Join(' ', args.Skip(1));
        string title = Utils.ColorString(factionColor, $"{mark}{sender.GetRealName()}{mark}");
        string body = Utils.ColorString(factionColor, message);

        // 宛先は「生存派閥員 + 全死者(霊界傍聴)」。同期 batchWriter ループだと 500B ごとの flush が
        // 多人数の終盤で 1 フレームに複数 reliable パケットを連射し、9-12 RPC/frame の burst 閾値を
        // 超えてホストが公式鯖で Hacking kick される。実績ある throttle 経路 (SendMultipleMessages =
        // 0.4s 間引き + vanilla 宛は事前行分割) に載せて 1frame 集中を根絶する。
        List<Message> messages = [];

        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
        {
            if (pc.IsAlive() && !isInFaction(pc)) continue;
            messages.Add(new Message(body, pc.PlayerId, title));
        }

        messages.SendMultipleMessages();
    }

    private static void ImpostorChatCommand(PlayerControl player, string text, string[] args)
        => SendFactionChat(
            player, args,
            pc => pc.GetCustomRole().IsImpostor() || pc.Is(CustomRoles.Egoist),
            Options.EnableImpostorChat,
            Palette.ImpostorRed,
            '★',
            "Commands.FactionChat.ImpostorDisabled");

    private static void JackalChatCommand(PlayerControl player, string text, string[] args)
        => SendFactionChat(
            player, args,
            pc => pc.Is(CustomRoles.Jackal) || pc.Is(CustomRoles.Sidekick),
            Options.EnableJackalChat,
            Utils.GetRoleColor(CustomRoles.Jackal),
            'Φ',
            "Commands.FactionChat.JackalDisabled");

    private static void LoversChatCommand(PlayerControl player, string text, string[] args)
        => SendFactionChat(
            player, args,
            pc => pc.Is(CustomRoles.Lovers),
            Options.EnableLoversChat,
            Utils.GetRoleColor(CustomRoles.Lovers),
            '♥',
            "Commands.FactionChat.LoversDisabled");
}

[HarmonyPatch(typeof(ChatController), nameof(ChatController.Update))]
internal static class ChatUpdatePatch
{
    public static readonly List<(string Text, byte SendTo, string Title, long SendTimeStamp)> LastMessages = [];

    public static void Postfix(ChatController __instance)
    {
        var chatBubble = __instance.chatBubblePool.Prefab.CastFast<ChatBubble>();
        chatBubble.TextArea.overrideColorTags = false;

        if (Main.DarkTheme.Value)
        {
            chatBubble.TextArea.color = Color.white;
            chatBubble.Background.color = new(0.1f, 0.1f, 0.1f, 1f);
        }

        try
        {
            long now = Utils.TimeStamp;
            LastMessages.RemoveAll(x => now - x.SendTimeStamp > 10);
        }
        catch (Exception ex)
        {
            Logger.Error($"LastMessages cleanup failed, clearing list: {ex.Message}", "ChatUpdatePatch");
            LastMessages.Clear();
        }
    }

    internal static bool SendLastMessages(ref CustomRpcSender sender)
    {
        // 公式鯖でも他プレイヤー名義の発言が許可されたため、vanilla 特例 (host 名義固定) を撤去
        PlayerControl player = GameStates.IsLobby ? Main.EnumeratePlayerControls().Without(PlayerControl.LocalPlayer).RandomElement() : Main.EnumerateAlivePlayerControls().MinBy(x => x.PlayerId) ?? Main.EnumeratePlayerControls().MinBy(x => x.PlayerId) ?? PlayerControl.LocalPlayer;
        if (player == null) return false;

        bool wasCleared = false;

        foreach ((string msg, byte sendTo, string title, _) in LastMessages)
            wasCleared = SendMessage(player, msg, sendTo, title, ref sender);

        return LastMessages.Count > 0 && !wasCleared;
    }

    private static bool SendMessage(PlayerControl player, string msg, byte sendTo, string title, ref CustomRpcSender sender)
    {
        var broadcast = sendTo == byte.MaxValue;
        PlayerControl receiver = broadcast ? null : Utils.GetPlayerById(sendTo);

        // 宛先指定なのに相手が居ない場合、-1 に落とすとブロードキャストへ化けてしまうので再送を諦める。
        if (!broadcast && receiver == null) return false;

        int clientId = broadcast ? -1 : receiver.OwnerId;

        // 生の Data.PlayerName 読みは禁止 (BUG-20260710-05) — Utils.SafePlayerName 参照。
        // ここはロビーでホストを除いたランダムなプレイヤーが sender になる経路 (:4554) なので、
        // 「解放済みの名前を持つプレイヤー」を引く確率が構造的に一番高い。
        // ミラーが無いときは名前を安全に書き戻せないため、このメッセージの再送自体を諦める
        // (空名で SetName / RPC を書くと相手の名前が消えてしまう)。
        string name = Utils.SafePlayerName(player);
        if (name.Length == 0) return false;

        if (clientId == -1 && HudManager.InstanceExists)
        {
            player.SetName(title);
            HudManager.Instance.Chat.AddChat(player, msg);
            player.SetName(name);
        }

        // 宛先がホスト自身の unicast は自分宛 tag6 エンベロープになる。ローカル表示だけして RPC は組まない
        // (ロビー whisper の履歴が LastMessages に載るため、この再送経路でも実際に到達する)。
        if (!broadcast && receiver.AmOwner)
        {
            if (HudManager.InstanceExists)
            {
                player.SetName(title);
                HudManager.Instance.Chat.AddChat(player, msg);
                player.SetName(name);
            }

            return false;
        }

        sender.AutoStartRpc(player.NetId, RpcCalls.SetName, clientId)
            .Write(player.Data.NetId)
            .Write(title)
            .EndRpc();

        sender.AutoStartRpc(player.NetId, RpcCalls.SendChat, clientId)
            .Write(msg)
            .EndRpc();

        sender.AutoStartRpc(player.NetId, RpcCalls.SetName, clientId)
            .Write(player.Data.NetId)
            .Write(name)
            .EndRpc();

        if (sender.stream.Length > 500)
        {
            sender.SendMessage();
            sender = CustomRpcSender.Create(sender.name, sender.sendOption);
            return true;
        }

        return false;
    }
}

[HarmonyPatch(typeof(FreeChatInputField), nameof(FreeChatInputField.Awake))]
internal static class FreeChatFieldAwakePatch
{
    public static void Postfix(FreeChatInputField __instance)
    {
        UpdateCharCountPatch.Postfix(__instance);
    }
}

[HarmonyPatch(typeof(FreeChatInputField), nameof(FreeChatInputField.UpdateCharCount))]
internal static class UpdateCharCountPatch
{
    public static void Postfix(FreeChatInputField __instance)
    {
        int length = TextBoxPatch.SafeChatText(__instance.textArea).Length;
        __instance.charCountText.SetText(length <= 0 ? GetString("ThankYouForUsingEndKnot") : $"{length}/{__instance.textArea.characterLimit}");
        __instance.charCountText.enableWordWrapping = false;

        __instance.charCountText.color = length switch
        {
            < 800 => Color.black,
            < 1000 => new(1f, 1f, 0f, 1f),
            _ => Color.red
        };
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSendChat))]
internal static class RpcSendChatPatch
{
    public static bool Prefix(PlayerControl __instance, string chatText, ref bool __result)
    {
        if (Akazukin.IsPseudoDead(__instance.PlayerId))
        {
            __result = false;
            return false;
        }

        if (string.IsNullOrWhiteSpace(chatText))
        {
            __result = false;
            return false;
        }

        int return_count = PlayerControl.LocalPlayer.name.Count(x => x == '\n');
        chatText = new StringBuilder(chatText).Insert(0, "\n", return_count).ToString();
        if (AmongUsClient.Instance.AmClient && HudManager.InstanceExists) HudManager.Instance.Chat.AddChat(__instance, chatText);

        if (chatText.Contains("who", StringComparison.OrdinalIgnoreCase)) UnityTelemetry.Instance.SendWho();

        MessageWriter messageWriter = AmongUsClient.Instance.StartRpcImmediately(__instance.NetId, (byte)RpcCalls.SendChat, SendOption.Reliable);
        messageWriter.Write(chatText);
        EarlyWarning.OnPacket("RpcSendChat", messageWriter.Length, messageWriter.Length, "Reliable");
        AmongUsClient.Instance.FinishRpcImmediately(messageWriter);
        __result = true;
        return false;
    }
}

// ── Lobby Kill feature ─────────────────────────────────────────────────────────

internal static class LobbyKillSystem
{
    public static void ProcessLobbyKill(PlayerControl killer, byte targetId)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        PlayerControl target = Utils.GetPlayerById(targetId);
        if (target == null || target.Data == null || target.Data.IsDead) return;
        if (Main.LobbyDead.Contains(targetId)) return;

        Main.LobbyDead.Add(targetId);

        try
        {
            target.RpcExileV2();
        }
        catch (Exception ex) { Logger.Warn($"RpcExileV2 in lobby failed: {ex.Message}", "LobbyKill"); }

        try
        {
            Utils.RpcCreateDeadBody(target.transform.position, (byte)target.Data.DefaultOutfit.ColorId, target);
        }
        catch (Exception ex) { Logger.Warn($"RpcCreateDeadBody in lobby failed: {ex.Message}", "LobbyKill"); }

        target.Data.IsDead = true;
        target.Data.SetDirtyBit(0b_1u << targetId);
        AmongUsClient.Instance.SendAllStreamedObjects();

        try { target.RpcSetRole(RoleTypes.CrewmateGhost); } catch (Exception ex) { Logger.Warn($"RpcSetRole(CrewmateGhost) in lobby failed: {ex.Message}", "LobbyKill"); }

        try
        {
            if (HudManager.InstanceExists && killer != null && killer.KillSfx)
                SoundManager.Instance.PlaySound(killer.KillSfx, false, 0.8f);
        }
        catch (Exception ex) { Logger.Warn($"kill sfx in lobby failed: {ex.Message}", "LobbyKill"); }

        EndKnot.Modules.RPC.SyncLobbyState();

        string msg = string.Format(Translator.GetString("LobbyKill.ChatMessage"), killer.GetRealName(), target.GetRealName());
        Utils.SendMessage(msg);
    }
}
