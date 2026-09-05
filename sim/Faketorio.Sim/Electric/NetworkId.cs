namespace Faketorio.Sim.Electric;

// 一次 EnsureTopology() 结果里的网络索引;不跨 tick 保留身份。
public readonly record struct NetworkId(int Index)
{
    public static readonly NetworkId Invalid = new(-1);
    public bool IsValid => Index >= 0;
}
