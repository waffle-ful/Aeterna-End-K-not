using System;
using UnityEngine;

namespace EndKnot.Modules;

// GUILayout を使わない IMGUI コンポーネントは Layout イベントが要らない。止めると OnGUI の呼び出しが
// 1 フレーム 2 回から 1 回 (Repaint) に減る (入力イベントの分は変わらない)。
internal static class ImguiNoLayout
{
    public static void Apply(MonoBehaviour behaviour)
    {
        try { behaviour.useGUILayout = false; }
        catch (Exception e) { Logger.Warn($"useGUILayout=false failed on {behaviour.GetType().Name}: {e.Message}", "ImguiNoLayout"); }
    }
}
