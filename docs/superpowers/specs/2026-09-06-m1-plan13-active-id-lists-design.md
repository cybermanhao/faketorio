# M1 Plan 13 — 有序活跃 id 列表(消除每 tick 全实体扫描)设计

日期: 2026-09-06
状态: 已与用户确认的设计基线
前置依赖: P1–P12 全部已合并 `main`(`e1a4655`)。特别是 P9(`Machines`)、P10(`MiningDrills`)、P11(`Inserters`)的运行时状态容器,P7(`ElectricGrid` 的发电机簿记),P12(`Faketorio.Sim.Bench` —— 本子项的收益台架 + 零行为变化的硬证明)。
配套参考: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](2026-07-03-faketorio-design.md)(§5.3 实体休眠 / 性能地基)、[`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md)(横切"实体休眠 / 活跃列表"条目)、[`docs/superpowers/specs/2026-09-05-m1-plan12-ci-benchmark-baseline-design.md`](2026-09-05-m1-plan12-ci-benchmark-baseline-design.md)

## 1. 目标

`Simulation.Step()` 里有 8 个 tick 循环,每个都是 `for (int i = 0; i < Entities.Capacity; i++)`,对**每个实体槽位**做"存活?" + `Prototypes.TryGetById` + `is not XPrototype` 类型过滤,只为找出属于自己那个子系统的少数实体。P12 的分阶段计时(scale 50 / 5000 tick)实测:`Inserters` 44% / `MiningDrills` 29% / `Machines` 15% / `Electric` 12% 的每 tick 成本几乎全花在这些全量扫描上 —— `Entities.Capacity` 是历史高水位、只增不减,而基准场景里一个子系统实际只有 100–320 个实体、总实体 ~3050。

每个子系统的运行时状态容器**已经**有一个 `Dictionary` 只装自己的实体(`Machines._states`、`MiningDrills._states`、`Inserters._states`、`ElectricGrid._fuelBufferJ`)。本子项让这 4 个容器各长出一个**按 `EntityId.Index` 升序维护的活跃 id 列表**,8 个 tick 循环改成 `foreach (var id in <容器>.ActiveIds)`。

**这是纯性能改造,零可观测行为变化。** 遍历的实体集合、遍历顺序、每个实体的处理逻辑都逐字不变。`bench` 的 golden 状态哈希(`finalHash` + 4 个 `sampleHashes`)在实现前后必须完全一致 —— 这是本子项"没改坏任何东西"的硬证明,变了即红。

**这是"实体休眠 / 活跃列表"路线的第一步(换迭代源)。** 真休眠(给实体加"睡着"标记、tick 跳过、触发器唤醒)是独立的后续子项,不在本次范围 —— 因为唤醒条件(尤其机械臂 vs 每 tick 在动的传送带)是确定性雷区、值得单独一轮 brainstorm,且和"睡着的实体还耗不耗电"这种会改电力模型 + 改 golden 的决策耦合。

**不做**(明确排除):

- 不做真休眠 / 唤醒机制。空闲实体照样每 tick 被遍历、跑完整逻辑分支、无条件登记待机能耗(维持 P9/P10/P11 的既有语义)。
- 不动 belt 推进 / 交接两个循环(P12 实测 <0.5% 每 tick,且 `BeltNetwork` 是独立池,`Belts.Capacity` 扫描不在本次范围)。
- 不动 `ElectricGrid.Settle()` 的图求解(不是全实体扫描,是另一类优化)。
- 不动 `Simulation.WriteState` 里的 `Entities.Capacity` 扫描(那是哈希序列化,不是 tick 成本,必须保持完整有序扫描)。
- 不把 `Prototypes.GetById(data.ProtoId)` 的结果缓存进 state(另一个微优化,留着)。
- 不碰 `EntityPool` / `Entities` 的公开 API,不碰任何 prototype / `data/base` / `bench/golden.json`。

## 2. 组件与文件结构

**改动**

| 文件 | 改动 |
|---|---|
| `sim/Faketorio.Sim/Machines/Machines.cs` | 加 `private readonly List<EntityId> _order = new();`;`RegisterMachine` 有序插入、`UnregisterMachine` 移除;加 `public IReadOnlyList<EntityId> ActiveIds => _order;`。`WriteState` 可改用 `_order`(已有序,省掉临时 `List` + `Sort`)——见 §5。 |
| `sim/Faketorio.Sim/MiningDrills.cs` | 同上:`_order` + `RegisterDrill`/`UnregisterDrill` 维护 + `ActiveIds`。 |
| `sim/Faketorio.Sim/Inserters.cs` | 同上:`_order` + `RegisterInserter`/`UnregisterInserter` 维护 + `ActiveIds`。 |
| `sim/Faketorio.Sim/Electric/ElectricGrid.cs` | 加 `private readonly List<EntityId> _generatorOrder = new();`;`RegisterGenerator` 有序插入、`UnregisterGenerator` 移除;加 `public IReadOnlyList<EntityId> GeneratorIds => _generatorOrder;`。`_fuelBufferJ`(`Dictionary`)保持不动,所有 getter 仍 O(1)。 |
| `sim/Faketorio.Sim/Simulation.cs` | 8 个 tick 方法把 `for (int i = 0; i < Entities.Capacity; i++) { …类型过滤… }` 换成 `foreach (var id in <容器>.ActiveIds) { var data = Entities.Get(id); … }`(见 §4)。`InserterTickPostSettle` 里 `delta` 的计算从方法头挪到阶段 A 之后(见 §4.3)。 |

**新增测试**

| 文件 | 内容 |
|---|---|
| `sim/Faketorio.Sim.Tests/ActiveIdListTests.cs` | 4 个容器的有序插入 / 删除 / 集合一致性单测(见 §6)。 |

**无新文件、无新项目、无 API 破坏**(`ActiveIds` / `GeneratorIds` 是纯新增只读属性)。

## 3. `_order` 的维护

### 3.1 有序插入(`Register*` / `RegisterGenerator`)

`EntityPool` 复用被释放的槽位索引,所以新实体的 `EntityId.Index` 可能落在现有活跃实体的 `Index` 之间 —— 必须二分定位后 `Insert`,不能 append:

```csharp
private void InsertOrdered(EntityId id)
{
    int lo = 0, hi = _order.Count;
    while (lo < hi)
    {
        int mid = (lo + hi) >> 1;
        if (_order[mid].Index < id.Index) lo = mid + 1;
        else hi = mid;
    }
    _order.Insert(lo, id);
}
```

`Register*` 现在是 `_states[id] = new XState(...)`(幂等 upsert);加 `_order` 后要保证 `_order` 不出现重复 id。约定:`Register*` 只在 `Simulation.PlaceEntity` 里对一个**刚 `Entities.Create()` 出来的新 id** 调用一次,不会对已在 `_order` 里的 id 重复调。实现时加一个 `Debug.Assert` 或在 `InsertOrdered` 里跳过已存在的 id(二分位置的 `_order[lo].Index == id.Index` 时不插)——**选后者**,防御性且 O(1) 额外判断。

### 3.2 删除(`Unregister*` / `UnregisterGenerator`)

```csharp
_states.Remove(id);           // 或 _fuelBufferJ.Remove(id)
_order.Remove(id);            // List<T>.Remove:线性查找 + 移位,O(N)
```

`EntityId` 是 `readonly record struct`,`_order.Remove(id)` 用值相等(`Index` + `Generation` 都比),不会误删复用了同一 `Index` 的新实体(新实体此刻还没进 `_order`)。

### 3.3 频率

`Register*` / `Unregister*` 只在 `Simulation.PlaceEntity` / `DestroyEntityAt` 里调用,这两个只在 `Step()` 的 Commands 阶段(`Apply(in command)`)执行。相对每 tick 8 趟 `ActiveIds` 遍历,放置/拆除是低频事件,O(N) 的插入/删除可忽略。

## 4. tick 方法改造

### 4.1 改造模式(8 处一致)

改造前(以 `MachinesTickPreSettle` 为例):
```csharp
for (int i = 0; i < Entities.Capacity; i++)
{
    if (!Entities.IsAliveAtIndex(i)) continue;
    ref var data = ref Entities.GetAtIndex(i);
    if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not CraftingMachinePrototype proto) continue;
    var id = new EntityId(i, Entities.GenerationAtIndex(i));
    MachineTickPreSettle(id, proto, data.X, data.Y /* …按各方法实际入参 */);
}
```

改造后:
```csharp
foreach (var id in Machines.ActiveIds)
{
    var data = Entities.Get(id);                                   // 取 X/Y/Rotation,只读
    var proto = (CraftingMachinePrototype)Prototypes.GetById(data.ProtoId);
    MachineTickPreSettle(id, proto, data.X, data.Y /* … */);
}
```

- `ActiveIds` 的成员资格已保证"存活 + 属于本子系统",外层 `IsAliveAtIndex` + `TryGetById` + `is not XPrototype` 三重过滤全删。
- `data` 从 `ref` 取变成按值取(`Entities.Get(id) -> EntityData`)——8 个 tick 方法都只**读** `data.X` / `data.Y` / `data.Rotation`,不写,按值安全。若 `Entities` 没有 `EntityData Get(EntityId)` 的公开方法(P11 的 Inserter 代码已用 `Entities.Get(pickEntity)`,应当有),实现时确认签名;没有则用现有等价手段取只读快照,**不新增 `Entities` API**。
- `Prototypes.GetById` 从"可能失败的 `TryGetById` + 类型检查"变成"直接 cast"——`ActiveIds` 里的 id 必然是本类型,cast 不会失败。

### 4.2 8 个改造点

| tick 方法(`Simulation.cs` 约行号) | 遍历源 | 内层单实体方法 |
|---|---|---|
| `MachinesTickPreSettle`(~552) | `Machines.ActiveIds` | `MachineTickPreSettle` |
| `MachinesTickPostSettle`(~615) | `Machines.ActiveIds` | `MachineTickPostSettle` |
| `MiningDrillsTickPreSettle`(~664) | `MiningDrills.ActiveIds` | `MiningDrillTickPreSettle` |
| `MiningDrillsTickPostSettle`(~740) | `MiningDrills.ActiveIds` | `MiningDrillTickPostSettle` |
| `InsertersTickPreSettle`(~774) | `Inserters.ActiveIds` | (内联 `RegisterDemand`) |
| `InsertersTickPostSettle`(~788) | `Inserters.ActiveIds` | `InserterTickPostSettle` |
| `ElectricGeneratorsRegisterSupply`(~513) | `ElectricGrid.GeneratorIds` | (内联) |
| `ElectricGeneratorsBurnFuel`(~528) | `ElectricGrid.GeneratorIds` | (内联) |

内层单实体方法(`MachineTickPreSettle` 等)的签名和函数体**完全不动**。

### 4.3 微优化:`InserterTickPostSettle` 的 `delta`

现在方法头无条件算:
```csharp
long delta = proto.RotationSpeed.Mul(ElectricGrid.GetSatisfaction(id).Raw);
```
阶段 A(`held == 0 && progress == 0`,尝试抓取)不使用 `delta`,却每个停在抓取角的空闲机械臂每 tick 白算一次(含一次 `GetSatisfaction` 字典查表)。把 `delta` 的声明+计算移到阶段 A 的 `return` 之后、阶段 B 之前。阶段 B / C 里 `delta` 的值和现在逐字相同(`GetSatisfaction` 在 `Settle()` 之后调用,值已确定,和计算位置无关)。

## 5. `WriteState` 顺带简化(可选,低风险)

`Machines` / `MiningDrills` / `Inserters` 的 `WriteState` 现在是:
```csharp
var ids = new List<EntityId>(_states.Keys);
ids.Sort((a, b) => a.Index.CompareTo(b.Index));
foreach (var id in ids) { ... }
```
`_order` 已经是同一个升序,可直接 `foreach (var id in _order)`,省掉每次 `ComputeStateHash` 的临时 `List` 分配 + 排序。**产出的字节序列必须与现在完全一致**(都是 `Index` 升序遍历同一批 id)——这是零风险替换,但若实现时对 `_order` 与 `_states.Keys` 集合恒等有任何怀疑,保留原写法也可,`WriteState` 不是热路径。实现计划里做成一个独立小 step,单独被 golden 哈希验证。

## 6. 测试

### 6.1 新增容器单测(`ActiveIdListTests.cs`)

对 `Machines` / `MiningDrills` / `Inserters` / `ElectricGrid` 四个容器各一组(用各自的 `Register*` / `Unregister*` / `ActiveIds` 或 `GeneratorIds`):

- `Register` 三个乱序 `Index` 的 id(如 `EntityId(5,1)`、`EntityId(2,1)`、`EntityId(8,1)`)→ 断言 `ActiveIds` 序列 == `[Index 2, Index 5, Index 8]`。
- 再 `Register` 一个中间 `Index`(`EntityId(4,1)`)→ `[2, 4, 5, 8]`。
- `Unregister(EntityId(5,1))` → `[2, 4, 8]`。
- `Unregister` 一个从未注册的 id → 不抛异常,`ActiveIds` 不变。
- 复用槽位:`Unregister(EntityId(4,1))` 后 `Register(EntityId(4,2))`(同 `Index` 新 `Generation`)→ `ActiveIds` 含 `EntityId(4,2)`、不含 `EntityId(4,1)`,序列仍 `[2, 4, 8]`。
- 不变式:任意操作序列后 `new HashSet<EntityId>(ActiveIds)` == `_states.Keys`(或 `_fuelBufferJ.Keys`)对应的集合 —— 若容器不暴露 keys,用"注册过且未注销"的 id 集合对照。

### 6.2 回归防线(已有,必须继续全绿)

- `DeterminismTests` 的 `SameCommands_SameHashEveryTick` + 4 个 `Run*Scenario`(电力 / 机器 / 采矿机 / 机械臂各跑两遍逐 tick 哈希比对)。
- `SimulationTests` 里 P9/P10/P11 的进度推进、产出、缺料冻结、输出堵塞、role 1/2 搬运等断言。
- P12 的 `ScenarioBuilderTests` / `BenchScenarioTests`(含 `SmallScale_HashSequence_IsStableAcrossRuns` 钉死的 tick-800 哈希)。
- 全套 ~423 + 新增约 20 个容器单测。

### 6.3 golden 硬证明(实现计划的验收步骤)

实现完成后:
```
dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json --report /tmp/r.json
```
必须 **exit 0**,`gate` = `PASS`(`baselineNsPerTick` 仍是 0,性能门禁 SKIP;哈希门禁比对 `finalHash` + 4 个 `sampleHashes` 必须全等)。若哈希不符 → 本子项引入了行为变化 → 是 bug,不是"可接受的 golden 更新",禁止 `--update-golden` 掩盖。

## 7. 收益测量(实现计划的验收步骤,非断言)

实现前(在 `main` `e1a4655`)和实现后各跑:
```
dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --json --report /tmp/before.json   # 前
dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --json --report /tmp/after.json    # 后
```
对比 `minNsPerTick` 和 `phases[]`。预期:

- `Machines` / `MiningDrills` / `Inserters` / `Electric` 四项的**绝对 ns 明显下降**(scale 50 下每趟遍历从 ~3050 降到:机器 100、采矿机 200、机械臂 300、发电机 319)。
- `BeltAdvance` / `BeltHandoff` 的**绝对 ns 基本不变**,占比相对上升(它们没被改,只是别人变快了)。
- `minNsPerTick` 整体下降。

把 before / after 的 `minNsPerTick` 和四项 ns 写进实现计划的完成说明 + roadmap 执行期确认段。**不据此设任何测试断言**(壁钟数字有抖动);唯一的硬门禁是 golden 哈希不变。

## 8. 全局约束(实现计划照抄进 Global Constraints)

- **确定性铁律**:`ActiveIds` / `GeneratorIds` 的遍历序必须是 `EntityId.Index` 升序,和被替换的 `for i in 0..Entities.Capacity` 逐字一致。遍历的实体集合、顺序、每个实体的处理逻辑零变化。`bench --golden` 的 `finalHash` + 4 个 `sampleHashes` 实现前后必须全等,禁止 `--update-golden`。
- sim 层不引入 `float`/`double`。
- 8 个 tick 方法的内层单实体方法(`MachineTickPreSettle` 等)签名和函数体不动;只改外层遍历。
- `_order` 在子系统 tick 期间不增删(`Register*` / `Unregister*` 只在 Commands 阶段)—— 遍历稳定。
- 不新增 `Entities` / `EntityPool` 公开 API;`Entities.Get(EntityId)` 若不存在则用现有等价手段。
- 不碰 prototype / `data/base` / `bench/golden.json` / belt 循环 / `ElectricGrid.Settle` / `WriteState` 的 `Entities.Capacity` 扫描(§5 的 `WriteState` 简化除外,且那也必须产出字节一致的序列)。
- 提交信息结尾带 `Co-Authored-By:` / `Claude-Session:` trailer。

## 9. 开放项(留给实现计划)

- `Entities` 取只读 `EntityData` 的确切方法名(P11 Inserter 代码里已用 `Entities.Get(...)`,确认签名与返回类型;是 `ref`/`in`/按值)。
- `_order` 维护逻辑要不要抽成一个小 helper(4 个容器共用,如一个 `OrderedEntityIdList` 内部类型)还是各容器内联二分。倾向:抽一个 `internal sealed class OrderedEntityIdList { void Add(EntityId); void Remove(EntityId); IReadOnlyList<EntityId> Ids; }` 放 `sim/Faketorio.Sim/`,4 个容器各持有一个 —— DRY,单测只测这一个类 + 各容器一个 smoke。实现计划定。
- §5 的 `WriteState` 简化是并入本次还是完全跳过(倾向并入,做成独立 step + 独立 golden 验证)。
