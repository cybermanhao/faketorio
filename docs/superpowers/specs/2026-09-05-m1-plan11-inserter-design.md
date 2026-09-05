# M1 Plan 11 — 机械臂(Inserter)设计

日期: 2026-09-05
状态: 已与用户确认的设计基线
前置依赖: P4(库存,`Inventory`/`Inventories` 的 `role`)、P7(电网,`ElectricGrid`)、P9(机器 role 库存:role 1 输入 / role 2 输出)、P10(typed belt items,`BeltLane` 带物品类型 + `TryRemoveItemInRange`/`TryInsertAtBack`)——均已合并 `main`(`71cc448`)
配套参考: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](2026-07-03-faketorio-design.md)(§5.2 传送带/机械臂交互、§5.3 实体休眠列举"空转的机械臂")、[`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md)(P11 条目)

## 1. 目标

在相邻两个格子之间搬运单个物品的实体。机械臂占 1×1;放置时的 `Command.Rotation` 决定朝向,**抓取格 = anchor − Delta(Rotation)**,**放置格 = anchor + Delta(Rotation)**(身后抓、身前放,跨过自身,伸手各 1 格)。抓取/放置的对象可以是传送带 lane、容器、或机器的 role 库存。

机械臂有一个"手":同一时刻手上至多一个物品。摆臂用**转速模型**——原型给一个基础转速(不同种类机械臂不同),运行时再乘 `satisfaction`(供电),将来接科技树时在同一处再乘科技加成因子。半程摆臂(抓取角 → 放置角)转过 `Q16.One`,一整个"抓 → 摆出 → 放 → 摆回"周期转过 `2 × Q16.One`。摆回来那半程是空手且**不能抓**,这就是放置之间的天然间隔:一个物品到下一个物品最快 = 一整个周期。

这是 M1 让"采矿机 → 带 → 机械臂 → 熔炉输入 →(熔炉)→ 熔炉输出 → 机械臂 → 带 → 箱子"闭环自动化的最后一块。

**不做**:长手机械臂(伸手 > 1 格)、过滤机械臂(只抓指定物品)、堆叠机械臂(一次抓多个)、机械臂对准传送带近/远 lane 的几何选择(M1 从简:先试 lane A,不行再 lane B,同 P10 采矿机输出的简化级别)、`RotateEntity` 命令(朝向放置时定死,同 P10 采矿机输出方向的先例)、科技加成(没有科技树)、待机能耗建模的精细化(空转机械臂也照常登记固定 `EnergyUsageJPerTick`,同 P9/P10 的"无条件登记"决定)。

## 2. 组件与文件结构

**新文件**

| 文件 | 职责 |
|---|---|
| `sim/Faketorio.Sim/Prototypes/InserterPrototype.cs` | `sealed class InserterPrototype : EntityPrototype`——`RotationSpeed`(`Q16`)、`EnergyUsageJPerTick`。 |
| `sim/Faketorio.Sim/Inserters.cs` | 机械臂运行时状态容器,**扁平 `namespace Faketorio.Sim;`**(同 `Player`/`Machines`/`MiningDrills` 先例,类名不进子命名空间;文件直接放在 `sim/Faketorio.Sim/` 下,无子目录)。 |
| `data/base/inserter.json` | 一个机械臂 prototype。 |

**改动**

| 文件 | 改动 |
|---|---|
| `sim/Faketorio.Sim/Belts/BeltLane.cs` | ① `TryRemoveItemInRange` 加 `out int removedItemProtoId` 参数(现有 P3c `BeltNetwork.ClearRange` 调用点传 `out _`)。② 新增 `bool TryInsertAt(int leadingEdgeSubTile, int itemProtoId)`——在 lane 的绝对位置中段插入一个物品,和邻近物品重叠 / 超出线长 / 位置为负 → 返回 false 不改状态。 |
| `sim/Faketorio.Sim/Belts/BeltNetwork.cs` | `ClearRange` 里 `while (lane.TryRemoveItemInRange(from, to))` 改成 `while (lane.TryRemoveItemInRange(from, to, out _))`。无其它改动。 |
| `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` | `Parse` 加 `"inserter"` arm(走 `ValidateFootprint<T>`);新 sibling pass `ResolveAndValidateInserters`(第六个,不并入前五个)。 |
| `sim/Faketorio.Sim/Simulation.cs` | `Inserters` 属性;`PlaceEntity`/`DestroyEntityAt` 给机械臂接入/摘出;`Step` 在 P10 采矿机两趟之后插入机械臂两趟;`WriteState` 追加 `Inserters.WriteState`。 |

## 3. BeltLane 新基元

### 3.1 `TryRemoveItemInRange` 加 out 参数

现有签名(P10 后):`public bool TryRemoveItemInRange(int fromSubTile, int toSubTile)`。改为:

```csharp
// 摘除"前沿绝对距离落在 [fromSubTile, toSubTile) 内"的最前一个物品,并通过
// removedItemProtoId 返回它的类型。无命中:返回 false,removedItemProtoId = 0,不改状态。
public bool TryRemoveItemInRange(int fromSubTile, int toSubTile, out int removedItemProtoId)
{
    removedItemProtoId = 0;
    int pos = 0;
    int removeAt = -1;
    for (int k = 0; k < _gaps.Count; k++)
    {
        pos += _gaps[k];
        if (pos >= toSubTile) return false;
        if (pos >= fromSubTile) { removeAt = k; break; }
        pos += ItemWidthSubTiles;
    }
    if (removeAt < 0) return false;

    removedItemProtoId = _itemProtoIds[removeAt];   // ← 摘除前先记下类型
    // ... 其余逻辑(缝合 gap、RemoveAt、收 _openIndex)与现有完全一致
}
```

`BeltNetwork.cs` 里唯一的调用点在 `ClearRange`(P3c 拆传送带用,只数个数不关心类型):`while (lane.TryRemoveItemInRange(from, to)) c++;` → `while (lane.TryRemoveItemInRange(from, to, out _)) c++;`。

### 3.2 新增 `TryInsertAt`

```csharp
// 在 lane 的绝对位置(前沿离出口 leadingEdgeSubTile 亚格)插入一个 itemProtoId 物品。
// 要求:leadingEdgeSubTile >= 0;leadingEdgeSubTile + ItemWidthSubTiles <= _lineLengthSubTiles;
// 且插入后与前后相邻物品的间距都 >= 0(体不重叠)。任一不满足 → 返回 false,不改状态。
// 命中:在正确的下标处插进 _gaps / _itemProtoIds(保持前到后升序),重算被影响的相邻 gap,
// _openIndex 收回到 <= 插入下标(插入点前方的物品可能因此需要重新参与推进判断)。
public bool TryInsertAt(int leadingEdgeSubTile, int itemProtoId)
```

实现走"绝对位置"路线(和 `ToAbsolutePositions`/`FromAbsolutePositions` 同一套思路,但只动一个物品,不整体重建):

1. 边界:`leadingEdgeSubTile < 0` 或 `leadingEdgeSubTile + ItemWidthSubTiles > _lineLengthSubTiles` → false。
2. 线性扫 `_gaps` 累加前沿位置,找到第一个前沿 `> leadingEdgeSubTile` 的物品下标 `i`(新物品要排在它前面);若扫到尾都没有,`i = _gaps.Count`(排最后)。
3. 前邻居(下标 `i−1`,若存在)的**后沿** = 其前沿 + `ItemWidthSubTiles`;新物品前沿 `leadingEdgeSubTile` 必须 `>= 前邻居后沿`,否则重叠 → false。
4. 后邻居(下标 `i`,若存在)的前沿必须 `>= leadingEdgeSubTile + ItemWidthSubTiles`,否则重叠 → false。
5. 都过:算出新物品的前方 gap = `leadingEdgeSubTile − (前邻居后沿, 没有前邻居则 0)`,把它 `Insert(i, ...)` 进 `_gaps`,`itemProtoId` 同下标 `Insert(i, ...)` 进 `_itemProtoIds`;后邻居的 gap 减去 `新物品前方 gap + ItemWidthSubTiles`(它离新物品更近了);`_openIndex = Math.Min(_openIndex, i)`。返回 true。

（如果实现时发现直接改 `_gaps` 太容易错,允许退化成:读 `ToAbsolutePositions()` → 找插入点 → 拼进 `PositionedItem` → `FromAbsolutePositions()` 重建,只要行为一致、且 `FromAbsolutePositions` 的重叠校验能兜住 step 3/4。冷路径,一个机械臂一 tick 最多调一次,分配可接受。）

## 4. `InserterPrototype`

```csharp
public sealed class InserterPrototype : EntityPrototype
{
    // 基础转速:每 tick 转过的"半程比例"。Q16.One = 1 tick 摆完半程;
    // Q16.FromRatio(1, 20) = 20 tick 摆完半程。运行时再乘 satisfaction(将来乘科技加成)。
    public Q16 RotationSpeed { get; init; } = Q16.One;
    public long EnergyUsageJPerTick { get; init; }
}
```

JSON 里用"半程摆臂秒数"表达(和配方 `energyRequiredSeconds`、矿脉 `miningTimeSeconds` 同风格,人读友好),loader 转成 `Q16`:

```csharp
"inserter" => ValidateFootprint(new InserterPrototype
{
    Name = name,
    TileWidth = GetInt(el, "tileWidth", 1),
    TileHeight = GetInt(el, "tileHeight", 1),
    EnergyUsageJPerTick = el.TryGetProperty("energyUsage", out var eu) ? Units.ParsePower(eu.GetString()!) : 0L,
    RotationSpeed = Q16.FromRatio(1, Math.Max(1, Units.SecondsToTicks(GetDouble(el, "rotationTimeSeconds", 1.0)))),
}),
```

`ResolveAndValidateInserters`(`AssignIds()` 之后新增的第六个 sibling pass):`EnergyUsageJPerTick >= 0`;`RotationSpeed.Raw >= 1`(半程秒数太大导致 `FromRatio(1, n)` 取整成 0 的话,一整个周期永远走不完——这里兜住)。`RotationSpeed` 不像 P9 `CraftingSpeed`/P10 `MiningSpeed` 那样"JSON 不解析、恒 `Q16.One`",它是真数据驱动的,所以要校验。

## 5. `Inserters`:每机械臂运行时状态

**命名空间坑(同 P5 `Player`、P9 `Machines`、P10 `MiningDrills` 先例)**:类放**扁平** `namespace Faketorio.Sim;`,文件直接放 `sim/Faketorio.Sim/Inserters.cs`,无子目录。

```csharp
namespace Faketorio.Sim;

// 机械臂运行时状态容器。刻意不知道 Simulation/Prototypes/Inventories/Belts/
// ElectricGrid——只收裸 EntityId/int/long,同 ElectricGrid/Machines/MiningDrills 的隔离原则。
// 抓取/放置格解析、库存/传送带读写、电网登记全部在 Simulation 的机械臂 tick 方法里做。
public sealed class Inserters
{
    private readonly Dictionary<EntityId, InserterState> _states = new();

    public void RegisterInserter(EntityId id) => _states[id] = new InserterState(0, 0);
    public void UnregisterInserter(EntityId id) => _states.Remove(id);

    public int GetHeldItemProtoId(EntityId id) => _states.TryGetValue(id, out var s) ? s.HeldItemProtoId : 0;
    public long GetSwingProgress(EntityId id) => _states.TryGetValue(id, out var s) ? s.SwingProgress : 0;

    // 空手抓起:设手上物品,进度归 0(从抓取角开始往外摆)。前置:已注册。
    public void Grab(EntityId id, int itemProtoId)
    {
        _ = _states[id];   // throw-on-missing,同 P9 Machines / P10 MiningDrills 的一致性要求
        _states[id] = _states[id] with { HeldItemProtoId = itemProtoId, SwingProgress = 0 };
    }

    // 摆臂推进(往外或往回都用它)。前置:已注册。
    public void AddSwing(EntityId id, long delta)
    {
        var s = _states[id];
        _states[id] = s with { SwingProgress = s.SwingProgress + delta };
    }

    // 到放置角、放置成功:清手,进度钉在半程(接着从半程往回摆)。前置:已注册。
    public void Release(EntityId id)
    {
        var s = _states[id];
        _states[id] = s with { HeldItemProtoId = 0, SwingProgress = HalfSwing };
    }

    // 摆回抓取角:整个周期结束,进度归 0(下 tick 可以再抓)。前置:已注册。
    public void ArriveAtPickup(EntityId id)
    {
        _ = _states[id];
        _states[id] = _states[id] with { SwingProgress = 0 };
    }

    public const long HalfSwing = 1L << 16;      // Q16.One:半程(抓取角 → 放置角)
    public const long FullSwing = HalfSwing * 2; // 一整个周期(抓 → 摆出 → 放 → 摆回)

    // 按 EntityId.Index 排序后写:index/代数/手上物品 id/摆臂进度。
    public void WriteState(Faketorio.Sim.State.IStateWriter writer)
    {
        var ids = new List<EntityId>(_states.Keys);
        ids.Sort((a, b) => a.Index.CompareTo(b.Index));
        writer.Write(ids.Count);
        foreach (var id in ids)
        {
            var s = _states[id];
            writer.Write(id.Index); writer.Write(id.Generation);
            writer.Write(s.HeldItemProtoId); writer.Write(s.SwingProgress);
        }
    }
}

internal readonly record struct InserterState(int HeldItemProtoId, long SwingProgress);
```

`SwingProgress` 的三个阶段(由 `HeldItemProtoId` + 进度值区分,无单独的 phase 字段):

| `HeldItemProtoId` | `SwingProgress` | 阶段 | 该 tick 做什么 |
|---|---|---|---|
| `0` | `0` | 停在抓取角,空手 | 尝试抓(§6 步骤 2) |
| `!= 0` | `~[0, HalfSwing]` | 往外摆,拿着物品 | 进度推进(到 `HalfSwing` 就不再累加,可能小幅越过——同 P9/P10 "停在阈值"约定,越过量最多一个 `delta`,`Release` 时显式钉回 `HalfSwing` 丢弃);到 `>= HalfSwing` 且放置侧能接 → 放 |
| `0` | `~(0, FullSwing)` | 往回摆,空手 | 进度推进(到 `FullSwing` 不再累加);到 `>= FullSwing` → `ArriveAtPickup`(显式归 0,丢弃越过量) |

## 6. `Simulation` 里的两趟机械臂 tick

同 P9/P10:需求登记必须在 `ElectricGrid.Settle()` 之前、读 satisfaction 必须在之后,所以两趟全量扫描,接在 P10 采矿机两趟之后(`Settle()` 仍只调一次,三个子系统共用)。

**`Step()` 里的最终顺序**:

```
应用命令
玩家 tick
电网段:发电机登记供给 → MachinesTickPreSettle → MiningDrillsTickPreSettle → InsertersTickPreSettle
        → ElectricGrid.Settle() → 发电机烧油
        → MachinesTickPostSettle → MiningDrillsTickPostSettle → InsertersTickPostSettle
传送带推进
传送带拐角交接
Tick++
```

机械臂 post-settle **在传送带推进之前**——机械臂看到的是本 tick 开头的传送带状态,抓/放完之后传送带才移动。确定、无歧义。

### 6.1 `InserterTickPreSettle(id, proto, x, y)`

无条件 `ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick)`。就这一件事(机械臂没有"匹配配方 / 搜目标"这种 pre-settle 工作)。

### 6.2 `InserterTickPostSettle(id, proto, x, y, rotation)`

```csharp
var (dx, dy) = BeltNetwork.Delta(rotation);
int pickX = x - dx, pickY = y - dy;   // 身后
int dropX = x + dx, dropY = y + dy;   // 身前
int held = Inserters.GetHeldItemProtoId(id);
long progress = Inserters.GetSwingProgress(id);
```

**阶段 A:空手停在抓取角(`held == 0 && progress == 0`)** —— 尝试抓:
- `pickX,pickY` 上有传送带线:算出该格在 lane 坐标系里的位置 `k = line.Tiles.IndexOf((pickX,pickY))`(必命中,格属于这条线);抓窗 `from = k*256 - (ItemWidthSubTiles - 1)`、`to = (k+1)*256`(前沿落在这个区间的物品,其体覆盖了这个格)。先试 `line.LaneA.TryRemoveItemInRange(from, to, out int grabbed)`,不行再试 `LaneB`。任一成功 → `Inserters.Grab(id, grabbed)`。
- 否则 `pickX,pickY` 上有实体且有库存:机器(`proto is CraftingMachinePrototype`)取 role 2(输出),其它取 role 0。按槽序 `for i in 0..inv.SlotCount` 找第一个 `!inv[i].IsEmpty` 的槽,`itemId = inv[i].ItemProtoId`;`inv.Remove(itemId, 1)`;`Inserters.Grab(id, itemId)`。全空 → 抓不到。
- 抓不到 → 本 tick 什么都不做(停在阶段 A,进度保持 0)。

**阶段 B:往外摆,拿着物品(`held != 0`)**:
```csharp
long delta = proto.RotationSpeed.Mul(ElectricGrid.GetSatisfaction(id).Raw);
if (progress < Inserters.HalfSwing) Inserters.AddSwing(id, delta);
if (Inserters.GetSwingProgress(id) < Inserters.HalfSwing) return;   // 还没摆到放置角(停在阈值不再累加,同 P9/P10 约定)
// 到放置角了,尝试放:
```
- `dropX,dropY` 上有传送带线:`k = line.Tiles.IndexOf((dropX,dropY))`;放置前沿 `pos = k*256 + 128`(格中心)。先试 `line.LaneA.TryInsertAt(pos, held)`,不行再试 `LaneB`。任一成功 → `Inserters.Release(id)`。
- 否则 `dropX,dropY` 上有实体且有库存:机器取 role 1(输入),其它取 role 0。`stack = ((ItemPrototype)Prototypes.GetById(held)).StackSize`;`inv.Insert(held, 1, stack) > 0` → `Inserters.Release(id)`。
- 放不下(传送带那个位置被占 / 目标库存满 / 过滤不匹配)→ **不 Release**,手一直拿着,`SwingProgress` 停在 `HalfSwing` 附近不再推进(下 tick 阶段 B 的 `progress < HalfSwing` 为假,不再 `AddSwing`),下 tick 再试放(同 P9/P10 输出堵塞的模式)。

**阶段 C:往回摆,空手(`held == 0 && progress > 0`)**:
```csharp
long delta = proto.RotationSpeed.Mul(ElectricGrid.GetSatisfaction(id).Raw);
if (progress < Inserters.FullSwing) Inserters.AddSwing(id, delta);
if (Inserters.GetSwingProgress(id) < Inserters.FullSwing) return;
Inserters.ArriveAtPickup(id);   // 归 0,下 tick 回到阶段 A
```

### 6.3 `PlaceEntity` / `DestroyEntityAt` 钩子

- `PlaceEntity`:`if (proto is InserterPrototype) Inserters.RegisterInserter(id);`(和现有 belt/container/pole/generator/machine/drill 钩子并列)。
- `DestroyEntityAt`:`bool isInserter = proto is InserterPrototype;` 在 `Entities.Destroy(id)` 之前捕获,之后 `if (isInserter) Inserters.UnregisterInserter(id);`。
  - **手上物品的处理**:被销毁时手上如果还拿着物品,直接丢弃——这是全代码库一致的既有行为(传送带、箱子、P9 机器完成品、P10 采矿机 pending 都在销毁时丢弃,M1 没有掉落到世界的机制)。P10 最终审查已把"销毁掉落"记为将来单独统一处理所有实体的横切项,本计划不破例。

## 7. 数据

`data/base/inserter.json`:

```json
[
  { "type": "inserter", "name": "inserter-basic", "tileWidth": 1, "tileHeight": 1,
    "rotationTimeSeconds": 0.5, "energyUsage": "5kW" }
]
```

`rotationTimeSeconds: 0.5` → `SecondsToTicks(0.5)` = 30 tick 半程 → `RotationSpeed = Q16.FromRatio(1, 30)`(每 tick 转 1/30 半程)。一整个抓-放周期 60 tick(满电时),即满电每秒搬 1 个物品。`energyUsage: "5kW"` → `ParsePower` = 83 J/tick(`5000/60` 向下取整)。

不新增物品/配方。

## 8. 确定性

- `SwingProgress`/`HalfSwing`/`FullSwing`/`delta` 全 `long`(Q16.16),`RotationSpeed`/`satisfaction` 相乘走 `Q16.Mul`,无 `float`/`double`(`rotationTimeSeconds` 的 `double` 只在 loader 一次性换算,同 `energyRequiredSeconds`/`miningTimeSeconds` 的先例)。
- 抓取格/放置格是放置时 `Rotation` 的纯函数(`BeltNetwork.Delta`),不依赖运行时状态。
- 全量扫描按 `Entities` 池索引序(同 P9/P10/发电机模式)。
- 传送带抓/放:`TryRemoveItemInRange`/`TryInsertAt` 都是按绝对位置的确定操作;先 lane A 后 lane B 的固定顺序。
- 库存抓:取"第一个非空槽"按槽序确定;`Insert`/`Remove` 本身确定。
- `Inserters.WriteState` 按 `EntityId.Index` 排序后写。
- 机械臂 post-settle 严格在传送带推进之前——机械臂和传送带对同一 tick 的物品位置没有读写竞争。
- `BeltLane` 的 `_gaps` 与 `_itemProtoIds` 在 `TryRemoveItemInRange`/`TryInsertAt` 里也必须严格平行、成对更新(同 P10 的不变式,测试要覆盖)。

## 9. 测试

- **`BeltLaneTests`** 追加:
  - `TryRemoveItemInRange` 的 `out` 参数返回被摘物品的类型;无命中时 `out` 为 0 且不改状态。
  - `TryInsertAt`:空 lane 中段插入成功,gap 正确;插在两个已有物品之间(有空隙)成功;插入位置和邻居重叠 → false 不改状态;位置为负 / 超线长 → false;插入后 `_gaps` 与 `_itemProtoIds` 平行、`ToAbsolutePositions` 里新物品位置和类型都对。
- **`BeltNetworkTests`** 追加:`ClearRange`(经 `RemoveBelt` 端点/中段拆分触发)在 `TryRemoveItemInRange` 加了 `out` 之后计数仍正确(回归)。
- **`PrototypeLoaderTests`** 追加:`inserter.json` 正常加载,`RotationSpeed` = `Q16.FromRatio(1, 30)`(`Raw` = 2184,即 `65536 / 30` 向下取整);负例两条 —— `energyUsage` 负数(`"-120W"`,同 P10 的量级理由);`rotationTimeSeconds` 大到半程 tick 数 > 65536 使 `FromRatio(1, n)` 取整成 0(`"rotationTimeSeconds": 2000` → `SecondsToTicks(2000)` = 120000 > 65536 → `RotationSpeed.Raw` = 0 → 触发 `RotationSpeed.Raw >= 1` 校验抛 `InvalidDataException`)。
- **`InsertersTests`**(新文件,同 `MiningDrillsTests` 风格):`RegisterInserter`/`UnregisterInserter`/`Grab`/`AddSwing`/`Release`/`ArriveAtPickup` 的独立行为;未注册实体调用写方法抛 `KeyNotFoundException`;`WriteState` 按 index 排序、注册顺序不影响哈希。
- **`SimulationTests`** 追加:
  - 容器 → 机械臂 → 容器:源箱放物,机械臂朝向对,跑够一个周期,物品到目的箱;`held`/`SwingProgress` 在周期中的推进符合三阶段。
  - 传送带 → 机械臂 → 传送带:上游带上放物并推进到机械臂抓取格,机械臂抓起、摆出、放到下游带的放置格中心(断言下游带那个位置出现正确物品 id)。
  - 机械臂 → 熔炉输入 / 熔炉输出 → 机械臂:验证 role 1/role 2 分派(放机器用 role 1,抓机器用 role 2)。
  - 放置侧堵塞:目的箱预先塞满,机械臂摆到放置角后 `held` 不清、`SwingProgress` 钉在 `HalfSwing`;腾出空间后下一个周期正常放下并继续。
  - 电力不足降速:同 P9/P10 的 satisfaction 降速测试模式(摆电线杆 + 发电机 + 燃料,competing demand 压到 ~0.5),机械臂完成一个周期的 tick 数约翻倍(容差 `Assert.InRange`)。
  - 拆除手上有物的机械臂:不抛异常,`Inserters` 状态摘掉,手上物品丢弃(不掉世界——记录既有行为)。
- **`DeterminismTests`** 追加:摆一条"箱 → 机械臂 → 熔炉 → 机械臂 → 箱"的小生产线 + 电线杆 + 发电机 + 燃料,跑够搬运若干个物品的 tick 数,同命令两遍逐 tick 哈希全等。

## 10. 实施拆分

- **Task 1**(BeltLane 基元):`TryRemoveItemInRange` 加 `out` + `BeltNetwork.ClearRange` 调用点适配 + `TryInsertAt` 新方法 + `BeltLaneTests`/`BeltNetworkTests` 相应测试。不碰 `Simulation.cs`、不碰任何 P11 新文件。
- **Task 2**(机械臂原型 + 状态类):`InserterPrototype` + `PrototypeLoader` 解析(新 type + 第六个 sibling pass)+ `data/base/inserter.json` + `Inserters` 状态类 + `InsertersTests`。依赖 Task 1 已合并(共用分支历史,不直接用其接口)。不碰 `Simulation.cs`。
- **Task 3**(接线):`Simulation.cs` 的 `Inserters` 属性、`PlaceEntity`/`DestroyEntityAt` 钩子、两趟机械臂 tick(§6 全部三阶段状态机 + 抓取/放置格解析)、`WriteState` 追加 + `SimulationTests` + `DeterminismTests`。依赖 Task 1(`TryRemoveItemInRange` 的 out / `TryInsertAt`)+ Task 2(`InserterPrototype`/`Inserters`)。必须最后做。

每个 Task 走完整「实现→审查→(修复→复审)」闭环。
