using Faketorio.Sim.State;

namespace Faketorio.Sim.Items;

// 定长槽位库存。prototype 无关(和 BeltLane 一样):堆叠上限每次调用由外面传。
// 越界索引抛原生 IndexOutOfRangeException(信任调用方);Insert/Remove 只
// 防御 count <= 0(返回 0,不抛)。
public sealed class Inventory
{
    private readonly ItemStack[] _slots;   // 定长,长度 = 构造时槽数

    public bool ReadOnly { get; }
    public int FilterItemProtoId { get; }  // 0 = 不过滤;>0 = 只收这种。Inventories 层写进哈希。

    public Inventory(int slotCount, bool readOnly = false, int filterItemProtoId = 0)
    {
        _slots = new ItemStack[slotCount];
        ReadOnly = readOnly;
        FilterItemProtoId = filterItemProtoId;
    }

    public int SlotCount => _slots.Length;

    public ItemStack this[int slot] => _slots[slot];

    // 先补持有 itemProtoId 的未满槽(封顶 stackSize),再占空槽(每槽最多 stackSize)。
    // ReadOnly 或过滤不匹配 → 0。返回实际放入数(<= count);调用方保留 count - 返回值。
    public int Insert(int itemProtoId, int count, int stackSize)
    {
        if (ReadOnly) return 0;
        if (FilterItemProtoId != 0 && itemProtoId != FilterItemProtoId) return 0;
        if (count <= 0) return 0;

        int remaining = count;

        // 第一轮:补同类未满槽
        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (_slots[i].ItemProtoId != itemProtoId || _slots[i].Count >= stackSize) continue;
            int space = stackSize - _slots[i].Count;
            int put = space < remaining ? space : remaining;
            _slots[i] = new ItemStack(itemProtoId, _slots[i].Count + put);
            remaining -= put;
        }

        // 第二轮:占空槽
        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (!_slots[i].IsEmpty) continue;
            int put = stackSize < remaining ? stackSize : remaining;
            _slots[i] = new ItemStack(itemProtoId, put);
            remaining -= put;
        }

        return count - remaining;
    }

    // 从前往后扣持有 itemProtoId 的槽,扣到 0 的槽整个置 ItemStack.Empty。
    // count <= 0 → 0(与 Insert 对称;缺了它 Remove(x,-5) 会扣 min(3,-5)=-5,
    // 槽数量反而增加、返回负,静默污染)。返回实际取出数(<= count)。
    public int Remove(int itemProtoId, int count)
    {
        if (count <= 0) return 0;

        int remaining = count;
        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (_slots[i].ItemProtoId != itemProtoId || _slots[i].Count == 0) continue;
            int take = _slots[i].Count < remaining ? _slots[i].Count : remaining;
            int left = _slots[i].Count - take;
            _slots[i] = left == 0 ? ItemStack.Empty : new ItemStack(itemProtoId, left);
            remaining -= take;
        }
        return count - remaining;
    }

    public int CountOf(int itemProtoId)
    {
        int total = 0;
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i].ItemProtoId == itemProtoId) total += _slots[i].Count;
        return total;
    }

    // 所有非空槽的 Count 之和(拆箱返回用)。
    public int TotalItems()
    {
        int total = 0;
        for (int i = 0; i < _slots.Length; i++) total += _slots[i].Count;
        return total;
    }

    // 写槽数前缀 + 逐槽 (ItemProtoId, Count)。不写 ReadOnly / FilterItemProtoId
    // ——那两个由 Inventories.WriteState 在实体层写一次(哈希防御)。
    public void WriteState(IStateWriter writer)
    {
        writer.Write(_slots.Length);
        for (int i = 0; i < _slots.Length; i++)
        {
            writer.Write(_slots[i].ItemProtoId);
            writer.Write(_slots[i].Count);
        }
    }
}
