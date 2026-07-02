using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AmongUs.Data;
using Assets.InnerNet;
using HarmonyLib;
using UnityEngine.Networking;
using static EndKnot.Translator;

namespace EndKnot;

// ReSharper disable once ClassNeverInstantiated.Global
public class ModNews
{
    // ReSharper disable UnassignedField.Global
    // ReSharper disable UnusedAutoPropertyAccessor.Global
    public int Number { get; set; }
    public string Date { get; set; }
    public string Title { get; set; }
    public string SubTitle { get; set; }
    public string ShortTitle { get; set; }

    public string Text { get; set; }
    // ReSharper restore UnassignedField.Global
    // ReSharper restore UnusedAutoPropertyAccessor.Global

    public Announcement ToAnnouncement()
    {
        return new()
        {
            Number = Number,
            Title = Title,
            SubTitle = SubTitle,
            ShortTitle = ShortTitle,
            Text = Text,
            Language = (uint)DataManager.Settings.Language.CurrentLanguage,
            Date = Date,
            Id = "ModNews"
        };
    }

    public static List<ModNews> FromJson(string json)
    {
        return JsonSerializer.Deserialize<List<ModNews>>(json);
    }
}

public static class ModNewsFetcher
{
    private const string NewsUrl = "https://app.gurge44.eu/modnews";

    public static IEnumerator FetchNews()
    {
        // Mod news fetch disabled
        yield break;
#pragma warning disable CS0162
        if (OperatingSystem.IsAndroid()) yield break;

        UnityWebRequest request = UnityWebRequest.Get(NewsUrl);
        request.SetRequestHeader("User-Agent", $"{Main.ModName} v{Main.PluginVersion}");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Logger.Error("Failed to fetch mod news: " + request.error, "ModNewsFetcher");
            yield break;
        }

        try
        {
            List<ModNews> newsList = ModNews.FromJson(request.downloadHandler.text);
            ModNewsHistory.AllModNews = newsList.OrderByDescending(n => DateTime.Parse(n.Date)).ToList();
            Logger.Info($"Successfully fetched {ModNewsHistory.AllModNews.Count} mod news items.", "ModNewsFetcher");
        }
        catch (Exception ex) { Utils.ThrowException(ex); }
#pragma warning restore CS0162
    }
}

[HarmonyPatch]
public static class ModNewsHistory
{
    public static List<ModNews> AllModNews = [];

    public static bool Prepare()
    {
        return !OperatingSystem.IsAndroid();
    }

    [HarmonyPatch(typeof(AnnouncementPopUp), nameof(AnnouncementPopUp.ShowIfNew))]
    [HarmonyPrefix]
    public static bool ShowIfNew_Prefix(AnnouncementPopUp __instance, Action onDismissed)
    {
        if (!ModUpdater.UpdatePopupPending) return true;
        ModUpdater.QueueNewsAfterUpdate(__instance, onDismissed);
        return false;
    }
}