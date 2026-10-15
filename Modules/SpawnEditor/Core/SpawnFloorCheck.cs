using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpawnEditorKit;

// Walkable-floor grid for the current ship, built once per editor session.
// Room areas are not floors (they include wall art and space outside the hull), so the floor is found by
// walking a 0.3 grid from points known to be on the floor (vents, ladders, the local player) and
// stopping at anything that blocks a player's feet.
public static class SpawnFloorCheck
{
    public const float Step = 0.3f;

    // A spawn position is the body center; the feet are this far below.
    public const float FeetOffset = 0.3636f;

    // Swept with a foot-sized circle, not a line: some wall chains have ~0.1 gaps a line slips through.
    private const float FootRadius = 0.1f;

    // Android's engine lacks the layer-collision-matrix query. The matrix is game data, so the value
    // read on Windows (layers that collide with the player layer) is used there.
    private const int AndroidWallMask = unchecked((int)0xFFFC3ACF);

    private static float _x0, _y0;
    private static int _w, _h;
    private static byte[] _cells = []; // 0 = not reached, 1 = floor, 2 = reached from a seed outside the hull
    private static int _mask;

    public static bool Built => _w > 0;

    public static void Clear()
    {
        _w = _h = 0;
        _cells = [];
    }

    public static bool IsSpawnOnFloor(float x, float y) => IsFloorAt(x, y - FeetOffset);

    public static bool IsFloorAt(float x, float y)
    {
        if (_w == 0) return true;
        int gx = (int)MathF.Floor((x - _x0) / Step), gy = (int)MathF.Floor((y - _y0) / Step);
        return gx >= 0 && gy >= 0 && gx < _w && gy < _h && _cells[gy * _w + gx] == 1;
    }

    // Nearest floor cell center (as a spawn position, i.e. feet + offset) within maxDistance.
    public static bool TryNearestSpawn(Vector2 spawn, float maxDistance, out Vector2 result)
    {
        result = spawn;
        if (_w == 0) return true;
        if (IsSpawnOnFloor(spawn.x, spawn.y)) return true;

        float fx = spawn.x, fy = spawn.y - FeetOffset;
        int cx = (int)MathF.Floor((fx - _x0) / Step), cy = (int)MathF.Floor((fy - _y0) / Step);
        int reach = (int)MathF.Ceiling(maxDistance / Step);
        float best = maxDistance * maxDistance;
        bool found = false;

        for (int gy = Math.Max(0, cy - reach); gy <= Math.Min(_h - 1, cy + reach); gy++)
        {
            for (int gx = Math.Max(0, cx - reach); gx <= Math.Min(_w - 1, cx + reach); gx++)
            {
                if (_cells[gy * _w + gx] != 1) continue;
                float px = _x0 + (gx + 0.5f) * Step, py = _y0 + (gy + 0.5f) * Step;
                float d = (px - fx) * (px - fx) + (py - fy) * (py - fy);
                if (d >= best) continue;
                best = d;
                result = new Vector2(px, py + FeetOffset);
                found = true;
            }
        }

        return found;
    }

    public static void Build(ShipStatus ship, Vector2 playerPosition)
    {
        Clear();
        if (!ship) return;

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (PlainShipRoom room in ship.AllRooms)
        {
            if (!room || !room.roomArea) continue;
            Bounds b = room.roomArea.bounds;
            minX = MathF.Min(minX, b.min.x);
            minY = MathF.Min(minY, b.min.y);
            maxX = MathF.Max(maxX, b.max.x);
            maxY = MathF.Max(maxY, b.max.y);
        }

        if (minX > maxX) return;

        _x0 = MathF.Floor(minX) - 1f;
        _y0 = MathF.Floor(minY) - 1f;
        int w = (int)MathF.Ceiling((maxX + 1f - _x0) / Step);
        int h = (int)MathF.Ceiling((maxY + 1f - _y0) / Step);
        if (w <= 0 || h <= 0 || w * h > 200000) return;

        _mask = WallMask();
        _w = w;
        _h = h;
        _cells = new byte[w * h];

        var seeds = new List<Vector2>();
        foreach (Vent v in ship.AllVents)
            if (v) seeds.Add(v.transform.position);
        foreach (Ladder ladder in ship.GetComponentsInChildren<Ladder>(true))
            if (ladder) seeds.Add(ladder.transform.position);
        seeds.Add(new Vector2(playerPosition.x, playerPosition.y - FeetOffset));

        bool hitTriggers = Physics2D.queriesHitTriggers;
        Physics2D.queriesHitTriggers = false;

        try
        {
            var region = new List<int>();
            var queue = new Queue<int>();

            foreach (Vector2 s in seeds)
            {
                int gx = (int)MathF.Floor((s.x - _x0) / Step), gy = (int)MathF.Floor((s.y - _y0) / Step);
                if (gx < 0 || gy < 0 || gx >= w || gy >= h) continue;
                int start = gy * w + gx;
                if (_cells[start] != 0) continue;
                if (!NoWall(s.x, s.y, CellX(gx), CellY(gy))) continue;

                // Each seed floods its own region; a region that reaches the grid border started outside the hull.
                region.Clear();
                queue.Clear();
                _cells[start] = 3;
                queue.Enqueue(start);
                bool leaky = false;

                while (queue.Count > 0)
                {
                    int c = queue.Dequeue();
                    region.Add(c);
                    int cx = c % w, cy = c / w;
                    if (cx == 0 || cy == 0 || cx == w - 1 || cy == h - 1) leaky = true;

                    for (int d = 0; d < 4; d++)
                    {
                        int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int n = ny * w + nx;
                        if (_cells[n] != 0) continue;
                        if (!NoWall(CellX(cx), CellY(cy), CellX(nx), CellY(ny))) continue;
                        _cells[n] = 3;
                        queue.Enqueue(n);
                    }
                }

                byte mark = leaky ? (byte)2 : (byte)1;
                foreach (int c in region) _cells[c] = mark;
            }
        }
        finally { Physics2D.queriesHitTriggers = hitTriggers; }
    }

    private static float CellX(int gx) => _x0 + (gx + 0.5f) * Step;
    private static float CellY(int gy) => _y0 + (gy + 0.5f) * Step;

    private static int WallMask()
    {
        PlayerControl lp = PlayerControl.LocalPlayer;
        if (!lp) return Constants.ShipAndAllObjectsMask;
        int layer = lp.gameObject.layer;
        if (OperatingSystem.IsAndroid()) return AndroidWallMask & ~(1 << layer);
        return Physics2D.GetLayerCollisionMask(layer) & ~(1 << layer);
    }

    private static bool NoWall(float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float len = MathF.Sqrt(dx * dx + dy * dy);

        if (len < 0.0001f)
        {
            var overlaps = Physics2D.OverlapCircleAll(new Vector2(ax, ay), FootRadius, _mask);
            if (overlaps == null) return true;
            for (int i = 0; i < overlaps.Length; i++)
            {
                Collider2D c = overlaps[i];
                if (c && !c.isTrigger) return false;
            }

            return true;
        }

        var hits = Physics2D.CircleCastAll(new Vector2(ax, ay), FootRadius, new Vector2(dx / len, dy / len), len, _mask);
        if (hits == null) return true;
        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D c = hits[i].collider;
            if (c && !c.isTrigger) return false;
        }

        return true;
    }
}
