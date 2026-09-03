using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Modules;

// ShipStatus.AllVents の位置は試合中に動かない。毎 tick の「最寄りベント」探索が全ベントの vent / transform を
// interop 越しに読み直すと、1 回につき 12×3 個前後のラッパー (= il2cpp 側 GC ハンドル) を作り、その解放が
// Boehm ヒープのゴミとして積もる (試合中の周期 GC 停止の原資)。マップ (ShipStatus) ごとに位置だけ 1 回読んで
// 使い回し、interop 越しの読みは自分の座標だけにする。
public static class VentPositionCache
{
    private static IntPtr shipPtr;
    private static IntPtr ventsPtr; // AllVents 配列のアドレス。ShipStatus のアドレス再利用と同時に一致することは実質ない
    private static int ventCount = -1;
    private static Vector2[] positions = [];
    private static Vent[] vents = [];

    // 最寄りベント。ShipStatus 不在なら null。
    public static Vent Closest(Vector2 from)
    {
        ShipStatus ship = ShipStatus.Instance;
        if (!ship) return null;

        Il2CppReferenceArray<Vent> all = ship.AllVents;
        if (all == null || all.Length == 0) return null;

        if (ship.Pointer != shipPtr || all.Pointer != ventsPtr || all.Length != ventCount) Rebuild(ship, all);

        int best = -1;
        float bestSqr = float.MaxValue;

        for (int i = 0; i < positions.Length; i++)
        {
            float d = (positions[i] - from).sqrMagnitude;

            if (d < bestSqr)
            {
                bestSqr = d;
                best = i;
            }
        }

        return best >= 0 ? vents[best] : null;
    }

    private static void Rebuild(ShipStatus ship, Il2CppReferenceArray<Vent> all)
    {
        int n = all.Length;
        var pos = new Vector2[n];
        var arr = new Vent[n];

        for (int i = 0; i < n; i++)
        {
            Vent v = all[i];
            arr[i] = v;
            pos[i] = v ? (Vector2)v.transform.position : new Vector2(float.MaxValue, float.MaxValue);
        }

        positions = pos;
        vents = arr;
        ventCount = n;
        shipPtr = ship.Pointer;
        ventsPtr = all.Pointer;
    }
}
