using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EndKnot.Modules;

// Shows the vanilla Influencer (SpiritGuide) picture cards to one player, sent by the host.
// The message only carries indices into the card list every client already has, and it goes out from
// ShipStatus rather than from a player, so any client shows it no matter who asked for it.
internal static class SpiritGuideCards
{
    public const byte Balloons = 0;
    public const byte DeadBody = 20;
    public const byte Vent = 35;
    public const byte BrokenBulb = 39;
    public const byte PirateFlag = 40;
    public const byte RadioactiveEye = 42;
    public const byte StopHand = 48;
    public const byte Warning = 52;
    public const byte CheckQuestion = 53;

    private const float MinSecondsBetweenMessages = 2f;
    private static readonly Dictionary<byte, float> LastSent = [];

    public static bool Send(PlayerControl target, params byte[] cards)
    {
        if (!AmongUsClient.Instance.AmHost || !target || cards == null || cards.Length == 0) return false;
        if (!GameStates.IsInTask || GameStates.IsMeeting || ExileController.Instance) return false;
        if (!target.Data || target.Data.Disconnected) return false;

        ShipStatus ship = ShipStatus.Instance;
        if (!ship) return false;

        // Receivers drop the message while comms are down, so don't spend a send on it.
        if (Utils.IsActive(SystemTypes.Comms)) return false;

        float now = Time.realtimeSinceStartup;
        if (LastSent.TryGetValue(target.PlayerId, out float last) && now - last < MinSecondsBetweenMessages) return false;

        int count = ship.GetSocialMediumSpriteList()?.Count ?? 0;
        if (count == 0) return false;

        byte[] indices = cards.Where(x => x < count).ToArray();
        if (indices.Length == 0) return false;

        if (target.AmOwner)
        {
            if (!ship.socialMediumFeedSystem) return false;
            ship.socialMediumFeedSystem.AddImageToFeed(indices);
        }
        else
        {
            if (target.OwnerId < 0) return false;
            ship.SendSpiritGuideMessage(indices, target.OwnerId);
        }

        LastSent[target.PlayerId] = now;
        return true;
    }
}
