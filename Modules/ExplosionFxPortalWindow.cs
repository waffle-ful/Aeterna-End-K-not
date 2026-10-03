using UnityEngine;

namespace EndKnot.Modules;

// ポータルの中にワープ先の景色を映す (End K not を入れている人の画面だけ)。
// 行き先ごとに小さなカメラを置いて小さな描き先へ写し、それを楕円の板に貼る。楕円は板の形そのもので作るので型抜きは要らない。
// 人・死体・演出の粒・影 (視界の外の暗さ) は写さない — 視界の外にいる人が見えると、入れていない人より有利になる。
public static partial class ExplosionFx
{
    private sealed class PortalWindow
    {
        public Camera Cam;
        public RenderTexture Rt;
        public GameObject Go;
        public Material Mat;
        public MeshRenderer Renderer;
        public Vector2 At;
        public Vector2 View;
    }

    private static readonly PortalWindow[] Windows = new PortalWindow[2];
    private static Mesh _windowMesh;

    private const int WindowTexW = 160;
    private const int WindowTexH = 240;
    // 窓に写す範囲 (楕円の半分の高さの何倍か)。1 より大きいと少し引いた景色になる
    private const float WindowZoom = 1.35f;
    // 自分のカメラからこれより遠い窓は写さない (見えない窓のために毎フレーム描かない)
    private const float WindowReach = 14f;

    // 写さない層: 0 = 演出・死体 / 5 = UI / 8 = 人 / 10 = 影 / 14 = 幽霊
    private const int WindowHiddenLayers = (1 << 0) | (1 << 5) | (1 << 8) | (1 << 10) | (1 << 14);

    private static void UpdatePortalWindows(float camX, float camY)
    {
        if (PortalEmitters.Count != 2)
        {
            if (Windows[0] != null || Windows[1] != null) ReleasePortalWindows();
            return;
        }

        for (int i = 0; i < 2; i++)
        {
            Vector2 at = PortalEmitters[i].Pos, view = PortalEmitters[1 - i].Pos;
            PortalWindow w = Windows[i];

            if (w == null || !w.Go || !w.Cam || !w.Rt || FxMath.Abs(w.At.x - at.x) > 0.01f || FxMath.Abs(w.At.y - at.y) > 0.01f || FxMath.Abs(w.View.x - view.x) > 0.01f || FxMath.Abs(w.View.y - view.y) > 0.01f)
            {
                ReleaseWindow(i);
                w = Windows[i] = BuildWindow(at, view);
                if (w == null) return;
            }

            float dx = at.x - camX, dy = at.y - camY;
            bool near = dx * dx + dy * dy < WindowReach * WindowReach;
            if (w.Cam.enabled != near) w.Cam.enabled = near;
            if (w.Renderer.enabled != near) w.Renderer.enabled = near;
        }
    }

    private static PortalWindow BuildWindow(Vector2 at, Vector2 view)
    {
        Camera main = Camera.main;
        Shader shader = Shader.Find("UI/Default");
        if (!main || !shader) return null;

        EnsureWindowMesh();

        var w = new PortalWindow { At = at, View = view };

        // 部屋の床と壁はステンシルで描くので、ステンシル付きの深度 (24) が要る
        w.Rt = new RenderTexture(WindowTexW, WindowTexH, 24) { name = "EK_PortalWindow" };
        w.Rt.hideFlags |= HideFlags.HideAndDontSave;

        var camGo = new GameObject("EK_PortalWindowCam");
        w.Cam = camGo.AddComponent<Camera>();
        w.Cam.CopyFrom(main);
        // 影の板 (メインカメラの前に付いていて、自分の視界の暗さを描く) も写さない。窓の行き先がメインカメラの写す範囲に重なると、
        // その板まで写り込んで窓の中身が自分の位置で変わってしまう
        int hidden = WindowHiddenLayers;
        if (HudManager.InstanceExists && HudManager.Instance.ShadowQuad) hidden |= 1 << HudManager.Instance.ShadowQuad.gameObject.layer;
        w.Cam.cullingMask = main.cullingMask & ~hidden;
        w.Cam.clearFlags = CameraClearFlags.SolidColor;
        w.Cam.backgroundColor = main.backgroundColor;
        w.Cam.orthographic = true;
        w.Cam.orthographicSize = PortalH * WindowZoom;
        w.Cam.aspect = PortalW / PortalH;
        w.Cam.depth = main.depth - 2f;
        w.Cam.targetTexture = w.Rt;
        camGo.transform.position = FxMath.V3(view.x, view.y, main.transform.position.z);

        w.Go = new GameObject("EK_PortalWindow") { layer = 0 };
        w.Go.transform.position = FxMath.V3(at.x, at.y, 0f);
        w.Go.transform.localScale = FxMath.V3(PortalW, PortalH, 1f);
        w.Go.AddComponent<MeshFilter>().sharedMesh = _windowMesh;
        w.Mat = new Material(shader) { name = "EK_PortalWindow" };
        w.Mat.hideFlags |= HideFlags.HideAndDontSave;
        // 描き先の透明度は使わない (床の絵が半透明でも窓は不透明に見せる)
        w.Mat.SetVector("_TextureSampleAdd", new Vector4(0f, 0f, 0f, 1f));
        w.Mat.mainTexture = w.Rt;
        w.Renderer = w.Go.AddComponent<MeshRenderer>();
        w.Renderer.sharedMaterial = w.Mat;
        w.Renderer.sortingOrder = SortingOrder + 2;

        Logger.Info($"portal window at ({at.x:F1}, {at.y:F1}) shows ({view.x:F1}, {view.y:F1}) mask=0x{w.Cam.cullingMask:X}", "ExplosionFx");
        return w;
    }

    // 半径 1 の楕円 (板の大きさで縦横を決める)。芯はくっきり、縁の外側 1 割で透明へ溶かす。色はわずかに青緑へ寄せる
    private static void EnsureWindowMesh()
    {
        if (_windowMesh) return;

        const int seg = 32;
        const float inner = 0.88f;
        var verts = new Vector3[1 + seg * 2];
        var uvs = new Vector2[verts.Length];
        var cols = new Color[verts.Length];
        var tris = new int[seg * 9];
        Color core = FxMath.Rgba(0.85f, 1f, 1f, 0.95f), edge = FxMath.Rgba(0.6f, 1f, 1f, 0f);

        verts[0] = FxMath.V3(0f, 0f, 0f);
        uvs[0] = FxMath.V2(0.5f, 0.5f);
        cols[0] = core;

        for (int k = 0; k < seg; k++)
        {
            float a = k * 2f * FxMath.PI / seg;
            float cx = FxMath.Cos(a), cy = FxMath.Sin(a);
            verts[1 + k] = FxMath.V3(cx * inner, cy * inner, 0f);
            uvs[1 + k] = FxMath.V2(0.5f + 0.5f * cx * inner, 0.5f + 0.5f * cy * inner);
            cols[1 + k] = core;
            verts[1 + seg + k] = FxMath.V3(cx, cy, 0f);
            uvs[1 + seg + k] = FxMath.V2(0.5f + 0.5f * cx, 0.5f + 0.5f * cy);
            cols[1 + seg + k] = edge;

            int n = (k + 1) % seg, t = k * 9;
            tris[t] = 0; tris[t + 1] = 1 + n; tris[t + 2] = 1 + k;
            tris[t + 3] = 1 + k; tris[t + 4] = 1 + n; tris[t + 5] = 1 + seg + k;
            tris[t + 6] = 1 + n; tris[t + 7] = 1 + seg + n; tris[t + 8] = 1 + seg + k;
        }

        _windowMesh = new Mesh { name = "EK_PortalWindow" };
        _windowMesh.vertices = verts;
        _windowMesh.uv = uvs;
        _windowMesh.colors = cols;
        _windowMesh.triangles = tris;
        _windowMesh.RecalculateBounds();
        _windowMesh.hideFlags |= HideFlags.HideAndDontSave;
    }

    private static void ReleaseWindow(int i)
    {
        PortalWindow w = Windows[i];
        Windows[i] = null;
        if (w == null) return;

        if (w.Cam) w.Cam.targetTexture = null;
        if (w.Cam) Object.Destroy(w.Cam.gameObject);
        if (w.Go) Object.Destroy(w.Go);
        if (w.Mat) Object.Destroy(w.Mat);
        if (w.Rt)
        {
            w.Rt.Release();
            Object.Destroy(w.Rt);
        }
    }

    // 会議中は窓を写さない (明けたら近さの判定でまた点く)
    private static void HidePortalWindows()
    {
        foreach (PortalWindow w in Windows)
        {
            if (w == null) continue;
            if (w.Cam && w.Cam.enabled) w.Cam.enabled = false;
            if (w.Renderer && w.Renderer.enabled) w.Renderer.enabled = false;
        }
    }

    private static void ReleasePortalWindows()
    {
        ReleaseWindow(0);
        ReleaseWindow(1);
    }
}
