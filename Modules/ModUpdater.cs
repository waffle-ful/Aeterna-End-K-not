using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using TMPro;
using Twitch;
using UnityEngine;
using UnityEngine.Events;
using static EndKnot.Translator;

namespace EndKnot;

[HarmonyPatch]
public static class ModUpdater
{
    private const string URLGithub = "https://api.github.com/repos/waffle-ful/End-K-not";
    public const bool ForceUpdate = false;
    public static bool HasUpdate;
    public static bool UpdatePopupPending;
    private static bool FirstNotify = true;
    private static bool HasOutdate;
    private static AnnouncementPopUp _queuedNewsInstance;
    private static Action _queuedNewsOnDismissed;
    public static bool IsBroken;
    private static bool IsChecked;
    private static Version LatestVersion;
    private static string LatestTitleModName;
    private static string LatestTitle;
    public static string DownloadUrl;
    private static GenericPopup InfoPopup;
    private static GenericPopup InfoPopupV2;
    private static readonly HttpClient HttpClient = new();

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    [HarmonyPrefix]
    [HarmonyPriority(2)]
    public static void Start_Prefix()
    {
        if (!OperatingSystem.IsAndroid())
        {
            // Version checks are not handled on Android
            NewVersionCheck();
            DeleteOldFiles();
        }
        
        InfoPopup = Object.Instantiate(TwitchManager.Instance.TwitchPopup);
        InfoPopup.name = "InfoPopup";
        InfoPopup.TextAreaTMP.GetComponent<RectTransform>().sizeDelta = new(2.5f, 2f);

        InfoPopupV2 = Object.Instantiate(TwitchManager.Instance.TwitchPopup);
        InfoPopupV2.name = "InfoPopupV2";

        if (!OperatingSystem.IsAndroid() && !IsChecked)
        {
            bool done = CheckReleaseFromGithub(Main.BetaBuildUrl.Value != "").GetAwaiter().GetResult();
            if (HasUpdate) UpdatePopupPending = true;
            Logger.Msg("done: " + done, "CheckRelease");
            Logger.Info("hasupdate: " + HasUpdate, "CheckRelease");
            Logger.Info("forceupdate: " + ForceUpdate, "CheckRelease");
            Logger.Info("downloadUrl: " + DownloadUrl, "CheckRelease");
            Logger.Info("latestVersionl: " + LatestVersion, "CheckRelease");
        }
    }

    public static void ShowAvailableUpdate()
    {
        if (!FirstNotify || !HasUpdate)
        {
            UpdatePopupPending = false;
            return;
        }

        FirstNotify = false;

        if (!string.IsNullOrWhiteSpace(LatestTitleModName))
            ShowPopupWithTwoButtons(
                string.Format(GetString("NewUpdateAvailable"), LatestTitleModName),
                GetString("updateButton"),
                onClickOnFirstButton: () => { StartUpdate(DownloadUrl, true); OnUpdatePopupClosed(false); },
                onClickOnSecondButton: () => OnUpdatePopupClosed(true));
        else
            OnUpdatePopupClosed(true);
    }

    private static void OnUpdatePopupClosed(bool triggerNews)
    {
        UpdatePopupPending = false;
        if (!triggerNews || _queuedNewsInstance == null) return;
        var instance = _queuedNewsInstance;
        var callback = _queuedNewsOnDismissed;
        _queuedNewsInstance = null;
        _queuedNewsOnDismissed = null;
        instance.ShowIfNew(callback);
    }

    public static void QueueNewsAfterUpdate(AnnouncementPopUp instance, Action onDismissed)
    {
        _queuedNewsInstance = instance;
        _queuedNewsOnDismissed = onDismissed;
    }

    public static string Get(string url)
    {
        string result;
        HttpResponseMessage res = HttpClient.GetAsync(url).Result;
        Stream stream = res.Content.ReadAsStreamAsync().Result;

        try
        {
            using StreamReader reader = new(stream);
            result = reader.ReadToEnd();
        }
        finally { stream.Close(); }

        return result;
    }

    public static async Task<bool> CheckReleaseFromGithub(bool beta = false)
    {
        Logger.Msg("Checking GitHub Release", "CheckRelease");
        // End K not's releases are all marked pre-release, and GitHub's /releases/latest
        // endpoint excludes pre-releases (returns 404 when there are only pre-releases).
        // Use the list endpoint and take the most recent entry (sorted by created_at desc).
        const string url = URLGithub + "/releases";

        try
        {
            string result;

            using (HttpClient client = new())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "EndKnot Updater");
                using HttpResponseMessage response = await client.GetAsync(new Uri(url), HttpCompletionOption.ResponseContentRead);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Error($"Response Status Code: {response.StatusCode}", "CheckRelease");
                    return false;
                }

                result = await response.Content.ReadAsStringAsync();
            }

            // This IL2CPP Newtonsoft only surfaces JObject.Parse (not JArray/JToken.Parse),
            // and /releases returns a top-level JSON array, so wrap it in an object first.
            JObject wrapper = JObject.Parse("{\"releases\":" + result + "}");
            JArray releases = wrapper["releases"].CastFast<JArray>();

            if (releases.Count == 0)
            {
                Logger.Error("No releases found", "CheckRelease");
                return false;
            }

            var data = releases[0];

            LatestTitleModName = data["name"].ToString();

            if (beta)
            {
                LatestTitle = data["name"].ToString();
                DownloadUrl = data["url"].ToString();
                HasUpdate = LatestTitle != ThisAssembly.Git.Commit;
            }
            else
            {
                // Strip the 'v' prefix and the '-alpha' suffix; Version.Parse can't handle the suffix
                // (mirrors Main.Version = Version.Parse(PluginVersion.Split('-')[0])).
                LatestVersion = new(data["tag_name"]?.ToString().TrimStart('v').Split('-')[0] ?? string.Empty);
                LatestTitle = $"Ver. {LatestVersion}";
                var assets = data["assets"].CastFast<JArray>();

                for (var i = 0; i < assets.Count; i++)
                {
                    // Match by suffix so we don't depend on the exact version naming in the asset
                    // (actual asset name is "EndKnot-<version>_Steam.zip").
                    if (assets[i]["name"].ToString().EndsWith("_Steam.zip"))
                    {
                        DownloadUrl = assets[i]["browser_download_url"].ToString();
                        break;
                    }
                }

                HasUpdate = LatestVersion.CompareTo(Main.Version) > 0;
                HasOutdate = LatestVersion.CompareTo(Main.Version) < 0;
            }

            Logger.Info("hasupdate: " + HasUpdate, "GitHub");
            Logger.Info("hasoutdate: " + HasOutdate, "GitHub");
            Logger.Info("forceupdate: " + ForceUpdate, "GitHub");
            Logger.Info("downloadUrl: " + DownloadUrl, "GitHub");
            Logger.Info("latestVersionl: " + LatestVersion, "GitHub");
            Logger.Info("latestTitle: " + LatestTitle, "GitHub");

            if (string.IsNullOrWhiteSpace(DownloadUrl))
            {
                Logger.Error("No Download URL", "CheckRelease");
                return false;
            }

            IsChecked = true;
            IsBroken = false;
        }
        catch (Exception ex)
        {
            IsBroken = true;
            Logger.Error($"Error while checking release from GitHub:\n{ex}", "CheckRelease", false);
            return false;
        }

        return true;
    }

    public static void StartUpdate(string url, bool github)
    {
        if (OperatingSystem.IsAndroid()) return;
        ShowPopup(GetString("updatePleaseWait"), StringNames.Cancel, true, false);
        _ = !github ? DownloadDLL(url) : DownloadDLLGithub(url);
    }

    public static bool NewVersionCheck()
    {
        try
        {
            if (Directory.Exists($"{Main.DataPath}/TOH_DATA") && File.Exists($"{Main.DataPath}/EndKnot_DATA/BanWords.txt"))
            {
                DirectoryInfo di = new($"{Main.DataPath}/TOH_DATA");
                di.Delete(true);
                Logger.Warn("Directory deleted: TOH_DATA", "NewVersionCheck");
            }
        }
        catch (Exception ex)
        {
            Logger.Exception(ex, "NewVersionCheck");
            return false;
        }

        return true;
    }

    public static void DeleteOldFiles()
    {
        string path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        const string searchPattern = "EndKnot.dll*";

        if (path != null)
        {
            string[] files = Directory.GetFiles(path, searchPattern);

            try
            {
                foreach (string filePath in files)
                {
                    if (Path.GetFileName(filePath).EndsWith(".bak") || Path.GetFileName(filePath).EndsWith(".temp"))
                    {
                        Logger.Info($"{filePath} will be deleted", "DeleteOldFiles");
                        File.Delete(filePath);
                    }
                }
            }
            catch (Exception e) { Logger.Error($"Failed to clear update residue\n{e}", "DeleteOldFiles"); }
        }
    }

    public static async Task<bool> DownloadDLL(string url)
    {
        try
        {
            const string savePath = "BepInEx/plugins/EndKnot.dll.temp";

            // Delete the temporary file if it exists
            if (File.Exists(savePath)) File.Delete(savePath);

            HttpResponseMessage response = await HttpClient.GetAsync(url);

            if (response is not { IsSuccessStatusCode: true }) throw new($"File retrieval failed with status code: {response.StatusCode}");

            await using (Stream stream = await response.Content.ReadAsStreamAsync())
            await using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                var buffer = new byte[1024];
                int length;

                while ((length = await stream.ReadAsync(buffer)) != 0) await fileStream.WriteAsync(buffer.AsMemory(0, length));
            }

            string fileName = Assembly.GetExecutingAssembly().Location;
            File.Move(fileName, fileName + ".bak");
            File.Move(savePath, fileName);
            ShowPopup(GetString("updateRestart"), StringNames.Close, true);
        }
        catch (Exception ex)
        {
            Logger.Error($"Update failed\n{ex}", "DownloadDLL", false);
            ShowPopup(GetString("updateManually"), StringNames.Close, true);
            return false;
        }

        return true;
    }

    public static async Task<bool> DownloadDLLGithub(string url)
    {
        try
        {
            const string savePath = "BepInEx/plugins/EndKnot.dll.temp";

            // Delete the temporary file if it exists
            if (File.Exists(savePath)) File.Delete(savePath);

            HttpResponseMessage response = await HttpClient.GetAsync(url);

            if (response is not { IsSuccessStatusCode: true }) throw new($"File retrieval failed with status code: {response.StatusCode}");

            await using (Stream stream = await response.Content.ReadAsStreamAsync())
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                // Specify the relative path within the ZIP archive where "EndKnot.dll" is located
                const string entryPath = "BepInEx/plugins/EndKnot.dll";
                ZipArchiveEntry entry = archive.GetEntry(entryPath) ?? throw new($"'{entryPath}' not found in the ZIP archive");

                // Extract "EndKnot.dll" to the temporary file
                await using Stream entryStream = entry.Open();
                await using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
                await entryStream.CopyToAsync(fileStream);
            }

            string fileName = Assembly.GetExecutingAssembly().Location;
            File.Move(fileName, fileName + ".bak");
            File.Move(savePath, fileName);
            ShowPopup(GetString("updateRestart"), StringNames.Close, true);
        }
        catch (Exception ex)
        {
            Logger.Error($"Update failed\n{ex}", "DownloadDLL", false);
            ShowPopup(GetString("updateManually"), StringNames.Close, true);
            return false;
        }

        return true;
    }

    public static void ShowPopup(string message, StringNames buttonText, bool showButton = false, bool buttonIsExit = true)
    {
        if (!InfoPopup) return;

        InfoPopup.Show(message);
        Transform button = InfoPopup.transform.FindChild("ExitGame");

        if (button)
        {
            button.gameObject.SetActive(showButton);
            var textTranslatorTMP = button.GetChild(0).GetComponent<TextTranslatorTMP>();
            textTranslatorTMP.TargetText = buttonText;
            textTranslatorTMP.ResetText();
            var passiveButton = button.GetComponent<PassiveButton>();
            passiveButton.OnClick = new();

            if (buttonIsExit)
                passiveButton.OnClick.AddListener((Action)Application.Quit);
            else
                passiveButton.OnClick.AddListener((Action)(() => InfoPopup.Close()));
        }
    }

    private static void ShowPopupWithTwoButtons(string message, string firstButtonText, string secondButtonText = "", Action onClickOnFirstButton = null, Action onClickOnSecondButton = null)
    {
        if (InfoPopupV2)
        {
            var templateExitGame = InfoPopupV2.transform.FindChild("ExitGame");
            if (!templateExitGame) return;

            var background = InfoPopupV2.transform.FindChild("Background");
            if (!background) return;
            background.localScale *= 2f;

            InfoPopupV2.Show(message);
            templateExitGame.gameObject.SetActive(false);
            var firstButton = Object.Instantiate(templateExitGame, InfoPopupV2.transform);
            var secondButton = Object.Instantiate(templateExitGame, InfoPopupV2.transform);
            
            if (firstButton)
            {
                firstButton.gameObject.SetActive(true);
                firstButton.name = "FirstButton";
                var firstButtonTransform = firstButton.transform;
                firstButton.transform.localPosition = new Vector3(firstButtonTransform.localPosition.x - 1f, firstButtonTransform.localPosition.y - 0.7f, firstButtonTransform.localPosition.z);
                firstButton.transform.localScale *= 1.2f;
                var firstButtonGetChild = firstButton.GetChild(0);
                var textTranslatorTMP = firstButtonGetChild.GetComponent<TextTranslatorTMP>();
                textTranslatorTMP.TargetText = StringNames.Cancel;
                textTranslatorTMP.ResetText();
                textTranslatorTMP.DestroyTranslator();
                firstButtonGetChild.GetComponent<TextMeshPro>().text = firstButtonText;
                firstButtonGetChild.GetComponent<TMP_Text>().text = firstButtonText;
                var passiveButton = firstButton.GetComponent<PassiveButton>();
                passiveButton.OnClick = new();
                if (onClickOnFirstButton != null)
                    passiveButton.OnClick.AddListener((UnityAction)(() => { onClickOnFirstButton(); InfoPopupV2.Close();}));
                else passiveButton.OnClick.AddListener((UnityAction)(() => InfoPopupV2.Close()));
            }
            
            if (secondButton)
            {
                secondButton.gameObject.SetActive(true);
                secondButton.name = "SecondButton";
                var secondButtonTransform = secondButton.transform;
                secondButton.transform.localPosition = new Vector3(secondButtonTransform.localPosition.x + 1f, secondButtonTransform.localPosition.y - 0.7f, secondButtonTransform.localPosition.z);
                secondButton.transform.localScale *= 1.2f;
                var secondButtonGetChild = secondButton.GetChild(0);
                var textTranslatorTMP = secondButtonGetChild.GetComponent<TextTranslatorTMP>();
                textTranslatorTMP.TargetText = StringNames.Cancel;
                textTranslatorTMP.ResetText();
                if (!string.IsNullOrWhiteSpace(secondButtonText))
                {
                    textTranslatorTMP.DestroyTranslator();
                    secondButtonGetChild.GetComponent<TextMeshPro>().text = secondButtonText;
                    secondButtonGetChild.GetComponent<TMP_Text>().text = secondButtonText;
                }

                var passiveButton = secondButton.GetComponent<PassiveButton>();
                passiveButton.OnClick = new();
                if (onClickOnSecondButton != null)
                    passiveButton.OnClick.AddListener((UnityAction)(() => { onClickOnSecondButton(); InfoPopupV2.Close(); }));
                else passiveButton.OnClick.AddListener((UnityAction)(() => InfoPopupV2.Close()));
            }
        }
    }
}
