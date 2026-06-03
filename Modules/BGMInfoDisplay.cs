using System;
using System.Collections;
using TMPro;
using UnityEngine;

namespace EndKnot.Modules;

public static class BGMInfoDisplay
{
    private const float FadeInDuration  = 0.6f;
    private const float HoldDuration    = 4.0f;
    private const float FadeOutDuration = 1.2f;

    private static TextMeshPro displayText;
    private static Coroutine activeFade;

    public static bool HasDisplay => displayText != null && displayText.gameObject != null;

    /// <summary>
    /// BGMクレジットを表示する。
    /// title/author がどちらも空の場合は fileNameFallback の " -" 分割を試みる。
    /// それでも空なら表示しない。
    /// </summary>
    public static void Show(string title, string author, string fileNameFallback = "")
    {
        try
        {
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(author))
            {
                if (!string.IsNullOrEmpty(fileNameFallback))
                {
                    int sep = fileNameFallback.LastIndexOf(" -", StringComparison.Ordinal);
                    if (sep > 0 && sep < fileNameFallback.Length - 2)
                    {
                        title  = fileNameFallback[..sep].Trim();
                        author = fileNameFallback[(sep + 2)..].Trim();
                    }
                }
                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(author)) return;
            }

            EnsureDisplay();
            if (displayText == null) return;

            displayText.text = string.IsNullOrEmpty(author)
                ? $"♪ {title}"
                : $"♪ {title} <color=#aaaaaa>-{author}</color>";
            displayText.ForceMeshUpdate();

            if (activeFade != null) Main.Instance.StopCoroutine(activeFade);
            activeFade = Main.Instance.StartCoroutine(FadeRoutine());
        }
        catch (Exception ex) { Utils.ThrowException(ex); }
    }

    public static void Hide()
    {
        if (displayText == null) return;
        if (activeFade != null) Main.Instance.StopCoroutine(activeFade);
        displayText.gameObject.SetActive(false);
    }

    private static void EnsureDisplay()
    {
        if (displayText != null && displayText.gameObject != null) return;
        displayText = null;

        TextMeshPro template = null;

        // Prefer a HUD-side template when in-game (cooldown text font). In menu/lobby
        // pre-HUD, fall back to a button label TMP. The choice of template only
        // affects font/material — we override every transform/anchor below.
        if (HudManager.InstanceExists && HudManager.Instance.KillButton != null)
            template = HudManager.Instance.KillButton.cooldownTimerText;
        else
        {
            MainMenuManager menu = UnityEngine.Object.FindObjectOfType<MainMenuManager>();
            PassiveButton btnSrc = menu != null ? (menu.quitButton ?? menu.playButton) : null;
            if (btnSrc != null)
            {
                Transform tmpTf = btnSrc.transform.Find("FontPlacer/Text_TMP");
                if (tmpTf != null) template = tmpTf.GetComponent<TextMeshPro>();
            }

            template ??= UnityEngine.Object.FindObjectOfType<TextMeshPro>();
        }

        if (template == null)
        {
            Logger.Warn("Credit display setup aborted: no template found", "BGMInfoDisplay");
            return;
        }

        displayText = UnityEngine.Object.Instantiate(template);

        // Cloned TMP inherits the template's mesh; without clearing first,
        // the next SetActive(true) flashes the original button label
        // (e.g. "終了") for 1+ frame before our text rebuilds.
        displayText.gameObject.SetActive(false);
        displayText.text = string.Empty;

        displayText.gameObject.name = "BGMInfoDisplay";
        displayText.DestroyTranslator(); // remove vanilla TextTranslator that would re-localize our title

        // Detach from any layout-controlled parent and strip the inherited
        // AspectPosition so our own anchor (added below) is the only one steering
        // position. Without this, the cloned KillButton timer keeps pinning the
        // text to the bottom-right kill button slot in lobby/in-game.
        displayText.transform.SetParent(null, false);
        var inheritedAp = displayText.GetComponent<AspectPosition>();
        if (inheritedAp != null) UnityEngine.Object.Destroy(inheritedAp);

        displayText.alignment = TextAlignmentOptions.CaplineRight;
        displayText.fontStyle = FontStyles.Normal;
        displayText.fontSize = displayText.fontSizeMax = displayText.fontSizeMin = 3f;
        displayText.color = Color.white;
        displayText.transform.localScale = Vector3.one;

        var rt = displayText.GetComponent<RectTransform>();
        if (rt != null)
        {
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(6f, 0.8f);
            var anchor = new Vector2(0.5f, 0.5f);
            rt.anchorMax = anchor;
            rt.anchorMin = anchor;
        }

        displayText.overflowMode = TextOverflowModes.Overflow;
        displayText.enableWordWrapping = false;
        displayText.sortingOrder = 100;
        displayText.gameObject.SetActive(false);
        displayText.ForceMeshUpdate();

        // Anchor to the camera's top-right edge so the credit stays in the same
        // visual spot whether we're in MainMenu (fixed cam), lobby (cam follows
        // player), or in-game (cam follows player + meeting).
        AnchorToCamera();
        Logger.Info($"Credit display created, pos={displayText.transform.position}", "BGMInfoDisplay");
    }

    private static void AnchorToCamera()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        Vector3 offset = new(0.4f, 0.9f, cam.nearClipPlane + 0.1f);
        displayText.transform.position = AspectPosition.ComputeWorldPosition(cam, AspectPosition.EdgeAlignments.RightTop, offset);
    }

    private static IEnumerator FadeRoutine()
    {
        displayText.gameObject.SetActive(true);
        displayText.alpha = 0f;

        for (float t = 0f; t < FadeInDuration; t += Time.deltaTime)
        {
            AnchorToCamera();
            displayText.alpha = t / FadeInDuration;
            yield return null;
        }

        displayText.alpha = 1f;
        for (float t = 0f; t < HoldDuration; t += Time.deltaTime)
        {
            AnchorToCamera();
            yield return null;
        }

        for (float t = 0f; t < FadeOutDuration; t += Time.deltaTime)
        {
            AnchorToCamera();
            displayText.alpha = 1f - (t / FadeOutDuration);
            yield return null;
        }

        displayText.alpha = 0f;
        displayText.gameObject.SetActive(false);
        activeFade = null;
    }
}
