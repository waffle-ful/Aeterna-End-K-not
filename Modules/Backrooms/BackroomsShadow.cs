using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Modules;

// ============================================================================
// Backrooms バニラ GPU 影ドライバ (Phase 1)
//
// AU 本来の影パイプライン (LightSource → LightSourceGpuRenderer → ShadowCamera →
// HudManager.ShadowQuad) をロビーで手動駆動する。experiment/backrooms-vanilla-shadow の
// /bbrenderer 実証コードを clean port したもの。実機で確定済みの事実:
//   ・ロビーで ShipStatus 不要。ls.renderer は素のロビーで既に非null (GPU)。
//   ・バニラ LightSource.Update はロビーで gate され renderer を駆動しない → 手動 Render が要る。
//   ・ShadowCamera を毎フレ Camera.main に追従させないと全画面が真っ黒になる。
//   ・影 caster = layer 10 (Shadow) の Collider2D。光半径内を Physics2D で拾って遮蔽メッシュ化。
//   ・per-cell 塗り BoxCollider2D は blocky な真っ黒影 / EdgeCollider2D 輪郭線は滑らか
//     (LevelImposter と同型) ← これを SpawnTestRoom で実機 A/B 検証する。
// ============================================================================
public static class BackroomsShadow
{
    private static LightSourceRenderer _ownRenderer;  // ls.renderer が null の時だけ自前生成 (Dispose 用)
    private static bool _driveActive;                 // 毎フレ手動 Render driver の ON/OFF
    private static bool _loggedThrow;                 // driver throw の log-once フラグ
    private static ShadowCamera _shadowCam;           // Camera.main に追従させる ShadowCamera (キャッシュ)
    private static Camera _shadowCamCam;              // 同 GO の Camera component (ortho 同期用)
    private static float _diagTimer;                  // 毎秒位置診断の throttle

    private static float _darkLevel = -1f;            // >=0 で ShadowQuad._Color を (v,v,v,1) に毎フレ上書き
    private static float _edgeBlur = -1f;             // >=0 で LightCutaway._EdgeBlur を毎フレ上書き

    private static readonly List<GameObject> _testCasters = []; // /bbtestroom で spawn する検証ジオメトリ

    private const string Tag = "BBShadow";

    // ---- ドライバ起動 / 停止 ------------------------------------------------

    // 冪等。ls.renderer (素のロビーで既に非null) を使う。null の時だけ自前 renderer を生成。
    // ls.renderer に自前 renderer を *注入しない* (注入するとバニラ Update が NRE flood)。
    public static void Arm(float radius)
    {
        PlayerControl lp = PlayerControl.LocalPlayer;
        if (lp == null) return;

        LightSource ls = lp.lightSource;
        if (ls == null) { Logger.Warn("Arm: lightSource null (AdjustLighting 未呼び出し)", Tag); return; }

        if (ls.renderer == null && _ownRenderer == null)
        {
            try
            {
                LightSourceRenderer r = LightSourceRenderer.Create(LightSourceRenderer.GetPlatformType(), ls);
                r.Initialize();
                _ownRenderer = r;
            }
            catch (Exception ex) { Logger.Warn($"Arm: renderer 生成失敗 {ex}", Tag); }
        }

        try { ls.SetViewDistance(radius); }
        catch (Exception ex) { Logger.Warn($"Arm: SetViewDistance 失敗 {ex.Message}", Tag); }

        _driveActive = true;
        _loggedThrow = false;
        _diagTimer = 0f;
        Logger.Info($"Armed radius={radius} renderer={(_ownRenderer != null ? "own:" + _ownRenderer.GetType().Name : "ls.renderer")}", Tag);
    }

    // 毎フレ: ShadowCamera を Camera.main に追従 + renderer.Render(足元)。RunPerFrameUpdates から駆動。
    public static void Drive()
    {
        if (!_driveActive) return;

        try
        {
            PlayerControl lp = PlayerControl.LocalPlayer;
            if (lp == null) return;

            LightSource ls = lp.lightSource;
            if (ls == null) return;

            LightSourceRenderer r = _ownRenderer != null ? _ownRenderer : ls.renderer;
            if (r == null) return;

            // ShadowCamera framing: ShadowQuad が overlay する視点 (メインカメラ) に追従。
            //   追従が無いと lit cutaway が RT に映らず全画面が暗転する。
            Camera mainCam = Camera.main;
            if (_shadowCam == null) _shadowCam = Object.FindObjectOfType<ShadowCamera>(true);
            if (_shadowCam != null && mainCam != null)
            {
                if (_shadowCamCam == null) _shadowCamCam = _shadowCam.GetComponent<Camera>();
                Vector3 mc = mainCam.transform.position;
                Vector3 sc = _shadowCam.transform.position;
                _shadowCam.transform.position = new Vector3(mc.x, mc.y, sc.z); // z は ShadowCamera 固有を維持
                if (_shadowCamCam != null) _shadowCamCam.orthographicSize = mainCam.orthographicSize;
            }

            // light origin は足元 (no-clip 127u trap 回避)。framing とは別アンカー。
            r.Render(LocalPlayerFeet());

            ApplyDarkOverride();

            // 毎秒位置診断 + caster query の hits 本数 (EdgeCollider2D が拾われているかの確証)
            _diagTimer += Time.unscaledDeltaTime;
            if (_diagTimer >= 1f)
            {
                _diagTimer = 0f;
                Vector2 feet = LocalPlayerFeet();
                string hitsInfo = "hits=?";
                try
                {
                    LightSourceGpuRenderer gpu = r.TryCast<LightSourceGpuRenderer>();
                    if (gpu != null) hitsInfo = gpu.hits != null ? $"hits={gpu.hits.Length}" : "hits=null(未Render)";
                    else hitsInfo = "hits=NA(非GPU型)";
                }
                catch (Exception ex) { hitsInfo = $"hits EXC {ex.Message}"; }

                // ★診断: Physics2D が occluder を検出できているか (plain ロビー vs Backrooms 比較用)
                int omMask = -1, om10 = -1;
                try { Il2CppReferenceArray<Collider2D> f = Physics2D.OverlapCircleAll(feet, ls.ViewDistance, Constants.ShadowMask); omMask = f != null ? f.Length : -2; } catch { omMask = -3; }
                try { Il2CppReferenceArray<Collider2D> f10 = Physics2D.OverlapCircleAll(feet, ls.ViewDistance, 1 << BackroomsConfig.ShadowCasterLayer); om10 = f10 != null ? f10.Length : -2; } catch { om10 = -3; }

                // ★診断: LightChild (光の円メッシュ) の layer/active/scale — layer!=10 なら ShadowCamera が撮れず影が出ない真因
                string lcInfo = "LightChild=NULL";
                try
                {
                    GameObject lc = ls.LightChild;
                    if (lc != null) lcInfo = $"LightChild layer={lc.layer} active={lc.activeInHierarchy} scale={lc.transform.lossyScale}";
                }
                catch (Exception ex) { lcInfo = $"LightChild EXC {ex.Message}"; }

                Logger.Info(
                    $"[driver] feet=({feet.x:F2},{feet.y:F2}) ortho={(mainCam != null ? mainCam.orthographicSize : 0):F1} viewDist={ls.ViewDistance:F1} | {hitsInfo} | OverlapShadowMask={omMask} OverlapLayer10={om10} | {lcInfo}",
                    Tag);
            }
        }
        catch (Exception ex)
        {
            if (!_loggedThrow) { _loggedThrow = true; Logger.Warn($"[driver] throw: {ex}", Tag); }
        }
    }

    // driver 停止 + 自前 renderer Dispose + テストジオメトリ破棄。ls.renderer には触らない (注入していない)。
    public static void Disarm()
    {
        _driveActive = false;
        _shadowCam = null;
        _shadowCamCam = null;
        _diagTimer = 0f;
        _darkLevel = -1f;
        _edgeBlur = -1f;
        ClearTestRoom();
        RestoreShadowQuadColor(); // dark override の残り tint を消し、off 後のロビーを通常に戻す

        if (_ownRenderer != null)
        {
            try { _ownRenderer.Dispose(); }
            catch (Exception ex) { Logger.Warn($"Disarm: Dispose 失敗 {ex.Message}", Tag); }
            _ownRenderer = null;
        }

        Logger.Info("Disarmed", Tag);
    }

    // ShadowQuad._Color をバニラ既定 (0.275) に戻す。dark override 後の off で frozen tint を残さないため。
    private static void RestoreShadowQuadColor()
    {
        try
        {
            if (HudManager.InstanceExists && HudManager.Instance.ShadowQuad != null)
            {
                Material sq = HudManager.Instance.ShadowQuad.material;
                if (sq != null && sq.HasProperty("_Color"))
                    sq.SetColor("_Color", new Color(BackroomsConfig.DefaultDarkLevel, BackroomsConfig.DefaultDarkLevel, BackroomsConfig.DefaultDarkLevel, 1f));
            }
        }
        catch { /* HudManager/material 未準備 — 無視 */ }
    }

    // scene teardown (ゲーム突入 / lobby reload / 退室) 用。全例外を握り潰し、unload 後の ls.renderer には触らない。
    public static void Reset()
    {
        _driveActive = false;
        _loggedThrow = false;
        _shadowCam = null;
        _shadowCamCam = null;
        _diagTimer = 0f;
        _darkLevel = -1f;
        _edgeBlur = -1f;

        if (_ownRenderer != null)
        {
            try { _ownRenderer.Dispose(); } catch { /* scene teardown — 無視 */ }
            _ownRenderer = null;
        }

        ClearTestRoom();
    }

    // ---- 暗さ調整 (Q2) ------------------------------------------------------

    // level<0 で解除、edgeBlur<0 で据え置き。driver から毎フレ ApplyDarkOverride で再適用される。
    public static void SetDark(float level, float edgeBlur)
    {
        _darkLevel = level;
        _edgeBlur = edgeBlur;
        ApplyDarkOverride();
    }

    private static void ApplyDarkOverride()
    {
        if (_darkLevel < 0f && _edgeBlur < 0f) return;

        try
        {
            if (_darkLevel >= 0f && HudManager.InstanceExists && HudManager.Instance.ShadowQuad != null)
            {
                Material sq = HudManager.Instance.ShadowQuad.material;
                if (sq != null && sq.HasProperty("_Color"))
                    sq.SetColor("_Color", new Color(_darkLevel, _darkLevel, _darkLevel, 1f));
            }

            if (_edgeBlur >= 0f)
            {
                LightSource ls = PlayerControl.LocalPlayer != null ? PlayerControl.LocalPlayer.lightSource : null;
                Material lc = ls != null ? ls.LightCutawayMaterial : null;
                if (lc != null && lc.HasProperty("_EdgeBlur")) lc.SetFloat("_EdgeBlur", _edgeBlur);
            }
        }
        catch { /* マテリアル未準備 — 無視 */ }
    }

    // ---- 状態ダンプ ---------------------------------------------------------

    public static void Status(byte targetPid)
    {
        PlayerControl lp = PlayerControl.LocalPlayer;
        LightSource ls = lp != null ? lp.lightSource : null;
        LightSourceRenderer r = _ownRenderer != null ? _ownRenderer : ls?.renderer;
        string view = "?";
        try { if (ls != null) view = ls.ViewDistance.ToString("F2"); } catch { }
        Logger.Info($"=== status === driveActive={_driveActive} useVanilla={BackroomsConfig.UseVanillaShadow} renderer={(r != null ? r.GetType().Name : "NULL")} viewDist={view} darkLevel={_darkLevel:F2} testCasters={_testCasters.Count}", Tag);
        Utils.SendMessage($"BBShadow: drive={_driveActive} vanilla={BackroomsConfig.UseVanillaShadow} renderer={(r != null ? r.GetType().Name : "NULL")} view={view}", targetPid);
    }

    // ---- 検証ハーネス (Phase 1 の make-or-break) -----------------------------

    // variant: "edge"=layer10 EdgeCollider2D 閉ループ(滑らか想定) / "box"=layer10 BoxCollider2D(blocky 想定)
    //          / "both"=左 edge + 右 box の A/B / "off"=破棄。
    //
    // 配置の鉄則 (advisor 指摘の罠回避): テスト物は光半径(5)に完全内包させ、かつ ortho≈3 の
    // 画面(±5u 横/±3u 縦)に収める。半径ぴったり/画面外だと影ウェッジが暗部に落ちて「何も出ない=偽陰性」になる。
    // 実証済アンカー = /bbrenderer caster (物体 feet+2u・小・半径3 で影確認済) に寄せ、小さく近くに置く。
    public static void SpawnTestRoom(string variant, byte targetPid)
    {
        if (variant is "off")
        {
            int n = ClearTestRoom();
            Utils.SendMessage($"test room cleared ({n})", targetPid);
            return;
        }

        Vector2 feet = LocalPlayerFeet();

        switch (variant)
        {
            case "box":
                SpawnTestBox(new Vector2(feet.x + 2.2f, feet.y), 1.1f); // すぐ右の小箱 → blocky 影
                break;
            case "both":
                SpawnTestEdgeRoom(new Vector2(feet.x - 2.8f, feet.y), 1.6f); // 左に小部屋
                SpawnTestBox(new Vector2(feet.x + 2.8f, feet.y), 0.9f);      // 右に小箱
                break;
            default: // "edge"
                variant = "edge";
                SpawnTestEdgeRoom(feet, 2.5f); // プレイヤーを部屋の中に置く (本来の使い方・最も診断的)
                break;
        }

        // 半径は画面(±5u)より大きめに取りテスト物を内包 → 影ウェッジを明るい領域に出す。
        // 半径5は ortho3 画面をほぼ照らし「暗部=影ウェッジだけ」が見える状態 (= blocky vs 滑らか の判別に最適)。
        Arm(5f);

        Logger.Info($"SpawnTestRoom({variant}) at feet=({feet.x:F1},{feet.y:F1}) casters={_testCasters.Count}", Tag);
        Utils.SendMessage($"test '{variant}' spawned + driver armed(半径5)。ログ(BBShadow)の [driver] hits=N で caster 検出を確認。除去 /bbtestroom off", targetPid);
    }

    // layer10 の EdgeCollider2D 閉ループ (2*half 角の部屋、左壁にドア開口)。LevelImposter 同型の本物バニラ影 caster。
    private static void SpawnTestEdgeRoom(Vector2 center, float half)
    {
        GameObject room = new("BBTestEdgeRoom");
        room.transform.position = new Vector3(center.x, center.y, 0f);
        room.layer = BackroomsConfig.ShadowCasterLayer;

        // local 座標の折れ線。始点(-half,-half+doorH)→末尾(-half,-half) の間 (左壁下部) はドアとして開ける。
        float doorH = Mathf.Min(1.2f, half);
        Vector2[] pts =
        [
            new(-half, -half + doorH), // ドア上端
            new(-half, half),          // 左壁を上へ
            new(half, half),           // 天井
            new(half, -half),          // 右壁を下へ
            new(-half, -half)          // 床。ここで終端 → 左壁下部がドア開口
        ];

        EdgeCollider2D ec = room.AddComponent<EdgeCollider2D>();
        Il2CppStructArray<Vector2> arr = new(pts.Length);
        for (int i = 0; i < pts.Length; i++) arr[i] = pts[i];
        ec.points = arr;

        // 視覚: 各セグメントを細い壁スプライトで描いて「どこに壁があるか」を見えるようにする (影本体とは別)
        Color wall = new(0.78f, 0.62f, 0.25f, 0.9f);
        for (int i = 0; i < pts.Length - 1; i++) AddSegmentSprite(room, pts[i], pts[i + 1], wall);

        _testCasters.Add(room);
    }

    // layer10 の塗り BoxCollider2D (2*half 角)。旧アプローチの blocky 真っ黒影を再現する対照群。
    private static void SpawnTestBox(Vector2 center, float half)
    {
        GameObject box = new("BBTestBox");
        box.transform.position = new Vector3(center.x, center.y, 0f);
        box.layer = BackroomsConfig.ShadowCasterLayer;

        float size = half * 2f;
        BoxCollider2D bc = box.AddComponent<BoxCollider2D>();
        bc.size = new Vector2(size, size);

        GameObject vis = new("vis");
        vis.transform.SetParent(box.transform, false);
        vis.transform.localScale = new Vector3(size, size, 1f);
        SpriteRenderer sr = vis.AddComponent<SpriteRenderer>();
        sr.sprite = MarkerSprite;
        sr.color = new Color(0.85f, 0.2f, 0.2f, 0.55f);
        sr.sortingOrder = 50;

        _testCasters.Add(box);
    }

    // 2 点間を細いスプライト矩形で結ぶ (壁の見える化)。parent の local 空間で配置。
    private static void AddSegmentSprite(GameObject parent, Vector2 a, Vector2 b, Color color)
    {
        Vector2 mid = (a + b) * 0.5f;
        Vector2 d = b - a;
        float len = d.magnitude;
        if (len < 0.001f) return;
        float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;

        GameObject seg = new("seg");
        seg.transform.SetParent(parent.transform, false);
        seg.transform.localPosition = new Vector3(mid.x, mid.y, 0f);
        seg.transform.localRotation = Quaternion.Euler(0f, 0f, ang);
        seg.transform.localScale = new Vector3(len, 0.3f, 1f);
        SpriteRenderer sr = seg.AddComponent<SpriteRenderer>();
        sr.sprite = MarkerSprite;
        sr.color = color;
        sr.sortingOrder = 50;
    }

    private static int ClearTestRoom()
    {
        int n = 0;
        foreach (GameObject go in _testCasters)
        {
            if (go == null) continue;
            try { Object.Destroy(go); n++; } catch { /* 既に破棄 — 無視 */ }
        }

        _testCasters.Clear();
        return n;
    }

    // ---- ヘルパー -----------------------------------------------------------

    // No-clip 中は GetTruePosition() が 127u 上空を返すため、transform から固定 body offset を引いて足元を直接計算。
    // (BackroomsLobby.LocalPlayerFeet と同じ。Phase 3 で共通化予定)
    private static Vector2 LocalPlayerFeet()
    {
        Vector3 t = PlayerControl.LocalPlayer.transform.position;
        return new Vector2(t.x, t.y - 0.3636f);
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
