using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Modules;

// メインメニュー背景 (CalamityDusk) の PNG を AssetBundle (非圧縮 RGBA32) から読む経路。埋込 PNG の
// LoadImage デコードはメニュー表示中のフレームで主スレッドを 100ms 近く止めるため、スプラッシュ後半の
// 空き時間に bundle ごと非同期ロードしておき、メニュー構築時は出来上がったテクスチャを受け取るだけにする。
// 埋込からメモリ直読み — ディスクへは書き出さない。バンドルは unity/BgmBundle/ で焼いて
// Resources/Images/MainMenu/Dusk/endknot_dusk.bundle として埋め込む (tools/build-bgm-bundle.ps1)。
// AsyncOperation の完了は毎フレーム Tick で isDone を見て拾う (コールバックは登録しない)。
// メインスレッド専用。何か失敗したら以後は常に null を返し、呼び出し側が埋込 PNG へ落ちる。
internal static class DuskBundle
{
    private const string EmbeddedResourceName = "EndKnot.Resources.Images.MainMenu.Dusk.endknot_dusk.bundle";
    private const string AssetRoot = "assets/dusk/";

    // ENDKNOT_DUSK_BUNDLE=0: bundle 経路を完全に無効にし、常に埋込 PNG から読む。
    private static readonly bool EnvEnabled = Environment.GetEnvironmentVariable("ENDKNOT_DUSK_BUNDLE") != "0";

    private enum Stage { Idle, Creating, Loading, Done, Failed }

    private static Stage _stage = Stage.Idle;
    private static string _theme;
    private static long _startTicks;
    private static Il2CppStructArray<byte> _bytes;
    private static AssetBundleCreateRequest _createRequest;
    private static AssetBundle _bundle;
    private static readonly List<(string Name, AssetBundleRequest Request)> Requests = [];
    private static readonly Dictionary<string, Texture2D> Textures = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Sprite> Sprites = new(StringComparer.Ordinal);

    // 1 回だけ非同期ロードを始める。完了は Tick が拾う。
    internal static void Prewarm(string theme)
    {
        if (_stage != Stage.Idle) return;
        if (!EnvEnabled || string.IsNullOrEmpty(theme) || OperatingSystem.IsAndroid()) { _stage = Stage.Failed; return; }

        try
        {
            _theme = theme;
            BootTimeline.Mark("dusk.bundle.begin");
            _startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            _bytes = ReadEmbeddedBytes();
            if (_bytes == null) { Fail(); return; }

            _createRequest = AssetBundle.LoadFromMemoryAsync(_bytes);
            if (_createRequest == null) { Fail("LoadFromMemoryAsync returned null"); return; }

            _stage = Stage.Creating;
        }
        catch (Exception e)
        {
            Fail(e.Message);
        }
    }

    // 毎フレーム呼ぶ。進行が無い状態では何もしない。
    internal static void Tick()
    {
        if (_stage is not (Stage.Creating or Stage.Loading)) return;

        try
        {
            if (_stage == Stage.Creating)
            {
                if (!_createRequest.isDone) return;

                _bundle = _createRequest.assetBundle;
                _createRequest = null;
                if (_bundle == null) { Fail("bundle could not be created"); return; }

                string prefix = AssetRoot + _theme + "/";
                foreach (string assetName in _bundle.GetAllAssetNames())
                {
                    if (!assetName.StartsWith(prefix, StringComparison.Ordinal) || !assetName.EndsWith(".png", StringComparison.Ordinal)) continue;
                    Requests.Add((assetName, _bundle.LoadAssetAsync(assetName, Il2CppType.Of<Texture2D>())));
                }

                if (Requests.Count == 0) { Fail("no textures for theme " + _theme); return; }

                _stage = Stage.Loading;
                return;
            }

            foreach ((string _, AssetBundleRequest request) in Requests)
                if (!request.isDone) return;

            foreach ((string assetName, AssetBundleRequest request) in Requests)
            {
                Texture2D tex = request.asset != null ? request.asset.TryCast<Texture2D>() : null;
                if (tex == null) continue;

                tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor | HideFlags.DontUnloadUnusedAsset;
                Textures[_theme + "/" + Path.GetFileNameWithoutExtension(assetName)] = tex;
            }

            Requests.Clear();
            _bundle.Unload(false);
            _bundle = null;
            _bytes = null;

            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Logger.Info($"Dusk bundle: theme={_theme} textures={Textures.Count} ready in {ms:0}ms", "DuskBundle");
            _stage = Stage.Done;
            BootTimeline.Mark("dusk.bundle.ready");
        }
        catch (Exception e)
        {
            Fail(e.Message);
        }
    }

    // 完了済みならテクスチャ、未完了・不在・失敗なら null (待たない)。name は "dusk_" 付きの拡張子なし。
    internal static Texture2D TryGetTexture(string theme, string name)
    {
        if (_stage != Stage.Done) return null;
        return Textures.TryGetValue(theme + "/" + name, out Texture2D tex) && tex ? tex : null;
    }

    // テクスチャから作った Sprite をテーマ+名前で保持し、メニュー再訪のたびに作り直さない。
    internal static Sprite TryGetSprite(string theme, string name, float pixelsPerUnit)
    {
        string key = theme + "/" + name + "@" + pixelsPerUnit;
        if (Sprites.TryGetValue(key, out Sprite cached) && cached) return cached;

        Texture2D tex = TryGetTexture(theme, name);
        if (tex == null) return null;

        Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), pixelsPerUnit, 0, SpriteMeshType.FullRect);
        sprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
        return Sprites[key] = sprite;
    }

    private static void Fail(string reason = null)
    {
        _stage = Stage.Failed;
        _createRequest = null;
        _bytes = null;
        Requests.Clear();
        if (_bundle != null)
        {
            try { _bundle.Unload(false); } catch { /* best effort */ }
            _bundle = null;
        }

        if (reason != null) Logger.Warn($"Dusk bundle unavailable, using embedded PNG: {reason}", "DuskBundle");
    }

    private static unsafe Il2CppStructArray<byte> ReadEmbeddedBytes()
    {
        try
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null)
            {
                Logger.Info("Dusk bundle: embedded resource not found, using embedded PNG", "DuskBundle");
                return null;
            }

            int length = (int)stream.Length;
            var array = new Il2CppStructArray<byte>(length);
            byte* dst = (byte*)IntPtr.Add(array.Pointer, IntPtr.Size * 4).ToPointer();
            int read = 0;
            while (read < length)
            {
                int n = stream.Read(new Span<byte>(dst + read, length - read));
                if (n <= 0) break;
                read += n;
            }

            if (read != length) { Logger.Warn("Dusk bundle: embedded resource read was short", "DuskBundle"); return null; }
            return array;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return null;
        }
    }
}
