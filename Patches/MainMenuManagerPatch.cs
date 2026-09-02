using System;
using BepInEx.Unity.IL2CPP;
using EndKnot.Modules;
using EndKnot.Modules.CalamityMenu;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace EndKnot;

[HarmonyPatch]
public static class MainMenuManagerPatch
{
    public static PassiveButton Template;
    public static PassiveButton UpdateButton;
    private static PassiveButton GitHubButton;
    private static PassiveButton DiscordButton;
    private static PassiveButton WebsiteButton;

    private static bool IsOnline;
    public static bool ShowedBak;
    public static bool ShowingPanel;
    private static bool MenuBGMInitPending;
    private static bool MenuBGMStarted;
    private static float MenuBGMSilenceUntil;
    private static SpriteRenderer MgLogo;
    private static MainMenuManager Instance { get; set; }

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.OpenGameModeMenu))]
    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.OpenAccountMenu))]
    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.OpenCredits))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    public static void ShowRightPanel()
    {
        ShowingPanel = true;
    }

    // ゲームモード選択画面の見た目を整える。器の中身は
    //   RightPanel / MaskedBlackScreen / { GameModeButtons | OnlineButtons | EnterCodeButtons }
    // という並びで、MaskedBlackScreen が全画面共通の下敷き。
    // ここで落とすのは「ローカル/オンラインを選ぶ画面」の飾りだけに限定する — 総当たりで
    // 伏せると、まだ中身の入っていないオンライン画面の入れ物まで巻き込んで空白になる。
    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.OpenGameModeMenu))]
    [HarmonyPostfix]
    public static void HideRightPanelChrome()
    {
        try
        {
            Transform panel = TitleLogoPatch.RightPanel != null ? TitleLogoPatch.RightPanel.transform : null;
            if (panel == null) return;

            // 器の枠。
            var frame = panel.GetComponent<SpriteRenderer>();
            if (frame != null) frame.enabled = false;

            // Calamity メニューでは BACK ボタンが閉じ手段なので、器の左に付くつまみは要らない。
            // バニラのメニューへ退避しているときは唯一の閉じ手段なので残す。
            if (CalamityMenuState.Active)
                panel.Find("CloseRightPanelButton")?.gameObject.SetActive(false);

            // 見出し「プレイ」と、その下の区切り線。
            panel.Find("MaskedBlackScreen/GameModeButtons/Header")?.gameObject.SetActive(false);
            panel.Find("MaskedBlackScreen/GameModeButtons/Divider")?.gameObject.SetActive(false);

            // 暗幕はロビーから戻ると親が MainUI 直下へ戻って参照が古くなるため、毎回名前で取り直す。
            // バニラは設定/アカウント/クレジットの暗転にも使い回すので、Calamity のときだけ伏せる。
            if (CalamityMenuState.Active)
                GameObject.Find("Tint")?.SetActive(false);
        }
        catch (Exception ex) { Logger.Exception(ex, "MainMenuManagerPatch.HideRightPanelChrome"); }
    }

    // 下敷きはゲームモード選択のときだけ隠す。オンライン画面 (ゲーム作成/コード入力/検索) は
    // 文字が多く下敷きが要るので、そちらへ移ったら戻す。画面の出し入れはバニラが握っていて
    // 通知も無いので、毎フレーム現在の画面を見て決める。
    private static float BackdropAlpha = -1f;

    private static void UpdateRightPanelBackdrop()
    {
        Transform panel = TitleLogoPatch.RightPanel != null ? TitleLogoPatch.RightPanel.transform : null;
        if (panel == null) return;

        Transform backdrop = panel.Find("MaskedBlackScreen");
        if (backdrop == null) return;

        var sr = backdrop.GetComponent<SpriteRenderer>();
        if (sr == null) return;

        Color c = sr.color;
        if (BackdropAlpha < 0f) BackdropAlpha = c.a;

        Transform modes = backdrop.Find("GameModeButtons");
        bool onModeScreen = modes != null && modes.gameObject.activeInHierarchy;

        // renderer を切るのではなくアルファだけ 0 にする。描画そのものを止めると子の見え方まで変わる。
        float want = onModeScreen ? 0f : BackdropAlpha;
        if (!Mathf.Approximately(c.a, want)) sr.color = new Color(c.r, c.g, c.b, want);
    }

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Open))]
    [HarmonyPatch(typeof(AnnouncementPopUp), nameof(AnnouncementPopUp.Show))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    public static void HideRightPanel()
    {
        ShowingPanel = false;
        AccountManager.Instance?.transform.FindChild("AccountTab/AccountWindow")?.gameObject.SetActive(false);
    }

    public static void ShowRightPanelImmediately()
    {
        ShowingPanel = true;
        TitleLogoPatch.RightPanel.transform.localPosition = TitleLogoPatch.RightPanelOp;
        Instance.OpenGameModeMenu();
        Instance.playButton.OnClick.AddListener((UnityAction)ShowRightPanelImmediately);
    }

    [HarmonyPatch(typeof(SignInStatusComponent), nameof(SignInStatusComponent.SetOnline))]
    [HarmonyPostfix]
    public static void SetOnline_Postfix()
    {
        LateTask.New(() => { IsOnline = true; }, 0.1f, "Set Online Status");
    }

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    [HarmonyPrefix]
    public static void Start_Prefix(MainMenuManager __instance)
    {
        // 番犬による(再)起動なら前回設定で自動ホスト (Calamity 有無に関わらず走らせたいので Prefix で呼ぶ)。
        AutoRehost.OnMainMenuStart();

        // メニュー到達 = ブートは成功。以降の OnApplicationQuit は「ユーザーの意図的終了」として扱ってよい。
        WatchdogLauncher.MainMenuReached = true;

        if (Main.GcUafBootProbe.Value) LateTask.New(GcUafProbe.RunOnce, 3f, "GcUafProbe.RunOnce");

        LateTask.New(GcUafSelfHeal.RunOnce, 3f, "GcUafSelfHeal.RunOnce");

        // MainMenu シーンの資産が揃った後に巨大テクスチャを縮小・圧縮版へ作り替える (冪等・設定で無効化可)
        LateTask.New(TextureSlimmer.RunOnce, 1.5f, "TextureSlimmer.RunOnce", log: false);

        if (Template == null) Template = __instance.quitButton;

        if (Template == null) return;

        if (UpdateButton == null)
        {
            UpdateButton = CreateButton(
                "updateButton",
                new(4.2f, -1.3f, 1f),
                new(255, 165, 0, byte.MaxValue),
                new(255, 200, 0, byte.MaxValue),
                () => ModUpdater.StartUpdate(ModUpdater.DownloadUrl, true),
                Translator.GetString("updateButton"));

            UpdateButton.transform.localScale = Vector3.one;
        }

        UpdateButton.gameObject.SetActive(ModUpdater.HasUpdate);

        Application.targetFrameRate = Main.UnlockFps.Value ? 120 : 60;

        MenuBGMInitPending = true;
        MenuBGMStarted = false;
        MenuBGMSilenceUntil = Time.realtimeSinceStartup + 2.5f;
    }

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.LateUpdate))]
    [HarmonyPostfix]
    public static void MainMenuManager_LateUpdate(MainMenuManager __instance)
    {
        // RightPanel slide animation runs in both vanilla and Calamity modes —
        // Calamity Multiplayer button needs it to slide RightPanel in.
        if (GameObject.Find("MainUI") == null) ShowingPanel = false;

        UpdateRightPanelBackdrop();

        if (TitleLogoPatch.RightPanel != null)
        {
            Vector3 pos1 = TitleLogoPatch.RightPanel.transform.localPosition;
            Vector3 lerp1 = Vector3.Lerp(pos1, TitleLogoPatch.RightPanelOp + new Vector3(ShowingPanel ? 0f : 10f, 0f, 0f), Time.deltaTime * (ShowingPanel ? 3f : 2f));

            if (ShowingPanel
                    ? TitleLogoPatch.RightPanel.transform.localPosition.x > TitleLogoPatch.RightPanelOp.x + 0.03f
                    : TitleLogoPatch.RightPanel.transform.localPosition.x < TitleLogoPatch.RightPanelOp.x + 9f
                )
                TitleLogoPatch.RightPanel.transform.localPosition = lerp1;
        }

        if (CalamityMenuState.Active) return;

        if (MenuBGMInitPending)
        {
            if (SoundManager.Instance != null)
            {
                BGMManager.SilenceVanillaAudio();

                if (!MenuBGMStarted)
                {
                    BGMManager.InvalidatePlaylist();
                    BGMManager.SetMenuBGM();
                    MenuBGMStarted = true;
                    // OGG 同期デコードで実再生まで 2-4 秒掛かる場合があるため、
                    // 鳴った瞬間から 2.5 秒に張り直して AU の遅延アンビエント再生を潰す。
                    MenuBGMSilenceUntil = Time.realtimeSinceStartup + 2.5f;
                }
            }

            if (Time.realtimeSinceStartup >= MenuBGMSilenceUntil)
                MenuBGMInitPending = false;
        }

        if (ShowedBak || !IsOnline) return;

        GameObject bak = GameObject.Find("BackgroundTexture");
        if (bak == null || !bak.active) return;

        Vector3 pos2 = bak.transform.position;
        Vector3 lerp2 = Vector3.Lerp(pos2, new(pos2.x, 7.1f, pos2.z), Time.deltaTime * 1.4f);
        bak.transform.position = lerp2;
        if (pos2.y > 7f) ShowedBak = true;
    }

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.VeryHigh)]
    public static void Start_Postfix(MainMenuManager __instance)
    {
        if (CalamityMenuState.Active) return;

        Instance = __instance;

        SimpleButton.SetBase(__instance.creditsButton);
        var logoObject = new GameObject("titleLogo_MG");
        Transform logoTransform = logoObject.transform;
        MgLogo = logoObject.AddComponent<SpriteRenderer>();
        logoTransform.localPosition = new(2f, -0.5f, 1f);
        logoTransform.localScale *= 1.2f;
        // TODO: drop a PNG at Resources/Images/EHR-Icon.png (or rename and update this path) once an End K not logo is ready.
        // While the asset is missing, LoadSprite logs one error and returns null — the title screen renders without a logo.
        MgLogo.sprite = Utils.LoadSprite("EndKnot.Resources.Images.EHR-Icon.png", 400f);

        // GitHub Button
        if (GitHubButton == null)
        {
            GitHubButton = CreateButton(
                "GitHubButton",
                new(-2.3f, -1.3f, 1f),
                new(153, 153, 153, byte.MaxValue),
                new(209, 209, 209, byte.MaxValue),
                () => { },
                Translator.GetString("GitHub")); //"GitHub"
        }

        GitHubButton.gameObject.SetActive(true);

        // Discord Button
        if (DiscordButton == null)
        {
            DiscordButton = CreateButton(
                "DiscordButton",
                new(-0.5f, -1.3f, 1f),
                new(88, 101, 242, byte.MaxValue),
                new(148, 161, 255, byte.MaxValue),
                () => { },
                Translator.GetString("Discord")); //"Discord"
        }

        DiscordButton.gameObject.SetActive(true);

        // Website Button
        if (WebsiteButton == null)
        {
            WebsiteButton = CreateButton(
                "WebsiteButton",
                new(1.3f, -1.3f, 1f),
                new(251, 81, 44, byte.MaxValue),
                new(211, 77, 48, byte.MaxValue),
                () => { },
                Translator.GetString("Website")); //"Website"
        }

        WebsiteButton.gameObject.SetActive(true);

        Application.targetFrameRate = Main.UnlockFps.Value ? 120 : 60;

        foreach (string buttonName in new[] { "SettingsButton", "Inventory Button", "CreditsButton", "ExitGameButton" })
        {
            if (buttonName == "Inventory Button" && IL2CPPChainloader.Instance.Plugins.ContainsKey("com.DigiWorm.LevelImposter")) continue;
            var go = GameObject.Find(buttonName);
            if (!go) continue;
            var buttonText = go.GetComponentInChildren<TMP_Text>();
            if (!buttonText) continue;
            buttonText.DestroyTranslator();
            buttonText.text = Translator.GetString($"MainMenu.{buttonName.Replace(" ", "")}");
        }

        __instance.PlayOnlineButton.OnClick.AddListener((UnityAction)(() =>
        {
            GameOptionsManager.Instance.Initialize();

            if (GameOptionsManager.Instance.normalGameHostOptions.MapId == 3 || (GameOptionsManager.Instance.normalGameHostOptions.MapId > 5 && !SubmergedCompatibility.Loaded))
            {
                GameOptionsManager.Instance.normalGameHostOptions.MapId = 0;
                GameOptionsManager.Instance.SaveNormalHostOptions();
            }
        }));

        LateTask.New(() => ModUpdater.ShowAvailableUpdate(), 0.5f, "ShowUpdatePopupVanilla");
    }

    private static PassiveButton CreateButton(string name, Vector3 localPosition, Color32 normalColor, Color32 hoverColor, Action action, string label, Vector2? scale = null)
    {
        PassiveButton button = Object.Instantiate(Template, Template.transform.parent);
        // Cloned TMP inherits the template button's mesh ("終了" / "Quit"); without
        // hiding while we re-configure, the original label flashes for a frame.
        button.gameObject.SetActive(false);
        button.name = name;
        Object.Destroy(button.GetComponent<AspectPosition>());
        button.transform.localPosition = localPosition;

        button.OnClick = new();
        button.OnClick.AddListener(action);

        var buttonText = button.transform.Find("FontPlacer/Text_TMP").GetComponent<TMP_Text>();
        buttonText.DestroyTranslator();
        buttonText.fontSize = buttonText.fontSizeMax = buttonText.fontSizeMin = 3.5f;
        buttonText.enableWordWrapping = false;
        buttonText.text = label;
        var normalSprite = button.inactiveSprites.GetComponent<SpriteRenderer>();
        var hoverSprite = button.activeSprites.GetComponent<SpriteRenderer>();
        normalSprite.color = normalColor;
        hoverSprite.color = hoverColor;

        Transform container = buttonText.transform.parent;
        Object.Destroy(container.GetComponent<AspectPosition>());
        Object.Destroy(buttonText.GetComponent<AspectPosition>());
        container.SetLocalX(0f);
        buttonText.transform.SetLocalX(0f);
        buttonText.horizontalAlignment = HorizontalAlignmentOptions.Center;

        var buttonCollider = button.GetComponent<BoxCollider2D>();
        if (scale.HasValue) normalSprite.size = hoverSprite.size = buttonCollider.size = scale.Value;

        buttonCollider.offset = new(0f, 0f);

        button.gameObject.SetActive(true);
        buttonText.ForceMeshUpdate();
        return button;
    }
}