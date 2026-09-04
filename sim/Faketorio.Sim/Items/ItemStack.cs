namespace Faketorio.Sim.Items;

// 一个物品堆:物品 prototype id + 数量。空槽 ⟺ Count == 0。
// 不变式:没有代码路径产生 Count == 0 && ItemProtoId != 0,
// 所以 (slot == Empty) 与 slot.IsEmpty 永远等价。堆叠上限不进 struct
// (它是 ItemPrototype.StackSize,由调用方传给 Inventory.Insert)。
public readonly record struct ItemStack(int ItemProtoId, int Count)
{
    public static readonly ItemStack Empty = default;   // (0, 0)
    public bool IsEmpty => Count == 0;
}
