using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace EndKnot.Tests;

// Modules/Random/ — MersenneTwister / Xorshift / NetRandomWrapper の決定性・範囲・引数検証。
// (HashRandomWrapper はゲーム本体の HashRandom へ委譲するだけなのでテスト対象外)

public class MersenneTwisterTests
{
    [Fact]
    public void SameSeed_ProducesSameSequence()
    {
        var a = new MersenneTwister(12345);
        var b = new MersenneTwister(12345);

        for (var i = 0; i < 2000; i++) Assert.Equal(a.Next(), b.Next());
    }

    [Fact]
    public void SameSeed_ProducesSameRangedSequence()
    {
        var a = new MersenneTwister(777);
        var b = new MersenneTwister(777);

        for (var i = 0; i < 1000; i++) Assert.Equal(a.Next(3, 50), b.Next(3, 50));
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentSequences()
    {
        var a = new MersenneTwister(1);
        var b = new MersenneTwister(2);

        var anyDifferent = false;
        for (var i = 0; i < 100 && !anyDifferent; i++) anyDifferent = a.Next() != b.Next();

        Assert.True(anyDifferent);
    }

    [Fact]
    public void Next_MinEqualsMax_ReturnsMin()
    {
        var rng = new MersenneTwister(42);
        Assert.Equal(7, rng.Next(7, 7));
        Assert.Equal(0, rng.Next(0, 0));
    }

    [Fact]
    public void Next_NegativeBounds_ThrowsArgumentOutOfRange()
    {
        var rng = new MersenneTwister(42);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.Next(-1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.Next(0, -5));
    }

    [Fact]
    public void Next_MinGreaterThanMax_ThrowsArgument()
    {
        var rng = new MersenneTwister(42);
        Assert.Throws<ArgumentException>(() => rng.Next(10, 5));
    }

    [Fact]
    public void Next_StaysWithinRange()
    {
        var rng = new MersenneTwister(99);

        for (var i = 0; i < 5000; i++)
        {
            int v = rng.Next(3, 17);
            Assert.InRange(v, 3, 16); // [min, max)

            int w = rng.Next(10);
            Assert.InRange(w, 0, 9); // [0, max)
        }
    }
}

public class XorshiftTests
{
    [Fact]
    public void SameSeed_ProducesSameSequence()
    {
        var a = new Xorshift(123456789u);
        var b = new Xorshift(123456789u);

        for (var i = 0; i < 2000; i++) Assert.Equal(a.Next(), b.Next());
    }

    [Fact]
    public void SameSeed_ProducesSameRangedSequence()
    {
        var a = new Xorshift(31337u);
        var b = new Xorshift(31337u);

        for (var i = 0; i < 1000; i++) Assert.Equal(a.Next(1, 100), b.Next(1, 100));
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentSequences()
    {
        var a = new Xorshift(1u);
        var b = new Xorshift(2u);

        var anyDifferent = false;
        for (var i = 0; i < 100 && !anyDifferent; i++) anyDifferent = a.Next() != b.Next();

        Assert.True(anyDifferent);
    }

    [Fact]
    public void Next_MinEqualsMax_ReturnsMin()
    {
        var rng = new Xorshift(42u);
        Assert.Equal(5, rng.Next(5, 5));
    }

    [Fact]
    public void Next_NegativeBounds_ThrowsArgumentOutOfRange()
    {
        var rng = new Xorshift(42u);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.Next(-3, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.Next(0, -1));
    }

    [Fact]
    public void Next_MinGreaterThanMax_ThrowsArgument()
    {
        var rng = new Xorshift(42u);
        Assert.Throws<ArgumentException>(() => rng.Next(8, 2));
    }

    [Fact]
    public void Next_StaysWithinRange()
    {
        var rng = new Xorshift(0xDEADBEEFu);

        for (var i = 0; i < 5000; i++)
        {
            int v = rng.Next(2, 23);
            Assert.InRange(v, 2, 22);

            int w = rng.Next(6);
            Assert.InRange(w, 0, 5);
        }
    }
}

public class NetRandomWrapperTests
{
    [Fact]
    public void SameSeed_ProducesSameSequence()
    {
        var a = new NetRandomWrapper(2024);
        var b = new NetRandomWrapper(2024);

        for (var i = 0; i < 1000; i++) Assert.Equal(a.Next(0, 1000), b.Next(0, 1000));
    }

    [Fact]
    public void Next_StaysWithinRange()
    {
        var rng = new NetRandomWrapper(7);

        for (var i = 0; i < 5000; i++)
        {
            Assert.InRange(rng.Next(5, 15), 5, 14);
            Assert.InRange(rng.Next(3), 0, 2);
        }
    }
}

// IRandom の静的ヘルパー (Instance 共有のため 1 クラスに集約 — 他クラスと並列実行されても衝突しない)
public class IRandomStaticTests
{
    [Fact]
    public void SetInstance_Null_DoesNotClearInstance()
    {
        var mt = new MersenneTwister(5);
        IRandom.SetInstance(mt);
        IRandom.SetInstance(null);
        Assert.Same(mt, IRandom.Instance);
    }

    [Fact]
    public void SetInstanceById_SwitchesImplementation()
    {
        IRandom.SetInstanceById(3);
        Assert.IsType<Xorshift>(IRandom.Instance);

        IRandom.SetInstanceById(4);
        Assert.IsType<MersenneTwister>(IRandom.Instance);
    }

    [Fact]
    public void SetInstanceById_InvalidId_DoesNotThrowOrChangeInstance()
    {
        var mt = new MersenneTwister(9);
        IRandom.SetInstance(mt);

        Exception ex = Record.Exception(() => IRandom.SetInstanceById(999));
        Assert.Null(ex);
        Assert.Same(mt, IRandom.Instance);
    }

    [Fact]
    public void Sequence_YieldsRequestedCountWithinRange()
    {
        IRandom.SetInstance(new MersenneTwister(1234));

        List<int> seq = IRandom.Sequence(500, 10, 20).ToList();
        Assert.Equal(500, seq.Count);
        Assert.All(seq, v => Assert.InRange(v, 10, 19));
    }

    [Fact]
    public void SequenceUnique_YieldsUniqueValuesWithinRange()
    {
        IRandom.SetInstance(new Xorshift(4242u));

        List<int> seq = IRandom.SequenceUnique(50, 0, 200).ToList();
        Assert.Equal(50, seq.Count);
        Assert.Equal(seq.Count, seq.Distinct().Count());
        Assert.All(seq, v => Assert.InRange(v, 0, 199));
    }

    [Fact]
    public void SequenceUnique_CapsAtRangeSize()
    {
        IRandom.SetInstance(new MersenneTwister(555));

        // 値域は 5 個しかない → 10 個要求しても 5 個で打ち切り
        List<int> seq = IRandom.SequenceUnique(10, 0, 5).ToList();
        Assert.Equal(5, seq.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, seq.OrderBy(v => v).ToArray());
    }
}
