using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AmongUs.Data;
using AmongUs.Data.Player;
using Assets.InnerNet;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
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

    // 公式鯖では desync 役職セットアップが Hacking 判定でホスト切断されるため、End K not は実質遊べない。
    // 入室/開始ポップアップ ([[OfficialServerNotice]]) を読まないホスト向けに、メインメニューの
    // 公式ニュースパネルへ「公式鯖では遊べない」警告を地震速報風の色で常設する (完全ローカル / 通信なし)。
    // 高い Number = 未読扱いなので vanilla 自身の ShowIfNew がパネルを安全な経路で開く。
    // (こちらから AnnouncementPopUp.Show() を直叩きすると内部リスト未構築で index out of range クラッシュするため呼ばない)
    private static bool _enabled;

    public static bool Prepare()
    {
        return !OperatingSystem.IsAndroid();
    }

    // 起動時 (Main.cs) に呼ぶ。実際の警告文は SetModAnnouncements でパネル表示時に組み立てる。
    // (起動直後は言語が未確定で英語になってしまうため、ここでは有効化フラグだけ立てる)
    public static void LoadLocalNotice()
    {
        if (OperatingSystem.IsAndroid()) return;
        _enabled = true;
    }

    // 色は DecomposeAnnouncementText を通らない Title / SubTitle / ShortTitle にだけ付ける (赤+黄)。
    private static ModNews BuildOfficialServerNotice()
    {
        const string red = "#FF1A1A";    // 血赤
        const string yellow = "#FFD800"; // 行動指示

        string listTitle = GetString("ModNews.OfficialServer.ListTitle");
        string title = GetString("ModNews.OfficialServer.Title");
        string subTitle = GetString("ModNews.OfficialServer.SubTitle");
        string body = GetString("ModNews.OfficialServer.Body");
        string action = GetString("ModNews.OfficialServer.Action");

        // 本文 (Text) は完全プレーン必須。クリック時に AU の SelectableHyperLinkHelper.DecomposeAnnouncementText が
        // 「描画済み TMP (タグ除去後) と生テキストの位置」をマッピングして substring するため、リッチタグを多用すると
        // 「生テキスト長 > パース後長」のズレで負長 substring → クリックでクラッシュする (実機ログで確認)。
        // <link> も入れると ExtractUrl が別経路で負長 substring。だから本文はタグ/リンク/特殊記号なしのプレーンにする。
        string text = $"{body}\n\n{action}";

        return new ModNews
        {
            // Number は一覧の識別子。0 だと AU に弾かれて一覧から消えるので大きめの固定値にする。
            // Date は遠い未来にして日付降順ソートの最上位 (= 一覧の先頭) に置く。
            // (以前ここを最新にすると落ちると考えたが、真因は force-open の Show() 直叩きだった。最新でも安全)
            Number = 100777,
            Date = "2099-12-31T00:00:00Z",
            ShortTitle = $"<color={red}><b>⚠ {listTitle}</b></color>",
            // Title は `<color>+<b>` のみにする。`<mark>` で入れ子にすると AU の
            // UpdateAnnouncementText がクリック時に "Length cannot be less than zero" で落ちる (実機ログで確認)。
            Title = $"<color={red}><b>⚠⚠ {title} ⚠⚠</b></color>",
            SubTitle = $"<color={yellow}><b>{subTitle}</b></color>",
            Text = text
        };
    }

    [HarmonyPatch(typeof(PlayerAnnouncementData), nameof(PlayerAnnouncementData.SetAnnouncements))]
    [HarmonyPrefix]
    public static void SetModAnnouncements(ref Il2CppReferenceArray<Announcement> aRange)
    {
        if (!_enabled || aRange == null) return;

        try
        {
            // パネル表示時 (= 言語ロード後) に組み立てる。Language も現在の言語で入るので一覧に出る。
            Announcement notice = BuildOfficialServerNotice().ToAnnouncement();

            List<Announcement> finalAllNews = [notice];
            finalAllNews.AddRange(aRange.Where(news => news.Number != notice.Number));
            finalAllNews.Sort((a1, a2) => DateTime.Compare(DateTime.Parse(a2.Date), DateTime.Parse(a1.Date)));

            aRange = new Il2CppReferenceArray<Announcement>(finalAllNews.Count);
            for (var i = 0; i < finalAllNews.Count; i++)
                aRange[i] = finalAllNews[i];
        }
        catch (Exception ex) { Utils.ThrowException(ex); }
    }
}