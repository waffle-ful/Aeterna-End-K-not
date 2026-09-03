using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace EndKnot.Modules;

// 長尺の効果音 (WaveCannon の発射/チャージ・Backrooms ロビー環境音) を AssetBundle (Vorbis 圧縮のまま
// メモリ常駐) から読む経路。埋込 OGG/WAV → float PCM デコードだと 3 本で常駐 17MB になるが、
// 圧縮のままなら 1MB 弱。BGM 用の BgmBundle と同じ「埋込からメモリ直読み・ディスクへ書かない」方式で、
// バンドルは unity/BgmBundle/ の Assets/SFX から焼いて Resources/Sounds/endknot_sfx.bundle に埋め込む
// (tools/build-bgm-bundle.ps1)。効果音は短いので preloadAudioData=true で焼いてあり、取り出した
// クリップはそのまま鳴らせる (LoadAudioData 待ちは無い)。メインスレッド専用。
internal static class SfxBundle
{
    private const string EmbeddedResourceName = "EndKnot.Resources.Sounds.endknot_sfx.bundle";

    private static bool _initialized;
    private static bool _available;
    private static AssetBundle _bundle;
    private static readonly Dictionary<string, AudioClip> Clips = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<IntPtr> BundleClipPointers = [];

    internal static bool IsEnabled => OperatingSystem.IsWindows() && (Main.SfxUseAssetBundle?.Value ?? false);

    // 初回呼び出しで遅延初期化する。失敗したら以後ずっと false を返す (毎回再試行しない)。
    internal static bool TryGetClip(string name, out AudioClip clip)
    {
        clip = null;
        if (name == null) return false;
        if (!_initialized) Initialize();
        if (!_available) return false;

        if (!Clips.TryGetValue(name, out clip)) return false;
        if (clip != null) return true;

        // fake-null: シーン遷移の UnloadUnusedAssets がクリップだけ回収した場合の再取得。
        RefreshClips();
        return Clips.TryGetValue(name, out clip) && clip != null;
    }

    // バンドルに入っている音名の写し (裏スレッドのプリロード除外用にメインスレッドで取る)。
    internal static HashSet<string> ClipNamesSnapshot()
    {
        if (!_initialized) Initialize();
        return _available ? new HashSet<string>(Clips.Keys, StringComparer.OrdinalIgnoreCase) : [];
    }

    // 取り出したクリップは Destroy しない側に振り分けるための判定 (追加専用の集合)。
    internal static bool IsBundleClip(AudioClip clip) => clip != null && BundleClipPointers.Contains(clip.Pointer);

    private static void Initialize()
    {
        _initialized = true;

        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            byte[] bytes = ReadEmbeddedBytes();
            if (bytes == null) { _available = false; return; }

            _bundle = AssetBundle.LoadFromMemory(bytes);
            if (_bundle == null)
            {
                Logger.Warn("SFX bundle: AssetBundle.LoadFromMemory failed", "SfxBundle");
                _available = false;
                return;
            }

            _available = RefreshClips();
            sw.Stop();

            if (_available)
                Logger.Info($"SFX bundle: loaded {Clips.Count} clips [{string.Join(",", Clips.Keys)}] from embedded resource ({sw.ElapsedMilliseconds}ms)", "SfxBundle");
            else
                Logger.Warn("SFX bundle: no AudioClip assets found in embedded resource", "SfxBundle");
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
                Logger.Warn("SFX bundle: embedded resource not found, falling back to PCM decode", "SfxBundle");
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
