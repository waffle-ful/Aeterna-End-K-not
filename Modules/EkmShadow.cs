using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Modules;

// ============================================================================
// EKM 影レイヤー ランタイム (Phase A)
//
// カスタムマップの「影専用レイヤー」(ユーザーがエディタでベクター線=遮蔽辺を引く) を
// 実機の影 caster に変換する。マップチップ由来の occluder (旧 CustomMapCastShadows) は
// 端で破綻したので廃止 → 代わりにユーザーが明示的に引いた線だけを影源にする。
//
// 仕組み (reference_au_map_construction_shadows / BackroomsShadow.SpawnTestEdgeRoom と同型):
//   各折れ線 → layer10 (Shadow) の GameObject + EdgeCollider2D.points。
//   バニラ GPU 影ドライバ (BackroomsShadow.Drive、EnterCustomMap で Arm 済) が
//   Physics2D.OverlapCircle(プレイヤー足元, 半径, ShadowMask) で layer10 を拾い、
//   各 collider の辺を遮蔽メッシュ化 → プレイヤー光源中心の滑らかな影になる。
//
//   ・発射点はプレイヤー body center 固定 (AU が transform 駆動)。ユーザーは「壁の位置」だけ引けばよい。
//   ・線は厚さゼロなので Lo/Hi 面選択ロジック (BackroomsCasters の二重壁 gating) は不要。
//   ・layer10 はプレイヤー衝突 (layer9) と別なので、影線は通行を塞がない (= 影専用)。衝突はタイル pass 属性が担当。
// ============================================================================
public static class EkmShadow
{
    private const string Tag = "EkmShadow";

    private static readonly List<GameObject> _segments = [];

    // true で各影線をシアンのスプライトで可視化する (/map debug casters 用)。既定 false (影本体だけ出す)。
    public static bool Visualize;

    // カスタムマップ入場時に呼ぶ (EnterCustomMap、SpawnCustomMapBoundaryWalls の直後)。
    // src.ShadowLines (セル座標フラット折れ線) を layer10 EdgeCollider2D 化する。
    public static void Spawn(CustomMapSource src)
    {
        Clear();
        if (src?.ShadowLines == null || src.ShadowLines.Count == 0) return;

        float cs = src.CellSize;
        foreach (float[] line in src.ShadowLines)
        {
            if (line == null || line.Length < 4 || (line.Length & 1) != 0) continue;

            int n = line.Length / 2;
            var pts = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                // セル座標 → ワールド座標 (タイル/spawn/decor と同式: world = OriginX + x*cs, OriginY - y*cs)
                pts[i] = new Vector2(EkmapLoader.OriginX + line[i * 2] * cs, EkmapLoader.OriginY - line[i * 2 + 1] * cs);
            }

            // GO はワールド原点に置き、collider points を直接ワールド座標 (= local 座標) として与える。
            GameObject go = new("EkmShadowLine");
            go.transform.SetParent(null, false);
            go.transform.position = Vector3.zero;
            go.layer = BackroomsConfig.ShadowCasterLayer; // layer10 (Shadow)

            EdgeCollider2D ec = go.AddComponent<EdgeCollider2D>();
            Il2CppStructArray<Vector2> arr = new(n);
            for (int i = 0; i < n; i++) arr[i] = pts[i];
            ec.points = arr;

            if (Visualize)
                for (int i = 0; i < n - 1; i++)
                    AddSegmentSprite(go, pts[i], pts[i + 1]);

            _segments.Add(go);
        }

        Logger.Info($"Spawn: {_segments.Count} caster line(s) from {src.ShadowLines.Count} entries (cellSize={cs:F2})", Tag);
    }

    // カスタムマップ退出 / procgen 復帰 / リロード時に呼ぶ (DestroyCustomMapBoundaryWalls 内に同居)。
    public static void Clear()
    {
        for (int i = 0; i < _segments.Count; i++)
            if (_segments[i] != null) Object.Destroy(_segments[i]);
        _segments.Clear();
    }

    public static int Count => _segments.Count;

    // ---- 可視化ヘルパー (Visualize=true 時のみ) ------------------------------

    // 2 点間を細いシアンスプライト矩形で結ぶ。GO は原点なので a/b はそのまま local 座標。
    private static void AddSegmentSprite(GameObject parent, Vector2 a, Vector2 b)
    {
        Vector2 mid = (a + b) * 0.5f;
        Vector2 d = b - a;
        float len = d.magnitude;
        if (len < 0.001f) return;
        float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;

        GameObject seg = new("vis");
        seg.transform.SetParent(parent.transform, false);
        seg.transform.localPosition = new Vector3(mid.x, mid.y, 0f);
        seg.transform.localRotation = Quaternion.Euler(0f, 0f, ang);
        seg.transform.localScale = new Vector3(len, 0.12f, 1f);
        SpriteRenderer sr = seg.AddComponent<SpriteRenderer>();
        sr.sprite = MarkerSprite;
        sr.color = new Color(0.2f, 0.95f, 1f, 0.9f);
        sr.sortingOrder = 120;
    }

    private static Sprite _markerSprite;
    private static Sprite MarkerSprite
    {
        get
        {
            if (_markerSprite != null) return _markerSprite;
            Texture2D tex = new(4, 4, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            Color[] pixels = new Color[16];
            for (int i = 0; i < 16; i++) pixels[i] = Color.white;
            tex.SetPixels(pixels);
            tex.Apply();
            _markerSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            _markerSprite.hideFlags |= HideFlags.HideAndDontSave;
            return _markerSprite;
        }
    }
}
