# M1 Plan 3c — BeltLine 拆分算法 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现"移除一格传送带"——`BeltLane.ShrinkBack`/`ShrinkFront` 两个截短原语,`TileToLineIndex.Clear`,以及 `BeltNetwork.RemoveBelt`(单格线销毁 / 出口端摘除 / 入口端摘除 / 中间拆分四种,返回被丢弃物品计数)。配独立单测。不接入 `Simulation`。

**Architecture:** 端点摘除与中间拆分的前半段都在**原线上就地截短**(`ShrinkBack`/`ShrinkFront`,`BeltLineId` 不变、tile 索引 churn 最小);只有中间拆分的**后半段**是一条全新 `BeltLine`(用 3a 的 `ToAbsolutePositions`/`FromAbsolutePositions` 从快照重建)。断口处"前沿在存活侧、身体跨进被移格"的物品按丢弃处理,清除区间向前拓宽 `ItemWidthSubTiles - 1`。物品去向不是本层的事——`RemoveBelt` 只摘下、计数、返回。

**Tech Stack:** C# / .NET 8,xUnit 2.9,FNV-1a 状态哈希。

**Spec:** `docs/superpowers/specs/2026-08-28-belt-line-merge-split-design.md`(第 3 节 `ShrinkBack`/`ShrinkFront` 行、第 6 节"拆分算法"、第 10 节物品去向分层、第 11 节 Plan 3c 条目)

## Global Constraints

- 模拟层**不引用任何 Godot 类型**;**禁止 `float`/`double`** 参与状态计算——全部 `int`。
- 单位:`BeltLine.TileSubTiles` = 256(1 tile);`BeltLane.ItemWidthSubTiles` = 64(物品槽宽)。
- 确定性:`BeltNetwork` 按 `BeltLinePool` 索引序遍历(既有,不改);`TileToLineIndex` 只点查。
- **不改动** `sim/Faketorio.Sim/Simulation.cs`、`sim/Faketorio.Sim/Entities/EntityPool.cs`。**不改动 `BeltLane` 既有方法**(只追加 `ShrinkBack`/`ShrinkFront`)。
- 拆除是**冷路径**(玩家操作触发,不在每 tick 热路径),允许托管堆分配。
- `RemoveBelt(x, y)` 的**前置条件**:`(x, y)` 属于一条存活 `BeltLine`(3d 里由 `WorldGrid` + `Simulation.Apply` 的既有 `RemoveEntity` 逻辑保证)。不做运行时前置检查,与 `BeltLane` / `AddBelt` 信任前置条件的风格一致。
- 被清下来的物品**只计数、不决定去向**(§10:结构层摘下计数返回,策略层 3d+ 再定丢弃/掉地/进背包)。当前 `BeltLane` 上的物品无类型,`RemoveBelt` 返回总个数(`int`)。
- **本计划不做**:接入 `Simulation`;`EntityId → 线` 反查;每 tick 推进 / 线间交接;物品类型 / 去向策略。

## File Structure

| 文件 | 本计划的改动 |
|---|---|
| `sim/Faketorio.Sim/Belts/BeltLane.cs` | 在 `ExtendFront` 之后追加 `ShrinkBack(int)` / `ShrinkFront(int)` |
| `sim/Faketorio.Sim/Belts/TileToLineIndex.cs` | 追加 `Clear(int x, int y)` |
| `sim/Faketorio.Sim/Belts/BeltNetwork.cs` | 追加 `RemoveBelt(int, int) → int` + 私有 `ClearRange` / `MiddleSplit` / `SplitBackLane` |
| `sim/Faketorio.Sim.Tests/BeltLaneTests.cs` | Task 1 追加 |
| `sim/Faketorio.Sim.Tests/TileToLineIndexTests.cs` | Task 2 追加 |
| `sim/Faketorio.Sim.Tests/BeltNetworkTests.cs` | Task 2 / 3 追加 |

运行(工作目录 `C:\code\faketorio`,PowerShell):

- 单类:`dotnet test --filter "FullyQualifiedName~BeltLaneTests"`(换 `TileToLineIndexTests` / `BeltNetworkTests`)
- 全量:`dotnet test`

Plan 3b 合并后基线 `dotnet test` = 115。测试文件里已有的辅助方法(Plan 3b 留下):`BeltNetworkTests` 有 `LiveLines(BeltNetwork)`、`Hash(BeltNetwork)`、`EastLineOn(BeltNetwork, params (int x,int y)[])`(在既有 net 上顺 E 向建一条线,前到后给出 tiles,返回 `(net, BeltLineId)`)、常量 `N/E/S/W = 0/1/2/3`。

---

### Task 1: `BeltLane.ShrinkBack` / `ShrinkFront`

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltLane.cs`(在 `ExtendFront` 方法之后追加)
- Test: `sim/Faketorio.Sim.Tests/BeltLaneTests.cs`(追加)

**Interfaces:**
- Consumes: 既有 `BeltLane` 私有成员 `_lineLengthSubTiles`、`_gaps`、`BackFreeSubTiles()`、常量 `ItemWidthSubTiles`。
- Produces:
  - `public void ShrinkBack(int subtiles)` — `subtiles < 0` 抛 `ArgumentOutOfRangeException`;`_lineLengthSubTiles - subtiles < ItemWidthSubTiles` 抛 `InvalidOperationException`;`BackFreeSubTiles() < subtiles` 抛 `InvalidOperationException`(会把入口端物品截飞);否则 `_lineLengthSubTiles -= subtiles`,不碰 gap、不碰 `_openIndex`。
  - `public void ShrinkFront(int subtiles)` — `subtiles < 0` 抛 `ArgumentOutOfRangeException`;`_lineLengthSubTiles - subtiles < ItemWidthSubTiles` 抛 `InvalidOperationException`;`_gaps.Count > 0 && _gaps[0] < subtiles` 抛 `InvalidOperationException`(会把队首物品截飞);否则 `_lineLengthSubTiles -= subtiles`,`if (_gaps.Count > 0) _gaps[0] -= subtiles`,不碰 `_openIndex`。

- [ ] **Step 1: 写失败测试**

追加到 `BeltLaneTests.cs`:

```csharp
[Fact]
public void ShrinkBack_TightensBackCapacity_WithoutMovingItems()
{
    var lane = new BeltLane(512);
    lane.TryInsertAtBack();   // gaps=[448]
    lane.Advance(300);         // gaps=[148],物品前沿 148、尾沿 212,队尾空 300
    lane.ShrinkBack(256);      // 512 -> 256:物品仍在 [148,212],放得下
    Assert.Equal(new[] { 148 }, lane.Gaps);
    Assert.False(lane.TryInsertAtBack()); // 现在队尾只剩 256-(148+64)=44,插不下
}

[Fact]
public void ShrinkBack_WouldStrandEntryItem_Throws()
{
    var lane = new BeltLane(512);
    lane.TryInsertAtBack();   // gaps=[448],物品尾沿贴在 512 入口,队尾空 0
    Assert.Throws<InvalidOperationException>(() => lane.ShrinkBack(256));
}

[Fact]
public void ShrinkBack_BelowOneItemWidth_Throws()
{
    var lane = new BeltLane(256);
    Assert.Throws<InvalidOperationException>(() => lane.ShrinkBack(200)); // 256-200=56 < 64
}

[Fact]
public void ShrinkBack_Negative_Throws()
{
    Assert.Throws<ArgumentOutOfRangeException>(() => new BeltLane(256).ShrinkBack(-1));
}

[Fact]
public void ShrinkFront_MovesExitInward_TowardItems()
{
    var lane = new BeltLane(512);
    lane.TryInsertAtBack();   // gaps=[448]
    lane.ShrinkFront(256);     // 出口内移 256:gaps[0] 448-256=192,长度 256
    Assert.Equal(new[] { 192 }, lane.Gaps);
    Assert.Equal(new[] { 192 }, lane.ToAbsolutePositions());
}

[Fact]
public void ShrinkFront_WouldStrandFrontItem_Throws()
{
    var lane = new BeltLane(512);
    lane.TryInsertAtBack();
    lane.Advance(400);         // gaps=[48]
    Assert.Throws<InvalidOperationException>(() => lane.ShrinkFront(256)); // gaps[0]=48 < 256
}

[Fact]
public void ShrinkFront_EmptyLane_JustShortens()
{
    var lane = new BeltLane(512);
    lane.ShrinkFront(256);
    Assert.Equal(0, lane.Count);
    Assert.True(lane.TryInsertAtBack());
    Assert.Equal(new[] { 192 }, lane.Gaps); // 256 - 64
}

[Fact]
public void ShrinkFront_Negative_Throws()
{
    Assert.Throws<ArgumentOutOfRangeException>(() => new BeltLane(256).ShrinkFront(-1));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests.Shrink"`
Expected: 编译失败 —— `BeltLane` 无 `ShrinkBack` / `ShrinkFront`。

- [ ] **Step 3: 实现**

在 `BeltLane.cs` 的 `ExtendFront` 方法之后追加:

```csharp
    // ExtendBack 的逆:把入口端向内截短 subtiles 个亚格(移除队尾方向的
    // 相邻格子)。不碰任何 gap,已有物品到出口的距离不变。
    // 前置:被截区间 [新长, 旧长) 内没有物品——否则会把入口端物品截飞。
    public void ShrinkBack(int subtiles)
    {
        if (subtiles < 0) throw new ArgumentOutOfRangeException(nameof(subtiles));
        if (_lineLengthSubTiles - subtiles < ItemWidthSubTiles)
            throw new InvalidOperationException("ShrinkBack below one item width");
        if (BackFreeSubTiles() < subtiles)
            throw new InvalidOperationException("ShrinkBack would strand an item at the entry end");
        _lineLengthSubTiles -= subtiles;
    }

    // ExtendFront 的逆:把出口端向内截短 subtiles 个亚格(移除出口方向的
    // 相邻格子)。出口整体内移,最前物品到新出口的距离相应减小:gaps[0] -= subtiles。
    // 前置:出口 subtiles 亚格内没有物品(gaps[0] >= subtiles),否则会把队首物品截飞。
    // _openIndex 不变:gaps[0] 只会更小或归 0,不会重新变非零,不破坏游标不变式。
    public void ShrinkFront(int subtiles)
    {
        if (subtiles < 0) throw new ArgumentOutOfRangeException(nameof(subtiles));
        if (_lineLengthSubTiles - subtiles < ItemWidthSubTiles)
            throw new InvalidOperationException("ShrinkFront below one item width");
        if (_gaps.Count > 0 && _gaps[0] < subtiles)
            throw new InvalidOperationException("ShrinkFront would strand the front item");
        _lineLengthSubTiles -= subtiles;
        if (_gaps.Count > 0) _gaps[0] -= subtiles;
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests"` 然后 `dotnet test`
Expected: PASS(新增 8 个 + 全量绿)。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/BeltLane.cs sim/Faketorio.Sim.Tests/BeltLaneTests.cs
git commit -m "feat(sim): BeltLane.ShrinkBack / ShrinkFront — in-place truncation with strand guards

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

### Task 2: `TileToLineIndex.Clear` + `BeltNetwork.RemoveBelt`(单格线 / 出口端 / 入口端)

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/TileToLineIndex.cs`(追加 `Clear`)
- Modify: `sim/Faketorio.Sim/Belts/BeltNetwork.cs`(追加 `RemoveBelt` + 私有 `ClearRange`;中间分支先留 `throw`)
- Test: `sim/Faketorio.Sim.Tests/TileToLineIndexTests.cs`、`sim/Faketorio.Sim.Tests/BeltNetworkTests.cs`(追加)

**Interfaces:**
- Consumes: Task 1 的 `BeltLane.ShrinkBack`/`ShrinkFront`;既有 `BeltLane.TryRemoveItemInRange(int, int)`;`BeltLinePool.Get`/`Destroy`;`TileToLineIndex.Get`/`Set`;`BeltLine.Tiles` / `.LaneA` / `.LaneB`;`BeltLine.TileSubTiles`、`BeltLane.ItemWidthSubTiles`。
- Produces:
  - `TileToLineIndex.Clear(int x, int y)` — `Set(x, y, BeltLineId.Invalid)`。
  - `BeltNetwork.RemoveBelt(int x, int y) → int` — 返回被丢弃物品总个数(LaneA + LaneB)。分支:`n == 1` → `_pool.Destroy(id)` + `_tiles.Clear(x,y)`,返回 0。`k == 0`(出口端)→ 两 lane 各 `ClearRange(lane, 0, L)` 累加计数、各 `ShrinkFront(L)`、`Tiles.RemoveAt(0)`、`_tiles.Clear(x,y)`。`k == n-1`(入口端)→ 两 lane 各 `ClearRange(lane, (n-1)*L - (W-1), n*L)`、各 `ShrinkBack(L)`、`Tiles.RemoveAt(n-1)`、`_tiles.Clear(x,y)`。中间(`0 < k < n-1`)→ **本任务 `throw new NotSupportedException("middle split: Task 3")`**。
  - `private static int ClearRange(BeltLane lane, int from, int to)` — `while (lane.TryRemoveItemInRange(from, to)) c++;` 返回 `c`。
  - `L` = `BeltLine.TileSubTiles`,`W` = `BeltLane.ItemWidthSubTiles`(方法内局部 const)。

- [ ] **Step 1: 写失败测试**

追加到 `TileToLineIndexTests.cs`:

```csharp
[Fact]
public void Clear_RemovesEntry()
{
    var ix = new TileToLineIndex();
    ix.Set(5, 5, new BeltLineId(2, 1));
    ix.Clear(5, 5);
    Assert.Equal(BeltLineId.Invalid, ix.Get(5, 5));
}

[Fact]
public void Clear_UnsetTile_IsNoOp()
{
    var ix = new TileToLineIndex();
    ix.Clear(9, 9); // 不抛
    Assert.Equal(BeltLineId.Invalid, ix.Get(9, 9));
}
```

追加到 `BeltNetworkTests.cs`:

```csharp
[Fact]
public void RemoveBelt_SingleTileLine_DestroysIt()
{
    var net = new BeltNetwork();
    net.AddBelt(5, 5, E);
    int discarded = net.RemoveBelt(5, 5);
    Assert.Equal(0, discarded);
    Assert.Empty(LiveLines(net));
    Assert.False(net.GetLineAt(5, 5).IsValid);
}

[Fact]
public void RemoveBelt_ExitEndpoint_ShrinksLineKeepsId()
{
    var net = new BeltNetwork();
    var (_, id) = EastLineOn(net, (5, 3), (4, 3), (3, 3)); // len 768, exit (5,3)
    int discarded = net.RemoveBelt(5, 3);
    Assert.Equal(0, discarded);
    Assert.Single(LiveLines(net));
    var line = net.GetLine(id);                // 同一 id 仍存活
    Assert.Equal(new[] { (4, 3), (3, 3) }, line.Tiles);
    Assert.Equal(512, line.LengthSubTiles);
    Assert.False(net.GetLineAt(5, 3).IsValid);
    Assert.Equal(id, net.GetLineAt(4, 3));
}

[Fact]
public void RemoveBelt_EntryEndpoint_ShrinksLineKeepsId()
{
    var net = new BeltNetwork();
    var (_, id) = EastLineOn(net, (5, 3), (4, 3), (3, 3)); // entry (3,3), k = 2 = n-1
    int discarded = net.RemoveBelt(3, 3);
    Assert.Equal(0, discarded);
    var line = net.GetLine(id);
    Assert.Equal(new[] { (5, 3), (4, 3) }, line.Tiles);
    Assert.Equal(512, line.LengthSubTiles);
    Assert.False(net.GetLineAt(3, 3).IsValid);
}

[Fact]
public void RemoveBelt_ExitEndpoint_DiscardsItemOnRemovedTile_AndCounts()
{
    var net = new BeltNetwork();
    var (_, id) = EastLineOn(net, (5, 3), (4, 3), (3, 3)); // len 768
    var lane = net.GetLine(id).LaneA;
    lane.TryInsertAtBack();   // gaps=[704]
    lane.Advance(704);         // gaps=[0],物品贴在出口(前沿 0,在出口格 [0,256))
    int discarded = net.RemoveBelt(5, 3);
    Assert.Equal(1, discarded);
    Assert.Equal(0, net.GetLine(id).LaneA.Count);
    Assert.Equal(512, net.GetLine(id).LengthSubTiles);
}

[Fact]
public void RemoveBelt_EntryEndpoint_DiscardsBodyStraddler_AndCounts()
{
    var net = new BeltNetwork();
    var (_, id) = EastLineOn(net, (5, 3), (4, 3), (3, 3)); // len 768, k=2 removes tile [512,768)
    var lane = net.GetLine(id).LaneA;
    lane.TryInsertAtBack();   // gaps=[704],abs 704
    lane.Advance(222);         // gaps=[482],abs 482:身体 [482,546] 从 tile (4,3) 跨进 (3,3)
    int discarded = net.RemoveBelt(3, 3);
    Assert.Equal(1, discarded); // 前沿 482 ∈ [512-63, 768) = [449,768) → 被清
    Assert.Equal(0, net.GetLine(id).LaneA.Count);
    Assert.Equal(512, net.GetLine(id).LengthSubTiles);
}

[Fact]
public void RemoveBelt_Endpoint_IsDeterministic()
{
    BeltNetwork Build()
    {
        var n = new BeltNetwork();
        EastLineOn(n, (5, 3), (4, 3), (3, 3));
        n.RemoveBelt(5, 3);
        return n;
    }
    Assert.Equal(Hash(Build()), Hash(Build()));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~TileToLineIndexTests.Clear"` 加 `dotnet test --filter "FullyQualifiedName~BeltNetworkTests.RemoveBelt"`
Expected: 编译失败 —— `TileToLineIndex.Clear` / `BeltNetwork.RemoveBelt` 不存在。

- [ ] **Step 3: 实现**

`TileToLineIndex.cs` 的 `Set` 之后追加:

```csharp
    // 清掉某格的记录(拆除传送带时用)。等价于 Set(x, y, Invalid)。
    public void Clear(int x, int y) => Set(x, y, BeltLineId.Invalid);
```

`BeltNetwork.cs` 在 `WriteState` 之后、`ConcatLanes` 之前追加:

```csharp
    // 拆除 (x, y) 这格传送带。返回被丢弃物品的总个数(两条 lane 合计)。
    // 前置条件:(x, y) 属于一条存活线(见 Global Constraints)。
    // 端点摘除与中间拆分的前半段就地截短(id 不变);只有中间拆分的后半段是
    // 全新线。断口处身体跨进被移格的物品按丢弃处理(见设计文档第 6 / 10 节)。
    public int RemoveBelt(int x, int y)
    {
        const int L = BeltLine.TileSubTiles;
        const int W = BeltLane.ItemWidthSubTiles;

        var id = _tiles.Get(x, y);
        var line = _pool.Get(id);
        int n = line.Tiles.Count;
        int k = line.Tiles.IndexOf((x, y));

        if (n == 1)
        {
            _pool.Destroy(id);
            _tiles.Clear(x, y);
            return 0;
        }

        if (k == 0) // 出口端
        {
            int count = ClearRange(line.LaneA, 0, L) + ClearRange(line.LaneB, 0, L);
            line.LaneA.ShrinkFront(L);
            line.LaneB.ShrinkFront(L);
            line.Tiles.RemoveAt(0);
            _tiles.Clear(x, y);
            return count;
        }

        if (k == n - 1) // 入口端:低端向前拓宽 W-1,收身体跨进被移格的物品
        {
            int lo = (n - 1) * L - (W - 1);
            int count = ClearRange(line.LaneA, lo, n * L) + ClearRange(line.LaneB, lo, n * L);
            line.LaneA.ShrinkBack(L);
            line.LaneB.ShrinkBack(L);
            line.Tiles.RemoveAt(n - 1);
            _tiles.Clear(x, y);
            return count;
        }

        throw new NotSupportedException("middle split: Task 3");
    }

    // 循环把前沿落在 [from, to) 内的物品从 lane 摘除,返回摘除数。
    private static int ClearRange(BeltLane lane, int from, int to)
    {
        int c = 0;
        while (lane.TryRemoveItemInRange(from, to)) c++;
        return c;
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~TileToLineIndexTests"` 加 `dotnet test --filter "FullyQualifiedName~BeltNetworkTests"` 然后 `dotnet test`
Expected: PASS(新增 8 个 + 全量绿)。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/TileToLineIndex.cs sim/Faketorio.Sim/Belts/BeltNetwork.cs sim/Faketorio.Sim.Tests/TileToLineIndexTests.cs sim/Faketorio.Sim.Tests/BeltNetworkTests.cs
git commit -m "feat(sim): BeltNetwork.RemoveBelt — single-tile destroy + endpoint shrink; TileToLineIndex.Clear

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

### Task 3: `RemoveBelt` 中间拆分

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltNetwork.cs`(把 Task 2 的 `throw new NotSupportedException("middle split: Task 3")` 换成 `return MiddleSplit(line, k, n, x, y);`;新增私有 `MiddleSplit` + `SplitBackLane`)
- Test: `sim/Faketorio.Sim.Tests/BeltNetworkTests.cs`(追加)

**Interfaces:**
- Consumes: Task 2 的 `RemoveBelt` 骨架、`ClearRange`;既有 `BeltLane.ToAbsolutePositions()`、`BeltLane.FromAbsolutePositions(int, IReadOnlyList<int>)`、`BeltLane.ShrinkBack(int)`;`BeltLinePool.Create`;`TileToLineIndex.Set`/`Clear`;`BeltLine` 构造函数 `(byte, List<(int X,int Y)>, BeltLane, BeltLane)`。
- Produces:
  - `private int MiddleSplit(BeltLine line, int k, int n, int x, int y)` — `cut = (k+1)*L`;`backLen = (n-1-k)*L`。**后半段**:两条 lane 各 `SplitBackLane(lane, cut, backLen)`;`backTiles = line.Tiles.GetRange(k+1, n-1-k)`;`new BeltLine(line.Direction, backTiles, backA, backB)` → `_pool.Create` 得 `backId`;`backTiles` 每格 `_tiles.Set(→ backId)`。**前半段**(原线,id 不变):对两条 lane,先 `while (lane.TryRemoveItemInRange(cut, n*L)) {}`(移走去后半段的,不计数),再 `while (lane.TryRemoveItemInRange(k*L - (W-1), n*L)) discarded++`(丢弃跨界 / 被移格上的,计数),再 `lane.ShrinkBack((n-k)*L)`;然后 `line.Tiles.RemoveRange(k, n-k)`。最后 `_tiles.Clear(x, y)`。返回 `discarded`。
  - `private static BeltLane SplitBackLane(BeltLane src, int cut, int backLen)` — `var back = new List<int>();` `foreach (int p in src.ToAbsolutePositions()) if (p >= cut) back.Add(p - cut);` `return BeltLane.FromAbsolutePositions(backLen, back);`

- [ ] **Step 1: 写失败测试**

追加到 `BeltNetworkTests.cs`:

```csharp
[Fact]
public void RemoveBelt_MiddleTile_SplitsIntoTwoLines()
{
    var net = new BeltNetwork();
    var (_, id) = EastLineOn(net, (6, 3), (5, 3), (4, 3), (3, 3), (2, 3)); // 5 tiles, len 1280
    int discarded = net.RemoveBelt(4, 3); // k = 2, n = 5

    Assert.Equal(0, discarded);
    Assert.Equal(2, LiveLines(net).Count);

    var front = net.GetLine(id);                       // 原 id 仍存活
    Assert.Equal(new[] { (6, 3), (5, 3) }, front.Tiles);
    Assert.Equal(512, front.LengthSubTiles);

    var backId = net.GetLineAt(3, 3);
    Assert.NotEqual(id, backId);
    var back = net.GetLine(backId);
    Assert.Equal(new[] { (3, 3), (2, 3) }, back.Tiles);
    Assert.Equal(512, back.LengthSubTiles);

    Assert.False(net.GetLineAt(4, 3).IsValid);
    Assert.Equal(id, net.GetLineAt(6, 3));
    Assert.Equal(backId, net.GetLineAt(2, 3));
}

[Fact]
public void RemoveBelt_MiddleTile_BackHalfItemGetsCorrectAbsolutePosition()
{
    var net = new BeltNetwork();
    var (_, id) = EastLineOn(net, (6, 3), (5, 3), (4, 3), (3, 3), (2, 3)); // len 1280
    net.GetLine(id).LaneA.TryInsertAtBack(); // gaps=[1216],abs 1216(在 tile (2,3))

    net.RemoveBelt(4, 3); // k=2,cut = 3*256 = 768
    var backId = net.GetLineAt(2, 3);

    // 1216 >= 768 → 后半段,新前沿 1216 - 768 = 448
    Assert.Equal(new[] { 448 }, net.GetLine(backId).LaneA.ToAbsolutePositions());
    Assert.Equal(0, net.GetLine(id).LaneA.Count); // 前半段没物品
}

[Fact]
public void RemoveBelt_MiddleTile_ItemOnRemovedTile_DiscardedAndCounted()
{
    var net = new BeltNetwork();
    var (_, id) = EastLineOn(net, (6, 3), (5, 3), (4, 3), (3, 3), (2, 3)); // len 1280
    var lane = net.GetLine(id).LaneA;
    lane.TryInsertAtBack(); // gaps=[1216]
    lane.Advance(576);       // gaps=[640],abs 640:落在被移格 tile 2 = [512,768)

    int discarded = net.RemoveBelt(4, 3);
    Assert.Equal(1, discarded);
    var backId = net.GetLineAt(2, 3);
    Assert.Equal(0, net.GetLine(id).LaneA.Count);
    Assert.Equal(0, net.GetLine(backId).LaneA.Count);
}

[Fact]
public void RemoveBelt_MiddleTile_SeamStraddler_DiscardedAndCounted()
{
    var net = new BeltNetwork();
    var (_, id) = EastLineOn(net, (6, 3), (5, 3), (4, 3), (3, 3), (2, 3)); // len 1280
    var lane = net.GetLine(id).LaneA;
    lane.TryInsertAtBack(); // gaps=[1216]
    lane.Advance(726);       // gaps=[490],abs 490:前沿在 tile 1 [256,512),身体 [490,554] 跨进被移格

    int discarded = net.RemoveBelt(4, 3); // 若清除区间不向前拓宽 W-1,这个物品会留在前半段,ShrinkBack 会抛
    Assert.Equal(1, discarded);
    Assert.Equal(0, net.GetLine(id).LaneA.Count);
    Assert.Equal(512, net.GetLine(id).LengthSubTiles);
}

[Fact]
public void RemoveBelt_MiddleTile_IsDeterministic()
{
    BeltNetwork Build()
    {
        var n = new BeltNetwork();
        EastLineOn(n, (6, 3), (5, 3), (4, 3), (3, 3), (2, 3));
        n.RemoveBelt(4, 3);
        return n;
    }
    Assert.Equal(Hash(Build()), Hash(Build()));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltNetworkTests.RemoveBelt_MiddleTile"`
Expected: FAIL —— 当前中间分支 `throw new NotSupportedException`,测试全部抛异常。

- [ ] **Step 3: 实现**

`BeltNetwork.cs` 里把

```csharp
        throw new NotSupportedException("middle split: Task 3");
```

换成

```csharp
        return MiddleSplit(line, k, n, x, y);
```

并在 `ClearRange` 之后追加:

```csharp
    // 中间拆分:被移格下标 0 < k < n-1。前半段(原线,id 不变)就地 ShrinkBack;
    // 后半段(全新线)从快照重建,出口落在 (k+1)*L 亚格边界。返回丢弃物品数。
    private int MiddleSplit(BeltLine line, int k, int n, int x, int y)
    {
        const int L = BeltLine.TileSubTiles;
        const int W = BeltLane.ItemWidthSubTiles;
        int cut = (k + 1) * L;
        int backLen = (n - 1 - k) * L;

        // 1) 后半段:前沿 >= cut 的物品,前沿减 cut(在原线被 mutate 之前读快照)
        var backA = SplitBackLane(line.LaneA, cut, backLen);
        var backB = SplitBackLane(line.LaneB, cut, backLen);
        var backTiles = line.Tiles.GetRange(k + 1, n - 1 - k);
        var backId = _pool.Create(new BeltLine(line.Direction, backTiles, backA, backB));
        foreach (var (tx, ty) in backTiles)
            _tiles.Set(tx, ty, backId);

        // 2) 前半段:原线。先移走去后半段的(不计数),再丢弃跨界/被移格上的(计数)
        int discarded = 0;
        foreach (var lane in new[] { line.LaneA, line.LaneB })
        {
            while (lane.TryRemoveItemInRange(cut, n * L)) { }
            while (lane.TryRemoveItemInRange(k * L - (W - 1), n * L)) discarded++;
            lane.ShrinkBack((n - k) * L);
        }
        line.Tiles.RemoveRange(k, n - k);

        // 3) 被移格
        _tiles.Clear(x, y);
        return discarded;
    }

    // 从 src 的绝对位置快照里取前沿 >= cut 的物品,前沿减 cut,重建一条长
    // backLen 的新 lane。前沿 < cut 的(前半段 / 被移格 / 跨界)一律不带进来。
    private static BeltLane SplitBackLane(BeltLane src, int cut, int backLen)
    {
        var back = new List<int>();
        foreach (int p in src.ToAbsolutePositions())
            if (p >= cut) back.Add(p - cut);
        return BeltLane.FromAbsolutePositions(backLen, back);
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltNetworkTests"` 然后 `dotnet test`
Expected: PASS(新增 5 个 + 全量绿)。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/BeltNetwork.cs sim/Faketorio.Sim.Tests/BeltNetworkTests.cs
git commit -m "feat(sim): BeltNetwork.RemoveBelt middle split — front half shrinks in place, back half rebuilt

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

## Self-Review

**Spec coverage(设计文档 §3 / §6 / §10 / §11 Plan 3c 条目):**

| 要求 | 任务 |
|---|---|
| `BeltLane.ShrinkBack(int)`(fail-fast:截飞物品 / 低于一个物品宽) | Task 1 |
| `BeltLane.ShrinkFront(int)`(fail-fast:截飞队首 / 低于一个物品宽);`_openIndex` 不变 | Task 1 |
| `TileToLineIndex.Clear(int, int)` | Task 2 |
| `RemoveBelt` `n == 1` → 销毁 + 清索引 | Task 2 |
| `RemoveBelt` 出口端(`k==0`)→ 清 `[0, L)`、`ShrinkFront(L)`、`Tiles.RemoveAt(0)`、id 不变 | Task 2 |
| `RemoveBelt` 入口端(`k==n-1`)→ 清 `[(n-1)L - (W-1), nL)`(拓宽收跨界)、`ShrinkBack(L)`、id 不变 | Task 2 |
| `RemoveBelt` 中间 → 前半段就地 `ShrinkBack`(id 不变)、后半段全新线(`FromAbsolutePositions` 重建、新 id、tile 索引重指向) | Task 3 |
| 中间前半段两轮清除:先移走去后半段的(不计数)、再丢弃跨界/被移格上的(计数) | Task 3 |
| `RemoveBelt` 返回被丢弃物品计数 | Task 2(`int` 返回)+ Task 3(`MiddleSplit` 返回 `discarded`) |
| §11 测试:"跨界物品被清" | Task 2 test `EntryEndpoint_DiscardsBodyStraddler`、Task 3 test `SeamStraddler_DiscardedAndCounted` |
| §11 测试:"后半段物品绝对位置正确" | Task 3 test `BackHalfItemGetsCorrectAbsolutePosition` |
| §11 测试:"前半段 id 不变 / 后半段新 id / tile 索引一致" | Task 3 test `SplitsIntoTwoLines` |
| §11 测试:"拆分确定性" | Task 2/3 各一个 `IsDeterministic` |
| 不做:`Simulation` / `EntityId→线` / 每 tick / 物品去向 | 全计划不涉及 |

**Placeholder scan:** 无 TBD/TODO;每个代码步骤是完整可编译代码;Task 2 的中间分支是明确的 `throw new NotSupportedException("middle split: Task 3")`,Task 3 Step 3 明确替换它。

**Type consistency:**
- `RemoveBelt(int, int) → int`;`ClearRange(BeltLane, int, int) → int`(static private,Task 2 定义,Task 3 不改);`MiddleSplit(BeltLine, int, int, int, int) → int`、`SplitBackLane(BeltLane, int, int) → BeltLane`(Task 3 定义并调用)。
- `L` / `W` 在每个方法内是局部 `const`(`BeltLine.TileSubTiles` / `BeltLane.ItemWidthSubTiles`),测试断言里的 `256/512/768/1280` 是期望值不是魔法常量。
- `BeltLine` 构造签名 `(byte, List<(int X,int Y)>, BeltLane, BeltLane)` —— Task 3 后半段用它,和 Plan 3b 一致。
- `line.Tiles.IndexOf((x, y))` —— `Tiles` 是 `List<(int X, int Y)>`,`(x,y)` 是 `(int,int)`,同一底层 `ValueTuple<int,int>`,结构相等,编译并工作。
- 依赖的 3a/3b 成员:`BeltLane.TryRemoveItemInRange` / `ToAbsolutePositions` / `FromAbsolutePositions` / `TryInsertAtBack` / `Advance` / `Count` / `Gaps`;`BeltLinePool.Get`/`Destroy`/`Create`;`TileToLineIndex.Get`/`Set`;`BeltLine.Tiles`/`LaneA`/`LaneB`/`LengthSubTiles`/`Direction`/`TileSubTiles` —— 全部已在 main。

**数值复核(关键测试):**
- `ShrinkFront` 成功案例:len 512,gaps=[448] → `ShrinkFront(256)`:`512-256=256 >= 64` ✓,`gaps[0]=448 >= 256` ✓ → len 256,gaps[0]=192。abs=[192]。✓
- Task 2 入口端跨界:5→3 tile 线 len 768,k=2=n-1,`lo = 2*256 - 63 = 449`。物品 abs 482 ∈ [449, 768) → 清,discarded=1。清后 `ShrinkBack(256)`:BackFree = 768 - 0 = 768 >= 256 ✓ → len 512。若不拓宽(区间 [512,768)),482 漏掉 → `ShrinkBack(256)`:BackFree = 768 - (482+64) = 222 < 256 → 抛。拓宽是 load-bearing。✓
- Task 3 拆分:5 tile len 1280,移 `(4,3)` k=2,`cut = 768`,`backLen = (5-1-2)*256 = 512`,前半段 `ShrinkBack((5-2)*256 = 768)` → len 512。后半段物品 abs 1216 → 1216-768 = 448,`FromAbsolutePositions(512,[448])` → 尾沿 448+64=512 ≤ 512 ✓。前半段 seam straddler abs 490 ∈ [2*256-63, 1280) = [449,1280) → 清,discarded=1,`ShrinkBack(768)` 在清空后 BackFree=1280 ≥ 768 ✓。✓

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-09-02-m1-plan3c-beltline-split.md`. Two execution options:

1. **Subagent-Driven(推荐)** — 每任务派新 subagent,任务间审查,迭代快。
2. **Inline Execution** — 当前会话按 executing-plans 批量执行,带检查点。

Which approach?
