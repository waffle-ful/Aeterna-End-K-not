using EndKnot.Modules.CalamityMenu;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

public static class MenuRoot
{
    public static GameObject Create(MainMenuManager mm)
    {
        var root = new GameObject("EndKnotMenuRoot");
        root.transform.SetParent(mm.transform);
        root.transform.localPosition = Vector3.zero;
        CalamityMenuState.Root = root;

        CreateLayer(root, "BackgroundLayer", 100f);
        CreateLayer(root, "ParticleLayer",    50f);
        CreateLayer(root, "ForegroundLayer",  30f);
        CreateLayer(root, "LogoLayer",        10f);
        CreateLayer(root, "ButtonLayer",       5f);
        CreateLayer(root, "OverlayLayer",      1f);
        return root;
    }

    public static Transform GetLayer(string name)
        => CalamityMenuState.Root?.transform.Find(name);

    private static void CreateLayer(GameObject root, string name, float z)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform);
        go.transform.localPosition = new Vector3(0f, 0f, z);
    }
}
