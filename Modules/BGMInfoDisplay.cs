using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TMPro;
using UnityEngine;

namespace EndKnot.Modules;

public static class BGMInfoDisplay
{
    private const float FadeInDuration = 0.6f;
    private const float HoldDuration = 4.0f;
    private const float FadeOutDuration = 1.2f;

    private static TextMeshPro displayText;
    private static Coroutine activeFade;
    private static Dictionary<string, BGMTitle> titleMap;

    public class BGMTitle
    {
        public string title { get; set; }
        public string author { get; set; }
    }

    public static void Show(string bgmFileName)
    {
        try
        {
            EnsureDisplay();
            if (displayText == null) return;

            (string title, string author) = ResolveTitle(bgmFileName);
            displayText.text = string.IsNullOrEmpty(author)
                ? $"♪ {title}"
                : $"♪ {title} <color=#aaaaaa>-{author}</color>";

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

        Transform parent = null;
        TextMeshPro template = null;
        bool useHudPosition = false;

        if (HudManager.InstanceExists && HudManager.Instance.KillButton != null)
        {
            parent = HudManager.Instance.transform.FindChild("TopRight");
            template = HudManager.Instance.KillButton.cooldownTimerText;
            useHudPosition = true;
            Logger.Info("Using HUD anchor for credit display", "BGMInfoDisplay");
        }
        else
        {
            MainMenuManager menu = UnityEngine.Object.FindObjectOfType<MainMenuManager>();
            if (menu == null)
            {
                Logger.Warn("MainMenuManager not found, cannot display credit", "BGMInfoDisplay");
                return;
            }

            // Use scene root (no parent) to bypass any LayoutGroup that controls button positions
            parent = null;

            PassiveButton btnSrc = menu.quitButton ?? menu.playButton;
            if (btnSrc != null)
            {
                Transform tmpTf = btnSrc.transform.Find("FontPlacer/Text_TMP");
                if (tmpTf != null) template = tmpTf.GetComponent<TextMeshPro>();
            }

            template ??= UnityEngine.Object.FindObjectOfType<TextMeshPro>();
            Logger.Info($"Using scene root for menu credit, template found: {template != null}", "BGMInfoDisplay");
        }

        if (template == null)
        {
            Logger.Warn("Credit display setup aborted: no template found", "BGMInfoDisplay");
            return;
        }

        displayText = parent != null
            ? UnityEngine.Object.Instantiate(template, parent, false)
            : UnityEngine.Object.Instantiate(template);

        displayText.gameObject.name = "BGMInfoDisplay";
        displayText.alignment = useHudPosition ? TextAlignmentOptions.Right : TextAlignmentOptions.Left;
        displayText.fontStyle = FontStyles.Normal;
        float size = useHudPosition ? 1.6f : 2.4f;
        displayText.fontSize = displayText.fontSizeMax = displayText.fontSizeMin = size;
        displayText.color = Color.white;
        displayText.transform.localScale = Vector3.one;

        if (useHudPosition)
            displayText.transform.localPosition = new Vector3(0f, -0.6f, -10f);
        else
            displayText.transform.position = new Vector3(-4.6f, 2.7f, 0f); // world space, top-left

        displayText.overflowMode = TextOverflowModes.Overflow;
        displayText.enableWordWrapping = false;
        displayText.sortingOrder = 100;
        displayText.gameObject.SetActive(false);
        Logger.Info($"Credit display created, useHud={useHudPosition}, pos={displayText.transform.position}", "BGMInfoDisplay");
    }

    private static IEnumerator FadeRoutine()
    {
        displayText.gameObject.SetActive(true);
        displayText.alpha = 0f;

        for (float t = 0f; t < FadeInDuration; t += Time.deltaTime)
        {
            displayText.alpha = t / FadeInDuration;
            yield return null;
        }

        displayText.alpha = 1f;
        yield return new WaitForSeconds(HoldDuration);

        for (float t = 0f; t < FadeOutDuration; t += Time.deltaTime)
        {
            displayText.alpha = 1f - (t / FadeOutDuration);
            yield return null;
        }

        displayText.alpha = 0f;
        displayText.gameObject.SetActive(false);
        activeFade = null;
    }

    private static (string title, string author) ResolveTitle(string bgmFileName)
    {
        EnsureTitleMap();
        if (titleMap.TryGetValue(bgmFileName, out BGMTitle entry))
            return (entry.title ?? bgmFileName, entry.author ?? string.Empty);

        int sepIdx = bgmFileName.LastIndexOf(" -", StringComparison.Ordinal);
        if (sepIdx > 0 && sepIdx < bgmFileName.Length - 2)
            return (bgmFileName[..sepIdx].Trim(), bgmFileName[(sepIdx + 2)..].Trim());

        return (bgmFileName, string.Empty);
    }

    private static void EnsureTitleMap()
    {
        if (titleMap != null) return;
        titleMap = [];

        try
        {
            string path = BGMManager.BGMPath + "bgm_titles.json";

            if (!Directory.Exists(BGMManager.BGMPath))
                Directory.CreateDirectory(BGMManager.BGMPath);

            Dictionary<string, BGMTitle> defaults = GetDefaultTitles();

            if (!File.Exists(path))
            {
                titleMap = defaults;
                WriteTitlesJson(path, titleMap);
                return;
            }

            string json = File.ReadAllText(path);
            titleMap = JsonSerializer.Deserialize<Dictionary<string, BGMTitle>>(json) ?? [];

            // Merge missing default keys into existing JSON so users get new entries on update
            bool changed = false;
            foreach (var kv in defaults)
            {
                if (!titleMap.ContainsKey(kv.Key))
                {
                    titleMap[kv.Key] = kv.Value;
                    changed = true;
                }
            }

            if (changed) WriteTitlesJson(path, titleMap);
        }
        catch (Exception ex)
        {
            Logger.Exception(ex, "BGMInfoDisplay.EnsureTitleMap");
            titleMap = GetDefaultTitles();
        }
    }

    private static Dictionary<string, BGMTitle> GetDefaultTitles() => new()
    {
        ["menu"] = new() { title = "Main Menu", author = "DM Dokuro" },
        ["lobby"] = new() { title = "Lobby", author = "DM Dokuro" },
        ["intask"] = new() { title = "In-Task", author = "DM Dokuro" },
        ["climax"] = new() { title = "stained, brutal calamity", author = "DM Dokuro" },
        ["meeting"] = new() { title = "Meeting", author = "DM Dokuro" },
        ["result"] = new() { title = "Result", author = "DM Dokuro" }
    };

    private static void WriteTitlesJson(string path, Dictionary<string, BGMTitle> map)
    {
        string json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
