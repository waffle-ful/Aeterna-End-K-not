using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules;

// 撮影用の画面掃除 (TestBridge の `cleanshot`)。配信用の重ね表示・名前・ペットを隠し、全部隠すモードでは HUD とロビーの開始パネルも隠す。
// HudManager 自体を止めると Update にぶら下がる演出 (ExplosionFx / MiraStorm) まで止まるので、子だけを隠す。
// カットイン類は HudManager の親 (カメラ) の下に作られるので、隠しても残る。
public static class CleanShot
{
    // 重ね表示 (OnGUI) を隠すかどうか。HUD を残すモードでも立つ
    public static bool On { get; private set; }

    private static bool _hud;
    private static bool _noPlayers;
    private static readonly List<GameObject> Hidden = [];
    private static float _baseOrtho = -1f;
    private static float _zoomOrtho = -1f;

    // 寄り引き・ずらしの補間 (秒数指定時)。始点から終点へ smoothstep で動かす
    private static float _zoomFrom, _zoomTo, _zoomStart, _zoomDur;
    private static bool _zoomReset;
    private static Vector2 _panFrom, _panTo;
    private static float _panStart, _panDur;
    private static bool _panActive;
    private static Vector2 _basePan;

    public static void Set(bool on, bool hud = true)
    {
        if (On && (!on || _hud != hud)) RestoreHidden();
        On = on;
        _hud = on && hud;
        if (on) return;

        SetNoPlayers(false);
        SetZoom(0f);
        SetPan(Vector2.zero, 0f, true);
    }

    // 誰もいない画を撮るために、人の姿を手元の画面だけで消す
    public static void SetNoPlayers(bool hide)
    {
        if (_noPlayers == hide) return;
        _noPlayers = hide;
        if (hide) return;

        foreach (PlayerControl pc in Main.AllPlayerControlsToArray)
            if (pc) pc.Visible = true;
    }

    // 本編のズームは引く方向 (3 以上) だけなので、寄りはここでメインカメラの orthographicSize を直接変える。0 以下で元に戻す
    public static void SetZoom(float ortho, float seconds = 0f)
    {
        Camera cam = Camera.main;
        if (!cam) return;

        _zoomDur = 0f;
        if (seconds > 0f)
        {
            if (_baseOrtho <= 0f) _baseOrtho = cam.orthographicSize;
            _zoomFrom = _zoomOrtho > 0f ? _zoomOrtho : cam.orthographicSize;
            _zoomReset = ortho <= 0f;
            _zoomTo = _zoomReset ? _baseOrtho : ortho;
            _zoomStart = Time.time;
            _zoomDur = seconds;
            _zoomOrtho = _zoomFrom;
            return;
        }

        if (ortho <= 0f)
        {
            if (_baseOrtho > 0f) cam.orthographicSize = _baseOrtho;
            _baseOrtho = -1f;
            _zoomOrtho = -1f;
            return;
        }

        if (_baseOrtho <= 0f) _baseOrtho = cam.orthographicSize;
        _zoomOrtho = ortho;
        cam.orthographicSize = ortho;
    }

    // カメラの追従先を人からずらす (単位はマップ座標)。reset で元のずれへ戻す
    public static void SetPan(Vector2 offset, float seconds, bool reset = false)
    {
        if (!HudManager.InstanceExists || !HudManager.Instance.PlayerCam) return;
        FollowerCamera fc = HudManager.Instance.PlayerCam;

        if (!_panActive && !reset) _basePan = fc.Offset;
        Vector2 to = reset ? _basePan : _basePan + offset;
        if (reset && !_panActive && _panDur <= 0f) return;

        if (seconds <= 0f)
        {
            fc.Offset = to;
            _panDur = 0f;
        }
        else
        {
            _panFrom = fc.Offset;
            _panTo = to;
            _panStart = Time.time;
            _panDur = seconds;
        }

        _panActive = !reset;
    }

    private static float Smooth(float start, float dur)
    {
        float t = Mathf.Clamp01((Time.time - start) / dur);
        return t * t * (3f - 2f * t);
    }

    // 隠した後に作り直された物も拾うため、オンの間は毎回なめる
    public static void Tick()
    {
        if (_zoomDur > 0f)
        {
            float k = Smooth(_zoomStart, _zoomDur);
            _zoomOrtho = Mathf.Lerp(_zoomFrom, _zoomTo, k);
            if (k >= 1f)
            {
                _zoomDur = 0f;
                if (_zoomReset) SetZoom(0f);
            }
        }

        if (_panDur > 0f && HudManager.InstanceExists && HudManager.Instance.PlayerCam)
        {
            float k = Smooth(_panStart, _panDur);
            HudManager.Instance.PlayerCam.Offset = Vector2.Lerp(_panFrom, _panTo, k);
            if (k >= 1f) _panDur = 0f;
        }

        // 本編のズーム (生存中は寄れない) が 3 に戻すので、指定中は当て直す
        if (_zoomOrtho > 0f)
        {
            Camera cam = Camera.main;
            if (cam && !Mathf.Approximately(cam.orthographicSize, _zoomOrtho)) cam.orthographicSize = _zoomOrtho;
        }

        if (_noPlayers)
        {
            foreach (PlayerControl pc in Main.AllPlayerControlsToArray)
                if (pc && pc.Visible) pc.Visible = false;
        }

        if (!On) return;

        if (_hud)
        {
            if (HudManager.InstanceExists) HideChildren(HudManager.Instance.transform);
            if (GameStartManager.InstanceExists) HideChildren(GameStartManager.Instance.transform);
        }

        foreach (PlayerControl pc in Main.AllPlayerControlsToArray)
        {
            if (!pc || !pc.cosmetics) continue;
            if (pc.cosmetics.nameText) Hide(pc.cosmetics.nameText.gameObject);
            if (pc.cosmetics.colorBlindText) Hide(pc.cosmetics.colorBlindText.gameObject);
            if (pc.cosmetics.CurrentPet) Hide(pc.cosmetics.CurrentPet.gameObject);
        }
    }

    private static void RestoreHidden()
    {
        foreach (GameObject go in Hidden)
            if (go) go.SetActive(true);

        Hidden.Clear();
    }

    // HUD を描くカメラ (UI Camera)・キルの絵・会議画面も HudManager の子にある。カメラを消すと会議もキルの絵も映らず、
    // 会議画面は作られた直後に消すと出てくる動きが止まって戻らないので、この 3 つは残す
    private static void HideChildren(Transform t)
    {
        // MeetingHud.Instance は作られた最初のフレームではまだ空なので、会議画面とカメラは部品の有無で見分ける
        Transform killOverlay = HudManager.InstanceExists && HudManager.Instance.KillOverlay ? HudManager.Instance.KillOverlay.transform : null;

        for (var i = 0; i < t.childCount; i++)
        {
            Transform child = t.GetChild(i);
            if (!child.gameObject.activeSelf) continue;
            if ((killOverlay && killOverlay.IsChildOf(child)) || child.GetComponent<MeetingHud>() || child.GetComponent<Camera>()) continue;
            Hide(child.gameObject);
        }
    }

    private static void Hide(GameObject go)
    {
        if (!go.activeSelf) return;
        go.SetActive(false);
        Hidden.Add(go);
    }
}
