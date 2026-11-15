// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/SettingsCover.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/Options/SettingsCover.cs (GPL-3.0)
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EndKnot.SettingsUI;

// 設定画面に足した頁 (SortingGroup の順番 5) は描く順番を上げて本編の画面の手前に描くが、
// 本編の ESC の窓とチャットは順番 0 のままなので、それらの手前にも出てしまう。どちらかが開いている間は隠し、閉じたら隠した物だけ戻す。
// 開く・閉じるの後置で即座に見て、後置が呼ばれない道の分は 0.1 秒ごとにも見る (頁が 1 つも無い間は何もしない)
internal static class SettingsCover
{
    private const float Every = 0.1f;

    private static readonly List<GameObject> Roots = new();
    private static readonly List<GameObject> Hidden = new();
    private static bool _covered;
    private static float _next;

    internal static void Add(GameObject root)
    {
        if (!root) return;
        Roots.Add(root);
        if (_covered && root.activeSelf)
        {
            root.SetActive(false);
            Hidden.Add(root);
        }
    }

    // 設定画面が開いている間、毎フレーム
    internal static void Tick()
    {
        if (Roots.Count == 0 || Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + Every;
        Refresh();
    }

    internal static void Refresh()
    {
        if (Roots.Count == 0) return;
        for (int i = Roots.Count - 1; i >= 0; i--)
            if (!Roots[i]) Roots.RemoveAt(i);
        bool covered = Roots.Count > 0 && Covered();
        if (covered == _covered) return;
        _covered = covered;
        if (covered)
        {
            foreach (var r in Roots)
            {
                if (!r.activeSelf) continue;
                r.SetActive(false);
                Hidden.Add(r);
            }
        }
        else
        {
            foreach (var h in Hidden)
                if (h) h.SetActive(true);
            Hidden.Clear();
        }
    }

    // 設定画面を閉じた時 (頁は破棄される)
    internal static void Reset()
    {
        Roots.Clear();
        Hidden.Clear();
        _covered = false;
    }

    private static bool Covered()
    {
        var hud = HudManager.InstanceExists ? HudManager.Instance : null;
        if (!hud) return false;
        return hud.GameMenu && hud.GameMenu.IsOpen || hud.Chat && hud.Chat.IsOpenOrOpening;
    }
}

[HarmonyPatch]
internal static class SettingsCoverPatches
{
    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Open)), HarmonyPostfix]
    public static void Open() => SettingsCover.Refresh();

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Close)), HarmonyPostfix]
    public static void Close() => SettingsCover.Refresh();

    [HarmonyPatch(typeof(ChatController), nameof(ChatController.Toggle)), HarmonyPostfix]
    public static void Toggle() => SettingsCover.Refresh();
}
