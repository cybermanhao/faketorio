namespace Faketorio.Sim.Items;

// 一个 Inventory 的稳定句柄:池槽位 + 代数。用前校验代数一致,拦截"指向
// 已删除并被复用的槽位"。与 Belts 的 BeltLineId 同构(库存有独立的池)。
public readonly record struct InventoryId(int Index, int Generation)
{
    public static readonly InventoryId Invalid = new(-1, 0);
    public bool IsValid => Index >= 0;
}
