using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SpawnEditorKit;

// Everything the editor needs from the mod it runs in.
public interface ISpawnEditorHost
{
    string SavePath { get; }

    string Text(string key);
    void Log(string message);
    void Warn(string message);

    // A short message the player sees even when the editor window is closed.
    void Notify(string message);

    // Map id being edited, or -1 when the editor must not be shown (not in freeplay, unsupported map, loading).
    int EditMapId { get; }

    Vector2 LocalPlayerPosition { get; }
    void TeleportLocalPlayer(Vector2 position);
    void TeleportPlayer(PlayerControl player, Vector2 position);

    string RoomNameAt(Vector2 position);

    // Font and outline for world-space labels.
    void StyleText(TextMeshPro text);

    // Built-in random spawn positions of a map, and whether the lobby setting uses custom points only.
    // Used by the dummy test so it picks the same way a real game does.
    IReadOnlyList<Vector2> DefaultSpawnPositions(int mapId);
    bool CustomOnly { get; }

    // Whether the lobby settings let a real game use custom points at all.
    bool CustomActive { get; }

    // Lobby setting for custom points: 0 = off, 1 = mixed with the rooms, 2 = custom points only.
    // Unavailable when the current game mode never uses them.
    bool SpawnModeAvailable { get; }
    int SpawnMode { get; set; }

    // Folder for exported spawn files, and whether the platform can show it in a file browser.
    string ShareFolder { get; }
    bool CanOpenFolder { get; }
    void OpenFolder(string path);
}
