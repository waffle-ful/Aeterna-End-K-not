using System.IO;
using UnityEditor;
using UnityEngine;

// 演出用 AssetBundle endknot_fx のビルド入口 (tools/build-fx-bundle.ps1 から Unity バッチモードで呼ばれる)。
// 毎回 Assets/Fx に素材を作り直してから焼く:
//   glow.png      … 白地に中心から縁へ滑らかに消える円 (128px・非圧縮)
//   additive.mat  … EndKnot/Additive シェーダ + glow
//   burst.prefab  … additive.mat で描く噴き上がる粒 (ループ再生)
//   noise.png     … 継ぎ目なく敷き詰められる雲状のノイズ (128px・繰り返し)。下の 2 枚のシェーダが流して使う
//   floorpool.mat … EndKnot/FloorPool (縁のない揺らめく床の光だまり)
//   flowbeam.mat  … EndKnot/FlowBeam (根元から先へ筋が流れる光の帯)
// シェーダはマテリアルから参照されるのでバンドルに一緒に入る。ターゲットごとに描画 API 向けへ変換される
// (Windows = Direct3D11、Android = GLES3 / Vulkan)。
public static class FxBundleBuilder
{
    private const string Folder = "Assets/Fx";
    private const string BundleName = "endknot_fx";
    private const string ShaderPath = "Assets/Shaders/EKAdditive.shader";
    private const int GlowSize = 128;
    private const int NoiseSize = 128;

    public static void Build() => BuildFor(BuildTarget.StandaloneWindows64, "Build");

    public static void BuildAndroid() => BuildFor(BuildTarget.Android, Path.Combine("Build", "android"));

    private static void BuildFor(BuildTarget target, string outSubDir)
    {
        if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "Fx");

        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        if (shader == null)
        {
            Debug.LogError("FxBundleBuilder: shader not found at " + ShaderPath);
            EditorApplication.Exit(2);
            return;
        }

        Shader pool = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/EKFloorPool.shader");
        Shader beam = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/EKFlowBeam.shader");
        if (pool == null || beam == null)
        {
            Debug.LogError("FxBundleBuilder: FloorPool / FlowBeam shader not found");
            EditorApplication.Exit(2);
            return;
        }

        Tag(ShaderPath);
        Texture2D glow = MakeGlow();
        Material mat = MakeMaterial(shader, glow);
        MakeBurst(mat);

        Texture2D noise = MakeNoise();
        MakeNoiseMaterial(pool, noise, "floorpool.mat");
        MakeNoiseMaterial(beam, noise, "flowbeam.mat");

        string outDir = Path.Combine(Directory.GetCurrentDirectory(), outSubDir);
        Directory.CreateDirectory(outDir);

        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(outDir, BuildAssetBundleOptions.ChunkBasedCompression, target);
        if (manifest == null)
        {
            Debug.LogError("FxBundleBuilder: BuildAssetBundles returned null");
            EditorApplication.Exit(2);
            return;
        }

        Debug.Log($"FxBundleBuilder: built [{string.Join(",", manifest.GetAllAssetBundles())}] target={target} assets=[{string.Join(",", AssetDatabase.GetAssetPathsFromAssetBundle(BundleName))}]");
    }

    private static Texture2D MakeGlow()
    {
        string path = Folder + "/glow.png";
        var tex = new Texture2D(GlowSize, GlowSize, TextureFormat.RGBA32, false);
        float half = GlowSize * 0.5f;

        for (int y = 0; y < GlowSize; y++)
        for (int x = 0; x < GlowSize; x++)
        {
            float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Clamp01(1f - r);
            a = a * a * (3f - 2f * a);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }

        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Default;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.isReadable = false;
        importer.assetBundleName = BundleName;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // 格子の大きさが画像の幅を割り切る値ノイズを 4 段重ねる (格子の端を折り返すので上下左右が繋がる)
    private static Texture2D MakeNoise()
    {
        string path = Folder + "/noise.png";
        var tex = new Texture2D(NoiseSize, NoiseSize, TextureFormat.RGBA32, false);
        var rnd = new System.Random(20261004);
        int[] cells = { 4, 8, 16, 32 };
        float[] weights = { 0.5f, 0.27f, 0.15f, 0.08f };
        var grids = new float[cells.Length][];
        for (int o = 0; o < cells.Length; o++)
        {
            grids[o] = new float[cells[o] * cells[o]];
            for (int k = 0; k < grids[o].Length; k++) grids[o][k] = (float)rnd.NextDouble();
        }

        for (int y = 0; y < NoiseSize; y++)
        for (int x = 0; x < NoiseSize; x++)
        {
            float v = 0f;
            for (int o = 0; o < cells.Length; o++)
            {
                int n = cells[o];
                float fx = x * n / (float)NoiseSize, fy = y * n / (float)NoiseSize;
                int x0 = (int)fx, y0 = (int)fy;
                float tx = fx - x0, ty = fy - y0;
                tx = tx * tx * (3f - 2f * tx);
                ty = ty * ty * (3f - 2f * ty);
                float[] g = grids[o];
                float a = g[y0 % n * n + x0 % n], b = g[y0 % n * n + (x0 + 1) % n];
                float c = g[(y0 + 1) % n * n + x0 % n], d = g[(y0 + 1) % n * n + (x0 + 1) % n];
                v += weights[o] * Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
            }

            // 中間を広げて明暗をはっきりさせる
            v = Mathf.Clamp01((v - 0.5f) * 1.8f + 0.5f);
            tex.SetPixel(x, y, new Color(v, v, v, 1f));
        }

        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = false;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.filterMode = FilterMode.Bilinear;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.isReadable = false;
        importer.assetBundleName = BundleName;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    private static void MakeNoiseMaterial(Shader shader, Texture2D noise, string name)
    {
        string path = Folder + "/" + name;
        AssetDatabase.DeleteAsset(path);

        var mat = new Material(shader);
        mat.SetTexture("_Noise", noise);
        AssetDatabase.CreateAsset(mat, path);
        Tag(path);
    }

    private static Material MakeMaterial(Shader shader, Texture2D glow)
    {
        string path = Folder + "/additive.mat";
        AssetDatabase.DeleteAsset(path);

        var mat = new Material(shader) { mainTexture = glow };
        AssetDatabase.CreateAsset(mat, path);
        Tag(path);
        return AssetDatabase.LoadAssetAtPath<Material>(path);
    }

    private static void MakeBurst(Material mat)
    {
        string path = Folder + "/burst.prefab";
        AssetDatabase.DeleteAsset(path);

        var go = new GameObject("burst");
        ParticleSystem ps = go.AddComponent<ParticleSystem>();

        ParticleSystem.MainModule main = ps.main;
        main.duration = 1f;
        main.loop = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.4f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.25f, 0.6f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.55f, 0.15f, 1f), new Color(0.2f, 0.8f, 1f, 1f));
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = -0.15f;
        main.maxParticles = 200;
        main.playOnAwake = true;

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.rateOverTime = 70f;

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = 0.25f;
        shape.rotation = Vector3.zero;

        ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
        col.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(fade);

        ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.3f));

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = mat;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sortingOrder = 160;

        PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
        Tag(path);
    }

    private static void Tag(string path)
    {
        AssetImporter importer = AssetImporter.GetAtPath(path);
        if (importer == null || importer.assetBundleName == BundleName) return;

        importer.assetBundleName = BundleName;
        importer.SaveAndReimport();
    }
}
