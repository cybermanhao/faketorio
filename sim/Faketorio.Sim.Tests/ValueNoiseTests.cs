using Faketorio.Sim.World;

namespace Faketorio.Sim.Tests;

public class ValueNoiseTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(10, 3)]
    [InlineData(15, 3)]
    [InlineData(16, 4)]
    [InlineData(1_000_000, 1000)]
    [InlineData(1_000_002, 1000)]
    public void Isqrt_FloorRoot(long n, int expected)
        => Assert.Equal(expected, ValueNoise.Isqrt(n));

    [Fact]
    public void Isqrt_Negative_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ValueNoise.Isqrt(-1));

    [Fact]
    public void Lerp_Endpoints_And_Midpoint()
    {
        Assert.Equal(100, ValueNoise.Lerp(100, 200, 0));
        Assert.Equal(200, ValueNoise.Lerp(100, 200, 65536));
        Assert.Equal(150, ValueNoise.Lerp(100, 200, 32768));
    }

    [Fact]
    public void Ease_Endpoints_And_Monotonic()
    {
        Assert.Equal(0, ValueNoise.Ease(0));
        Assert.Equal(65536, ValueNoise.Ease(65536));
        int prev = -1;
        for (int t = 0; t <= 65536; t += 512)
        {
            int e = ValueNoise.Ease(t);
            Assert.True(e >= prev, $"Ease not monotonic at t={t}: {e} < {prev}");
            prev = e;
        }
    }

    [Fact]
    public void Fbm_Deterministic()
        => Assert.Equal(
            ValueNoise.Fbm(42L, 3, 100, -200, 64, 3),
            ValueNoise.Fbm(42L, 3, 100, -200, 64, 3));

    [Fact]
    public void Fbm_InRange_0_to_65536()
    {
        for (int x = -300; x <= 300; x += 37)
            for (int y = -300; y <= 300; y += 41)
            {
                int v = ValueNoise.Fbm(7L, 1, x, y, 64, 3);
                Assert.InRange(v, 0, 65535);
            }
    }

    [Fact]
    public void Fbm_DifferentSeedOrField_ChangesField()
    {
        // 在一片区域上,换 seed 或换 field 至少有一格不同
        bool seedDiff = false, fieldDiff = false;
        for (int x = 0; x < 128 && !(seedDiff && fieldDiff); x += 8)
            for (int y = 0; y < 128; y += 8)
            {
                int a = ValueNoise.Fbm(1L, 1, x, y, 64, 3);
                if (ValueNoise.Fbm(2L, 1, x, y, 64, 3) != a) seedDiff = true;
                if (ValueNoise.Fbm(1L, 2, x, y, 64, 3) != a) fieldDiff = true;
            }
        Assert.True(seedDiff);
        Assert.True(fieldDiff);
    }

    [Fact]
    public void Fbm_SingleOctave_NoDivideByZero()
        => Assert.InRange(ValueNoise.Fbm(1L, 1, 5, 5, 64, 1), 0, 65535);
}
