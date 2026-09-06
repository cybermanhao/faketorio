using Faketorio.Sim.Entities;

namespace Faketorio.Sim;

// 按 EntityId.Index 升序维护的 id 列表。四个状态容器各持一个,在 Register*/Unregister*
// 里同步。EntityPool 复用被释放的槽位索引,所以插入必须二分定位,不能 append。
internal sealed class OrderedEntityIdList
{
    private readonly List<EntityId> _ids = new();

    public IReadOnlyList<EntityId> Ids => _ids;
    // 热路径迭代用:返回具体 List<EntityId>(struct 枚举器,可 for 索引),
    // 就是 _ids 本身,升序集合与 Ids 完全一致。
    internal List<EntityId> IdsList => _ids;
    public int Count => _ids.Count;

    // 第一个 _ids[pos].Index >= index 的位置。
    private int LowerBound(int index)
    {
        int lo = 0, hi = _ids.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_ids[mid].Index < index) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public void Add(EntityId id)
    {
        int lo = LowerBound(id.Index);
        // lo 是第一个 Index >= id.Index 的位置。命中同 Index 视为已存在,忽略。
        if (lo < _ids.Count && _ids[lo].Index == id.Index) return;
        _ids.Insert(lo, id);
    }

    public void Remove(EntityId id)
    {
        int lo = LowerBound(id.Index);
        if (lo < _ids.Count && _ids[lo] == id) _ids.RemoveAt(lo);
    }
}
