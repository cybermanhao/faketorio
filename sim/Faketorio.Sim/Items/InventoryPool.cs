using Faketorio.Sim.State;

namespace Faketorio.Sim.Items;

// 装 Inventory(引用类型)的代数 ID 池。刻意平行于 BeltLinePool——
// EntityPool<T> 约束 where T : struct,容不下 Inventory。确定性簿记
// (代数数组 + 空闲栈 + 高水位)只在这一处维护。
public sealed class InventoryPool
{
    private Inventory?[] _data;
    private int[] _generations;   // 偶数=空槽,奇数=存活(create+destroy 各 +1)
    private int[] _freeStack;
    private int _freeCount;
    private int _count;

    public InventoryPool(int initialCapacity = 64)
    {
        _data = new Inventory?[initialCapacity];
        _generations = new int[initialCapacity];
        _freeStack = new int[initialCapacity];
    }

    // 高水位:已用过的最大槽位数。按索引序遍历 [0, Capacity) 用。
    public int Capacity => _count;

    public InventoryId Create(Inventory inv)
    {
        int index;
        if (_freeCount > 0)
        {
            index = _freeStack[--_freeCount];
        }
        else
        {
            if (_count == _data.Length) Grow();
            index = _count++;
        }
        _generations[index]++;   // 偶 -> 奇:存活
        _data[index] = inv;
        return new InventoryId(index, _generations[index]);
    }

    public void Destroy(InventoryId id)
    {
        if (!IsAlive(id)) throw new InvalidOperationException($"Destroy on dead InventoryId {id}");
        _generations[id.Index]++; // 奇 -> 偶:空槽
        _data[id.Index] = null;
        if (_freeCount == _freeStack.Length) Array.Resize(ref _freeStack, _freeStack.Length * 2);
        _freeStack[_freeCount++] = id.Index;
    }

    public bool IsAlive(InventoryId id)
        => id.Index >= 0 && id.Index < _count && _generations[id.Index] == id.Generation
           && (id.Generation & 1) == 1;

    public Inventory Get(InventoryId id)
    {
        if (!IsAlive(id)) throw new InvalidOperationException($"Get on dead InventoryId {id}");
        return _data[id.Index]!;
    }

    // 按索引序确定遍历(状态哈希用)。
    public bool IsAliveAtIndex(int index) => (_generations[index] & 1) == 1;
    public Inventory GetAtIndex(int index) => _data[index]!;
    public int GenerationAtIndex(int index) => _generations[index];

    // 分配器簿记(高水位/空闲栈/全部代数,含死槽)。参照 BeltLinePool.WriteState。
    public void WriteState(IStateWriter writer)
    {
        writer.Write(_count);
        for (int i = 0; i < _count; i++)
            writer.Write(_generations[i]);
        writer.Write(_freeCount);
        for (int i = 0; i < _freeCount; i++)
            writer.Write(_freeStack[i]);
    }

    private void Grow()
    {
        int newSize = _data.Length * 2;
        Array.Resize(ref _data, newSize);
        Array.Resize(ref _generations, newSize);
    }
}
