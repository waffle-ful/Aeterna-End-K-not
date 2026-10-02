using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Modules;

// Backrooms ロビーの床・壁テクスチャ (1024px PNG 2 枚) を AssetBundle (非圧縮 RGBA32) から読む経路。
// 埋込 PNG の LoadImage デコードは 1 枚 30ms 級で主スレッドを止めるため、入室の直前に bundle を
// 非同期ロードし、出来上がったテクスチャを受け取るだけにする。
// Android 版のエンジンには LoadFromMemoryAsync と GetAllAssetNames が無いので、bundle を開くところだけ
// 同期 (LoadFromMemory) にし、中身は名前を決め打ちして非同期で取り出す。
// 埋込からメモリ直読み — ディスクへは書き出さない。バンドルは unity/BgmBundle/ で焼いて
// Resources/Images/Backrooms/endknot_backrooms.bundle として埋め込む (tools/build-bgm-bundle.ps1)。
// AsyncOperation の完了は Poll を呼ぶたびに isDone を見て拾う (コールバックは登録しない)。
// メインスレッド専用。何か失敗したら以後は常に null を返し、呼び出し側が埋込 PNG へ落ちる。
internal static class BackroomsBundle
{
    private const string EmbeddedResourceName = "EndKnot.Resources.Images.Backrooms.endknot_backrooms.bundle";
    private const string AssetRoot = "assets/backrooms/";
    private static readonly string[] TextureNames = ["floor", "wall"];

    // 続けて待つのはここまで。超えたら呼び出し側は埋込 PNG で先へ進む (入室を待たせ続けない)。
    // 進行中のロードは止めず、bundle と元バイト列も持ったままにする (ロード中の解放はしない)。
    private const double MaxWaitMs = 3000.0;

    // これより長く Poll が呼ばれなかったら、待ちが途切れたとみなして計り直す (ロビーを抜けて戻った時)。
    private const double WaitGapMs = 1000.0;

    // ENDKNOT_BACKROOMS_BUNDLE=0: bundle 経路を完全に無効にし、常に埋込 PNG から読む。
    private static readonly bool EnvEnabled = Environment.GetEnvironmentVariable("ENDKNOT_BACKROOMS_BUNDLE") != "0";

    private enum Stage { Idle, Creating, Loading, Done, Failed }

    private static Stage _stage = Stage.Idle;
    private static long _startTicks;
    private static long _waitStartTicks;
    private static long _lastPollTicks;
    private static bool _waitExpired;
    private static Il2CppStructArray<byte> _bytes;
    private static AssetBundleCreateRequest _createRequest;
    private static AssetBundle _bundle;
    private static readonly List<(string Name, AssetBundleRequest Request)> Requests = [];
    private static readonly Dictionary<string, Texture2D> Textures = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Sprite> Sprites = new(StringComparer.Ordinal);

    // 1 回だけ非同期ロードを始める。完了は Poll が拾う。
    internal static void Begin()
    {
        if (_stage != Stage.Idle) return;
        if (!EnvEnabled) { _stage = Stage.Failed; return; }

        try
        {
            _startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            _bytes = ReadEmbeddedBytes();
            if (_bytes == null) { Fail(); return; }

            if (OperatingSystem.IsAndroid())
            {
                _bundle = AssetBundle.LoadFromMemory(_bytes);
                if (_bundle == null) { Fail("bundle could not be created"); return; }

                StartRequests();
                _stage = Stage.Loading;
                return;
            }

            _createRequest = AssetBundle.LoadFromMemoryAsync(_bytes);
            if (_createRequest == null) { Fail("LoadFromMemoryAsync returned null"); return; }

            _stage = Stage.Creating;
        }
        catch (Exception e)
        {
            Fail(e.Message);
        }
    }

    // 進行を 1 段拾い、呼び出し側がまだ待つべきなら true。毎フレーム呼ぶ。
    internal static bool Poll()
    {
        if (_stage is not (Stage.Creating or Stage.Loading)) return false;

        try
        {
            if (_stage == Stage.Creating)
            {
                if (!_createRequest.isDone) return StillWaiting();

                _bundle = _createRequest.assetBundle;
                _createRequest = null;
                if (_bundle == null) { Fail("bundle could not be created"); return false; }

                StartRequests();
                _stage = Stage.Loading;
                return StillWaiting();
            }

            foreach ((string _, AssetBundleRequest request) in Requests)
                if (!request.isDone) return StillWaiting();

            foreach ((string name, AssetBundleRequest request) in Requests)
            {
                Texture2D tex = request.asset != null ? request.asset.TryCast<Texture2D>() : null;
                if (tex == null) continue;

                tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor | HideFlags.DontUnloadUnusedAsset;
                Textures[name] = tex;
            }

            Requests.Clear();
            _bundle.Unload(false);
            _bundle = null;
            _bytes = null;

            Logger.Info($"Backrooms bundle: textures={Textures.Count} ready in {MsSince(_startTicks):0}ms", "BackroomsBundle");
            _stage = Stage.Done;
        }
        catch (Exception e)
        {
            Fail(e.Message);
        }

        return false;
    }

    private static void StartRequests()
    {
        foreach (string name in TextureNames)
            Requests.Add((name, _bundle.LoadAssetAsync(AssetRoot + name + ".png", Il2CppType.Of<Texture2D>())));
    }

    // 未完了の時に呼ぶ。続けて待った時間が上限を超えたら、以後は待たせない。
    private static bool StillWaiting()
    {
        if (_waitExpired) return false;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_waitStartTicks == 0 || MsBetween(_lastPollTicks, now) > WaitGapMs) _waitStartTicks = now;
        _lastPollTicks = now;

        if (MsBetween(_waitStartTicks, now) <= MaxWaitMs) return true;

        _waitExpired = true;
        Logger.Warn("Backrooms bundle is taking too long, using embedded PNG", "BackroomsBundle");
        return false;
    }

    // 完了済みならスプライト、未完了・不在・失敗なら null (待たない)。name は拡張子なしの小文字 ("floor" / "wall")。
    internal static Sprite TryGetSprite(string name, float pixelsPerUnit)
    {
        if (_stage != Stage.Done) return null;

        string key = name + "@" + pixelsPerUnit;
        if (Sprites.TryGetValue(key, out Sprite cached) && cached) return cached;

        if (!Textures.TryGetValue(name, out Texture2D tex) || !tex) return null;

        Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), pixelsPerUnit, 0, SpriteMeshType.FullRect);
        sprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
        return Sprites[key] = sprite;
    }

    private static double MsBetween(long from, long to) => (to - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static double MsSince(long from) => MsBetween(from, System.Diagnostics.Stopwatch.GetTimestamp());

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

        if (reason != null) Logger.Warn($"Backrooms bundle unavailable, using embedded PNG: {reason}", "BackroomsBundle");
    }

    private static unsafe Il2CppStructArray<byte> ReadEmbeddedBytes()
    {
        try
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null)
            {
                Logger.Info("Backrooms bundle: embedded resource not found, using embedded PNG", "BackroomsBundle");
                return null;
            }

            int length = (int)stream.Length;
            var array = new Il2CppStructArray<byte>((long)length);
            byte* dst = (byte*)IntPtr.Add(array.Pointer, IntPtr.Size * 4).ToPointer();
            int read = Utils.ReadStreamChunked(stream, dst, length);

            if (read != length) { Logger.Warn("Backrooms bundle: embedded resource read was short", "BackroomsBundle"); return null; }
            return array;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return null;
        }
    }
}
