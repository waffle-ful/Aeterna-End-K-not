using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Modules;

// ホスト画面ローカルの「モッドサーバー参加案内」。
// NetObject / RPC は一切使わない。ホスト画面だけに表示 → 配信キャプチャ経由で視聴者に届く設計。
// ロビー左下にトグルボタンを置き、押すと半透明黒カードのパネルが下からスライドアップする（メニュー風）。
public static class ServerInviteOverlay
{
    public enum ServerInviteRegion
    {
        ModdedNA = 0,
        ModdedEU = 1,
        ModdedAS = 2
    }

    private readonly record struct RegionInfo(string DisplayName, string Domain, string DeepLink, string QrResource);

    private static readonly RegionInfo[] Regions =
    [
        new("Modded NA", "aumods.org",
            "amongus://init?servername=Modded_NA&serverport=443&serverip=https%3A%2F%2Faumods.org&usedtls=false",
            "EndKnot.Resources.Images.qr_modded_na.png"),
        new("Modded EU", "au-eu.duikbo.at",
            "amongus://init?servername=Modded_EU&serverport=443&serverip=https%3A%2F%2Fau-eu.duikbo.at&usedtls=false",
            "EndKnot.Resources.Images.qr_modded_eu.png"),
        new("Modded AS", "au-as.duikbo.at",
            "amongus://init?servername=Modded_AS&serverport=443&serverip=https%3A%2F%2Fau-as.duikbo.at&usedtls=false",
            "EndKnot.Resources.Images.qr_modded_as.png")
    ];

    private const string RegionPageUrl = "https://au.niko233.me";

    // ===== tunable (実機で位置・大きさ調整) =====
    private static readonly Vector2 AnchorOffset = new(1.3f, 0.5f);  // 画面左下からのオフセット (ボタン位置)
    private const float QrScale = 0.28f;                              // QR スプライト倍率 (512px@100ppu = 5.12u × scale)
    private const float TextFontSize = 1.5f;
    private const float PanelX = 0f;                                  // パネルの X (ボタン基準)
    private const float PanelShownY = 1.1f;                           // 開いた時のパネル Y (ボタンの上)
    private const float PanelHiddenY = -3.4f;                         // 収納時の Y (下に隠れる)
    private const float SlideDuration = 0.28f;                        // にょきっと所要秒
    private const float QrLocalX = 0.1f;                              // パネル内 QR 位置
    private const float QrLocalY = 2.4f;
    private const float TextLocalY = 0f;
    // 半透明黒の背景カード
    private static readonly Vector2 BgCenter = new(2.2f, 0.05f);      // パネル内のカード中心
    private static readonly Vector2 BgSize = new(6.6f, 7.2f);        // カード寸法 (units)
    private static readonly Color BgColor = new(0f, 0f, 0f, 0.66f);   // 半透明黒
    // ============================================

    private static GameObject _root;        // camera 追従アンカー (ボタン+パネルの親)
    private static SimpleButton _toggle;     // 開閉ボタン
    private static GameObject _panelRoot;    // スライドするパネル本体
    private static SpriteRenderer _bgRenderer; // 半透明黒カード
    private static TextMeshPro _textTmp;
    private static SpriteRenderer _qrRenderer;

    private static bool _isOpen;
    private static float _slideT;            // 0=収納 1=表示
    private static float _nextPollAt;
    private static int _qrRegionIdx = -1;    // 現在ロード中の QR リージョン
    private static Sprite _solidSprite;      // カード用の単色スプライト

    public static string GetServerInfoMessage()
    {
        RegionInfo info = GetCurrentRegionInfo();
        return string.Format(GetString("ServerInvite.Message"),
            info.DisplayName,
            info.Domain,
            RegionPageUrl,
            info.DeepLink);
    }

    private static int CurrentRegionIdx()
    {
        int idx = (int)(ServerInviteRegion)Options.ServerInviteRegion.GetValue();
        return idx < 0 || idx >= Regions.Length ? 0 : idx;
    }

    private static RegionInfo GetCurrentRegionInfo() => Regions[CurrentRegionIdx()];

    // LobbyBehaviour.Update Postfix から毎フレーム
    public static void Tick()
    {
        if (!GameStates.IsLobby || !Options.ServerInviteEnabled.GetBool() || !AmongUsClient.Instance.AmHost)
        {
            HideAll();
            return;
        }

        EnsureUi();
        if (_root == null) return;

        AnchorToCamera();

        // スライド (にょきっと) — フレームレート非依存の MoveTowards
        float target = _isOpen ? 1f : 0f;
        _slideT = Mathf.MoveTowards(_slideT, target, Time.deltaTime / SlideDuration);
        float eased = EaseOutCubic(_slideT);

        if (_panelRoot != null)
        {
            bool visible = _slideT > 0.001f;
            if (_panelRoot.activeSelf != visible) _panelRoot.SetActive(visible);
            _panelRoot.transform.localPosition = new Vector3(PanelX, Mathf.Lerp(PanelHiddenY, PanelShownY, eased), 0f);
        }

        // 開いている間だけ region 切替を反映 (2 秒ポーリング)
        if (_isOpen)
        {
            float now = Time.realtimeSinceStartup;
            if (now >= _nextPollAt)
            {
                _nextPollAt = now + 2f;
                UpdateText();
                RefreshQr();
            }
        }
    }

    private static void HideAll()
    {
        _isOpen = false;
        _slideT = 0f;
        if (_panelRoot != null && _panelRoot.activeSelf) _panelRoot.SetActive(false);
        if (_toggle?.Button != null && _toggle.Button.gameObject.activeSelf) _toggle.Button.gameObject.SetActive(false);
    }

    public static void Destroy()
    {
        if (_root != null) Object.Destroy(_root);
        _root = null;
        _toggle = null;
        _panelRoot = null;
        _bgRenderer = null;
        _textTmp = null;
        _qrRenderer = null;
        _isOpen = false;
        _slideT = 0f;
        _qrRegionIdx = -1;
    }

    private static void EnsureUi()
    {
        if (_root != null)
        {
            if (_toggle?.Button != null && !_toggle.Button.gameObject.activeSelf)
                _toggle.Button.gameObject.SetActive(true);
            return;
        }

        // TMP テンプレート (BGMInfoDisplay / LobbyDecor と同じパターン)
        TextMeshPro template = null;
        if (HudManager.InstanceExists && HudManager.Instance.KillButton != null)
            template = HudManager.Instance.KillButton.cooldownTimerText;
        template ??= Object.FindObjectOfType<TextMeshPro>();

        if (template == null)
        {
            Logger.Warn("ServerInviteOverlay: no TMP template found", "ServerInviteOverlay");
            return;
        }

        _root = new GameObject("ServerInviteRoot");
        _root.transform.SetParent(null, false);

        // ----- トグルボタン (SimpleButton, base は main menu で登録済) -----
        try
        {
            _toggle = new SimpleButton(
                _root.transform,
                "ServerInviteToggle",
                Vector3.zero,
                new Color32(0, 165, 255, byte.MaxValue),
                new Color32(0, 255, 255, byte.MaxValue),
                ToggleOpen,
                GetString("ServerInvite.ButtonClosed"));
            _toggle.Button.transform.localScale = Vector3.one * 0.55f;
        }
        catch (Exception e)
        {
            Logger.Warn($"ServerInviteOverlay: toggle button unavailable ({e.Message})", "ServerInviteOverlay");
        }

        // ----- パネル本体 (スライドする入れ物) -----
        _panelRoot = new GameObject("ServerInvitePanel");
        _panelRoot.transform.SetParent(_root.transform, false);
        _panelRoot.transform.localPosition = new Vector3(PanelX, PanelHiddenY, 0f);

        // 半透明黒カード (一番奥)
        BuildBackground();

        // テキスト
        _textTmp = Object.Instantiate(template);
        _textTmp.gameObject.SetActive(false);
        _textTmp.gameObject.name = "ServerInviteText";
        _textTmp.DestroyTranslator();
        _textTmp.transform.SetParent(_panelRoot.transform, false);

        AspectPosition inheritedAp = _textTmp.GetComponent<AspectPosition>();
        if (inheritedAp != null) Object.Destroy(inheritedAp);

        _textTmp.alignment = TextAlignmentOptions.TopLeft;
        _textTmp.fontStyle = FontStyles.Normal;
        _textTmp.fontSize = _textTmp.fontSizeMax = _textTmp.fontSizeMin = TextFontSize;
        _textTmp.color = Color.white;
        _textTmp.transform.localScale = Vector3.one;
        _textTmp.transform.localPosition = new Vector3(0f, TextLocalY, 0f);
        _textTmp.overflowMode = TextOverflowModes.Overflow;
        _textTmp.enableWordWrapping = false;
        _textTmp.sortingOrder = 101;

        RectTransform rt = _textTmp.GetComponent<RectTransform>();
        if (rt != null)
        {
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(5f, 4f);
            Vector2 anchor = new(0.5f, 0.5f);
            rt.anchorMax = anchor;
            rt.anchorMin = anchor;
        }

        // QR (空 renderer を作り、リージョンに応じて RefreshQr で sprite 差し込み)
        var qrGo = new GameObject("ServerInviteQR");
        qrGo.transform.SetParent(_panelRoot.transform, false);
        qrGo.transform.localPosition = new Vector3(QrLocalX, QrLocalY, 0f);
        qrGo.transform.localScale = Vector3.one * QrScale;
        _qrRenderer = qrGo.AddComponent<SpriteRenderer>();
        _qrRenderer.sortingOrder = 101;
        RefreshQr();

        UpdateText();
        _textTmp.gameObject.SetActive(true);
        _textTmp.ForceMeshUpdate();

        _panelRoot.SetActive(false);
    }

    private static void BuildBackground()
    {
        var bgGo = new GameObject("ServerInviteBg");
        bgGo.transform.SetParent(_panelRoot.transform, false);
        bgGo.transform.localPosition = new Vector3(BgCenter.x, BgCenter.y, 0.01f);
        bgGo.transform.localScale = new Vector3(BgSize.x, BgSize.y, 1f);

        _bgRenderer = bgGo.AddComponent<SpriteRenderer>();
        _bgRenderer.sprite = GetSolidSprite();
        _bgRenderer.color = BgColor;
        _bgRenderer.sortingOrder = 99; // テキスト/QR(101) の奥
    }

    // 単色 1x1 白スプライト (カードの土台。SpriteRenderer.color で着色)
    private static Sprite GetSolidSprite()
    {
        if (_solidSprite != null) return _solidSprite;
        try
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            _solidSprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            _solidSprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
        }
        catch (Exception e) { Logger.Warn($"ServerInviteOverlay: solid sprite failed ({e.Message})", "ServerInviteOverlay"); }

        return _solidSprite;
    }

    private static void ToggleOpen()
    {
        _isOpen = !_isOpen;
        if (_isOpen) _nextPollAt = 0f; // 開いた瞬間に最新へ
        if (_toggle?.Label != null)
            _toggle.Label.text = GetString(_isOpen ? "ServerInvite.ButtonOpen" : "ServerInvite.ButtonClosed");
    }

    // 選択リージョンに応じた直リンク QR をロード (リージョン変更時のみ差し替え)
    private static void RefreshQr()
    {
        if (_qrRenderer == null) return;
        int idx = CurrentRegionIdx();
        if (idx == _qrRegionIdx) return;
        _qrRegionIdx = idx;

        Sprite qrSprite = Utils.LoadSprite(Regions[idx].QrResource, 100f);
        if (qrSprite == null)
        {
            Logger.Warn("ServerInviteOverlay: QR resource not found, text-only mode", "ServerInviteOverlay");
            _qrRenderer.gameObject.SetActive(false);
            return;
        }

        // FilterMode.Point 必須 (bilinear だとモジュール境界ボケでスキャン不可)
        if (qrSprite.texture != null) qrSprite.texture.filterMode = FilterMode.Point;
        _qrRenderer.sprite = qrSprite;
        _qrRenderer.gameObject.SetActive(true);
    }

    private static void UpdateText()
    {
        if (_textTmp == null) return;
        RegionInfo info = GetCurrentRegionInfo();
        _textTmp.text = string.Format(GetString("ServerInvite.PanelText"),
            info.DisplayName,
            info.Domain,
            RegionPageUrl);
        _textTmp.ForceMeshUpdate();
    }

    // camera-relative anchor: 画面左下に root を固定 (カメラはプレイヤー追従なので毎フレーム)
    private static void AnchorToCamera()
    {
        if (_root == null) return;
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 offset = new(AnchorOffset.x, AnchorOffset.y, cam.nearClipPlane + 0.1f);
        _root.transform.position = AspectPosition.ComputeWorldPosition(cam, AspectPosition.EdgeAlignments.LeftBottom, offset);
    }

    private static float EaseOutCubic(float t)
    {
        t = Mathf.Clamp01(t);
        float inv = 1f - t;
        return 1f - (inv * inv * inv);
    }
}

// ロビー離脱時にクリーンアップ
[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.OnDestroy))]
internal static class ServerInviteOverlayDestroyHook
{
    public static void Postfix() => ServerInviteOverlay.Destroy();
}

// ロビー中の Update で Tick (ボタン生成・camera 追従・スライド)
[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Update))]
internal static class ServerInviteOverlayTickHook
{
    public static void Postfix()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        ServerInviteOverlay.Tick();
    }
}
