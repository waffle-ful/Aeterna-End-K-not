using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace EndKnot.Modules;

// 同梱の BGM を収めた EndKnot.Music.dll (BepInEx/plugins に置く・End K not とは別のライセンス) を読む。
// EndKnot.Music.dll はコードを持たずリソースだけを持つアセンブリで、曲 (.ogg・暗号化済み)・曲名の一覧
// (bgm_titles.json)・BGM の AssetBundle を End K not の埋め込みと同じリソース名で収めている。
// End K not はそれをデータとして読むだけ。無ければ BGM はホストが resources/BGM/ に置いた曲だけになる。
// GetManifestResourceStream はスレッドをまたいで呼べるので、裏のデコードのスレッドからも使える。
internal static class MusicPack
{
    internal const string FileName = "EndKnot.Music.dll";

    private static readonly Lazy<Assembly> Pack = new(Load);
    private static readonly Lazy<HashSet<string>> ResourceNames = new(() =>
        new HashSet<string>(Pack.Value?.GetManifestResourceNames() ?? [], StringComparer.Ordinal));

    internal static string PackPath => Path.Combine(BepInEx.Paths.PluginPath, FileName);

    internal static bool Available => Pack.Value != null;

    internal static IReadOnlyCollection<string> Names => ResourceNames.Value;

    internal static bool Contains(string resourceName) => resourceName != null && ResourceNames.Value.Contains(resourceName);

    // 曲は暗号化して収めてあるので、SoundSeal が解きながら読むストリームで包む (AssetBundle と曲名の一覧はそのまま)
    internal static Stream Open(string resourceName)
        => Contains(resourceName) ? SoundSeal.Wrap(Pack.Value.GetManifestResourceStream(resourceName), resourceName) : null;

    // End K not 自身の埋め込みを先に見て、無ければこちらを見る
    internal static Stream OpenEmbeddedOrPack(string resourceName)
        => Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName) ?? Open(resourceName);

    private static Assembly Load()
    {
        string path = PackPath;

        try
        {
            if (!File.Exists(path))
            {
                Logger.Info($"{FileName} not found; bundled BGM is unavailable (host files in resources/BGM still play)", "MusicPack");
                return null;
            }

            Assembly asm = Assembly.LoadFile(path);
            Logger.Info($"{FileName}: {asm.GetManifestResourceNames().Length} resources", "MusicPack");
            return asm;
        }
        catch (Exception e)
        {
            Logger.Warn($"{FileName}: load failed ({e.GetType().Name}: {e.Message})", "MusicPack");
            return null;
        }
    }
}
