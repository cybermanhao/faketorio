# M1 Plan 10 — Typed Belt Items + 电力采矿机 设计

日期: 2026-09-05
状态: 已与用户确认的设计基线
前置依赖: P2/P3(传送带 `BeltLane`/`BeltLine`/`BeltNetwork`)、P4(库存)、P6(矿脉生成,`ResourceGrid`)、P7(电网,`ElectricGrid`)、P9(加工状态机,两趟 tick + satisfaction 降速模式的直接先例)——均已合并 `main`(`89c996c`)
配套参考: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](2026-07-03-faketorio-design.md)、[`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md)(横切项"typed belt items"、P10 条目)

## 1. 目标

两件事,前者是后者的前置阻塞项:

**A. Typed belt items**——`BeltLane` 目前只表达"物品之间的间距"(FFF-176 gap 表示法),不记录物品是什么;`TryInsertAtBack()` 不收物品参数。这在 P2/P3 阶段是有意简化(先把间距推进/合并/拆分的数学做对),但路线图早已标注"P10 之前必须"补上——采矿机产出矿物到传送带,传送带另一头的机器/容器需要知道那是什么物品。

**B. 电力采矿机**——熔炉/装配机(P9)之外第三种"由电网供电、进度推进产出物品"的实体。footprint 覆盖区内找矿 → 电力检查(登记需求、读 satisfaction)→ 按挖掘速度等比推进 → 到点产出 1 件,放输出格(传送带优先,否则容器,否则原地等)。

**不做**:`RotateEntity` 命令(采矿机的输出方向复用现有 `Command.Rotation`,放置时定死,不能后续改向——同belt 已有的先例)、传送带↔库存双向搬运的通用机制(即"传送带一端插进箱子"这种，那是 P11 机械臂的范围,本计划只让**采矿机自己**能判断输出格是 belt 还是容器并相应地插入物品,不新增一个"belt 读容器/容器读 belt"的通用系统)、多目标同时挖矿(采矿机 footprint 内一次只锁定一个矿格)、矿石种类过滤(采矿机挖脚下有什么矿就出什么,不像熔炉那样按 category 匹配配方)。

## 2. 组件与文件结构

**新文件**

| 文件 | 职责 |
|---|---|
| `sim/Faketorio.Sim/Prototypes/MiningDrillPrototype.cs` | `sealed class MiningDrillPrototype : EntityPrototype`——`MiningSpeed`(`Q16`)、`EnergyUsageJPerTick`。 |
| `sim/Faketorio.Sim/MiningDrills.cs` | 采矿机运行时状态容器,**扁平 `namespace Faketorio.Sim;`**(同 `Machines`/`Player` 先例,类名不进子命名空间)。 |
| `data/base/mining-drill.json` | 一个电力采矿机 prototype。 |

**改动**

| 文件 | 改动 |
|---|---|
| `sim/Faketorio.Sim/Belts/BeltLane.cs` | 加一条与 `_gaps` 平行的 `_itemProtoIds`,`TryInsertAtBack` 加 `int itemProtoId` 参数,新增 `FrontItemProtoId` 只读属性(peek,不摘除),`ToAbsolutePositions`/`FromAbsolutePositions` 携带物品 id,`WriteState` 多写一个 `int`。 |
| `sim/Faketorio.Sim/Belts/BeltNetwork.cs` | `ConcatLanes`/`SplitBackLane`(合并/拆分时重建 lane)改用带物品 id 的位置元组,其余逻辑不变(`TryRemoveItemInRange` 只按位置摘除,不需要知道物品类型)。 |
| `sim/Faketorio.Sim/Simulation.cs` | **两次改动,分属两个 Task(见 §10 的顺序说明)**:① 传送带拐角交接的 `TryInsertAtBack()` 调用加 `FrontItemProtoId` 参数——`TryInsertAtBack` 签名一旦加了必选参数,这一行不改就编译不过,所以这一半在 Task 1 里做,不能拖到 Task 3。② `MiningDrills` 属性、`PlaceEntity`/`DestroyEntityAt` 给采矿机接入/摘出、`Step` 插入两趟采矿 tick、`WriteState` 追加——这一半在 Task 3。 |
| `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` | `Parse` 加 `"mining-drill"` arm;新 sibling pass 校验 `MiningSpeed`/`EnergyUsageJPerTick`。 |
| `sim/Faketorio.Sim.Tests/BeltLaneTests.cs`、`BeltNetworkTests.cs`、`BeltIntegrationTests.cs`、`DeterminismTests.cs` | 全部 78 处 `TryInsertAtBack()` 调用加物品 id 参数(纯位置/间距测试用同一个常量物品 id 即可,不影响原有断言);另外新增几条专门验证"物品类型随物品一起流动、合并、拐角交接"的测试(见 §7)。 |

## 3. Typed belt items:`BeltLane` 改造

```csharp
public sealed class BeltLane
{
    private readonly List<int> _gaps = new();
    private readonly List<int> _itemProtoIds = new();   // 与 _gaps 严格平行,同下标同物品
    private int _lineLengthSubTiles;
    private int _openIndex;

    public bool TryInsertAtBack(int itemProtoId)
    {
        int free = BackFreeSubTiles();
        if (free < ItemWidthSubTiles) return false;
        _gaps.Add(free - ItemWidthSubTiles);
        _itemProtoIds.Add(itemProtoId);
        return true;
    }

    // 队首物品的类型(只读,不摘除)。前置:IsFrontReady == true(同 RemoveFront 的前置)。
    public int FrontItemProtoId => _itemProtoIds[0];

    public void RemoveFront()
    {
        if (!IsFrontReady)
            throw new InvalidOperationException("RemoveFront called when front is not ready");
        _gaps.RemoveAt(0);
        _itemProtoIds.RemoveAt(0);
        if (_gaps.Count > 0)
            _gaps[0] += ItemWidthSubTiles;
        _openIndex = 0;
    }
    // ... Advance/IsFrontReady/ExtendBack/ExtendFront/ShrinkBack/ShrinkFront 不碰 _itemProtoIds,原样不变
}
```

**`TryRemoveItemInRange`**(P3c 拆传送带用)只按位置摘除,摘除下标时 `_itemProtoIds.RemoveAt(removeAt)` 与 `_gaps.RemoveAt(removeAt)` 同步执行——不需要知道被摘除的是什么物品,只需要保持两个列表下标对齐。

**`ToAbsolutePositions`/`FromAbsolutePositions`**(合并/拆分时的冷路径重建)签名改为携带物品 id:

```csharp
public readonly record struct PositionedItem(int LeadingEdgeSubTiles, int ItemProtoId);

public IReadOnlyList<PositionedItem> ToAbsolutePositions()
{
    var result = new PositionedItem[_gaps.Count];
    int pos = 0;
    for (int i = 0; i < _gaps.Count; i++)
    {
        pos += _gaps[i];
        result[i] = new PositionedItem(pos, _itemProtoIds[i]);
        pos += ItemWidthSubTiles;
    }
    return result;
}

public static BeltLane FromAbsolutePositions(int lineLength, IReadOnlyList<PositionedItem> positions)
{
    var lane = new BeltLane(lineLength);
    int prevTrailingEdge = 0;
    foreach (var p in positions)
    {
        int gap = p.LeadingEdgeSubTiles - prevTrailingEdge;
        if (gap < 0) throw new ArgumentException(/* 同现有报错文案,附物品 id 无意义,不加 */);
        lane._gaps.Add(gap);
        lane._itemProtoIds.Add(p.ItemProtoId);
        prevTrailingEdge = p.LeadingEdgeSubTiles + ItemWidthSubTiles;
    }
    if (prevTrailingEdge > lineLength) throw new ArgumentException(/* 同现有报错文案 */);
    return lane;
}
```

**`WriteState`**:每个物品多写一个 `int`(物品 id),紧跟在该物品的 gap 之后:

```csharp
public void WriteState(IStateWriter writer)
{
    writer.Write(_gaps.Count);
    for (int i = 0; i < _gaps.Count; i++)
    {
        writer.Write(_gaps[i]);
        writer.Write(_itemProtoIds[i]);
    }
}
```

（哈希格式变化,但项目没有跨版本存档兼容承诺,`DeterminismTests` 只比较同次运行两遍相同命令序列的哈希是否相等,不影响任何现有断言——同 P9 §4 库存 role 化时的先例。）

`BeltNetwork.cs` 里 `ConcatLanes`/`SplitBackLane`(合并/拆分时重建整条 lane)把 `int` 位置列表换成 `PositionedItem` 列表,逻辑不变(位置怎么平移/怎么过滤,物品 id 原样带过去,不参与任何计算)。`MiddleSplit`/`RemoveBelt` 里调用 `TryRemoveItemInRange` 的地方不需要改——它们只关心"摘掉了几个"(计数),不关心摘掉的是什么。

`Simulation.cs` 的拐角交接段:

```csharp
while (line.LaneA.IsFrontReady && down.LaneA.TryInsertAtBack(line.LaneA.FrontItemProtoId)) line.LaneA.RemoveFront();
while (line.LaneB.IsFrontReady && down.LaneB.TryInsertAtBack(line.LaneB.FrontItemProtoId)) line.LaneB.RemoveFront();
```

## 4. `MiningDrillPrototype`

```csharp
public sealed class MiningDrillPrototype : EntityPrototype
{
    public Q16 MiningSpeed { get; init; } = Q16.One;      // 进度倍率,M1 数据恒 1.0,JSON 不解析
    public long EnergyUsageJPerTick { get; init; }         // 每 tick 向电网登记的 PrimaryInput 需求
}
```

不引入"能挖哪些矿"的过滤字段——采矿机挖脚下(footprint 内)有什么矿出什么矿,`ResourcePrototype` 本身也没有分类概念,和熔炉靠 `Category` 匹配配方是两回事。校验(新 sibling pass,同 P9 `ResolveAndValidateCraftingMachines` 的写法):`EnergyUsageJPerTick >= 0`。`MiningSpeed` 不需要校验——同 P9 `CraftingSpeed` 的先例,JSON 不解析这个字段(永远是默认值 `Q16.One`),没有数据能把它配置成非法值,校验它是死代码。

## 5. `MiningDrills`:每采矿机运行时状态

**命名空间坑(同 P5 `Player`、P9 `Machines` 先例)**:类名不能进 `namespace Faketorio.Sim.MiningDrills` 之类的子命名空间——本类干脆不用文件夹,直接放 `sim/Faketorio.Sim/MiningDrills.cs`,`namespace Faketorio.Sim;`。

```csharp
namespace Faketorio.Sim;

public sealed class MiningDrills
{
    private readonly Dictionary<EntityId, DrillRuntimeState> _states = new();

    public void RegisterDrill(EntityId id) => _states[id] = new DrillRuntimeState(-1, -1, 0, false, 0);
    public void UnregisterDrill(EntityId id) => _states.Remove(id);

    public int GetTargetX(EntityId id) => _states.TryGetValue(id, out var s) ? s.TargetX : -1;
    public int GetTargetY(EntityId id) => _states.TryGetValue(id, out var s) ? s.TargetY : -1;
    public long GetProgress(EntityId id) => _states.TryGetValue(id, out var s) ? s.Progress : 0;
    public bool IsCompleted(EntityId id) => _states.TryGetValue(id, out var s) && s.Completed;
    public int GetPendingItemProtoId(EntityId id) => _states.TryGetValue(id, out var s) ? s.PendingItemProtoId : 0;

    public void SetTarget(EntityId id, int x, int y)
    {
        _ = _states[id];   // 前置:已注册(同 P9 Machines 的 throw-on-missing 风格)
        _states[id] = _states[id] with { TargetX = x, TargetY = y, Progress = 0 };
    }

    public void AddProgress(EntityId id, long delta)
    {
        var s = _states[id];
        _states[id] = s with { Progress = s.Progress + delta };
    }

    // 到点产出:记下待放置的物品 id,清目标(下 tick 重新搜矿——除非调用方判断
    // 矿格没挖空,那种情况下 Simulation 层会紧接着再调一次 SetTarget 保持同一目标,
    // 见 §6 第 5 步)。
    public void MarkCompleted(EntityId id, int pendingItemProtoId)
    {
        var s = _states[id];
        _states[id] = s with { Completed = true, PendingItemProtoId = pendingItemProtoId };
    }

    // flush 成功后调用:清 Completed/PendingItemProtoId/Progress,TargetX/Y 是否保留
    // 由调用方决定(矿格挖空传 -1/-1,没挖空传原目标)。
    public void ResetAfterFlush(EntityId id, int targetX, int targetY)
        => _states[id] = new DrillRuntimeState(targetX, targetY, 0, false, 0);

    public void WriteState(Faketorio.Sim.State.IStateWriter writer)
    {
        var ids = new List<EntityId>(_states.Keys);
        ids.Sort((a, b) => a.Index.CompareTo(b.Index));
        writer.Write(ids.Count);
        foreach (var id in ids)
        {
            var s = _states[id];
            writer.Write(id.Index); writer.Write(id.Generation);
            writer.Write(s.TargetX); writer.Write(s.TargetY);
            writer.Write(s.Progress); writer.Write(s.Completed ? (byte)1 : (byte)0);
            writer.Write(s.PendingItemProtoId);
        }
    }
}

internal readonly record struct DrillRuntimeState(int TargetX, int TargetY, long Progress, bool Completed, int PendingItemProtoId);
```

`MiningDrills` 不碰 `Simulation`/`Prototypes`/`Inventories`/`Belts`/`ElectricGrid`/`Resources`——只收裸 `EntityId`/`int`/`long`/`bool`,同 `Machines`/`ElectricGrid` 的隔离原则。目标搜索、资源查询、电网登记、库存/传送带插入,全部在 `Simulation.MiningDrillsTick*` 里做。

## 6. `Simulation` 里的两趟采矿 tick

和 P9 一样,需求登记必须在 `ElectricGrid.Settle()` 之前、读 satisfaction 必须在之后,所以也是两趟全量扫描,直接接在 P9 已有的 `MachinesTickPreSettle`/`Settle`/`MachinesTickPostSettle` 段后面(顺序不影响正确性,两个子系统互不相干,谁先谁后都行,按文件里出现顺序放在机器段之后即可)。

**`MiningDrillTickPreSettle(id, proto, x, y)`**:

1. **flush 已完成的产出**(`MiningDrills.IsCompleted(id)`):`itemId = MiningDrills.GetPendingItemProtoId(id)`;算出输出格坐标(见下方"输出格计算");
   - 输出格上有传送带线(`Belts.GetLineAt(outX, outY).IsValid`):`TryInsertAtBack` 本来就是"从队尾(入口侧)插入",不涉及朝向判断——同现有拐角交接的既有精神("不检查方向",见 `Simulation.cs` 现有注释),两条 lane 只是传送带物理上并排的两条独立物品流,都通向同一个 `Direction`。直接尝试 `LaneA.TryInsertAtBack(itemId)`,插不进(满)再试 `LaneB`,一条成功即算成功。
   - 没有传送带,输出格上有实体且有库存(`Inventories.GetInventoryId(outEntity)` 有效):`Insert(itemId, 1, itemProto.StackSize)`。
   - 都没有:跳过第 2 步(不重新搜矿/不清 `Completed`,下 tick 再试同一个 pending 产出,同熔炉输出堵塞的模式),但**仍然执行第 3 步**——阻塞状态的采矿机也照常产生待机能耗(同 P9 的"无条件登记"决定,不因为输出堵塞就免单)。
   - 成功后:检查目标矿格是否已挖空(`Resources.GetResourceAt(tx,ty).IsEmpty`)——挖空 `MiningDrills.ResetAfterFlush(id, -1, -1)`(下 tick 重新搜),没挖空 `MiningDrills.ResetAfterFlush(id, tx, ty)`(保持同一目标,继续挖)。**本 tick 内继续走到第 2 步**(同 P9 的 fallthrough 模式)。
2. **目标搜索**(仅当 `MiningDrills.GetTargetX(id) == -1`;第 1 步 flush 失败时跳过整个第 2 步):按 footprint 内 row-major 顺序(`for y in [proto.TileHeight); for x in [proto.TileWidth)`,先 y 后 x)扫描 `Resources.GetResourceAt(anchorX+dx, anchorY+dy)`,第一个非空的格 `MiningDrills.SetTarget(id, tx, ty)`。全空 → 保持 `(-1,-1)`。
3. **电力需求登记**(无条件执行,不受第 1/2 步结果影响——恒定待机能耗,同 P9):`ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick)`。

**`MiningDrillTickPostSettle(id, proto)`**:

4. **进度推进**(仅当 `TargetX != -1` 且未 `Completed`):
   ```csharp
   var cell = Resources.GetResourceAt(tx, ty);   // tx,ty = MiningDrills.GetTargetX/Y(id)
   var resProto = (ResourcePrototype)Prototypes.GetById(cell.ResourceProtoId);
   long threshold = (long)resProto.MiningTimeTicks << 16;
   var satisfaction = ElectricGrid.GetSatisfaction(id);
   long delta = proto.MiningSpeed.Mul(satisfaction.Raw);
   if (MiningDrills.GetProgress(id) < threshold) MiningDrills.AddProgress(id, delta);
   if (MiningDrills.GetProgress(id) < threshold) return;
   ```
5. **产出**:`itemProto = registry.Get<ItemPrototype>(resProto.MinableResult)`;`Resources.Extract(tx, ty, 1)`(必返回 1——矿格非空刚查过,且两台采矿机的 footprint 不可能重叠:`PlaceEntity` 靠 `World.IsAreaFree` 保证任意两个实体的占地互不相交,所以任意矿格任意时刻最多被一台采矿机锁定为目标,不存在两台机器同时对同一格调 `Extract` 的情形);`MiningDrills.MarkCompleted(id, itemProto.Id)`。

**输出格计算**(放置时定死,复用 `BeltNetwork.Delta`):
```csharp
var (dx, dy) = BeltNetwork.Delta(rotation);
int outX = anchorX + dx * proto.TileWidth;
int outY = anchorY + dy * proto.TileHeight;
```
（`rotation` 取自实体的 `EntityData.Rotation`,放置命令的 `Rotation` 字段——不新增字段,复用 belt 已经在用的同一个约定。方向 0/1/2/3 = 北/东/南/西,同 `Command.Rotation` 现有文档。）

`Step()` 里的位置(接在 P9 段之后):

```
应用命令
玩家 tick
电网 + 加工 tick(P9,已有)
电网 + 采矿 tick:① MiningDrillTickPreSettle(在 P9 的 pre-settle 之后、Settle() 之前一起登记)② ElectricGrid.Settle()(P9/采矿共用同一次结算,不重复调)③ ... ④ MiningDrillTickPostSettle
传送带推进 + 拐角交接
Tick++
```

**具体实现细节**:P9 已经把 `ElectricGrid.Settle()` 单独调了一次(在它的 pre/post 两段中间)。本计划的采矿 pre-settle 段插在 P9 的 `MachinesTickPreSettle()` 之后、`ElectricGrid.Settle()` 之前;采矿 post-settle 段插在 `ElectricGrid.Settle()`/发电机烧油/`MachinesTickPostSettle()` 之后。`Settle()` 本身只调一次,两个子系统的登记都在它之前完成、都在它之后读取——不需要为采矿机再调第二次 `Settle()`。

## 7. 数据

`data/base/mining-drill.json`:

```json
[
  { "type": "mining-drill", "name": "electric-mining-drill", "tileWidth": 2, "tileHeight": 2, "energyUsage": "90kW" }
]
```

不新增物品/配方——采矿机产出 `data/base` 里已有的 `iron-ore`/`copper-ore`/`coal`/`stone`(P6 已注册为 `ResourcePrototype.MinableResult`)。

## 8. 确定性

- `Progress`/`threshold` 全 `long`(Q16.16 定点),`MiningSpeed`/satisfaction 相乘走 `Q16.Mul`,无 `float`/`double`。
- 目标搜索固定 row-major 顺序,确定、可复现。
- 输出格计算是放置时 `Rotation` 的纯函数,不依赖任何运行时状态。
- 全量扫描按 `Entities` 池索引序(同 P9/发电机模式)。
- `MiningDrills.WriteState`/新 belt `WriteState` 都按固定序写。
- `BeltLane` 的 `_itemProtoIds` 与 `_gaps` 严格平行、同下标同物品,任何操作都成对更新,不存在"gap 有 N 个但 itemProtoIds 有 M 个"的不变式破坏可能——测试需覆盖这一点(见 §9)。

## 9. 测试

- **`BeltLaneTests`/`BeltNetworkTests`/`BeltIntegrationTests`/`DeterminismTests`**:全部 78 处 `TryInsertAtBack()` 加一个测试用常量物品 id(不影响原有位置/间距断言)。
- **`BeltLaneTests`** 追加:插入不同物品 id 后 `FrontItemProtoId` 分别正确;`RemoveFront` 后下一个物品的 `FrontItemProtoId` 正确(验证不是"总读下标 0 却物品已经错位");`ToAbsolutePositions`/`FromAbsolutePositions` 往返物品 id 不丢失。
- **`BeltNetworkTests`** 追加:三路合并(P3b)后原本两条线各自的物品类型在合并后的 lane 里保持正确顺序和类型;`RemoveBelt` 中间拆分(P3c)后前后两段各自的物品类型正确。
- **`SimulationTests`** 追加:拐角交接后下游线 `FrontItemProtoId` 与上游一致(证明 §3 的 `Simulation.cs` 改动生效,不是只改了 `BeltLane` 自己)。
- **`PrototypeLoaderTests`** 追加:`mining-drill.json` 正常加载;负例(`MiningSpeed`/`EnergyUsageJPerTick` 非法)。
- **`MiningDrillsTests`**(新文件,同 `MachinesTests` 风格):`RegisterDrill`/`UnregisterDrill`/`SetTarget`/`AddProgress`/`MarkCompleted`/`ResetAfterFlush` 独立行为;未注册实体调用写方法抛 `KeyNotFoundException`(同 P9 的一致性要求,一次做对不留后续修复轮);`WriteState` 按 index 排序。
- **`SimulationTests`** 追加:footprint 内自动找矿并挖出;矿格挖空后自动换到 footprint 内下一个矿格;输出到相邻箱子;输出到相邻传送带(验证传送带 lane 上出现正确物品 id);两者都没有时产出堵在机器里、腾出空间后下 tick 自动 flush;电力不足时挖矿变慢(同 P9 的 satisfaction 降速测试模式,注意本计划的电力测试也需要摆电线杆+发电机+燃料,同 P9 Ruling 1 的教训)。
- **`DeterminismTests`** 追加:放采矿机(有矿的位置)+ 电线杆 + 发电机,跑够挖出至少一件矿物的 tick 数,同命令两遍逐 tick 哈希全等。

## 10. 实施拆分

- **Task 1**(typed belt items 本体):`BeltLane` 改造(`_itemProtoIds`/`TryInsertAtBack`/`FrontItemProtoId`/`ToAbsolutePositions`/`FromAbsolutePositions`/`WriteState`)+ `BeltNetwork.ConcatLanes`/`SplitBackLane` 适配 + **`Simulation.cs` 拐角交接那两行改用 `FrontItemProtoId`(必须包含在这个 Task 里——`TryInsertAtBack` 加了必选参数后,不改这两行整个项目编译不过,不能留到 Task 3)** + 全部 4 个既有测试文件的 78 处调用点迁移 + `BeltLaneTests`/`BeltNetworkTests`/`SimulationTests` 新增的物品类型正确性测试(§9 的拐角交接测试也跟着挪进这个 Task,因为它验证的正是这两行)。不碰任何 P10 新文件(`MiningDrillPrototype`/`MiningDrills`/`mining-drill.json`)。
- **Task 2**(采矿机原型 + 状态类):`MiningDrillPrototype` + `PrototypeLoader` 解析(新 type + 新 sibling pass)+ `data/base/mining-drill.json` + `MiningDrills` 状态类 + `MiningDrillsTests`。依赖 Task 1 已合并(不直接用 Task 1 的接口,但共用分支历史)。不碰 `Simulation.cs`。
- **Task 3**(接线):`Simulation.cs` 里剩下那一半——`MiningDrills` 属性、`PlaceEntity`/`DestroyEntityAt` 钩子、两趟采矿 tick、`WriteState` 追加(依赖 Task 2)+ `SimulationTests`(找矿/挖空换目标/输出到箱子/输出到传送带/堵塞/降速)+ `DeterminismTests`。必须最后做。

每个 Task 走完整「实现→审查→(修复→复审)」闭环。
