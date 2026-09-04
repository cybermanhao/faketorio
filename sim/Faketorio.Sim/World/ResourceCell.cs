namespace Faketorio.Sim.World;

// 一格的矿:矿 prototype id + 矿量。空格 ⟺ Amount == 0。
// 不变式(同 ItemStack):没有路径产生 ResourceProtoId != 0 && Amount == 0。
public readonly record struct ResourceCell(int ResourceProtoId, int Amount)
{
    public static readonly ResourceCell Empty = default;   // (0, 0)
    public bool IsEmpty => Amount == 0;
}
