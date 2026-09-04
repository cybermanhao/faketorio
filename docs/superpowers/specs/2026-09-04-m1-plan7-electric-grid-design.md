# M1 Plan 7 — 电网(ElectricGrid + 燃料发电机)设计

日期: 2026-09-04
状态: 已与用户确认的设计基线
前置依赖: P1(模拟核心)、P4(库存)、P5(玩家,`TransferToEntity`/`TransferFromEntity` 复用其 reach 判定)、P6(`ValueNoise.Isqrt`)——均已合并进 `main`(`a5f569d`)
配套参考: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](2026-07-03-faketorio-design.md)(§5.4 电力网络、§5.6 数值表示约定)、[`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md)(P7 条目)

## 1. 目标

给模拟层一个**电力网络**:电线杆按连线距离组成连通分量(网络),供电覆盖区内的生产者/消费者按位置查询接入网络,每 tick 每网络汇总结算一次(不是逐机器各自算电),按 `usage_priority` 六档分优先级分配,供不应求时按比例广播 satisfaction 系数。MVP 发电只有**燃料直接发电机**(Burner 入 + Electric primary-output 出)。P7 是 P9(加工状态机)、P10(电力采矿机)的共同前置。

顺带交付一个**通用的玩家↔实体物品转移命令**(`TransferToEntity`/`TransferFromEntity`)——发电机加燃料需要它,箱子等其他有库存的实体以后也复用。

**不做**:太阳能板、蓄电池/储能实体(`Solar`/`Tertiary` 档占位但无对应 prototype)、`SecondaryOutput`/`SecondaryInput` 的真实实体、真正的电力消费者(P9/P10 的机器——P7 只交付登记 API)、电线视觉渲染、电线杆数量大时的空间索引优化(横切基准测试项处理)。

## 2. 组件与文件结构

**新文件**

| 文件 | 职责 |
|---|---|
| `sim/Faketorio.Sim/Electric/ElectricGrid.cs` | 电网管理器:电线杆连通分量(全量重算)、`FindNetworkAt(x,y)` 位置查询、供给/需求登记 + 每 tick 结算(`Settle`)、发电机注册与燃料推进、`WriteState`。 |
| `sim/Faketorio.Sim/Electric/UsagePriority.cs` | 完整 6 档 `enum UsagePriority : byte`。 |
| `sim/Faketorio.Sim/Electric/NetworkId.cs` | `readonly record struct NetworkId(int Index)`,`Invalid = new(-1)`;不跨 tick 保留,每次 `EnsureTopology()` 结果里才有效。 |
| `sim/Faketorio.Sim/Prototypes/ElectricPolePrototype.cs` | `MaximumWireDistanceTiles`、`SupplyAreaDistanceTiles`。 |
| `sim/Faketorio.Sim/Prototypes/FuelGeneratorPrototype.cs` | `PowerOutputJPerTick`、`FuelItemName` → 解析后 `FuelItemProtoId`。 |
| `data/base/electric.json` | 电线杆 + 燃料发电机的 prototype。 |

**改动**

| 文件 | 改动 |
|---|---|
| `sim/Faketorio.Sim/Items/Inventories.cs` | `AddContainer` 加可选 `readOnly = false`、`filterItemProtoId = 0` 参数(向后兼容,现有箱子调用不变)。 |
| `sim/Faketorio.Sim/Commands/Command.cs` | `CommandType` 加 `TransferToEntity`、`TransferFromEntity`;`Command` 加 `int Count` 字段(现有命令类型都不用,向后兼容)。 |
| `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` | `Parse` 加 `"electric-pole"`/`"fuel-generator"` 两个 arm;post-`AssignIds` pass 里解析 `FuelItemName` → `FuelItemProtoId` + 校验。 |
| `sim/Faketorio.Sim/Simulation.cs` | `ElectricGrid` 属性;`Apply` 加 2 个物品转移 case;`PlaceEntity`/`RemoveEntity` 给电线杆/发电机接入/摘出电网;`Step` 插入电网结算段;`WriteState` 追加。 |

`ElectricGrid` 不枚举"谁是电力实体"——只维护电线杆连通分量 + 位置查询。发电机(现在)与未来的消费者(P9/P10)各自在放置时主动登记「我在这,我是生产者/消费者」,网络归属由查询解析,P7 不用预知未来消费者的 prototype 形状。

## 3. 电线杆连通分量(全量重算)

```csharp
public sealed class ElectricGrid
{
    private readonly Dictionary<EntityId, PoleInfo> _poles = new();
    private bool _topologyDirty = true;
    private List<Network> _networks = new();

    public void RegisterPole(EntityId id, int x, int y, ElectricPolePrototype proto);
    public void UnregisterPole(EntityId id);
}

internal readonly record struct PoleInfo(int X, int Y, int MaximumWireDistanceTiles, int SupplyAreaDistanceTiles);

internal sealed class Network
{
    public List<EntityId> Poles = new();   // 按 EntityId.Index 升序
}
```

- `RegisterPole`/`UnregisterPole`:只置 `_topologyDirty = true`,不立即重算。
- `EnsureTopology()`(结算前调一次,`_topologyDirty` 才真正重算,否则直接返回):对 `_poles` 按 `EntityId.Index` 升序遍历;两根电线杆若欧氏距离(`ValueNoise.Isqrt`,整数 tile 坐标,建筑无子格精度、不乘 256)`≤ min(两侧 MaximumWireDistanceTiles)` 则连边(取更严格一侧,防止单向假连通);并查集求连通分量;分量内电线杆按 `EntityId.Index` 排序存进 `Network`;`_networks` 列表本身按每个分量最小 `EntityId.Index` 升序排列(确定的网络索引序,供 §4 覆盖区重叠决胜用)。
- 复杂度 O(N²)(N = 电线杆数)。M1 无基准测试兜底,先用最简单的正确实现;性能优化是横切基准测试项该解决的,不在 P7 范围。
- `Network` 不跨 tick 保留身份——每次 `EnsureTopology()` 整体重建;发电机的燃料缓冲(§6)是发电机自己的状态,不挂在 `Network` 上。

## 4. 供电覆盖区(方形)+ `FindNetworkAt`

```csharp
public NetworkId FindNetworkAt(int x, int y)
{
    EnsureTopology();
    for (int ni = 0; ni < _networks.Count; ni++)
        foreach (var poleId in _networks[ni].Poles)
        {
            var p = _poles[poleId];
            if (Math.Max(Math.Abs(x - p.X), Math.Abs(y - p.Y)) <= p.SupplyAreaDistanceTiles)
                return new NetworkId(ni);
        }
    return NetworkId.Invalid;
}
```

- Chebyshev(`max(|dx|,|dy|)`)——方形覆盖区,贴近 Factorio `supply_area_distance` 实际实现。
- 两个不同网络的覆盖区在同一格重叠:遍历顺序是网络索引序 → 网络内电线杆 `EntityId.Index` 序,第一个覆盖到的赢,确定、可复现。
- 查询即时算,不缓存绑定在生产者/消费者身上——生产者/消费者每次要用电网时都查一次(§6:发电机每 tick 查一次)。M1 电线杆/发电机数量小,重复查询可接受。

## 5. usage_priority 六档 + 每 tick 结算

```csharp
public enum UsagePriority : byte
{
    Solar = 0, PrimaryOutput = 1, SecondaryOutput = 2,   // 供给侧,数值越小越先用(越"便宜"越先用)
    PrimaryInput = 3, SecondaryInput = 4, Tertiary = 5,  // 需求侧,数值越小越先满足
}
```

**登记 API**(生产者/消费者每 tick 结算前调用;M1 里只有燃料发电机是真实生产者,消费者登记 API 现在没有真实调用方,P7 自己的测试直接调它验证结算数学):

```csharp
public void RegisterSupply(EntityId id, int x, int y, UsagePriority priority, long maxJThisTick);
public void RegisterDemand(EntityId id, int x, int y, UsagePriority priority, long amountJ);
```

两者内部先 `FindNetworkAt(x,y)`;查不到网络(没接上电线杆)→ 不进结算,生产者 `GetAllocatedSupply` 恒 0、消费者 `GetSatisfaction` 恒 0。登记结果暂存在 `ElectricGrid` 内部按网络分组的列表里(`Dictionary<NetworkId, List<(EntityId, UsagePriority, long)>>`,供给/需求各一份),`Settle()` 读取后清空,供下一 tick 重新登记。**每个实体每 tick 只登记一次**(生产者/消费者各自的 tick 逻辑只调用一次 `RegisterSupply`/`RegisterDemand`);同一实体同一 tick 内重复登记不是受支持/受测试的用法。

**`Settle()` 完整算法**(每网络独立结算):

1. `totalDemand = Σ` 本 tick 该网络所有已登记需求(不分档先求和)。
2. **供给侧**按 `Solar → PrimaryOutput → SecondaryOutput` 顺序:`used = min(该档登记容量合计, 剩余未覆盖需求)`;该档内每个生产者的**实际产出** = `登记容量 × (used / 该档容量合计)`(Q16 比例;容量合计为 0 时该档无生产者,跳过)——避免"有富余仍然满功率烧油"。剩余未覆盖需求扣减 `used`,进入下一档。
3. 供给侧走完得到 `shortfall = 剩余未覆盖需求`(可能是 0)。
4. **需求侧**按反向优先级 `Tertiary → SecondaryInput → PrimaryInput` 吸收 `shortfall`(先牺牲低优先级):`absorbed = min(该档需求合计, shortfall)`;该档 `satisfaction = Q16.FromRatio(该档需求合计 − absorbed, 该档需求合计)`(需求合计为 0 时该档无消费者,`satisfaction` 无意义、不产出);`shortfall -= absorbed`。`shortfall` 归零后,更高优先级档一律 `satisfaction = Q16.One`。
5. 存两张表(`Dictionary<EntityId, ...>`,`Settle()` 每次调用重建):`GetAllocatedSupply(EntityId) → long`(生产者用,§6 烧油)、`GetSatisfaction(EntityId) → Q16`(消费者用)。

M1 只有 `PrimaryOutput`/`PrimaryInput` 两档非空,算法在其余档上是空合计(0 生产者/0 需求),等价于直接跳过,不用特判。

## 6. 燃料发电机

```csharp
public sealed class FuelGeneratorPrototype : EntityPrototype
{
    public long PowerOutputJPerTick { get; init; }   // "powerOutput": "90kW" -> Units.ParsePower
    public required string FuelItemName { get; init; }
    public int FuelItemProtoId { get; internal set; } // 解析 pass 填,同 P5 配方名字解析模式
}
```

`ElectricGrid` 额外维护 `Dictionary<EntityId, long> _fuelBufferJ`(能量缓冲,J),配合 `Inventories` 的燃料槽(1 槽,`filterItemProtoId = FuelItemProtoId`)。

- `RegisterGenerator(EntityId id, int x, int y, long powerOutputJPerTick)` / `UnregisterGenerator(EntityId id)`:放置/拆除时调,`_fuelBufferJ[id]` 初始 0,拆除时该键剩余能量丢弃(同库存 §10 分层约定的丢弃策略)。

**发电机每 tick**(§7 定具体调用位置):

1. **登记**(结算前):`long potential = _fuelBufferJ[id] > 0 || fuelInv.CountOf(FuelItemProtoId) > 0 ? PowerOutputJPerTick : 0;` → `RegisterSupply(id, x, y, UsagePriority.PrimaryOutput, potential)`。
2. **`Settle()`**(§5)。
3. **烧油结算**(结算后):`actual = GetAllocatedSupply(id)`;若 `_fuelBufferJ[id] < actual` 且燃料槽有货,循环 `fuelInv.Remove(FuelItemProtoId, 1); _fuelBufferJ[id] += fuelItemProto.FuelValueJ;` 直到够付或槽空;`delivered = min(actual, _fuelBufferJ[id]); _fuelBufferJ[id] -= delivered;`(`delivered` 理论上恒等于 `actual`——第 1 步登记时已确认有燃料,登记与结算之间燃料槽不会被外部改动)。

**放置/拆除接入**:`PlaceEntity` 遇 `FuelGeneratorPrototype` → `Inventories.AddContainer(id, 1, filterItemProtoId: proto.FuelItemProtoId)` + `ElectricGrid.RegisterGenerator(id, x, y, proto.PowerOutputJPerTick)`。`RemoveEntity` 对称摘除。

`data/base/electric.json` 示意:

```json
[
  { "type": "electric-pole", "name": "small-electric-pole", "tileWidth": 1, "tileHeight": 1,
    "maximumWireDistanceTiles": 7, "supplyAreaDistanceTiles": 2 },
  { "type": "fuel-generator", "name": "burner-generator", "tileWidth": 2, "tileHeight": 2,
    "powerOutput": "90kW", "fuelItemName": "coal" }
]
```

## 7. 通用玩家↔实体物品转移命令

**命令**(`Command` 加 `int Count` 字段,现有命令类型都不用,向后兼容):

| 命令 | 字段 | 效果 |
|---|---|---|
| `TransferToEntity` | `X`/`Y` = 目标格,`ProtoId` = item id,`Count` = 数量 | 玩家背包 → 目标实体库存 |
| `TransferFromEntity` | 同上 | 目标实体库存 → 玩家背包 |

**`TransferToEntity`**(`Apply` 里):
1. reach 判定(复用 P5 的 `ValueNoise.Isqrt` 到目标格中心,同 `HandMine`)——超出 `ReachSubTiles` → 拒绝(`RejectedCommandCount++`)。
2. `World.GetEntityAt(X,Y)` 无效,或该实体没有库存(`Inventories.GetInventoryId` 无效)→ 拒绝。
3. `Count <= 0` → 拒绝。
4. `amount = min(Count, Player.Inventory.CountOf(itemId))`;`amount <= 0` → 拒绝(背包里没有)。
5. `inserted = targetInv.Insert(itemId, amount, itemProto.StackSize)`;`Player.Inventory.Remove(itemId, inserted)`。`inserted` 可能小于 `amount`(目标库存放不下部分),不算失败,尽量转移。

**`TransferFromEntity`** 对称,唯一区别:玩家背包放不下部分时,把差额**放回**源库存(`sourceInv.Insert(itemId, removed - inserted, stackSize)`),避免物品凭空消失。

**`Inventories.AddContainer` 扩展**(向后兼容):
```csharp
public InventoryId AddContainer(EntityId entity, int slotCount, bool readOnly = false, int filterItemProtoId = 0);
```
现有调用(箱子)不传后两个参数,行为不变。发电机燃料槽传 `filterItemProtoId`(§6)。

这条命令通用——箱子、发电机、以后任何有库存的实体都能用它转移物品。

## 8. Simulation 接线

- `public ElectricGrid ElectricGrid { get; } = new();`。
- **`Step` 顺序**(在 P5 已有的基础上插「电网 tick」段):
  ```
  应用命令(含 TransferToEntity/TransferFromEntity)
  玩家 tick(行走/挖掘/合成,P5 已有)
  电网 tick:① 发电机登记供给 ② ElectricGrid.Settle() ③ 发电机烧油结算
  传送带推进(现有)
  传送带拐角交接(现有)
  Tick++
  ```
- **`Apply`** 加 2 个物品转移 case(§7);`PlaceEntity`/`RemoveEntity` 加电线杆(`RegisterPole`/`UnregisterPole`)和发电机(`RegisterGenerator`/`UnregisterGenerator` + 燃料槽)的钩子,和现有 belt/container 钩子并列。
- **`WriteState`**:`ElectricGrid.WriteState(writer)` 追加在 `Player.WriteState` 之后(当前最后一项)。

## 9. 确定性

- 全 `int`/`long`,连线/供电覆盖区距离整数(建筑无子格精度,不用 P5 那种 ×256);比例用 `Q16`。无 `float`/`double`。
- 连通分量重算按 `EntityId.Index` 升序两两比较,结果确定;`FindNetworkAt` 按网络索引序 + 网络内 `Index` 序,重叠覆盖区决胜确定。
- `Settle()` 对每网络、每档,登记序固定(遍历发电机按 `EntityId.Index` 序登记;未来消费者也会用同样确定的顺序注册)。
- **`WriteState`**:电线杆连通分量本身不序列化(`_networks` 是每次 `EnsureTopology()` 前的存活电线杆纯派生数据,和传送带 §10 分层约定同理——结构层写,派生缓存不写);要写的是**发电机的燃料缓冲**(`_fuelBufferJ`,按 `EntityId.Index` 序)。`satisfaction`/`GetAllocatedSupply` 结果每 tick 用完即弃,不单独写进状态(除非未来 P9/P10 把它累进自己的 progress 状态,那是它们自己状态的一部分,不是 P7 的)。

## 10. 测试

- **`ElectricGridTests`**:
  - 连通分量:两根电线杆距离 ≤ 连线半径 → 同网络;超出 → 两个网络;三根链式(A-B 相连、B-C 相连、A-C 超距)→ 一个网络;`MaximumWireDistanceTiles` 不对称(A 说能连 10、B 只能连 5,间距 7)→ 不连通(取更严格一侧)。
  - `FindNetworkAt`:覆盖区内命中;覆盖区外(方形边界外,对角线距离超但 Chebyshev 未超的格要命中)不误判;两网络覆盖区重叠 → 索引序决胜确定可复现。
  - 结算:单发电机单消费者,供给 ≥ 需求 → `satisfaction = Q16.One`、发电机实际产出 = 需求(不多烧,验证比例产出而非满功率);供给 < 需求 → `satisfaction` 按比例、发电机满功率产出;两发电机分摊(各自按比例产出,不是一个满一个空);多档需求(手动登记 `PrimaryInput` + `Tertiary`)→ shortfall 先吃 `Tertiary`,`PrimaryInput` 优先保满。
  - 无电线杆覆盖的生产者/消费者 → `GetAllocatedSupply`/`GetSatisfaction` 恒 0。
- **`SimulationTests`** 追加:放两根电线杆(间距在连线范围内)→ `ElectricGrid.FindNetworkAt` 在两杆覆盖区各命中同一网络;放发电机 + `TransferToEntity` 塞煤 → 燃料槽有货;拆电线杆 → 网络拆分(`FindNetworkAt` 观察不到之前能查到的联通)。
- **`PrototypeLoaderTests`** 追加:`electric.json` 正常加载;`FuelItemName` 解析成 `FuelItemProtoId`;负例(引用不存在的 item、`MaximumWireDistanceTiles`/`SupplyAreaDistanceTiles`/`PowerOutputJPerTick` 非正数)。
- **`InventoriesTests`** 追加:`AddContainer` 的 `filterItemProtoId`/`readOnly` 参数生效(过滤非目标物品、只读拒绝写入)。
- **`DeterminismTests`** 追加:放电线杆 + 发电机 + `TransferToEntity` 塞煤 + 手动登记一次测试用需求,跑若干 tick,同命令两遍逐 tick 哈希全等;现有 golden 场景仍过。

## 11. 实施拆分

- **Task 1**:`ElectricPolePrototype` + `FuelGeneratorPrototype` + `UsagePriority` + `NetworkId` + `PrototypeLoader` 解析(两个 type + 燃料名解析 pass + 校验)+ `data/base/electric.json` + `Inventories.AddContainer` 扩展参数 + loader/`Inventories` 测试。不碰 `Simulation`。
- **Task 2**:`ElectricGrid` 连通分量(`RegisterPole`/`UnregisterPole`/`EnsureTopology`)+ `FindNetworkAt` + `ElectricGridTests`(连通性 + 位置查询部分)。
- **Task 3**:`ElectricGrid` 结算(`RegisterSupply`/`RegisterDemand`/`Settle`/`GetAllocatedSupply`/`GetSatisfaction`)+ `WriteState` + `ElectricGridTests`(结算部分)。
- **Task 4**:燃料发电机接入(`RegisterGenerator`/`UnregisterGenerator` + 每 tick 登记/烧油)+ `Command` 加 `TransferToEntity`/`TransferFromEntity` + `Simulation` 接线(`ElectricGrid` 属性、`Step` 电网段、`PlaceEntity`/`RemoveEntity` 钩子、`WriteState`)+ `SimulationTests` + `DeterminismTests`。

每个 Task 走完整「实现→审查→(修复→复审)」闭环。
