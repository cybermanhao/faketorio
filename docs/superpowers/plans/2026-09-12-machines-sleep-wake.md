# Machines 休眠/唤醒 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `Machines`(熔炉+装配机)在配方缺失/原料不足/输出堵塞时不再每 tick
跑完整的 `MachineTickPreSettle`/`PostSettle` 逻辑,只做一次轻量待机电力登记,
直到一个明确的外部事件(玩家命令或机械臂动作)把它唤醒;唤醒调用点之外再加一个
60 tick 周期的兜底安全网,防止漏写唤醒调用导致机器永久卡死。

**Architecture:** `Machines` 内部把"全部已注册机器"拆成 `_awake`/`_asleep`
两个 `OrderedEntityIdList`。`Simulation` 的两个 tick 方法改成:睡着的机器走一条
只做 `RegisterDemand` + 安全网取模判断的轻量路径;醒着的机器走原有完整逻辑,
在四个既有的"闲置"早退分支里各自调用 `Machines.MarkAsleep`。五个外部代码点
(SetRecipe/TransferToEntity/TransferFromEntity 命令 + 机械臂放件/取件)在成功
改变机器输入/输出库存内容后调用 `Machines.MarkAwake`,把唤醒延迟从"最多 60
tick"降到"最多 1 tick"。

**Tech Stack:** C# / .NET 8,xUnit,现有的 `Faketorio.Sim`/`Faketorio.Sim.Tests`/
`Faketorio.Sim.Bench` 三个项目,不引入新依赖。

**Spec:** `docs/superpowers/specs/2026-09-12-machines-sleep-wake-design.md`

## Global Constraints

- 只改 `Machines` 系统;`MiningDrills`/`Inserters` 的休眠不在本计划范围内(spec §1、§9)。
- 待机能耗保持现状:睡着的机器仍每 tick 登记 `RegisterDemand`(spec §2,用户已决策选项 2)。
- `_awake`/`_asleep` 划分不序列化,`Machines.WriteState` 不变(spec §6)。反序列化/构建场景后所有已注册机器一律先进 `_awake`。
- 安全网周期写死常量 `SafetyNetIntervalTicks = 60`,不做运行时可配置(spec §4、§9)。
- `MachinesTickPreSettle`/`PostSettle` 循环体内会调用可能修改 `_awake`/`_asleep` 的方法,必须对快照数组 `foreach`,不能直接对活列表 `foreach`(spec §7)。
- 任何直接操纵机器输入/输出 `Inventory` 而不经过命令/机械臂的测试,如果断言在 60 tick 之内生效,必须显式调用 `sim.Machines.MarkAwake(machineId)`(spec §5 测试约定)。
- 本次改动预期让所有跑到几百/几千 tick 的确定性测试哈希发生变化(`GOLDEN_TICK_800`、`bench/golden.json`),这是预期结果,不是回归,必须重新生成并人工确认合理性(spec §6)。

---

## Task 1: `OrderedEntityIdList` 快照拷贝

**Files:**
- Modify: `sim/Faketorio.Sim/OrderedEntityIdList.cs`
- Test: `sim/Faketorio.Sim.Tests/OrderedEntityIdListTests.cs`(新建——目前没有专门测这个类的文件,现有覆盖都是间接通过 `Machines`/`Inserters` 等使用方测的)

**Interfaces:**
- Produces: `internal EntityId[] ToArray()` —— 返回 `_ids` 的一份独立拷贝,后续修改原列表不影响已返回的数组。

- [ ] **Step 1: 写失败测试**

```csharp
using Faketorio.Sim.Entities;

namespace Faketorio.Sim.Tests;

public class OrderedEntityIdListTests
{
    [Fact]
    public void ToArray_ReturnsIndependentCopy_MutatingOriginalDoesNotAffectSnapshot()
    {
        var list = new OrderedEntityIdList();
        var a = new EntityId(1, 1);
        var b = new EntityId(2, 1);
        list.Add(a);
        list.Add(b);

        var snapshot = list.ToArray();
        list.Remove(a);
        list.Add(new EntityId(3, 1));

        Assert.Equal(new[] { a, b }, snapshot);   // 快照不受后续修改影响
        Assert.Equal(new[] { b, new EntityId(3, 1) }, list.Ids);
    }

    [Fact]
    public void ToArray_EmptyList_ReturnsEmptyArray()
    {
        var list = new OrderedEntityIdList();
        Assert.Empty(list.ToArray());
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~OrderedEntityIdListTests"`
Expected: 编译失败,`ToArray` 不存在。

- [ ] **Step 3: 实现**

在 `sim/Faketorio.Sim/OrderedEntityIdList.cs` 的 `Remove` 方法之后加:

```csharp
    // 循环体内可能修改 _ids 时用这个拍快照,避免 foreach 活列表触发
    // InvalidOperationException(集合已修改)。
    public EntityId[] ToArray() => _ids.ToArray();
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~OrderedEntityIdListTests"`
Expected: 2 个测试通过。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/OrderedEntityIdList.cs sim/Faketorio.Sim.Tests/OrderedEntityIdListTests.cs
git commit -m "feat(sim): OrderedEntityIdList.ToArray() 快照拷贝,供休眠/唤醒的安全遍历用"
```

---

## Task 2: `Machines` 的 awake/asleep 状态模型

**Files:**
- Modify: `sim/Faketorio.Sim/Machines/Machines.cs`
- Test: `sim/Faketorio.Sim.Tests/MachinesTests.cs`

**Interfaces:**
- Consumes: `OrderedEntityIdList.ToArray()`(Task 1)。
- Produces:
  - `public void MarkAwake(EntityId id)` —— 从 `_asleep` 移到 `_awake`,已在 `_awake` 则空操作。未注册的 id 空操作(不抛异常——见 Step 3 的设计理由)。
  - `public void MarkAsleep(EntityId id)` —— 从 `_awake` 移到 `_asleep`,已在 `_asleep` 则空操作。
  - `internal EntityId[] AwakeSnapshot()` / `internal EntityId[] AsleepSnapshot()`。
  - `public bool IsAwake(EntityId id)` —— 测试和后续任务都需要断言"这台机器现在醒着还是睡着"。
  - `RegisterMachine` 改为把新机器放进 `_awake`(而不是只塞进 `_order`)。
  - `UnregisterMachine` 同时从 `_awake`/`_asleep` 里移除。

- [ ] **Step 1: 写失败测试**

追加到 `sim/Faketorio.Sim.Tests/MachinesTests.cs`:

```csharp
    [Fact]
    public void RegisterMachine_StartsAwake()
    {
        var m = new Machines();
        var id = new EntityId(20, 1);
        m.RegisterMachine(id);

        Assert.True(m.IsAwake(id));
        Assert.Contains(id, m.AwakeSnapshot());
        Assert.DoesNotContain(id, m.AsleepSnapshot());
    }

    [Fact]
    public void MarkAsleep_MovesFromAwakeToAsleep()
    {
        var m = new Machines();
        var id = new EntityId(21, 1);
        m.RegisterMachine(id);

        m.MarkAsleep(id);

        Assert.False(m.IsAwake(id));
        Assert.Contains(id, m.AsleepSnapshot());
        Assert.DoesNotContain(id, m.AwakeSnapshot());
    }

    [Fact]
    public void MarkAwake_MovesFromAsleepBackToAwake()
    {
        var m = new Machines();
        var id = new EntityId(22, 1);
        m.RegisterMachine(id);
        m.MarkAsleep(id);

        m.MarkAwake(id);

        Assert.True(m.IsAwake(id));
        Assert.Contains(id, m.AwakeSnapshot());
    }

    [Fact]
    public void MarkAsleep_AlreadyAsleep_IsNoOp()
    {
        var m = new Machines();
        var id = new EntityId(23, 1);
        m.RegisterMachine(id);
        m.MarkAsleep(id);

        m.MarkAsleep(id);   // 第二次不应该抛异常或重复插入

        Assert.Single(m.AsleepSnapshot());
    }

    [Fact]
    public void MarkAwake_AlreadyAwake_IsNoOp()
    {
        var m = new Machines();
        var id = new EntityId(24, 1);
        m.RegisterMachine(id);

        m.MarkAwake(id);   // 已经醒着,第二次调用不应该重复插入

        Assert.Single(m.AwakeSnapshot());
    }

    [Fact]
    public void MarkAwake_UnregisteredEntity_IsNoOp()
    {
        var m = new Machines();
        var id = new EntityId(25, 1);

        m.MarkAwake(id);   // 不应该抛异常——见 Simulation 里唤醒调用点可能对着
                            // 非机器实体调用的场景(比如 dropEntity 不是机器时)

        Assert.Empty(m.AwakeSnapshot());
        Assert.Empty(m.AsleepSnapshot());
    }

    [Fact]
    public void UnregisterMachine_RemovesFromAwakeOrAsleep()
    {
        var m = new Machines();
        var idAwake = new EntityId(26, 1);
        var idAsleep = new EntityId(27, 1);
        m.RegisterMachine(idAwake);
        m.RegisterMachine(idAsleep);
        m.MarkAsleep(idAsleep);

        m.UnregisterMachine(idAwake);
        m.UnregisterMachine(idAsleep);

        Assert.Empty(m.AwakeSnapshot());
        Assert.Empty(m.AsleepSnapshot());
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~MachinesTests"`
Expected: 编译失败,`MarkAwake`/`MarkAsleep`/`AwakeSnapshot`/`AsleepSnapshot`/`IsAwake` 不存在。

- [ ] **Step 3: 实现**

`MarkAwake`/`MarkAsleep` 对未注册 id 设计成空操作而不是抛异常,原因是 Simulation
侧的唤醒调用点(见 Task 4)有的挂在"目标实体不一定是机器"的分支上(比如机械臂
放件的目标可能是箱子而不是机器),调用方没有义务先判断"这是不是一台已注册的
机器"——`MarkAwake` 自己吸收这个判断,和现有 `GetCurrentRecipe`/`GetProgress`
对未注册实体返回默认值(而不是抛异常)的既有风格一致。

把 `sim/Faketorio.Sim/Machines/Machines.cs` 改成:

```csharp
public sealed class Machines
{
    private readonly Dictionary<EntityId, MachineRuntimeState> _states = new();
    private readonly OrderedEntityIdList _order = new();
    private readonly OrderedEntityIdList _awake = new();
    private readonly OrderedEntityIdList _asleep = new();

    public IReadOnlyList<EntityId> ActiveIds => _order.Ids;
    internal List<EntityId> ActiveIdsList => _order.IdsList;
    internal int StateCount => _states.Count;

    // 60 tick(1 秒游戏时间)的兜底安全网周期——见设计 spec §4/§9。
    public const int SafetyNetIntervalTicks = 60;

    public void RegisterMachine(EntityId id)
    {
        _states[id] = new MachineRuntimeState(-1, 0, false);
        _order.Add(id);
        _awake.Add(id);   // 新机器一律先醒着,走一次正常 tick 自己判断该不该睡。
    }

    public void UnregisterMachine(EntityId id)
    {
        _states.Remove(id);
        _order.Remove(id);
        _awake.Remove(id);
        _asleep.Remove(id);
    }

    public void MarkAwake(EntityId id)
    {
        if (!_states.ContainsKey(id)) return;   // 未注册(比如目标不是机器):空操作
        _asleep.Remove(id);
        _awake.Add(id);   // Add 本身对已存在的 id 是空操作(OrderedEntityIdList.Add)
    }

    public void MarkAsleep(EntityId id)
    {
        if (!_states.ContainsKey(id)) return;
        _awake.Remove(id);
        _asleep.Add(id);
    }

    internal EntityId[] AwakeSnapshot() => _awake.ToArray();
    internal EntityId[] AsleepSnapshot() => _asleep.ToArray();

    // ...GetCurrentRecipe/GetProgress/IsCompleted/SetRecipe/AddProgress/MarkCompleted/RestartCycle/WriteState 不变...
}
```

`IsAwake` 不要真去查 `_asleep` 内部私有的搜索方法(`OrderedEntityIdList` 没有
暴露 `Contains`)——直接改成:

```csharp
    public bool IsAwake(EntityId id) => _states.ContainsKey(id) && !AsleepContains(id);

    private bool AsleepContains(EntityId id)
    {
        foreach (var x in _asleep.Ids) if (x == id) return true;
        return false;
    }
```

(这个线性扫描只在测试和后续任务的断言里用,不在 per-tick 热路径上,不影响性能目标。)

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~MachinesTests"`
Expected: 全部通过,包括 Task 2 之前就有的既有测试(`RegisterMachine_StartsWithNoRecipeZeroProgressNotCompleted` 等)。

- [ ] **Step 5: 跑全量测试确认没有破坏别的东西**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj`
Expected: 462 个测试全部通过(Task 2 只加了新方法,没碰 `Simulation.cs` 的 tick 逻辑,`Machines.ActiveIdsList` 语义不变,现有测试不应该受影响)。

- [ ] **Step 6: 提交**

```bash
git add sim/Faketorio.Sim/Machines/Machines.cs sim/Faketorio.Sim.Tests/MachinesTests.cs
git commit -m "feat(machines): awake/asleep 状态模型 —— MarkAwake/MarkAsleep + 快照访问器"
```

---

## Task 3: Simulation 里接入睡眠——四个早退分支 + 双轨 tick 循环(慢速安全网,尚无外部唤醒)

**Files:**
- Modify: `sim/Faketorio.Sim/Simulation.cs:624-682`(`MachinesTickPreSettle`/`MachineTickPreSettle`)
- Modify: `sim/Faketorio.Sim/Simulation.cs:685-729`(`MachinesTickPostSettle`/`MachineTickPostSettle`)
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`

**Interfaces:**
- Consumes: `Machines.MarkAsleep(id)`、`Machines.AwakeSnapshot()`、`Machines.AsleepSnapshot()`、`Machines.SafetyNetIntervalTicks`(Task 2)。
- Produces: 本任务完成后,机器闲置时会真的停止跑重逻辑;唤醒只能通过 60 tick 安全网发生(Task 4 才加快速外部唤醒)——这个中间状态本身就是可独立测试、可独立评审的一步。

- [ ] **Step 1: 写失败测试**

追加到 `sim/Faketorio.Sim.Tests/SimulationTests.cs`(紧跟在
`AssemblingMachine_MissingIngredients_FreezesProgressWithoutResetting` 后面,
复用同一批 `PlacePoweredMachineInfra`/`PlaceAssembler`/`SetRecipe` helper):

```csharp
    [Fact]
    public void AssemblingMachine_NoRecipe_GoesAsleepWithinOneTick()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 0, 0));
        sim.Step();   // 放置

        var asmId = sim.World.GetEntityAt(0, 0);
        sim.Step();   // 一次 PreSettle 判断:没配方 -> 睡

        Assert.False(sim.Machines.IsAwake(asmId));
    }

    [Fact]
    public void AssemblingMachine_SafetyNet_SelfHealsWithoutExplicitWakeCall()
    {
        // 模拟"漏唤醒"场景:直接往输入库存塞原料(不经过任何命令/机械臂,
        // 也不手动调 MarkAwake),断言安全网在 SafetyNetIntervalTicks 个 tick
        // 之内自己把机器叫醒并推进——这是 spec §8 要求的关键回归测试,验证
        // "唤醒调用点漏写"不会导致永久卡死。
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceAssembler(sim, 0, 2));
        sim.Step();

        var asmId = sim.World.GetEntityAt(0, 2);
        int gearRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;
        sim.Submit(SetRecipe(0, 2, gearRecipeId));
        sim.Step();   // 有配方但没原料 -> PostSettle 判定 asleep

        Assert.False(sim.Machines.IsAwake(asmId));

        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 1));
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        inputInv.Insert(plateId, 2, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);
        // 故意不调 sim.Machines.MarkAwake —— 这就是"漏唤醒"的场景。

        for (int t = 0; t < Machines.SafetyNetIntervalTicks; t++) sim.Step();

        Assert.True(sim.Machines.GetProgress(asmId) > 0);   // 安全网救回来了
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~AssemblingMachine_NoRecipe_GoesAsleepWithinOneTick|FullyQualifiedName~AssemblingMachine_SafetyNet_SelfHealsWithoutExplicitWakeCall"`
Expected: `AssemblingMachine_NoRecipe_GoesAsleepWithinOneTick` 失败(现在没有机器会真的睡着,`IsAwake` 永远 true);`SafetyNet` 那条测试因为进度永远不会推进(机器还在跑,但没原料所以卡在 0)而失败——注意这条测试**在实现之前也会失败**,但失败原因是"从来没醒来处理过"而不是"安全网没生效",实现完成后才是验证安全网本身。

- [ ] **Step 3: 实现**

在 `sim/Faketorio.Sim/Simulation.cs` 改 `MachinesTickPreSettle`(原
`Simulation.cs:624-632`):

```csharp
    private void MachinesTickPreSettle()
    {
        // 睡着的机器:轻量待机登记 + 安全网。先拍快照再遍历——MarkAwake 可能
        // 在这个循环体内修改 _asleep/_awake,不能直接 foreach 活列表
        // (spec §7,C# 不允许边遍历边改同一个 List<T>)。
        foreach (var id in Machines.AsleepSnapshot())
        {
            ref var data = ref Entities.Get(id);
            var proto = (CraftingMachinePrototype)Prototypes.GetById(data.ProtoId);
            ElectricGrid.RegisterDemand(id, data.X, data.Y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
            if (id.Index % Machines.SafetyNetIntervalTicks == Tick % Machines.SafetyNetIntervalTicks)
                Machines.MarkAwake(id);
        }

        foreach (var id in Machines.AwakeSnapshot())
        {
            ref var data = ref Entities.Get(id);
            var proto = (CraftingMachinePrototype)Prototypes.GetById(data.ProtoId);
            MachineTickPreSettle(id, proto, data.X, data.Y);
        }
    }
```

`MachineTickPreSettle` 方法体(原 `Simulation.cs:634-682`)有两处要改:

1. `!fits`(输出堵塞)分支,在现有 `return;` 前加 `Machines.MarkAsleep(id);`:

```csharp
            else
            {
                // 输出堵塞:跳过第 2 步(不重新匹配/不能开始新一轮),但仍登记待机能耗。
                ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
                Machines.MarkAsleep(id);
                return;
            }
```

2. 第 2 步(配方选定)之后、第 3 步(登记需求)之前,新增一个早退分支——这是
   spec §1 表格里标注的"现状没有早退,这里新增"的那一支:

```csharp
        // 第 2 步:配方选定(仅当当前没有配方)
        if (Machines.GetCurrentRecipe(id) == -1 && proto is FurnacePrototype)
        {
            var inputInv = Inventories.Get(Inventories.GetInventoryId(id, 1));
            for (int rid = 0; rid < Prototypes.Count; rid++)
            {
                if (Prototypes.GetById(rid) is not RecipePrototype recipe || recipe.Category != proto.Category) continue;
                bool satisfied = true;
                foreach (var ing in recipe.ResolvedIngredients)
                    if (inputInv.CountOf(ing.ItemProtoId) < ing.Amount) { satisfied = false; break; }
                if (satisfied) { Machines.SetRecipe(id, recipe.Id); break; }
            }
        }
        // 装配机:不自动匹配,只用玩家此前 SetRecipe 设置的结果(可能仍是 -1)。

        // 仍然没有配方(装配机从没被 SetRecipe 过,或熔炉扫完一圈没匹配上):
        // 睡眠等外部事件——见 spec §1 情况 A/D。
        if (Machines.GetCurrentRecipe(id) == -1)
        {
            ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
            Machines.MarkAsleep(id);
            return;
        }

        // 第 3 步:电力需求登记(无条件——恒定待机能耗)
        ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
```

（这里在新增的早退分支里也调用了一次 `RegisterDemand`，是为了保留“待机能耗
不变”这个决策——即使这个 tick 就要睡着，这一 tick 该登记的需求还是要登记，
睡眠只影响“下一 tick 起”。）

同理改 `MachinesTickPostSettle`(原 `Simulation.cs:685-693`)只遍历
`AwakeSnapshot()`:

```csharp
    private void MachinesTickPostSettle()
    {
        foreach (var id in Machines.AwakeSnapshot())
        {
            ref var data = ref Entities.Get(id);
            var proto = (CraftingMachinePrototype)Prototypes.GetById(data.ProtoId);
            MachineTickPostSettle(id, proto);
        }
    }
```

`MachineTickPostSettle`(原 `Simulation.cs:695-729`)的 `!satisfied` 分支加
`Machines.MarkAsleep(id)`:

```csharp
        bool satisfied = true;
        foreach (var ing in recipe.ResolvedIngredients)
            if (inputInv.CountOf(ing.ItemProtoId) < ing.Amount) { satisfied = false; break; }
        if (!satisfied)
        {
            Machines.MarkAsleep(id);
            return;   // 缺料:本 tick 冻结进度,不清零、不重置配方,等原料备齐
        }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~AssemblingMachine_NoRecipe_GoesAsleepWithinOneTick|FullyQualifiedName~AssemblingMachine_SafetyNet_SelfHealsWithoutExplicitWakeCall"`
Expected: 两个都通过。

- [ ] **Step 5: 跑全量测试,记录哪些既有测试开始失败**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj`
Expected: **预期会有失败**——至少 `Machine_OutputBlocked_HoldsCompletedUntilSpaceFrees` 和 `AssemblingMachine_MissingIngredients_FreezesProgressWithoutResetting`(两者都在少于 60 tick 内断言"喂料/腾空间后立刻恢复",但本任务还没加外部唤醒调用点,只有 60 tick 安全网)。把完整失败列表记下来,交给 Task 5 处理——**这一步不要现在就去改测试**,先完成 Task 4(加外部唤醒)再回头看还有没有真正需要改测试断言的地方,很多失败会在 Task 4 之后自动消失。

- [ ] **Step 6: 提交(即使全量测试还有已知失败,也先提交这一步的产出,失败列表记在提交信息里)**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "$(cat <<'EOF'
feat(machines): 接入睡眠状态机 —— 四个闲置分支自动 MarkAsleep,双轨 tick 循环

MachinesTickPreSettle/PostSettle 改成拍快照遍历 AwakeSnapshot/AsleepSnapshot,
睡着的机器只做待机电力登记 + 60 tick 安全网取模判断。尚未接入外部唤醒调用点
(Task 4),此时机器只能靠安全网苏醒,预期部分既有测试(Machine_OutputBlocked_
HoldsCompletedUntilSpaceFrees 等)在少于 60 tick 断言处失败,留给 Task 4/5 处理。
EOF
)"
```

---

## Task 4: 五个外部唤醒调用点

**Files:**
- Modify: `sim/Faketorio.Sim/Simulation.cs:367-403`(`TransferToEntity`)
- Modify: `sim/Faketorio.Sim/Simulation.cs:404-439`(`TransferFromEntity`)
- Modify: `sim/Faketorio.Sim/Simulation.cs:440-453`(`SetRecipe`)
- Modify: `sim/Faketorio.Sim/Simulation.cs:886-980`(`InserterTickPostSettle`,放件 + 阶段 A 抓取两处)
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`

**Interfaces:**
- Consumes: `Machines.MarkAwake(EntityId)`(Task 2)。

- [ ] **Step 1: 写失败测试**

追加到 `sim/Faketorio.Sim.Tests/SimulationTests.cs`:

```csharp
    [Fact]
    public void TransferToEntity_WakesUpAsleepMachine_SameTick()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();   // 放置

        var furnaceId = sim.World.GetEntityAt(0, 2);
        sim.Step();   // 没原料 -> 熔炉 PreSettle 扫完配方仍是 -1 -> 睡
        Assert.False(sim.Machines.IsAwake(furnaceId));

        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        sim.Player.Inventory.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);
        sim.Submit(TransferTo(0, 2, oreId, 1));
        sim.Step();   // TransferToEntity 在 Commands 阶段(整个 tick 最早)执行,
                       // 早于本 tick 的 MachinesTickPreSettle,应该同 tick 就醒。

        Assert.True(sim.Machines.IsAwake(furnaceId));
    }

    [Fact]
    public void TransferFromEntity_WakesUpMachineBlockedOnFullOutput()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(0, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));
        var outputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 2));
        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;

        inputInv.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);
        sim.Machines.MarkAwake(furnaceId);   // 直接操纵库存,按测试约定显式唤醒(spec §5)
        outputInv.Insert(plateId, plateStack, plateStack);   // 预填满输出槽

        for (int t = 0; t < 200; t++) sim.Step();   // 完成一轮 -> 输出塞不下 -> 睡
        Assert.False(sim.Machines.IsAwake(furnaceId));
        Assert.Equal(0, inputInv.CountOf(oreId));            // 已经在完成时消耗

        sim.Submit(TransferFrom(0, 2, plateId, plateStack)); // 玩家手动搬走成品
        sim.Step();

        Assert.True(sim.Machines.IsAwake(furnaceId));
    }

    [Fact]
    public void SetRecipe_WakesUpSleepingAssembler_SameTick()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 0, 0));
        sim.Step();
        var asmId = sim.World.GetEntityAt(0, 0);
        sim.Step();   // 没配方 -> 睡
        Assert.False(sim.Machines.IsAwake(asmId));

        int gearRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;
        sim.Submit(SetRecipe(0, 0, gearRecipeId));
        sim.Step();

        Assert.True(sim.Machines.IsAwake(asmId));
    }

    [Fact]
    public void Inserter_DropIntoMachineInput_WakesUpSleepingFurnace()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 2, 2));
        // insA at (1,2) rot=East feeds a wooden-chest's contents into the furnace input —
        // 复用既有 Inserter_IntoFurnaceInput_UsesRole1 一带的布局风格:机械臂背后放一个
        // 木箱塞满铁矿,机械臂负责把矿搬进熔炉输入。
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));   // East:pickup(0,2) chest -> drop(2,2) furnace
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(2, 2);
        sim.Step();   // 没原料 -> 睡
        Assert.False(sim.Machines.IsAwake(furnaceId));

        var chestId = sim.World.GetEntityAt(0, 2);
        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId)).Insert(oreId, 5, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);

        // 机械臂抓取 + 摆动 + 放件需要若干 tick(同既有机械臂测试的量级),
        // 给够余量;一旦机械臂成功放件,MarkAwake 应该在同一 tick 内生效。
        bool wokeUp = false;
        for (int t = 0; t < 30 && !wokeUp; t++)
        {
            sim.Step();
            wokeUp = sim.Machines.IsAwake(furnaceId);
        }

        Assert.True(wokeUp);
    }
```

`PlaceInserter`/`PlaceChest`/`TransferFrom`/`TransferTo` 都已经是这个测试文件
里存在的 helper(`TransferFrom`/`TransferTo` 在 `TransferFromEntity`/
`TransferToEntity` 相关的既有测试群附近;`PlaceInserter`/`PlaceChest` 在机械臂
测试群附近)——写这几个新测试之前,先用 `grep -n "private static Command PlaceInserter\|private static Command PlaceChest\|private static Command TransferFrom\|private static Command TransferTo"  sim/Faketorio.Sim.Tests/SimulationTests.cs` 确认精确签名(参数顺序/是否需要
`sim` 参数),照抄现有调用风格,不要臆造参数。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~TransferToEntity_WakesUpAsleepMachine_SameTick|FullyQualifiedName~TransferFromEntity_WakesUpMachineBlockedOnFullOutput|FullyQualifiedName~SetRecipe_WakesUpSleepingAssembler_SameTick|FullyQualifiedName~Inserter_DropIntoMachineInput_WakesUpSleepingFurnace"`
Expected: 全部失败(`IsAwake` 断言为 true 时实际是 false——还没有任何外部唤醒调用点)。

- [ ] **Step 3: 实现**

`TransferToEntity`(`Simulation.cs:367-403`),在 `Player.Inventory.Remove(...)`
之后、`return;` 之前:

```csharp
                var targetInv = Inventories.Get(targetInvId);
                bool blockedByFilter = targetIsMachine
                    && !CanAcceptMachineInput(eid, (CraftingMachinePrototype)targetProto!, command.ProtoId);
                int inserted = blockedByFilter ? 0 : targetInv.Insert(command.ProtoId, amount, itemProto.StackSize);
                Player.Inventory.Remove(command.ProtoId, inserted);
                if (targetIsMachine && inserted > 0) Machines.MarkAwake(eid);
                return;
```

`TransferFromEntity`(`Simulation.cs:404-438`),需要先知道源是不是机器
(现有代码已经算出 `sourceRole`,`sourceRole == 2` 就是机器输出),在
`Player.Inventory.Insert(...)` 断言之后加:

```csharp
                int inserted2 = Player.Inventory.Insert(command.ProtoId, removed, itemProto2.StackSize);
                System.Diagnostics.Debug.Assert(inserted2 == removed, "...");
                if (sourceRole == 2 && removed > 0) Machines.MarkAwake(eid2);
                return;
```

`SetRecipe`(`Simulation.cs:440-453`):

```csharp
                Machines.SetRecipe(mid, command.ProtoId);
                Machines.MarkAwake(mid);
                return;
```

`InserterTickPostSettle` 放件分支(`Simulation.cs:954-970`附近,`dropIsMachine`
那一段):

```csharp
                if (invId.IsValid && (!dropIsMachine || CanAcceptMachineInput(dropEntity, (CraftingMachinePrototype)dp!, held)))
                {
                    int stack = ((ItemPrototype)Prototypes.GetById(held)).StackSize;
                    int insertedCount = Inventories.Get(invId).Insert(held, 1, stack);
                    released = insertedCount > 0;
                    if (dropIsMachine && released) Machines.MarkAwake(dropEntity);
                }
```

（原代码是 `released = Inventories.Get(invId).Insert(held, 1, stack) > 0;`
一行——拆成两行只是为了能在 `released` 判断之后再调用 `MarkAwake`，逻辑不变。）

`InserterTickPostSettle` 阶段 A 抓取分支(`Simulation.cs:917-928`附近):

```csharp
                if (invId.IsValid)
                {
                    var inv = Inventories.Get(invId);
                    for (int s = 0; s < inv.SlotCount; s++)
                    {
                        if (inv[s].IsEmpty) continue;
                        int itemId = inv[s].ItemProtoId;
                        inv.Remove(itemId, 1);
                        Inserters.Grab(id, itemId);
                        if (role == 2) Machines.MarkAwake(pickEntity);
                        break;
                    }
                }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~TransferToEntity_WakesUpAsleepMachine_SameTick|FullyQualifiedName~TransferFromEntity_WakesUpMachineBlockedOnFullOutput|FullyQualifiedName~SetRecipe_WakesUpSleepingAssembler_SameTick|FullyQualifiedName~Inserter_DropIntoMachineInput_WakesUpSleepingFurnace"`
Expected: 全部通过。

- [ ] **Step 5: 跑全量测试,重新记录失败列表**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj`
Expected: 失败数应该比 Task 3 结束时少很多(大部分外部喂料路径现在都有快速唤醒了)。把剩下的失败列表交给 Task 5。

- [ ] **Step 6: 提交**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "feat(machines): 五个外部唤醒调用点 —— SetRecipe/TransferTo/TransferFrom + 机械臂放件/取件"
```

---

## Task 5: 修复受影响的既有测试 + 全量回归

**Files:**
- Modify: `sim/Faketorio.Sim.Tests/SimulationTests.cs`(至少 `Machine_OutputBlocked_HoldsCompletedUntilSpaceFrees` 和 `AssemblingMachine_MissingIngredients_FreezesProgressWithoutResetting`,外加 Task 3/4 Step 5 记录下来的其它失败)

**Interfaces:**
- 无新接口——本任务只修测试断言/测试内部的库存操纵方式,不改产品代码。

- [ ] **Step 1: 确认当前失败列表**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj` 2>&1 | grep -E "^\s+Faketorio.Sim.Tests\.|Failed "`

把完整失败测试名列出来,一条条对照下面两条已知修法,或者判断是否需要新增 `sim.Machines.MarkAwake(...)`。

- [ ] **Step 2: 修 `Machine_OutputBlocked_HoldsCompletedUntilSpaceFrees`**

原测试用 `outputInv.Remove(plateId, plateStack)` 直接腾空间,绕开所有唤醒钩子。
改成走真实的 `TransferFromEntity` 命令(这本来就是 Task 4 新增的唤醒触发点,
用真实路径验证比继续用裸库存操纵更贴近生产行为):

```csharp
    [Fact]
    public void Machine_OutputBlocked_HoldsCompletedUntilSpaceFrees()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(0, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));
        var outputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 2));
        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;

        inputInv.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);
        sim.Machines.MarkAwake(furnaceId);   // 直接操纵输入库存,按测试约定显式唤醒
        outputInv.Insert(plateId, plateStack, plateStack);   // 预填满输出槽(不触发任何唤醒/睡眠判断,合法)

        for (int t = 0; t < 200; t++) sim.Step();

        Assert.Equal(0, inputInv.CountOf(oreId));
        Assert.Equal(plateStack, outputInv.CountOf(plateId));
        Assert.False(sim.Machines.IsAwake(furnaceId));   // 新增断言:确认它确实睡了,不是碰巧还醒着

        sim.Submit(TransferFrom(0, 2, plateId, plateStack));   // 真实路径腾空间,同时是 Task 4 的唤醒触发点
        sim.Step();

        Assert.Equal(1, outputInv.CountOf(plateId));
    }
```

- [ ] **Step 3: 修 `AssemblingMachine_MissingIngredients_FreezesProgressWithoutResetting`**

在直接 `inputInv.Insert(...)` 之后加一行 `sim.Machines.MarkAwake(asmId)`
(按测试约定,这是"直接操纵库存,断言在 60 tick 内生效"的场景):

```csharp
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 1));
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        inputInv.Insert(plateId, 2, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);
        sim.Machines.MarkAwake(asmId);

        sim.Step();
        Assert.True(sim.Machines.GetProgress(asmId) > 0);
```

- [ ] **Step 4: 处理 Step 1 列出的其它失败**

对每一条:判断它是不是"直接操纵机器输入/输出库存 + 少于 60 tick 内断言"的
模式——是,就照 Step 3 的方式加一行 `MarkAwake`;不是(比如断言本身就基于
旧的"无条件耗电"假设,或者依赖某个具体 tick 数的精确时序而休眠引入了 1 tick
延迟),就按 spec §6"允许改变时序"的既有共识调整断言(优先改成"最终会不会
发生"而不是"精确第几个 tick 发生",除非那个精确 tick 数就是这条测试要保护的
行为本身)。不要不看内容就大范围加 `MarkAwake`"让测试过"——每一条都要能讲清楚
"这是不是一个真实生产路径覆盖不到的场景"。

- [ ] **Step 5: 跑全量测试确认全绿**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj`
Expected: 全部通过(测试总数应该比 Task 2 之前多了 Task 1~4 新增的测试)。

- [ ] **Step 6: 提交**

```bash
git add sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "test(machines): 修复休眠机制引入后受影响的既有测试断言"
```

---

## Task 6: 分阶段 profiler 验证性能收益

**Files:**
- 不改代码,只跑 `Faketorio.Sim.Bench` 采集数据。

- [ ] **Step 1: 构造一个"大部分机器长期闲置"的验证场景**

当前 `ScenarioBuilder`(`sim/Faketorio.Sim.Bench/Scenario/ScenarioBuilder.cs`)
默认场景里的熔炉持续有料可烧(fed drill 供矿),机器大部分时间是醒着的——这个
场景测不出休眠的收益,只是回归验证"没有变慢"。跑:

```bash
dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --scale 100 --ticks 400 --warmup 1 --iterations 2 --json
```

记录 `Machines` 阶段的 `ns`/`pctOfTick`,和 commit `d1eb4f5`(`ElectricGrid`
优化后校准的基线)时的历史数据(scale 100 时 `Machines` 约 49337 ns,占 8.8%)
比较——预期这个默认场景下 `Machines` 阶段耗时基本不变或轻微下降(因为多数
机器本来就一直有事干,睡眠机制在这个场景里生效的比例不高),这是**符合预期的
阴性结果**,不是本任务要证明收益的地方,只是确认没有跑出回归。

- [ ] **Step 2: 记录结果到 roadmap,不需要额外新建场景**

如果 Step 1 显示 `Machines` 阶段耗时明显上升(说明双轨循环 + 快照拷贝的开销
盖过了收益,该场景大部分机器本来就没有机会睡着),需要停下来复查 Task 3/4
的实现,而不是继续往下走——这种情况下问题很可能出在快照拷贝本身成了新的热点
(`AwakeSnapshot()` 在"几乎全员醒着"的场景里,每 tick 都要复制几乎全部机器的
`EntityId[]`,这是一个已知的、本设计范围内接受的开销,如果验证发现它超出预期,
在 roadmap 里记一笔留给后续优化,不在本次计划里展开解决)。

---

## Task 7: Golden 重新生成 + roadmap 记录 + bench baseline 重新校准

**Files:**
- Modify: `sim/Faketorio.Sim.Tests/BenchScenarioTests.cs`(`GOLDEN_TICK_800` 常量)
- Modify: `bench/golden.json`
- Modify: `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`

- [ ] **Step 1: 重新生成 `GOLDEN_TICK_800`**

Run:
```bash
dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~DeterminismRoundTrip_800Ticks"
```
(先确认现在这条测试的确切失败输出——xUnit 的 `Assert.Equal` 失败信息里会打印
实际算出的哈希值,那就是新的 `GOLDEN_TICK_800`。)把
`sim/Faketorio.Sim.Tests/BenchScenarioTests.cs` 里的常量替换成这个新值,并把
注释更新成说明"因 Machines 休眠/唤醒机制引入,行为时序改变(spec §6 预期
内)"。

- [ ] **Step 2: 重新生成 `bench/golden.json`**

```bash
dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --update-golden
```

打开 `bench/golden.json` 的 diff:`finalHash`/`sampleHashes` 应该变了(预期
内),`baselineNsPerTick` 如果被重写成非 0,手动改回 `0`(标记"未校准",按
`bench/README.md` 的流程,校准要等推到 `main`、CI 跑出干净的 `bench-report`
之后才做——同本 session 之前 `ElectricGrid` 优化时走的流程一样)。

- [ ] **Step 3: 全量测试 + 提交**

```bash
dotnet test -c Release Faketorio.sln
git add sim/Faketorio.Sim.Tests/BenchScenarioTests.cs bench/golden.json
git commit -m "$(cat <<'EOF'
chore(bench): Machines 休眠/唤醒引入后重新生成 golden —— 行为时序改变,预期内

GOLDEN_TICK_800 和 bench/golden.json 的哈希都变了(机械臂投料唤醒机器有
1 tick 延迟,直接命令唤醒同 tick 生效,见 spec §6);baselineNsPerTick
重置为 0,等推到 main 后 CI 出干净基线再校准。
EOF
)"
```

- [ ] **Step 4: 推送 + 等 CI + 校准 baseline**

```bash
git push origin main
```
等 CI 绿,下载 `bench-report` artifact,读 `minNsPerTick`,回填
`bench/golden.json` 的 `baselineNsPerTick`,单独提交(同本 session 之前
`ElectricGrid` 优化后 commit `d1eb4f5` 的流程),推送。

- [ ] **Step 5: 更新 roadmap**

在 `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md` 里记录:
Machines 休眠/唤醒已完成(commit 引用),Task 6 的验证数据,以及"MiningDrills/
Inserters 的休眠留待后续"这个明确的范围边界。提交、推送。

---

## Self-Review Notes

- **Spec 覆盖**:§1(四种闲置)→ Task 3;§2(待机能耗决策)→ Task 3 的
  `RegisterDemand` 保留;§3(数据结构)→ Task 2;§4(每 tick 算法/安全网)
  → Task 3;§5(五个唤醒触发点,含 spec 修订后新增的 `TransferFromEntity`)
  → Task 4;§6(确定性/WriteState)→ Task 7;§7(快照遍历)→ Task 1+3;
  §8(测试计划,含安全网自愈测试)→ Task 3/6;§9(范围外)→ Global Constraints
  明确写了不碰 `MiningDrills`/`Inserters`。
- **占位符扫描**:无 "TBD"/"后续实现"/"类似 Task N 不贴代码" 这类模式——
  每个 Step 3 都是可以直接抄的完整代码块。
- **类型一致性**:`Machines.MarkAwake(EntityId)`/`MarkAsleep(EntityId)`/
  `AwakeSnapshot(): EntityId[]`/`AsleepSnapshot(): EntityId[]`/
  `IsAwake(EntityId): bool`/`SafetyNetIntervalTicks: const int` 在 Task 2
  定义后,Task 3/4/5/6 全部按同样签名引用,没有出现改名不同步的情况。
