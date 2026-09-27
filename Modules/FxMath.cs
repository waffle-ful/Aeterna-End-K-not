using UnityEngine;

namespace EndKnot.Modules;

// 毎フレーム回す演出用の算術。UnityEngine の Mathf / Random / Vector・Color のコンストラクタや演算子は
// interop 越しに il2cpp_runtime_invoke を通り、戻り値の箱がゲーム本体側のヒープに 1 回ごとに積まれる
// (数百回/フレームで GC が目に見えて増える)。ここの関数は全部 managed で完結し、Unity の構造体はフィールド直書きで組む。
// 意味は同名の Unity 関数に合わせてある (Range(int,int) は上限を含まない等)。
public static class FxMath
{
    public const float PI = System.MathF.PI;
    public const float Rad2Deg = 180f / System.MathF.PI;
    public const float Deg2Rad = System.MathF.PI / 180f;

    private static readonly System.Random Rng = new();

    public static float Sqrt(float v) => System.MathF.Sqrt(v);
    public static float Sin(float v) => System.MathF.Sin(v);
    public static float Cos(float v) => System.MathF.Cos(v);
    public static float Exp(float v) => System.MathF.Exp(v);
    public static float Pow(float a, float b) => System.MathF.Pow(a, b);
    public static float Atan2(float y, float x) => System.MathF.Atan2(y, x);
    public static float Abs(float v) => v < 0f ? -v : v;
    public static float Min(float a, float b) => a < b ? a : b;
    public static float Max(float a, float b) => a > b ? a : b;
    public static int Min(int a, int b) => a < b ? a : b;
    public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
    public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

    public static float MoveTowards(float current, float target, float maxDelta)
    {
        float d = target - current;
        return Abs(d) <= maxDelta ? target : current + (d > 0f ? maxDelta : -maxDelta);
    }

    public static float Repeat(float t, float length) => Clamp(t - System.MathF.Floor(t / length) * length, 0f, length);

    public static float Range(float min, float max) => min + (float)Rng.NextDouble() * (max - min);
    public static int Range(int min, int max) => max <= min ? min : Rng.Next(min, max);
    public static float Value => (float)Rng.NextDouble();

    public static Vector2 InsideUnitCircle()
    {
        float ang = Range(0f, 2f * PI);
        float r = Sqrt(Value);
        return V2(Cos(ang) * r, Sin(ang) * r);
    }

    public static Vector2 V2(float x, float y)
    {
        Vector2 v = default;
        v.x = x;
        v.y = y;
        return v;
    }

    public static Vector3 V3(float x, float y, float z = 0f)
    {
        Vector3 v = default;
        v.x = x;
        v.y = y;
        v.z = z;
        return v;
    }

    public static Color Rgba(float r, float g, float b, float a = 1f)
    {
        Color c = default;
        c.r = r;
        c.g = g;
        c.b = b;
        c.a = a;
        return c;
    }

    public static Color32 Rgba32(byte r, byte g, byte b, byte a)
    {
        Color32 c = default;
        c.r = r;
        c.g = g;
        c.b = b;
        c.a = a;
        return c;
    }

    // z 軸まわりだけの回転 (Quaternion.Euler(0, 0, deg) と同じ)
    public static Quaternion RotZ(float deg)
    {
        float half = deg * (PI / 360f);
        Quaternion q = default;
        q.z = Sin(half);
        q.w = Cos(half);
        return q;
    }

    // RotZ(deg) * (x, y, 0) の結果
    public static Vector3 RotateZ(float deg, float x, float y, float z = 0f)
    {
        float rad = deg * (PI / 180f);
        float c = Cos(rad), s = Sin(rad);
        return V3(x * c - y * s, x * s + y * c, z);
    }
}
