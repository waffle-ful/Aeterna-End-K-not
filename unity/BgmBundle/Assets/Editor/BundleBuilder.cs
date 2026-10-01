using System.IO;
using UnityEditor;
using UnityEngine;

// BGM 用 AssetBundle のビルド入口 (tools/build-bgm-bundle.ps1 から Unity バッチモードで呼ばれる)。
// Assets/BGM 配下の音声を「Vorbis 圧縮のままメモリ常駐 (Compressed In Memory)・事前ロード無し」の
// 設定に揃えて 1 本のバンドル endknot_bgm に焼く。mod 側は AudioClip.LoadAudioData で必要な曲だけ
// FMOD にデコードさせるため、float PCM をマネージド側に持たない。
// Assets/SFX 配下の長尺効果音 (WaveCannon 発射/チャージ・Backrooms 環境音) は別バンドル endknot_sfx。
// 短いので音声データごと事前ロード (preloadAudioData=true) し、取り出したクリップをそのまま鳴らせる。
// Assets/Dusk 配下のメインメニュー背景 PNG (テーマごとのサブフォルダ) は endknot_dusk。
// PNG デコードをメニュー表示中のフレームから外すため、非圧縮 RGBA32 のまま焼く (Windows 版のみ)。
// Assets/Backrooms 配下のロビー床・壁 PNG は同じ設定で endknot_backrooms (Windows / Android 両方)。
public static class BundleBuilder
{
    private const string SourceFolder = "Assets/BGM";
    private const string BundleName = "endknot_bgm";
    private const string SfxSourceFolder = "Assets/SFX";
    private const string SfxBundleName = "endknot_sfx";
    private const float VorbisQuality = 0.7f;
    private const string DuskSourceFolder = "Assets/Dusk";
    private const string DuskBundleName = "endknot_dusk";
    private const string BackroomsSourceFolder = "Assets/Backrooms";
    private const string BackroomsBundleName = "endknot_backrooms";

    public static void Build() => BuildFor(BuildTarget.StandaloneWindows64, "Build");

    // Android (arm64) 版。出力先を分けて Windows 版と混ざらないようにする (Build/android/)。
    // Android Build Support モジュールが無い Editor ではここで失敗する。
    public static void BuildAndroid() => BuildFor(BuildTarget.Android, Path.Combine("Build", "android"));

    private static void BuildFor(BuildTarget target, string outSubDir)
    {
        string[] names = Import(SourceFolder, BundleName, preload: false);
        string[] sfxNames = Import(SfxSourceFolder, SfxBundleName, preload: true);
        int duskCount = ImportTextures(DuskSourceFolder, DuskBundleName, target == BuildTarget.StandaloneWindows64);
        int backroomsCount = ImportTextures(BackroomsSourceFolder, BackroomsBundleName, true);

        string outDir = Path.Combine(Directory.GetCurrentDirectory(), outSubDir);
        Directory.CreateDirectory(outDir);

        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(outDir, BuildAssetBundleOptions.ChunkBasedCompression, target);
        if (manifest == null)
        {
            Debug.LogError("BundleBuilder: BuildAssetBundles returned null");
            EditorApplication.Exit(2);
            return;
        }

        Debug.Log($"BundleBuilder: built [{string.Join(",", manifest.GetAllAssetBundles())}] target={target} clips=[{string.Join(",", names)}] sfx=[{string.Join(",", sfxNames)}] dusk={duskCount} backrooms={backroomsCount}");
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

    // include のターゲットではテクスチャを指定のバンドルに入れる。それ以外のターゲットでは bundle 名を外し、
    // 前回の Windows ビルドで .meta に残った割り当てが他ターゲットの出力へ混ざらないようにする。
    private static int ImportTextures(string folder, string bundleName, bool include)
    {
        if (!AssetDatabase.IsValidFolder(folder)) return 0;

        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });

        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);

            if (!include)
            {
                importer.assetBundleName = null;
                importer.SaveAndReimport();
                continue;
            }

            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.isReadable = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = true;
            importer.streamingMipmaps = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.maxTextureSize = 8192;
            importer.textureCompression = TextureImporterCompression.Uncompressed;

            var standalone = new TextureImporterPlatformSettings
            {
                name = "Standalone",
                overridden = true,
                maxTextureSize = 8192,
                format = TextureImporterFormat.RGBA32,
                textureCompression = TextureImporterCompression.Uncompressed,
            };
            importer.SetPlatformTextureSettings(standalone);

            // Android の既定 (ASTC 圧縮) に落とさず、Windows 版と同じ画素をそのまま持たせる。
            var android = new TextureImporterPlatformSettings
            {
                name = "Android",
                overridden = true,
                maxTextureSize = 8192,
                format = TextureImporterFormat.RGBA32,
                textureCompression = TextureImporterCompression.Uncompressed,
            };
            importer.SetPlatformTextureSettings(android);
            importer.assetBundleName = bundleName;
            importer.SaveAndReimport();
        }

        return include ? guids.Length : 0;
    }
}
