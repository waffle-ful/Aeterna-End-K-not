using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules;

// 粒の部品: バンドルの粒のひな形 (火の粉 / 煙 / 灰 / 火花 / 漂う光) を種類ごとに 1 つずつ置いておき、
// 撒く直前に位置・色・大きさ・速さ・撒く範囲を差し替えて Emit する。粒は World 空間で動くので、
// 撒いた後に位置を変えても前の粒は動かない (1 つの粒の箱で、別々の場所・色の粒を何度でも撒ける)。
// 粒の動きはゲーム本体が計算するので、毎フレームの処理は予約の確認だけ。
// 揺らぎ・大きさや色の変化はゲーム側から変えられないので、ひな形に焼き込んである (FxBundleBuilder)。
// 描くのは手元の画面だけ。メインスレッド専用。
internal static class FxParticles
{
    internal enum Preset : byte
    {
        Embers,
        Smoke,
        Ash,
        Sparks,
        Motes
    }

    private const int PresetCount = 5;
    private const int MaxScheduled = 512;

    private sealed class Sys
    {
        public GameObject Go;
        public Transform Tf;
        public ParticleSystem Ps;
        public ParticleSystemRenderer Renderer;
        public ParticleSystem.MainModule Main;
    }

    private struct Job
    {
        public float At;
        public Preset Preset;
        public Vector2 Pos;
        public int Count;
        public Color C0, C1;
        public float Size, Speed, Spread, Z, Angle;
        public bool Vision;
        public int Order;
    }

    // [種類 × 2]: 0 = 影より手前 (視界に関係なく見える) / 1 = 視界の外では影に隠れる置き方
    private static readonly Sys[] Systems = new Sys[PresetCount * 2];
    private static readonly List<Job> Scheduled = [];
    private static bool _anyLive;
    private const int MaxFailures = 3;
    private static readonly int[] Failures = new int[PresetCount];

    internal static bool Available => FxShaderBundle.ParticlePreset(0);

    // 粒を撒く。delay 秒後に撒く予約もできる (会議・試合の終わりで取り消される)。
    // size / speed は粒の大きさと速さの倍率、spread は撒く範囲の倍率。
    // angle は撒く向き (度・右が 0 で反時計回り)。扇の形で撒くひな形 (灰) だけに効き、円で撒くものは向きに関係ない。
    // vision = true なら影の板と同じ層に置き、z で人との前後を決める (視界の外では影に隠れる)
    internal static void Emit(Preset preset, Vector2 pos, int count, Color c0, Color c1, float size = 1f, float speed = 1f, float spread = 1f, float delay = 0f,
                              bool vision = false, int order = 150, float z = 0f, float angle = 0f)
    {
        if (count <= 0) return;

        var job = new Job { At = Time.time + delay, Preset = preset, Pos = pos, Count = count, C0 = c0, C1 = c1, Size = size, Speed = speed, Spread = spread, Vision = vision, Order = order, Z = z, Angle = angle };

        if (delay <= 0f) Run(job);
        else if (Scheduled.Count < MaxScheduled) Scheduled.Add(job);
    }

    // 毎フレーム呼ぶ。予約の時刻が来たものを撒く
    internal static void Tick()
    {
        if (Scheduled.Count == 0) return;

        float now = Time.time;
        for (int i = Scheduled.Count - 1; i >= 0; i--)
        {
            if (Scheduled[i].At > now) continue;

            Job job = Scheduled[i];
            Scheduled.RemoveAt(i);
            Run(job);
        }
    }

    // 予約と、出ている粒を全部消す
    internal static void ClearAll()
    {
        Scheduled.Clear();
        System.Array.Clear(Failures, 0, Failures.Length);
        if (!_anyLive) return;

        _anyLive = false;
        foreach (Sys s in Systems)
        {
            if (s == null || !s.Ps) continue;

            s.Ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            s.Ps.Play();
        }
    }

    private static void Run(Job job)
    {
        if (Failures[(int)job.Preset] >= MaxFailures) return;

        try
        {
            Sys s = Get(job.Preset, job.Vision);
            if (s == null) return;

            if (job.Vision)
            {
                MeshRenderer shadow = HudManager.InstanceExists ? HudManager.Instance.ShadowQuad : null;
                if (shadow)
                {
                    s.Renderer.sortingLayerID = shadow.sortingLayerID;
                    s.Renderer.sortingOrder = shadow.sortingOrder;
                }
            }
            else s.Renderer.sortingOrder = job.Order;

            s.Tf.position = new Vector3(job.Pos.x, job.Pos.y, job.Z);
            s.Tf.localScale = new Vector3(job.Spread, job.Spread, 1f);
            // 扇は右向き (0°) から反時計回りに開くので、扇の真ん中が angle を向くように回す
            if (Arc(job.Preset) < 360f) s.Tf.rotation = FxMath.RotZ(job.Angle - Arc(job.Preset) * 0.5f);
            s.Main.startSizeMultiplier = job.Size;
            s.Main.startSpeedMultiplier = job.Speed;

            // 2 色は半分ずつ撒き分ける (ゲーム側からは 1 色の指定しか効かない)
            bool same = job.C0.r == job.C1.r && job.C0.g == job.C1.g && job.C0.b == job.C1.b && job.C0.a == job.C1.a;
            int first = same ? job.Count : (job.Count + 1) / 2;
            s.Main.startColor = new ParticleSystem.MinMaxGradient(job.C0);
            s.Ps.Emit(first);
            if (first < job.Count)
            {
                s.Main.startColor = new ParticleSystem.MinMaxGradient(job.C1);
                s.Ps.Emit(job.Count - first);
            }
            _anyLive = true;
        }
        catch (System.Exception e)
        {
            // 壊れた箱は捨てて、次に使う時に作り直す。同じ壊れ方が続く種類は、この試合の間は撒かない
            int index = (int)job.Preset * 2 + (job.Vision ? 1 : 0);
            Sys broken = Systems[index];
            if (broken != null && broken.Go) Object.Destroy(broken.Go);
            Systems[index] = null;
            if (++Failures[(int)job.Preset] == MaxFailures) Utils.ThrowException(e);
            else if (Failures[(int)job.Preset] < MaxFailures) Logger.Warn($"FxParticles {job.Preset}: {e.Message}", "FxParticles");
        }
    }

    // 今の状態 (確認用)
    internal static string Describe(Preset preset, bool vision)
    {
        Sys s = Systems[(int)preset * 2 + (vision ? 1 : 0)];
        if (s == null || !s.Ps) return "none";

        Vector3 p = s.Tf.position;
        return $"count={s.Ps.particleCount} playing={s.Ps.isPlaying} pos={p.x:0.00},{p.y:0.00},{p.z:0.00} layer={s.Renderer.sortingLayerID} order={s.Renderer.sortingOrder} active={s.Go.activeInHierarchy} mat={(s.Renderer.sharedMaterial ? s.Renderer.sharedMaterial.shader.name : "null")}";
    }

    // ひな形の扇の開き (FxBundleBuilder の値と揃える)
    private static float Arc(Preset preset) => preset == Preset.Ash ? 70f : 360f;

    private static Sys Get(Preset preset, bool vision)
    {
        int index = (int)preset * 2 + (vision ? 1 : 0);
        Sys s = Systems[index];
        if (s != null && s.Ps) return s;

        GameObject prefab = FxShaderBundle.ParticlePreset((int)preset);
        if (!prefab) return null;

        GameObject go = Object.Instantiate(prefab);
        go.name = "FxParticles";
        go.layer = 0;
        Object.DontDestroyOnLoad(go);

        ParticleSystem ps = go.GetComponent<ParticleSystem>();
        if (!ps)
        {
            Object.Destroy(go);
            return null;
        }

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        if (!renderer)
        {
            Object.Destroy(go);
            return null;
        }

        s = new Sys { Go = go, Tf = go.transform, Ps = ps, Renderer = renderer, Main = ps.main };

        // 放出 0 で再生し続けておく (止まっていると撒いた粒が動かない)
        ps.Play();
        Systems[index] = s;
        return s;
    }
}
