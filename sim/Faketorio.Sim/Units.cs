using System.Globalization;

namespace Faketorio.Sim;

public static class Units
{
    public const int TicksPerSecond = 60;
    public const int SubTilesPerTile = 256;

    // "4MJ" -> 4_000_000. 仅数据加载期调用(double 换算一次性、确定)。
    public static long ParseEnergy(string s)
    {
        var span = s.AsSpan().Trim();
        if (span[^1] is 'J' or 'W') span = span[..^1];
        long multiplier = span[^1] switch
        {
            'k' => 1_000L,
            'M' => 1_000_000L,
            'G' => 1_000_000_000L,
            _ => 1L,
        };
        if (multiplier != 1L) span = span[..^1];
        double value = double.Parse(span, CultureInfo.InvariantCulture);
        return (long)Math.Round(value * multiplier);
    }

    // "90kW" -> 焦耳/tick(不整除时向下取整)
    public static long ParsePower(string s) => ParseEnergy(s) / TicksPerSecond;

    public static int SecondsToTicks(double seconds)
        => (int)Math.Round(seconds * TicksPerSecond);
}
