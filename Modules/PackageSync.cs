using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BepInEx;

namespace EndKnot.Modules;

// 配布 zip 一式 (Doorstop・BepInEx 本体・dotnet ランタイム・patcher・設定・同梱データ) をゲームフォルダへ揃える。
// 配布 zip には目録 BepInEx/EndKnot.package.txt (版番号と各ファイルの SHA-256) が入っている。
// DLL だけが差し替わった環境 (手で DLL を置いた・DLL しか替えない旧版の自動更新) では目録の版が自分の版と食い違うので、
// 起動時に自分と同じ版の zip を取り寄せて、中身の違うファイルだけを入れ替える。自動更新も同じ処理で zip 全体を適用する。
//
// 使用中の DLL (winhttp.dll・dotnet・BepInEx/core) は上書きできないが改名はできるので、
// 新しい物を <path>.endknot-new に書き出してから、旧を <path>.endknot-old へ改名して入れ替える。旧ファイルは次の起動で消す。
// 目録は全部入れ替えた後に書くので、途中で失敗しても次の起動でやり直すだけで済む。
//
// BepInEx/config/ 配下は利用者や mod が書き換えるので丸ごとは置き換えず、配布側で変わった値だけを取り込む (MergeConfig)。
// 目録に載っていないファイル (利用者のデータなど) には触らない。
public static class PackageSync
{
    public const string ManifestPath = "BepInEx/EndKnot.package.txt";
    private const string SelfPath = "BepInEx/plugins/EndKnot.dll";
    private const string ConfigPrefix = "BepInEx/config/";
    private const string NewSuffix = ".endknot-new";
    private const string OldSuffix = ".endknot-old";
    private const string ReleaseDownloadBase = "https://github.com/waffle-ful/Aeterna-End-K-not/releases/download";
    private const string TestZipVariable = "ENDKNOT_PACKAGE_ZIP";
    private const int MaxAttempts = 3;

    private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("EndKnot.PackageSync");
    private static readonly object ApplyLock = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private static bool _started;
    private static bool _noticePending;

    private static string Root => Paths.GameRootPath;
    private static string CacheDir => Path.Combine(Paths.BepInExRootPath, "cache");
    private static string CleanupListPath => Path.Combine(CacheDir, "EndKnot.package-cleanup.txt");
    private static string BaseDir => Path.Combine(CacheDir, "EndKnot.package-base");
    private static string PendingDir => Path.Combine(CacheDir, "EndKnot.package-pending");
    private static string AttemptsPath => Path.Combine(Paths.ConfigPath, "EndKnot.PackageSync.txt");
    private static string StartupDownloadPath => Path.Combine(CacheDir, "EndKnot.package.zip");
    private static string UpdateDownloadPath => Path.Combine(CacheDir, "EndKnot.update.zip");

    private static bool IsReleaseBuild
    {
        get
        {
#if ENDKNOT_PUBLIC_RELEASE
            return !Main.PluginVersion.EndsWith("-dev", StringComparison.Ordinal);
#else
            return false;
#endif
        }
    }

    // 入れ替えに入った後の失敗。手元は戻してあるが、DLL 単体の差し替えに落としてはいけない
    private sealed class CommitException(string message, Exception inner) : Exception(message, inner);

    private enum UpdateResult { Applied, NoManifest, FailedBeforeCommit, FailedDuringCommit }

    // メインメニュー到達時に呼ぶ (Windows のみ)。前回の入れ替えの後始末と、DLL だけ替わった環境の検知。
    public static void OnMenuStart()
    {
        if (_started) return;
        _started = true;

        try
        {
            CleanupPrevious();
            ApplyPendingConfigs();

            string testZip = Environment.GetEnvironmentVariable(TestZipVariable);
            bool useTestZip = !string.IsNullOrEmpty(testZip);
            if (!useTestZip && !IsReleaseBuild) return;

            Manifest local = ReadLocalManifest();
            if (local?.Version == Main.PluginVersion)
            {
                if (File.Exists(AttemptsPath)) File.Delete(AttemptsPath);
                return;
            }

            int attempts = ReadAttempts();
            if (attempts >= MaxAttempts)
            {
                Log.LogError($"PKGSYNC: gave up after {attempts} attempts for {Main.PluginVersion} (local package {local?.Version ?? "none"}) — reinstall with the installer or the release zip");
                return;
            }

            WriteAttempts(attempts + 1);
            Log.LogInfo($"PKGSYNC: local package {local?.Version ?? "none"} does not match {Main.PluginVersion} — syncing (attempt {attempts + 1})");

            Task<int> task = Task.Run(() => SyncOwnVersionAsync(useTestZip ? testZip : null));
            WhenDone(task, t =>
            {
                if (!t.IsCompletedSuccessfully)
                {
                    Log.LogWarning($"PKGSYNC: sync failed ({t.Exception?.GetBaseException().Message}) — will retry on the next launch");
                    return;
                }

                if (t.Result <= 0) return;
                _noticePending = true;
                ShowPendingNotice();
            });
        }
        catch (Exception e)
        {
            Log.LogWarning($"PKGSYNC: startup check failed ({e.GetType().Name}: {e.Message})");
        }
    }

    // 入れ替えが終わった時にメニューの外にいたら、次にメニューへ戻った時に出す
    public static void ShowPendingNotice()
    {
        if (!_noticePending || !ModUpdater.CanShowPopup) return;
        _noticePending = false;
        ModUpdater.ShowPopup(Translator.GetString("packageSyncRestart"), StringNames.Close, true);
    }

    // 自動更新: 新しい版の zip を取得して一式を適用する。ポップアップは主スレッドで出す。
    public static void StartUpdate(string url, Action fallbackDllOnly)
    {
        Task<UpdateResult> task = Task.Run(async () =>
        {
            try
            {
                await DownloadAsync(url, UpdateDownloadPath).ConfigureAwait(false);
                return Apply(UpdateDownloadPath, includeSelf: true) < 0 ? UpdateResult.NoManifest : UpdateResult.Applied;
            }
            catch (CommitException e)
            {
                Log.LogError($"PKGSYNC: update failed while replacing files — {e.InnerException?.Message}");
                return UpdateResult.FailedDuringCommit;
            }
            catch (Exception e)
            {
                Log.LogWarning($"PKGSYNC: update failed before replacing files — {e.GetType().Name}: {e.Message}");
                return UpdateResult.FailedBeforeCommit;
            }
            finally { TryDelete(UpdateDownloadPath); }
        });

        WhenDone(task, t =>
        {
            switch (t.IsCompletedSuccessfully ? t.Result : UpdateResult.FailedBeforeCommit)
            {
                case UpdateResult.Applied:
                    ModUpdater.ShowPopup(Translator.GetString("updateRestart"), StringNames.Close, true);
                    break;
                case UpdateResult.FailedDuringCommit:
                    ModUpdater.ShowPopup(Translator.GetString("updateManually"), StringNames.Close, true);
                    break;
                default:
                    fallbackDllOnly();
                    break;
            }
        });
    }

    // Task の完了を主スレッド (LateTask) で拾う
    private static void WhenDone<T>(Task<T> task, Action<Task<T>> onDone)
    {
        if (!task.IsCompleted)
        {
            LateTask.New(() => WhenDone(task, onDone), 0.5f, "PackageSync.Poll", log: false);
            return;
        }

        try { onDone(task); }
        catch (Exception e) { Log.LogWarning($"PKGSYNC: completion handler failed ({e.GetType().Name}: {e.Message})"); }
    }

    private static async Task<int> SyncOwnVersionAsync(string testZip)
    {
        if (testZip != null) return Apply(testZip, includeSelf: false);

        string version = Main.PluginVersion;
        string url = $"{ReleaseDownloadBase}/v{version}/EndKnot-{version}_Steam.zip";
        await DownloadAsync(url, StartupDownloadPath).ConfigureAwait(false);

        try { return Apply(StartupDownloadPath, includeSelf: false); }
        finally { TryDelete(StartupDownloadPath); }
    }

    private static async Task DownloadAsync(string url, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string part = path + ".part";

        using (HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode) throw new($"download failed with status {(int)response.StatusCode}: {url}");

            await using Stream src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await src.CopyToAsync(dst).ConfigureAwait(false);
        }

        File.Move(part, path, true);
    }

    // zip を適用して、入れ替え・退避・更新したファイルの数を返す。zip に目録が無い時は -1 (旧形式の配布物)。
    private static int Apply(string zipPath, bool includeSelf)
    {
        lock (ApplyLock)
        {
            Directory.CreateDirectory(CacheDir);
            using ZipArchive zip = ZipFile.OpenRead(zipPath);

            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry e in zip.Entries)
                if (e.Name.Length > 0) entries[Normalize(e.FullName)] = e;

            if (!entries.TryGetValue(ManifestPath, out ZipArchiveEntry manifestEntry))
            {
                Log.LogWarning("PKGSYNC: the package has no manifest — nothing applied");
                return -1;
            }

            string manifestText = ReadEntryText(manifestEntry);
            Manifest next = Manifest.Parse(manifestText);
            Manifest prev = ReadLocalManifest();

            var plan = new List<(string Path, ZipArchiveEntry Entry, string Hash)>();
            var configs = new List<(string Path, string Shipped)>();

            foreach ((string rel, (string hash, long size)) in next.Files)
            {
                if (!IsSafe(rel)) throw new InvalidDataException($"unsafe path in manifest: {rel}");
                if (!includeSelf && rel.Equals(SelfPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!entries.TryGetValue(rel, out ZipArchiveEntry entry)) throw new InvalidDataException($"manifest entry missing from the package: {rel}");

                string full = FullPath(rel);

                // 手元にある設定ファイルは丸ごと置き換えず、値の単位で取り込む
                if (IsConfig(rel) && File.Exists(full))
                {
                    configs.Add((rel, ReadEntryText(entry)));
                    continue;
                }

                string localHash = File.Exists(full) ? HashFile(full, size) : null;
                if (localHash == hash) continue;

                plan.Add((rel, entry, hash));
            }

            List<string> stale = prev == null
                ? []
                : prev.Files.Keys
                    .Where(k => !next.Files.ContainsKey(k) && !IsConfig(k) && IsSafe(k))
                    .Where(k => includeSelf || !k.Equals(SelfPath, StringComparison.OrdinalIgnoreCase))
                    .Where(k => File.Exists(FullPath(k)))
                    .ToList();

            // 先に全部を隣へ書き出して中身を確かめる (ここで失敗しても手元のファイルは無傷)
            try
            {
                foreach ((string rel, ZipArchiveEntry entry, string hash) in plan)
                {
                    string staged = FullPath(rel) + NewSuffix;
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    entry.ExtractToFile(staged, true);
                    if (HashFile(staged, -1) != hash) throw new InvalidDataException($"staged file does not match the manifest: {rel}");
                }
            }
            catch
            {
                foreach ((string rel, _, _) in plan) TryDelete(FullPath(rel) + NewSuffix);
                throw;
            }

            // 入れ替え。途中で失敗したら退避した旧ファイルを逆順に戻し、目録は書かない (次の起動でやり直す)
            var moved = new List<(string Full, string Aside)>();
            try
            {
                foreach ((string rel, _, _) in plan)
                {
                    string full = FullPath(rel);
                    if (File.Exists(full)) moved.Add((full, MoveAside(full)));
                    File.Move(full + NewSuffix, full);
                }

                foreach (string rel in stale)
                {
                    string full = FullPath(rel);
                    moved.Add((full, MoveAside(full)));
                }
            }
            catch (Exception e)
            {
                for (int i = moved.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (File.Exists(moved[i].Full)) File.Move(moved[i].Full, moved[i].Full + NewSuffix, true);
                        File.Move(moved[i].Aside, moved[i].Full);
                    }
                    catch (Exception) { }
                }

                foreach ((string rel, _, _) in plan) TryDelete(FullPath(rel) + NewSuffix);
                throw new CommitException("replacing files failed", e);
            }

            if (moved.Count > 0) File.AppendAllLines(CleanupListPath, moved.Select(m => m.Aside));

            // 設定ファイル: 今回配布の写しは保留に置き、取り込みは今と次の起動の 2 回行う
            // (動いている BepInEx や mod がこの後に古い値で書き戻しても、次の起動で取り込み直せる)
            var mergedConfigs = 0;
            foreach (string rel in next.Files.Keys.Where(IsConfig))
            {
                string pendingPath = Path.Combine(PendingDir, ToNative(rel));
                Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
                File.WriteAllText(pendingPath, ReadEntryText(entries[rel]));
            }

            foreach ((string rel, string shipped) in configs)
                if (MergeConfig(rel, shipped)) mergedConfigs++;

            string manifestFull = FullPath(ManifestPath);
            File.WriteAllText(manifestFull + NewSuffix, manifestText);
            File.Move(manifestFull + NewSuffix, manifestFull, true);

            Log.LogInfo($"PKGSYNC: applied package {next.Version} — replaced {plan.Count}, removed {stale.Count}, updated {mergedConfigs} config file(s)");
            return plan.Count + stale.Count + mergedConfigs;
        }
    }

    // 保留中の配布設定を取り込み直し、次の更新の比較元 (前回配布の写し) として確定する
    private static void ApplyPendingConfigs()
    {
        if (!Directory.Exists(PendingDir)) return;

        foreach (string pendingPath in Directory.GetFiles(PendingDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                string rel = Normalize(Path.GetRelativePath(PendingDir, pendingPath));
                if (!IsConfig(rel) || !IsSafe(rel)) continue;

                string shipped = File.ReadAllText(pendingPath);
                if (File.Exists(FullPath(rel))) MergeConfig(rel, shipped);

                string basePath = BasePath(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
                File.Move(pendingPath, basePath, true);
            }
            catch (Exception e) { Log.LogWarning($"PKGSYNC: pending config {pendingPath} failed ({e.GetType().Name}: {e.Message})"); }
        }

        try { Directory.Delete(PendingDir, true); }
        catch (Exception) { }
    }

    // 手元の設定ファイルへ、配布側で変わった値だけを取り込む。BepInEx は起動のたびに設定ファイルを自分の書式で
    // 書き直すのでファイル単位では比べられない。前回配布の写し (base)・今回配布 (shipped)・手元 (local) の 3 つを
    // 「[節] キー」ごとに比べ、配布側で値が変わったキーのうち手元が前回配布の値のまま (または未設定) の物だけを書き換える。
    // 利用者や mod が変えた値には触らない。前回配布の写しが無い時 (初めての同期) は手元をそのまま使う。
    private static bool MergeConfig(string rel, string shipped)
    {
        string basePath = BasePath(rel);
        if (!File.Exists(basePath))
        {
            Log.LogInfo($"PKGSYNC: kept {rel} as is (no previous package copy to compare with)");
            return false;
        }

        string full = FullPath(rel);
        string local = File.ReadAllText(full);
        Dictionary<string, string> baseValues = ParseCfg(File.ReadAllText(basePath));
        Dictionary<string, string> localValues = ParseCfg(local);

        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in ParseCfg(shipped))
        {
            baseValues.TryGetValue(key, out string baseValue);
            localValues.TryGetValue(key, out string localValue);
            if (baseValue == value || localValue == value) continue;

            if (localValue == null || localValue == baseValue || baseValue == null) updates[key] = value;
            else Log.LogInfo($"PKGSYNC: kept locally changed {rel} {key.Replace('\n', '.')}");
        }

        if (updates.Count == 0) return false;

        string merged = ApplyCfgUpdates(local, updates);
        string tmp = full + NewSuffix;
        File.WriteAllText(tmp, merged);
        File.Move(tmp, full, true);
        Log.LogInfo($"PKGSYNC: updated {rel}: {string.Join(", ", updates.Keys.Select(k => k.Replace('\n', '.')))}");
        return true;
    }

    // BepInEx の設定ファイル: "[節]" と "キー = 値"。# と ; で始まる行は注釈。キーは "節\nキー"。
    private static Dictionary<string, string> ParseCfg(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = "";
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq > 0) values[$"{section}\n{line[..eq].Trim()}"] = line[(eq + 1)..].Trim();
        }

        return values;
    }

    // 値の行だけを書き換え、無いキーは節の末尾 (節も無ければファイル末尾) に足す。他の行と改行の形はそのまま。
    private static string ApplyCfgUpdates(string text, Dictionary<string, string> updates)
    {
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var pending = new Dictionary<string, string>(updates, StringComparer.Ordinal);
        var lastLineOfSection = new Dictionary<string, int>(StringComparer.Ordinal);
        var section = "";

        for (var i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                lastLineOfSection[section] = i;
                continue;
            }

            lastLineOfSection[section] = i;
            if (line[0] is '#' or ';') continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = $"{section}\n{line[..eq].Trim()}";
            if (!pending.Remove(key, out string value)) continue;

            int rawEq = lines[i].IndexOf('=');
            lines[i] = lines[i][..(rawEq + 1)] + " " + value;
        }

        foreach (IGrouping<string, KeyValuePair<string, string>> group in pending.GroupBy(kv => kv.Key[..kv.Key.IndexOf('\n')]).ToList())
        {
            List<string> added = group.Select(kv => $"{kv.Key[(kv.Key.IndexOf('\n') + 1)..]} = {kv.Value}").ToList();
            if (lastLineOfSection.TryGetValue(group.Key, out int at))
            {
                lines.InsertRange(at + 1, added);
                foreach (string s in lastLineOfSection.Keys.ToList())
                    if (lastLineOfSection[s] > at) lastLineOfSection[s] += added.Count;
            }
            else
            {
                if (group.Key.Length > 0) added.Insert(0, $"[{group.Key}]");
                lines.Add("");
                lines.AddRange(added);
            }
        }

        return string.Join(newline, lines);
    }

    // 旧ファイルを隣へ改名する。前回の退避物がまだ使用中で消せない時は別名にする。
    private static string MoveAside(string full)
    {
        string aside = full + OldSuffix;
        try { File.Move(full, aside, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            aside = $"{full}{OldSuffix}-{DateTime.UtcNow.Ticks}";
            File.Move(full, aside);
        }

        return aside;
    }

    // 前回退避した旧ファイルを消す。本体が無く、かつ手元の目録に載っている時だけ (= 入れ替えが途中で止まった) 旧ファイルを戻す。
    // 配布物から外れて退避した物は目録に載らないので戻さない。
    private static void CleanupPrevious()
    {
        if (!File.Exists(CleanupListPath)) return;

        Manifest local = ReadLocalManifest();
        var remaining = new List<string>();
        foreach (string aside in File.ReadAllLines(CleanupListPath).Where(l => l.Length > 0).Distinct())
        {
            try
            {
                if (!File.Exists(aside)) continue;

                int cut = aside.LastIndexOf(OldSuffix, StringComparison.Ordinal);
                string original = cut > 0 ? aside[..cut] : null;
                string rel = original != null ? Normalize(Path.GetRelativePath(Root, original)) : null;

                if (original != null && !File.Exists(original) && local != null && local.Files.ContainsKey(rel)) File.Move(aside, original);
                else File.Delete(aside);
            }
            catch (Exception) { remaining.Add(aside); }
        }

        if (remaining.Count > 0) File.WriteAllLines(CleanupListPath, remaining);
        else File.Delete(CleanupListPath);
    }

    private static Manifest ReadLocalManifest()
    {
        string path = FullPath(ManifestPath);
        return File.Exists(path) ? Manifest.Parse(File.ReadAllText(path)) : null;
    }

    private static int ReadAttempts()
    {
        if (!File.Exists(AttemptsPath)) return 0;
        string[] lines = File.ReadAllLines(AttemptsPath);
        if (lines.Length < 2 || lines[0] != $"version={Main.PluginVersion}") return 0;
        return int.TryParse(lines[1].Replace("attempts=", ""), out int n) ? n : 0;
    }

    private static void WriteAttempts(int n) => File.WriteAllLines(AttemptsPath, [$"version={Main.PluginVersion}", $"attempts={n}"]);

    // 大きさが目録と違えば中身を読むまでもなく別物 (sizeHint < 0 で常に計算)
    private static string HashFile(string path, long sizeHint)
    {
        if (sizeHint >= 0 && new FileInfo(path).Length != sizeHint) return "size-mismatch";
        using FileStream fs = File.OpenRead(path);
        using SHA256 sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string ToNative(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    private static bool IsConfig(string rel) => rel.StartsWith(ConfigPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsSafe(string rel) =>
        rel.Length > 0 && !Path.IsPathRooted(rel) && !rel.Contains(':') && rel.Split('/').All(seg => seg.Length > 0 && seg != "." && seg != "..");

    private static string FullPath(string rel) => Path.Combine(Root, ToNative(rel));

    private static string BasePath(string rel) => Path.Combine(BaseDir, ToNative(rel));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception) { }
    }

    private sealed class Manifest
    {
        public string Version;
        public readonly Dictionary<string, (string Hash, long Size)> Files = new(StringComparer.OrdinalIgnoreCase);

        // 1 行目 "version=<版>"、以降 "<sha256> <bytes> <path>" (パスは空白を含みうるので 3 列目以降を全部)
        public static Manifest Parse(string text)
        {
            var m = new Manifest();
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("version=", StringComparison.Ordinal))
                {
                    m.Version = line["version=".Length..];
                    continue;
                }

                string[] parts = line.Split(' ', 3);
                if (parts.Length == 3 && long.TryParse(parts[1], out long size))
                    m.Files[Normalize(parts[2])] = (parts[0].ToLowerInvariant(), size);
            }

            return m;
        }
    }
}
