using System.Numerics;

namespace Faketorio.Sim.World;

// 定点值噪声(Q16.16)。纯函数,全 int/long + 位移,无 float/double(铁律 3)。
public static class ValueNoise
{
    // 整数下取整平方根。
    public static int Isqrt(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (n == 0) return 0;
        long x = (long)Math.Sqrt(n);            // 近似起点(仅整数结果参与状态,double 不入状态)
        while (x * x > n) x--;
        while ((x + 1) * (x + 1) <= n) x++;
        return (int)x;
    }

    // smoothstep 缓动 ease(t) = t·t·(3 − 2t),t 原始 Q16 [0, 65536]。
    public static int Ease(int t)
    {
        long t2 = ((long)t * t) >> 16;
        return (int)((t2 * ((3L << 16) - 2L * t)) >> 16);
    }

    // 定点线性插值,t 原始 Q16。
    public static int Lerp(int p, int q, int t)
        => p + (int)(((long)(q - p) * t) >> 16);

    private static int LatticeQ16(long seed, int field, int lx, int ly)
        => (int)(DeterministicHash.Hash(seed, field, lx, ly) & 0xFFFF);

    // 单倍频:格距 S = latticeSize >> k(latticeSize 是 2 的幂)。
    private static int Octave(long seed, int field, int tx, int ty, int latticeSize, int k)
    {
        int shift = BitOperations.Log2((uint)latticeSize) - k;   // 调用方保证 >= 0
        int s = 1 << shift;
        int lx0 = tx >> shift, ly0 = ty >> shift;

        int v00 = LatticeQ16(seed, field, lx0,     ly0);
        int v10 = LatticeQ16(seed, field, lx0 + 1, ly0);
        int v01 = LatticeQ16(seed, field, lx0,     ly0 + 1);
        int v11 = LatticeQ16(seed, field, lx0 + 1, ly0 + 1);

        int fxQ = (int)(((long)(tx & (s - 1)) << 16) / s);
        int fyQ = (int)(((long)(ty & (s - 1)) << 16) / s);
        int ux = Ease(fxQ), uy = Ease(fyQ);

        int a = Lerp(v00, v10, ux);
        int b = Lerp(v01, v11, ux);
        return Lerp(a, b, uy);
    }

    // 多倍频 fBm。归一化用循环求和(闭式在 octaves == 1 时除零)。
    public static int Fbm(long seed, int field, int tileX, int tileY, int latticeSize, int octaves)
    {
        long sum = 0, norm = 0;
        for (int k = 0; k < octaves; k++)
        {
            sum += ((long)Octave(seed, field, tileX, tileY, latticeSize, k)) >> k;
            norm += 65536L >> k;
        }
        return (int)((sum << 16) / norm);
    }
}
