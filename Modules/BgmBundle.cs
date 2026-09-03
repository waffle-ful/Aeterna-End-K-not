using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace EndKnot.Modules;

// BGM を AssetBundle (Vorbis 圧縮のままメモリ常駐) から読む経路。埋込 OGG → float PCM デコードより
// 常駐メモリが 1 曲あたり 25-50MB から 1-2MB へ落ちる。埋込からメモリ直読み — ディスクへは
// 書き出さない (素材の再配布条項)。バンドル本体は unity/BgmBundle/ で焼いて
// Resources/Sounds/BGM/endknot_bgm.bundle として埋め込む (tools/build-bgm-bundle.ps1)。
// メインスレッド専用 (AssetBundle/AudioClip API は Unity メインスレッドでしか呼べない)。
internal static class BgmBundle
{
    private const string EmbeddedResourceName = "EndKnot.Resources.Sounds.BGM.endknot_bgm.bundle";

    // 旧バージョン (ディスク抽出方式) が BepInEx/plugins/EndKnot/Media に残した一時ファイルの掃除用。
    // 今後は書き出さないので、この定数は初回起動時の掃除にしか使わない。
    private const string LegacyMediaSubDir = "EndKnot/Media";
    private const string LegacyBundleFilePrefix = "endknot_bgm.";
    private const string LegacyBundleFileSuffix = ".bundle";

    private static bool _initialized;
    private static bool _available;
    private static AssetBundle _bundle;
    private static readonly Dictionary<string, AudioClip> Clips = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<IntPtr> BundleClipPointers = [];

    internal static bool IsEnabled => OperatingSystem.IsWindows() && (Main.BgmUseAssetBundle?.Value ?? false);

    // 初回呼び出しで遅延初期化する。失敗したら以後ずっと false を返す (毎回再試行しない)。
    internal static bool TryGetClip(string name, out AudioClip clip)
    {
        clip = null;
        if (name == null) return false;
        if (!_initialized) Initialize();
        if (!_available) return false;

        if (!Clips.TryGetValue(name, out clip)) return false; // バンドルに無い名前はリロードしても無駄
        if (clip != null) return true;

        // fake-null: シーン遷移の UnloadUnusedAssets がクリップだけ回収した場合の再取得。
        // AssetBundle 本体のハンドルは生きている前提で LoadAllAssets だけやり直す。
        RefreshClips();
        return Clips.TryGetValue(name, out clip) && clip != null;
    }

    // 過去にリロードで置き換わった古いクリップ実体のポインタも Contains 判定に残したいので、
    // ここは追加専用にする (RefreshClips 側で Clear しない)。取りこぼすと Destroy 側 (float PCM
    // 経路) に誤って落ちて native リークになる。
    internal static bool IsBundleClip(AudioClip clip) => clip != null && BundleClipPointers.Contains(clip.Pointer);

    private static void Initialize()
    {
        _initialized = true;

        CleanupLegacyDiskExtraction();

        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            byte[] bytes = ReadEmbeddedBytes();
            if (bytes == null) { _available = false; return; }

            _bundle = AssetBundle.LoadFromMemory(bytes);
            if (_bundle == null)
            {
                Logger.Warn("BGM bundle: AssetBundle.LoadFromMemory failed", "BgmBundle");
                _available = false;
                return;
            }

            _available = RefreshClips();
            sw.Stop();

            if (_available)
                Logger.Info($"BGM bundle: loaded {Clips.Count} clips [{string.Join(",", Clips.Keys)}] from embedded resource ({sw.ElapsedMilliseconds}ms)", "BgmBundle");
            else
                Logger.Warn("BGM bundle: no AudioClip assets found in embedded resource", "BgmBundle");
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            _available = false;
        }
    }

    private static byte[] ReadEmbeddedBytes()
    {
        try
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null)
            {
                Logger.Warn("BGM bundle: embedded resource not found, bundle playback unavailable", "BgmBundle");
                return null;
            }

            using MemoryStream mem = new((int)stream.Length);
            stream.CopyTo(mem);
            return mem.ToArray();
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return null;
        }
    }

    // 旧バージョンがディスクへ抽出していた一時ファイルの掃除 (1 回きり・失敗は無視)。
    private static void CleanupLegacyDiskExtraction()
    {
        try
        {
            string mediaDir = Path.Combine(BepInEx.Paths.PluginPath, LegacyMediaSubDir);
            if (!Directory.Exists(mediaDir)) return;

            foreach (string stale in Directory.GetFiles(mediaDir, $"{LegacyBundleFilePrefix}*{LegacyBundleFileSuffix}"))
            {
                try { File.Delete(stale); } catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    // _bundle から AudioClip を列挙し直して Clips を作り直す (初期化と fake-null 再取得の共通処理)。
    // AssetBundle 本体は Unload しない前提で呼ぶ。
    private static bool RefreshClips()
    {
        if (_bundle == null) return false;

        try
        {
            var assets = _bundle.LoadAllAssets(Il2CppType.Of<AudioClip>());
            if (assets == null) return false;

            Clips.Clear();

            foreach (var o in assets)
            {
                AudioClip clip = o != null ? o.TryCast<AudioClip>() : null;
                if (clip == null) continue;

                clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                Clips[clip.name] = clip;
                BundleClipPointers.Add(clip.Pointer);
            }

            return Clips.Count > 0;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return false;
        }
    }
}
