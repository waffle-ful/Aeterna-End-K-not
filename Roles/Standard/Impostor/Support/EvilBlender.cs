using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

public class EvilBlender : RoleBase
{
    private const int Id = 700800;

    public static bool On;
    public static byte UseingId = byte.MaxValue;
    public static Dictionary<byte, SystemTypes?> PlayerRooms = [];

    public static OptionItem AbilityCooldown;
    private static OptionItem SabotageLimitTime;

    private byte EvilBlenderId;
    private bool IsUsed;
    private float limittimer;
    private float sendtimer;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.EvilBlender);

        AbilityCooldown = new FloatOptionItem(Id + 10, "AbilityCooldown", new(1f, 120f, 1f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilBlender])
            .SetValueFormat(OptionFormat.Seconds);

        SabotageLimitTime = new FloatOptionItem(Id + 11, "EvilBlenderSabotageLimittime", new(10f, 90f, 1f), 45f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilBlender])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        On = false;
        UseingId = byte.MaxValue;
        PlayerRooms = [];
    }

    public override void Add(byte playerId)
    {
        On = true;
        EvilBlenderId = playerId;
        IsUsed = false;
        limittimer = 0f;
        sendtimer = 0f;
    }

    public override void Remove(byte playerId)
    {
        On = false;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        float cd = IsUsed ? 200f : AbilityCooldown.GetFloat();
        if (IntroCutsceneDestroyPatch.PreventKill) cd = Mathf.Max(cd, 12f);

        if (Options.UsePhantomBasis.GetBool())
            AURoleOptions.PhantomCooldown = cd;
        else if (!Options.UsePets.GetBool())
        {
            AURoleOptions.ShapeshifterCooldown = cd;
            AURoleOptions.ShapeshifterDuration = 1f;
        }
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        string text = Translator.GetString("EvilBlender_AbilityButton");
        if (Options.UsePets.GetBool() && !Options.UsePhantomBasis.GetBool())
            hud.PetButton?.OverrideText(text);
        else
            hud.AbilityButton?.OverrideText(text);
    }

    public override void OnPet(PlayerControl pc) => OnAbility(pc);

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return true;
        OnAbility(shapeshifter);
        return false;
    }

    public override bool OnVanish(PlayerControl pc)
    {
        OnAbility(pc);
        return false;
    }

    private void OnAbility(PlayerControl pc)
    {
        if (IsUsed || UseingId != byte.MaxValue) return;
        if (Utils.IsAnySabotageActive()) return;

        UseingId = pc.PlayerId;
        IsUsed = true;
        limittimer = 0f;
        sendtimer = 0f;

        List<SystemTypes> allRooms = ShipStatus.Instance.AllRooms
            .Where(r => r?.RoomId != null)
            .Select(r => r.RoomId)
            .Where(r => r != SystemTypes.Hallway && r != SystemTypes.Outside && r != SystemTypes.Ventilation && !r.ToString().Contains("Decontamination"))
            .ToList();

        foreach (PlayerControl player in Main.AllAlivePlayerControls)
        {
            List<SystemTypes> rooms = new(allRooms);
            PlainShipRoom currentRoom = player.GetPlainShipRoom();
            if (currentRoom != null) rooms.Remove(currentRoom.RoomId);
            if (rooms.Count == 0) rooms = allRooms;

            SystemTypes assigned = rooms[IRandom.Instance.Next(0, rooms.Count)];
            PlayerRooms[player.PlayerId] = assigned;
        }

        pc.SyncSettings();
        pc.RpcResetAbilityCooldown();
        Utils.NotifyRoles();
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !GameStates.IsInTask) return;
        if (UseingId != EvilBlenderId) return;
        SabotageCheck();
    }

    private void SabotageCheck()
    {
        float limitTime = SabotageLimitTime.GetFloat();
        bool timerExpired = limittimer >= limitTime;
        List<byte> toRemove = [];
        List<(PlayerControl Pc, SystemTypes Room)> toWarn = [];

        foreach ((byte playerId, SystemTypes? assignedRoom) in PlayerRooms)
        {
            PlayerControl pc = Utils.GetPlayerById(playerId);
            if (pc == null || !pc.IsAlive())
            {
                toRemove.Add(playerId);
                continue;
            }

            PlainShipRoom currentRoom = pc.GetPlainShipRoom();
            if (currentRoom?.RoomId == assignedRoom)
            {
                toRemove.Add(playerId);
                continue;
            }

            if (timerExpired)
            {
                PlayerControl blender = Utils.GetPlayerById(EvilBlenderId);
                if (pc.inVent)
                    pc.MyPhysics.RpcBootFromVent(pc.GetClosestVent()?.Id ?? 0);
                pc.SetRealKiller(blender);
                pc.Suicide(PlayerState.DeathReason.Trapped, blender);
            }
            else if (assignedRoom.HasValue)
            {
                toWarn.Add((pc, assignedRoom.Value));
            }
        }

        foreach (byte id in toRemove)
            PlayerRooms.Remove(id);

        if (timerExpired)
        {
            EndMiniSab();
            return;
        }

        limittimer += Time.fixedDeltaTime;
        sendtimer += Time.fixedDeltaTime;

        if (sendtimer >= 1f)
        {
            int timeLeft = (int)(limitTime - limittimer);
            foreach ((PlayerControl warnPc, SystemTypes room) in toWarn)
            {
                string roomName = Translator.GetString(room.ToString());
                string msg = string.Format(Translator.GetString("EvilBlender_SabotageLowerAlive"), timeLeft, roomName);
                warnPc.Notify($"<size=2.5><color=red>!! {msg} !!</color></size>", 1.5f, overrideAll: true);
            }
            Utils.NotifyRoles();
            sendtimer = 0f;
        }
    }

    private void EndMiniSab()
    {
        UseingId = byte.MaxValue;
        PlayerRooms.Clear();
        limittimer = 0f;
        Utils.NotifyRoles();
    }

    public override bool OnSabotage(PlayerControl pc)
    {
        return UseingId == byte.MaxValue;
    }

    public override void OnReportDeadBody()
    {
        if (UseingId != byte.MaxValue)
            EndMiniSab();
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud, bool meeting)
    {
        if (meeting) return string.Empty;
        if (seer.PlayerId != target.PlayerId) return string.Empty;
        if (UseingId == byte.MaxValue) return string.Empty;
        if (UseingId != EvilBlenderId) return string.Empty;

        if (PlayerRooms.TryGetValue(seer.PlayerId, out SystemTypes? room) && room.HasValue)
            return string.Format(Translator.GetString("EvilBlender_SabotageLowerAlive"),
                (int)(SabotageLimitTime.GetFloat() - limittimer),
                Translator.GetString(room.Value.ToString()));

        return string.Format(Translator.GetString("EvilBlender_SabotageLower"),
            (int)(SabotageLimitTime.GetFloat() - limittimer));
    }
}
