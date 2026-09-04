# M1 Plan 2: 传送带核心(FFF-176 gap 表示法)Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **状态: ✅ 已合并 main · 已验证** — 主线提交 `72e64ed`..`6ac95e0`(见 `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md` 进度快照)。下方 `- [ ]` 复选框为执行期工件,不代表当前状态。

**Goal:** 实现并用严格的单测证明正确的传送带核心算法(FFF-176 的 gap 表示法),外加传送带 prototype 的数据支持——两者都是独立、可单测的构件,尚不接入 `Simulation`。

**Architecture:** `BeltLane` 是一个纯数据结构(不依赖 Godot、不依赖 `Simulation`/`EntityPool`/`WorldGrid`),表示一条传送带 lane 上的物品流:物品不存绝对坐标,只存相对 gap(整数亚格单位)。正常流动只需增减最前面一个 gap(O(1));堵塞时通过 `_openIndex` 缓存"最后一个未压缩到 0 的 gap"下标,该下标只前移不回退,使更新摊还 O(1)。`TransportBeltPrototype` 走既有的 POCO + JSON 数据管线(与 M1 Plan 1 的 `ContainerPrototype` 同构)。

**Tech Stack:** .NET 8(C# 12)、xUnit。延续 `sim/Faketorio.Sim`(纯 .NET 类库)/`sim/Faketorio.Sim.Tests`(xUnit)结构,解决方案 `Faketorio.sln`。

**Spec:** `docs/superpowers/specs/2026-07-03-faketorio-design.md`(第 5.2 节"传送带(FFF-176 方案)"、第 5.6 节"数值表示约定")

## Out of Scope(本计划明确不做,留给后续计划)

- **接入 `Simulation`**:`BeltLane` 本计划中不会被 `Simulation.Step()`/`Apply`/`WriteState` 引用,因此本计划*不*要求它可被 `IStateWriter` 序列化(那是接入计划的职责,接入时会补上)。
- **传送带实体本身**(可放置、双 lane 组合、朝向/旋转、与相邻传送带的物品交接):由后续计划把两个 `BeltLane` 组合成一个传送带实体、接入放置命令与每 tick 系统更新。
- **相邻传送带合并成更长 line**(spec 5.2 的"动态合并/拆分"):`BeltLane` 的构造函数接受任意 `lineLengthSubTiles`,算法与长度无关,因此合并是未来计划的纯增量工作,本计划不实现。
- **机械臂与传送带的增量追踪交互、分离器**:均为更后续的计划。

## Global Constraints

- 模拟层与数据层均不引用任何 Godot 类型,可在纯 .NET 进程中无头运行(xUnit)
- 模拟层禁止 `float`/`double` 参与影响状态的运行时计算;数据加载期可用 double 换算(一次性、确定)
- 数值表示:1 tile = 256 亚格单位(`Units.SubTilesPerTile`);物品间距 0.25 tile = 64 亚格单位
- 热路径禁 LINQ/闭包/装箱

---

### Task 1: BeltLane 构造与队尾插入

**Files:**
- Create: `sim/Faketorio.Sim/Belts/BeltLane.cs`
- Test: `sim/Faketorio.Sim.Tests/BeltLaneTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `Faketorio.Sim.Belts.BeltLane(int lineLengthSubTiles)`(`lineLengthSubTiles < ItemWidthSubTiles` 时抛 `ArgumentOutOfRangeException`)
  - `const int BeltLane.ItemWidthSubTiles = 64`
  - `int BeltLane.Count`
  - `IReadOnlyList<int> BeltLane.Gaps`(仅供测试内省,前到后排列)
  - `bool BeltLane.TryInsertAtBack()`

- [ ] **Step 1: 写失败测试**

`sim/Faketorio.Sim.Tests/BeltLaneTests.cs`:
```csharp
using Faketorio.Sim.Belts;

namespace Faketorio.Sim.Tests;

public class BeltLaneTests
{
    [Fact]
    public void NewLane_IsEmpty()
    {
        var lane = new BeltLane(256);
        Assert.Equal(0, lane.Count);
    }

    [Fact]
    public void Constructor_RejectsLengthShorterThanOneItem()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BeltLane(63));
    }

    [Fact]
    public void TryInsertAtBack_FirstItem_EntersAtBackWithFullGapToExit()
    {
        var lane = new BeltLane(256);
        Assert.True(lane.TryInsertAtBack());
        Assert.Equal(1, lane.Count);
        Assert.Equal(new[] { 192 }, lane.Gaps); // 256 - 64
    }

    [Fact]
    public void TryInsertAtBack_WhenNoRoom_ReturnsFalseAndDoesNotChangeState()
    {
        var lane = new BeltLane(256);
        Assert.True(lane.TryInsertAtBack());  // 单个物品刚好占满整条 256 长的 line
        Assert.False(lane.TryInsertAtBack()); // 队尾无空间
        Assert.Equal(1, lane.Count);
        Assert.Equal(new[] { 192 }, lane.Gaps);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~BeltLaneTests"`
Expected: 编译失败(`BeltLane` 不存在)

- [ ] **Step 3: 实现**

`sim/Faketorio.Sim/Belts/BeltLane.cs`:
```csharp
namespace Faketorio.Sim.Belts;

// 一条传送带 lane 上的物品流,采用 FFF-176 的 gap 表示法(spec 5.2/5.6):
// 不存储每个物品的绝对坐标,只记录物品之间(以及最前物品到出口)的相对
// 距离——整数亚格单位,1 tile = 256。这让正常流动只需增减最前面一个 gap
// (O(1));堵塞时也只需增减"最后一个未压缩到 0 的 gap"。
//
// lineLengthSubTiles 是这条 lane 可用的总长度。M1 Plan 2 只在单个传送带
// 格子(256)上使用这个类型;之后若要把相邻传送带合并成更长的 line,只
// 需传入更大的长度——本类型的算法不需要改动。
public sealed class BeltLane
{
    // spec: 物品间距 0.25 tile = 64 亚格单位(每个物品占用的"槽宽")。
    public const int ItemWidthSubTiles = 64;

    // 从前(出口,position 0)到后(入口)排列。
    // _gaps[0] = 出口到最前物品前沿的距离;
    // _gaps[i](i>0) = 物品 i-1 后沿到物品 i 前沿的距离。
    private readonly List<int> _gaps = new();
    private readonly int _lineLengthSubTiles;

    public BeltLane(int lineLengthSubTiles)
    {
        if (lineLengthSubTiles < ItemWidthSubTiles)
            throw new ArgumentOutOfRangeException(nameof(lineLengthSubTiles));
        _lineLengthSubTiles = lineLengthSubTiles;
    }

    public int Count => _gaps.Count;

    // 仅供测试内省:前到后的 gap 列表,_gaps[0] = 出口到最前物品的距离。
    public IReadOnlyList<int> Gaps => _gaps;

    // 队尾(入口侧)剩余的空闲亚格数——最后一个物品之后到 line 尾端的空间。
    private int BackFreeSubTiles()
    {
        int used = 0;
        foreach (var g in _gaps) used += g;
        used += _gaps.Count * ItemWidthSubTiles;
        return _lineLengthSubTiles - used;
    }

    // 在队尾(入口)插入一个新物品,新物品贴着 line 的入口边界进入。
    // 空间不足时返回 false,不改变任何状态。
    public bool TryInsertAtBack()
    {
        int free = BackFreeSubTiles();
        if (free < ItemWidthSubTiles) return false;
        _gaps.Add(free - ItemWidthSubTiles);
        return true;
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~BeltLaneTests"`
Expected: 4 passed

- [ ] **Step 5: Commit**

```powershell
git -C C:\code\faketorio add -A
git -C C:\code\faketorio commit -m "feat(sim): BeltLane construction and back-insertion (gap representation)"
```

---

### Task 2: BeltLane 前进(Advance)

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltLane.cs`
- Modify: `sim/Faketorio.Sim.Tests/BeltLaneTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `BeltLane`(`_gaps`、`Count`、`TryInsertAtBack`)
- Produces: `void BeltLane.Advance(int speed)`;新增私有字段 `_openIndex`(下一任务 `RemoveFront` 会依赖它,任务边界详见 Task 3)

- [ ] **Step 1: 写失败测试**

在 `sim/Faketorio.Sim.Tests/BeltLaneTests.cs` 的 `BeltLaneTests` 类内追加(闭合大括号之前):
```csharp

    [Fact]
    public void Advance_UnblockedSingleItem_MovesFrontGapForward()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(); // gaps=[192]
        lane.Advance(50);
        Assert.Equal(new[] { 142 }, lane.Gaps);
    }

    [Fact]
    public void Advance_NeverDrivesGapNegative_ClampsAtZero()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(); // gaps=[192]
        lane.Advance(1000);      // 远超剩余 gap
        Assert.Equal(new[] { 0 }, lane.Gaps);
    }

    [Fact]
    public void Advance_CascadesIntoNextGapWhenFrontFullyCloses()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack();  // gaps=[192]
        lane.Advance(100);        // gaps=[92]
        lane.TryInsertAtBack();  // free=256-(92+64+64)=36 -> gaps=[92,36]
        lane.Advance(110);        // 92 耗尽 gaps[0],剩余 18 接着消耗 gaps[1]
        Assert.Equal(new[] { 0, 18 }, lane.Gaps);
    }

    [Fact]
    public void Advance_ContinuesFromCachedOpenIndexAfterFrontCloses()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack();
        lane.Advance(100);
        lane.TryInsertAtBack();
        lane.Advance(110); // gaps=[0,18]
        lane.Advance(5);    // 从缓存的下标(不是 0)继续推进
        Assert.Equal(new[] { 0, 13 }, lane.Gaps);
    }

    [Fact]
    public void Advance_OnEmptyLane_DoesNothing()
    {
        var lane = new BeltLane(256);
        lane.Advance(50); // 不能抛异常
        Assert.Equal(0, lane.Count);
    }

    [Fact]
    public void Advance_WithZeroSpeed_DoesNotChangeGaps()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack();
        lane.Advance(0);
        Assert.Equal(new[] { 192 }, lane.Gaps);
    }

    [Fact]
    public void Advance_ManyTicksWhileBlocked_ConvergesToFullyCompressedWithoutError()
    {
        var lane = new BeltLane(256);
        // 256 / 64 = 4 个物品刚好装满一条 line;每次插入后都把物品尽量往前推,
        // 为下一次插入腾出队尾空间。
        for (int i = 0; i < 4; i++)
        {
            Assert.True(lane.TryInsertAtBack());
            lane.Advance(1000);
        }
        Assert.Equal(4, lane.Count);
        Assert.False(lane.TryInsertAtBack()); // 4*64=256,队尾无空间

        // 此后一直堵塞(没有任何 RemoveFront):重复 Advance 必须保持稳定,
        // 不抛异常、不出现负值、不改变物品数。
        for (int tick = 0; tick < 1000; tick++)
            lane.Advance(7);

        Assert.Equal(4, lane.Count);
        Assert.All(lane.Gaps, g => Assert.True(g >= 0));
        Assert.Equal(0, lane.Gaps[0]); // 完全压缩到出口
    }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~BeltLaneTests"`
Expected: 编译失败(`Advance` 不存在)

- [ ] **Step 3: 实现**

把 `sim/Faketorio.Sim/Belts/BeltLane.cs` 替换为(在 Task 1 内容基础上新增 `_openIndex` 字段与 `Advance` 方法):
```csharp
namespace Faketorio.Sim.Belts;

// 一条传送带 lane 上的物品流,采用 FFF-176 的 gap 表示法(spec 5.2/5.6):
// 不存储每个物品的绝对坐标,只记录物品之间(以及最前物品到出口)的相对
// 距离——整数亚格单位,1 tile = 256。这让正常流动只需增减最前面一个 gap
// (O(1));堵塞时也只需增减"最后一个未压缩到 0 的 gap"。
//
// lineLengthSubTiles 是这条 lane 可用的总长度。M1 Plan 2 只在单个传送带
// 格子(256)上使用这个类型;之后若要把相邻传送带合并成更长的 line,只
// 需传入更大的长度——本类型的算法不需要改动。
public sealed class BeltLane
{
    // spec: 物品间距 0.25 tile = 64 亚格单位(每个物品占用的"槽宽")。
    public const int ItemWidthSubTiles = 64;

    // 从前(出口,position 0)到后(入口)排列。
    // _gaps[0] = 出口到最前物品前沿的距离;
    // _gaps[i](i>0) = 物品 i-1 后沿到物品 i 前沿的距离。
    private readonly List<int> _gaps = new();
    private readonly int _lineLengthSubTiles;

    // 第一个"可能仍未压缩到 0"的下标;之前的下标已确认为 0,Advance 不再
    // 重新扫描它们——这就是摊还 O(1) 的关键。只有 RemoveFront(Task 3)
    // 会把它重置回 0。
    private int _openIndex;

    public BeltLane(int lineLengthSubTiles)
    {
        if (lineLengthSubTiles < ItemWidthSubTiles)
            throw new ArgumentOutOfRangeException(nameof(lineLengthSubTiles));
        _lineLengthSubTiles = lineLengthSubTiles;
    }

    public int Count => _gaps.Count;

    // 仅供测试内省:前到后的 gap 列表,_gaps[0] = 出口到最前物品的距离。
    public IReadOnlyList<int> Gaps => _gaps;

    // 队尾(入口侧)剩余的空闲亚格数——最后一个物品之后到 line 尾端的空间。
    private int BackFreeSubTiles()
    {
        int used = 0;
        foreach (var g in _gaps) used += g;
        used += _gaps.Count * ItemWidthSubTiles;
        return _lineLengthSubTiles - used;
    }

    // 在队尾(入口)插入一个新物品,新物品贴着 line 的入口边界进入。
    // 空间不足时返回 false,不改变任何状态。
    public bool TryInsertAtBack()
    {
        int free = BackFreeSubTiles();
        if (free < ItemWidthSubTiles) return false;
        _gaps.Add(free - ItemWidthSubTiles);
        return true;
    }

    // 让这条 lane 上的物品流前进最多 speed 个亚格。
    // 只触碰 _gaps[_openIndex..] 中被实际消耗到 0 的那些下标,其余原样
    // 保留——这就是 FFF-176 描述的摊还 O(1) 更新。
    public void Advance(int speed)
    {
        if (_gaps.Count == 0) return;
        int remaining = speed;
        int i = _openIndex;
        while (remaining > 0 && i < _gaps.Count)
        {
            int consume = Math.Min(remaining, _gaps[i]);
            _gaps[i] -= consume;
            remaining -= consume;
            if (_gaps[i] > 0) break;
            i++;
        }
        _openIndex = Math.Min(i, _gaps.Count - 1);
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~BeltLaneTests"`
Expected: 11 passed(Task 1 的 4 个 + 本任务的 7 个)

- [ ] **Step 5: Commit**

```powershell
git -C C:\code\faketorio add -A
git -C C:\code\faketorio commit -m "feat(sim): BeltLane.Advance with amortized O(1) gap updates"
```

---

### Task 3: BeltLane 队首移交(IsFrontReady / RemoveFront)

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltLane.cs`
- Modify: `sim/Faketorio.Sim.Tests/BeltLaneTests.cs`

**Interfaces:**
- Consumes: Task 1-2 的 `BeltLane`(`_gaps`、`_openIndex`、`Advance`、`TryInsertAtBack`)
- Produces: `bool BeltLane.IsFrontReady`、`void BeltLane.RemoveFront()`(队首未就绪时抛 `InvalidOperationException`)。至此 `BeltLane` 完成本计划范围内的完整公开接口。

- [ ] **Step 1: 写失败测试**

在 `sim/Faketorio.Sim.Tests/BeltLaneTests.cs` 的 `BeltLaneTests` 类内追加(闭合大括号之前):
```csharp

    [Fact]
    public void IsFrontReady_FalseWhenEmpty()
    {
        var lane = new BeltLane(256);
        Assert.False(lane.IsFrontReady);
    }

    [Fact]
    public void IsFrontReady_FalseWhenFrontGapNotYetZero()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(); // gaps=[192]
        Assert.False(lane.IsFrontReady);
    }

    [Fact]
    public void IsFrontReady_TrueWhenFrontGapReachesZero()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack();
        lane.Advance(1000);
        Assert.True(lane.IsFrontReady);
    }

    [Fact]
    public void RemoveFront_WhenNotReady_Throws()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(); // gaps=[192],队首还没到出口
        Assert.Throws<InvalidOperationException>(() => lane.RemoveFront());
    }

    [Fact]
    public void RemoveFront_FreesSpaceIntoNewFrontGap()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack();  // gaps=[192]
        lane.Advance(100);        // gaps=[92]
        lane.TryInsertAtBack();  // gaps=[92,36]
        lane.Advance(110);        // gaps=[0,18]
        lane.RemoveFront();
        Assert.Equal(1, lane.Count);
        Assert.Equal(new[] { 82 }, lane.Gaps); // 18 + 64(被移除物品腾出的槽宽)
    }

    [Fact]
    public void RemoveFront_WhenLastItem_LeavesLaneEmpty()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack();
        lane.Advance(1000);
        lane.RemoveFront();
        Assert.Equal(0, lane.Count);
        Assert.False(lane.IsFrontReady);
    }

    [Fact]
    public void RemoveFront_ResetsCursorSoTrailingItemsCanAdvanceOnNextTick()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack();  // gaps=[192]
        lane.Advance(100);        // gaps=[92]
        lane.TryInsertAtBack();  // gaps=[92,36]
        lane.Advance(110);        // gaps=[0,18]
        lane.RemoveFront();       // gaps=[82]
        lane.Advance(30);         // 原来的第二个物品现在向出口前进
        Assert.Equal(new[] { 52 }, lane.Gaps); // 82-30
    }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~BeltLaneTests"`
Expected: 编译失败(`IsFrontReady`/`RemoveFront` 不存在)

- [ ] **Step 3: 实现**

在 `sim/Faketorio.Sim/Belts/BeltLane.cs` 的 `Advance` 方法之后(类闭合大括号之前)追加:
```csharp

    // 队首物品是否已经贴到出口(gap 为 0),可以移交给下游(传送带/机械
    // 臂/建筑输入口——移交逻辑本身在后续接入 Simulation 的计划中实现)。
    public bool IsFrontReady => _gaps.Count > 0 && _gaps[0] == 0;

    // 移除队首物品(调用前必须已确认 IsFrontReady)。它腾出的空间并入新
    // 队首的前方 gap;_openIndex 重置为 0,因为后面的物品可能因此重新有
    // 空间前进。
    public void RemoveFront()
    {
        if (!IsFrontReady)
            throw new InvalidOperationException("RemoveFront called when front is not ready");
        _gaps.RemoveAt(0);
        if (_gaps.Count > 0)
            _gaps[0] += ItemWidthSubTiles;
        _openIndex = 0;
    }
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~BeltLaneTests"`
Expected: 18 passed(Task 1-2 的 11 个 + 本任务的 7 个)

- [ ] **Step 5: 全量回归**

Run: `dotnet test C:\code\faketorio\Faketorio.sln`
Expected: 此前所有测试(含 M1 Plan 1 的 40 个)仍全部通过

- [ ] **Step 6: Commit**

```powershell
git -C C:\code\faketorio add -A
git -C C:\code\faketorio commit -m "feat(sim): BeltLane front hand-off (IsFrontReady/RemoveFront)"
```

---

### Task 4: TransportBeltPrototype 数据支持

**Files:**
- Create: `sim/Faketorio.Sim/Prototypes/TransportBeltPrototype.cs`
- Modify: `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`
- Modify: `data/base/entities.json`
- Modify: `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs`

**Interfaces:**
- Consumes: `EntityPrototype`(M1 Plan 1,`sim/Faketorio.Sim/Prototypes/EntityPrototype.cs`,提供 `TileWidth`/`TileHeight`/`Name`/`Id` 等基类字段)、`PrototypeLoader.Parse` 的既有 `switch`(M1 Plan 1)、`PrototypeLoader.GetInt`(既有私有辅助方法)、`PrototypeLoaderTests.Load()`(既有测试辅助方法)
- Produces: `Faketorio.Sim.Prototypes.TransportBeltPrototype : EntityPrototype`,新增字段 `int SpeedSubTilesPerTick`;`PrototypeLoader.Parse` 新增 `"transport-belt"` 分支;`data/base/entities.json` 新增 `transport-belt-basic` 条目

本任务与 Task 1-3 相互独立(不同文件、不同子系统——原型数据管线 vs. 传送带算法),可以并行执行或按任意顺序完成;放在最后是为了让"先啃最难的算法"这一顺序在提交历史中体现出来(呼应 spec §9 的风险应对策略)。

- [ ] **Step 1: 写失败测试**

在 `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` 的 `PrototypeLoaderTests` 类内追加(`UnknownTypeThrows` 方法之后,`ZeroTileWidthThrows` 方法之前均可,顺序不影响测试结果):
```csharp

    [Fact]
    public void LoadsTransportBeltWithSpeed()
    {
        var belt = Load().Get<TransportBeltPrototype>("transport-belt-basic");
        Assert.Equal(8, belt.SpeedSubTilesPerTick);
        Assert.Equal(1, belt.TileWidth);
        Assert.Equal(1, belt.TileHeight);
    }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~LoadsTransportBeltWithSpeed"`
Expected: 编译失败(`TransportBeltPrototype` 不存在)

- [ ] **Step 3: 新建 prototype 类**

`sim/Faketorio.Sim/Prototypes/TransportBeltPrototype.cs`:
```csharp
namespace Faketorio.Sim.Prototypes;

public sealed class TransportBeltPrototype : EntityPrototype
{
    // 无阻挡时每 tick 前进的亚格数(spec 5.6: 1 tile = 256 亚格)。
    // 直接以这个内部整数单位在数据里authoring,不做"物品/秒"之类的换算
    // ——本计划尚未把这个字段接到 BeltLane.Advance,换算辅助函数留给接
    // 入计划按需添加(YAGNI)。
    public int SpeedSubTilesPerTick { get; init; }
}
```

- [ ] **Step 4: 在加载器里注册新类型**

在 `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` 的 `Parse` 方法内,`"container" => ValidateFootprint(...)` 分支之后、`_ => throw ...` 之前插入新分支:
```csharp
            "transport-belt" => ValidateFootprint(new TransportBeltPrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                SpeedSubTilesPerTick = GetInt(el, "speedSubTilesPerTick", 0),
            }),
```

- [ ] **Step 5: 新增数据条目**

把 `data/base/entities.json` 替换为:
```json
[
  { "type": "container", "name": "wooden-chest", "tileWidth": 1, "tileHeight": 1,
    "inventorySize": 16, "minableResult": "wooden-chest", "miningTimeSeconds": 0.5 },
  { "type": "container", "name": "large-chest", "tileWidth": 2, "tileHeight": 3,
    "inventorySize": 48, "minableResult": "large-chest", "miningTimeSeconds": 1.0 },
  { "type": "transport-belt", "name": "transport-belt-basic", "tileWidth": 1, "tileHeight": 1,
    "speedSubTilesPerTick": 8 }
]
```

- [ ] **Step 6: 运行确认通过**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~PrototypeLoaderTests"`
Expected: 7 passed(既有 6 个 + 本任务新增 1 个)

- [ ] **Step 7: 全量回归**

Run: `dotnet test C:\code\faketorio\Faketorio.sln`
Expected: 61 passed, 0 failed(M1 Plan 1 的 40 个 + 本计划 Task 1-3 及其修复轮次新增的 20 个 `BeltLaneTests` + Task 4 新增的 1 个 `LoadsTransportBeltWithSpeed`)

- [ ] **Step 8: Commit**

```powershell
git -C C:\code\faketorio add -A
git -C C:\code\faketorio commit -m "feat(sim): TransportBeltPrototype data support"
```

---

## 完成标准

- `dotnet test` 全绿,不依赖 Godot
- `BeltLane` 的构造、插入、前进(含级联压缩、缓存下标续接、长时间堵塞稳定性)、队首移交全部有单测覆盖,且每个断言都基于对 gap 数值的手工推导(而非"看起来对"的猜测)
- `TransportBeltPrototype` 数据可通过既有 JSON 管线加载,`speedSubTilesPerTick` 正确解析
- `BeltLane` 与 `Simulation`/`WorldGrid`/`EntityPool`/`IStateWriter` 均无耦合,为下一个计划(接入 Simulation:传送带实体、双 lane 组合、朝向、放置命令、每 tick 系统更新、纳入状态哈希)打好独立可测的地基
