namespace Faketorio.Sim.World;

// splitmix64 式整数混合哈希。矿脉生成的唯一熵源(铁律 3:禁用 System.Random)。
// 全 ulong 位运算,C# 语义完全定义、跨平台位一致。常量勿改。
public static class DeterministicHash
{
    private static ulong Mix(ulong x)
    {
        x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27; x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return x;
    }

    public static ulong Hash(long seed, int field, int x, int y)
    {
        ulong h = Mix((ulong)seed ^ 0x9E3779B97F4A7C15UL);
        h = Mix(h ^ ((ulong)(uint)field * 0x9E3779B97F4A7C15UL));
        h = Mix(h ^ ((ulong)(uint)x     * 0xFF51AFD7ED558CCDUL));
        h = Mix(h ^ ((ulong)(uint)y     * 0xC4CEB9FE1A85EC53UL));
        return h;
    }
}
