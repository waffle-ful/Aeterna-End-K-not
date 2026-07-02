using System;
using EndKnot.Modules.VoiceVox;
using EndKnot.Roles;
using HarmonyLib;

namespace EndKnot;

// Leak-safe hook for host-local per-crew VoiceVox TTS.
//
// Prefix on ChatManager.SendMessage(PlayerControl, string) — the unified broadcast chokepoint for
// player chat. Both callers are guarded by `if (!canceled)`, and the overload has NO recipient
// parameter, so it is structurally a broadcast classifier: subset-visible channels (whisper, faction
// chat, guesses) are either canceled upstream or injected via Utils.SendMessage and never arrive here.
//
// The prefix runs BEFORE the method body, so it replicates the body's own L263 guards (host / alive /
// silenced) itself. It reads the ORIGINAL message (the body lowercases it) and only ever OBSERVES —
// it never cancels the message and never sends anything.
//
// Why every "do-not-speak" case is covered (audit 2026-07-02):
//   /cmd w -> /w, /lc, /w, /jt, faction chats  : all retain a leading '/' and/or are injected via
//                                                 Utils.SendMessage; excluded by the '/' test / never reach here.
//   dead / ghost chat ("霊界")                 : !player.IsAlive().
//   Lovers.PrivateChat (task-phase, non-slash) : the one real leak — a lover's private task chat is a
//                                                 plain broadcast that DOES reach here; guarded explicitly.
//   TheMindGame / BedWars command chatter      : not hidden info, but noisy -> scoped to Standard mode.
[HarmonyPatch(typeof(ChatManager), nameof(ChatManager.SendMessage))]
internal static class VoiceVoxChatPatch
{
    public static void Prefix(PlayerControl player, string message)
    {
        try
        {
            if (!Main.EnableVoiceVox.Value) return;
            if (!OperatingSystem.IsWindows()) return;
            if (!AmongUsClient.Instance.AmHost) return;
            if (player == null || !player.IsAlive()) return;
            if (Silencer.ForSilencer.Contains(player.PlayerId)) return;
            if (Options.CurrentGameMode != CustomGameMode.Standard) return;
            if (string.IsNullOrWhiteSpace(message) || message.TrimStart().StartsWith('/')) return;
            if (Lovers.PrivateChat.GetBool() && !GameStates.IsMeeting && player.Is(CustomRoles.Lovers)) return;
            if (!Main.VoiceVoxReadHostOwnChat.Value && player.AmOwner) return;

            VoiceVoxManager.Speak(player, message);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }
}
