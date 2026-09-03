using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EndKnot.Patches;

// ベント接続網を地図に重ねて描く、ホスト画面だけのローカル描画 (RPC は一切送らない)。
// 連結成分ごとに色を変えるので、同じ穴で繋がっているベント群が一目で分かる。
[HarmonyPatch]
public static class VentMinimapPatch
{
    private static OptionItem EnableVentNetworkMap;
    private static OptionItem ShowPlayerDots;

    public static void SetupCustomOption()
    {
        new TextOptionItem(110130, "MenuTitle.VentNetworkMap", TabGroup.GameSettings)
            .SetColor(new Color32(160, 220, 255, byte.MaxValue))
            .SetHeader(true);

        EnableVentNetworkMap = new BooleanOptionItem(960250, "EnableVentNetworkMap", false, TabGroup.GameSettings)
            .SetColor(new Color32(160, 220, 255, byte.MaxValue));

        ShowPlayerDots = new BooleanOptionItem(960251, "VentMapShowPlayerDots", true, TabGroup.GameSettings)
            .SetParent(EnableVentNetworkMap)
            .SetColor(new Color32(160, 220, 255, byte.MaxValue));
    }

    private static bool Enabled => EnableVentNetworkMap != null && EnableVentNetworkMap.GetBool();

    // ==== 状態 (Show のたびに作り直し、Close で破棄する — シーン遷移で無音破棄される罠を避ける) ====

    private static GameObject Root;
    private static Material LineMaterial;
    private static readonly List<GameObject> LineObjects = [];

    private const float LineWidth = 0.14f;

    private static bool IsDrawableMode(MapOptions opts) => opts.Mode is MapOptions.Modes.Normal or MapOptions.Modes.Sabotage;

    // ==== ドット (死亡後/観戦中に全員の現在地を見せる) — バニラの ShowLivePlayerPosition をそのまま借りる ====

    [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.Show))]
    private static class ShowDotsPrefixPatch
    {
        public static void Prefix(ref MapOptions opts)
        {
            if (!Enabled || ShowPlayerDots?.GetBool() != true) return;
            if (GameStates.IsMeeting) return;
            if (!IsDrawableMode(opts)) return;

            PlayerControl lp = PlayerControl.LocalPlayer;
            if (!lp || lp.IsAlive()) return;

            opts.ShowLivePlayerPosition = true;
        }
    }

    // ==== ベント接続網の描画 ====

    [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.Show))]
    private static class ShowDrawPatch
    {
        public static void Postfix(MapBehaviour __instance, MapOptions opts)
        {
            try
            {
                Cleanup();

                if (!Enabled) return;
                if (GameStates.IsMeeting) return;
                if (!__instance || !__instance.IsOpen) return; // 別 Prefix (Hacker/InfoPoor 等) が原体を止めていたら何もしない
                if (!IsDrawableMode(opts)) return;
                if (ShipStatus.Instance == null) return;

                Build(__instance);
            }
            catch (Exception e) { Logger.Exception(e, "VentMinimapPatch.Build"); }
        }
    }

    [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.Close))]
    private static class ClosePatch
    {
        public static void Postfix()
        {
            try { Cleanup(); }
            catch (Exception e) { Logger.Exception(e, "VentMinimapPatch.Cleanup"); }
        }
    }

    private static void Build(MapBehaviour instance)
    {
        SpriteRenderer donor = PickDonorSprite(instance);
        if (!donor) return;

        Transform parent = instance.HerePoint ? instance.HerePoint.transform.parent : instance.transform;
        if (!parent) return;

        Root = new GameObject("EndKnot_VentNetworkMap");
        Root.transform.SetParent(parent, false);
        Root.layer = donor.gameObject.layer; // 新規 GameObject は layer 0 で生まれ、SetParent しても継承しない

        // 借りたマテリアルは自分専用にコピーして1個だけ持つ (donor 本体には触らない)。
        // 全 LineRenderer で sharedMaterial として共有するので (Instance) は増えない。
        // 借り元は地図の半透明スプライト (実測では FadedBackground) なので、マテリアルの色をそのまま
        // 引き継ぐと線まで褪せる。白の不透明へ戻して、色は LineRenderer の頂点カラーだけで決める。
        LineMaterial = new Material(donor.sharedMaterial) { mainTexture = null, color = Color.white };

        Vent[] allVents = ShipStatus.Instance.AllVents;
        if (allVents == null || allVents.Length == 0) return;

        List<Vent> vents = allVents.Where(v => v && v.isActiveAndEnabled).ToList();
        if (vents.Count == 0) return;

        Dictionary<int, int> indexById = [];
        for (int i = 0; i < vents.Count; i++) indexById[vents[i].Id] = i;

        int[] component = new int[vents.Count];
        Array.Fill(component, -1);
        int componentCount = 0;

        for (int i = 0; i < vents.Count; i++)
        {
            if (component[i] != -1) continue;

            Queue<int> queue = new();
            queue.Enqueue(i);
            component[i] = componentCount;

            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                foreach (Vent nb in vents[cur].NearbyVents)
                {
                    if (!nb || !indexById.TryGetValue(nb.Id, out int nIdx) || component[nIdx] != -1) continue;
                    component[nIdx] = componentCount;
                    queue.Enqueue(nIdx);
                }
            }

            componentCount++;
        }

        HashSet<(int, int)> drawnEdges = [];

        for (int i = 0; i < vents.Count; i++)
        {
            foreach (Vent nb in vents[i].NearbyVents)
            {
                if (!nb || !indexById.TryGetValue(nb.Id, out int j) || i == j) continue;

                (int, int) key = i < j ? (i, j) : (j, i);
                if (!drawnEdges.Add(key)) continue;

                // プレイヤー色は明度が高く地図の塗りに埋もれるので、少し落として不透明で描く。
                Color baseColor = Palette.PlayerColors[component[i] % Palette.PlayerColors.Length];
                var color = new Color(baseColor.r * 0.8f, baseColor.g * 0.8f, baseColor.b * 0.8f, 1f);
                CreateLine(vents[i], vents[j], color, donor, instance.HerePoint);
            }
        }

        // 線が1本も出ないときに「設定が OFF」「借りるスプライトが無い」「ベントが 0 本」のどれなのかを
        // 後から切り分けられるようにしておく (描画物そのものはログを残さないため)。
        Logger.Info($"vent network drawn: vents={vents.Count} components={componentCount} lines={drawnEdges.Count}", "VentNetworkMap");

        // 「線は作ったのに画面に出ない」を切り分けるための実測値。借り元スプライトと HerePoint の
        // 描画順・レイヤー、実際に置いた座標を並べて、埋もれているのか画面外なのかを見分ける。
        if (LineObjects.Count > 0)
        {
            LineRenderer probe = LineObjects[0].GetComponent<LineRenderer>();
            SpriteRenderer here = instance.HerePoint;
            Logger.Info($"vent line probe: donor={donor.name} donorLayer={donor.sortingLayerID} donorOrder={donor.sortingOrder} " +
                        $"here={(here ? here.name : "null")} hereOrder={(here ? here.sortingOrder : -999)} hereLocal={(here ? here.transform.localPosition.ToString() : "-")} " +
                        $"lineLayer={probe.sortingLayerID} lineOrder={probe.sortingOrder} lineP0={probe.GetPosition(0)} lineP1={probe.GetPosition(1)} " +
                        $"goLayer={LineObjects[0].layer} donorGoLayer={donor.gameObject.layer} hereGoLayer={(here ? here.gameObject.layer : -1)} " +
                        $"root={Root.transform.parent?.name} mapScale={ShipStatus.Instance.MapScale} shipScaleX={ShipStatus.Instance.transform.localScale.x}", "VentNetworkMap");
        }
    }

    private static SpriteRenderer PickDonorSprite(MapBehaviour instance)
    {
        SpriteRenderer[] candidates = instance.GetComponentsInChildren<SpriteRenderer>(true);
        if (candidates == null || candidates.Length == 0) return null;

        foreach (SpriteRenderer sr in candidates)
        {
            if (!sr || !sr.sharedMaterial || !sr.sharedMaterial.shader) continue;
            if (sr.sharedMaterial.shader.name.Contains("Sprites")) return sr;
        }

        return candidates.FirstOrDefault(sr => sr);
    }

    private static void CreateLine(Vent a, Vent b, Color color, SpriteRenderer donor, SpriteRenderer herePoint)
    {
        GameObject go = new($"VentLink_{a.Id}_{b.Id}");
        go.transform.SetParent(Root.transform, false);
        go.layer = donor.gameObject.layer; // 地図を描いているカメラに刈られないよう、借り元と同じレイヤーに乗せる

        // レイヤーは donor (地図の背景スプライト) から借りるが、描画順は HerePoint (プレイヤー位置ドット) の
        // すぐ下に置く — donor の直下だと地図の背景に埋もれて見えないことがある。
        LineRenderer lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.positionCount = 2;
        lr.SetPosition(0, MapLocalPos(a.transform.position));
        lr.SetPosition(1, MapLocalPos(b.transform.position));
        lr.startWidth = LineWidth;
        lr.endWidth = LineWidth;
        lr.startColor = color;
        lr.endColor = color;
        lr.sharedMaterial = LineMaterial;
        lr.sortingLayerID = donor.sortingLayerID;
        // HerePoint (自分の位置ドット) と同じ描画順まで上げる。1つ下だと地図の部屋塗りに潜って
        // 線が褪せて見えた (実測)。ドット自体は z が手前なので、同列でも線に隠れない。
        lr.sortingOrder = herePoint ? herePoint.sortingOrder : donor.sortingOrder + 10;
        lr.receiveShadows = false;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        LineObjects.Add(go);
    }

    private static Vector3 MapLocalPos(Vector3 worldPos)
    {
        Vector3 p = worldPos / ShipStatus.Instance.MapScale;
        p.x *= Mathf.Sign(ShipStatus.Instance.transform.localScale.x);
        p.z = -0.99f;
        return p;
    }

    private static void Cleanup()
    {
        foreach (GameObject go in LineObjects)
            if (go) UnityEngine.Object.Destroy(go);
        LineObjects.Clear();

        if (Root) UnityEngine.Object.Destroy(Root);
        Root = null;

        if (LineMaterial) UnityEngine.Object.Destroy(LineMaterial);
        LineMaterial = null;
    }
}
