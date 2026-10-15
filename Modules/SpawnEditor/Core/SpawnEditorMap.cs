using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SpawnEditorKit;

// Pins on the in-game map while the spawn editor is active, and map-to-ship coordinate conversion for clicks.
[HarmonyPatch]
public static class SpawnEditorMap
{
    private static GameObject _root;

    public static bool IsOpen => MapBehaviour.Instance && MapBehaviour.Instance.IsOpen && _root;

    [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.Show))]
    [HarmonyPostfix]
    public static void ShowPostfix(MapBehaviour __instance)
    {
        try
        {
            if (!SpawnEditorUi.Instance || !SpawnEditorUi.Instance.Active) return;
            if (!__instance || !__instance.IsOpen) return;
            Rebuild(__instance);
        }
        catch (Exception e) { SpawnStore.Host?.Warn($"Spawn editor map pins failed: {e}"); }
    }

    [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.Close))]
    [HarmonyPostfix]
    public static void ClosePostfix() => Clear();

    public static void Clear()
    {
        if (_root) UnityEngine.Object.Destroy(_root);
        _root = null;
    }

    public static void Refresh()
    {
        // Also covers a map that was already open before the editor was.
        if (!SpawnEditorUi.Instance || !SpawnEditorUi.Instance.Active) return;
        if (MapBehaviour.Instance && MapBehaviour.Instance.IsOpen) Rebuild(MapBehaviour.Instance);
    }

    private static void Rebuild(MapBehaviour map)
    {
        Clear();

        SpriteRenderer here = map.HerePoint;
        Transform parent = here ? here.transform.parent : map.transform;
        if (!parent || !ShipStatus.Instance) return;

        int layer = here ? here.gameObject.layer : map.gameObject.layer;
        _root = new GameObject("SpawnEditorMapPins") { layer = layer };
        _root.transform.SetParent(parent, false);

        List<SpawnPoint> points = SpawnEditorUi.Instance.CurrentPoints;
        int selected = SpawnEditorUi.Instance.Selected;

        for (int i = 0; i < points.Count; i++)
        {
            SpawnPoint p = points[i];
            var go = new GameObject($"MapPin{i}") { layer = layer };
            go.transform.SetParent(_root.transform, false);

            Vector3 local = ShipToMap(new Vector2(p.X, p.Y));
            local.z = i == selected ? -1.02f : -1f;
            go.transform.localPosition = local;
            float s = i == selected ? 0.8f : 0.55f;
            go.transform.localScale = new Vector3(s, s, 1f);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = SpawnEditorArt.Pin;
            if (here)
            {
                sr.sortingLayerID = here.sortingLayerID;
                sr.sortingOrder = here.sortingOrder + 1;
            }

            sr.color = SpawnEditorUi.PinColor(p, SpawnFloorCheck.IsSpawnOnFloor(p.X, p.Y));
        }

        foreach ((Vector2 pos, Color color) in SpawnEditorUi.Instance.ScatterDots)
        {
            var go = new GameObject("MapDot") { layer = layer };
            go.transform.SetParent(_root.transform, false);
            Vector3 local = ShipToMap(pos);
            local.z = -1.01f;
            go.transform.localPosition = local;
            go.transform.localScale = new Vector3(0.35f, 0.35f, 1f);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = SpawnEditorArt.Dot;
            if (here)
            {
                sr.sortingLayerID = here.sortingLayerID;
                sr.sortingOrder = here.sortingOrder + 2;
            }

            sr.color = color;
        }
    }

    private static float FlipX => ShipStatus.Instance.transform.localScale.x < 0f ? -1f : 1f;

    private static Vector3 ShipToMap(Vector2 ship)
    {
        float scale = ShipStatus.Instance.MapScale;
        return new Vector3(ship.x / scale * FlipX, ship.y / scale, 0f);
    }

    // Screen point (Input.mousePosition) to a ship position, when the map is open.
    public static bool TryScreenToShip(Vector3 screen, out Vector2 ship)
    {
        ship = default;
        if (!IsOpen || !ShipStatus.Instance) return false;

        // The map is drawn by the HUD camera (fixed projection). Camera.main follows the player and zooms.
        Camera cam = HudManager.InstanceExists ? HudManager.Instance.UICamera : null;
        if (!cam) return false;

        Vector3 world = cam.ScreenToWorldPoint(screen);
        Vector3 local = _root.transform.parent.InverseTransformPoint(world);
        float scale = ShipStatus.Instance.MapScale;
        ship = new Vector2(local.x * scale * FlipX, local.y * scale);
        return true;
    }
}
