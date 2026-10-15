using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

namespace EndKnot.Modules;

public static class OnlinePresetsManager
{
    private static readonly Dictionary<int, StringOption> OptionBehaviourCache = [];
    private static readonly Dictionary<int, CategoryHeaderMasked> HeaderCache = [];
    public static List<PresetMeta> CachedPresets = [];
    public static bool PresetsLoaded = false;

    // UI キャッシュ全破棄 (PurgeUiCache) 用。破棄済み StringOption / ヘッダーへの強参照を残すと
    // IL2CPP ラッパーごと生存ヒープに残り続ける (dead-cache ガードは上書きするだけでキー数を減らさない)。
    public static void ClearUiCache()
    {
        OptionBehaviourCache.Clear();
        HeaderCache.Clear();
    }

    public static void CreatePresetExplorerUI(GameOptionsMenu menu)
    {
        if (!PresetsLoaded) return;

        float y = 2.0f;

        {
            // Fresh/dead-cache guard mirroring GameOptionsMenuPatch:
            // SetHeader/SetUpFromData (native) re-instance masked materials on every call, so they must only
            // run on freshly instantiated objects, and a scene-reload-destroyed cache entry must be rebuilt.
            bool freshH0 = !(HeaderCache.TryGetValue(0, out CategoryHeaderMasked cacheH0) && cacheH0);
            CategoryHeaderMasked header = !freshH0 ? cacheH0 : ModGameOptionsMenu.Track(Object.Instantiate(
                menu.categoryHeaderOrigin,
                Vector3.zero,
                Quaternion.identity,
                menu.settingsContainer
            ));
            HeaderCache[0] = header;

            if (freshH0) header.SetHeader(StringNames.RolesCategory, 20);
            header.Title.DestroyTranslator();
            header.Title.text = Translator.GetString("ExportPresetHeader");
            header.transform.localScale = Vector3.one * 0.63f;
            header.transform.localPosition = new(-0.903f, y, -2f);

            y -= 0.8f;

            bool freshUpload = !(OptionBehaviourCache.TryGetValue(-1, out StringOption uploadCache) && uploadCache);
            StringOption upload = !freshUpload ? uploadCache : ModGameOptionsMenu.Track(Object.Instantiate(menu.stringOptionOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer));
            upload.name = nameof(OnlinePresetsManager) + ";" + Translator.GetString("ExportPreset");
            upload.transform.localPosition = new Vector3(0.952f, y, -2f);
            upload.SetClickMask(menu.ButtonClickMask);
            if (freshUpload) upload.SetUpFromData(null, 20);

            if (freshUpload)
            {
                Object.Destroy(upload.transform.FindChild("Value_TMP (1)").gameObject);
                Object.Destroy(upload.transform.FindChild("ValueBox").gameObject);
                Object.Destroy(upload.PlusBtn.gameObject);

                upload.OnValueChanged = new Action<OptionBehaviour>(menu.ValueChanged);
                upload.MinusBtn.OnClick = new();
                upload.MinusBtn.OnClick.AddListener((UnityAction)(() => ExportCurrentPreset(menu)));
                TextMeshPro text = upload.MinusBtn.GetComponentInChildren<TextMeshPro>();
                text.DestroyTranslator();
                text.text = "↸";
                upload.TitleText.DestroyTranslator();
            }
            upload.gameObject.SetActive(true);

            if (!menu.Children.Contains(upload)) menu.Children.Add(upload);
            OptionBehaviourCache[-1] = upload;

            y -= 0.6f;
        }

        {
            bool freshH1 = !(HeaderCache.TryGetValue(1, out CategoryHeaderMasked cacheH1) && cacheH1);
            CategoryHeaderMasked header = !freshH1 ? cacheH1 : ModGameOptionsMenu.Track(Object.Instantiate(
                menu.categoryHeaderOrigin,
                Vector3.zero,
                Quaternion.identity,
                menu.settingsContainer
            ));
            HeaderCache[1] = header;

            if (freshH1) header.SetHeader(StringNames.RolesCategory, 20);
            header.Title.DestroyTranslator();
            header.Title.text = Translator.GetString("LocalPresetsHeader");
            header.transform.localScale = Vector3.one * 0.63f;
            header.transform.localPosition = new(-0.903f, y, -2f);

            y -= 0.8f;
        }

        for (var index = 0; index < CachedPresets.Count; index++)
        {
            PresetMeta preset = CachedPresets[index];
            int rowIndex = index;
            bool freshRow = !(OptionBehaviourCache.TryGetValue(index, out StringOption cacheRow) && cacheRow);
            StringOption row = !freshRow ? cacheRow : ModGameOptionsMenu.Track(Object.Instantiate(menu.stringOptionOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer));
            row.name = $"{nameof(OnlinePresetsManager)};{string.Format(Translator.GetString("LocalPresetInfo"), preset.name, DateTimeOffset.FromUnixTimeSeconds((long)preset.created_at).ToLocalTime().ToString("yyyy/MM/dd HH:mm"), preset.downloads)}";
            row.transform.localPosition = new Vector3(0.952f, y, -2f);
            row.SetClickMask(menu.ButtonClickMask);
            if (freshRow) row.SetUpFromData(null, 20);

            if (freshRow)
            {
                // 行の組み立ては行単位で隔離する。FindChild が null を返せば .gameObject で NRE、PlusBtn/
                // MinusBtn に TMP が無ければ DestroyTranslator で NRE — どれもループごと中断させ、この index
                // 以降の行が一切作られないまま抜けてしまう。しかも OptionBehaviourCache への登録はループ末尾
                // (:180) なので失敗した index はキャッシュされず、**毎回同じ所で落ち続ける** (= 一覧の途中
                // から先がアプリ再起動まで永久欠落)。さらにループ後の scrollBar 範囲と ControllerSelectable
                // 登録も飛ぶ。設定検索欄と同型の「付随処理の失敗が本処理を道連れにする」
                // 構造なので、壊れた行だけ捨てて次の行へ進む。
                try
                {
                Object.Destroy(row.transform.FindChild("Value_TMP (1)").gameObject);
                Object.Destroy(row.transform.FindChild("ValueBox").gameObject);

                row.OnValueChanged = new Action<OptionBehaviour>(menu.ValueChanged);
                row.LabelBackground.transform.localScale += new Vector3(1f, 0f, 0f);
                row.TitleText.GetComponent<RectTransform>().sizeDelta = new(5.7f, 0.37f);

                TextMeshPro plusText = row.PlusBtn.GetComponentInChildren<TextMeshPro>();
                plusText.DestroyTranslator();
                plusText.text = "ⓘ";
                row.PlusBtn.OnClick = new();
                row.PlusBtn.OnClick.AddListener((UnityAction)(() =>
                {
                    bool b = plusText.text == "ⓘ";
                    GameSettingMenuPatch.SetGameModeListOpen(false);
                    GameSettingMenuPatch.ResetDescriptionPaging(GameSettingMenu.Instance.MenuDescriptionText);
                    // 行は index でキャッシュされ、一覧は開くたびに読み直すので、押した時点の中身を引く
                    if (b && rowIndex < CachedPresets.Count) GameSettingMenu.Instance.MenuDescriptionText.text = CachedPresets[rowIndex].description;
                    plusText.text = b ? "∅" : "ⓘ";
                }));

                row.MinusBtn.transform.localPosition += new Vector3(1.7f, 0f, 0f);
                row.MinusBtn.OnClick = new();
                row.MinusBtn.OnClick.AddListener((UnityAction)(() =>
                {
                    if (rowIndex >= CachedPresets.Count) return;
                    string presetPath = CachedPresets[rowIndex].id;
                    GameSettingMenu.Instance.Close();

                    Prompt.Show(Translator.GetString("Promt.ApplyPreset"), () =>
                    {
                        if (!GameStates.IsLobby || !AmongUsClient.Instance.AmHost) return;
                        Dictionary<int, int> loaded = ReadLocalPreset(presetPath);

                        if (loaded == null)
                        {
                            Logger.SendInGame(Translator.GetString("PresetImportFailed"), Color.red);
                            return;
                        }

                        ApplyPreset(loaded);
                    }, () =>
                    {
                        LateTask.New(() =>
                        {
                            if (!GameStates.IsLobby) return;
                            GameObject.Find("Host Buttons").transform.FindChild("Edit").GetComponent<PassiveButton>().ReceiveClickDown();
                        }, 0.1f);

                        LateTask.New(() =>
                        {
                            if (!GameStates.IsLobby || !GameSettingMenu.Instance) return;
                            const int tabIndex = (int)TabGroup.PresetExplorer + 3;
                            GameSettingMenu.Instance.ChangeTab(tabIndex, Controller.currentTouchType == Controller.TouchType.Joystick);
                            ModGameOptionsMenu.TabIndex = tabIndex;
                        }, 0.4f);
                    });
                }));
                TextMeshPro minusText = row.MinusBtn.GetComponentInChildren<TextMeshPro>();
                minusText.DestroyTranslator();
                minusText.text = "▶";
                row.TitleText.DestroyTranslator();
                }
                catch (Exception e)
                {
                    Logger.Warn($"preset row [{index}] build failed ({e.Message}) — dropping this row and continuing", "OnlinePresets");
                    if (row) Object.Destroy(row.gameObject);
                    continue;
                }
            }
            row.gameObject.SetActive(true);

            if (!menu.Children.Contains(row)) menu.Children.Add(row);
            OptionBehaviourCache[index] = row;

            y -= 0.6f;
        }

        // ファイルが減った時に、前回作った余りの行を隠す
        foreach ((int cachedIndex, StringOption cachedRow) in OptionBehaviourCache)
        {
            if (cachedIndex >= CachedPresets.Count && cachedRow)
            {
                cachedRow.gameObject.SetActive(false);
                TextMeshPro plusText = cachedRow.PlusBtn ? cachedRow.PlusBtn.GetComponentInChildren<TextMeshPro>() : null;
                if (plusText) plusText.text = "ⓘ";
            }
        }

        menu.scrollBar.SetYBoundsMax(-y - 1.65f);

        menu.ControllerSelectable.Clear();

        foreach (UiElement x in menu.scrollBar.GetComponentsInChildren<UiElement>())
            menu.ControllerSelectable.Add(x);
    }

    private const string LocalPresetFormat = "EndKnotPreset";
    private const int LocalPresetVersion = 1;
    private static string LocalPresetDirectory => $"{Main.DataPath}/EndKnot_DATA/Presets/";

    // 書き出しファイルを新しい順に一覧へ読み込む。読めないファイルは一覧に出さない。
    public static void LoadLocalPresets()
    {
        List<PresetMeta> list = [];

        try
        {
            var dir = new DirectoryInfo(LocalPresetDirectory);

            if (dir.Exists)
            {
                foreach (FileInfo file in dir.GetFiles("*.json").OrderByDescending(x => x.LastWriteTimeUtc))
                {
                    Dictionary<int, int> preset = ReadLocalPreset(file.FullName);
                    if (preset == null) continue;

                    list.Add(new PresetMeta
                    {
                        id = file.FullName,
                        // 行タイトルは name の ';' 区切りで取り出すので、ファイル名の ';' と TMP タグを無害化する
                        name = Path.GetFileNameWithoutExtension(file.Name).Replace(';', '；').Replace("<", "<noparse><</noparse>"),
                        description = file.FullName,
                        created_at = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds(),
                        downloads = preset.Count
                    });
                }
            }
        }
        catch (Exception e)
        {
            Logger.Warn($"local preset scan failed: {e.Message}", "OnlinePresets");
        }

        CachedPresets = list;
        PresetsLoaded = true;
    }

    private static Dictionary<int, int> ReadLocalPreset(string path)
    {
        try
        {
            var file = JsonSerializer.Deserialize<LocalPresetFile>(File.ReadAllText(path));
            if (file?.format != LocalPresetFormat || file.version > LocalPresetVersion || file.preset == null) return null;
            return file.preset;
        }
        catch (Exception e)
        {
            Logger.Warn($"local preset read failed ({Path.GetFileName(path)}): {e.Message}", "OnlinePresets");
            return null;
        }
    }

    // 既定値と同じ値も全部書く (後の版で既定値が変わっても、取り込んだ時に書き出した時の値になる)。
    // 取り込み側は書かれていない設定を既定値に戻すので、後から増えた設定があっても読み込める。
    private static void ExportCurrentPreset(GameOptionsMenu menu)
    {
        try
        {
            Dictionary<int, int> preset = OptionItem.AllOptions
                .Where(x => !x.IsSingleValue)
                .ToDictionary(x => x.Id, x => x.CurrentValue);

            Directory.CreateDirectory(LocalPresetDirectory);
            string path = Path.Combine(LocalPresetDirectory, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json");
            var file = new LocalPresetFile { format = LocalPresetFormat, version = LocalPresetVersion, preset = preset };
            File.WriteAllText(path, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));

            Logger.SendInGame(string.Format(Translator.GetString("PresetExported"), Path.GetFileName(path)), Color.green);
        }
        catch (Exception e)
        {
            Logger.Error($"preset export failed: {e}", "OnlinePresets");
            Logger.SendInGame(Translator.GetString("PresetExportFailed"), Color.red);
            return;
        }

        LoadLocalPresets();
        if (menu) CreatePresetExplorerUI(menu);
    }

    private static void ApplyPreset(Dictionary<int, int> preset)
    {
        foreach ((int id, OptionItem optionItem) in OptionItem.FastOptions)
        {
            if (optionItem.IsSingleValue) continue;
            int value = preset.TryGetValue(id, out int v) ? NormalizeValue(optionItem, v) : optionItem.DefaultValue;
            optionItem.SetValue(value, doSave: false, doSync: false);
        }

        OptionItem.SyncAllOptions();
        OptionSaver.Save();

        Logger.SendInGame(Translator.GetString("PresetApplied"), Color.green);
    }

    // 手で書き換えたファイルや別の版のファイルでも、範囲外の値を入れない (各型の SetValue(int, bool) と同じ丸め方)
    private static int NormalizeValue(OptionItem optionItem, int value) => optionItem switch
    {
        BooleanOptionItem => value % 2 == 0 ? 0 : 1,
        IntegerOptionItem x => x.Rule.RepeatIndex(value),
        FloatOptionItem x => x.Rule.RepeatIndex(value),
        StringOptionItem x => x.Rule.RepeatIndex(value),
        _ => value
    };

    private static IEnumerator UploadCurrentPreset()
    {
        // External upload disabled
        yield break;
#pragma warning disable CS0162
        Dictionary<int, int> preset = OptionItem.AllOptions.Where(x => !x.IsSingleValue).ToDictionary(x => x.Id, x => x.CurrentValue);
        
        PresetDraftRequest body = new PresetDraftRequest
        {
            friend_code = PlayerControl.LocalPlayer.Data.FriendCode,
            puid = PlayerControl.LocalPlayer.GetClient().ProductUserId,
            preset = preset
        };

        string json = JsonSerializer.Serialize(body);

        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

        UnityWebRequest request = new UnityWebRequest("https://app.gurge44.eu/presets/draft", "POST")
        {
            uploadHandler = new UploadHandlerRaw(bodyRaw),
            downloadHandler = new DownloadHandlerBuffer()
        };

        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Logger.SendInGame($"Preset upload failed: {request.error}");
            yield break;
        }

        PresetDraftResponse response;

        try
        {
            response = JsonSerializer.Deserialize<PresetDraftResponse>(request.downloadHandler.text);
        }
        catch
        {
            Logger.SendInGame("Preset upload response parse failed");
            yield break;
        }

        if (!string.IsNullOrEmpty(response.error))
        {
            Logger.SendInGame($"Preset upload rejected: {response.error}");
            yield break;
        }

        if (string.IsNullOrEmpty(response.draft_id))
        {
            Logger.SendInGame("Preset upload failed: no draft_id");
            yield break;
        }

        OpenPublishPage(response.draft_id);
#pragma warning restore CS0162
    }

    static void OpenPublishPage(string draftId)
    {
        // External publish page disabled
        return;
#pragma warning disable CS0162
        string url = $"https://app.gurge44.eu/publish?preset={draftId}";

        try
        {
            Process.Start(url);
        }
        catch
        {
            Application.OpenURL(url);
        }
#pragma warning restore CS0162
    }

    private static IEnumerator DownloadPreset(string presetId, Action<Dictionary<int,int>> onSuccess)
    {
        // External preset download disabled
        yield break;
#pragma warning disable CS0162
        UnityWebRequest request = UnityWebRequest.Get($"https://app.gurge44.eu/presets/{presetId}");

        request.timeout = 5;

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Logger.SendInGame($"Preset download failed: {request.error}");
            yield break;
        }

        PresetDownloadResponse response;

        try
        {
            response = JsonSerializer.Deserialize<PresetDownloadResponse>(request.downloadHandler.text);
        }
        catch
        {
            Logger.SendInGame("Preset JSON parse failed");
            yield break;
        }

        if (!string.IsNullOrEmpty(response.error))
        {
            Logger.SendInGame($"Preset rejected: {response.error}");
            yield break;
        }

        if (response.preset == null || string.IsNullOrEmpty(response.hash))
        {
            Logger.SendInGame("Preset invalid response");
            yield break;
        }

        onSuccess?.Invoke(response.preset);
#pragma warning restore CS0162
    }

    public static IEnumerator FetchPresetList(Action<List<PresetMeta>> onSuccess)
    {
        // External preset list fetch disabled
        yield break;
#pragma warning disable CS0162
        UnityWebRequest request = UnityWebRequest.Get("https://app.gurge44.eu/presets/list");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Logger.SendInGame($"Preset list fetch failed: {request.error}");
            yield break;
        }

        PresetListResponse response;

        try
        {
            response = JsonSerializer.Deserialize<PresetListResponse>(request.downloadHandler.text.Trim());
        }
        catch (Exception ex)
        {
            Logger.SendInGame("Preset list parse failed");
            Logger.Info(request.downloadHandler.text.Trim(), "Response");
            Utils.ThrowException(ex);
            yield break;
        }

        if (response?.presets == null)
        {
            Logger.SendInGame("Preset list empty or invalid");
            yield break;
        }

        onSuccess?.Invoke(response.presets.OrderByDescending(x => x.downloads).ToList());
#pragma warning restore CS0162
    }

    // ReSharper disable all
    public class PresetDraftRequest
    {
        public string friend_code { get; set; }
        public string puid { get; set; }
        public Dictionary<int, int> preset { get; set; }
    }

    public class PresetDraftResponse
    {
        public string draft_id { get; set; }
        public string error { get; set; }
    }
    
    public class PresetDownloadResponse
    {
        public Dictionary<int, int> preset { get; set; }
        public string hash { get; set; }
        public string error { get; set; }
    }
    
    public class PresetMeta
    {
        public string id { get; set; }
        public string name { get; set; }
        public string description { get; set; }
        public string author { get; set; }
        public string hash { get; set; }
        public double created_at { get; set; }
        public int downloads { get; set; }
    }

    public class LocalPresetFile
    {
        public string format { get; set; }
        public int version { get; set; }
        public Dictionary<int, int> preset { get; set; }
    }

    public class PresetListResponse
    {
        public List<PresetMeta> presets { get; set; }
    }
}