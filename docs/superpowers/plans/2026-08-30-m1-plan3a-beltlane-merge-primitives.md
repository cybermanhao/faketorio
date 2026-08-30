# M1 Plan 3a — BeltLane 合并/拆分原语 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给已完成的 `BeltLane` 追加 6 个"贴到 `Simulation` 才需要"的能力(线段延长、绝对位置互转、范围摘除、规范序列化),不改动任何既有方法,不引入 `BeltLine`/`Simulation`。

**Architecture:** 全部作为 `BeltLane`(`sim/Faketorio.Sim/Belts/BeltLane.cs`)上的新增公开方法,复用其现有内部表示(`_gaps` 相对 gap 列表、`_lineLengthSubTiles`、`_openIndex` 游标)。唯一的结构改动是把 `_lineLengthSubTiles` 从 `readonly` 改为可变(`ExtendBack`/`ExtendFront` 要写它)。每个方法配一组 xUnit 单测,追加进现有的 `BeltLaneTests.cs`。

**Tech Stack:** C# / .NET 8,xUnit 2.9,FNV-1a 状态哈希(`Faketorio.Sim.State.Fnv1aHashWriter` / `IStateWriter`)。

**Spec:** `docs/superpowers/specs/2026-08-28-belt-line-merge-split-design.md`(第 3 节"`BeltLane` 新增能力";数值约定见 `docs/superpowers/specs/2026-07-03-faketorio-design.md` 5.6 节)

## Global Constraints

- 模拟层**不引用任何 Godot 类型**,可在纯 .NET 无头运行。
- 模拟层**禁止 `float`/`double`** 参与任何影响状态的计算——本计划全部用 `int`。
- 1 tile = 256 亚格单位;物品槽宽 `BeltLane.ItemWidthSubTiles` = 64(已有常量,不改)。
- **不改动 `BeltLane` 既有方法**(`TryInsertAtBack`/`Advance`/`IsFrontReady`/`RemoveFront`/`Count`/`Gaps`/`TouchesInLastAdvance`)的语义;每个任务末尾跑整个 `BeltLaneTests` 类,确认既有 21 个测试仍全绿。
- 新方法都在冷路径(放置/拆除/存档触发,不在每 tick 热路径),**允许托管堆分配**(`ToAbsolutePositions` 返回新数组等)。
- 规范序列化是确定性哈希与存档的唯一出口(spec 铁律 4):新增状态若不写进 `WriteState`,确定性测试就覆盖不到。
- **本计划不碰** `BeltLine`(尚不存在)、`Simulation`、`Simulation.WriteState`。

## File Structure

| 文件 | 责任 | 本计划的改动 |
|---|---|---|
| `sim/Faketorio.Sim/Belts/BeltLane.cs` | 单 lane 物品流算法核心 | `_lineLengthSubTiles` 去掉 `readonly`;新增 `using Faketorio.Sim.State;`;追加 6 个公开方法 |
| `sim/Faketorio.Sim.Tests/BeltLaneTests.cs` | `BeltLane` 单测 | 新增 `using Faketorio.Sim.State;`;追加 6 组 `[Fact]` + 1 个私有 helper `MakeThreeItemLane` + 1 个私有 helper `Hash` |

运行测试(工作目录 `C:\code\faketorio`,PowerShell):

- 全部 BeltLane 测试:`dotnet test --filter "FullyQualifiedName~BeltLaneTests"`
- 单个测试:`dotnet test --filter "FullyQualifiedName~BeltLaneTests.<方法名>"`

---

### Task 1: `ExtendBack(int subtiles)` — 入口端延长

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltLane.cs`(第 20 行 `_lineLengthSubTiles` 去 `readonly`;在 `RemoveFront` 之后追加方法)
- Test: `sim/Faketorio.Sim.Tests/BeltLaneTests.cs`(追加)

**Interfaces:**
- Consumes: 既有 `BeltLane(int lineLengthSubTiles)`、`TryInsertAtBack()`、`Gaps`。
- Produces: `public void ExtendBack(int subtiles)` — `subtiles < 0` 抛 `ArgumentOutOfRangeException`;否则只把 `_lineLengthSubTiles += subtiles`,不动任何 gap、不动 `_openIndex`。

- [ ] **Step 1: 写失败测试**

追加到 `BeltLaneTests.cs`:

```csharp
[Fact]
public void ExtendBack_GrowsBackCapacityWithoutMovingItems()
{
    var lane = new BeltLane(256);
    lane.TryInsertAtBack();               // gaps=[192],队尾已满
    Assert.False(lane.TryInsertAtBack()); // 延长前没有空间

    lane.ExtendBack(256);                 // line 现在长 512

    Assert.Equal(new[] { 192 }, lane.Gaps);       // 最前物品没有移动
    Assert.True(lane.TryInsertAtBack());           // 队尾腾出了空间
    Assert.Equal(new[] { 192, 192 }, lane.Gaps);  // 512 - (192+64) - 64 = 192
}

[Fact]
public void ExtendBack_NegativeSubtiles_Throws()
{
    var lane = new BeltLane(256);
    Assert.Throws<ArgumentOutOfRangeException>(() => lane.ExtendBack(-1));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests.ExtendBack"`
Expected: 编译失败 / FAIL —— `BeltLane` 不含 `ExtendBack`。

- [ ] **Step 3: 实现**

`BeltLane.cs` 第 20 行:

```csharp
    private int _lineLengthSubTiles;   // 原为 readonly;ExtendBack/ExtendFront 会写它
```

在 `RemoveFront()` 之后追加:

```csharp
    // 把入口端向外延长 subtiles 个亚格(并入队尾方向的相邻线段)。
    // 只增加可用总长,不触碰任何 gap——BackFreeSubTiles 的公式自动反映新
    // 长度,已有物品到出口的距离不变。
    public void ExtendBack(int subtiles)
    {
        if (subtiles < 0) throw new ArgumentOutOfRangeException(nameof(subtiles));
        _lineLengthSubTiles += subtiles;
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests"`
Expected: PASS（新增 2 个 + 既有全部）。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/BeltLane.cs sim/Faketorio.Sim.Tests/BeltLaneTests.cs
git commit -m "feat(sim): BeltLane.ExtendBack — grow entry end for belt merge

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

### Task 2: `ExtendFront(int subtiles)` — 出口端延长(含游标重置回归测试)

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltLane.cs`(在 `ExtendBack` 之后追加)
- Test: `sim/Faketorio.Sim.Tests/BeltLaneTests.cs`(追加)

**Interfaces:**
- Consumes: 既有 `TryInsertAtBack()`、`Advance(int)`、`Count`、`Gaps`。
- Produces: `public void ExtendFront(int subtiles)` — `subtiles < 0` 抛 `ArgumentOutOfRangeException`;否则 `_lineLengthSubTiles += subtiles`,若 `Count > 0` 则 `_gaps[0] += subtiles`,**并把 `_openIndex` 重置为 0**。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void ExtendFront_PushesExitOutwardAndEnlargesFrontGap()
{
    var lane = new BeltLane(256);
    lane.TryInsertAtBack();   // gaps=[192]
    lane.ExtendFront(256);    // 出口离物品又远了 256
    Assert.Equal(new[] { 448 }, lane.Gaps);
}

[Fact]
public void ExtendFront_OnEmptyLane_JustGrowsLength()
{
    var lane = new BeltLane(256);
    lane.ExtendFront(256);
    Assert.Equal(0, lane.Count);
    Assert.True(lane.TryInsertAtBack());       // 现在长 512
    Assert.Equal(new[] { 448 }, lane.Gaps);   // 512 - 64
}

[Fact]
public void ExtendFront_ResetsCursorSoBlockedFrontItemAdvancesIntoNewSpace()
{
    var lane = new BeltLane(256);
    // 装满并完全压缩:gaps=[0,0,0,0],内部游标停在末尾
    for (int i = 0; i < 4; i++) { lane.TryInsertAtBack(); lane.Advance(1000); }
    Assert.Equal(new[] { 0, 0, 0, 0 }, lane.Gaps);

    lane.ExtendFront(64);   // gaps=[64,0,0,0]
    lane.Advance(64);       // 队首必须能走进新腾出的 64

    Assert.Equal(new[] { 0, 0, 0, 0 }, lane.Gaps);
}

[Fact]
public void ExtendFront_NegativeSubtiles_Throws()
{
    var lane = new BeltLane(256);
    Assert.Throws<ArgumentOutOfRangeException>(() => lane.ExtendFront(-1));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests.ExtendFront"`
Expected: 编译失败 / FAIL —— 无 `ExtendFront`。

- [ ] **Step 3: 实现**

在 `ExtendBack` 之后追加:

```csharp
    // 把出口端向外延长 subtiles 个亚格(并入出口方向的相邻线段)。
    // 出口整体外移,所以最前物品到新出口的距离要相应增大:gaps[0] += subtiles。
    // 同时把内部游标重置为 0——原本压缩到 0 的最前 gap 现在重新有了空间,
    // 游标若停在它后面会导致最前物品永远不向新出口前进。
    public void ExtendFront(int subtiles)
    {
        if (subtiles < 0) throw new ArgumentOutOfRangeException(nameof(subtiles));
        _lineLengthSubTiles += subtiles;
        if (_gaps.Count > 0) _gaps[0] += subtiles;
        _openIndex = 0;
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests"`
Expected: PASS（新增 4 个 + 既有全部）。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/BeltLane.cs sim/Faketorio.Sim.Tests/BeltLaneTests.cs
git commit -m "feat(sim): BeltLane.ExtendFront — grow exit end, reset cursor

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

### Task 3: `ToAbsolutePositions()` — gap 列表 → 前沿绝对距离

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltLane.cs`(在 `ExtendFront` 之后追加)
- Test: `sim/Faketorio.Sim.Tests/BeltLaneTests.cs`(追加)

**Interfaces:**
- Consumes: 既有 `TryInsertAtBack()`、`Advance(int)`。
- Produces: `public IReadOnlyList<int> ToAbsolutePositions()` — 返回前到后每个物品**前沿距出口的绝对亚格距离**的新 `int[]`;空 lane 返回空数组。定义:`pos[0] = gaps[0]`;`pos[i] = pos[i-1] + ItemWidthSubTiles + gaps[i]`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void ToAbsolutePositions_EmptyLane_ReturnsEmpty()
{
    var lane = new BeltLane(256);
    Assert.Empty(lane.ToAbsolutePositions());
}

[Fact]
public void ToAbsolutePositions_ReturnsLeadingEdgeDistancesFromExit()
{
    var lane = new BeltLane(256);
    lane.TryInsertAtBack(); // gaps=[192]
    lane.Advance(100);       // gaps=[92]
    lane.TryInsertAtBack(); // gaps=[92,36]
    Assert.Equal(new[] { 92, 192 }, lane.ToAbsolutePositions()); // 92, 92+64+36
}

[Fact]
public void ToAbsolutePositions_TouchingItems_AreOneWidthApart()
{
    var lane = new BeltLane(256);
    for (int i = 0; i < 2; i++) { lane.TryInsertAtBack(); lane.Advance(1000); }
    Assert.Equal(new[] { 0, 64 }, lane.ToAbsolutePositions());
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests.ToAbsolutePositions"`
Expected: 编译失败 / FAIL。

- [ ] **Step 3: 实现**

在 `ExtendFront` 之后追加:

```csharp
    // 把相对 gap 列表转成"每个物品前沿距出口的绝对亚格距离"(前到后)。
    // 冷路径(合并/拆分/存档),一次线性扫描,允许分配。
    public IReadOnlyList<int> ToAbsolutePositions()
    {
        var result = new int[_gaps.Count];
        int pos = 0;
        for (int i = 0; i < _gaps.Count; i++)
        {
            pos += _gaps[i];
            result[i] = pos;
            pos += ItemWidthSubTiles;
        }
        return result;
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests"`
Expected: PASS（新增 3 个 + 既有全部）。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/BeltLane.cs sim/Faketorio.Sim.Tests/BeltLaneTests.cs
git commit -m "feat(sim): BeltLane.ToAbsolutePositions — gap list to absolute edges

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

### Task 4: `FromAbsolutePositions(int lineLength, IReadOnlyList<int> positions)` — 反向重建

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltLane.cs`(在 `ToAbsolutePositions` 之后追加)
- Test: `sim/Faketorio.Sim.Tests/BeltLaneTests.cs`(追加)

**Interfaces:**
- Consumes: 既有构造函数(`lineLength < ItemWidthSubTiles` 时抛 `ArgumentOutOfRangeException`);`Task 3` 的 `ToAbsolutePositions()`(roundtrip 测试用)。
- Produces: `public static BeltLane FromAbsolutePositions(int lineLength, IReadOnlyList<int> positions)` — `positions` 为前到后、升序的前沿绝对距离。构造一条新 `BeltLane(lineLength)`,`_gaps[i] = positions[i] - (i==0 ? 0 : positions[i-1] + ItemWidthSubTiles)`;`_openIndex` 取默认值 0。相邻前沿差 < `ItemWidthSubTiles`(含首项 < 0)抛 `ArgumentException`;末项尾沿 `positions[^1] + ItemWidthSubTiles > lineLength` 抛 `ArgumentException`。空列表 → 空 lane。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void FromAbsolutePositions_RoundTripsWithToAbsolutePositions()
{
    var lane = new BeltLane(256);
    lane.TryInsertAtBack(); lane.Advance(100);
    lane.TryInsertAtBack();                 // gaps=[92,36]
    var rebuilt = BeltLane.FromAbsolutePositions(256, lane.ToAbsolutePositions());
    Assert.Equal(new[] { 92, 36 }, rebuilt.Gaps);
    Assert.Equal(2, rebuilt.Count);
}

[Fact]
public void FromAbsolutePositions_EmptyList_GivesEmptyLaneOfGivenLength()
{
    var lane = BeltLane.FromAbsolutePositions(512, Array.Empty<int>());
    Assert.Equal(0, lane.Count);
    Assert.True(lane.TryInsertAtBack());
    Assert.Equal(new[] { 448 }, lane.Gaps); // 512 - 64
}

[Fact]
public void FromAbsolutePositions_OverlappingItems_Throws()
{
    Assert.Throws<ArgumentException>(
        () => BeltLane.FromAbsolutePositions(256, new[] { 10, 50 })); // 50-10 < 64
}

[Fact]
public void FromAbsolutePositions_ItemPastExit_Throws()
{
    Assert.Throws<ArgumentException>(
        () => BeltLane.FromAbsolutePositions(256, new[] { -1 }));
}

[Fact]
public void FromAbsolutePositions_ItemOverrunsLineEnd_Throws()
{
    Assert.Throws<ArgumentException>(
        () => BeltLane.FromAbsolutePositions(256, new[] { 200 })); // 200+64 > 256
}

[Fact]
public void FromAbsolutePositions_CursorStartsAtZero_BlockedLaneStillConverges()
{
    var lane = BeltLane.FromAbsolutePositions(256, new[] { 0, 64, 128, 192 });
    Assert.Equal(new[] { 0, 0, 0, 0 }, lane.Gaps);
    lane.Advance(7); // 不抛、不产生负值
    Assert.Equal(new[] { 0, 0, 0, 0 }, lane.Gaps);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests.FromAbsolutePositions"`
Expected: 编译失败 / FAIL。

- [ ] **Step 3: 实现**

在 `ToAbsolutePositions` 之后追加:

```csharp
    // 从"前沿绝对距离"列表(前到后、升序)和总长度重建一条新 lane。
    // _openIndex 取默认 0(唯一恒安全的初值,不沿用来源 lane 的游标)。
    // 相邻前沿差 < ItemWidthSubTiles 视为物品重叠(上游 bug),fail-fast。
    public static BeltLane FromAbsolutePositions(int lineLength, IReadOnlyList<int> positions)
    {
        var lane = new BeltLane(lineLength); // 长度非法时构造函数抛 ArgumentOutOfRangeException
        int prevTrailingEdge = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            int leadingEdge = positions[i];
            int gap = leadingEdge - prevTrailingEdge;
            if (gap < 0)
                throw new ArgumentException(
                    $"position[{i}]={leadingEdge} overlaps previous item (gap {gap})",
                    nameof(positions));
            lane._gaps.Add(gap);
            prevTrailingEdge = leadingEdge + ItemWidthSubTiles;
        }
        if (prevTrailingEdge > lineLength)
            throw new ArgumentException(
                $"last item trailing edge {prevTrailingEdge} exceeds line length {lineLength}",
                nameof(positions));
        return lane;
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests"`
Expected: PASS（新增 6 个 + 既有全部）。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/BeltLane.cs sim/Faketorio.Sim.Tests/BeltLaneTests.cs
git commit -m "feat(sim): BeltLane.FromAbsolutePositions — rebuild lane from edges

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

### Task 5: `TryRemoveItemInRange(int fromSubTile, int toSubTile)` — 范围摘除 + 缝合

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltLane.cs`(在 `FromAbsolutePositions` 之后追加)
- Test: `sim/Faketorio.Sim.Tests/BeltLaneTests.cs`(追加,含私有 helper `MakeThreeItemLane`)

**Interfaces:**
- Consumes: `Task 4` 的 `FromAbsolutePositions`(helper 与部分测试用);既有 `RemoveFront()`、`Advance(int)`、`ToAbsolutePositions()`、`Gaps`。
- Produces: `public bool TryRemoveItemInRange(int fromSubTile, int toSubTile)` — 找到**前沿绝对距离落在 `[fromSubTile, toSubTile)` 内的最前一个物品**;命中则摘除、把它前方 gap + 自身 `ItemWidthSubTiles` + 后方 gap 缝合进后一个 gap(是最后一个物品时直接丢弃,空间自动回到队尾),返回 `true`;无命中返回 `false` 且不改状态。摘除下标 `k ≤ _openIndex` 时把 `_openIndex` 收回到 `k`,并夹到 `[0, Count-1]`(空 lane 归 0)。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void TryRemoveItemInRange_NoItemInRange_ReturnsFalseAndKeepsState()
{
    var lane = MakeThreeItemLane();                 // gaps=[20,20,24],前沿 20/104/192
    Assert.False(lane.TryRemoveItemInRange(200, 220));
    Assert.Equal(new[] { 20, 20, 24 }, lane.Gaps);
}

[Fact]
public void TryRemoveItemInRange_MiddleItem_StitchesGapsKeepingSurvivorsInPlace()
{
    var lane = MakeThreeItemLane();
    Assert.True(lane.TryRemoveItemInRange(100, 110)); // 命中中间物品(前沿 104)
    Assert.Equal(new[] { 20, 108 }, lane.Gaps);        // 24 + 20 + 64
    Assert.Equal(new[] { 20, 192 }, lane.ToAbsolutePositions()); // 幸存物品没有移动
}

[Fact]
public void TryRemoveItemInRange_LastItem_JustDropsIt()
{
    var lane = MakeThreeItemLane();
    Assert.True(lane.TryRemoveItemInRange(190, 260));
    Assert.Equal(new[] { 20, 20 }, lane.Gaps);
}

[Fact]
public void TryRemoveItemInRange_FrontItemWithNonZeroFrontGap_FoldsIntoNextGap()
{
    var lane = MakeThreeItemLane();
    Assert.True(lane.TryRemoveItemInRange(0, 50));   // 命中最前物品(前沿 20)
    Assert.Equal(new[] { 104, 24 }, lane.Gaps);       // 20 + 20 + 64
}

[Fact]
public void TryRemoveItemInRange_MatchesRemoveFront_InTheFrontZeroGapCase()
{
    var a = new BeltLane(256);
    a.TryInsertAtBack(); a.Advance(100); a.TryInsertAtBack(); a.Advance(110); // gaps=[0,18]
    var b = new BeltLane(256);
    b.TryInsertAtBack(); b.Advance(100); b.TryInsertAtBack(); b.Advance(110); // gaps=[0,18]

    a.RemoveFront();
    Assert.True(b.TryRemoveItemInRange(0, 1));

    Assert.Equal(a.Gaps, b.Gaps); // 两者都是 [82]
}

[Fact]
public void TryRemoveItemInRange_PullsCursorBackSoTrailingItemsAdvance()
{
    var lane = BeltLane.FromAbsolutePositions(256, new[] { 0, 64, 128, 192 }); // gaps=[0,0,0,0]
    lane.Advance(1000); // 游标推到末尾,gaps 仍是 [0,0,0,0]

    Assert.True(lane.TryRemoveItemInRange(64, 65)); // 摘掉下标 1(前沿 64)
    // 缝合:_gaps[2] += _gaps[1] + 64 => [0,0,64,0],RemoveAt(1) => [0,64,0]

    lane.Advance(64); // 若游标没被收回,这个 64 gap 不会被消耗
    Assert.Equal(new[] { 0, 0, 0 }, lane.Gaps);
}

private static BeltLane MakeThreeItemLane()
{
    // len 256,三个 64 宽物品,gap 20/20/24(和 64 + 物品体 192 = 256)
    return BeltLane.FromAbsolutePositions(256, new[] { 20, 104, 192 });
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests.TryRemoveItemInRange"`
Expected: 编译失败 / FAIL。

- [ ] **Step 3: 实现**

在 `FromAbsolutePositions` 之后追加:

```csharp
    // 摘除"前沿绝对距离落在 [fromSubTile, toSubTile) 内"的最前一个物品。
    // 命中:把它前方 gap、自身 ItemWidthSubTiles、后方 gap 缝合进后一个 gap
    // (是最后一个物品时直接丢弃,腾出的空间自动回到队尾),返回 true。
    // 无命中:返回 false,不改状态。
    // RemoveFront 是本操作在"下标 0、前方 gap 为 0"特例下的简化版。
    public bool TryRemoveItemInRange(int fromSubTile, int toSubTile)
    {
        int pos = 0;
        int removeAt = -1;
        for (int k = 0; k < _gaps.Count; k++)
        {
            pos += _gaps[k];                    // 物品 k 的前沿
            if (pos >= toSubTile) return false; // 前沿只增不减,后面不可能再命中
            if (pos >= fromSubTile) { removeAt = k; break; }
            pos += ItemWidthSubTiles;
        }
        if (removeAt < 0) return false;

        if (removeAt + 1 < _gaps.Count)
            _gaps[removeAt + 1] += _gaps[removeAt] + ItemWidthSubTiles;
        _gaps.RemoveAt(removeAt);

        if (removeAt <= _openIndex)
            _openIndex = removeAt;
        if (_gaps.Count == 0)
            _openIndex = 0;
        else if (_openIndex > _gaps.Count - 1)
            _openIndex = _gaps.Count - 1;

        return true;
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests"`
Expected: PASS（新增 6 个 + 既有全部）。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/BeltLane.cs sim/Faketorio.Sim.Tests/BeltLaneTests.cs
git commit -m "feat(sim): BeltLane.TryRemoveItemInRange — pluck item, stitch gaps

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

### Task 6: `WriteState(IStateWriter)` — 规范序列化

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltLane.cs`(文件顶部加 `using Faketorio.Sim.State;`;在 `TryRemoveItemInRange` 之后追加方法)
- Test: `sim/Faketorio.Sim.Tests/BeltLaneTests.cs`(文件顶部加 `using Faketorio.Sim.State;`;追加测试 + 私有 helper `Hash`)

**Interfaces:**
- Consumes: `Faketorio.Sim.State.IStateWriter`(`void Write(byte)` / `void Write(int)` / `void Write(long)`)、`Faketorio.Sim.State.Fnv1aHashWriter`;`Task 4` 的 `FromAbsolutePositions`。
- Produces: `public void WriteState(IStateWriter writer)` — 写 `_gaps.Count`(`int`)后逐个写 `_gaps[i]`(`int`)。**不写** `_openIndex`、`_lineLengthSubTiles`、`TouchesInLastAdvance`。

- [ ] **Step 1: 写失败测试**

`BeltLaneTests.cs` 顶部把 `using` 补成:

```csharp
using Faketorio.Sim.Belts;
using Faketorio.Sim.State;
```

追加:

```csharp
[Fact]
public void WriteState_SameGaps_SameHash_RegardlessOfInternalCursor()
{
    var blocked = new BeltLane(256);
    for (int i = 0; i < 4; i++) { blocked.TryInsertAtBack(); blocked.Advance(1000); }
    // blocked.Gaps == [0,0,0,0],内部游标停在末尾

    var rebuilt = BeltLane.FromAbsolutePositions(256, new[] { 0, 64, 128, 192 });
    // rebuilt.Gaps == [0,0,0,0],游标在 0

    Assert.Equal(Hash(blocked), Hash(rebuilt));
}

[Fact]
public void WriteState_DifferentGaps_DifferentHash()
{
    var a = new BeltLane(256);
    a.TryInsertAtBack();                 // gaps=[192]
    var b = new BeltLane(256);
    b.TryInsertAtBack(); b.Advance(50);  // gaps=[142]
    Assert.NotEqual(Hash(a), Hash(b));
}

[Fact]
public void WriteState_EmptyLane_IsStable()
{
    var lane = new BeltLane(256);
    Assert.Equal(Hash(lane), Hash(lane));
}

private static ulong Hash(BeltLane lane)
{
    var w = new Fnv1aHashWriter();
    lane.WriteState(w);
    return w.Hash;
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests.WriteState"`
Expected: 编译失败 / FAIL —— 无 `WriteState`。

- [ ] **Step 3: 实现**

`BeltLane.cs` 第 1 行 `namespace` 之上加:

```csharp
using Faketorio.Sim.State;
```

在 `TryRemoveItemInRange` 之后追加:

```csharp
    // 规范序列化(spec 铁律 4):只写 gap 数量与 gap 列表。
    // 不写 _openIndex / TouchesInLastAdvance——两者纯派生:加载后 _openIndex
    // 归 0,下一次 Advance 多扫一趟即自愈,最终 gap 轨迹与状态哈希不受影响。
    // 线长不在这里写:BeltLine.WriteState 会写 Tiles 数量(线长 = 256 × 格数)。
    public void WriteState(IStateWriter writer)
    {
        writer.Write(_gaps.Count);
        for (int i = 0; i < _gaps.Count; i++)
            writer.Write(_gaps[i]);
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltLaneTests"`
Expected: PASS（新增 3 个 + 既有全部）。

- [ ] **Step 5: 全量回归 + 提交**

Run: `dotnet test`
Expected: 整个解决方案 PASS。

```bash
git add sim/Faketorio.Sim/Belts/BeltLane.cs sim/Faketorio.Sim.Tests/BeltLaneTests.cs
git commit -m "feat(sim): BeltLane.WriteState — canonical gap-list serialization

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

## Self-Review

**Spec coverage(设计文档第 3 节表格 6 行):**

| 能力 | 任务 |
|---|---|
| `ExtendBack(int subtiles)` | Task 1 |
| `ExtendFront(int subtiles)`(含 `_openIndex` 重置) | Task 2 |
| `ToAbsolutePositions()` | Task 3 |
| `static FromAbsolutePositions(int lineLength, IReadOnlyList<int> positions)`(含重叠 fail-fast) | Task 4 |
| `TryRemoveItemInRange(int fromSubTile, int toSubTile)`(含缝合、`_openIndex` 收回) | Task 5 |
| `WriteState(IStateWriter)`(不写 `_openIndex`) | Task 6 |

- "`ItemWidthSubTiles`(64)、`Advance` 摊还 O(1)、`RemoveFront` 既有语义全部不变" → 每个任务 Step 4 跑整个 `BeltLaneTests` 类(既有 21 测试)确认无回归。
- "不碰 `BeltLine`/`Simulation`" → 6 个任务只改 `BeltLane.cs` 与 `BeltLaneTests.cs`。
- 三路合并的"两条带物品 lane 拼接"由 `ToAbsolutePositions` + `FromAbsolutePositions` 组合实现(Plan 3b 消费),原语本身在 Task 3/4 就位。

**Placeholder scan:** 无 TODO/TBD;每个代码步骤都是完整可编译代码;测试都是具体断言。

**Type consistency:**
- `ToAbsolutePositions(): IReadOnlyList<int>` ↔ `FromAbsolutePositions(int, IReadOnlyList<int>)` 入参一致(Task 3 → Task 4 roundtrip)。
- `_lineLengthSubTiles` 在 Task 1 去 `readonly`,Task 1/2 都写它;Task 6 注释说明它不进 `WriteState`(由上层 `Tiles` 数量覆盖),与设计文档 §8 一致。
- `_openIndex` 语义:Task 2 重置为 0、Task 4 默认 0、Task 5 收回到摘除下标——三处都满足"≤ 真实首个非零下标"这个 `Advance` 正确性前提。
- helper `MakeThreeItemLane`(Task 5)、`Hash`(Task 6)均为 `BeltLaneTests` 私有静态方法,与既有 `EntityPoolTests.Hash` 不在同一类,无冲突。

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-30-m1-plan3a-beltlane-merge-primitives.md`. Two execution options:

1. **Subagent-Driven(推荐)** — 每个任务派一个全新 subagent,任务之间我来审查,迭代快。
2. **Inline Execution** — 在当前会话里按 executing-plans 批量执行,带检查点。

Which approach?
