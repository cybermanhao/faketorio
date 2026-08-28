using Faketorio.Sim.State;

namespace Faketorio.Sim.Entities;

// SoA 风格实体池:数据连续存储,代数 ID 防悬垂引用(spec 5.1)。
// 稳态零分配:数组按需翻倍,不在 tick 内分配。
public sealed class EntityPool<T> where T : struct
{
    private T[] _data;
    private int[] _generations;   // 偶数=空槽,奇数=存活(create+destroy 各 +1)
    private int[] _freeStack;
    private int _freeCount;
    private int _count;

    public EntityPool(int initialCapacity = 256)
    {
        _data = new T[initialCapacity];
        _generations = new int[initialCapacity];
        _freeStack = new int[initialCapacity];
    }

    public int Capacity => _count;

    public EntityId Create(in T value)
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
        _generations[index]++;   // 偶 -> 奇: 存活
        _data[index] = value;
        return new EntityId(index, _generations[index]);
    }

    public void Destroy(EntityId id)
    {
        if (!IsAlive(id)) throw new InvalidOperationException($"Destroy on dead id {id}");
        _generations[id.Index]++; // 奇 -> 偶: 空槽
        _data[id.Index] = default;
        if (_freeCount == _freeStack.Length) Array.Resize(ref _freeStack, _freeStack.Length * 2);
        _freeStack[_freeCount++] = id.Index;
    }

    public bool IsAlive(EntityId id)
        => id.Index >= 0 && id.Index < _count && _generations[id.Index] == id.Generation
           && (id.Generation & 1) == 1;

    public ref T Get(EntityId id)
    {
        if (!IsAlive(id)) throw new InvalidOperationException($"Get on dead id {id}");
        return ref _data[id.Index];
    }

    // 按索引序确定遍历(哈希/系统更新用)
    public bool IsAliveAtIndex(int index) => (_generations[index] & 1) == 1;
    public ref T GetAtIndex(int index) => ref _data[index];
    public int GenerationAtIndex(int index) => _generations[index];

    // 分配器自身的簿记(高水位/空闲栈/全部代数,含死槽)必须可写,
    // 否则从存档恢复后 Create() 会分配出与原始运行不同的 EntityId(spec 铁律 4)。
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
