using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Items;

// 库存的实体层门面:持有 InventoryPool + 两张索引对齐的稀疏表。
// _byEntity 以 EntityId.Index 为键(实体 -> 库存),_ownerByIndex 以池索引
// 为键(库存 -> 实体),互为反向。只点查、不遍历,确定性不受影响。
public sealed class Inventories
{
    private readonly InventoryPool _pool = new();
    private InventoryId[] _byEntity;      // 缺省 InventoryId.Invalid
    private EntityId[] _ownerByIndex;     // 缺省 EntityId.Invalid;与 _pool 索引对齐

    public Inventories(int initialCapacity = 64)
    {
        _byEntity = new InventoryId[initialCapacity];
        Array.Fill(_byEntity, InventoryId.Invalid);
        _ownerByIndex = new EntityId[initialCapacity];
        Array.Fill(_ownerByIndex, EntityId.Invalid);
    }

    // 建一个 slotCount 槽的库存并绑给 entity。前置:该 entity 尚未有库存。
    public InventoryId AddContainer(EntityId entity, int slotCount, bool readOnly = false, int filterItemProtoId = 0)
    {
        var id = _pool.Create(new Inventory(slotCount, readOnly, filterItemProtoId));
        EnsureByEntity(entity.Index);
        _byEntity[entity.Index] = id;
        EnsureOwnerByIndex(id.Index);
        _ownerByIndex[id.Index] = entity;
        return id;
    }

    // 毁掉 entity 的库存,返回它当时的物品总数。前置:该 entity 有库存。
    // 只用 entity.Index 索引 _byEntity,不解引用实体本身——Simulation 在
    // Entities.Destroy(id) 之后调用它是安全的。
    public int RemoveContainer(EntityId entity)
    {
        var id = _byEntity[entity.Index];
        int total = _pool.Get(id).TotalItems();
        _pool.Destroy(id);
        _byEntity[entity.Index] = InventoryId.Invalid;
        _ownerByIndex[id.Index] = EntityId.Invalid;
        return total;
    }

    public InventoryId GetInventoryId(EntityId entity)
        => entity.Index >= 0 && entity.Index < _byEntity.Length
            ? _byEntity[entity.Index]
            : InventoryId.Invalid;

    public Inventory Get(InventoryId id) => _pool.Get(id);

    public int Capacity => _pool.Capacity;
    public bool IsAliveAtIndex(int index) => _pool.IsAliveAtIndex(index);
    public Inventory GetAtIndex(int index) => _pool.GetAtIndex(index);

    // 先写池分配器簿记,再按池索引序对每个存活库存写:
    // i / 代数 / 所属 EntityId(Index 再 Generation)/ ReadOnly(byte)/
    // FilterItemProtoId(int)/ inv.WriteState(自带槽数前缀 + 槽内容)。
    // 写所属 EntityId:哈希防御(误绑会被抓到)+ 给 M2 存读档留绑定锚。
    public void WriteState(IStateWriter writer)
    {
        _pool.WriteState(writer);
        for (int i = 0; i < _pool.Capacity; i++)
        {
            if (!_pool.IsAliveAtIndex(i)) continue;
            var item = _pool.GetAtIndex(i);
            var owner = _ownerByIndex[i];
            writer.Write(i);
            writer.Write(_pool.GenerationAtIndex(i));
            writer.Write(owner.Index);
            writer.Write(owner.Generation);
            writer.Write((byte)(item.ReadOnly ? 1 : 0));
            writer.Write(item.FilterItemProtoId);
            item.WriteState(writer);
        }
    }

    // Array.Resize 是零填充,default(InventoryId) = (0,0) 且 (0,0).IsValid == true
    // ——每次扩容的新增段必须显式填 Invalid,否则未绑定的实体会"看起来指向池索引 0"。
    private void EnsureByEntity(int index)
    {
        if (index < _byEntity.Length) return;
        int oldLen = _byEntity.Length;
        int newLen = oldLen == 0 ? 1 : oldLen;
        while (newLen <= index) newLen *= 2;
        Array.Resize(ref _byEntity, newLen);
        Array.Fill(_byEntity, InventoryId.Invalid, oldLen, newLen - oldLen);
    }

    private void EnsureOwnerByIndex(int index)
    {
        if (index < _ownerByIndex.Length) return;
        int oldLen = _ownerByIndex.Length;
        int newLen = oldLen == 0 ? 1 : oldLen;
        while (newLen <= index) newLen *= 2;
        Array.Resize(ref _ownerByIndex, newLen);
        Array.Fill(_ownerByIndex, EntityId.Invalid, oldLen, newLen - oldLen);
    }
}
