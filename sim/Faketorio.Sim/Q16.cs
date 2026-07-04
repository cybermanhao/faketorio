namespace Faketorio.Sim;

// Q16.16 定点数,用于 satisfaction 等比例系数(spec 5.6)
public readonly struct Q16
{
    public readonly int Raw;
    private Q16(int raw) => Raw = raw;

    public static readonly Q16 One = new(1 << 16);
    public static readonly Q16 Zero = new(0);

    public static Q16 FromRatio(long numerator, long denominator)
        => new((int)(numerator * (1 << 16) / denominator));

    public long Mul(long value) => value * Raw >> 16;
}
