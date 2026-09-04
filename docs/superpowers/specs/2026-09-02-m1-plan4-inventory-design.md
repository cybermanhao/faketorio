# M1 Plan 4 — 库存(ItemStack + Inventory)设计

日期: 2026-09-02
状态: 已与用户确认的设计基线
前置依赖: M1 Plan 1(模拟核心地基)、M1 Plan 3(传送带,均已合并进 `main`)
配套参考: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](2026-07-03-faketorio-design.md)(5.1 世界与实体存储、5.3 加工状态机)、[`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md)(P4 条目)

## 1. 目标

给模拟层一个**槽位式库存**:`ItemStack` 值类型、`Inventory` 定长槽位容器、`InventoryPool` 代数 ID 池、`EntityId → InventoryId` 反查,并把箱子实体接上(放置建、拆除毁)。这是箱子、机械臂(P11)、所有机器(P9)的共同地基——当前 sim 完全没有库存,`entities.json` 里 `inventorySize`、`items.json` 里 `stackSize` 字段都还没人读。

不做:玩家背包(P5)、机器多槽铺路(P9)、机械臂按槽抓放的具体行为(P11)、物品去向策略(拆箱内容物 M1 里直接丢弃,同传送带 §10 分层约定)。

## 2. 参考:旧项目 `godot-playgroud`

旧项目(AlchemFarm)的 `2026-06-02-inventory-system-design.md` + `ItemStack.gd` / `SlotGroup.gd` / `MachineSlot.gd` 提供了**概念模型**,值得沿用:

- `ItemStack = 物品 + 数量`,空 = null / 0
- 堆叠:同类合并到 `max_stack`,不可叠加物一格一个,溢出开新槽
- 每库存的**过滤**(`accepts` 标签 / `filter` item_id)+ **只读**标志——P9 机器输出槽 / 燃料槽要用,现在留字段很便宜
- 机器持有多个单一用途的具名库存(input / output / fuel)——印证 `InventoryPool + EntityId→InventoryId`(一个实体可多个 id)

**不沿用**:UI(背包面板 / 快捷栏 / 拖拽)是表现层,不在 sim 范围;`Player.bag` 是 P5;GDScript `Resource` + 对象引用 → C# + int proto id;旧项目 `SlotGroup`(增长到 capacity)与 `Player.bag`(定长空槽数组)两套模型不一致,Faketorio 统一用**定长空槽数组**(因为 `ContainerPrototype.InventorySize` 已经是"槽数",且要支持"槽满但容量没满"、槽过滤)。

## 3. `ItemStack`

`sim/Faketorio.Sim/Items/ItemStack.cs`:

```
public readonly record struct ItemStack(int ItemProtoId, int Count)
{
    public static readonly ItemStack Empty = default;   // (0, 0)
    public bool IsEmpty => Count == 0;
}
```

- 空槽 ⟺ `Count == 0`(空时 `ItemProtoId` 也为 0,`default` 即空)。
- 堆叠上限**不进 struct**——它是 `ItemPrototype.StackSize`(已存在,默认 50),由调用方从 `PrototypeRegistry` 解析后传给 `Inventory.Insert`。`Inventory` 本身 prototype 无关,和 `BeltLane` 一样。
- `Count` 恒 `> 0` 当且仅当槽非空;`Count < 0` 不合法(调用方保证,不做运行时检查,信任前置条件的风格)。

## 4. `Inventory`

`sim/Faketorio.Sim/Items/Inventory.cs`:

```
public sealed class Inventory
{
    private readonly ItemStack[] _slots;      // 定长,长度 = 构造时的槽数
    private readonly int _filterItemProtoId;  // 0 = 不过滤;>0 = 只收这种

    public bool ReadOnly { get; }             // 机械臂能否往里 Insert(输出库存 = true)

    public Inventory(int slotCount, bool readOnly = false, int filterItemProtoId = 0);

    public int SlotCount { get; }
    public ItemStack this[int slot] { get; }  // 只读索引,越界抛 IndexOutOfRangeException(信任调用方)

    // 先填持有 itemProtoId 的未满槽(封顶 stackSize),再占空槽(每槽最多 stackSize)。
    // ReadOnly 或过滤不匹配 → 返回 0。返回实际放入数(≤ count);调用方保留 count - 返回值。
    public int Insert(int itemProtoId, int count, int stackSize);

    // 从前往后扣持有 itemProtoId 的槽,扣到 0 的槽清空(置 ItemStack.Empty)。
    // 返回实际取出数(≤ count)。
    public int Remove(int itemProtoId, int count);

    public int CountOf(int itemProtoId);      // 跨槽合计某种物品
    public int TotalItems();                  // 所有非空槽的 Count 之和(拆箱返回用)

    public void WriteState(IStateWriter writer);  // 写 SlotCount,再逐槽写 (ItemProtoId, Count)
}
```

### 4.1 `Insert` 语义

1. `ReadOnly` → 返回 0,不改状态。
2. `_filterItemProtoId != 0 && itemProtoId != _filterItemProtoId` → 返回 0。
3. `count <= 0` → 返回 0(防御,不抛)。
4. **第一轮**:从前往后遍历槽,凡 `_slots[i].ItemProtoId == itemProtoId && _slots[i].Count < stackSize` 的,补到 `stackSize`(或补完 `count` 剩余量),累加放入数。
5. **第二轮**:遍历空槽,每槽放入 `min(剩余, stackSize)`,累加。
6. 返回累计放入数。

### 4.2 `Remove` 语义

从前往后遍历,凡 `_slots[i].ItemProtoId == itemProtoId` 的,扣 `min(_slots[i].Count, 剩余)`,扣到 0 则 `_slots[i] = ItemStack.Empty`,累加取出数。返回累计。

### 4.3 `WriteState`

`writer.Write(_slots.Length);` 然后逐槽 `writer.Write(slot.ItemProtoId); writer.Write(slot.Count);`。**不写** `ReadOnly` / `_filterItemProtoId`——它们是构造期常量,由 `Inventories.WriteState` 在实体层面覆盖(与 `BeltLane.WriteState` 不写线长同理:重建时从 `ContainerPrototype` 推)。规范序列化只认槽内容。

## 5. `InventoryId` + `InventoryPool`

`sim/Faketorio.Sim/Items/InventoryId.cs`:

```
public readonly record struct InventoryId(int Index, int Generation)
{
    public static readonly InventoryId Invalid = new(-1, 0);
    public bool IsValid => Index >= 0;
}
```

`sim/Faketorio.Sim/Items/InventoryPool.cs`:装 `Inventory`(引用类型)的代数 ID 池。**刻意平行于 `BeltLinePool`**——`EntityPool<T>` 仍是 `where T : struct` 约束,无法容纳 `Inventory`。分配器簿记(高水位 `_count`、空闲下标栈、平行 `_generations` 偶死奇活)只在这一处维护。成员:`Create(Inventory) → InventoryId`、`Destroy(InventoryId)`、`IsAlive(InventoryId)`、`Get(InventoryId) → Inventory`、`Capacity`、`IsAliveAtIndex(int)`、`GetAtIndex(int)`、`GenerationAtIndex(int)`、`WriteState(IStateWriter)`(参照 `BeltLinePool.WriteState`)。

> 若 3b 起的三份平行池(`EntityPool<T:struct>`、`BeltLinePool`、`InventoryPool`)让平行实现的重复变得明显,可在 P4 里把分配器簿记抽成一个共享的非泛型 `GenerationTable`,让三个池组合它——这是 P4 实施计划里可选的一步,不强制。

## 6. `Inventories`(容器类)+ Simulation 接线

`sim/Faketorio.Sim/Items/Inventories.cs`:

```
public sealed class Inventories
{
    private readonly InventoryPool _pool = new();
    // EntityId.Index 对齐的稀疏数组;缺省 InventoryId.Invalid。与 Entities 池同步增长。
    private InventoryId[] _byEntity;

    // 建一个 slotCount 槽的库存并绑给 entity,返回 id。前置:该 entity 尚未有库存。
    public InventoryId AddContainer(EntityId entity, int slotCount);

    // 毁掉 entity 的库存(_pool.Destroy + 把 _byEntity[entity.Index] 置回 Invalid),
    // 返回它当时的物品总数(TotalItems)。前置:该 entity 有库存。
    public int RemoveContainer(EntityId entity);

    public InventoryId GetInventoryId(EntityId entity);   // Invalid = 没有
    public Inventory Get(InventoryId id);

    public int Capacity { get; }
    public bool IsAliveAtIndex(int index);
    public Inventory GetAtIndex(int index);

    public void WriteState(IStateWriter writer);
}
```

`Inventories.WriteState`:先 `_pool.WriteState(writer)`(分配器簿记),再按池索引序对每个存活 `Inventory` 写 `i`、`GenerationAtIndex(i)`、`SlotCount`、`ReadOnly`(byte)、`_filterItemProtoId`(int)、`inv.WriteState`(槽内容)。`ReadOnly`/`filter` 在这里写一次(实体层),`Inventory.WriteState` 只写槽——两者拼起来足以重建。与 `Simulation.WriteState` 写 `Entities` / `BeltNetwork` 的模式一致。

> `_byEntity` 稀疏数组的增长:`Entities` 池按需翻倍;`AddContainer` 里若 `entity.Index >= _byEntity.Length` 则 `Array.Resize` 到足够大(初值填 `Invalid`)。不遍历它,只点查,确定性不受影响(同 `TileToLineIndex` 用 `Dictionary` 的理由)。

### Simulation 改动

- `public Inventories Inventories { get; } = new();`(在 `Belts` 旁)。
- `Apply / PlaceEntity`:`World.OccupyArea` 成功后,`if (proto is ContainerPrototype cp) Inventories.AddContainer(id, cp.InventorySize);`(返回值忽略——绑定在 `AddContainer` 内部完成)。
- `Apply / RemoveEntity`:在 `Entities.Destroy(id)` **之前**抓 `bool isContainer = proto is ContainerPrototype;`(`id` 这个 `EntityId` 值已在手,是记录结构体的拷贝);`World.ClearArea` + `Entities.Destroy` 照跑;之后 `if (isContainer) Inventories.RemoveContainer(id);`。`RemoveContainer(id)` 只用 `id.Index` 索引 `_byEntity`,不 `Entities.Get(id)` 解引用已死实体,所以 `Destroy` 之后调用安全。返回的物品总数在 M1 忽略——策略层 P5 起再定,同传送带 §10;实际效果 = 丢弃。belt 后处理与 container 后处理都排在既有实体移除逻辑之后,顺序 belt→container 固定(一个实体不会既是传送带又是箱子,两者互不影响)。
- `WriteState`:`Belts.WriteState(writer);` 之后加 `Inventories.WriteState(writer);`。
- **`Step` 不改**——箱子被动,P4 无每 tick 逻辑。

`ContainerPrototype`(已存在,`InventorySize` 字段)不改。多格箱子(`large-chest` 2×3)的 `InventorySize=48` 照常——库存与 footprint 无关,一个实体一个库存。

## 7. `IStateWriter` 覆盖 / 确定性

- `Inventory.WriteState` / `InventoryPool.WriteState` / `Inventories.WriteState` 全部按定长 / 索引序遍历,无 `Dictionary` 迭代。
- `Simulation.WriteState` 追加一行 `Inventories.WriteState`,位置固定(`Belts` 之后)。既有确定性测试是相对比较(两遍相等 / 变化前后不等),空 `Inventories` 只多写两个 `0`,不受影响。
- 存读档往返(M2)时 `ReadOnly`/`filter` 从 `ContainerPrototype` 重建,`_openIndex` 式的纯派生字段本设计没有。

## 8. 测试

- **`InventoryTests`**:Insert 进空库存填槽;Insert 同类先补未满槽再占空槽;Insert 封顶 `stackSize`;Insert 进满库存返回未放入的余量;`ReadOnly` → 0;过滤不匹配 → 0;Remove 部分 / 全部 / 跨槽;`CountOf` / `TotalItems`;`WriteState` 相同槽内容 → 相同哈希,不同 → 不同。
- **`InventoryPoolTests`**:镜像 `BeltLinePoolTests`——create/get、destroy/stale、槽位复用 bump 代数、按索引序遍历只见存活、`WriteState` 稳定→改分配器状态则变。
- **`SimulationTests`** 追加:放 `wooden-chest` → `Inventories.GetInventoryId(id)` 有效、`Get(...).SlotCount == 16`;通过 API 插入物品后仍在;拆 `wooden-chest` → 库存被毁、`RemoveContainer` 返回当时物品总数;放 `large-chest`(2×3)→ 48 槽;拆非箱子实体不碰 `Inventories`。
- **`DeterminismTests`** 追加:放一个箱子 + 插几种物品 + 拆掉,60 tick,两遍逐 tick 哈希全等。

## 9. 实施拆分

- **Task 1**:`ItemStack` + `Inventory`(§3/§4)+ `InventoryTests`。不碰 Simulation。
- **Task 2**:`InventoryId` + `InventoryPool`(§5)+ `InventoryPoolTests`。不碰 Simulation。
- **Task 3**:`Inventories` 容器类(§6)+ Simulation 接线(`Inventories` 属性、`Apply` 放置/拆除钩子、`WriteState` 追加)+ `SimulationTests` / `DeterminismTests` 追加。

(共享 `GenerationTable` 抽取是 Task 2 里可选的一步,视三份平行池的重复观感决定,不强制。)

每个 Task 走完整的"实现→审查→(修复→复审)"闭环。
