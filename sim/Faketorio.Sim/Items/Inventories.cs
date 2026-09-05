using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Items;

// 库存的实体层门面:持有 InventoryPool + 两张对齐/反向的索引表。
// _byEntityRole 以 (EntityId.Index, role) 为键(哪个实体的哪个角色库存 -> InventoryId),
// _ownerByIndex 以池索引为键(库存 -> 所属实体 + role),互为反向。只点查、不遍历,
// 确定性不受影响。role 语义由调用方约定(Inventories 自己不解释):
// 0 = 默认/箱子/发电机燃料槽,1 = 机器输入,2 = 机器输出(P9)。
public sealed class Inventories
{
    private readonly InventoryPool _pool = new();
    private readonly Dictionary<(int EntityIndex, int Role), InventoryId> _byEntityRole = new();
    private (EntityId Owner, int Role)[] _ownerByIndex;   // 缺省 (Invalid, 0);与 _pool 索引对齐

    public Inventories(int initialCapacity = 64)
    {
        _ownerByIndex = new (EntityId, int)[initialCapacity];
        Array.Fill(_ownerByIndex, (EntityId.Invalid, 0));
    }

    // 建一个 slotCount 槽的库存并绑给 (entity, role)。前置:该 (entity, role) 尚未有库存。
    public InventoryId AddContainer(EntityId entity, int slotCount, bool readOnly = false, int filterItemProtoId = 0, int role = 0)
    {
        var id = _pool.Create(new Inventory(slotCount, readOnly, filterItemProtoId));
        _byEntityRole[(entity.Index, role)] = id;
        EnsureOwnerByIndex(id.Index);
        _ownerByIndex[id.Index] = (entity, role);
        return id;
    }

    // 毁掉 (entity, role) 的库存,返回它当时的物品总数。前置:该 (entity, role) 有库存。
    public int RemoveContainer(EntityId entity, int role = 0)
    {
        var key = (entity.Index, role);
        var id = _byEntityRole[key];
        int total = _pool.Get(id).TotalItems();
        _pool.Destroy(id);
        _byEntityRole.Remove(key);
        _ownerByIndex[id.Index] = (EntityId.Invalid, 0);
        return total;
    }

    public InventoryId GetInventoryId(EntityId entity, int role = 0)
        => _byEntityRole.TryGetValue((entity.Index, role), out var id) ? id : InventoryId.Invalid;

    public Inventory Get(InventoryId id) => _pool.Get(id);

    public int Capacity => _pool.Capacity;
    public bool IsAliveAtIndex(int index) => _pool.IsAliveAtIndex(index);
    public Inventory GetAtIndex(int index) => _pool.GetAtIndex(index);

    // 先写池分配器簿记,再按池索引序对每个存活库存写:
    // i / 代数 / 所属 EntityId(Index 再 Generation)/ role / ReadOnly(byte)/
    // FilterItemProtoId(int)/ inv.WriteState(自带槽数前缀 + 槽内容)。
    public void WriteState(IStateWriter writer)
    {
        _pool.WriteState(writer);
        for (int i = 0; i < _pool.Capacity; i++)
        {
            if (!_pool.IsAliveAtIndex(i)) continue;
            var item = _pool.GetAtIndex(i);
            var (owner, role) = _ownerByIndex[i];
            writer.Write(i);
            writer.Write(_pool.GenerationAtIndex(i));
            writer.Write(owner.Index);
            writer.Write(owner.Generation);
            writer.Write(role);
            writer.Write((byte)(item.ReadOnly ? 1 : 0));
            writer.Write(item.FilterItemProtoId);
            item.WriteState(writer);
        }
    }

    // Array.Resize 是零填充——每次扩容的新增段必须显式填 (Invalid, 0),
    // 否则未绑定的池索引会"看起来属于 EntityId(0,0)、role 0"。
    private void EnsureOwnerByIndex(int index)
    {
        if (index < _ownerByIndex.Length) return;
        int oldLen = _ownerByIndex.Length;
        int newLen = oldLen == 0 ? 1 : oldLen;
        while (newLen <= index) newLen *= 2;
        Array.Resize(ref _ownerByIndex, newLen);
        Array.Fill(_ownerByIndex, (EntityId.Invalid, 0), oldLen, newLen - oldLen);
    }
}
