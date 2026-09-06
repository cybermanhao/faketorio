# M1 Plan 13 — 有序活跃 id 列表(消除每 tick 全实体扫描)Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `Machines` / `MiningDrills` / `Inserters` / `ElectricGrid`(发电机)四个状态容器各维护一个按 `EntityId.Index` 升序的活跃 id 列表,把 `Simulation.Step()` 里 8 个 `for (int i = 0; i < Entities.Capacity; i++)` tick 循环改成 `foreach (var id in <容器>.ActiveIds)`,消除每 tick 对全部实体槽位的类型过滤扫描。

**Architecture:** 新增一个内部数据结构 `OrderedEntityIdList`(二分有序插入 + 去重 + 线性删除),四个容器各持有一个,在各自的 `Register*` / `Unregister*` 里同步维护,暴露只读 `ActiveIds` / `GeneratorIds`。`Simulation` 的 8 个 tick 方法改遍历源、删掉外层三重过滤(`IsAliveAtIndex` + `TryGetById` + `is not XPrototype`),`data` 从 `Entities.GetAtIndex(i)`(ref)改成 `Entities.Get(id)`(ref,只读)。纯性能改造,遍历集合 / 顺序 / 每实体逻辑逐字不变。

**Tech Stack:** C# / .NET 8(`net8.0`),xUnit 2.9。无新依赖、无新项目、无 API 破坏。

**Spec:** [`docs/superpowers/specs/2026-09-06-m1-plan13-active-id-lists-design.md`](../specs/2026-09-06-m1-plan13-active-id-lists-design.md)

## Global Constraints

- **确定性铁律**:`ActiveIds` / `GeneratorIds` 的遍历序必须是 `EntityId.Index` 升序,和被替换的 `for i in 0..Entities.Capacity` 逐字一致。遍历的实体集合、顺序、每个实体的处理逻辑零变化。
- **golden 硬门禁**:每个改动容器的 task 完成后,`dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json --report /tmp/r.json` 必须 **exit 0**、`gate` = `PASS`(`finalHash` + 4 个 `sampleHashes` 与 `bench/golden.json` 全等)。哈希若变 = 引入了行为变化 = bug。**禁止 `--update-golden` 掩盖。**
- sim 层不引入 `float` / `double`。
- 8 个 tick 方法的**内层单实体方法**(`MachineTickPreSettle` / `MachineTickPostSettle` / `MiningDrillTickPreSettle` / `MiningDrillTickPostSettle` / `InserterTickPostSettle`)的签名和函数体**不动**;只改外层遍历循环(以及 §Task 4 里 `InserterTickPostSettle` 的一处 `delta` 挪位)。
- `_order` 在子系统 tick 期间不增删(`Register*` / `Unregister*` 只在 `Simulation.PlaceEntity` / `DestroyEntityAt`,即 `Step()` 的 Commands 阶段)——遍历稳定,无迭代中修改。
- 不新增 `Entities` / `EntityPool` 公开 API。用现成的 `public ref EntityData Get(EntityId id)`(`EntityPool.cs:54`,dead id 抛 `InvalidOperationException`;`ActiveIds` 里的 id 必然存活)。
- 不碰 prototype / `data/base` / `bench/golden.json` / belt 推进+交接循环 / `ElectricGrid.Settle()` / `Simulation.WriteState` 里的 `Entities.Capacity` 扫描(`Simulation.cs:143`,那是哈希序列化)。
- 提交信息结尾带:
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01KwHR5m3PgiyWtkMRcC21Jb
  ```

## 参考:现有代码事实(照此实现,勿猜)

- `EntityId` = `public readonly record struct EntityId(int Index, int Generation)`,`EntityId.Invalid = new(-1, 0)`,`bool IsValid => Index >= 0`。值相等按 `Index` + `Generation`。
- `EntityPool<T>`(`sim/Faketorio.Sim/Entities/EntityPool.cs`):`public ref T Get(EntityId id)`(dead → throw),`int Capacity => _count`,`bool IsAliveAtIndex(int)`,`ref T GetAtIndex(int)`,`int GenerationAtIndex(int)`。`Simulation.Entities` 是 `EntityPool<EntityData>`。
- `EntityData` 有 `int ProtoId; int X; int Y; byte Rotation;`(见 `Simulation.WriteState`)。
- 四个容器(全部在**扁平** `namespace Faketorio.Sim;`,`Inserters` / `MiningDrills` 在 `sim/Faketorio.Sim/` 下,`Machines` 在 `sim/Faketorio.Sim/Machines/`):
  - `Machines`:`private readonly Dictionary<EntityId, MachineRuntimeState> _states`。`RegisterMachine(EntityId id) => _states[id] = new MachineRuntimeState(-1, 0, false);` / `UnregisterMachine(EntityId id) => _states.Remove(id);`。`WriteState` 现在:`var ids = new List<EntityId>(_states.Keys); ids.Sort((a,b) => a.Index.CompareTo(b.Index)); writer.Write(ids.Count); foreach ...`。
  - `MiningDrills`:`private readonly Dictionary<EntityId, DrillRuntimeState> _states`。`RegisterDrill` / `UnregisterDrill`。`WriteState` 同款 `_states.Keys` + `Sort` 模式。
  - `Inserters`:`private readonly Dictionary<EntityId, InserterState> _states`。`RegisterInserter` / `UnregisterInserter`。`WriteState` 同款。
  - `ElectricGrid`(`sim/Faketorio.Sim/Electric/ElectricGrid.cs`,`namespace Faketorio.Sim.Electric`):`private readonly Dictionary<EntityId, long> _fuelBufferJ`。`RegisterGenerator(EntityId id) => _fuelBufferJ[id] = 0;` / `UnregisterGenerator(EntityId id) => _fuelBufferJ.Remove(id);`。`WriteState` 现在:`var ids = new List<EntityId>(_fuelBufferJ.Keys); ...`（同款 `Sort` 模式,只写燃料缓冲）。
- `Simulation.PlaceEntity` 注册点(`Simulation.cs` ~318–330):`if (proto is FuelGeneratorPrototype) { ...; ElectricGrid.RegisterGenerator(id); }`、`if (proto is CraftingMachinePrototype) { ...; Machines.RegisterMachine(id); }`、`if (proto is MiningDrillPrototype) MiningDrills.RegisterDrill(id);`、`if (proto is InserterPrototype) Inserters.RegisterInserter(id);`。**这些是唯一的 `Register*` 调用点**,每个新实体一次。
- `Simulation.DestroyEntityAt` 清理点(`Simulation.cs` ~485–506):捕获 `isGenerator` / `isMachine` / `isDrill` / `isInserter` **在 `Entities.Destroy(id)` 之前**,`Destroy` 之后 `if (isGenerator) { ...; ElectricGrid.UnregisterGenerator(id); }` 等。**这些是唯一的 `Unregister*` 调用点**。
- 8 个待改 tick 方法(`Simulation.cs`,行号近似):`ElectricGeneratorsRegisterSupply`(~511)、`ElectricGeneratorsBurnFuel`(~526)、`MachinesTickPreSettle`(~550)、`MachinesTickPostSettle`(~613)、`MiningDrillsTickPreSettle`(~662)、`MiningDrillsTickPostSettle`(~738)、`InsertersTickPreSettle`(~772)、`InsertersTickPostSettle`(~786)。都是同一个 `for (int i = 0; i < Entities.Capacity; i++) { if (!Entities.IsAliveAtIndex(i)) continue; ref var data = ref Entities.GetAtIndex(i); if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not XPrototype proto) continue; var id = new EntityId(i, Entities.GenerationAtIndex(i)); <调用内层方法或内联逻辑> }` 结构。
- **性能 before-baseline**(在 `main` `12e7480` 实测,scale 50 / 5000 tick):`minNsPerTick = 5_031_616`;分阶段 ns:`Inserters 2_195_976` / `MiningDrills 1_477_179` / `Machines 763_093` / `Electric 592_909` / `BeltAdvance 9_738` / `BeltHandoff 8_558` / `Commands 214` / `Player 93`。四个目标阶段 ≈ 整个 tick。

---

### Task 1: `OrderedEntityIdList`

**Files:**
- Create: `sim/Faketorio.Sim/OrderedEntityIdList.cs`
- Test: `sim/Faketorio.Sim.Tests/OrderedEntityIdListTests.cs`

**Interfaces:**
- Consumes: `Faketorio.Sim.Entities.EntityId`。
- Produces:
  ```csharp
  namespace Faketorio.Sim;

  // 按 EntityId.Index 升序维护的 id 列表。四个状态容器各持一个,在 Register*/Unregister*
  // 里同步。EntityPool 会复用被释放的槽位索引,所以插入必须二分定位,不能 append。
  internal sealed class OrderedEntityIdList
  {
      // 已存在(按 Index 命中)则忽略——防御 Register* 的幂等 upsert 语义被重复调。
      public void Add(EntityId id);
      // 值相等(Index + Generation)删除;不存在则无操作,不抛。
      public void Remove(EntityId id);
      public IReadOnlyList<EntityId> Ids { get; }   // 升序快照视图(直接返回内部 List)
      public int Count { get; }
  }
  ```

- [ ] **Step 1: 写失败测试**

`sim/Faketorio.Sim.Tests/OrderedEntityIdListTests.cs`:
```csharp
using Faketorio.Sim;
using Faketorio.Sim.Entities;

namespace Faketorio.Sim.Tests;

public class OrderedEntityIdListTests
{
    private static int[] Indices(OrderedEntityIdList l)
        => l.Ids.Select(x => x.Index).ToArray();

    [Fact]
    public void Add_KeepsIndexAscending_RegardlessOfInsertOrder()
    {
        var l = new OrderedEntityIdList();
        l.Add(new EntityId(5, 1));
        l.Add(new EntityId(2, 1));
        l.Add(new EntityId(8, 1));
        Assert.Equal(new[] { 2, 5, 8 }, Indices(l));
        Assert.Equal(3, l.Count);

        l.Add(new EntityId(4, 1));   // 落在中间
        Assert.Equal(new[] { 2, 4, 5, 8 }, Indices(l));

        l.Add(new EntityId(1, 1));   // 落在最前
        Assert.Equal(new[] { 1, 2, 4, 5, 8 }, Indices(l));

        l.Add(new EntityId(9, 1));   // 落在最后
        Assert.Equal(new[] { 1, 2, 4, 5, 8, 9 }, Indices(l));
    }

    [Fact]
    public void Add_DuplicateIndex_IsIgnored()
    {
        var l = new OrderedEntityIdList();
        l.Add(new EntityId(3, 1));
        l.Add(new EntityId(3, 1));       // 完全相同
        l.Add(new EntityId(3, 7));       // 同 Index 不同 Generation —— 也当作已存在,忽略
        Assert.Equal(new[] { 3 }, Indices(l));
        Assert.Equal(1, l.Count);
        Assert.Equal(1, l.Ids[0].Generation);   // 保留最先加入的
    }

    [Fact]
    public void Remove_ByValue_LeavesOrderIntact()
    {
        var l = new OrderedEntityIdList();
        foreach (var i in new[] { 2, 5, 8 }) l.Add(new EntityId(i, 1));
        l.Remove(new EntityId(5, 1));
        Assert.Equal(new[] { 2, 8 }, Indices(l));
    }

    [Fact]
    public void Remove_Missing_IsNoOp()
    {
        var l = new OrderedEntityIdList();
        l.Add(new EntityId(2, 1));
        l.Remove(new EntityId(99, 1));           // 从没加过
        l.Remove(new EntityId(2, 4));            // Index 在,Generation 不符
        Assert.Equal(new[] { 2 }, Indices(l));
        Assert.Equal(1, l.Count);
    }

    [Fact]
    public void SlotReuse_NewGenerationReplacesOld()
    {
        var l = new OrderedEntityIdList();
        foreach (var i in new[] { 2, 4, 8 }) l.Add(new EntityId(i, 1));
        l.Remove(new EntityId(4, 1));            // 拆除
        l.Add(new EntityId(4, 3));              // 同槽位、新代数重建
        Assert.Equal(new[] { 2, 4, 8 }, Indices(l));
        Assert.Equal(3, l.Ids.Single(x => x.Index == 4).Generation);
    }
}
```

- [ ] **Step 2: 跑,确认失败**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~OrderedEntityIdListTests"`
Expected: 编译失败——`OrderedEntityIdList` 不存在。

- [ ] **Step 3: 实现**

`sim/Faketorio.Sim/OrderedEntityIdList.cs`:
```csharp
using Faketorio.Sim.Entities;

namespace Faketorio.Sim;

// 按 EntityId.Index 升序维护的 id 列表。四个状态容器各持一个,在 Register*/Unregister*
// 里同步。EntityPool 复用被释放的槽位索引,所以插入必须二分定位,不能 append。
internal sealed class OrderedEntityIdList
{
    private readonly List<EntityId> _ids = new();

    public IReadOnlyList<EntityId> Ids => _ids;
    public int Count => _ids.Count;

    public void Add(EntityId id)
    {
        int lo = 0, hi = _ids.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_ids[mid].Index < id.Index) lo = mid + 1;
            else hi = mid;
        }
        // lo 是第一个 Index >= id.Index 的位置。命中同 Index 视为已存在,忽略。
        if (lo < _ids.Count && _ids[lo].Index == id.Index) return;
        _ids.Insert(lo, id);
    }

    public void Remove(EntityId id)
    {
        int lo = 0, hi = _ids.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_ids[mid].Index < id.Index) lo = mid + 1;
            else hi = mid;
        }
        if (lo < _ids.Count && _ids[lo] == id) _ids.RemoveAt(lo);
    }
}
```

- [ ] **Step 4: 跑测试**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~OrderedEntityIdListTests"`
Expected: PASS(5)。

- [ ] **Step 5: 全套 + commit**

Run: `dotnet test sim/Faketorio.Sim.Tests -c Release` → 全绿(~423 + 5)。
```bash
git add sim/Faketorio.Sim/OrderedEntityIdList.cs sim/Faketorio.Sim.Tests/OrderedEntityIdListTests.cs
git commit -m "feat(perf): OrderedEntityIdList — 按 EntityId.Index 升序的活跃 id 列表

<trailer>"
```

---

### Task 2: `Machines` 接入活跃列表

**Files:**
- Modify: `sim/Faketorio.Sim/Machines/Machines.cs`
- Modify: `sim/Faketorio.Sim/Simulation.cs`(`MachinesTickPreSettle` ~550、`MachinesTickPostSettle` ~613)
- Test: `sim/Faketorio.Sim.Tests/ActiveIdListTests.cs`(新建,本 task 放 `Machines` 一组)

**Interfaces:**
- Consumes: `OrderedEntityIdList`(Task 1)。
- Produces: `Machines.ActiveIds` → `IReadOnlyList<EntityId>`(升序,== `_states.Keys` 集合)。

- [ ] **Step 1: 写失败测试**

`sim/Faketorio.Sim.Tests/ActiveIdListTests.cs`:
```csharp
using Faketorio.Sim;
using Faketorio.Sim.Entities;

namespace Faketorio.Sim.Tests;

public class ActiveIdListTests
{
    private static int[] Idx(IReadOnlyList<EntityId> ids) => ids.Select(x => x.Index).ToArray();

    [Fact]
    public void Machines_ActiveIds_TracksRegisterUnregister_InIndexOrder()
    {
        var m = new Machines();
        m.RegisterMachine(new EntityId(5, 1));
        m.RegisterMachine(new EntityId(2, 1));
        m.RegisterMachine(new EntityId(8, 1));
        Assert.Equal(new[] { 2, 5, 8 }, Idx(m.ActiveIds));

        m.RegisterMachine(new EntityId(4, 1));
        Assert.Equal(new[] { 2, 4, 5, 8 }, Idx(m.ActiveIds));

        m.UnregisterMachine(new EntityId(5, 1));
        Assert.Equal(new[] { 2, 4, 8 }, Idx(m.ActiveIds));

        m.UnregisterMachine(new EntityId(999, 1));   // 没注册过
        Assert.Equal(new[] { 2, 4, 8 }, Idx(m.ActiveIds));

        // 复用槽位
        m.UnregisterMachine(new EntityId(4, 1));
        m.RegisterMachine(new EntityId(4, 3));
        Assert.Equal(new[] { 2, 4, 8 }, Idx(m.ActiveIds));
        Assert.Equal(3, m.ActiveIds.Single(x => x.Index == 4).Generation);
    }
}
```

- [ ] **Step 2: 跑,确认失败**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ActiveIdListTests.Machines"`
Expected: 编译失败——`Machines.ActiveIds` 不存在。

- [ ] **Step 3: `Machines.cs` 加 `_order`**

字段区加:
```csharp
    private readonly OrderedEntityIdList _order = new();
    public IReadOnlyList<EntityId> ActiveIds => _order.Ids;
```
`RegisterMachine` / `UnregisterMachine` 改:
```csharp
    public void RegisterMachine(EntityId id)
    {
        _states[id] = new MachineRuntimeState(-1, 0, false);
        _order.Add(id);
    }
    public void UnregisterMachine(EntityId id)
    {
        _states.Remove(id);
        _order.Remove(id);
    }
```
`WriteState` 改用 `_order`(去掉临时 `List` + `Sort`;字节序列不变——都是 `Index` 升序遍历同一批 id):
```csharp
    public void WriteState(IStateWriter writer)
    {
        writer.Write(_order.Count);
        foreach (var id in _order.Ids)
        {
            var s = _states[id];
            writer.Write(id.Index);
            writer.Write(id.Generation);
            writer.Write(s.CurrentRecipeProtoId);
            writer.Write(s.Progress);
            writer.Write(s.Completed ? (byte)1 : (byte)0);
        }
    }
```

- [ ] **Step 4: `Simulation.cs` 改两个 tick 循环**

`MachinesTickPreSettle`:
```csharp
    private void MachinesTickPreSettle()
    {
        foreach (var id in Machines.ActiveIds)
        {
            ref var data = ref Entities.Get(id);
            var proto = (CraftingMachinePrototype)Prototypes.GetById(data.ProtoId);
            MachineTickPreSettle(id, proto, data.X, data.Y);
        }
    }
```
`MachinesTickPostSettle`:
```csharp
    private void MachinesTickPostSettle()
    {
        foreach (var id in Machines.ActiveIds)
        {
            ref var data = ref Entities.Get(id);
            var proto = (CraftingMachinePrototype)Prototypes.GetById(data.ProtoId);
            MachineTickPostSettle(id, proto);
        }
    }
```
`MachineTickPreSettle` / `MachineTickPostSettle` 的函数体**一字不改**。

- [ ] **Step 5: 跑 —— 单测 + 全套 + golden**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ActiveIdListTests.Machines"` → PASS。
Run: `dotnet test sim/Faketorio.Sim.Tests -c Release` → 全绿(尤其 `DeterminismTests.RunMachineScenario`、`SimulationTests` 的熔炉/装配机断言)。
Run: `dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json --report /tmp/r.json`
Expected: **exit 0,gate PASS**(哈希不变)。若 `HASH_FAIL` → 停,查 `_order` 的顺序或集合是否和旧扫描不一致;**不要** `--update-golden`。

- [ ] **Step 6: commit**

```bash
git add sim/Faketorio.Sim/Machines/Machines.cs sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/ActiveIdListTests.cs
git commit -m "perf(machines): tick 遍历 Machines.ActiveIds 而非全实体扫描

<trailer>"
```

---

### Task 3: `MiningDrills` 接入活跃列表

**Files:**
- Modify: `sim/Faketorio.Sim/MiningDrills.cs`
- Modify: `sim/Faketorio.Sim/Simulation.cs`(`MiningDrillsTickPreSettle` ~662、`MiningDrillsTickPostSettle` ~738)
- Test: `sim/Faketorio.Sim.Tests/ActiveIdListTests.cs`(加 `MiningDrills` 一组)

**Interfaces:**
- Consumes: `OrderedEntityIdList`(Task 1)。
- Produces: `MiningDrills.ActiveIds` → `IReadOnlyList<EntityId>`。

- [ ] **Step 1: 写失败测试**（加到 `ActiveIdListTests.cs`）
```csharp
    [Fact]
    public void MiningDrills_ActiveIds_TracksRegisterUnregister_InIndexOrder()
    {
        var d = new MiningDrills();
        d.RegisterDrill(new EntityId(7, 1));
        d.RegisterDrill(new EntityId(3, 1));
        d.RegisterDrill(new EntityId(7, 1));   // 重复,忽略
        Assert.Equal(new[] { 3, 7 }, Idx(d.ActiveIds));
        d.UnregisterDrill(new EntityId(3, 1));
        Assert.Equal(new[] { 7 }, Idx(d.ActiveIds));
        d.UnregisterDrill(new EntityId(42, 1));   // 没注册
        Assert.Equal(new[] { 7 }, Idx(d.ActiveIds));
    }
```

- [ ] **Step 2: 跑,确认失败** — `--filter "FullyQualifiedName~ActiveIdListTests.MiningDrills"`,编译失败。

- [ ] **Step 3: `MiningDrills.cs` 加 `_order`**
```csharp
    private readonly OrderedEntityIdList _order = new();
    public IReadOnlyList<EntityId> ActiveIds => _order.Ids;
```
```csharp
    public void RegisterDrill(EntityId id)
    {
        _states[id] = new DrillRuntimeState(-1, -1, 0, false, 0);
        _order.Add(id);
    }
    public void UnregisterDrill(EntityId id)
    {
        _states.Remove(id);
        _order.Remove(id);
    }
```
`WriteState` 改用 `_order.Ids`（同 Task 2 的模式,写的字段不变:index/代数/TargetX/TargetY/Progress/Completed/PendingItemProtoId —— **照文件里现有的 `writer.Write` 顺序逐条抄**,只把外层 `var ids = new List<...>(_states.Keys); ids.Sort(...)` 换成 `foreach (var id in _order.Ids)`,`writer.Write(ids.Count)` 换成 `writer.Write(_order.Count)`)。

- [ ] **Step 4: `Simulation.cs` 改两个 tick 循环**
```csharp
    private void MiningDrillsTickPreSettle()
    {
        foreach (var id in MiningDrills.ActiveIds)
        {
            ref var data = ref Entities.Get(id);
            var proto = (MiningDrillPrototype)Prototypes.GetById(data.ProtoId);
            MiningDrillTickPreSettle(id, proto, data.X, data.Y, data.Rotation);
        }
    }

    private void MiningDrillsTickPostSettle()
    {
        foreach (var id in MiningDrills.ActiveIds)
        {
            ref var data = ref Entities.Get(id);
            var proto = (MiningDrillPrototype)Prototypes.GetById(data.ProtoId);
            MiningDrillTickPostSettle(id, proto);
        }
    }
```
`MiningDrillTickPreSettle` / `MiningDrillTickPostSettle` 函数体一字不改。

- [ ] **Step 5: 跑 —— 单测 + 全套 + golden**

`--filter "FullyQualifiedName~ActiveIdListTests.MiningDrills"` → PASS;`dotnet test -c Release` 全绿(尤其 `DeterminismTests.RunMiningDrillScenario`、`SimulationTests` 的采矿机断言、P10 加的 `cell.IsEmpty` 崩溃回归);`bench --golden` → **exit 0 gate PASS**。

- [ ] **Step 6: commit**
```bash
git add sim/Faketorio.Sim/MiningDrills.cs sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/ActiveIdListTests.cs
git commit -m "perf(mining-drill): tick 遍历 MiningDrills.ActiveIds 而非全实体扫描

<trailer>"
```

---

### Task 4: `Inserters` 接入活跃列表 + `delta` 微优化

**Files:**
- Modify: `sim/Faketorio.Sim/Inserters.cs`
- Modify: `sim/Faketorio.Sim/Simulation.cs`(`InsertersTickPreSettle` ~772、`InsertersTickPostSettle` ~786、`InserterTickPostSettle` 的 `delta` 挪位)
- Test: `sim/Faketorio.Sim.Tests/ActiveIdListTests.cs`(加 `Inserters` 一组)

**Interfaces:**
- Consumes: `OrderedEntityIdList`(Task 1)。
- Produces: `Inserters.ActiveIds` → `IReadOnlyList<EntityId>`。

- [ ] **Step 1: 写失败测试**（加到 `ActiveIdListTests.cs`）
```csharp
    [Fact]
    public void Inserters_ActiveIds_TracksRegisterUnregister_InIndexOrder()
    {
        var ins = new Inserters();
        ins.RegisterInserter(new EntityId(6, 1));
        ins.RegisterInserter(new EntityId(1, 1));
        ins.RegisterInserter(new EntityId(4, 1));
        Assert.Equal(new[] { 1, 4, 6 }, Idx(ins.ActiveIds));
        ins.UnregisterInserter(new EntityId(4, 1));
        Assert.Equal(new[] { 1, 6 }, Idx(ins.ActiveIds));
        ins.UnregisterInserter(new EntityId(4, 1));   // 再删一次,no-op
        Assert.Equal(new[] { 1, 6 }, Idx(ins.ActiveIds));
    }
```

- [ ] **Step 2: 跑,确认失败** — `--filter "FullyQualifiedName~ActiveIdListTests.Inserters"`,编译失败。

- [ ] **Step 3: `Inserters.cs` 加 `_order`**
```csharp
    private readonly OrderedEntityIdList _order = new();
    public IReadOnlyList<EntityId> ActiveIds => _order.Ids;
```
```csharp
    public void RegisterInserter(EntityId id)
    {
        _states[id] = new InserterState(0, 0);
        _order.Add(id);
    }
    public void UnregisterInserter(EntityId id)
    {
        _states.Remove(id);
        _order.Remove(id);
    }
```
`WriteState` 改用 `_order.Ids`（写的字段不变:index/代数/HeldItemProtoId/SwingProgress —— 照现有 `writer.Write` 顺序抄,只换外层遍历和 `writer.Write(_order.Count)`)。

- [ ] **Step 4: `Simulation.cs` 改两个 tick 循环**

`InsertersTickPreSettle`(内联登记待机能耗,不调内层方法):
```csharp
    private void InsertersTickPreSettle()
    {
        foreach (var id in Inserters.ActiveIds)
        {
            ref var data = ref Entities.Get(id);
            var proto = (InserterPrototype)Prototypes.GetById(data.ProtoId);
            ElectricGrid.RegisterDemand(id, data.X, data.Y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
        }
    }
```
`InsertersTickPostSettle`:
```csharp
    private void InsertersTickPostSettle()
    {
        foreach (var id in Inserters.ActiveIds)
        {
            ref var data = ref Entities.Get(id);
            var proto = (InserterPrototype)Prototypes.GetById(data.ProtoId);
            InserterTickPostSettle(id, proto, data.X, data.Y, data.Rotation);
        }
    }
```

- [ ] **Step 5: `InserterTickPostSettle` 的 `delta` 挪位**

方法头现在是:
```csharp
    private void InserterTickPostSettle(EntityId id, InserterPrototype proto, int x, int y, byte rotation)
    {
        var (dx, dy) = BeltNetwork.Delta(rotation);
        int pickX = x - dx, pickY = y - dy;   // 身后
        int dropX = x + dx, dropY = y + dy;   // 身前
        int held = Inserters.GetHeldItemProtoId(id);
        long progress = Inserters.GetSwingProgress(id);
        long delta = proto.RotationSpeed.Mul(ElectricGrid.GetSatisfaction(id).Raw);   // ← 删这行

        // 阶段 A:空手停在抓取角 —— 尝试抓
        if (held == 0 && progress == 0)
        {
            ...
            return;
        }

        // 阶段 B:... 用到 delta
        // 阶段 C:... 用到 delta
```
把 `long delta = proto.RotationSpeed.Mul(ElectricGrid.GetSatisfaction(id).Raw);` 从方法头**删掉**,在**阶段 A 的 `return;` 之后、阶段 B 的 `if (held != 0)` 之前**新加一行:
```csharp
        long delta = proto.RotationSpeed.Mul(ElectricGrid.GetSatisfaction(id).Raw);
```
阶段 B、C 里对 `delta` 的用法一字不改。`delta` 的值不变(`GetSatisfaction(id)` 在 `Settle()` 之后,值已定,与计算位置无关)——golden 哈希不受影响。

- [ ] **Step 6: 跑 —— 单测 + 全套 + golden**

`--filter "FullyQualifiedName~ActiveIdListTests.Inserters"` → PASS;`dotnet test -c Release` 全绿(尤其 `DeterminismTests.RunInserterScenario`、`SimulationTests` 里 P11 的三相摆臂 / role 1/2 / 传送带子格抓插断言);`bench --golden` → **exit 0 gate PASS**。

- [ ] **Step 7: commit**
```bash
git add sim/Faketorio.Sim/Inserters.cs sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/ActiveIdListTests.cs
git commit -m "perf(inserter): tick 遍历 Inserters.ActiveIds;delta 计算挪出空闲阶段 A

<trailer>"
```

---

### Task 5: `ElectricGrid` 发电机接入活跃列表

**Files:**
- Modify: `sim/Faketorio.Sim/Electric/ElectricGrid.cs`
- Modify: `sim/Faketorio.Sim/Simulation.cs`(`ElectricGeneratorsRegisterSupply` ~511、`ElectricGeneratorsBurnFuel` ~526)
- Test: `sim/Faketorio.Sim.Tests/ActiveIdListTests.cs`(加 `ElectricGrid` 一组)

**Interfaces:**
- Consumes: `OrderedEntityIdList`(Task 1)。注意 `OrderedEntityIdList` 是 `internal`,`ElectricGrid` 在 `namespace Faketorio.Sim.Electric`——同程序集,`internal` 可见,直接用。
- Produces: `ElectricGrid.GeneratorIds` → `IReadOnlyList<EntityId>`(升序,== `_fuelBufferJ.Keys` 集合)。

- [ ] **Step 1: 写失败测试**（加到 `ActiveIdListTests.cs`;注意 `using Faketorio.Sim.Electric;`）
```csharp
    [Fact]
    public void ElectricGrid_GeneratorIds_TracksRegisterUnregister_InIndexOrder()
    {
        var g = new Faketorio.Sim.Electric.ElectricGrid();
        g.RegisterGenerator(new EntityId(9, 1));
        g.RegisterGenerator(new EntityId(3, 1));
        g.RegisterGenerator(new EntityId(6, 1));
        Assert.Equal(new[] { 3, 6, 9 }, Idx(g.GeneratorIds));
        g.UnregisterGenerator(new EntityId(6, 1));
        Assert.Equal(new[] { 3, 9 }, Idx(g.GeneratorIds));
        g.UnregisterGenerator(new EntityId(100, 1));   // 没注册
        Assert.Equal(new[] { 3, 9 }, Idx(g.GeneratorIds));
    }
```

- [ ] **Step 2: 跑,确认失败** — `--filter "FullyQualifiedName~ActiveIdListTests.ElectricGrid"`,编译失败。

- [ ] **Step 3: `ElectricGrid.cs` 加 `_generatorOrder`**

`_fuelBufferJ` 字段附近加:
```csharp
    private readonly Faketorio.Sim.OrderedEntityIdList _generatorOrder = new();
    public IReadOnlyList<EntityId> GeneratorIds => _generatorOrder.Ids;
```
（`ElectricGrid.cs` 顶部若没 `using Faketorio.Sim.Entities;` 则加;`OrderedEntityIdList` 用完全限定名 `Faketorio.Sim.OrderedEntityIdList` 或加 `using Faketorio.Sim;`——按文件现有 using 风格。）
```csharp
    public void RegisterGenerator(EntityId id)
    {
        _fuelBufferJ[id] = 0;
        _generatorOrder.Add(id);
    }
    public void UnregisterGenerator(EntityId id)
    {
        _fuelBufferJ.Remove(id);
        _generatorOrder.Remove(id);
    }
```
`ElectricGrid.WriteState`(只写燃料缓冲)改用 `_generatorOrder.Ids`:把 `var ids = new List<EntityId>(_fuelBufferJ.Keys); ids.Sort(...)` 换成 `foreach (var id in _generatorOrder.Ids)`,`writer.Write(ids.Count)` → `writer.Write(_generatorOrder.Count)`,循环体里 `_fuelBufferJ[id]` 的写法不变。

- [ ] **Step 4: `Simulation.cs` 改两个 tick 循环**

`ElectricGeneratorsRegisterSupply`:
```csharp
    private void ElectricGeneratorsRegisterSupply()
    {
        foreach (var id in ElectricGrid.GeneratorIds)
        {
            ref var data = ref Entities.Get(id);
            var gen = (FuelGeneratorPrototype)Prototypes.GetById(data.ProtoId);
            long buf = ElectricGrid.GetFuelBufferJ(id);
            var fuelInv = Inventories.Get(Inventories.GetInventoryId(id));
            bool hasFuel = buf > 0 || fuelInv.CountOf(gen.FuelItemProtoId) > 0;
            ElectricGrid.RegisterSupply(id, data.X, data.Y, UsagePriority.PrimaryOutput, hasFuel ? gen.PowerOutputJPerTick : 0);
        }
    }
```
`ElectricGeneratorsBurnFuel`:
```csharp
    private void ElectricGeneratorsBurnFuel()
    {
        foreach (var id in ElectricGrid.GeneratorIds)
        {
            ref var data = ref Entities.Get(id);
            var gen = (FuelGeneratorPrototype)Prototypes.GetById(data.ProtoId);
            long actual = ElectricGrid.GetAllocatedSupply(id);
            long buf = ElectricGrid.GetFuelBufferJ(id);
            var fuelInv = Inventories.Get(Inventories.GetInventoryId(id));
            var fuelItemProto = (ItemPrototype)Prototypes.GetById(gen.FuelItemProtoId);
            while (buf < actual && fuelInv.CountOf(gen.FuelItemProtoId) > 0)
            {
                fuelInv.Remove(gen.FuelItemProtoId, 1);
                buf += fuelItemProto.FuelValueJ;
            }
            long delivered = Math.Min(actual, buf);
            ElectricGrid.SetFuelBufferJ(id, buf - delivered);
        }
    }
```
（两处循环体逻辑和现在逐字一致,只是外层遍历源 + `data` 取法 + `gen` 从 `TryGetById`+检查改成直接 cast。）

- [ ] **Step 5: 跑 —— 单测 + 全套 + golden**

`--filter "FullyQualifiedName~ActiveIdListTests.ElectricGrid"` → PASS;`dotnet test -c Release` 全绿(尤其 `DeterminismTests.RunElectricScenario`、`SimulationTests` 里 P7 的发电机充能 / 烧油 / satisfaction 断言);`bench --golden` → **exit 0 gate PASS**。

- [ ] **Step 6: commit**
```bash
git add sim/Faketorio.Sim/Electric/ElectricGrid.cs sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/ActiveIdListTests.cs
git commit -m "perf(electric): 发电机两趟遍历 ElectricGrid.GeneratorIds 而非全实体扫描

<trailer>"
```

---

### Task 6: 收益测量 + 文档

**Files:**
- Modify: `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`(横切"实体休眠 / 活跃列表"条目 + 执行期确认段)

**Interfaces:** 无代码。产出:before/after 对比数字写进 roadmap。

- [ ] **Step 1: 跑 after 基准**

Run(repo 根,Release):
```bash
dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --json --golden bench/golden.json --report /tmp/p13-after.json
```
Expected: **exit 0,gate PASS**(哈希不变——这是本 plan 全程的硬门禁,再确认一次)。记下 stdout 的 `minNsPerTick` 和 markdown 表里 `Inserters` / `MiningDrills` / `Machines` / `Electric` 四行的 `ns`。

- [ ] **Step 2: 对比 before-baseline**

before(在 `main` `12e7480` 实测,scale 50 / 5000 tick):
| 指标 | before ns |
|---|---|
| `minNsPerTick` | 5_031_616 |
| `Inserters` | 2_195_976 |
| `MiningDrills` | 1_477_179 |
| `Machines` | 763_093 |
| `Electric` | 592_909 |
| `BeltAdvance` | 9_738 |
| `BeltHandoff` | 8_558 |

预期 after:四个目标阶段的 ns 明显下降(scale 50 下每趟遍历从 ~3050 降到 100 / 200 / 300 / 319);`BeltAdvance` / `BeltHandoff` 基本不变;`minNsPerTick` 整体下降。**若某个目标阶段没降或反升,是信号 —— 记进报告,别当没看见**(可能 `Entities.Get(id)` 的 `IsAlive` 检查、或每实体 `Prototypes.GetById` 的开销没被扫描节省抵消;仍应净下降)。

- [ ] **Step 3: 写进 roadmap**

`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`:
- 把"横切 · 实体休眠 / 活跃列表(§5.3 性能地基)"那行的状态从 `⬜ 未立项` 改成 `🟡 部分完成(P13:换迭代源)· 真休眠待后续`,备注列写"P13 换了迭代源(4 容器 `ActiveIds`);真休眠 / 唤醒是独立子项"。
- 在末尾的执行期确认段加一句 P13:before/after 的 `minNsPerTick`(用 Step 1/2 的实测值,格式 `X ms → Y ms(-Z%)`)+ 四个目标阶段 ns 的降幅 + "零 golden 哈希变化"。

- [ ] **Step 4: commit**
```bash
git add docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md
git commit -m "docs(p13): roadmap 进度 + 换迭代源前后 UPS 对比

<trailer>"
```

---

## Self-Review

**1. Spec coverage:**

| spec 章节 | 对应 task |
|---|---|
| §2 `OrderedEntityIdList` 抽成共享内部类(§9 开放项,倾向抽) | Task 1 |
| §2 `Machines` / `MiningDrills` / `Inserters` 加 `_order` + `ActiveIds` | Task 2 / 3 / 4 |
| §2 `ElectricGrid` 加 `_generatorOrder` + `GeneratorIds` | Task 5 |
| §2 + §4 `Simulation` 8 个 tick 循环改 `foreach` | Task 2(2)、Task 3(2)、Task 4(2)、Task 5(2) |
| §4.3 `InserterTickPostSettle` 的 `delta` 挪位 | Task 4 Step 5 |
| §5 `WriteState` 顺带简化(并入,独立 golden 验证) | Task 2/3/4/5 各自的 Step 3 + Step 5 的 golden 检查 |
| §6.1 4 个容器有序插入/删除/集合一致性单测 | Task 1(`OrderedEntityIdList` 直测)+ Task 2/3/4/5 各一个容器 smoke(`ActiveIdListTests`) |
| §6.2 已有回归防线全绿 | Task 2/3/4/5 各 Step 5 的 `dotnet test -c Release` |
| §6.3 golden 硬证明 | Task 2/3/4/5 各 Step 5 + Task 6 Step 1 |
| §7 收益测量(before/after,非断言) | Task 6 |
| §8 全局约束 | 复制进本 plan Global Constraints |
| §9 开放项①(`Entities.Get` 签名) | 已解决:`ref EntityData Get(EntityId)`,写进 Global Constraints + 参考节 |
| §9 开放项②(抽 helper) | 已决定:抽 `OrderedEntityIdList`(Task 1) |
| §9 开放项③(`WriteState` 简化并不并入) | 已决定:并入,每容器 task 内做 |

覆盖完整,§9 三个开放项全部在本 plan 里定死。

**2. Placeholder scan:** 无 "TBD / TODO / 类似 Task N / 处理边界情况" 类占位。`MiningDrills` / `Inserters` / `ElectricGrid` 的 `WriteState` 改写没贴完整代码,而是"照现有 `writer.Write` 顺序逐条抄,只换外层遍历"——因为这三个文件的 `WriteState` 字段各不相同且实现者手里就有文件,贴全反而容易抄错版本;Task 2 贴了 `Machines` 的完整改写作范例,三处同构。这是刻意的,不是偷懒。

**3. Type consistency:**
- `OrderedEntityIdList`:`Add` / `Remove` / `Ids`(`IReadOnlyList<EntityId>`)/ `Count` —— Task 1 定义,Task 2–5 一致消费。`internal` —— 四个容器全在同程序集 `Faketorio.Sim`,可见性 OK;`ElectricGrid` 在子命名空间 `Faketorio.Sim.Electric` 但同程序集,`internal` 仍可见(Task 5 Step 3 已注明用限定名或加 using)。
- `ActiveIds` / `GeneratorIds`:全是 `IReadOnlyList<EntityId>`,只读属性,Task 2–5 一致;`Simulation` 的 `foreach` 一致。
- `Entities.Get(id)` → `ref EntityData`,8 处 tick 循环一致用 `ref var data = ref Entities.Get(id);`(只读)。
- 内层单实体方法签名全程不动(`MachineTickPreSettle(EntityId, CraftingMachinePrototype, int, int)` 等)——Task 2–5 只改外层,不碰内层。
- 提交 message 前缀:Task 1 `feat(perf)`,Task 2–5 `perf(<域>)`,Task 6 `docs(p13)` —— 一致。

一致性 OK。
