using System;
using System.Collections.Generic;
using System.Diagnostics;
using EndKnot.Patches.CalamityMenu;
using SpawnEditorKit;
using TMPro;
using UnityEngine;

namespace EndKnot.Modules.SpawnEditor;

// Connects the portable spawn editor (SpawnEditorKit) to End K not.
internal sealed class EkSpawnEditorHost : ISpawnEditorHost
{
    public string SavePath => $"{Main.DataPath}/EndKnot_DATA/SpawnPoints.json";

    public string Text(string key) => Translator.GetString(key);
    public void Log(string message) => Logger.Info(message, "SpawnEditor");
    public void Warn(string message) => Logger.Warn(message, "SpawnEditor");
    public void Notify(string message) => Logger.SendInGame(message);

    public int EditMapId
    {
        get
        {
            if (!Il2Direct.Alive(AmongUsClient.Instance) || !GameStates.IsFreePlay) return -1;
            if (!Il2Direct.Alive(ShipStatus.Instance) || !Il2Direct.Alive(PlayerControl.LocalPlayer)) return -1;
            return EkSpawnEditor.ToStorageMapId(AmongUsClient.Instance.TutorialMapId);
        }
    }

    public Vector2 LocalPlayerPosition => PlayerControl.LocalPlayer.transform.position;

    public void TeleportLocalPlayer(Vector2 position) => PlayerControl.LocalPlayer.NetTransform.SnapTo(position);

    public void TeleportPlayer(PlayerControl player, Vector2 position) => player.NetTransform.SnapTo(position);

    public string RoomNameAt(Vector2 position)
    {
        PlainShipRoom best = null;
        float bestDist = float.MaxValue;

        foreach (PlainShipRoom room in ShipStatus.Instance.AllRooms)
        {
            if (!room) continue;
            if (room.roomArea && room.roomArea.OverlapPoint(position)) return Translator.GetString(room.RoomId);

            float d = Vector2.Distance(position, room.transform.position);
            if (d >= bestDist) continue;
            bestDist = d;
            best = room;
        }

        return best ? Translator.GetString(best.RoomId) : "";
    }

    public void StyleText(TextMeshPro text)
    {
        text.outlineColor = new Color32(0, 0, 0, 255);
        text.outlineWidth = 0.2f;
        CalamityFonts.Apply(text);
    }

    public IReadOnlyList<Vector2> DefaultSpawnPositions(int mapId)
    {
        RandomSpawn.SpawnMap map = mapId switch
        {
            0 => new RandomSpawn.SkeldSpawnMap(),
            1 => new RandomSpawn.MiraHQSpawnMap(),
            2 => new RandomSpawn.PolusSpawnMap(),
            4 => new RandomSpawn.AirshipSpawnMap(),
            5 => new RandomSpawn.FungleSpawnMap(),
            _ => null
        };

        if (map == null) return [];
        var list = new List<Vector2>(map.Positions.Values);
        if (mapId == 4 && !Options.AirshipAdditionalSpawn.GetBool() && list.Count > 6) list.RemoveRange(6, list.Count - 6);
        return list;
    }

    public bool CustomOnly => Options.CustomSpawnPointsMode?.GetValue() == 1;

    public bool CustomActive => SpawnModeAvailable && Options.RandomSpawn?.GetBool() == true && Options.CustomSpawnPoints?.GetBool() == true;

    public bool SpawnModeAvailable => Options.CurrentGameMode == CustomGameMode.Standard && Options.RandomSpawn != null && Options.CustomSpawnPoints != null && Options.CustomSpawnPointsMode != null;

    public int SpawnMode
    {
        get => !CustomActive ? 0 : CustomOnly ? 2 : 1;
        set
        {
            if (!SpawnModeAvailable) return;
            // Freeplay has no other clients, so the values are only saved, not synced.
            if (value > 0)
            {
                Options.RandomSpawn.SetValue(1, false, false);
                Options.CustomSpawnPointsMode.SetValue(value == 2 ? 1 : 0, false, false);
            }

            Options.CustomSpawnPoints.SetValue(value > 0 ? 1 : 0, true, false);
        }
    }

    public string ShareFolder => $"{Main.DataPath}/EndKnot_DATA/SpawnPoints/";

    public bool CanOpenFolder => OperatingSystem.IsWindows();

    public void OpenFolder(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        // explorer cannot resolve the relative data path.
        string full = System.IO.Path.GetFullPath(path);
        System.IO.Directory.CreateDirectory(full);
        Process.Start("explorer.exe", full.Replace("/", "\\"));
    }
}

internal static class EkSpawnEditor
{
    public static void Init() => SpawnStore.Init(new EkSpawnEditorHost());

    // Dleks has no layout of its own: it reuses the Skeld points mirrored (and freeplay "Dleks" opens Skeld).
    public static int ToStorageMapId(int mapId) => mapId switch
    {
        0 or 3 => 0,
        1 or 2 or 4 or 5 => mapId,
        _ => -1
    };

    // Picks a user-defined spawn point for a random spawn, or returns false to fall back to the built-in rooms.
    public static bool TryPick(RandomSpawn.SpawnMap map, out Vector2 position, out string name)
    {
        position = default;
        name = null;

        if (!Options.RandomSpawn.GetBool() || Options.CustomSpawnPoints?.GetBool() != true) return false;
        if (Options.CurrentGameMode != CustomGameMode.Standard) return false;

        int mapId = (int)Main.CurrentMap;
        int storageId = ToStorageMapId(mapId);
        if (storageId < 0) return false;

        List<SpawnPoint> points = SpawnStore.GetEnabledPoints(storageId);
        bool customOnly = Options.CustomSpawnPointsMode.GetValue() == 1;
        int defaultRooms = map is RandomSpawn.AirshipSpawnMap && !Options.AirshipAdditionalSpawn.GetBool() ? 6 : map.Positions.Count;

        if (!SpawnPicker.TryPick(points, defaultRooms, customOnly, out SpawnPoint picked)) return false;

        position = new Vector2(mapId == 3 ? -picked.X : picked.X, picked.Y);
        name = picked.Name;
        return true;
    }
}
