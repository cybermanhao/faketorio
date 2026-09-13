# MiningDrills 休眠/唤醒 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `MiningDrills`(采矿机)在"脚下矿脉挖空(无目标)"或"产出堵在箱子/大箱里"
时不再每 tick 跑完整的 `MiningDrillTickPreSettle`/`PostSettle` 逻辑,只做一次轻量
待机电力登记,直到一个明确的外部事件(玩家命令或机械臂动作)把它唤醒;"产出堵在
传送带里"这种情况明确不做,维持现状。

**Architecture:** 复用 `Machines` 休眠/唤醒(已合并 main,commit 范围
`782f1ef..487c858`)已验证的模式:`_awake`/`_asleep` 两个 `OrderedEntityIdList`、
快照数组遍历、60 tick 安全网、待机能耗不变。比 `Machines` 多一块新基础设施:
`_blockedOutputWaiters`(按坐标反查的等待表),因为采矿机的产出目标是**别人的**
库存,不能像 `Machines` 那样直接用自己的 `EntityId` 做唤醒 key。这次直接把
`Machines` 最终审查抓出来的"安全网唤醒当 tick 重复登记电力需求"这个真 bug 的
修复模式,从一开始就写进实现里,不用重新发现。

**Tech Stack:** C# / .NET 8,xUnit,现有的 `Faketorio.Sim`/`Faketorio.Sim.Tests`/
`Faketorio.Sim.Bench` 三个项目,不引入新依赖。

**Spec:** `docs/superpowers/specs/2026-09-13-miningdrills-sleep-wake-design.md`

## Global Constraints

- 只改 `MiningDrills` 系统;`Inserters` 的休眠不在本计划范围内(spec §0、§9)。
- 待机能耗保持现状:睡着的采矿机仍每 tick 登记 `RegisterDemand`(spec §7)。
- 情况 C(产出堵在传送带里)**不睡**,维持现状每 tick 正常跑(spec §1、§9)。
- `_awake`/`_asleep`/`_blockedOutputWaiters` 都不序列化,`MiningDrills.WriteState` 不变(spec §6)。反序列化/构建场景后所有已注册采矿机一律先进 `_awake`。
- 安全网周期写死常量 `SafetyNetIntervalTicks = 60`(同 `Machines`),不做运行时可配置(spec §9)。
- `MiningDrillsTickPreSettle`/`PostSettle` 循环体内会调用可能修改 `_awake`/`_asleep` 的方法,必须对快照数组 `foreach`,不能直接对活列表 `foreach`(spec §6,复用 `OrderedEntityIdList.ToArray()`,已在 `Machines` 那轮的 Task 1 加过,直接复用,不用重新实现)。
- **从一开始就要写对**(不是等 review 发现再修):安全网当 tick 唤醒的采矿机,这一 tick 的待机需求已经在 asleep 循环里登记过一次了,awake 循环必须跳过它,不能重复 `RegisterDemand`(spec §5,`_safetyNetWokenDrillsThisTick` 模式,直接照抄 `Machines` 最终审查 Finding 1 的修复)。
- **唤醒测试必须精确断言"事件发生的那个 tick"**,不能用宽松的 eventual-consistency 循环(等到 `IsAwake==true` 就算过)——那样可能被 60 tick 安全网碰巧满足,测不出真正要测的快速唤醒路径本身(spec §8,直接应用 `Machines` 最终审查 Finding 2 的教训)。
- 本次改动预期让所有跑到几百/几千 tick 的确定性测试哈希发生变化,这是预期结果,不是回归,必须重新生成并人工确认合理性(spec §6)。

---

## Task 1: `MiningDrills` 的 awake/asleep 状态模型

**Files:**
- Modify: `sim/Faketorio.Sim/MiningDrills.cs`
- Test: `sim/Faketorio.Sim.Tests/MiningDrillsTests.cs`(新建——目前没有专门测这个类的文件;**在动手前先跑一次 `grep -rn "class MiningDrillsTests" sim/Faketorio.Sim.Tests/` 确认这个文件真的不存在**——`Machines` 那轮的 Task 1 就是因为 brief 里这句断言错了,导致实现者覆盖删掉了 5 条无关测试,靠 review 才抓回来,这次直接把验证步骤写进任务里,不要重蹈覆辙)

**Interfaces:**
- Consumes: `OrderedEntityIdList.ToArray(): EntityId[]`(已存在,`Machines` 那轮加的,签名是 `public EntityId[] ToArray() => _ids.ToArray();`)。
- Produces:
  - `public void MarkAwake(EntityId id)` —— 从 `_asleep` 移到 `_awake`,已在 `_awake` 则空操作,未注册的 id 空操作(不抛异常——后续任务的唤醒调用点可能对着"目标不一定是采矿机"的实体调用)。
  - `public void MarkAsleep(EntityId id)` —— 从 `_awake` 移到 `_asleep`,已在 `_asleep` 则空操作。
  - `internal EntityId[] AwakeSnapshot()` / `internal EntityId[] AsleepSnapshot()`。
  - `public bool IsAwake(EntityId id)`。
  - `public const int SafetyNetIntervalTicks = 60;`
  - `RegisterDrill` 改为把新采矿机放进 `_awake`(而不是只塞进 `_order`)。
  - `UnregisterDrill` 同时从 `_awake`/`_asleep` 里移除(`_blockedOutputWaiters` 的清理留给 Task 2,那时候这个字段才存在)。

- [ ] **Step 1: 写失败测试**

新建 `sim/Faketorio.Sim.Tests/MiningDrillsTests.cs`:

```csharp
using Faketorio.Sim.Entities;

namespace Faketorio.Sim.Tests;

public class MiningDrillsTests
{
    [Fact]
    public void RegisterDrill_StartsAwake()
    {
        var m = new MiningDrills();
        var id = new EntityId(1, 1);
        m.RegisterDrill(id);

        Assert.True(m.IsAwake(id));
        Assert.Contains(id, m.AwakeSnapshot());
        Assert.DoesNotContain(id, m.AsleepSnapshot());
    }

    [Fact]
    public void MarkAsleep_MovesFromAwakeToAsleep()
    {
        var m = new MiningDrills();
        var id = new EntityId(2, 1);
        m.RegisterDrill(id);

        m.MarkAsleep(id);

        Assert.False(m.IsAwake(id));
        Assert.Contains(id, m.AsleepSnapshot());
        Assert.DoesNotContain(id, m.AwakeSnapshot());
    }

    [Fact]
    public void MarkAwake_MovesFromAsleepBackToAwake()
    {
        var m = new MiningDrills();
        var id = new EntityId(3, 1);
        m.RegisterDrill(id);
        m.MarkAsleep(id);

        m.MarkAwake(id);

        Assert.True(m.IsAwake(id));
        Assert.Contains(id, m.AwakeSnapshot());
    }

    [Fact]
    public void MarkAsleep_AlreadyAsleep_IsNoOp()
    {
        var m = new MiningDrills();
        var id = new EntityId(4, 1);
        m.RegisterDrill(id);
        m.MarkAsleep(id);

        m.MarkAsleep(id);

        Assert.Single(m.AsleepSnapshot());
    }

    [Fact]
    public void MarkAwake_AlreadyAwake_IsNoOp()
    {
        var m = new MiningDrills();
        var id = new EntityId(5, 1);
        m.RegisterDrill(id);

        m.MarkAwake(id);

        Assert.Single(m.AwakeSnapshot());
    }

    [Fact]
    public void MarkAwake_UnregisteredEntity_IsNoOp()
    {
        var m = new MiningDrills();
        var id = new EntityId(6, 1);

        m.MarkAwake(id);   // 不应该抛异常

        Assert.Empty(m.AwakeSnapshot());
        Assert.Empty(m.AsleepSnapshot());
    }

    [Fact]
    public void UnregisterDrill_RemovesFromAwakeOrAsleep()
    {
        var m = new MiningDrills();
        var idAwake = new EntityId(7, 1);
        var idAsleep = new EntityId(8, 1);
        m.RegisterDrill(idAwake);
        m.RegisterDrill(idAsleep);
        m.MarkAsleep(idAsleep);

        m.UnregisterDrill(idAwake);
        m.UnregisterDrill(idAsleep);

        Assert.Empty(m.AwakeSnapshot());
        Assert.Empty(m.AsleepSnapshot());
    }

    [Fact]
    public void SafetyNetIntervalTicks_Is60()
        => Assert.Equal(60, MiningDrills.SafetyNetIntervalTicks);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~MiningDrillsTests"`
Expected: 编译失败,`MarkAwake`/`MarkAsleep`/`AwakeSnapshot`/`AsleepSnapshot`/`IsAwake`/`SafetyNetIntervalTicks` 不存在。

- [ ] **Step 3: 实现**

在 `sim/Faketorio.Sim/MiningDrills.cs` 里,`_order` 字段声明之后加两个新字段,
`ActiveIdsList` 之后加新方法,`RegisterDrill`/`UnregisterDrill` 按下面改:

```csharp
    private readonly OrderedEntityIdList _order = new();
    private readonly OrderedEntityIdList _awake = new();
    private readonly OrderedEntityIdList _asleep = new();

    public IReadOnlyList<EntityId> ActiveIds => _order.Ids;
    internal List<EntityId> ActiveIdsList => _order.IdsList;
    internal int StateCount => _states.Count;

    // 60 tick(1 秒游戏时间)的兜底安全网周期——见设计 spec §5/§9。
    public const int SafetyNetIntervalTicks = 60;

    public void RegisterDrill(EntityId id)
    {
        _states[id] = new DrillRuntimeState(-1, -1, 0, false, 0);
        _order.Add(id);
        _awake.Add(id);   // 新采矿机一律先醒着,走一次正常 tick 自己判断该不该睡。
    }

    public void UnregisterDrill(EntityId id)
    {
        _states.Remove(id);
        _order.Remove(id);
        _awake.Remove(id);
        _asleep.Remove(id);
    }

    public void MarkAwake(EntityId id)
    {
        if (!_states.ContainsKey(id)) return;   // 未注册(比如目标不是采矿机):空操作
        _asleep.Remove(id);
        _awake.Add(id);
    }

    public void MarkAsleep(EntityId id)
    {
        if (!_states.ContainsKey(id)) return;
        _awake.Remove(id);
        _asleep.Add(id);
    }

    internal EntityId[] AwakeSnapshot() => _awake.ToArray();
    internal EntityId[] AsleepSnapshot() => _asleep.ToArray();

    public bool IsAwake(EntityId id) => _states.ContainsKey(id) && !AsleepContains(id);

    private bool AsleepContains(EntityId id)
    {
        foreach (var x in _asleep.Ids) if (x == id) return true;
        return false;
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~MiningDrillsTests"`
Expected: 全部通过。

- [ ] **Step 5: 跑全量测试确认没有破坏别的东西**

Run: `dotnet test -c Release Faketorio.sln`
Expected: 现有全部测试通过(这个任务只加新方法,没碰 `Simulation.cs` 的 tick 逻辑,`ActiveIdsList` 语义不变)。

- [ ] **Step 6: 提交**

```bash
git add sim/Faketorio.Sim/MiningDrills.cs sim/Faketorio.Sim.Tests/MiningDrillsTests.cs
git commit -m "feat(miningdrills): awake/asleep 状态模型 —— MarkAwake/MarkAsleep + 快照访问器"
```

---

## Task 2: 按坐标反查的等待表 `_blockedOutputWaiters`

**Files:**
- Modify: `sim/Faketorio.Sim/MiningDrills.cs`
- Test: `sim/Faketorio.Sim.Tests/MiningDrillsTests.cs`

**Interfaces:**
- Consumes: `MarkAwake(EntityId)`(Task 1)。
- Produces:
  - `public void RegisterBlockedOutputWaiter(EntityId id, int x, int y)` —— 把 `id` 追加进 `_blockedOutputWaiters[(x, y)]`(键不存在则新建列表;同一个 `id` 对同一坐标重复注册不应该产生重复条目——用简单的 `Contains` 检查,列表通常很短,不是性能热点)。
  - `public void WakeWaitersAt(int x, int y)` —— 唤醒指定坐标的全部等待者(调 `MarkAwake`)并把该 key 从表里移除;坐标不存在则空操作。
  - `UnregisterDrill` 追加:从 `_blockedOutputWaiters` 的所有 value 列表里移除该 `id`(防止内存泄漏——spec §3 明确要求)。

- [ ] **Step 1: 写失败测试**

追加到 `sim/Faketorio.Sim.Tests/MiningDrillsTests.cs`:

```csharp
    [Fact]
    public void RegisterBlockedOutputWaiter_ThenWakeWaitersAt_WakesUpAndClears()
    {
        var m = new MiningDrills();
        var id = new EntityId(10, 1);
        m.RegisterDrill(id);
        m.MarkAsleep(id);
        m.RegisterBlockedOutputWaiter(id, 5, 7);

        m.WakeWaitersAt(5, 7);

        Assert.True(m.IsAwake(id));
    }

    [Fact]
    public void WakeWaitersAt_MultipleWaitersSameCoordinate_WakesAll()
    {
        var m = new MiningDrills();
        var idA = new EntityId(11, 1);
        var idB = new EntityId(12, 1);
        m.RegisterDrill(idA);
        m.RegisterDrill(idB);
        m.MarkAsleep(idA);
        m.MarkAsleep(idB);
        m.RegisterBlockedOutputWaiter(idA, 3, 3);
        m.RegisterBlockedOutputWaiter(idB, 3, 3);

        m.WakeWaitersAt(3, 3);

        Assert.True(m.IsAwake(idA));
        Assert.True(m.IsAwake(idB));
    }

    [Fact]
    public void WakeWaitersAt_DifferentCoordinate_DoesNotWake()
    {
        var m = new MiningDrills();
        var id = new EntityId(13, 1);
        m.RegisterDrill(id);
        m.MarkAsleep(id);
        m.RegisterBlockedOutputWaiter(id, 1, 1);

        m.WakeWaitersAt(2, 2);   // 不同坐标

        Assert.False(m.IsAwake(id));
    }

    [Fact]
    public void WakeWaitersAt_NoWaiters_IsNoOp()
    {
        var m = new MiningDrills();
        m.WakeWaitersAt(99, 99);   // 不应该抛异常
    }

    [Fact]
    public void WakeWaitersAt_SameCoordinateCalledTwice_SecondCallIsNoOp()
    {
        // WakeWaitersAt 唤醒后要把 key 从表里移除——第二次调用同一坐标不应该
        // 再"唤醒"任何东西(此时列表已空,验证的是"不留残留状态"而不是具体行为)。
        var m = new MiningDrills();
        var id = new EntityId(14, 1);
        m.RegisterDrill(id);
        m.MarkAsleep(id);
        m.RegisterBlockedOutputWaiter(id, 4, 4);

        m.WakeWaitersAt(4, 4);
        m.MarkAsleep(id);   // 手动睡回去,模拟"唤醒后又堵住了"
        m.WakeWaitersAt(4, 4);   // 表已经空了,这次调用不应该再把它唤醒

        Assert.False(m.IsAwake(id));
    }

    [Fact]
    public void UnregisterDrill_RemovesFromBlockedOutputWaiters()
    {
        // 采矿机被卸载后,如果还挂在某个坐标的等待列表里,必须被清理掉——
        // 否则 WakeWaitersAt 会对着一个已经不存在状态的 EntityId 调 MarkAwake
        // (MarkAwake 本身对未注册 id 是空操作,不会崩溃,但列表会无限增长)。
        var m = new MiningDrills();
        var idStays = new EntityId(15, 1);
        var idRemoved = new EntityId(16, 1);
        m.RegisterDrill(idStays);
        m.RegisterDrill(idRemoved);
        m.MarkAsleep(idStays);
        m.MarkAsleep(idRemoved);
        m.RegisterBlockedOutputWaiter(idStays, 9, 9);
        m.RegisterBlockedOutputWaiter(idRemoved, 9, 9);

        m.UnregisterDrill(idRemoved);
        m.WakeWaitersAt(9, 9);

        Assert.True(m.IsAwake(idStays));
        // idRemoved 已卸载,IsAwake 对未注册 id 恒为 false,这里只验证 idStays 没受影响
        // ——UnregisterDrill 没有把整个 (9,9) 列表清空,只精确移除了 idRemoved 那一条。
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~MiningDrillsTests"`
Expected: 新增的几条编译失败(`RegisterBlockedOutputWaiter`/`WakeWaitersAt` 不存在)。

- [ ] **Step 3: 实现**

在 `sim/Faketorio.Sim/MiningDrills.cs` 里,`_asleep` 字段声明之后加:

```csharp
    // 按产出坐标反查"谁在等这个格子腾空间"——采矿机的产出目标是别人的库存
    // (箱子/大箱),不是自己的,不能像 Machines 那样直接用自身 EntityId 做唤醒 key。
    private readonly Dictionary<(int X, int Y), List<EntityId>> _blockedOutputWaiters = new();
```

`AsleepSnapshot()`/`IsAwake` 方法群之后加:

```csharp
    public void RegisterBlockedOutputWaiter(EntityId id, int x, int y)
    {
        if (!_blockedOutputWaiters.TryGetValue((x, y), out var waiters))
            _blockedOutputWaiters[(x, y)] = waiters = new List<EntityId>();
        if (!waiters.Contains(id)) waiters.Add(id);
    }

    // 某个坐标腾出了库存空间——唤醒所有在这个坐标排队等待的采矿机,并把这个
    // key 从表里移除。
    public void WakeWaitersAt(int x, int y)
    {
        if (!_blockedOutputWaiters.TryGetValue((x, y), out var waiters)) return;
        foreach (var id in waiters) MarkAwake(id);
        _blockedOutputWaiters.Remove((x, y));
    }
```

`UnregisterDrill` 改成:

```csharp
    public void UnregisterDrill(EntityId id)
    {
        _states.Remove(id);
        _order.Remove(id);
        _awake.Remove(id);
        _asleep.Remove(id);
        foreach (var waiters in _blockedOutputWaiters.Values) waiters.Remove(id);
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~MiningDrillsTests"`
Expected: 全部通过。

- [ ] **Step 5: 跑全量测试确认没有破坏别的东西**

Run: `dotnet test -c Release Faketorio.sln`
Expected: 全部通过。

- [ ] **Step 6: 提交**

```bash
git add sim/Faketorio.Sim/MiningDrills.cs sim/Faketorio.Sim.Tests/MiningDrillsTests.cs
git commit -m "feat(miningdrills): 按坐标反查的等待表 —— RegisterBlockedOutputWaiter/WakeWaitersAt"
```

---

## Task 3: Simulation 里接入睡眠(两个闲置分支 + 双轨 tick 循环 + 安全网去重)

**Files:**
- Modify: `sim/Faketorio.Sim/Simulation.cs`(`MiningDrillsTickPreSettle`/`MiningDrillTickPreSettle`/`MiningDrillsTickPostSettle`,当前分别在 `:781`/`:802`/`:870`——**先重新 grep 确认行号,Task 1/2 不碰 `Simulation.cs`,理论上不变,但养成先核对的习惯**)
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`

**Interfaces:**
- Consumes: `MiningDrills.MarkAsleep(id)`、`MiningDrills.AwakeSnapshot()`、`MiningDrills.AsleepSnapshot()`、`MiningDrills.SafetyNetIntervalTicks`、`MiningDrills.RegisterBlockedOutputWaiter(id, x, y)`(Task 1/2)。
- Produces: 本任务完成后,采矿机闲置时会真的停止跑重逻辑;唤醒只能通过 60 tick 安全网发生(Task 4 才加快速外部唤醒)——这个中间状态是可独立测试、可独立评审的一步,同 `Machines` 那轮的 Task 3。

- [ ] **Step 1: 写失败测试**

追加到 `sim/Faketorio.Sim.Tests/SimulationTests.cs`(紧跟在 `MiningDrill_FindsResourceInFootprint_AndExtractsToOutputChest` 附近,复用 `PlaceDrill`/`PlacePoweredDrillInfra` helper):

```csharp
    [Fact]
    public void MiningDrill_NoResourceUnderFootprint_GoesAsleepAndStaysAsleep()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        // 找一个确定没有矿的 2x2 区域(默认种子全局覆盖率只有 ~11%,大多数格子
        // 都是空的)——用同样的探测手法但反过来断言:必须全空。
        int px = 0, py = 20;   // 远离其它测试用到的坐标,避免踩到别的矿脉
        bool anyResource = false;
        for (int dy = 0; dy < 2 && !anyResource; dy++)
            for (int dx = 0; dx < 2 && !anyResource; dx++)
                if (!sim.Resources.GetResourceAt(px + dx, py + dy).IsEmpty) anyResource = true;
        Assert.False(anyResource, "test assumes (0,20)-(1,21) has no ore under the default seed — if this fails, pick a different empty coordinate.");

        sim.Submit(PlaceDrill(sim, px, py, rotation: 1));
        sim.Step();   // 放置
        var drillId = sim.World.GetEntityAt(px, py);

        sim.Step();   // 目标搜索失败 -> 睡

        Assert.False(sim.MiningDrills.IsAwake(drillId));

        long progressBefore = sim.MiningDrills.GetProgress(drillId);
        for (int t = 0; t < 200; t++) sim.Step();   // 远超一次安全网周期(60),期间没有任何外部事件

        Assert.Equal(progressBefore, sim.MiningDrills.GetProgress(drillId));   // 完全没有进展——安全网把它闪醒了几次,但每次重新搜索仍然找不到矿,又睡回去
        Assert.Equal(-1, sim.MiningDrills.GetTargetX(drillId));
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~MiningDrill_NoResourceUnderFootprint_GoesAsleepAndStaysAsleep"`
Expected: 失败(现在没有采矿机会真的睡着,`IsAwake` 永远 true)。

- [ ] **Step 3: 实现**

`Simulation` 类里(`Machines` 那轮加的 `_safetyNetWokenThisTick` 字段附近)新增:

```csharp
    private readonly HashSet<EntityId> _safetyNetWokenDrillsThisTick = new();
```

把 `MiningDrillsTickPreSettle`(原 `:781-800`)改成:

```csharp
    private void MiningDrillsTickPreSettle()
    {
        _safetyNetWokenDrillsThisTick.Clear();
        foreach (var id in MiningDrills.AsleepSnapshot())
        {
            ref var data = ref Entities.Get(id);
            var proto = (MiningDrillPrototype)Prototypes.GetById(data.ProtoId);
            ElectricGrid.RegisterDemand(id, data.X, data.Y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
            if (id.Index % MiningDrills.SafetyNetIntervalTicks == Tick % MiningDrills.SafetyNetIntervalTicks)
            {
                MiningDrills.MarkAwake(id);
                _safetyNetWokenDrillsThisTick.Add(id);
            }
        }

        foreach (var id in MiningDrills.AwakeSnapshot())
        {
            if (_safetyNetWokenDrillsThisTick.Contains(id)) continue;   // 本 tick 已经登记过需求,跳过重复登记(Machines 最终审查 Finding 1 同款修复)
            ref var data = ref Entities.Get(id);
            // 排队转向(RotateEntity):挖完待排出(Completed)时危险,不在这消费——
            // 等下 tick flush 成功、Completed 归 false 了再应用。
            if (!MiningDrills.IsCompleted(id))
            {
                int pendingRot = MiningDrills.GetPendingRotation(id);
                if (pendingRot >= 0)
                {
                    data.Rotation = (byte)pendingRot;
                    MiningDrills.ClearPendingRotation(id);
                }
            }
            var proto = (MiningDrillPrototype)Prototypes.GetById(data.ProtoId);
            MiningDrillTickPreSettle(id, proto, data.X, data.Y, data.Rotation);
        }
    }
```

`MiningDrillTickPreSettle` 方法体(原 `:802-867`)两处要改:

1. 第 1 步 `else`(非传送带分支)`placed == false` 时,除了现有的
   `ElectricGrid.RegisterDemand` + `return`,加注册等待 + 睡眠:

```csharp
            else
            {
                // 输出堵塞:跳过第 2 步,但仍登记待机能耗;注册为这个坐标的等待者,
                // 等有人从这个库存里拿走东西再被唤醒(见 §3——目标是别人的库存,
                // 按坐标反查,不是按自身 EntityId)。
                ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
                MiningDrills.RegisterBlockedOutputWaiter(id, outX, outY);
                MiningDrills.MarkAsleep(id);
                return;
            }
```

   （这一支只在 `outLineId.IsValid` 为 false——也就是非传送带——的分支里，`outX`/`outY`
   在方法开头已经算好，直接用。传送带分支`outLineId.IsValid` 为 true 时 `placed==false`
   **不加**任何睡眠逻辑，维持现状。）

2. 第 2 步之后、第 3 步之前,新增早退(现状代码这里没有早退,直接落到第 3 步):

```csharp
        // 第 2 步:目标搜索(仅当当前没有目标;第 1 步 flush 失败时不会走到这里)
        if (MiningDrills.GetTargetX(id) == -1)
        {
            int cellCount = proto.TileWidth * proto.TileHeight;
            for (int cell = 0; cell < cellCount; cell++)
            {
                int tx = x + cell % proto.TileWidth;
                int ty = y + cell / proto.TileWidth;
                if (!Resources.GetResourceAt(tx, ty).IsEmpty)
                {
                    MiningDrills.SetTarget(id, tx, ty);
                    break;
                }
            }
        }

        // 仍然没有目标(脚下矿脉挖空,搜索失败):睡眠,永久(资源不会重新生成,
        // 唯一能解除的事件是 RotateEntity 换个 footprint——见 Task 4)。
        if (MiningDrills.GetTargetX(id) == -1)
        {
            ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
            MiningDrills.MarkAsleep(id);
            return;
        }

        // 第 3 步:电力需求登记(无条件——恒定待机能耗)
        ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
```

同理改 `MiningDrillsTickPostSettle`(原 `:870-878`)只遍历 `AwakeSnapshot()` 且跳过安全网当 tick 唤醒的:

```csharp
    private void MiningDrillsTickPostSettle()
    {
        foreach (var id in MiningDrills.AwakeSnapshot())
        {
            if (_safetyNetWokenDrillsThisTick.Contains(id)) continue;
            ref var data = ref Entities.Get(id);
            var proto = (MiningDrillPrototype)Prototypes.GetById(data.ProtoId);
            MiningDrillTickPostSettle(id, proto);
        }
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~MiningDrill_NoResourceUnderFootprint_GoesAsleepAndStaysAsleep"`
Expected: 通过。

- [ ] **Step 5: 跑全量测试,记录哪些既有测试开始失败**

Run: `dotnet test -c Release Faketorio.sln`
Expected: **预期会有失败**——任何直接操纵采矿机产出目标库存(绕过 `WakeWaitersAt`)且在少于 60 tick 内断言的既有测试都可能受影响;把完整失败列表记下来,交给 Task 5 处理,**这一步不要现在就去改测试**(同 `Machines` 那轮 Task 3 的做法——很多失败会在 Task 4 加完外部唤醒之后自动消失)。

- [ ] **Step 6: 提交(即使全量测试还有已知失败)**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "$(cat <<'EOF'
feat(miningdrills): 接入睡眠状态机 —— 两个闲置分支自动 MarkAsleep,双轨 tick 循环

MiningDrillsTickPreSettle/PostSettle 改成拍快照遍历 AwakeSnapshot/
AsleepSnapshot,睡着的采矿机只做待机电力登记 + 60 tick 安全网取模判断
(直接带上 _safetyNetWokenDrillsThisTick 去重,不重演 Machines 最终审查
Finding 1 的重复登记 bug)。尚未接入外部唤醒调用点(Task 4),此时采矿机
只能靠安全网苏醒(无目标情况本就不会靠安全网真的解除,永久等效睡眠),
预期部分既有测试在少于 60 tick 断言处失败,留给 Task 4/5 处理。
EOF
)"
```

---

## Task 4: 三个外部唤醒调用点

**Files:**
- Modify: `sim/Faketorio.Sim/Simulation.cs`(`RotateEntity` 分支当前在 `:465`;`TransferFromEntity` 分支当前在 `:413`;`InserterTickPostSettle` 阶段 A 抓取的非传送带分支当前在 `:959` 附近——**先重新 grep 确认,Task 3 改了 `MiningDrillsTickPreSettle`/`MiningDrillTickPreSettle`,这些行号大概率会往后移**)
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`

**Interfaces:**
- Consumes: `MiningDrills.MarkAwake(EntityId)`、`MiningDrills.WakeWaitersAt(int x, int y)`(Task 1/2)。

- [ ] **Step 1: 写失败测试**

追加到 `sim/Faketorio.Sim.Tests/SimulationTests.cs`:

```csharp
    [Fact]
    public void RotateEntity_RevealsNewTarget_WakesUpSleepingDrill()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        // (0,20)-(1,21) 空地(同上一个测试的探测);找一个转个向就能碰到矿的朝向,
        // 或者退而求其次:只断言转向后 IsAwake 变 true(RotateEntity 无条件唤醒,
        // 不要求这次转向本身一定找得到矿——设计只承诺"给它一次重新搜索的机会")。
        int px = 0, py = 20;
        sim.Submit(PlaceDrill(sim, px, py, rotation: 1));
        sim.Step();
        var drillId = sim.World.GetEntityAt(px, py);
        sim.Step();   // 没矿 -> 睡
        Assert.False(sim.MiningDrills.IsAwake(drillId));

        sim.Submit(new Command { Type = CommandType.RotateEntity, X = px, Y = py, Rotation = 2 });
        sim.Step();

        Assert.True(sim.MiningDrills.IsAwake(drillId));   // 转向立即唤醒,不管这次搜索最终有没有找到矿
    }

    [Fact]
    public void TransferFromEntity_WakesUpDrillBlockedOnFullOutputChest()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        sim.Submit(PlaceDrill(sim, 0, 2, rotation: 1));   // 朝东输出到 (2,2)
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        var drillId = sim.World.GetEntityAt(0, 2);
        var chestId = sim.World.GetEntityAt(2, 2);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));

        bool foundResource = false;
        for (int dy = 0; dy < 2 && !foundResource; dy++)
            for (int dx = 0; dx < 2 && !foundResource; dx++)
                if (!sim.Resources.GetResourceAt(dx, 2 + dy).IsEmpty) foundResource = true;
        Assert.True(foundResource, "adjust drill placement if the seeded map doesn't have ore here");

        // 把箱子塞满(16 槽 x 某个 stack size 的任意填充物),逼采矿机挖完之后卡住。
        int fillerId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int fillerStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        for (int s = 0; s < 16; s++) chestInv.Insert(fillerId, fillerStack, fillerStack);

        for (int t = 0; t < 200; t++) sim.Step();   // 挖完一份 -> 塞不进箱子 -> 睡
        Assert.False(sim.MiningDrills.IsAwake(drillId));

        // 精确定位箱子腾出空间的那个 tick,断言唤醒发生在紧邻的那个 tick 内——不用
        // 宽松的 eventual-consistency 循环(Machines 最终审查 Finding 2 教训:那样
        // 可能被 60-tick 安全网碰巧满足,测不出 WakeWaitersAt 这条路径本身)。
        int before = chestInv.CountOf(fillerId);
        sim.Submit(TransferFrom(2, 2, fillerId, fillerStack));
        sim.Step();

        Assert.True(chestInv.CountOf(fillerId) < before);   // 命令确实腾出了空间
        Assert.True(sim.MiningDrills.IsAwake(drillId));      // 同一 tick 内唤醒生效
    }

    [Fact]
    public void Inserter_GrabFromChest_WakesUpDrillBlockedOnThatChest()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        sim.Submit(PlaceDrill(sim, 0, 2, rotation: 1));   // 朝东输出到 (2,2)
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        var drillId = sim.World.GetEntityAt(0, 2);
        var chestId = sim.World.GetEntityAt(2, 2);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));

        bool foundResource = false;
        for (int dy = 0; dy < 2 && !foundResource; dy++)
            for (int dx = 0; dx < 2 && !foundResource; dx++)
                if (!sim.Resources.GetResourceAt(dx, 2 + dy).IsEmpty) foundResource = true;
        Assert.True(foundResource, "adjust drill placement if the seeded map doesn't have ore here");

        int fillerId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int fillerStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        for (int s = 0; s < 16; s++) chestInv.Insert(fillerId, fillerStack, fillerStack);

        for (int t = 0; t < 200; t++) sim.Step();
        Assert.False(sim.MiningDrills.IsAwake(drillId));

        // 现在才放机械臂(身后=箱子(2,2)、身前=另一个箱子(3,2)),让它把箱子里的东西搬走。
        sim.Submit(PlaceChest(sim, 4, 2));
        sim.Submit(PlaceInserter(sim, 3, 2, rotation: 1));   // East: pickup(2,2) chest -> drop(4,2) chest
        sim.Step();

        int fillCount = chestInv.CountOf(fillerId);
        int grabTick = -1;
        for (int t = 0; t < 200; t++)
        {
            sim.Step();
            if (chestInv.CountOf(fillerId) < fillCount) { grabTick = t; break; }
        }

        Assert.True(grabTick >= 0);   // 机械臂确实从箱子里抓走了东西
        Assert.True(sim.MiningDrills.IsAwake(drillId));   // WakeWaitersAt 与抓取同一次 Step() 内生效
    }
```

`PlaceInserter`/`PlaceChest`/`TransferFrom` 都已经是这个测试文件里存在的
helper——写这几个新测试之前,先用
`grep -n "private static Command PlaceInserter\|private static Command PlaceChest\|private static Command TransferFrom" sim/Faketorio.Sim.Tests/SimulationTests.cs`
确认精确签名,照抄现有调用风格,不要臆造参数。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~RotateEntity_RevealsNewTarget_WakesUpSleepingDrill|FullyQualifiedName~TransferFromEntity_WakesUpDrillBlockedOnFullOutputChest|FullyQualifiedName~Inserter_GrabFromChest_WakesUpDrillBlockedOnThatChest"`
Expected: 全部失败(还没有任何外部唤醒调用点)。

- [ ] **Step 3: 实现**

`RotateEntity` 分支(`Simulation.cs`,重新 grep 定位当前行号)里,**不是**
`MiningDrillPrototype && IsCompleted` 那个 `SetPendingRotation` 分支(那个是
案例 B 的危险窗口,不需要唤醒——见 spec §2 的更正说明),而是最后那个
"其它情况……立即生效"的 `else` 分支:

```csharp
                else
                {
                    // 其它情况(含空闲的机械臂/采矿机、箱子/机器/电线杆/发电机):立即生效——
                    // 它们的 tick 逻辑本就每 tick 现读 EntityData.Rotation。
                    rData.Rotation = command.Rotation;
                    if (rProto is MiningDrillPrototype) MiningDrills.MarkAwake(rid);   // 转向可能露出新矿格
                }
```

（**这里是对 spec §2 文字描述的一个更正**:spec 写的是"在 `SetPendingRotation`
之后调 `MarkAwake`",但那是案例 B——挖完待排出的危险窗口——对应的分支,采矿机
在那个分支时 `IsCompleted==true` 恒不是案例 A 的"无目标"状态,不需要在那里唤醒。
真正需要唤醒的是空闲采矿机走的"立即生效"分支,因为只有这个分支处理的是
`IsCompleted==false` 的采矿机,才可能是案例 A 在睡。写代码时以这里的更正为准,
不要照抄 spec §2 的原始文字。）

`TransferFromEntity` 分支(`Simulation.cs:413` 附近,现有的
`if (sourceRole == 2 && removed > 0) Machines.MarkAwake(eid2);` 那一行之后)加:

```csharp
                if (sourceRole == 2 && removed > 0) Machines.MarkAwake(eid2);
                if (removed > 0) MiningDrills.WakeWaitersAt(command.X, command.Y);
                return;
```

`InserterTickPostSettle` 阶段 A 抓取的非传送带分支(`Simulation.cs` 里
`inv.Remove(itemId, 1); Inserters.Grab(id, itemId); if (role == 2) Machines.MarkAwake(pickEntity);`
那几行,重新 grep 定位):

```csharp
                        int itemId = inv[s].ItemProtoId;
                        inv.Remove(itemId, 1);
                        Inserters.Grab(id, itemId);
                        if (role == 2) Machines.MarkAwake(pickEntity);
                        MiningDrills.WakeWaitersAt(pickX, pickY);
                        break;
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~RotateEntity_RevealsNewTarget_WakesUpSleepingDrill|FullyQualifiedName~TransferFromEntity_WakesUpDrillBlockedOnFullOutputChest|FullyQualifiedName~Inserter_GrabFromChest_WakesUpDrillBlockedOnThatChest"`
Expected: 全部通过。

- [ ] **Step 5: 跑全量测试,重新记录失败列表**

Run: `dotnet test -c Release Faketorio.sln`
Expected: 失败数应该比 Task 3 结束时少。把剩下的失败列表交给 Task 5。

- [ ] **Step 6: 提交**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "feat(miningdrills): 三个外部唤醒调用点 —— RotateEntity/TransferFromEntity/机械臂抓取"
```

---

## Task 5: 修复受影响的既有测试 + 全量回归

**Files:**
- Modify: `sim/Faketorio.Sim.Tests/SimulationTests.cs`(Task 3/4 Step 5 记录下来的失败列表)

**Interfaces:**
- 无新接口——本任务只修测试断言/测试内部的库存操纵方式,不改产品代码。

- [ ] **Step 1: 确认当前失败列表**

Run: `dotnet test -c Release Faketorio.sln`,把完整失败测试名列出来。

- [ ] **Step 2: 逐条处理**

对每一条:判断它是不是"直接操纵采矿机产出目标库存(绕过 `WakeWaitersAt`)+
少于 60 tick 内断言"的模式——是,就在直接操纵库存之后加一行显式
`sim.MiningDrills.WakeWaitersAt(x, y)`(或者改成走 `TransferFrom`/真实机械臂
路径,视哪种更贴近该测试想验证的行为而定,同 `Machines` 那轮 Task 5 处理
`Machine_OutputBlocked_HoldsCompletedUntilSpaceFrees` 的做法);不是,就按
spec §6"允许改变时序"的既有共识调整断言。不要不看内容就大范围加唤醒调用
"让测试过"——每一条都要能讲清楚"这是不是一个真实生产路径覆盖不到的场景"。

- [ ] **Step 3: 跑全量测试确认全绿**

Run: `dotnet test -c Release Faketorio.sln`
Expected: 全部通过。

- [ ] **Step 4: 提交**

```bash
git add sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "test(miningdrills): 修复休眠机制引入后受影响的既有测试断言"
```

---

## Task 6: 分阶段 profiler 验证性能收益

**Files:**
- 不改代码,只跑 `Faketorio.Sim.Bench` 采集数据。

- [ ] **Step 1: 在默认压测场景下验证**

```bash
dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --scale 100 --ticks 400 --warmup 2 --iterations 5 --json
dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --scale 1000 --ticks 300 --warmup 2 --iterations 5 --json
dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --scale 1500 --ticks 300 --warmup 2 --iterations 5 --json
```

记录 `MiningDrills` 阶段的 `ns`/`pctOfTick`,以及三个 scale 点各自的总 `minNsPerTick`。
跟 `Machines` 那轮完成后(commit `487c858`)的历史数据比较:scale 100 时
`minNsPerTick` 约 522,836 ns,scale 1000 约 9,571,315 ns,scale 1500 约
17,232,194 ns(这几个数字来自那一轮之后的 UPS-vs-scale 复核,可能有正常的
run-to-run 波动,不要求精确复现,只看方向)。**预期这次的提升比 `Machines`
那轮明显**,因为默认场景里 fed-drill 覆盖率只有 ~13%,相当比例的采矿机长期
处于案例 A(无目标)——不同于 `Machines` 那轮"机器持续繁忙、休眠很少真正触发"
的情况。

- [ ] **Step 2: 记录结果,决定要不要往下细挖**

如果 `MiningDrills` 阶段耗时明显下降(预期结果)且总 `minNsPerTick` 也跟着
明显下降,记录到 roadmap(Task 7 处理)。如果总耗时没有像预期那样明显改善,
不需要在本计划范围内继续深挖——记一笔"实测收益低于预期,原因待查"留给 roadmap,
不要在这个任务里临时扩大范围去调查。

---

## Task 7: Golden 重新生成 + roadmap 记录 + bench baseline 重新校准

**Files:**
- Modify: `sim/Faketorio.Sim.Tests/BenchScenarioTests.cs`(`GOLDEN_TICK_800` 常量)
- Modify: `bench/golden.json`
- Modify: `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`

- [ ] **Step 1: 重新生成 `GOLDEN_TICK_800`**

Run: `dotnet test -c Release sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~SmallScale_HashSequence_IsStableAcrossRuns"`,
从失败输出的 `Actual:` 里读新哈希值,替换 `BenchScenarioTests.cs` 里的
`GOLDEN_TICK_800` 常量,注释更新成说明"因 MiningDrills 休眠/唤醒机制引入,
行为时序改变(spec §6 预期内)"。

- [ ] **Step 2: 重新生成 `bench/golden.json`**

```bash
dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --update-golden
```

`baselineNsPerTick` 手动改回 `0`(标记"未校准",按 `bench/README.md` 流程,
校准要等推到 main、CI 跑出干净的 `bench-report` 之后才做)。

- [ ] **Step 3: 全量测试 + 提交**

```bash
dotnet test -c Release Faketorio.sln
git add sim/Faketorio.Sim.Tests/BenchScenarioTests.cs bench/golden.json
git commit -m "chore(bench): MiningDrills 休眠/唤醒引入后重新生成 golden —— 行为时序改变,预期内"
```

- [ ] **Step 4: 推送 + 等 CI + 校准 baseline**

```bash
git push origin main
```
等 CI 绿,下载 `bench-report` artifact,读 `minNsPerTick`,回填
`bench/golden.json` 的 `baselineNsPerTick`,单独提交,推送。

- [ ] **Step 5: 更新 roadmap**

在 `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md` 里记录:
`MiningDrills` 休眠/唤醒已完成(commit 引用),Task 6 的验证数据,以及
"`Inserters` 的休眠留待后续"这个明确的范围边界。提交、推送。

---

## Self-Review Notes

- **Spec 覆盖**:§1(三种状态)→ Task 3;§2(案例 A 唤醒——含本计划对 spec
  文字的更正,见 Task 4 Step 3 的说明)→ Task 4;§3(案例 B 的等待表)→
  Task 2;§4(睡眠判定点)→ Task 3;§5(每 tick 算法 + 安全网去重)→
  Task 3;§6(遍历安全性/确定性)→ Task 1(复用已有 `ToArray()`)+ Task 3
  + Task 7;§7(待机能耗)→ Task 3(`RegisterDemand` 保留);§8(测试计划)
  → Task 1/2/3/4;§9(范围外)→ Global Constraints 明确写了不碰
  `Inserters`、不做案例 C 的睡眠。
- **占位符扫描**:无 "TBD"/"后续实现" 这类模式——每个 Step 3 都是可以直接抄的完整代码块。
- **类型一致性**:`MiningDrills.MarkAwake(EntityId)`/`MarkAsleep(EntityId)`/
  `AwakeSnapshot(): EntityId[]`/`AsleepSnapshot(): EntityId[]`/
  `IsAwake(EntityId): bool`/`SafetyNetIntervalTicks: const int`/
  `RegisterBlockedOutputWaiter(EntityId, int, int): void`/
  `WakeWaitersAt(int, int): void` 在 Task 1/2 定义后,Task 3/4/5/6 全部按
  同样签名引用,没有出现改名不同步的情况。
- **已知的 spec 更正**(记在这里,方便 review 时核对):spec §2 描述
  `RotateEntity` 唤醒调用点时,文字上跟着 `SetPendingRotation` 写,但对照
  `Simulation.cs` 实际代码后发现案例 A(无目标、`IsCompleted==false`)的
  采矿机转向走的是"立即生效"的 `else` 分支,不是 `SetPendingRotation` 那个
  危险窗口分支——Task 4 Step 3 已经按实际代码更正,不是简单照抄 spec 文字。
