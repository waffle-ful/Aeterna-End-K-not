using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace EndKnot.Modules;

// 旧方式 (埋込音声を BepInEx/resources/ へ生ファイルのまま書き出す) で展開済みのファイルを、
// 1 回だけ掃除する移行処理。
//
// メモリ内デコードへ切り替えても、3 経路すべてが「ディスクにあればそれを優先」なので、
// すでに展開済みのファイルが残っている既存インストールでは新しい経路が一生使われない
// (= 素材の再配布条項に対する残置も消えない)。よってこの掃除は作り替えと不可分。
//
// 設計上の約束:
//   ・触る対象は「DLL に埋め込まれている音声リソース名から実行時に組み立てた名前」だけ。
//     同梱外の音源・VoiceVox の出力・bgm_config.json 等は名前の時点で対象外。
//   ・そのうち **中身が埋込リソースとバイト単位で一致する物は削除**。旧展開経路は埋込ストリームを
//     そのままコピーしていたので、現行版が展開した物は必ず一致する。
//   ・**バイト列が違う物は削除せず `.bak` へ退避**する。「違う = ホストの自作」とは限らず、
//     旧バージョンで展開された古い同梱曲・途中で切れた展開途中のファイルも違うため、温存すると
//     ディスク優先ルールでそれが鳴り続け、残置も消えない。`.bak` は音源解決の拡張子
//     (.wav/.ogg/.mp3) に当たらないので読み込み経路から即座に外れ、ホストの作業物は手元に残る。
//     ⚠️ ただし「古い同梱曲の .bak」は依然その素材のバイト列がディスクにある状態なので、
//     残置を完全に消したいなら削除しかない (その判断はご主人様に委ねる)。
//   ・バージョンスタンプ付きマーカーで「1 バージョンにつき 1 回」に固定する。旧版へロールバック
//     すると旧版が再び展開するため、単なる有無判定のマーカーだと再アップグレード後に二度と
//     掃除されず残置が黙って復活する。バイト判定と .bak 退避のおかげで再実行しても破壊は起きない。
//   ・1 ファイル = 1 行のログ (削除 / 退避の両方)。
public static class BundledAudioCleanup
{
    private const string MarkerName = ".endknot-audio-migrated";
    private const string ResourcePrefix = "EndKnot.Resources.Sounds.";
    private static readonly string[] AudioExtensions = [".wav", ".ogg", ".mp3"];

    // 埋込名はサブフォルダを '.' 区切りで平坦化するため、フォルダ配下かどうかは接頭辞で判別する。
    // (Resources/Sounds 直下のサブフォルダはこの 2 つだけ)
    private static readonly (string Prefix, Func<string> Folder)[] SubFolders =
    [
        ("BGM.", () => BGMManager.BGMPath),
        ("Backrooms.", () => BackroomsAmbient.AmbientPath)
    ];

    public static void Run()
    {
        try
        {
            string root = CustomSoundsManager.SoundsPath;
            string marker = root + MarkerName;

            if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            if (AlreadyDoneForThisVersion(marker)) return;

            int removed = 0, movedAside = 0, failed = 0;

            foreach ((string resource, string target) in EnumerateBundledTargets())
            {
                foreach (string victim in WithLeftoverTempFiles(target))
                {
                    // 本体ファイルが埋込と違うバイト列なら、消さずに .bak へ退避する。
                    // 「違う = ホストの自作」とは限らない (旧バージョンで展開された古い同梱曲も違う) ため、
                    // 温存するとディスク優先ルールでその古い曲が鳴り続け、残置も消えない。
                    // .bak は音源解決の拡張子 (.wav/.ogg/.mp3) に当たらないので読み込み経路から即座に外れ、
                    // ホストの作業物は手元に残る。孤児 tmp は mod 自身が付けた GUID 名なので中身を問わず消す。
                    if (victim == target && !MatchesEmbedded(victim, resource))
                    {
                        try
                        {
                            string bak = FreeBackupName(victim);
                            File.Move(victim, bak);
                            movedAside++;
                            Logger.Info($"Moved aside (differs from bundled): {victim} -> {Path.GetFileName(bak)}", "BundledAudioCleanup");
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Logger.Warn($"Could not move aside {victim}: {ex.Message}", "BundledAudioCleanup");
                        }

                        continue;
                    }

                    try
                    {
                        File.Delete(victim);
                        removed++;
                        Logger.Info($"Removed extracted bundled audio: {victim}", "BundledAudioCleanup");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Logger.Warn($"Could not remove {victim}: {ex.Message}", "BundledAudioCleanup");
                    }
                }
            }

            // 失敗が残っていてもスタンプは書く (次バージョンで再挑戦される)。バイト一致判定があるので
            // 再実行してもホストの差し替えは消えない。
            try { File.WriteAllText(marker, Main.PluginVersion + "\n"); }
            catch (Exception ex) { Logger.Warn($"Could not write migration marker: {ex.Message}", "BundledAudioCleanup"); }

            Logger.Info($"Bundled audio cleanup done (removed={removed}, movedAside={movedAside}, failed={failed})", "BundledAudioCleanup");
        }
        catch (Exception ex)
        {
            // 掃除が失敗しても音は鳴る (ディスク優先で旧ファイルが使われるだけ) ので握りつぶす
            Logger.Exception(ex, "BundledAudioCleanup.Run");
        }
    }

    // 既存の .bak を上書きしない退避先を探す (上書きすると前回の退避物を壊す)。
    private static string FreeBackupName(string path)
    {
        string candidate = path + ".bak";

        for (int i = 2; File.Exists(candidate) && i < 100; i++)
            candidate = $"{path}.bak{i}";

        return candidate;
    }

    // マーカーに書いてあるバージョンが現在と同じなら、この版ではもう掃除済み。
    // 旧版へロールバックすると旧版が再展開するので、バージョンが変わったら必ずもう一度走らせる。
    private static bool AlreadyDoneForThisVersion(string marker)
    {
        try { return File.Exists(marker) && File.ReadAllText(marker).Trim() == Main.PluginVersion; }
        catch { return false; }
    }

    // ディスク上のファイルが埋込リソースとバイト単位で一致するか。旧展開経路は埋込ストリームを
    // そのままコピーしていたので、mod が展開した物は必ず一致する。読めなければ「一致しない」に倒す
    // (判定できない物は消さない)。
    private static bool MatchesEmbedded(string path, string resourceName)
    {
        try
        {
            using Stream embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (embedded == null) return false;

            using FileStream disk = File.OpenRead(path);
            if (disk.Length != embedded.Length) return false;

            byte[] a = new byte[64 * 1024];
            byte[] b = new byte[64 * 1024];

            while (true)
            {
                int n = ReadBlock(disk, a);
                int m = ReadBlock(embedded, b);
                if (n != m) return false;
                if (n == 0) return true;

                for (int i = 0; i < n; i++)
                    if (a[i] != b[i])
                        return false;
            }
        }
        catch { return false; }
    }

    // Stream.Read は要求より短く返しうるので、バッファが埋まるか末尾に達するまで読む。
    private static int ReadBlock(Stream stream, byte[] buffer)
    {
        int off = 0;

        while (off < buffer.Length)
        {
            int n = stream.Read(buffer, off, buffer.Length - off);
            if (n <= 0) break;

            off += n;
        }

        return off;
    }

    // DLL に埋め込まれている音声リソースから、旧方式が書き出していたであろう実ファイルパスを組み立てる。
    // 埋込リソースが正典なので、同梱ラインナップが変わってもこの一覧は自動で追随する。
    private static IEnumerable<(string Resource, string Target)> EnumerateBundledTargets()
    {
        foreach (string res in Assembly.GetExecutingAssembly().GetManifestResourceNames())
        {
            if (!res.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;

            string tail = res[ResourcePrefix.Length..];

            // 音声以外 (bgm_titles.json 等) は対象外
            if (Array.IndexOf(AudioExtensions, Path.GetExtension(tail).ToLowerInvariant()) < 0) continue;

            string folder = CustomSoundsManager.SoundsPath;

            foreach ((string prefix, Func<string> path) in SubFolders)
            {
                if (!tail.StartsWith(prefix, StringComparison.Ordinal)) continue;

                folder = path();
                tail = tail[prefix.Length..];
                break;
            }

            yield return (res, folder + tail);
        }
    }

    // 旧展開経路は一時名 (<file>.<guid>.tmp) へ書いてから atomic move していた。move に負けた側の
    // 孤児 tmp は「次回起動の掃除に任せる」とされたまま誰も掃除していないので、ここで一緒に片付ける。
    // 中身は同梱音声そのものなので、放置すると残置が消えない。
    private static IEnumerable<string> WithLeftoverTempFiles(string target)
    {
        if (File.Exists(target)) yield return target;

        string dir = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;

        string[] orphans;
        try { orphans = Directory.GetFiles(dir, Path.GetFileName(target) + ".*.tmp"); }
        catch { yield break; }

        foreach (string orphan in orphans) yield return orphan;
    }
}
