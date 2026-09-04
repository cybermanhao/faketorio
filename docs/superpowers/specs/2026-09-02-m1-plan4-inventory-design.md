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

- 空槽 ⟺ `Count == 0`。**不变式**:任何代码路径都不产生 `Count == 0 && ItemProtoId != 0`——`Insert` 永不写零数量槽,`Remove` 扣到 0 时写整个 `ItemStack.Empty`。所以 `slot == ItemStack.Empty`(记录结构体值相等)与 `slot.IsEmpty` 永远等价;`IsEmpty` 只看 `Count` 是为了快。
- 堆叠上限**不进 struct**——它是 `ItemPrototype.StackSize`(已存在,默认 50),由调用方从 `PrototypeRegistry` 解析后传给 `Inventory.Insert`。`Inventory` 本身 prototype 无关,和 `BeltLane` 一样。
- **调用方保证的前置条件**(不做运行时检查,信任前置条件的风格):`Count` 恒 `> 0` 当且仅当槽非空(`Count < 0` 不合法);`Inventory.Insert` 的 `stackSize` 恒 `> 0`(否则一件放不进、返回 0——会让"循环放到放完"的调用方死转)。另外 `Insert` 也**防御** `stackSize <= 0`:直接返回 0、不改状态(与 `count <= 0` 守卫对称),否则 pass 2 会写出零/负数量槽、破坏上述不变式——有了这道防御,`ItemStack` 不变式无条件成立。

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

    public void WriteState(IStateWriter writer);  // 写 _slots.Length 作前缀,再逐槽写 (ItemProtoId, Count)
}
```

### 4.1 `Insert` 语义

1. `ReadOnly` → 返回 0,不改状态。
2. `_filterItemProtoId != 0 && itemProtoId != _filterItemProtoId` → 返回 0。
3. `count <= 0 || stackSize <= 0` → 返回 0(防御,不抛;`stackSize <= 0` 会让 pass 2 写出零/负数量槽,破坏不变式)。
4. **第一轮**:从前往后遍历槽,凡 `_slots[i].ItemProtoId == itemProtoId && _slots[i].Count < stackSize` 的,补到 `stackSize`(或补完 `count` 剩余量),累加放入数。
5. **第二轮**:遍历空槽,每槽放入 `min(剩余, stackSize)`,累加。
6. 返回累计放入数。

### 4.2 `Remove` 语义

1. `count <= 0` → 返回 0(防御,不抛;与 `Insert` 对称。缺了它 `Remove(x, -5)` 会 `扣 min(3, -5) = -5` → 槽数量反而增加、返回值变负,静默污染)。
2. 从前往后遍历,凡 `_slots[i].ItemProtoId == itemProtoId` 的,扣 `min(_slots[i].Count, 剩余)`,扣到 0 则 `_slots[i] = ItemStack.Empty`,累加取出数。返回累计。

### 4.3 `WriteState`

`writer.Write(_slots.Length);` 作槽数前缀,然后逐槽 `writer.Write(slot.ItemProtoId); writer.Write(slot.Count);`。**不写** `ReadOnly` / `_filterItemProtoId`——那两个由 `Inventories.WriteState` 在实体层写一次(见 §6),`Inventory.WriteState` 只负责槽内容。`Inventories` 层写 `ReadOnly`/filter 是**作为哈希防御**(误配一个 `ReadOnly`/filter 会被状态哈希抓到),不是因为它们不可重建——`ContainerPrototype` 确实能重建。M2 存读档的完整恢复(池索引复现、库存↔实体绑定)是另一个开放问题,见 §7。

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

`Inventories.WriteState`:先 `_pool.WriteState(writer)`(分配器簿记),再按池索引序对每个存活 `Inventory` 写:`i`、`GenerationAtIndex(i)`、**该库存所属实体的 `EntityId`(`Index` 再 `Generation`,各 1 个 int)**、`ReadOnly`(byte)、`_filterItemProtoId`(int)、`inv.WriteState`(它自带槽数前缀 + 槽内容)。`SlotCount` 不在这层重写(它是 `inv.WriteState` 的前缀)。

**为什么写所属实体 `EntityId`**:(1) M1 便宜的哈希增强——不写它的话,`AddContainer` 把库存误绑到别的实体,状态哈希看不出来(哈希只看槽内容,与所属实体无关);(2) 给 M2 存读档留一个"库存↔实体绑定"的锚。要写这个,`Inventories` 需要在每条记录上能拿到"池索引 `i` 的库存属于哪个实体"——最简单是加一个和 `_pool` 索引对齐的 `EntityId[] _ownerByIndex`(`AddContainer` 时填,`RemoveContainer` 时清),与 `_byEntity` 是互为反向的两张表。

> `_byEntity` 稀疏数组的增长(**这里有个易错点**):`Array.Resize` 对 struct 数组是**零填充**,而 `default(InventoryId)` 是 `(0, 0)`,不是 `InventoryId.Invalid`(`(-1, 0)`)。`IsValid => Index >= 0` 会让 `(0,0).IsValid == true` —— 每个没设过的实体槽都"看起来指向池索引 0",违反 `GetInventoryId` 的"Invalid = 没有"约定(不会静默损坏:代数 0 是偶数 → `_pool.IsAlive` 为 false → `Get` 抛 "Get on dead";但按约定先查 `.IsValid` 的调用方会拿到错答案)。**初始分配和每次 `Array.Resize` 之后,新增的那段必须用循环写满 `InventoryId.Invalid`(`Array.Fill(_byEntity, InventoryId.Invalid, oldLen, newLen - oldLen)`),不能只 resize。** `_ownerByIndex` 同理用 `EntityId.Invalid` 填。这两张表都只点查、不遍历,确定性不受影响(同 `TileToLineIndex` 用 `Dictionary` 的理由);但它们是本项目第一次用"零填充的代数句柄数组",这个坑 belt 代码没踩过(`TileToLineIndex` 是 `Dictionary`,缺 key = 没有)。

### Simulation 改动

- `public Inventories Inventories { get; } = new();`(在 `Belts` 旁)。**分层说明**:`BeltNetwork` 刻意"不知道 `Simulation` / `Entities` 的存在";`Inventories` 反过来直接收 `EntityId`。这是有意的偏差,理由是 `EntityId` 只是一个裸值类型(`readonly record struct`,不依赖 `EntityPool`),`Inventories` 拿它当键不引入对实体池的依赖——但确实跨了 belt 代码守住的那条边界,记一笔。
- `Apply / PlaceEntity`:`World.OccupyArea` 成功后,`if (proto is ContainerPrototype cp) Inventories.AddContainer(id, cp.InventorySize);`(返回值忽略——绑定在 `AddContainer` 内部完成)。
- `Apply / RemoveEntity`:在 `Entities.Destroy(id)` **之前**抓 `bool isContainer = proto is ContainerPrototype;`(`id` 这个 `EntityId` 值已在手,是记录结构体的拷贝);`World.ClearArea` + `Entities.Destroy` 照跑;之后 `if (isContainer) Inventories.RemoveContainer(id);`。`RemoveContainer(id)` 只用 `id.Index` 索引 `_byEntity`,不 `Entities.Get(id)` 解引用已死实体,所以 `Destroy` 之后调用安全。返回的物品总数在 M1 忽略——策略层 P5 起再定,同传送带 §10;实际效果 = 丢弃。belt 后处理与 container 后处理都排在既有实体移除逻辑之后,顺序 belt→container 固定(一个实体不会既是传送带又是箱子,两者互不影响)。
- `WriteState`:`Belts.WriteState(writer);` 之后加 `Inventories.WriteState(writer);`。
- **`Step` 不改**——箱子被动,P4 无每 tick 逻辑。

`ContainerPrototype`(已存在,`InventorySize` 字段)不改。多格箱子(`large-chest` 2×3)的 `InventorySize=48` 照常——库存与 footprint 无关,一个实体一个库存。

## 7. `IStateWriter` 覆盖 / 确定性

- `Inventory.WriteState` / `InventoryPool.WriteState` / `Inventories.WriteState` 全部按定长 / 索引序遍历,无 `Dictionary` 迭代。
- `Simulation.WriteState` 追加一行 `Inventories.WriteState`,位置固定(`Belts` 之后)。既有确定性测试是相对比较(两遍相等 / 变化前后不等),空 `Inventories` 只多写两个 `0`,不受影响。
- **M2 存读档往返是一个未解决的开放问题,不在 P4 范围**:`ReadOnly`/`filter`/`SlotCount` 能从 `ContainerPrototype` 重建,但 (a) `Inventories.WriteState` 写的池索引 `i` 靠 gameplay 期间的空闲栈复用顺序,不是实体创建顺序,回放不能靠"遍历实体逐个 `AddContainer`"复现;(b) 库存↔实体绑定除了 §6 新加的"每条记录写所属 `EntityId`"外,没有别的锚。M1 只要哈希、不 reload,这两点无害;M2 做真存读档时再回来定"忠实恢复池状态"的做法(可能像 `EntityPool.WriteState` / `BeltLinePool.WriteState` 那样把空闲栈也序列化,加上按 `EntityId` 重绑)。本设计没有 `_openIndex` 式的纯派生缓存字段。

## 8. 测试

- **`InventoryTests`**:Insert 进空库存填槽;Insert 同类先补未满槽再占空槽;Insert 封顶 `stackSize`;Insert 进满库存返回未放入的余量;`ReadOnly` → 0;过滤不匹配 → 0;`Insert(x, -1, s)` / `Insert(x, n, 0)` → 0 不改状态;Remove 部分 / 全部 / 跨槽;`Remove(x, -1)` → 0 不改状态(见 §4.2);`CountOf` / `TotalItems`;`WriteState` 相同槽内容 → 相同哈希,不同 → 不同。(`ReadOnly` / filter 只能靠 `new Inventory(slotCount, readOnly:true, filterItemProtoId:...)` 直接构造来测——M1 里没有 `AddContainer` 重载会设它们,这是有意的:字段先留着给 P9。)
- **`InventoryPoolTests`**:镜像 `BeltLinePoolTests`——create/get、destroy/stale、槽位复用 bump 代数、按索引序遍历只见存活、`WriteState` 稳定→改分配器状态则变。
- **`SimulationTests`** 追加:
  - 放 `wooden-chest` → `Inventories.GetInventoryId(id)` 有效、`Get(...).SlotCount == 16`;通过 API 插入物品后仍在。
  - 拆 `wooden-chest` → 库存被毁、`RemoveContainer` 返回当时物品总数。
  - 放 `large-chest`(2×3)→ 48 槽。
  - 拆非箱子实体不碰 `Inventories`。
  - **`_byEntity` resize 路径**(唯一能抓到 §6 那个零填充坑的测试):先放一个 `EntityId.Index` 很大的箱子(强制 resize——比如先放几十个再拆掉造出高水位,再放一个新的),然后断言一个更早的、没有库存的实体 `GetInventoryId(...).IsValid == false`。
  - **`Inventories` 层的槽位复用**:放箱子 → 插物品 → 拆 → 再放一个箱子 → 断言新库存是空的、且新 `InventoryId` 的 `Generation` 比旧的大(镜像 `InventoryPoolTests` 的"槽位复用 bump 代数",但走 `Inventories`)。
- **`DeterminismTests`** 追加:
  - 放一个箱子 + 插几种物品 + 拆掉,60 tick,两遍逐 tick 哈希全等。
  - **哈希对物品变化敏感**(镜像 `HashChangesWhenWorldChanges`):放箱子后取一次哈希,插一个物品再取,两者不等——证明槽内容真的进了 writer,而不只是"两遍相等"。

## 9. 实施拆分

- **Task 1**:`ItemStack` + `Inventory`(§3/§4)+ `InventoryTests`。不碰 Simulation。
- **Task 2**:`InventoryId` + `InventoryPool`(§5)+ `InventoryPoolTests`。不碰 Simulation。
- **Task 3**:`Inventories` 容器类(§6)+ Simulation 接线(`Inventories` 属性、`Apply` 放置/拆除钩子、`WriteState` 追加)+ `SimulationTests` / `DeterminismTests` 追加。

(共享 `GenerationTable` 抽取是 Task 2 里可选的一步,视三份平行池的重复观感决定,不强制。)

每个 Task 走完整的"实现→审查→(修复→复审)"闭环。
