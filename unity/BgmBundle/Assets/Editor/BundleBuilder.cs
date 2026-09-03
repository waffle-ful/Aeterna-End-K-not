using System.IO;
using UnityEditor;
using UnityEngine;

// BGM 用 AssetBundle のビルド入口 (tools/build-bgm-bundle.ps1 から Unity バッチモードで呼ばれる)。
// Assets/BGM 配下の音声を「Vorbis 圧縮のままメモリ常駐 (Compressed In Memory)・事前ロード無し」の
// 設定に揃えて 1 本のバンドル endknot_bgm に焼く。mod 側は AudioClip.LoadAudioData で必要な曲だけ
// FMOD にデコードさせるため、float PCM をマネージド側に持たない。
// Assets/SFX 配下の長尺効果音 (WaveCannon 発射/チャージ・Backrooms 環境音) は別バンドル endknot_sfx。
// 短いので音声データごと事前ロード (preloadAudioData=true) し、取り出したクリップをそのまま鳴らせる。
public static class BundleBuilder
{
    private const string SourceFolder = "Assets/BGM";
    private const string BundleName = "endknot_bgm";
    private const string SfxSourceFolder = "Assets/SFX";
    private const string SfxBundleName = "endknot_sfx";
    private const float VorbisQuality = 0.7f;

    public static void Build()
    {
        string[] names = Import(SourceFolder, BundleName, preload: false);
        string[] sfxNames = Import(SfxSourceFolder, SfxBundleName, preload: true);

        string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Build");
        Directory.CreateDirectory(outDir);

        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(outDir, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        if (manifest == null)
        {
            Debug.LogError("BundleBuilder: BuildAssetBundles returned null");
            EditorApplication.Exit(2);
            return;
        }

        Debug.Log($"BundleBuilder: built [{string.Join(",", manifest.GetAllAssetBundles())}] clips=[{string.Join(",", names)}] sfx=[{string.Join(",", sfxNames)}]");
    }

    private static string[] Import(string folder, string bundleName, bool preload)
    {
        if (!AssetDatabase.IsValidFolder(folder)) return new string[0];

        string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { folder });
        var names = new string[guids.Length];

        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);

            AudioImporterSampleSettings settings = importer.defaultSampleSettings;
            settings.loadType = AudioClipLoadType.CompressedInMemory;
            settings.compressionFormat = AudioCompressionFormat.Vorbis;
            settings.quality = VorbisQuality;
            settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            settings.preloadAudioData = preload;
            importer.defaultSampleSettings = settings;
            importer.loadInBackground = !preload;
            importer.forceToMono = false;
            importer.assetBundleName = bundleName;
            importer.SaveAndReimport();

            names[i] = Path.GetFileNameWithoutExtension(path);
        }

        return names;
    }
}
