namespace Faketorio.Sim.Tests;

public class UnitsTests
{
    [Theory]
    [InlineData("4MJ", 4_000_000L)]
    [InlineData("100J", 100L)]
    [InlineData("5kJ", 5_000L)]
    [InlineData("1.5MJ", 1_500_000L)]
    public void ParseEnergy_ConvertsToJoules(string input, long expected)
        => Assert.Equal(expected, Units.ParseEnergy(input));

    [Theory]
    [InlineData("90kW", 1500L)]   // 90000 W / 60 tick
    [InlineData("60W", 1L)]
    public void ParsePower_ConvertsToJoulesPerTick(string input, long expected)
        => Assert.Equal(expected, Units.ParsePower(input));

    [Fact]
    public void SecondsToTicks_HalfSecondIs30() => Assert.Equal(30, Units.SecondsToTicks(0.5));

    [Fact]
    public void Q16_HalfRatio_HalvesValue()
    {
        var half = Q16.FromRatio(1, 2);
        Assert.Equal(500L, half.Mul(1000L));
    }

    [Fact]
    public void Q16_One_IsIdentity() => Assert.Equal(1234L, Q16.One.Mul(1234L));
}
