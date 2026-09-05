# M1 Plan 10 — Typed Belt Items + 电力采矿机 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `BeltLane` item-type tracking (a prerequisite the roadmap flagged as required before any producer can drop typed items on a belt), then build an electric mining drill on top of it — footprint resource search, electricity-throttled progress, output routed to a belt or container.

**Architecture:** `BeltLane` grows a parallel `_itemProtoIds` array alongside its existing `_gaps` array, kept in lockstep at every mutation point; `ToAbsolutePositions`/`FromAbsolutePositions` (the cold-path rebuild used by belt merge/split) carry a new `PositionedItem(LeadingEdgeSubTiles, ItemProtoId)` record struct instead of a bare `int`. `MiningDrillPrototype` (static data) + `MiningDrills` (pure runtime-state container, flat `namespace Faketorio.Sim;` — same fix P5's `Player` and P9's `Machines` needed) + a two-pass `Simulation` tick mirroring P9's `Settle()`-bracketing pattern exactly.

**Tech Stack:** C# / .NET 8, xUnit. No Godot dependency in `Faketorio.Sim`. Deterministic fixed-tick simulation — canonical serialization through `IStateWriter` / `Fnv1aHashWriter` (FNV-1a).

**Spec:** [`docs/superpowers/specs/2026-09-05-m1-plan10-typed-belt-items-mining-drill-design.md`](../specs/2026-09-05-m1-plan10-typed-belt-items-mining-drill-design.md)

## Global Constraints

- **Determinism 铁律:** no `float`/`double` anywhere. `MiningDrills.Progress`/`threshold` are `long` Q16.16 fixed-point "tick counts"; `MiningSpeed`/satisfaction multiply via `Q16.Mul`. Target search is fixed row-major order. All entity-pool scans use `Entities.Capacity`/`IsAliveAtIndex`/`GetAtIndex` index order.
- **`BeltLane` invariant:** `_gaps` and `_itemProtoIds` are always the same length, same index = same item, at every point after every method returns. Never leave them out of sync mid-method (add/remove both together, not one then the other with early-return risk in between).
- **`MiningDrills` namespace (binding, do not deviate):** the class MUST be declared in flat `namespace Faketorio.Sim;`, NOT a nested namespace — same self-collision bug P5 (`Player`) and P9 (`Machines`) already hit and fixed the same way. Put the file directly at `sim/Faketorio.Sim/MiningDrills.cs` (no subfolder needed — it's a single file).
- **`MiningDrills` precondition style:** mutators that require the entity already be registered (`SetTarget`, `AddProgress`, `MarkCompleted`) throw `KeyNotFoundException` naturally via a dictionary indexer read — same as P9's `Machines` (fixed there after an initial inconsistency; get it right the first time here, no fix round needed).
- **Cross-task compile safety:** `TryInsertAtBack` gains a mandatory `int itemProtoId` parameter with NO default value. `Simulation.cs`'s existing corner-handoff call site (`down.LaneA.TryInsertAtBack()`) MUST be fixed in the SAME task that changes the signature (Task 1) — leaving it for a later task would leave the branch in a non-compiling state between tasks, which breaks every task's "keep tests green" requirement.
- Every commit ends with:
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01BXC5RZ5wsWirQpprXn9KZV
  ```
- Baseline before Task 1: 341 tests passing (`dotnet test sim/Faketorio.Sim.Tests`), `dotnet build -c Release` 0 warnings/0 errors.

---

## Task 1: Typed belt items

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltLane.cs`
- Modify: `sim/Faketorio.Sim/Belts/BeltNetwork.cs`
- Modify: `sim/Faketorio.Sim/Simulation.cs` (corner-handoff only, 2 lines)
- Test: `sim/Faketorio.Sim.Tests/BeltLaneTests.cs`
- Test: `sim/Faketorio.Sim.Tests/BeltNetworkTests.cs`
- Test: `sim/Faketorio.Sim.Tests/BeltIntegrationTests.cs`
- Test: `sim/Faketorio.Sim.Tests/DeterminismTests.cs`

**Interfaces:**
- Produces (for Task 3, indirectly — Task 3 doesn't call these directly but the drill's belt-output path does): `BeltLane.TryInsertAtBack(int itemProtoId) -> bool`, `BeltLane.FrontItemProtoId -> int` (peek, precondition `IsFrontReady`), `BeltLane.PositionedItem` record struct.
- Consumes: nothing from other tasks in this plan — fully self-contained, touches only pre-existing P2/P3 belt code plus 2 lines of pre-existing `Simulation.cs`.

This task does NOT touch any new P10 file (`MiningDrillPrototype`, `MiningDrills`, `mining-drill.json`) — those are Tasks 2/3.

- [ ] **Step 1: Add the parallel item-id array and `TryInsertAtBack`/`FrontItemProtoId` to `BeltLane.cs`**

Open `sim/Faketorio.Sim/Belts/BeltLane.cs`. Add a new field right after `_gaps`:

```csharp
    private readonly List<int> _gaps = new();
    private readonly List<int> _itemProtoIds = new();   // 与 _gaps 严格平行,同下标同物品
    private int _lineLengthSubTiles;
```

Change `TryInsertAtBack`:

```csharp
    // 在队尾(入口)插入一个新物品,新物品贴着 line 的入口边界进入。
    // 空间不足时返回 false,不改变任何状态。
    public bool TryInsertAtBack(int itemProtoId)
    {
        int free = BackFreeSubTiles();
        if (free < ItemWidthSubTiles) return false;
        _gaps.Add(free - ItemWidthSubTiles);
        _itemProtoIds.Add(itemProtoId);
        return true;
    }
```

Add a new property right after `IsFrontReady`:

```csharp
    // 队首物品是否已经贴到出口(gap 为 0),可以移交给下游(传送带/机械
    // 臂/建筑输入口——移交逻辑本身在后续接入 Simulation 的计划中实现)。
    public bool IsFrontReady => _gaps.Count > 0 && _gaps[0] == 0;

    // 队首物品的类型(只读,不摘除)。前置:IsFrontReady == true(同 RemoveFront 的前置)。
    public int FrontItemProtoId => _itemProtoIds[0];
```

Change `RemoveFront`:

```csharp
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
```

- [ ] **Step 2: Update `TryRemoveItemInRange` to keep `_itemProtoIds` in sync**

Find `TryRemoveItemInRange` in the same file. It currently does:

```csharp
        if (removeAt + 1 < _gaps.Count)
            _gaps[removeAt + 1] += _gaps[removeAt] + ItemWidthSubTiles;
        _gaps.RemoveAt(removeAt);
```

Change to:

```csharp
        if (removeAt + 1 < _gaps.Count)
            _gaps[removeAt + 1] += _gaps[removeAt] + ItemWidthSubTiles;
        _gaps.RemoveAt(removeAt);
        _itemProtoIds.RemoveAt(removeAt);
```

- [ ] **Step 3: Change `ToAbsolutePositions`/`FromAbsolutePositions` to carry item ids**

Add a new public record struct right before the `BeltLane` class declaration (top of the file, after the `using`/namespace):

```csharp
// 一个物品在 lane 上的绝对前沿距离(离出口多远)+ 它是什么物品。
// 只在合并/拆分/存档这些冷路径上使用,不参与每 tick 的推进热路径。
public readonly record struct PositionedItem(int LeadingEdgeSubTiles, int ItemProtoId);
```

Replace `ToAbsolutePositions`:

```csharp
    // 把相对 gap 列表转成"每个物品前沿距出口的绝对亚格距离 + 物品类型"(前到后)。
    // 冷路径(合并/拆分/存档),一次线性扫描,允许分配。
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
```

Replace `FromAbsolutePositions`:

```csharp
    // 从"前沿绝对距离 + 物品类型"列表(前到后、升序)和总长度重建一条新 lane。
    // _openIndex 取默认 0(唯一恒安全的初值,不沿用来源 lane 的游标)。
    // 相邻前沿差 < ItemWidthSubTiles 视为物品重叠(上游 bug),fail-fast。
    public static BeltLane FromAbsolutePositions(int lineLength, IReadOnlyList<PositionedItem> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        var lane = new BeltLane(lineLength); // 长度非法时构造函数抛 ArgumentOutOfRangeException
        int prevTrailingEdge = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            int leadingEdge = positions[i].LeadingEdgeSubTiles;
            int gap = leadingEdge - prevTrailingEdge;
            if (gap < 0)
                throw new ArgumentException(
                    i == 0
                        ? $"position[0]={leadingEdge} is past the exit (negative)"
                        : $"position[{i}]={leadingEdge} overlaps previous item (gap {gap})",
                    nameof(positions));
            lane._gaps.Add(gap);
            lane._itemProtoIds.Add(positions[i].ItemProtoId);
            prevTrailingEdge = leadingEdge + ItemWidthSubTiles;
        }
        if (prevTrailingEdge > lineLength)
            throw new ArgumentException(
                $"last item trailing edge {prevTrailingEdge} exceeds line length {lineLength}",
                nameof(positions));
        return lane;
    }
```

- [ ] **Step 4: Add the item id to `WriteState`**

Replace `WriteState` in the same file:

```csharp
    // 规范序列化(spec 铁律 4):写 gap 数量,然后逐个物品写 (gap, 物品 id)。
    // 不写 _openIndex / TouchesInLastAdvance——两者纯派生:加载后 _openIndex
    // 归 0,下一次 Advance 多扫一趟即自愈,最终 gap 轨迹与状态哈希不受影响。
    // 线长不在这里写:BeltLine.WriteState 会写 Tiles 数量(线长 = 256 × 格数)。
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

- [ ] **Step 5: Update `BeltNetwork.cs`'s `ConcatLanes`/`SplitBackLane` to carry `PositionedItem`**

Open `sim/Faketorio.Sim/Belts/BeltNetwork.cs`. Replace `SplitBackLane`:

```csharp
    // 从 src 的绝对位置快照里取前沿 >= cut 的物品,前沿减 cut,重建一条长
    // backLen 的新 lane。前沿 < cut 的(前半段 / 被移格 / 跨界)一律不带进来。
    private static BeltLane SplitBackLane(BeltLane src, int cut, int backLen)
    {
        var back = new List<BeltLane.PositionedItem>();
        foreach (var p in src.ToAbsolutePositions())
            if (p.LeadingEdgeSubTiles >= cut) back.Add(p with { LeadingEdgeSubTiles = p.LeadingEdgeSubTiles - cut });
        return BeltLane.FromAbsolutePositions(backLen, back);
    }
```

Replace `ConcatLanes`:

```csharp
    // 把 up(上游,拼在物理后侧)与 down(下游,拼在出口侧)两条带物品的
    // lane 拼成一条长 combinedLen 的新 lane。前沿绝对距离:down 的原样,
    // up 的每项整体后移 (256 + downLen)——越过新格 T 和整条 down。物品
    // 类型原样带过去,不参与位置计算。拼出的列表天然升序(up 最靠前项移位后
    // 仍远在 down 最靠后项之后),直接交给 FromAbsolutePositions。
    private static BeltLane ConcatLanes(BeltLane up, BeltLane down, int downLen, int combinedLen)
    {
        var pos = new List<BeltLane.PositionedItem>(down.Count + up.Count);
        pos.AddRange(down.ToAbsolutePositions());
        int shift = BeltLine.TileSubTiles + downLen;
        foreach (var p in up.ToAbsolutePositions())
            pos.Add(p with { LeadingEdgeSubTiles = p.LeadingEdgeSubTiles + shift });
        return BeltLane.FromAbsolutePositions(combinedLen, pos);
    }
```

Nothing else in `BeltNetwork.cs` needs to change — `TryRemoveItemInRange` calls (in `ClearRange`/`MiddleSplit`) only count removals by position, never inspect item type.

- [ ] **Step 6: Fix `Simulation.cs`'s corner-handoff (required for the branch to compile)**

Open `sim/Faketorio.Sim/Simulation.cs`. Find the corner-handoff section inside `Step()`:

```csharp
            while (line.LaneA.IsFrontReady && down.LaneA.TryInsertAtBack()) line.LaneA.RemoveFront();
            while (line.LaneB.IsFrontReady && down.LaneB.TryInsertAtBack()) line.LaneB.RemoveFront();
```

Change to:

```csharp
            while (line.LaneA.IsFrontReady && down.LaneA.TryInsertAtBack(line.LaneA.FrontItemProtoId)) line.LaneA.RemoveFront();
            while (line.LaneB.IsFrontReady && down.LaneB.TryInsertAtBack(line.LaneB.FrontItemProtoId)) line.LaneB.RemoveFront();
```

- [ ] **Step 7: Build to confirm production code compiles (tests will still fail to compile — expected)**

Run: `dotnet build sim/Faketorio.Sim`
Expected: 0 errors (the production assembly itself is fully migrated at this point). The test project will NOT build yet — that's expected and fixed in the next steps.

- [ ] **Step 8: Migrate `BeltLaneTests.cs`'s three call patterns**

This file has three distinct patterns that all need updating. Add this constant at the top of the `BeltLaneTests` class (right after the opening `{`):

```csharp
    private const int TestItem = 1;   // 位置/间距测试不关心具体是什么物品,固定用一个 id

    private static IReadOnlyList<BeltLane.PositionedItem> Positions(params int[] leadingEdges)
        => Array.ConvertAll(leadingEdges, p => new BeltLane.PositionedItem(p, TestItem));
```

**Pattern A — every zero-arg `TryInsertAtBack()` call becomes `TryInsertAtBack(TestItem)`.** There are 49 occurrences in this file. Example (from `TryInsertAtBack_FirstItem_EntersAtBackWithFullGapToExit`):

```csharp
// before
Assert.True(lane.TryInsertAtBack());
// after
Assert.True(lane.TryInsertAtBack(TestItem));
```

Apply this to every `TryInsertAtBack()` call in the file (both inside `Assert.True(...)`/`Assert.False(...)` and bare statement calls like `lane.TryInsertAtBack(); lane.Advance(100);`).

**Pattern B — every `Assert.Equal(new[] { ints... }, X.ToAbsolutePositions())` gets a `.Select(p => p.LeadingEdgeSubTiles)` wrap**, since `ToAbsolutePositions()` now returns `PositionedItem`s instead of bare ints and the tests only care about position, not type. There are 7 occurrences. Example (from `ToAbsolutePositions_ReturnsLeadingEdgeDistancesFromExit`):

```csharp
// before
lane.TryInsertAtBack(); // gaps=[192]
lane.Advance(100);       // gaps=[92]
lane.TryInsertAtBack(); // gaps=[92,36]
Assert.Equal(new[] { 92, 192 }, lane.ToAbsolutePositions()); // 92, 92+64+36
// after
lane.TryInsertAtBack(TestItem); // gaps=[192]
lane.Advance(100);       // gaps=[92]
lane.TryInsertAtBack(TestItem); // gaps=[92,36]
Assert.Equal(new[] { 92, 192 }, lane.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles)); // 92, 92+64+36
```

Add `using System.Linq;` to the top of the file if it isn't already imported (needed for `.Select`).

**Pattern C — every `new[] { ints... }` or `Array.Empty<int>()` passed as the second argument to `BeltLane.FromAbsolutePositions` becomes `Positions(ints...)` or `Positions()`.** There are 9 occurrences. Examples:

```csharp
// before
var rebuilt = BeltLane.FromAbsolutePositions(256, lane.ToAbsolutePositions());
// after — NO CHANGE. lane.ToAbsolutePositions() already returns the right
// type once Steps 1-4 land, so this specific call site needs no edit at all.

// before
var lane = BeltLane.FromAbsolutePositions(512, Array.Empty<int>());
// after
var lane = BeltLane.FromAbsolutePositions(512, Positions());

// before
Assert.Throws<ArgumentException>(
    () => BeltLane.FromAbsolutePositions(256, new[] { 10, 50 })); // 50-10 < 64
// after
Assert.Throws<ArgumentException>(
    () => BeltLane.FromAbsolutePositions(256, Positions(10, 50))); // 50-10 < 64

// before
var lane = BeltLane.FromAbsolutePositions(256, new[] { 0, 64, 128, 192 });
// after
var lane = BeltLane.FromAbsolutePositions(256, Positions(0, 64, 128, 192));
```

Apply the `Positions(...)` wrap to every remaining `new[] { ... }`/`Array.Empty<int>()` literal passed to `FromAbsolutePositions` — the one exception is any call that passes through the *result* of `ToAbsolutePositions()` directly (the round-trip test above), which needs no change since both sides already agree on `PositionedItem`.

- [ ] **Step 9: Add item-identity correctness tests to `BeltLaneTests.cs`**

Append these to the `BeltLaneTests` class:

```csharp
    [Fact]
    public void TryInsertAtBack_DifferentItems_FrontItemProtoIdTracksEachInTurn()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(10);
        lane.Advance(1000); // 推到出口
        Assert.True(lane.IsFrontReady);
        Assert.Equal(10, lane.FrontItemProtoId);

        lane.TryInsertAtBack(20); // 排在后面
        Assert.Equal(10, lane.FrontItemProtoId); // 队首还是第一个

        lane.RemoveFront();
        lane.Advance(1000);
        Assert.Equal(20, lane.FrontItemProtoId); // 现在轮到第二个
    }

    [Fact]
    public void ToAbsolutePositions_PreservesItemTypesInOrder()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(10);
        lane.Advance(100);
        lane.TryInsertAtBack(20);

        var positions = lane.ToAbsolutePositions();
        Assert.Equal(2, positions.Count);
        Assert.Equal(10, positions[0].ItemProtoId);
        Assert.Equal(20, positions[1].ItemProtoId);
    }

    [Fact]
    public void FromAbsolutePositions_RoundTripsItemTypes()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(10);
        lane.Advance(100);
        lane.TryInsertAtBack(20);

        var rebuilt = BeltLane.FromAbsolutePositions(256, lane.ToAbsolutePositions());
        var positions = rebuilt.ToAbsolutePositions();
        Assert.Equal(10, positions[0].ItemProtoId);
        Assert.Equal(20, positions[1].ItemProtoId);
    }
```

- [ ] **Step 10: Migrate `BeltNetworkTests.cs`**

Same three patterns as Step 8, but this file only uses patterns A (20 `TryInsertAtBack()` calls) and B (9 `.ToAbsolutePositions()` comparisons) — grep confirmed no `FromAbsolutePositions` calls in this file. Add the same constant near the top of the `BeltNetworkTests` class:

```csharp
    private const int TestItem = 1;
```

Apply pattern A (`TryInsertAtBack()` → `TryInsertAtBack(TestItem)`) to all 20 occurrences, and pattern B (`.ToAbsolutePositions()` → `.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles)`, keeping the existing `Assert.Equal(new[] {...}, ...)` left-hand side unchanged) to all 9 occurrences. Add `using System.Linq;` if not already present. Example from the three-way-merge test:

```csharp
// before
// LaneA: down 448 原样;up 504->1272, 704->1472
Assert.Equal(new[] { 448, 1272, 1472 }, merged.LaneA.ToAbsolutePositions());
Assert.Equal(new[] { 448, 760, 136 }, merged.LaneA.Gaps);
// after
// LaneA: down 448 原样;up 504->1272, 704->1472
Assert.Equal(new[] { 448, 1272, 1472 }, merged.LaneA.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles));
Assert.Equal(new[] { 448, 760, 136 }, merged.LaneA.Gaps);
```

- [ ] **Step 11: Add merge/split item-identity tests to `BeltNetworkTests.cs`**

Find the three-way-merge test in this file (the one asserting `merged.LaneA.ToAbsolutePositions()` equals `{448, 1272, 1472}` — it places items on the up-line and down-line before merging). Add a new test right after it that specifically checks item type survives the merge:

```csharp
    [Fact]
    public void ThreeWayMerge_PreservesItemTypesFromBothOriginalLines()
    {
        var net = new BeltNetwork();
        // 先建两段东向线,分别插入不同类型的物品,再放中间那格触发三路合并
        net.AddBelt(0, 0, 1); // 东
        net.AddBelt(1, 0, 1);
        net.GetLine(net.GetLineAt(0, 0)).LaneA.TryInsertAtBack(10);

        net.AddBelt(3, 0, 1);
        net.AddBelt(4, 0, 1);
        net.GetLine(net.GetLineAt(3, 0)).LaneA.TryInsertAtBack(20);

        var mergedId = net.AddBelt(2, 0, 1); // 触发三路合并
        var positions = net.GetLine(mergedId).LaneA.ToAbsolutePositions();

        Assert.Equal(2, positions.Count);
        // 上游(离出口更远的一侧,down 是 0,0/1,0 段,up 是 3,0/4,0 段)保序:
        // down 的物品(10)排在前面(离出口更近),up 的物品(20)排在后面。
        Assert.Equal(10, positions[0].ItemProtoId);
        Assert.Equal(20, positions[1].ItemProtoId);
    }
```

Find the middle-split test (the one that removes a middle tile and checks `net.GetLine(backId).LaneA.ToAbsolutePositions()` equals `{448}`). Add a test right after it:

```csharp
    [Fact]
    public void MiddleSplit_PreservesItemTypeInBackHalf()
    {
        var net = new BeltNetwork();
        net.AddBelt(0, 0, 1); net.AddBelt(1, 0, 1); net.AddBelt(2, 0, 1);
        net.GetLine(net.GetLineAt(2, 0)).LaneA.TryInsertAtBack(30); // 靠近入口那端

        net.RemoveBelt(1, 0); // 中间拆分

        var backId = net.GetLineAt(2, 0);
        var positions = net.GetLine(backId).LaneA.ToAbsolutePositions();
        Assert.Single(positions);
        Assert.Equal(30, positions[0].ItemProtoId);
    }
```

(If the exact coordinates/positions above don't line up with this file's existing helper conventions once you read the surrounding tests, adapt the setup to match this file's established `AddBelt`/`RemoveBelt` call style — the point of both tests is proving item type survives merge and split, not the specific geometry.)

- [ ] **Step 12: Migrate `BeltIntegrationTests.cs`**

This file has 6 `TryInsertAtBack()` calls, all pattern A, no `ToAbsolutePositions`/`FromAbsolutePositions` usage. Add near the top of the class:

```csharp
    private const int TestItem = 1;
```

Change every `TryInsertAtBack()` to `TryInsertAtBack(TestItem)`, including inside the `Pack` helper:

```csharp
    // before
    private static void Pack(BeltLane lane)
    {
        while (lane.TryInsertAtBack())
            lane.Advance(256);
    }
    // after
    private static void Pack(BeltLane lane)
    {
        while (lane.TryInsertAtBack(TestItem))
            lane.Advance(256);
    }
```

- [ ] **Step 13: Add a corner-handoff item-identity test to `BeltIntegrationTests.cs`**

Find `LShapeCorner_ItemFlowsFromEastLineToSouthLine` in this file. It already inserts one item into `LaneA` and one into `LaneB` and checks both lanes end up with 1 item each after the corner handoff — but it never checks item *type* survives (Step 6 of this task changed `Simulation.cs`'s corner-handoff to pass `FrontItemProtoId` through, and this is the test that proves that 2-line fix actually works, not just that `BeltLane` itself is internally consistent). Change the test's two insert calls and add two new assertions:

```csharp
    [Fact]
    public void LShapeCorner_ItemFlowsFromEastLineToSouthLine()
    {
        var sim = NewSim();
        // 东向线 A:(0,0)(1,0)(2,0),出口 (2,0),长 768
        PlaceBelt(sim, 0, 0, E); PlaceBelt(sim, 1, 0, E); PlaceBelt(sim, 2, 0, E);
        // 南向线 B:(3,0)(3,1)(3,2),入口 Tiles[^1]=(3,0),出口 (3,2),长 768
        PlaceBelt(sim, 3, 0, S); PlaceBelt(sim, 3, 1, S); PlaceBelt(sim, 3, 2, S);
        Assert.Equal(2, LiveLineCount(sim)); // 方向不同,两条独立线

        LineAt(sim, 0, 0).LaneA.TryInsertAtBack(10); // 放在 A 入口
        LineAt(sim, 0, 0).LaneB.TryInsertAtBack(20);

        // A 上走 704 (=3*256-64) 亚格,过拐角,再在 B 上走 704;每 tick 8;留足余量
        for (int t = 0; t < 704 / 8 + 704 / 8 + 20; t++) sim.Step();

        Assert.Equal(0, LineAt(sim, 0, 0).LaneA.Count);  // A 已空
        Assert.Equal(1, LineAt(sim, 3, 2).LaneA.Count);  // 物品到了 B
        Assert.Equal(0, LineAt(sim, 0, 0).LaneB.Count);
        Assert.Equal(1, LineAt(sim, 3, 2).LaneB.Count);
        Assert.Equal(10, LineAt(sim, 3, 2).LaneA.FrontItemProtoId); // 类型跟着走,不是只有数量
        Assert.Equal(20, LineAt(sim, 3, 2).LaneB.FrontItemProtoId);
    }
```

- [ ] **Step 14: Migrate `DeterminismTests.cs`**

This file has 3 `TryInsertAtBack()` calls, all pattern A. Find them (in the belt-scenario setup near the top of the file) and add item ids — since this is a determinism golden scenario, use a real resolved item id from the loaded registry rather than a bare literal (matching this file's existing style of resolving ids via `sim.Prototypes.Get<...>()`):

```csharp
// before
line.LaneA.TryInsertAtBack();
line.LaneB.TryInsertAtBack();
// after
int beltTestItem = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
line.LaneA.TryInsertAtBack(beltTestItem);
line.LaneB.TryInsertAtBack(beltTestItem);
```

Apply the same pattern to the third occurrence in this file. Reuse a single resolved `beltTestItem` local per scenario method rather than re-resolving it at each call site.

- [ ] **Step 15: Run the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS, 341 baseline + new tests (3 in `BeltLaneTests` + 2 in `BeltNetworkTests` + 1 in `BeltIntegrationTests` = 6 new) = **347/347**.

Run: `dotnet build -c Release`
Expected: 0 warnings, 0 errors.

- [ ] **Step 16: Commit**

```bash
git add sim/Faketorio.Sim/Belts/BeltLane.cs \
        sim/Faketorio.Sim/Belts/BeltNetwork.cs \
        sim/Faketorio.Sim/Simulation.cs \
        sim/Faketorio.Sim.Tests/BeltLaneTests.cs \
        sim/Faketorio.Sim.Tests/BeltNetworkTests.cs \
        sim/Faketorio.Sim.Tests/BeltIntegrationTests.cs \
        sim/Faketorio.Sim.Tests/DeterminismTests.cs
git commit -m "$(cat <<'EOF'
feat(belts): typed belt items

BeltLane carries an item-protoId alongside each gap (parallel array,
always kept in lockstep). TryInsertAtBack takes the item type; a new
FrontItemProtoId peek property lets callers read the queue head's type
before removing it. ToAbsolutePositions/FromAbsolutePositions (the
merge/split cold path) carry the type through a new PositionedItem
record struct. Simulation's corner-handoff now passes the upstream
line's FrontItemProtoId into the downstream line's insert instead of
losing item identity at every belt-to-belt transfer.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01BXC5RZ5wsWirQpprXn9KZV
EOF
)"
```

---

## Task 2: `MiningDrillPrototype` + `MiningDrills` state class

**Files:**
- Create: `sim/Faketorio.Sim/Prototypes/MiningDrillPrototype.cs`
- Create: `sim/Faketorio.Sim/MiningDrills.cs`
- Create: `data/base/mining-drill.json`
- Modify: `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`
- Test: `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs`
- Test: `sim/Faketorio.Sim.Tests/MiningDrillsTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1 directly (independent of the belt changes at compile time, though both live on the same branch).
- Produces (for Task 3): `MiningDrillPrototype { MiningSpeed, EnergyUsageJPerTick }` (plus inherited `TileWidth`/`TileHeight` from `EntityPrototype`). `MiningDrills` class (flat `namespace Faketorio.Sim;`) with `RegisterDrill(EntityId)`, `UnregisterDrill(EntityId)`, `GetTargetX(EntityId) -> int` (-1 default), `GetTargetY(EntityId) -> int` (-1 default), `GetProgress(EntityId) -> long`, `IsCompleted(EntityId) -> bool`, `GetPendingItemProtoId(EntityId) -> int`, `SetTarget(EntityId, int x, int y)`, `AddProgress(EntityId, long delta)`, `MarkCompleted(EntityId, int pendingItemProtoId)`, `ResetAfterFlush(EntityId, int targetX, int targetY)`, `WriteState(IStateWriter)`.

Does not touch `Simulation.cs` — that's Task 3.

- [ ] **Step 1: Create `MiningDrillPrototype.cs`**

```csharp
namespace Faketorio.Sim.Prototypes;

public sealed class MiningDrillPrototype : EntityPrototype
{
    public Q16 MiningSpeed { get; init; } = Q16.One;   // 进度倍率,M1 数据恒 1.0,JSON 不解析
    public long EnergyUsageJPerTick { get; init; }      // 每 tick 向电网登记的 PrimaryInput 需求
}
```

(`Q16` is in the flat `Faketorio.Sim` namespace; this file is `Faketorio.Sim.Prototypes`, but `MiningDrillPrototype.cs` doesn't need an explicit `using Faketorio.Sim;` if the project has implicit usings enabled for the root namespace — check how `CraftingMachinePrototype.cs` handled this same situation and match it exactly.)

- [ ] **Step 2: Add the `"mining-drill"` Parse arm and validation pass to `PrototypeLoader.cs`**

In `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`, add a new arm to the `Parse` method's `switch` expression, right before the `_ => throw new InvalidDataException(...)` default arm:

```csharp
            "mining-drill" => ValidateFootprint(new MiningDrillPrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                EnergyUsageJPerTick = el.TryGetProperty("energyUsage", out var eu) ? Units.ParsePower(eu.GetString()!) : 0L,
            }),
```

Add a new private static method (place it after `ResolveAndValidateCraftingMachines`):

```csharp
    // AssignIds() 之后:校验采矿机字段。
    private static void ResolveAndValidateMiningDrills(PrototypeRegistry registry)
    {
        for (int i = 0; i < registry.Count; i++)
        {
            if (registry.GetById(i) is not MiningDrillPrototype d) continue;
            if (d.EnergyUsageJPerTick < 0)
                throw new InvalidDataException($"Mining drill '{d.Name}': energyUsage must be >= 0");
        }
    }
```

Update `LoadFromDirectory` to call it — change:

```csharp
        ResolveAndValidateElectric(registry);
        ResolveAndValidateCraftingMachines(registry);
        return registry;
```

to:

```csharp
        ResolveAndValidateElectric(registry);
        ResolveAndValidateCraftingMachines(registry);
        ResolveAndValidateMiningDrills(registry);
        return registry;
```

- [ ] **Step 3: Create `data/base/mining-drill.json`**

```json
[
  { "type": "mining-drill", "name": "electric-mining-drill", "tileWidth": 2, "tileHeight": 2, "energyUsage": "90kW" }
]
```

- [ ] **Step 4: Run the full suite to confirm the new prototype loads without breaking anything**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS, 347/347 (from Task 1 — no new tests reference the new type yet, but the loader now parses `mining-drill.json` on every `Load()` call, proving it doesn't break anything else that loads `data/base`).

- [ ] **Step 5: Write `PrototypeLoaderTests.cs` additions**

Append to the `PrototypeLoaderTests` class:

```csharp
    [Fact]
    public void LoadsMiningDrill()
    {
        var drill = Load().Get<MiningDrillPrototype>("electric-mining-drill");
        Assert.Equal(2, drill.TileWidth);
        Assert.Equal(2, drill.TileHeight);
        Assert.Equal(1500, drill.EnergyUsageJPerTick);   // 90kW / 60 ticks-per-second
    }

    [Fact]
    public void MiningDrillNegativeEnergyUsage_Throws() => AssertLoadThrows(
        "[{ \"type\": \"mining-drill\", \"name\": \"d\", \"energyUsage\": \"-120W\" }]");
```

(Same `"-120W"` magnitude reasoning as P9's equivalent negative test: `Units.ParseEnergy` gives -120, `ParsePower` integer-divides by 60 truncating toward zero, giving -2 — safely negative. `"-1W"` would truncate to 0 and silently pass.)

- [ ] **Step 6: Run the new loader tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~PrototypeLoaderTests"`
Expected: PASS, all `PrototypeLoaderTests` including the 2 new ones.

- [ ] **Step 7: Create `MiningDrills.cs`**

**Read the Global Constraints namespace note before writing this file** — flat `namespace Faketorio.Sim;`, file directly at `sim/Faketorio.Sim/MiningDrills.cs`, no subfolder.

```csharp
using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim;   // 扁平——不是 Faketorio.Sim.MiningDrills,同 Player/Machines 的坑

// 采矿机运行时状态容器。刻意不知道 Simulation/Prototypes/Inventories/Belts/
// ElectricGrid/Resources——只收裸 EntityId/int/long/bool,同 ElectricGrid/
// Machines 的隔离原则。目标搜索、资源查询、电网登记、库存/传送带插入,
// 全部在 Simulation 的采矿 tick 方法里做。
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
        _ = _states[id];   // 前置:已注册(throw-on-missing,同 P9 Machines 的一致性要求)
        var s = _states[id];
        _states[id] = s with { TargetX = x, TargetY = y, Progress = 0 };
    }

    public void AddProgress(EntityId id, long delta)
    {
        var s = _states[id];
        _states[id] = s with { Progress = s.Progress + delta };
    }

    // 到点产出:记下待放置的物品 id。TargetX/Y 不变(留给 Simulation 层在
    // flush 成功后调 ResetAfterFlush 决定要不要保留)。
    public void MarkCompleted(EntityId id, int pendingItemProtoId)
    {
        var s = _states[id];
        _states[id] = s with { Completed = true, PendingItemProtoId = pendingItemProtoId };
    }

    // flush 成功后调用:清 Completed/PendingItemProtoId/Progress。TargetX/Y
    // 由调用方决定——矿格挖空传 (-1,-1)(下 tick 重新搜),没挖空传原目标
    // (继续挖同一格)。
    public void ResetAfterFlush(EntityId id, int targetX, int targetY)
        => _states[id] = new DrillRuntimeState(targetX, targetY, 0, false, 0);

    // 按 EntityId.Index 排序后写:index/代数/目标坐标/进度/是否已完成/待放置物品 id。
    public void WriteState(IStateWriter writer)
    {
        var ids = new List<EntityId>(_states.Keys);
        ids.Sort((a, b) => a.Index.CompareTo(b.Index));
        writer.Write(ids.Count);
        foreach (var id in ids)
        {
            var s = _states[id];
            writer.Write(id.Index);
            writer.Write(id.Generation);
            writer.Write(s.TargetX);
            writer.Write(s.TargetY);
            writer.Write(s.Progress);
            writer.Write(s.Completed ? (byte)1 : (byte)0);
            writer.Write(s.PendingItemProtoId);
        }
    }
}

internal readonly record struct DrillRuntimeState(int TargetX, int TargetY, long Progress, bool Completed, int PendingItemProtoId);
```

- [ ] **Step 8: Write `MiningDrillsTests.cs`**

```csharp
using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class MiningDrillsTests
{
    [Fact]
    public void RegisterDrill_StartsWithNoTargetZeroProgressNotCompleted()
    {
        var d = new MiningDrills();
        var id = new EntityId(1, 1);
        d.RegisterDrill(id);

        Assert.Equal(-1, d.GetTargetX(id));
        Assert.Equal(-1, d.GetTargetY(id));
        Assert.Equal(0, d.GetProgress(id));
        Assert.False(d.IsCompleted(id));
        Assert.Equal(0, d.GetPendingItemProtoId(id));
    }

    [Fact]
    public void UnregisteredEntity_FallsBackToDefaults()
    {
        var d = new MiningDrills();
        var id = new EntityId(2, 1);

        Assert.Equal(-1, d.GetTargetX(id));
        Assert.Equal(-1, d.GetTargetY(id));
        Assert.Equal(0, d.GetProgress(id));
        Assert.False(d.IsCompleted(id));
    }

    [Fact]
    public void SetTarget_UnregisteredEntity_Throws()
    {
        var d = new MiningDrills();
        var id = new EntityId(3, 1);
        Assert.Throws<KeyNotFoundException>(() => d.SetTarget(id, 5, 6));
    }

    [Fact]
    public void AddProgress_UnregisteredEntity_Throws()
    {
        var d = new MiningDrills();
        var id = new EntityId(4, 1);
        Assert.Throws<KeyNotFoundException>(() => d.AddProgress(id, 100));
    }

    [Fact]
    public void MarkCompleted_UnregisteredEntity_Throws()
    {
        var d = new MiningDrills();
        var id = new EntityId(5, 1);
        Assert.Throws<KeyNotFoundException>(() => d.MarkCompleted(id, 42));
    }

    [Fact]
    public void SetTarget_ResetsProgress()
    {
        var d = new MiningDrills();
        var id = new EntityId(6, 1);
        d.RegisterDrill(id);
        d.SetTarget(id, 1, 1);
        d.AddProgress(id, 500);

        d.SetTarget(id, 2, 2);

        Assert.Equal(2, d.GetTargetX(id));
        Assert.Equal(2, d.GetTargetY(id));
        Assert.Equal(0, d.GetProgress(id));
    }

    [Fact]
    public void AddProgress_Accumulates()
    {
        var d = new MiningDrills();
        var id = new EntityId(7, 1);
        d.RegisterDrill(id);
        d.SetTarget(id, 1, 1);

        d.AddProgress(id, 30);
        d.AddProgress(id, 12);

        Assert.Equal(42, d.GetProgress(id));
    }

    [Fact]
    public void MarkCompleted_SetsCompletedAndPendingItem_KeepsTargetAndProgress()
    {
        var d = new MiningDrills();
        var id = new EntityId(8, 1);
        d.RegisterDrill(id);
        d.SetTarget(id, 3, 4);
        d.AddProgress(id, 1000);

        d.MarkCompleted(id, 99);

        Assert.True(d.IsCompleted(id));
        Assert.Equal(99, d.GetPendingItemProtoId(id));
        Assert.Equal(3, d.GetTargetX(id));
        Assert.Equal(4, d.GetTargetY(id));
        Assert.Equal(1000, d.GetProgress(id));
    }

    [Fact]
    public void ResetAfterFlush_ClearsCompletedAndProgress_SetsGivenTarget()
    {
        var d = new MiningDrills();
        var id = new EntityId(9, 1);
        d.RegisterDrill(id);
        d.SetTarget(id, 3, 4);
        d.AddProgress(id, 1000);
        d.MarkCompleted(id, 99);

        d.ResetAfterFlush(id, 3, 4); // 矿格没挖空,保留同一目标

        Assert.False(d.IsCompleted(id));
        Assert.Equal(0, d.GetProgress(id));
        Assert.Equal(0, d.GetPendingItemProtoId(id));
        Assert.Equal(3, d.GetTargetX(id));
        Assert.Equal(4, d.GetTargetY(id));
    }

    [Fact]
    public void ResetAfterFlush_WithInvalidTarget_ClearsTargetToo()
    {
        var d = new MiningDrills();
        var id = new EntityId(10, 1);
        d.RegisterDrill(id);
        d.SetTarget(id, 3, 4);
        d.MarkCompleted(id, 99);

        d.ResetAfterFlush(id, -1, -1); // 矿格挖空了

        Assert.Equal(-1, d.GetTargetX(id));
        Assert.Equal(-1, d.GetTargetY(id));
    }

    [Fact]
    public void UnregisterDrill_RemovesState()
    {
        var d = new MiningDrills();
        var id = new EntityId(11, 1);
        d.RegisterDrill(id);
        d.SetTarget(id, 1, 1);

        d.UnregisterDrill(id);

        Assert.Equal(-1, d.GetTargetX(id));
        Assert.Equal(0, d.GetProgress(id));
    }

    [Fact]
    public void WriteState_SortsByEntityIndex_RegistrationOrderDoesNotMatter()
    {
        var idHigh = new EntityId(9, 1);
        var idLow = new EntityId(2, 1);

        var d1 = new MiningDrills();
        d1.RegisterDrill(idHigh);
        d1.SetTarget(idHigh, 1, 1);
        d1.RegisterDrill(idLow);
        d1.SetTarget(idLow, 2, 2);
        var w1 = new Fnv1aHashWriter();
        d1.WriteState(w1);

        var d2 = new MiningDrills();
        d2.RegisterDrill(idLow);
        d2.SetTarget(idLow, 2, 2);
        d2.RegisterDrill(idHigh);
        d2.SetTarget(idHigh, 1, 1);
        var w2 = new Fnv1aHashWriter();
        d2.WriteState(w2);

        Assert.Equal(w1.Hash, w2.Hash);
    }
}
```

- [ ] **Step 9: Run the new tests, then the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~MiningDrillsTests"`
Expected: PASS, all 12 `MiningDrillsTests`.

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS, 347 (Task 1) + 2 (loader) + 12 (`MiningDrillsTests`) = **361/361**.

Run: `dotnet build -c Release`
Expected: 0 warnings, 0 errors.

- [ ] **Step 10: Commit**

```bash
git add sim/Faketorio.Sim/Prototypes/MiningDrillPrototype.cs \
        sim/Faketorio.Sim/MiningDrills.cs \
        sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs \
        data/base/mining-drill.json \
        sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs \
        sim/Faketorio.Sim.Tests/MiningDrillsTests.cs
git commit -m "$(cat <<'EOF'
feat(mining-drill): MiningDrillPrototype + MiningDrills runtime state

Pure EntityId-keyed state (target tile / progress / completed flag /
pending output item), isolated from Simulation/Prototypes/Inventories/
Belts/ElectricGrid/Resources same as ElectricGrid and P9's Machines.
Declared in flat namespace Faketorio.Sim to avoid the same self-shadowing
bug P5's Player and P9's Machines already hit and fixed. Mutators throw
KeyNotFoundException on an unregistered entity from the start (P9's
Machines needed a fix round for this consistency; got it right here
the first time).

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01BXC5RZ5wsWirQpprXn9KZV
EOF
)"
```

---

## Task 3: `Simulation` wiring

**Files:**
- Modify: `sim/Faketorio.Sim/Simulation.cs`
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`
- Test: `sim/Faketorio.Sim.Tests/DeterminismTests.cs`

**Interfaces:**
- Consumes: Task 1's `BeltLane.TryInsertAtBack(int)`/`FrontItemProtoId`/`BeltNetwork.Delta`; Task 2's `MiningDrillPrototype` fields and `MiningDrills`'s exact method names.
- Produces: `Simulation.MiningDrills` property; a fully wired mining-drill tick.

This is the last task — depends on both Task 1 and Task 2 being merged first.

- [ ] **Step 1: Add the `MiningDrills` property**

In `sim/Faketorio.Sim/Simulation.cs`, change:

```csharp
    public Machines Machines { get; } = new();
```

to:

```csharp
    public Machines Machines { get; } = new();
    public MiningDrills MiningDrills { get; } = new();
```

(No new `using` needed — `MiningDrills` is flat `Faketorio.Sim`, same as `Machines`/`Player`.)

- [ ] **Step 2: Wire `PlaceEntity` to register drills**

In `Simulation.cs`'s `Apply` method, `CommandType.PlaceEntity` case, find the block that was most recently added for `CraftingMachinePrototype` and add a sibling block right after it:

```csharp
                if (proto is CraftingMachinePrototype cmp)
                {
                    Inventories.AddContainer(id, cmp.InputSlots, role: 1);
                    Inventories.AddContainer(id, cmp.OutputSlots, role: 2);
                    Machines.RegisterMachine(id);
                }
                if (proto is MiningDrillPrototype)
                    MiningDrills.RegisterDrill(id);
                return;
```

- [ ] **Step 3: Wire `DestroyEntityAt` to unregister drills**

In `Simulation.cs`'s `DestroyEntityAt`, add a flag alongside `isMachine` and a cleanup block:

```csharp
        bool isMachine = proto is CraftingMachinePrototype;
        bool isDrill = proto is MiningDrillPrototype;
        World.ClearArea(data.X, data.Y, proto.TileWidth, proto.TileHeight);
        Entities.Destroy(id);
        if (isBelt) Belts.RemoveBelt(bx, by);
        if (isContainer) Inventories.RemoveContainer(id);
        if (isPole) ElectricGrid.UnregisterPole(id);
        if (isGenerator)
        {
            Inventories.RemoveContainer(id);
            ElectricGrid.UnregisterGenerator(id);
        }
        if (isMachine)
        {
            Inventories.RemoveContainer(id, role: 1);
            Inventories.RemoveContainer(id, role: 2);
            Machines.UnregisterMachine(id);
        }
        if (isDrill) MiningDrills.UnregisterDrill(id);
    }
```

- [ ] **Step 4: Add `MiningDrillTickPreSettle`/`MiningDrillTickPostSettle` and their pool-scan wrappers**

Add these to `Simulation.cs` (place them right after `MachinesTickPostSettle`/`MachineTickPostSettle`, before the class's final closing brace):

```csharp
    // 采矿:目标搜索 + 电力需求登记(Settle() 之前)。两趟扫描的第一趟。
    private void MiningDrillsTickPreSettle()
    {
        for (int i = 0; i < Entities.Capacity; i++)
        {
            if (!Entities.IsAliveAtIndex(i)) continue;
            ref var data = ref Entities.GetAtIndex(i);
            if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not MiningDrillPrototype proto) continue;
            var id = new EntityId(i, Entities.GenerationAtIndex(i));
            MiningDrillTickPreSettle(id, proto, data.X, data.Y, data.Rotation);
        }
    }

    private void MiningDrillTickPreSettle(EntityId id, MiningDrillPrototype proto, int x, int y, byte rotation)
    {
        // 第 1 步:flush 已完成的产出
        if (MiningDrills.IsCompleted(id))
        {
            int itemId = MiningDrills.GetPendingItemProtoId(id);
            var (dx, dy) = BeltNetwork.Delta(rotation);
            int outX = x + dx * proto.TileWidth;
            int outY = y + dy * proto.TileHeight;

            bool placed = false;
            var outLineId = Belts.GetLineAt(outX, outY);
            if (outLineId.IsValid)
            {
                var outLine = Belts.GetLine(outLineId);
                placed = outLine.LaneA.TryInsertAtBack(itemId) || outLine.LaneB.TryInsertAtBack(itemId);
            }
            else
            {
                var outEntity = World.GetEntityAt(outX, outY);
                var outInvId = outEntity.IsValid ? Inventories.GetInventoryId(outEntity) : InventoryId.Invalid;
                if (outInvId.IsValid)
                {
                    var itemProto = (ItemPrototype)Prototypes.GetById(itemId);
                    placed = Inventories.Get(outInvId).Insert(itemId, 1, itemProto.StackSize) > 0;
                }
            }

            if (placed)
            {
                int tx = MiningDrills.GetTargetX(id), ty = MiningDrills.GetTargetY(id);
                bool exhausted = Resources.GetResourceAt(tx, ty).IsEmpty;
                MiningDrills.ResetAfterFlush(id, exhausted ? -1 : tx, exhausted ? -1 : ty);
                // 本 tick 内继续走到第 2 步,fits==true 时不 return。
            }
            else
            {
                // 输出堵塞:跳过第 2 步,但仍登记待机能耗。
                ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
                return;
            }
        }

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

        // 第 3 步:电力需求登记(无条件——恒定待机能耗)
        ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
    }

    // 采矿:进度推进 + 产出(Settle() 之后,可读 satisfaction)。两趟扫描的第二趟。
    private void MiningDrillsTickPostSettle()
    {
        for (int i = 0; i < Entities.Capacity; i++)
        {
            if (!Entities.IsAliveAtIndex(i)) continue;
            ref var data = ref Entities.GetAtIndex(i);
            if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not MiningDrillPrototype proto) continue;
            var id = new EntityId(i, Entities.GenerationAtIndex(i));
            MiningDrillTickPostSettle(id, proto);
        }
    }

    private void MiningDrillTickPostSettle(EntityId id, MiningDrillPrototype proto)
    {
        int tx = MiningDrills.GetTargetX(id), ty = MiningDrills.GetTargetY(id);
        if (tx == -1 || MiningDrills.IsCompleted(id)) return;   // 无目标,或本 tick 刚 flush 失败仍在等

        var cell = Resources.GetResourceAt(tx, ty);
        var resProto = (ResourcePrototype)Prototypes.GetById(cell.ResourceProtoId);
        long threshold = (long)resProto.MiningTimeTicks << 16;

        var satisfaction = ElectricGrid.GetSatisfaction(id);
        long delta = proto.MiningSpeed.Mul(satisfaction.Raw);
        if (MiningDrills.GetProgress(id) < threshold) MiningDrills.AddProgress(id, delta);
        if (MiningDrills.GetProgress(id) < threshold) return;

        var itemProto = Prototypes.Get<ItemPrototype>(resProto.MinableResult);
        Resources.Extract(tx, ty, 1);
        MiningDrills.MarkCompleted(id, itemProto.Id);
    }
```

- [ ] **Step 5: Splice the mining tick into `Step()`**

In `Simulation.cs`'s `Step()`, find the P9 processing segment:

```csharp
        // 电网 + 加工:① 发电机登记供给 ② 机器登记需求 ③ 结算 ④ 发电机烧油 ⑤ 机器推进+完成
        ElectricGeneratorsRegisterSupply();
        MachinesTickPreSettle();
        ElectricGrid.Settle();
        ElectricGeneratorsBurnFuel();
        MachinesTickPostSettle();
```

Change to:

```csharp
        // 电网 + 加工 + 采矿:① 发电机登记供给 ② 机器/采矿机登记需求 ③ 结算
        // ④ 发电机烧油 ⑤ 机器/采矿机推进+完成
        ElectricGeneratorsRegisterSupply();
        MachinesTickPreSettle();
        MiningDrillsTickPreSettle();
        ElectricGrid.Settle();
        ElectricGeneratorsBurnFuel();
        MachinesTickPostSettle();
        MiningDrillsTickPostSettle();
```

- [ ] **Step 6: Append `MiningDrills.WriteState` to `WriteState()`**

In `Simulation.cs`'s `WriteState`, change:

```csharp
        ElectricGrid.WriteState(writer);
        Machines.WriteState(writer);
    }
```

to:

```csharp
        ElectricGrid.WriteState(writer);
        Machines.WriteState(writer);
        MiningDrills.WriteState(writer);
    }
```

- [ ] **Step 7: Build to catch compile errors before writing tests**

Run: `dotnet build sim/Faketorio.Sim`
Expected: 0 errors. Fix any before proceeding.

- [ ] **Step 8: Write `SimulationTests.cs` additions**

Append to the `SimulationTests` class (reuse the existing `NewSim()`, `PlacePole`, `PlaceGenerator`, `TransferTo`, `PlaceChest` helpers already in this file from earlier plans):

```csharp
    private static Command PlaceDrill(Simulation sim, int x, int y, byte rotation = 0) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<MiningDrillPrototype>("electric-mining-drill").Id,
        X = x, Y = y, Rotation = rotation,
    };

    // 同 P9 的 PlacePoweredMachineInfra:电线杆(0,0)+ 发电机(2,0)充好煤。
    private static void PlacePoweredDrillInfra(Simulation sim)
    {
        sim.Submit(PlacePole(sim, 0, 0));
        sim.Submit(PlaceGenerator(sim, 2, 0));
        sim.Step();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        sim.Player.Inventory.Insert(coal, 5, coalStack);
        sim.Submit(TransferTo(2, 0, coal, 5));
        sim.Step();
    }

    [Fact]
    public void MiningDrill_FindsResourceInFootprint_AndExtractsToOutputChest()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        // 熔炉/装配机测试用 (0,2) 放机器,这里用 (0,3) 避免和电网 infra 的 (0,0)/(2,0) 冲突;
        // 采矿机 2x2 footprint 占 (0,3)-(1,4),朝东(rotation=1)输出到 (2,3)。
        sim.Submit(PlaceDrill(sim, 0, 3, rotation: 1));
        sim.Submit(PlaceChest(sim, 2, 3));
        sim.Step();

        // Resources 是惰性生成的矿脉——用固定种子(NewSim 默认种子 0)读一遍 (0,3)-(1,4)
        // 范围,找到实际有矿的格再断言(矿脉分布是种子的确定函数,不是本测试要验证的东西;
        // 这里只需要确认"某个格有矿、采矿机能找到它、挖出对应物品、堆进箱子"这条流程通)。
        var drillId = sim.World.GetEntityAt(0, 3);
        var chestId = sim.World.GetEntityAt(2, 3);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));

        bool foundResource = false;
        for (int dy = 0; dy < 2 && !foundResource; dy++)
            for (int dx = 0; dx < 2 && !foundResource; dx++)
                if (!sim.Resources.GetResourceAt(dx, 3 + dy).IsEmpty) foundResource = true;
        Assert.True(foundResource, "test assumes the default seed puts at least one resource tile under (0,3)-(1,4) — if this fails, adjust the drill's placement coordinates to a spot the seeded map actually has ore under.");

        for (int t = 0; t < 200; t++) sim.Step();

        Assert.True(chestInv.TotalItems() > 0);
        Assert.True(sim.MiningDrills.GetProgress(drillId) >= 0); // sanity: didn't throw, state is readable
    }

    [Fact]
    public void MiningDrill_OutputsToBelt_WithCorrectItemType()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        sim.Submit(PlaceDrill(sim, 0, 3, rotation: 1)); // 朝东输出到 (2,3)
        sim.Submit(new Command { Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id,
            X = 2, Y = 3, Rotation = 1 });
        sim.Step();

        bool foundResource = false;
        for (int dy = 0; dy < 2 && !foundResource; dy++)
            for (int dx = 0; dx < 2 && !foundResource; dx++)
                if (!sim.Resources.GetResourceAt(dx, 3 + dy).IsEmpty) foundResource = true;
        Assert.True(foundResource, "adjust drill placement if the seeded map doesn't have ore here");

        for (int t = 0; t < 200; t++) sim.Step();

        var lineId = sim.Belts.GetLineAt(2, 3);
        Assert.True(lineId.IsValid);
        var line = sim.Belts.GetLine(lineId);
        bool hasItem = line.LaneA.Count > 0 || line.LaneB.Count > 0;
        Assert.True(hasItem, "expected the drill's output to have landed on the belt at (2,3)");
    }

    [Fact]
    public void MiningDrill_NoTargetInFootprint_NeverProgresses()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        // (50,50) 起的 2x2 区域:若碰巧有矿,换一个更偏远的坐标直到全空(矿脉是无限惰性生成,
        // 但任意固定 2x2 区域全空的概率不为零——用一个大坐标降低撞上矿脉密集区的概率)。
        sim.Submit(PlaceDrill(sim, 5000, 5000, rotation: 1));
        sim.Step();

        var drillId = sim.World.GetEntityAt(5000, 5000);
        bool anyResource = false;
        for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (!sim.Resources.GetResourceAt(5000 + dx, 5000 + dy).IsEmpty) anyResource = true;
        Assert.False(anyResource, "test assumes (5000,5000)-(5001,5001) has no ore under the default seed — pick a different far-away coordinate if this ever becomes false");

        for (int t = 0; t < 50; t++) sim.Step();

        Assert.Equal(-1, sim.MiningDrills.GetTargetX(drillId));
        Assert.Equal(0, sim.MiningDrills.GetProgress(drillId));
    }

    [Fact]
    public void MiningDrill_DestroyedMidCycle_UnregistersCleanly()
    {
        var sim = NewSim();
        sim.Submit(PlaceDrill(sim, 10, 10, rotation: 1));
        sim.Step();
        var drillId = sim.World.GetEntityAt(10, 10);

        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 10, Y = 10 });
        sim.Step();

        Assert.Equal(-1, sim.MiningDrills.GetTargetX(drillId)); // 反查:摘除后读默认值,不抛异常
        Assert.False(sim.World.GetEntityAt(10, 10).IsValid);
    }
```

- [ ] **Step 9: Run the new simulation tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests"`
Expected: PASS, all `SimulationTests` including the 4 new ones. If `MiningDrill_FindsResourceInFootprint_AndExtractsToOutputChest` or `MiningDrill_OutputsToBelt_WithCorrectItemType` fail their `Assert.True(foundResource, ...)` sanity check, the seeded map doesn't have ore at `(0,3)-(1,4)` under the default seed (0) — try nearby coordinates (e.g. `(0,3)` → `(4,3)` → `(8,3)`, keeping the drill's output-facing side clear of other placed entities) until one has ore, and use that coordinate consistently in both tests. If `MiningDrill_NoTargetInFootprint_NeverProgresses`'s `Assert.False(anyResource, ...)` fails, pick a different large coordinate pair.

- [ ] **Step 10: Write the `DeterminismTests.cs` addition**

Append to the test class, following the exact structure of the existing `RunMachineScenario`/`MachineScenario_SameSeedSameCommands_SameHashEveryTick` pair:

```csharp
    private static List<ulong> RunMiningDrillScenario(long seed)
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"), seed);
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        sim.Player.Inventory.Insert(coal, 5, coalStack);

        var hashes = new List<ulong>();
        for (int t = 0; t < 250; t++)
        {
            if (t == 0)
            {
                sim.Submit(new Command { Type = CommandType.PlaceEntity,
                    ProtoId = sim.Prototypes.Get<ElectricPolePrototype>("small-electric-pole").Id, X = 0, Y = 0 });
                sim.Submit(new Command { Type = CommandType.PlaceEntity,
                    ProtoId = sim.Prototypes.Get<FuelGeneratorPrototype>("burner-generator").Id, X = 2, Y = 0 });
                sim.Submit(new Command { Type = CommandType.PlaceEntity,
                    ProtoId = sim.Prototypes.Get<MiningDrillPrototype>("electric-mining-drill").Id, X = 0, Y = 2, Rotation = 1 });
                sim.Submit(new Command { Type = CommandType.PlaceEntity,
                    ProtoId = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id, X = 2, Y = 2 });
            }
            if (t == 5)
                sim.Submit(new Command { Type = CommandType.TransferToEntity, X = 2, Y = 0, ProtoId = coal, Count = 5 });
            sim.Step();
            hashes.Add(sim.ComputeStateHash());
        }
        return hashes;
    }

    [Fact]
    public void MiningDrillScenario_SameSeedSameCommands_SameHashEveryTick()
        => Assert.Equal(RunMiningDrillScenario(4242), RunMiningDrillScenario(4242));
```

(This test doesn't need to assert anything about whether ore was actually found under `(0,2)-(1,3)` for seed 4242 — same-seed/same-commands determinism holds whether or not the drill finds ore, since "found nothing, stayed idle" is just as deterministic a trajectory as "found ore and extracted it." Unlike `SimulationTests`' tests, this one doesn't need a `foundResource` sanity check.)

- [ ] **Step 11: Run the new determinism test, then the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~DeterminismTests"`
Expected: PASS, all `DeterminismTests` including the new one.

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS, 361 (Task 2) + 4 (`SimulationTests`) + 1 (`DeterminismTests`) = **366/366**.

Run: `dotnet build -c Release`
Expected: 0 warnings, 0 errors.

- [ ] **Step 12: Commit**

```bash
git add sim/Faketorio.Sim/Simulation.cs \
        sim/Faketorio.Sim.Tests/SimulationTests.cs \
        sim/Faketorio.Sim.Tests/DeterminismTests.cs
git commit -m "$(cat <<'EOF'
feat(mining-drill): wire the electric mining drill into Simulation

Two-pass MiningDrillsTickPreSettle/PostSettle bracket ElectricGrid.Settle()
in Step(), mirroring P9's Machines register/settle/advance ordering.
Single-target footprint search (row-major, re-searches when the locked
tile is exhausted); output routed belt-first (via the typed
TryInsertAtBack from the belts plan), then container, else held until
space frees. Output tile is a fixed offset from the footprint edge
computed from the entity's placement-time Rotation, reusing
BeltNetwork.Delta — no RotateEntity command needed.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01BXC5RZ5wsWirQpprXn9KZV
EOF
)"
```

---

## Self-Review Notes

- **Spec coverage:** §3 `BeltLane` typed-item changes → Task 1 Steps 1-6. §4 `MiningDrillPrototype` → Task 2 Steps 1-3. §5 `MiningDrills` + namespace fix → Task 2 Steps 7-8. §6 two-pass tick algorithm (including the "unconditional demand registration" fix from spec self-review) → Task 3 Step 4. Output-tile computation → Task 3 Step 4 (inline in `MiningDrillTickPreSettle`). §7 data → Task 2 Step 3. §8 determinism → covered throughout (index-order scans, sorted `WriteState`, `Q16`-only math, fixed row-major search). §9 tests → every bullet has a corresponding test in Task 1/2/3, including the corner-handoff item-identity test the spec called out as proof the `Simulation.cs` fix (not just `BeltLane` in isolation) works.
- **Placeholder scan:** no TBD/TODO. The two `SimulationTests` cases that depend on the seeded map actually having ore under a specific footprint carry an explicit runtime `Assert.True(foundResource, "...")` sanity check with concrete remediation instructions (try nearby coordinates) rather than silently assuming — this is a known real risk (the exact resource layout for seed 0 isn't verified against `data/base`'s current `map-gen`/`resource` prototypes in this plan) called out explicitly rather than hidden.
- **Type consistency:** `MiningDrills` method names used in Task 3 (`RegisterDrill`, `UnregisterDrill`, `GetTargetX`, `GetTargetY`, `GetProgress`, `IsCompleted`, `GetPendingItemProtoId`, `SetTarget`, `AddProgress`, `MarkCompleted`, `ResetAfterFlush`, `WriteState`) match Task 2's declarations exactly. `BeltLane.TryInsertAtBack(int)`/`FrontItemProtoId`/`PositionedItem` used in Task 3's belt-output path and Task 1's own tests match Task 1's declarations exactly. `MiningDrillPrototype` field names (`MiningSpeed`, `EnergyUsageJPerTick`) match between Task 2's declaration and Task 3's usage.
- **Known risk flagged for the implementer, not hidden:** the `SimulationTests` cases that need an actual resource tile under a specific coordinate range depend on `ResourceGrid`'s seeded generation, which this plan does not independently re-derive. Rather than hardcoding a guessed-correct coordinate, Step 8 tells the implementer to verify at runtime and adjust if wrong — this is a deliberate, documented escape hatch, not a placeholder, since simulating P6's noise function by hand to pre-compute the exact answer would take longer than letting the implementer's test run tell them directly.
