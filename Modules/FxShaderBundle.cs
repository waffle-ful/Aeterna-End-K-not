using System;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace EndKnot.Modules;

// 自作シェーダ (加算合成) と、それで描く光の素材・粒の部品を AssetBundle endknot_fx から読む。
// バンドルは unity/FxBundle で焼いて Resources/Fx/endknot_fx.bundle として埋め込む (tools/build-fx-bundle.ps1)。
// 初回に同期で開き (60KB 程度)、取り出した資産は以後ずっと持つ。メインスレッド専用。
// 描くのは手元の画面だけなので、見えるのはこの mod を入れている人だけ。
internal static class FxShaderBundle
{
    private const string EmbeddedResourceName = "EndKnot.Resources.Fx.endknot_fx.bundle";
    private const string AssetRoot = "assets/fx/";

    private static bool _initialized;
    private static Material _additive;
    private static Texture2D _glow;
    private static GameObject _burst;
    private static Material _floorPool;
    private static Material _flowBeam;
    private static Sprite _glowSprite;
    private static readonly string[] ParticleNames = ["embers", "smoke", "ash", "sparks", "motes"];
    private static readonly GameObject[] Particles = new GameObject[5];

    internal static bool Available
    {
        get
        {
            if (!_initialized) Initialize();
            return _additive && _glow && _burst;
        }
    }

    // 演出の部品が使うマテリアル。バンドルが開けない時や古いバンドルでは null (呼ぶ側は標準の描き方に戻す)
    internal static Material Additive => Available ? _additive : null;
    internal static Material FloorPool => Available ? _floorPool : null;
    internal static Material FlowBeam => Available ? _flowBeam : null;


    // 粒のひな形 (FxParticles.Preset の順)。古いバンドルでは null
    internal static GameObject ParticlePreset(int index) => Available && index >= 0 && index < Particles.Length ? Particles[index] : null;

    // 演出の下ごしらえと同じ時に呼んで、最初に使う瞬間にバンドルを開く待ちが出ないようにする
    internal static void Warm()
    {
        if (!_initialized) Initialize();
    }

    private static void Initialize()
    {
        _initialized = true;

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AssetBundle bundle = EmbeddedBundleFile.TryOpen(EmbeddedResourceName);

            if (bundle == null)
            {
                byte[] bytes = ReadEmbeddedBytes();
                if (bytes == null) return;

                bundle = AssetBundle.LoadFromMemory(bytes);
            }

            if (bundle == null)
            {
                Logger.Warn("FX bundle: could not be opened", "FxShaderBundle");
                return;
            }

            _additive = Keep(Load(bundle, "additive.mat", Il2CppType.Of<Material>())?.TryCast<Material>());
            _glow = Keep(Load(bundle, "glow.png", Il2CppType.Of<Texture2D>())?.TryCast<Texture2D>());
            _burst = Keep(Load(bundle, "burst.prefab", Il2CppType.Of<GameObject>())?.TryCast<GameObject>());
            _floorPool = Keep(Load(bundle, "floorpool.mat", Il2CppType.Of<Material>())?.TryCast<Material>());
            _flowBeam = Keep(Load(bundle, "flowbeam.mat", Il2CppType.Of<Material>())?.TryCast<Material>());
            for (int i = 0; i < ParticleNames.Length; i++)
                Particles[i] = Keep(Load(bundle, "p_" + ParticleNames[i] + ".prefab", Il2CppType.Of<GameObject>())?.TryCast<GameObject>());
            bundle.Unload(false);

            Shader shader = _additive ? _additive.shader : null;
            Logger.Info($"FX bundle: material={(bool)_additive} glow={(bool)_glow} burst={(bool)_burst} pool={(bool)_floorPool} beam={(bool)_flowBeam} particles={(bool)Particles[0]} shader={(shader ? shader.name : "null")} supported={(shader && shader.isSupported)} in {sw.ElapsedMilliseconds}ms", "FxShaderBundle");
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
        }
    }

    // Android 版のエンジンには同期の LoadAsset が無いので、どの環境でも非同期で頼んで結果をその場で受け取る
    // (完了前に asset を読むと、読み込みが終わるまでその場で待つ)。
    private static Object Load(AssetBundle bundle, string name, Il2CppSystem.Type type)
    {
        AssetBundleRequest request = bundle.LoadAssetAsync(AssetRoot + name, type);
        return request?.asset;
    }

    private static T Keep<T>(T asset) where T : Object
    {
        if (asset) asset.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
        return asset;
    }

    // 実行中にマテリアルの係数を変える (見た目の調整用・焼き直さずに実機で詰める)。value 省略で今の値を返す
    internal static string Tune(string which, string prop, float? value)
    {
        if (!Available) return "ERR fxmat: bundle unavailable";

        Material mat = which switch
        {
            "pool" => _floorPool,
            "beam" => _flowBeam,
            "add" => _additive,
            _ => null
        };
        if (!mat) return "ERR fxmat: which = pool|beam|add";
        if (!mat.HasProperty(prop)) return $"ERR fxmat: {which} has no {prop}";

        if (value.HasValue) mat.SetFloat(prop, value.Value);
        return $"OK fxmat {which} {prop}={mat.GetFloat(prop):0.###}";
    }

    // 同じ光の円を 3 列に並べて、描き方の違いを見比べる。左 = 標準の半透明 / 中 = 加算シェーダ / 右 = 粒。
    // 左と中は赤・緑・青の円を少しずつずらして重ねる (加算なら重なりが白く光り、半透明なら上の色で隠れる)。
    // vision=true なら影の板 (HudManager.ShadowQuad) と同じ並び順に置き、z で板の奥に入れる (視界の外では人と同じく影に隠れる)。
    // false なら並び順 150〜 で影の板より手前に描く (視界に関係なく見える)。
    // lifetime 秒後に全部消える。結果の 1 行を返す。
    internal static string ShowComparison(Vector2 at, float lifetime, bool vision, float z = 0f, bool tintShadow = false)
    {
        if (!Available) return "ERR fxshader: bundle unavailable";

        if (!_glowSprite)
        {
            _glowSprite = Sprite.Create(_glow, new Rect(0, 0, _glow.width, _glow.height), new Vector2(0.5f, 0.5f), _glow.width, 0, SpriteMeshType.FullRect);
            _glowSprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
        }

        MeshRenderer shadow = HudManager.InstanceExists ? HudManager.Instance.ShadowQuad : null;
        int layerId = vision && shadow ? shadow.sortingLayerID : 0;
        int baseOrder = vision ? shadow ? shadow.sortingOrder : 0 : 150;

        var root = new GameObject("FxShaderComparison") { layer = 0 };
        root.transform.position = new Vector3(at.x, at.y, z);

        Color[] colors = [new(1f, 0.2f, 0.2f, 0.9f), new(0.2f, 1f, 0.2f, 0.9f), new(0.3f, 0.4f, 1f, 0.9f)];
        Vector2[] offsets = [new(0f, 0.32f), new(-0.28f, -0.16f), new(0.28f, -0.16f)];

        for (int column = 0; column < 2; column++)
        {
            for (int i = 0; i < 3; i++)
            {
                var go = new GameObject("glow") { layer = 0 };
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = new Vector3((column - 1) * 2.4f + offsets[i].x, offsets[i].y, 0f);
                go.transform.localScale = new Vector3(1.6f, 1.6f, 1f);

                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = _glowSprite;
                sr.color = colors[i];
                sr.sortingLayerID = layerId;
                sr.sortingOrder = baseOrder + (vision ? 0 : i);
                if (column == 1) sr.sharedMaterial = _additive;
            }
        }

        GameObject burst = Object.Instantiate(_burst, root.transform, false);
        burst.layer = 0;
        burst.transform.localPosition = new Vector3(2.4f, -0.3f, 0f);
        var psr = burst.GetComponent<ParticleSystemRenderer>();
        if (psr)
        {
            psr.sortingLayerID = layerId;
            psr.sortingOrder = vision ? baseOrder : 160;
        }

        ParticleSystem ps = burst.GetComponent<ParticleSystem>();
        if (ps) ps.Play();

        Object.Destroy(root, lifetime);

        // 影の範囲を見分けるため、表示中だけ影の色を赤く塗る (塗った所が影)。
        if (tintShadow && shadow)
        {
            Material sm = shadow.material;
            Color orig = sm.GetColor("_Color");
            sm.SetColor("_Color", new Color(1f, 0.1f, 0.1f, orig.a));
            LateTask.New(() => { if (sm) sm.SetColor("_Color", orig); }, lifetime, "FxShaderTint");
        }

        Shader shader = _additive.shader;
        string shadowInfo = shadow ? $"shadowQuad(z={shadow.transform.position.z:0.##} layer={shadow.sortingLayerID} order={shadow.sortingOrder} queue={shadow.sharedMaterial.renderQueue})" : "shadowQuad=null";
        return $"OK fxshader mode={(vision ? "vision" : "global")} z={z:0.##} {shadowInfo} at={at.x:0.00},{at.y:0.00} shader={shader.name} supported={shader.isSupported} particles={(ps ? ps.particleCount : -1)} playing={(ps && ps.isPlaying)}";
    }

    private static byte[] ReadEmbeddedBytes()
    {
        try
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null)
            {
                Logger.Info("FX bundle: embedded resource not found", "FxShaderBundle");
                return null;
            }

            using MemoryStream ms = new();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return null;
        }
    }
}
