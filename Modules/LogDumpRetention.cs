using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace EndKnot.Modules;

// EndKnot_Logs 直下に自動 DumpLog が作る日時フォルダ (yyyy-MM-dd_HH.mm.ss) の保持期間管理。
// 放置すると1週間で 800個/1.6GB 級に育つため、起動時に1回だけ古いものを削除する。
// 保護ルール: フォルダ名が日時形式そのものでなければ一切触らない — 残したい記録は
// 名前に _keep 等を足してリネームするだけで恒久保護される (HangDumps_keep と同じ流儀)。
// Screens / CrashSnapshots / CrashDumps 等の特殊フォルダも日時形式でないため対象外。
public static class LogDumpRetention
{
    private const int RetentionDays = 14;
    private const int AlwaysKeepNewest = 20; // 期限切れでも直近 N 個は残す全滅防止の安全弁

    public static void Prune()
    {
        try
        {
            string basePath = OperatingSystem.IsAndroid() ? Main.DataPath : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string root = Path.Combine(basePath, "EndKnot_Logs");
            if (!Directory.Exists(root)) return;

            List<(string Path, DateTime Stamp)> dated = [];

            foreach (string dir in Directory.GetDirectories(root))
            {
                if (DateTime.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd_HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime stamp))
                    dated.Add((dir, stamp));
            }

            DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);

            string[] victims = dated
                .OrderByDescending(x => x.Stamp)
                .Skip(AlwaysKeepNewest)
                .Where(x => x.Stamp < cutoff)
                .Select(x => x.Path)
                .ToArray();

            if (victims.Length == 0) return;

            Logger.Info($"Deleting {victims.Length} of {dated.Count} log dump folders (older than {RetentionDays} days; rename a folder, e.g. append _keep, to protect it)", "LogDumpRetention");

            // 削除本体は裏スレッド (数百フォルダの再帰 Delete はメインスレッドだとそれ自体がヒッチになる)。System.IO のみで安全。
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (string dir in victims)
                {
                    try { Directory.Delete(dir, true); }
                    catch { /* 開かれている・権限等で消せない分は次回起動時に再挑戦 */ }
                }
            });
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }
}
