using EndKnot.Gamemodes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EndKnot;

public static class Translator
{
    private const string LanguageFolderName = "Language";
    // 言語 ID → (キー → 訳文)。キーごとに言語の小辞書を持つと 1.3 万個の辞書で約 3MB になるので、言語ごとに 1 個にまとめる
    private static Dictionary<int, Dictionary<string, string>> TranslateMaps;

    // Init() より前に GetString を引く経路 (起動直後に走る OnGUI オーバーレイなど) があるので、
    // 「翻訳テーブルがまだ無い」を呼び出し側から見えるようにしておく。
    public static bool IsInitialized => TranslateMaps != null;

    // 遅延ロード: 起動時は英語 (+ その時点で決まる実効言語) だけ読み、他の埋込jsoncはDictionary化しない。
    // プラグイン Load 時点では TranslationController が未生成なので、実際のUI言語は最初の GetString 参照時に
    // 1本だけ同期ロードされる (実機 ja_JP 12577 キーで 32ms)。索引はリソース名だけを持つ。
    private static readonly Regex LanguageIdHeaderRegex = new("\"LanguageID\"\\s*:\\s*\"(\\d+)\"", RegexOptions.Compiled);
    private static Dictionary<int, string> LangResourceByLangId = [];
    private static HashSet<int> LoadedLangs = [];
    private static readonly object LangLoadLock = new();

    // EKN 役職コードの実行時名前上書き (例: "EkmCustomRole1" → 定義側の name)。
    // 言語別データは持たず、英語スロットに書くだけで GetString の「その言語で無ければ英語へフォールバック」
    // (下の GetString(string, SupportedLangs) 参照) にそのまま乗る。
    private static readonly Dictionary<string, string> RuntimeOverrides = [];

    public static void SetRuntimeOverride(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;
        RuntimeOverrides[key] = value ?? "";
    }

    public static void ClearRuntimeOverride(string key)
    {
        RuntimeOverrides.Remove(key);
    }
    public static Dictionary<CustomRoles, Dictionary<SupportedLangs, string>> OriginalRoleNames;
    public static readonly StringNames[] AllStringNames = Enum.GetValues<StringNames>();

    public static void Init()
    {
        Logger.Info("Loading Custom Translations...", "Translator");
        LoadLangs();
        Logger.Info("Loaded Custom Translations", "Translator");
    }

    public static void LoadLangs()
    {
        // 再読込 (F5+T) では、セッション中に参照済みだった言語も落とさず読み直す。
        int[] previouslyLoaded = LoadedLangs.ToArray();
        TranslateMaps = [];
        LoadedLangs = [];
        LangResourceByLangId = [];

        try
        {
            // Get the directory containing the JSON files (e.g., EndKnot.Resources.Lang)
            var jsonDirectory = "EndKnot.Resources.Lang";
            // Get the assembly containing the resources
            var assembly = Assembly.GetExecutingAssembly();
            string[] jsonFileNames = GetJsonFileNames(assembly, jsonDirectory);

            if (jsonFileNames.Length == 0)
            {
                Logger.Warn("Json Translation files does not exist.", "Translator");
            }
            else
            {
                // Only the LanguageID is needed up front, so peek the stream head instead of
                // deserializing all 21 embedded jsonc files (this is what used to keep ~30MB
                // of unused language tables resident for the whole session).
                byte[] headerBuffer = new byte[512];
                foreach (string jsonFileName in jsonFileNames)
                {
                    try
                    {
                        using Stream headStream = assembly.GetManifestResourceStream(jsonFileName);
                        if (headStream == null) continue;

                        int read = headStream.Read(headerBuffer, 0, headerBuffer.Length);
                        string headText = Encoding.UTF8.GetString(headerBuffer, 0, read);
                        Match m = LanguageIdHeaderRegex.Match(headText);
                        if (!m.Success)
                        {
                            Logger.Warn($"Invalid JSON format in {jsonFileName}: Missing or invalid 'LanguageID' field near file head.", "Translator");
                            continue;
                        }

                        LangResourceByLangId[int.Parse(m.Groups[1].Value)] = jsonFileName;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error indexing {jsonFileName}: {ex}", "Translator");
                    }
                }
            }
        }
        catch (Exception ex) { Logger.Error($"Error: {ex}", "Translator"); }
        Modules.BootTimeline.Mark("lang.index");

        // Loading custom translation files
        if (!Directory.Exists($"{Main.DataPath}/{LanguageFolderName}")) Directory.CreateDirectory($"{Main.DataPath}/{LanguageFolderName}");

        try { OriginalRoleNames = Main.CustomRoleValues.ToDictionary(x => x, x => new Dictionary<SupportedLangs, string>()); }
        catch (Exception e) { Utils.ThrowException(e); }

        SupportedLangs effectiveLang = SupportedLangs.English;
        try
        {
            int modLanguageId = Options.IsLoaded ? Options.ModLanguage.GetValue() : 0;
            if (modLanguageId != 0)
                effectiveLang = (SupportedLangs)(modLanguageId + 99);
            else if (Main.ForceOwnLanguage.Value)
                effectiveLang = GetUserTrueLang();
            else
                effectiveLang = TranslationController.InstanceExists
                    ? TranslationController.Instance.currentLanguage.languageID
                    : SupportedLangs.English;
        }
        catch { effectiveLang = SupportedLangs.English; }

        LoadLangResource((int)SupportedLangs.English);
        Modules.BootTimeline.Mark("lang.en");
        LoadLangResource((int)effectiveLang);
        Modules.BootTimeline.Mark("lang.eff");
        foreach (int langId in previouslyLoaded) LoadLangResource(langId);

        if (Main.LoadAllLanguages?.Value == true)
        {
            foreach (int langId in LangResourceByLangId.Keys.ToList())
                LoadLangResource(langId);
        }

        // Creating a translation template
        CreateTemplateFile();
        Modules.BootTimeline.Mark("lang.template");

        foreach (SupportedLangs lang in Enum.GetValues<SupportedLangs>())
        {
            if (File.Exists($"{Main.DataPath}/{LanguageFolderName}/{lang}.dat"))
            {
                UpdateCustomTranslation($"{lang}.dat" /*, lang*/);
                if (LoadedLangs.Contains((int)lang))
                    LoadCustomTranslation($"{lang}.dat", lang);
            }
        }

        try { Logger.Info($"Translator: languages loaded=[{string.Join(",", LoadedLangs.OrderBy(x => x))}] deferred={LangResourceByLangId.Count - LoadedLangs.Count}", "Translator"); }
        catch { }
    }

    // 起動時に読まなかった言語を初回参照時に1本だけ読む。TranslateMaps へ merge した後は
    // 通常の GetString(str, SupportedLangs) 経路にそのまま乗る。
    private static void LoadLangResource(int langId)
    {
        lock (LangLoadLock)
        {
            if (LoadedLangs.Contains(langId)) return;

            if (!LangResourceByLangId.TryGetValue(langId, out string resourceName))
            {
                LoadedLangs.Add(langId);
                try { Logger.Warn($"Translator: no language resource indexed for langId={langId}", "Translator"); } catch { }
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using Stream resourceStream = assembly.GetManifestResourceStream(resourceName);
                if (resourceStream == null)
                {
                    LoadedLangs.Add(langId);
                    return;
                }

                // ちょうどの大きさの配列に読んで Utf8JsonReader で直接たどる。Stream から逆シリアル化すると
                // ArrayPool の作業バッファ (ファイル全体が入る大きさ) がスレッドに残り続け、JsonElement も値ごとに文書を作る
                byte[] data = ReadAllBytes(resourceStream);
                int keyCount = MergeJsonIntoTranslationMap(langId, data);
                if (keyCount < 0)
                {
                    Logger.Warn($"Failed to deserialize JSON file: {resourceName}. Is it a vaild jsonc?", "Translator");
                    LoadedLangs.Add(langId);
                    return;
                }

                LoadedLangs.Add(langId);

                SupportedLangs lang = (SupportedLangs)langId;
                if (OriginalRoleNames != null)
                {
                    foreach (CustomRoles role in OriginalRoleNames.Keys)
                        OriginalRoleNames[role][lang] = GetString($"{role}", lang);
                }

                string datFile = $"{lang}.dat";
                if (File.Exists($"{Main.DataPath}/{LanguageFolderName}/{datFile}"))
                    LoadCustomTranslation(datFile, lang);

                try { Logger.Info($"Translator: loaded {resourceName} lang={langId} keys={keyCount} ({sw.ElapsedMilliseconds}ms)", "Translator"); }
                catch { }
            }
            catch (Exception ex)
            {
                try { Logger.Error($"Error parsing {resourceName}: {ex}", "Translator"); } catch { }
                LoadedLangs.Add(langId);
            }
        }
    }

    // 未ロードの言語が要求されたら同期的に1本だけ読み込む。GetString の毎回チェックは
    // HashSet.Contains の1回判定なので通常経路 (既ロード言語) への負担はない。
    public static void EnsureLangLoaded(SupportedLangs langId)
    {
        if (LoadedLangs != null && LoadedLangs.Contains((int)langId)) return;
        LoadLangResource((int)langId);
    }

    // 言語表を足す時は外側の辞書を作り直して差し替える。別スレッドの GetString が IsKnownKey で列挙していても壊れない
    private static void PublishLangMap(int languageId, Dictionary<string, string> langMap)
    {
        TranslateMaps = new Dictionary<int, Dictionary<string, string>>(TranslateMaps) { [languageId] = langMap };
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream.CanSeek)
        {
            var data = new byte[stream.Length - stream.Position];
            int read = 0;
            while (read < data.Length)
            {
                int n = stream.Read(data, read, data.Length - read);
                if (n <= 0) break;
                read += n;
            }

            return read == data.Length ? data : data[..read];
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // jsonc の最上位オブジェクトの文字列値を言語表へ入れ、入れたキー数を返す (読めなければ -1)。
    // 文字列以外の値は警告して飛ばす。LanguageID は索引用なので入れない
    private static int MergeJsonIntoTranslationMap(int languageId, byte[] utf8)
    {
        bool isNew = !TranslateMaps.TryGetValue(languageId, out var langMap);
        if (isNew)
        {
            int capacity = TranslateMaps.TryGetValue((int)SupportedLangs.English, out var english) ? english.Count : 0;
            langMap = new Dictionary<string, string>(capacity);
        }

        ReadOnlySpan<byte> json = utf8;
        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF) json = json[3..]; // Utf8JsonReader は BOM を読み飛ばさない

        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return -1;

        int count = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString();
            if (!reader.Read()) break;

            if (reader.TokenType != JsonTokenType.String)
            {
                Logger.Warn($"Invalid value type for key '{key}' in language ID {languageId}. Expected a string.", "Translator");
                reader.Skip();
                continue;
            }

            if (key == "LanguageID") continue;

            string translation = reader.GetString();
            if (string.IsNullOrEmpty(translation))
                continue;

            langMap[key] = translation.Replace("\\n", "\n").Replace("\\r", "\r");
            count++;
        }

        if (isNew) PublishLangMap(languageId, langMap);
        return count;
    }

    // Function to get a list of JSON file names in a directory
    private static string[] GetJsonFileNames(Assembly assembly, string directoryName)
    {
        string[] resourceNames = assembly.GetManifestResourceNames();
        return resourceNames.Where(resourceName => resourceName.StartsWith(directoryName) && (resourceName.EndsWith(".jsonc") || resourceName.EndsWith(".json"))).ToArray();
    }

    public static string GetString(string s, Dictionary<string, string> replacementDic = null, bool console = false)
    {
        SupportedLangs langId;
        int modLanguageId = Options.IsLoaded ? Options.ModLanguage.GetValue() : 0;

        if (console)
            langId = SupportedLangs.English;
        else
        {
            if (modLanguageId != 0)
                langId = (SupportedLangs)(modLanguageId + 99);
            else if (Main.ForceOwnLanguage.Value)
                langId = GetUserTrueLang();
            else
                langId = TranslationController.InstanceExists && TranslationController.Instance.currentLanguage != null
                ? TranslationController.Instance.currentLanguage.languageID
                : SupportedLangs.English;
        }

        if (GameStates.InGame)
        {
            int roomNumber = -1;
            if (SubmergedCompatibility.IsSubmerged() && int.TryParse(s, out roomNumber) && roomNumber is >= 128 and <= 135)
                s = $"SubmergedRoomName.{roomNumber}";

            if (Options.CurrentGameMode == CustomGameMode.Deathrace)
            {
                if ((roomNumber != -1 || int.TryParse(s, out roomNumber)) && Deathrace.CoordinateChecks.ContainsKey(roomNumber))
                    s = "Deathrace.CoordinateCheck";
            }
        }

        string str = GetString(s, langId);

        if (replacementDic != null && replacementDic.Count > 0)
        {
            foreach (KeyValuePair<string, string> rd in replacementDic)
                str = str.Replace(rd.Key, rd.Value);
        }
        if (modLanguageId == 1 && (str.Contains('ő') || str.Contains('ű'))) // Hungarian (none of the fonts support ő/ű and innersloth doesn't care, thankfully at least German has ö/ü)
            str = str.Replace("ő", "ö", StringComparison.CurrentCultureIgnoreCase).Replace("ű", "ü", StringComparison.CurrentCultureIgnoreCase);

        return str;
    }

    public static string GetString(string str, SupportedLangs langId)
    {
        try
        {
            if (RuntimeOverrides.Count > 0 && RuntimeOverrides.TryGetValue(str, out string overrideValue))
                return overrideValue;

            if (TranslateMaps == null) return $"*{str}"; // Init() 前 (起動直後) はテーブルが無い

            if (!LoadedLangs.Contains((int)langId)) EnsureLangLoaded(langId);

            if (TranslateMaps.TryGetValue((int)langId, out var langMap) && langMap.TryGetValue(str, out var res) && !string.IsNullOrEmpty(res))
                return res;

            if (IsKnownKey(str))
            {
                if (langId != SupportedLangs.English)
                    return GetString(str, SupportedLangs.English);
            }
            else if (TryGetStringName(str, out var stringName)) return GetString(stringName);
        }
        catch (Exception ex)
        {
            Logger.Fatal($"Error oucured at [{str}] in the translation file", "Translator");
            Logger.Error("Here was the error:\n" + ex, "Translator");
        }

        return $"*{str}";
    }

    // 読み込み済みのどれかの言語にあるキーか
    private static bool IsKnownKey(string key)
    {
        foreach (Dictionary<string, string> langMap in TranslateMaps.Values)
            if (langMap.ContainsKey(key)) return true;
        return false;
    }

    // 読み込み済みの全キー。英語のキーを先に、他の言語にだけあるキーを後ろに並べる
    private static IEnumerable<string> AllKeys()
    {
        TranslateMaps.TryGetValue((int)SupportedLangs.English, out var english);
        if (english != null)
            foreach (string key in english.Keys) yield return key;

        HashSet<string> seen = null;
        foreach ((int langId, Dictionary<string, string> langMap) in TranslateMaps)
        {
            if (langId == (int)SupportedLangs.English) continue;
            foreach (string key in langMap.Keys)
            {
                if (english != null && english.ContainsKey(key)) continue;
                seen ??= [];
                if (seen.Add(key)) yield return key;
            }
        }
    }

    // GetString が実際に描画に使う言語。ModLanguage 設定 → ForceOwnLanguage → ゲーム本体の言語、の順で
    // 決まる (上の GetString と同じ解決順)。GetUserTrueLang は OS のカルチャしか見ないので、
    // 「プレイヤーが実際に読んでいる言語」を知りたい側はこちらを使う。
    public static SupportedLangs GetEffectiveLang()
    {
        try
        {
            int modLanguageId = Options.IsLoaded ? Options.ModLanguage.GetValue() : 0;
            if (modLanguageId != 0) return (SupportedLangs)(modLanguageId + 99);
            if (Main.ForceOwnLanguage.Value) return GetUserTrueLang();

            return TranslationController.InstanceExists && TranslationController.Instance.currentLanguage != null
                ? TranslationController.Instance.currentLanguage.languageID
                : SupportedLangs.English;
        }
        catch { return SupportedLangs.English; }
    }

    // key がその言語で個別に定義されているかどうか (英語へのフォールバックなし)。GetString と違い
    // 「無ければ英語で代用する」ではなく「無ければ機能自体を出さない」判定に使う (合言葉プール等)。
    public static bool HasTranslation(string key, SupportedLangs langId)
    {
        try
        {
            if (TranslateMaps == null) return false;
            if (!LoadedLangs.Contains((int)langId)) EnsureLangLoaded(langId);
            return TranslateMaps.TryGetValue((int)langId, out var langMap) && langMap.TryGetValue(key, out var res) && !string.IsNullOrEmpty(res);
        }
        catch (Exception ex)
        {
            Logger.Error($"Error checking translation for [{key}]: {ex}", "Translator");
            return false;
        }
    }

    public static string GetString(StringNames stringName)
    {
        if (!TranslationController.InstanceExists) return $"*{stringName}"; // 起動直後は本体側も未生成
        return TranslationController.Instance.GetString(stringName);
    }
    public static string GetString(SystemTypes room)
    {
        if (!TranslationController.InstanceExists) return $"*{room}"; // 起動直後は本体側も未生成
        return TranslationController.Instance.GetString(room);
    }
    // 名前→StringNames の1回構築辞書。以前の実装 (AllStringNames 線形走査 + 要素ごとの enum ToString) は
    // 1 ミスあたり数千個の一時 string を確保し、未登録キーを毎 tick 引く HUD 経路 (例: 大半の役職に存在
    // しない SecondaryAbilityButtonText.*) で MB/s 級のアロケ源になっていた。ミスも記憶して再走査させない。
    private static Dictionary<string, StringNames> stringNameLookup;

    private static bool TryGetStringName(string str, out StringNames result)
    {
        if (stringNameLookup == null)
        {
            stringNameLookup = new(AllStringNames.Length);
            foreach (StringNames val in AllStringNames) stringNameLookup.TryAdd(val.ToString(), val);
        }

        return stringNameLookup.TryGetValue(str, out result);
    }
    public static string GetRoleString(string str, bool forUser = true)
    {
        SupportedLangs currentLanguage = TranslationController.Instance.currentLanguage.languageID;
        SupportedLangs lang = forUser ? currentLanguage : SupportedLangs.English;
        if (Main.ForceOwnLanguageRoleName.Value) lang = GetUserTrueLang();

        return GetString(str, lang);
    }

    public static SupportedLangs GetUserTrueLang()
    {
        try
        {
            string name = CultureInfo.CurrentUICulture.Name;
            if (name.StartsWith("en")) return SupportedLangs.English;
            if (name.StartsWith("zh_CHT")) return SupportedLangs.TChinese;
            if (name.StartsWith("zh")) return SupportedLangs.SChinese;
            if (name.StartsWith("ru")) return SupportedLangs.Russian;
            return TranslationController.Instance.currentLanguage.languageID;
        }
        catch { return SupportedLangs.English; }
    }

    private static void UpdateCustomTranslation(string filename /*, SupportedLangs lang*/)
    {
        var path = $"{Main.DataPath}/{LanguageFolderName}/{filename}";

        if (File.Exists(path))
        {
            Logger.Info("Updating Custom Translations", "UpdateCustomTranslation");

            try
            {
                // 12.5k 鍵 × List.Contains の総当たりで毎起動 ≈0.3s 掛かっていたので集合判定にする。
                HashSet<string> textStrings = [];

                using (StreamReader reader = new(path, Encoding.GetEncoding("UTF-8")))
                {
                    while (reader.ReadLine() is { } line)
                    {
                        // Split the line by ':' to get the first part
                        string[] parts = line.Split(':');

                        // Check if there is at least one part before ':'
                        if (parts.Length >= 1)
                        {
                            // Trim any leading or trailing spaces and add it to the list
                            string textString = parts[0].Trim();
                            textStrings.Add(textString);
                        }
                    }
                }

                var sb = new StringBuilder();

                foreach (string templateString in AllKeys())
                {
                    if (!textStrings.Contains(templateString))
                        sb.Append($"{templateString}:\n");
                }

                // 欠落鍵が無いのに空行だけ追記して .dat を毎起動 1 行ずつ太らせない。
                if (sb.Length == 0) return;

                using FileStream fileStream = new(path, FileMode.Append, FileAccess.Write);
                using StreamWriter writer = new(fileStream);
                writer.WriteLine(sb.ToString());
            }
            catch (Exception e) { Logger.Error("An error occurred: " + e.Message, "Translator"); }
        }
    }

    private static void LoadCustomTranslation(string filename, SupportedLangs lang)
    {
        var path = $"{Main.DataPath}/{LanguageFolderName}/{filename}";

        if (File.Exists(path))
        {
            Logger.Info($"Loading Custom Translation File: {filename}", "LoadCustomTranslation");

            try
            {
                using StreamReader sr = new(path, Encoding.GetEncoding("UTF-8"));

                while (sr.ReadLine() is { } text)
                {
                    string[] tmp = text.Split(':');

                    if (tmp.Length > 1 && tmp[1] != "")
                    {
                        if (IsKnownKey(tmp[0]))
                        {
                            if (!TranslateMaps.TryGetValue((int)lang, out var langMap)) PublishLangMap((int)lang, langMap = []);
                            langMap[tmp[0]] = string.Join(':', tmp[1..]).Replace("\\n", "\n").Replace("\\r", "\r");
                        }
                        else Logger.Warn($"Invalid Key: {tmp[0]}", "LoadCustomTranslation");
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception e) { Logger.Error(e.ToString(), "Translator.LoadCustomTranslation"); }
        }
        else
            Logger.Error($"Custom Translation File Not Found: {filename}", "LoadCustomTranslation");
    }

    private static void CreateTemplateFile()
    {
        // 1 行ずつ書く。全キーを 1 本の文字列にまとめると 50 万文字級になり、その組み立てと書き出しで
        // ArrayPool から借りた MB 級の作業配列がスレッドに残り続ける
        using var writer = new StreamWriter($"{Main.DataPath}/{LanguageFolderName}/template.dat", false, new UTF8Encoding(false));
        bool first = true;
        foreach (string key in AllKeys())
        {
            if (!first) writer.Write('\n');
            first = false;
            writer.Write(key);
            writer.Write(':');
        }
    }

    public static void ExportCustomTranslation()
    {
        LoadLangs();
        var sb = new StringBuilder();
        SupportedLangs lang = TranslationController.Instance.currentLanguage.languageID;
        EnsureLangLoaded(lang);

        TranslateMaps.TryGetValue((int)lang, out var langMap);

        foreach (string key in AllKeys())
        {
            string text = langMap?.GetValueOrDefault(key, "") ?? "";
            sb.Append($"{key}:{text.Replace("\n", "\\n").Replace("\r", "\\r")}\n");
        }

        File.WriteAllText($"{Main.DataPath}/{LanguageFolderName}/export_{lang}.dat", sb.ToString());
    }

    public static string FixRoleName(this string infoLong, CustomRoles role)
    {
        SupportedLangs userLang = GetUserTrueLang();
        EnsureLangLoaded(userLang);
        if (!OriginalRoleNames.TryGetValue(role, out var d) || !d.TryGetValue(userLang, out var o)) return infoLong;
        string modifiedName = role.ToColoredString();
        return infoLong.Contains(modifiedName) ? infoLong : infoLong.Replace(o, modifiedName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool LangHasSensitiveOutlineText()
    {
        return TranslationController.InstanceExists && TranslationController.Instance.currentLanguage.languageID is
                SupportedLangs.Russian or
                SupportedLangs.Korean or
                SupportedLangs.Japanese or
                SupportedLangs.SChinese or
                SupportedLangs.TChinese;
    }
    public static bool LangHasSensitiveOutlineText(SupportedLangs lang)
    {
        return lang is
            SupportedLangs.Russian or
            SupportedLangs.Korean or
            SupportedLangs.Japanese or
            SupportedLangs.SChinese or
            SupportedLangs.TChinese;
    }
}