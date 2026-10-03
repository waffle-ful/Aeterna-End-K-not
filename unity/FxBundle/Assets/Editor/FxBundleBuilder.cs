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
//   puff.png / flake.png … 煙のかたまり (ノイズで縁が崩れた円) / 灰の欠片 (いびつな多角形)
//   p_*.prefab    … EndKnot/Particle で描く粒のひな形 5 種 (embers 火の粉 / smoke 煙 / ash 灰 / sparks 火花 / motes 漂う光)。
//                   自分では撒かず (放出 0)、使う側が位置・色・大きさ・速さの倍率を決めてから Emit で撒く。
//                   揺らぎや大きさ・色の変化はゲーム側から設定できない部分なので、ここで焼き込む
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

        Shader particle = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/EKParticle.shader");
        if (particle == null)
        {
            Debug.LogError("FxBundleBuilder: Particle shader not found");
            EditorApplication.Exit(2);
            return;
        }

        Material pGlow = MakeParticleMaterial(particle, glow, "p_glow.mat", 0.35f);
        Material pSmoke = MakeParticleMaterial(particle, MakeAlphaTexture("puff.png", 128, PuffAlpha), "p_smoke.mat", 1f);
        Material pAsh = MakeParticleMaterial(particle, MakeAlphaTexture("flake.png", 64, FlakeAlpha), "p_ash.mat", 1f);
        MakeParticlePresets(pGlow, pSmoke, pAsh);

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

    private static Material MakeParticleMaterial(Shader shader, Texture2D tex, string name, float cover)
    {
        string path = Folder + "/" + name;
        AssetDatabase.DeleteAsset(path);

        var mat = new Material(shader) { mainTexture = tex };
        mat.SetFloat("_Cover", cover);
        AssetDatabase.CreateAsset(mat, path);
        Tag(path);
        return AssetDatabase.LoadAssetAtPath<Material>(path);
    }

    private static Texture2D MakeAlphaTexture(string name, int size, System.Func<float, float, float> alpha)
    {
        string path = Folder + "/" + name;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float half = size * 0.5f;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(alpha((x + 0.5f - half) / half, (y + 0.5f - half) / half))));

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

    // 縁が角度ごとに出入りする円 (煙のかたまり)。中は少し斑にする
    private static float PuffAlpha(float x, float y)
    {
        float r = Mathf.Sqrt(x * x + y * y);
        float ang = Mathf.Atan2(y, x);
        float edge = 0.78f + 0.1f * Mathf.Sin(ang * 3f + 0.7f) + 0.07f * Mathf.Sin(ang * 5f + 2.1f) + 0.04f * Mathf.Sin(ang * 9f);
        float a = Mathf.Clamp01((edge - r) / edge);
        a = a * a * (3f - 2f * a);
        float mottle = 0.8f + 0.2f * Mathf.Sin(x * 7.3f + 1.1f) * Mathf.Sin(y * 6.1f + 0.4f);
        return a * mottle;
    }

    // いびつな 5 角形の欠片 (縁だけ少しぼかす)
    private static float FlakeAlpha(float x, float y)
    {
        float r = Mathf.Sqrt(x * x + y * y);
        float ang = Mathf.Atan2(y, x);
        float edge = 0.62f + 0.22f * Mathf.Sin(ang * 2f + 0.5f) + 0.12f * Mathf.Sin(ang * 5f + 1.7f);
        return Mathf.Clamp01((edge - r) * 6f);
    }

    private sealed class Preset
    {
        public string Name;
        public Material Mat;
        public Vector2 Life, Speed, Size;
        public float Gravity, Spin, NoiseStrength, NoiseFreq, NoiseSize, Drag, Grow0 = 1f, Grow1 = 0.2f, FadeIn = 0.1f, FadeOutFrom = 0.5f;
        public int Max = 400;
        public ParticleSystemShapeType ShapeType = ParticleSystemShapeType.Circle;
        public Vector3 BoxSize = new Vector3(0.6f, 0.25f, 0f);
        public float Radius = 0.3f;
        public float Arc = 360f;
        public bool Stretch, RandomRotation;
    }

    private static void MakeParticlePresets(Material glow, Material smoke, Material ash)
    {
        // 火の粉: 揺らぎながら舞い上がり、瞬いて消える
        MakePreset(new Preset { Name = "embers", Mat = glow, Life = new Vector2(0.9f, 1.8f), Speed = new Vector2(0.3f, 1.2f), Size = new Vector2(0.06f, 0.14f), Gravity = -0.25f,
            NoiseStrength = 0.6f, NoiseFreq = 1.2f, NoiseSize = 0.7f, Grow0 = 1f, Grow1 = 0.2f, FadeIn = 0.08f, FadeOutFrom = 0.45f, Radius = 0.3f, Max = 500 });
        // 煙: 回りながらふくらみ、巻き込むように流れる
        MakePreset(new Preset { Name = "smoke", Mat = smoke, Life = new Vector2(1.2f, 2.2f), Speed = new Vector2(0.2f, 0.7f), Size = new Vector2(0.5f, 1f), Gravity = -0.05f, Spin = 40f,
            NoiseStrength = 0.4f, NoiseFreq = 0.5f, Grow0 = 0.6f, Grow1 = 1.6f, FadeIn = 0.15f, FadeOutFrom = 0.35f, Radius = 0.35f, Max = 300, RandomRotation = true });
        // 灰: 細かい欠片が回りながら横へ流れて昇る (色は使う側が決める)
        // 撒く向きは 70° の扇 (使う側が回して風下へ向ける)
        MakePreset(new Preset { Name = "ash", Mat = ash, Life = new Vector2(1.4f, 2.6f), Speed = new Vector2(0.6f, 1.8f), Size = new Vector2(0.06f, 0.15f), Gravity = -0.1f, Spin = 360f,
            NoiseStrength = 0.9f, NoiseFreq = 0.8f, Grow0 = 1f, Grow1 = 0.6f, FadeIn = 0.02f, FadeOutFrom = 0.65f, Max = 4000, RandomRotation = true, Radius = 0.35f, Arc = 70f });
        // 火花: 速く弾けて減速し、進む向きに伸びる
        MakePreset(new Preset { Name = "sparks", Mat = glow, Life = new Vector2(0.25f, 0.6f), Speed = new Vector2(3f, 7f), Size = new Vector2(0.04f, 0.08f), Gravity = 0.3f, Drag = 3f,
            Grow0 = 1f, Grow1 = 0.4f, FadeIn = 0.01f, FadeOutFrom = 0.3f, Radius = 0.05f, Max = 500, Stretch = true });
        // 漂う光: ゆっくり漂って瞬く
        MakePreset(new Preset { Name = "motes", Mat = glow, Life = new Vector2(1.5f, 2.5f), Speed = new Vector2(0.05f, 0.3f), Size = new Vector2(0.05f, 0.12f), Gravity = -0.03f,
            NoiseStrength = 0.3f, NoiseFreq = 0.4f, NoiseSize = 0.8f, Grow0 = 1f, Grow1 = 0.6f, FadeIn = 0.25f, FadeOutFrom = 0.4f, Radius = 1f, Max = 400 });
    }

    private static ParticleSystem.MinMaxCurve FlatRange(float min, float max) =>
        new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Constant(0f, 1f, min), AnimationCurve.Constant(0f, 1f, max));

    private static void MakePreset(Preset p)
    {
        string path = Folder + "/p_" + p.Name + ".prefab";
        AssetDatabase.DeleteAsset(path);

        var go = new GameObject("p_" + p.Name);
        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = ps.main;
        main.duration = 1f;
        main.loop = true;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(p.Life.x, p.Life.y);
        // 大きさと速さは「2 本の平らな曲線の間」で持つ。この形だと倍率 (startSizeMultiplier 等) が両端に同じだけ効くので、
        // 使う側は倍率だけで大きさ・速さを変えられる (ゲーム側から触れるのは倍率だけ)
        main.startSpeed = FlatRange(p.Speed.x, p.Speed.y);
        main.startSize = FlatRange(p.Size.x, p.Size.y);
        main.startRotation = p.RandomRotation ? new ParticleSystem.MinMaxCurve(0f, 2f * Mathf.PI) : new ParticleSystem.MinMaxCurve(0f);
        main.startColor = Color.white;
        main.gravityModifier = p.Gravity;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        // 拡縮は撒く範囲だけに効かせる (World 空間なので、撒き終わった粒は後から動かない)
        main.scalingMode = ParticleSystemScalingMode.Shape;
        main.maxParticles = p.Max;

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.rateOverTime = 0f;

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.shapeType = p.ShapeType;
        shape.radius = p.Radius;
        shape.radiusThickness = 1f;
        shape.arc = p.Arc;
        shape.scale = p.ShapeType == ParticleSystemShapeType.Box ? p.BoxSize : Vector3.one;
        shape.rotation = Vector3.zero;

        ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
        col.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, p.FadeIn), new GradientAlphaKey(1f, p.FadeOutFrom), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(fade);

        ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, p.Grow0, 1f, p.Grow1));

        if (p.Spin > 0f)
        {
            ParticleSystem.RotationOverLifetimeModule rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-p.Spin * Mathf.Deg2Rad, p.Spin * Mathf.Deg2Rad);
        }

        if (p.NoiseStrength > 0f)
        {
            ParticleSystem.NoiseModule noise = ps.noise;
            noise.enabled = true;
            noise.quality = ParticleSystemNoiseQuality.Medium;
            noise.strength = p.NoiseStrength;
            noise.frequency = p.NoiseFreq;
            noise.scrollSpeed = 0.4f;
            noise.damping = true;
            if (p.NoiseSize > 0f) noise.sizeAmount = p.NoiseSize;
        }

        if (p.Drag > 0f)
        {
            ParticleSystem.LimitVelocityOverLifetimeModule lim = ps.limitVelocityOverLifetime;
            lim.enabled = true;
            lim.drag = p.Drag;
            lim.multiplyDragByParticleSize = false;
            lim.multiplyDragByParticleVelocity = true;
        }

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = p.Mat;
        renderer.renderMode = p.Stretch ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
        if (p.Stretch)
        {
            renderer.lengthScale = 2f;
            renderer.velocityScale = 0.06f;
        }

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
