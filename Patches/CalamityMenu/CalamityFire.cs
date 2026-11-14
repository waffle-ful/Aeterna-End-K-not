using System;
using System.IO;
using EndKnot.Modules;

namespace EndKnot.Patches.CalamityMenu;

// 以前の版はメニュー背景に炎の動画を重ね、同梱の動画を BepInEx/plugins/EndKnot/Media/menu_fire.webm へ
// 書き出していた。炎はもう使わないので、その書き出し物だけを起動時に消す (利用者が置いた別の動画には触らない)。
public static class CalamityFire
{
    private const string FireVideoFileName = "menu_fire.webm";
    private const long LegacyExtractedSize = 1682254;
    private const string LegacyExtractedSha256 = "3A4E04D0AB7636E7F9A760C79116D4C406C89D91646492AB70798A53DB8EE417";

    public static void RemoveLegacyExtractedVideo()
    {
        try
        {
            string path = Path.Combine(BepInEx.Paths.PluginPath, "EndKnot", "Media", FireVideoFileName);
            FileInfo info = new(path);
            if (!info.Exists || info.Length != LegacyExtractedSize) return;

            string hash;
            using (FileStream fs = info.OpenRead())
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hash = Convert.ToHexString(sha.ComputeHash(fs));

            if (hash != LegacyExtractedSha256) return;

            File.Delete(path);
            Logger.Info("removed the bundled fire video written out by an earlier version", "CalamityFire");
        }
        catch (Exception e)
        {
            Logger.Warn($"legacy fire video cleanup failed ({e.GetType().Name}: {e.Message})", "CalamityFire");
        }
    }
}
