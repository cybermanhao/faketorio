namespace Faketorio.Sim.Belts;

// 一条 BeltLine 的稳定句柄:池槽位 + 代数。用前校验代数一致,拦截"指向
// 已删除并被复用的槽位"。与 Entities 的 EntityId 同构(Belts 有独立的池)。
public readonly record struct BeltLineId(int Index, int Generation)
{
    public static readonly BeltLineId Invalid = new(-1, 0);
    public bool IsValid => Index >= 0;
}
