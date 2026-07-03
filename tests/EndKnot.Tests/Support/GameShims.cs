using System;

// テスト専用シム — 本体ソースをテスト環境 (Unity/Il2Cpp 無し) でコンパイルするための最小スタブ。
// 本体側の実体:
//  - Logger      → Modules/Debugger.cs (internal static class Logger, namespace EndKnot)
//  - HashRandom  → Among Us 本体 (Il2Cpp) の静的乱数クラス
// どちらも「コンパイルを通す」ためだけの存在で、シム経由の挙動はテスト対象外。

namespace EndKnot;

internal static class Logger
{
    public static void Warn(string text, string tag) { }
}

internal static class HashRandom
{
    private static readonly Random Rng = new();

    public static int Next(int minValue, int maxValue)
    {
        return Rng.Next(minValue, maxValue);
    }

    public static int Next(int maxValue)
    {
        return Rng.Next(maxValue);
    }

    public static uint Next()
    {
        return (uint)Rng.Next();
    }

    public static int FastNext(int maxValue)
    {
        return Rng.Next(maxValue);
    }
}
