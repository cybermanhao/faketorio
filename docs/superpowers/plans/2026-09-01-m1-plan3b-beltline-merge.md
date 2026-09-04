# M1 Plan 3b — BeltLine 数据结构 + 合并算法 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **状态: ✅ 已合并 main · 已验证** — 主线提交 merge `87025f2`(见 `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md` 进度快照)。下方 `- [ ]` 复选框为执行期工件,不代表当前状态。

**Goal:** 引入 `BeltLineId` / `BeltLinePool` / `BeltLine` / `BeltNetwork`,并实现"放置一格传送带时把连续同向直线段焊成一条 `BeltLine`"的合并算法(单侧 / 三路 / 都不接),配独立单测。不接入 `Simulation`。

**Architecture:** `BeltLine` 是一个哑数据壳(方向 + `Tiles` 坐标列表 + 两条 `BeltLane`)。`BeltLinePool` 是一份刻意的、与 `EntityPool<T>` 平行的引用类型代数 ID 池(`EntityPool<T> where T : struct` 不改)。`BeltNetwork` 持有池 + 一个 `tile → BeltLineId` 的分块稀疏索引(`TileToLineIndex`),对外只暴露 `AddBelt(x, y, direction)`、按池索引序遍历、`WriteState`。合并只查 `TileToLineIndex`,焊接复用 Plan 3a 的 `BeltLane.ExtendBack`/`ExtendFront`(单侧)与 `ToAbsolutePositions`/`FromAbsolutePositions`(三路拼接)。

**Tech Stack:** C# / .NET 8,xUnit 2.9,FNV-1a 状态哈希(`Faketorio.Sim.State.IStateWriter` / `Fnv1aHashWriter`)。

**Spec:** `docs/superpowers/specs/2026-08-28-belt-line-merge-split-design.md`(第 4 节"`BeltLine`(新类型)"、第 5 节"合并算法"、第 8 节、第 11 节 Plan 3b 条目)

## Global Constraints

- 模拟层**不引用任何 Godot 类型**,可在纯 .NET 无头运行。
- 模拟层**禁止 `float`/`double`** 参与任何影响状态的计算——本计划全部用 `int`。
- 单位:1 tile = 256 亚格,常量记作 `BeltLine.TileSubTiles`;物品槽宽 `BeltLane.ItemWidthSubTiles` = 64(既有,不改)。
- 方向 `byte`,复用 `Command.Rotation` 约定:`0/1/2/3 = 北/东/南/西`。
- 确定性:每 tick 遍历与状态哈希按 `BeltLinePool` 的**索引序**;`TileToLineIndex` 只做点查(不遍历),内部用 `Dictionary` 不影响确定性(与 `WorldGrid` 的 chunk 字典同理)。
- **不改动**:`sim/Faketorio.Sim/Entities/EntityPool.cs`、`sim/Faketorio.Sim/Belts/BeltLane.cs`、`sim/Faketorio.Sim/Simulation.cs`。`BeltLinePool` 是刻意的平行实现——确定性簿记(代数数组 + 空闲栈 + 高水位)只在 `BeltLinePool.cs` 里维护一处。
- 合并/放置是**冷路径**(玩家操作时触发,不在每 tick 热路径),允许托管堆分配。
- 规范序列化是确定性哈希/存档的唯一出口(spec 铁律 4):新增状态必须写进 `BeltNetwork.WriteState`。
- **本计划不做**:接入 `Simulation` / `Simulation.WriteState`;`EntityId → 线` 反查数组;拆分(`RemoveBelt`);每 tick 推进与线间交接;`TransportBeltPrototype` / 速度。
- `AddBelt(x, y, direction)` 的**前置条件**:`(x, y)` 未被任何传送带线占用(3d 里由 `Simulation.Apply` 的 `IsAreaFree` 保证;本计划的测试从不对同一格调用两次)。不做运行时检查,与 `BeltLane` 各方法信任前置条件的风格一致。

## File Structure

| 文件 | 责任 |
|---|---|
| `sim/Faketorio.Sim/Belts/BeltLineId.cs`（新建） | `readonly record struct BeltLineId(int Index, int Generation)` + `Invalid` |
| `sim/Faketorio.Sim/Belts/BeltLine.cs`（新建） | 哑数据壳:`Direction` / `Tiles` / `LaneA` / `LaneB` / `LengthSubTiles` / `TileSubTiles` 常量 |
| `sim/Faketorio.Sim/Belts/BeltLinePool.cs`（新建） | `BeltLine` 的代数 ID 池;`Create`/`Destroy`/`IsAlive`/`Get`/索引序访问/`WriteState` |
| `sim/Faketorio.Sim/Belts/TileToLineIndex.cs`（新建） | `tile → BeltLineId` 分块稀疏索引;`Get(x,y)` / `Set(x,y,id)` |
| `sim/Faketorio.Sim/Belts/BeltNetwork.cs`（新建） | 持池 + 索引;`AddBelt` 合并算法;索引序遍历;`WriteState` |
| `sim/Faketorio.Sim.Tests/BeltLinePoolTests.cs`（新建） | Task 1 |
| `sim/Faketorio.Sim.Tests/TileToLineIndexTests.cs`（新建） | Task 2 |
| `sim/Faketorio.Sim.Tests/BeltNetworkTests.cs`（新建） | Task 3 / 4 / 5 追加 |

运行测试(工作目录 `C:\code\faketorio`,PowerShell):

- 单类:`dotnet test --filter "FullyQualifiedName~BeltLinePoolTests"`(换 `TileToLineIndexTests` / `BeltNetworkTests`)
- 单测:`dotnet test --filter "FullyQualifiedName~BeltNetworkTests.<方法名>"`
- 全量:`dotnet test`

Plan 3a 合并后的基线是 `dotnet test` 全绿(87 个)。

---

### Task 1: `BeltLineId` + `BeltLine` 数据壳 + `BeltLinePool`

**Files:**
- Create: `sim/Faketorio.Sim/Belts/BeltLineId.cs`
- Create: `sim/Faketorio.Sim/Belts/BeltLine.cs`
- Create: `sim/Faketorio.Sim/Belts/BeltLinePool.cs`
- Test: `sim/Faketorio.Sim.Tests/BeltLinePoolTests.cs`

**Interfaces:**
- Consumes: 既有 `Faketorio.Sim.Belts.BeltLane`(构造函数 `BeltLane(int lineLengthSubTiles)`);`Faketorio.Sim.State.IStateWriter` / `Fnv1aHashWriter`。
- Produces:
  - `readonly record struct BeltLineId(int Index, int Generation)` — `static readonly BeltLineId Invalid = new(-1, 0)`;`bool IsValid => Index >= 0`。
  - `sealed class BeltLine`:`const int TileSubTiles = 256`;`readonly byte Direction`;`readonly List<(int X, int Y)> Tiles`;`readonly BeltLane LaneA`;`readonly BeltLane LaneB`;`int LengthSubTiles => Tiles.Count * TileSubTiles`;构造函数 `BeltLine(byte direction, List<(int X, int Y)> tiles, BeltLane laneA, BeltLane laneB)`。
  - `sealed class BeltLinePool`:`BeltLineId Create(BeltLine line)`;`void Destroy(BeltLineId id)`;`bool IsAlive(BeltLineId id)`;`BeltLine Get(BeltLineId id)`;`int Capacity { get; }`(高水位);`bool IsAliveAtIndex(int index)`;`BeltLine GetAtIndex(int index)`;`int GenerationAtIndex(int index)`;`void WriteState(IStateWriter writer)`。

- [ ] **Step 1: 写失败测试**

新建 `sim/Faketorio.Sim.Tests/BeltLinePoolTests.cs`:

```csharp
using Faketorio.Sim.Belts;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class BeltLinePoolTests
{
    private static BeltLine MakeLine(byte dir = 1)
        => new BeltLine(dir, new List<(int X, int Y)> { (0, 0) },
                        new BeltLane(BeltLine.TileSubTiles), new BeltLane(BeltLine.TileSubTiles));

    [Fact]
    public void CreateThenGet_ReturnsSameInstance()
    {
        var pool = new BeltLinePool();
        var line = MakeLine();
        var id = pool.Create(line);
        Assert.True(pool.IsAlive(id));
        Assert.Same(line, pool.Get(id));
    }

    [Fact]
    public void Destroy_MakesIdStale()
    {
        var pool = new BeltLinePool();
        var id = pool.Create(MakeLine());
        pool.Destroy(id);
        Assert.False(pool.IsAlive(id));
        Assert.Throws<InvalidOperationException>(() => pool.Get(id));
    }

    [Fact]
    public void ReusedSlot_BumpsGeneration_StaleIdStaysDead()
    {
        var pool = new BeltLinePool();
        var a = pool.Create(MakeLine());
        pool.Destroy(a);
        var b = pool.Create(MakeLine());
        Assert.Equal(a.Index, b.Index);          // 槽位复用
        Assert.NotEqual(a.Generation, b.Generation);
        Assert.False(pool.IsAlive(a));
        Assert.True(pool.IsAlive(b));
    }

    [Fact]
    public void IndexOrderIteration_SeesLiveSlotsOnly()
    {
        var pool = new BeltLinePool();
        var a = pool.Create(MakeLine());
        var b = pool.Create(MakeLine());
        var c = pool.Create(MakeLine());
        pool.Destroy(b);

        var seen = new List<int>();
        for (int i = 0; i < pool.Capacity; i++)
            if (pool.IsAliveAtIndex(i)) seen.Add(i);

        Assert.Equal(new[] { a.Index, c.Index }, seen);
    }

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        var pool = new BeltLinePool(initialCapacity: 2);
        for (int i = 0; i < 50; i++) pool.Create(MakeLine());
        Assert.True(pool.Capacity >= 50);
    }

    private static ulong Hash(BeltLinePool pool)
    {
        var w = new Fnv1aHashWriter();
        pool.WriteState(w);
        return w.Hash;
    }

    [Fact]
    public void WriteState_StableThenChangesWithAllocatorState()
    {
        var pool = new BeltLinePool();
        var a = pool.Create(MakeLine());
        var b = pool.Create(MakeLine());
        pool.Destroy(a);
        pool.Destroy(b);

        var h1 = Hash(pool);
        Assert.Equal(h1, Hash(pool));       // 无改动:稳定

        pool.Create(MakeLine());             // 弹空闲栈:_freeCount 与槽位代数都变
        Assert.NotEqual(h1, Hash(pool));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltLinePoolTests"`
Expected: 编译失败 —— `BeltLineId` / `BeltLine` / `BeltLinePool` 均不存在。

- [ ] **Step 3: 实现**

新建 `sim/Faketorio.Sim/Belts/BeltLineId.cs`:

```csharp
namespace Faketorio.Sim.Belts;

// 一条 BeltLine 的稳定句柄:池槽位 + 代数。用前校验代数一致,拦截"指向
// 已删除并被复用的槽位"。与 Entities 的 EntityId 同构(Belts 有独立的池)。
public readonly record struct BeltLineId(int Index, int Generation)
{
    public static readonly BeltLineId Invalid = new(-1, 0);
    public bool IsValid => Index >= 0;
}
```

新建 `sim/Faketorio.Sim/Belts/BeltLine.cs`:

```csharp
namespace Faketorio.Sim.Belts;

// 一条完整的逻辑传送带线:组合两条并行 BeltLane,外加"贴到世界上"才需要
// 的方向与覆盖格子。合并只顺同方向、在端点追加一格,所以 Tiles 恒为一段
// 共线直线,LengthSubTiles 恒 = Tiles.Count * TileSubTiles(= 每条 lane 的长度)。
public sealed class BeltLine
{
    // spec 5.6:1 tile = 256 亚格。
    public const int TileSubTiles = 256;

    // 复用 Command.Rotation 约定:0/1/2/3 = 北/东/南/西。构造后不变。
    public readonly byte Direction;

    // 前到后排列,Tiles[0] 是出口所在格,Tiles[^1] 是入口所在格。
    // 列表实例固定,内容在合并时被拼接(Insert(0,..) / Add(..) / 整体替换)。
    public readonly List<(int X, int Y)> Tiles;

    // 单侧合并直接 mutate 这两个实例(ExtendBack/ExtendFront);三路合并
    // 构造一条全新的 BeltLine,带全新的 lane。
    public readonly BeltLane LaneA;
    public readonly BeltLane LaneB;

    public int LengthSubTiles => Tiles.Count * TileSubTiles;

    public BeltLine(byte direction, List<(int X, int Y)> tiles, BeltLane laneA, BeltLane laneB)
    {
        Direction = direction;
        Tiles = tiles;
        LaneA = laneA;
        LaneB = laneB;
    }
}
```

新建 `sim/Faketorio.Sim/Belts/BeltLinePool.cs`:

```csharp
using Faketorio.Sim.State;

namespace Faketorio.Sim.Belts;

// 装 BeltLine(引用类型)的代数 ID 池。分配器逻辑与 Entities 的
// EntityPool<T> 同思路,但那个类约束 where T : struct,无法容纳 BeltLine
// (含 List 与两个 BeltLane 引用)。这里是一份刻意的平行实现:确定性
// 簿记(代数数组 + 空闲栈 + 高水位)只在这个文件里维护一处,不动已测的
// EntityPool<T>。
public sealed class BeltLinePool
{
    private BeltLine?[] _data;
    private int[] _generations;   // 偶数=空槽,奇数=存活(create+destroy 各 +1)
    private int[] _freeStack;
    private int _freeCount;
    private int _count;

    public BeltLinePool(int initialCapacity = 64)
    {
        _data = new BeltLine?[initialCapacity];
        _generations = new int[initialCapacity];
        _freeStack = new int[initialCapacity];
    }

    // 高水位:已用过的最大槽位数。按索引序遍历 [0, Capacity) 用。
    public int Capacity => _count;

    public BeltLineId Create(BeltLine line)
    {
        int index;
        if (_freeCount > 0)
        {
            index = _freeStack[--_freeCount];
        }
        else
        {
            if (_count == _data.Length) Grow();
            index = _count++;
        }
        _generations[index]++;   // 偶 -> 奇:存活
        _data[index] = line;
        return new BeltLineId(index, _generations[index]);
    }

    public void Destroy(BeltLineId id)
    {
        if (!IsAlive(id)) throw new InvalidOperationException($"Destroy on dead BeltLineId {id}");
        _generations[id.Index]++; // 奇 -> 偶:空槽
        _data[id.Index] = null;
        if (_freeCount == _freeStack.Length) Array.Resize(ref _freeStack, _freeStack.Length * 2);
        _freeStack[_freeCount++] = id.Index;
    }

    public bool IsAlive(BeltLineId id)
        => id.Index >= 0 && id.Index < _count && _generations[id.Index] == id.Generation
           && (id.Generation & 1) == 1;

    public BeltLine Get(BeltLineId id)
    {
        if (!IsAlive(id)) throw new InvalidOperationException($"Get on dead BeltLineId {id}");
        return _data[id.Index]!;
    }

    // 按索引序确定遍历(每 tick 更新 / 状态哈希用)。
    public bool IsAliveAtIndex(int index) => (_generations[index] & 1) == 1;
    public BeltLine GetAtIndex(int index) => _data[index]!;
    public int GenerationAtIndex(int index) => _generations[index];

    // 分配器簿记(高水位/空闲栈/全部代数,含死槽)——从存档恢复后
    // Create() 的分配顺序必须一致(spec 铁律 4)。参照 EntityPool<T>.WriteState。
    public void WriteState(IStateWriter writer)
    {
        writer.Write(_count);
        for (int i = 0; i < _count; i++)
            writer.Write(_generations[i]);
        writer.Write(_freeCount);
        for (int i = 0; i < _freeCount; i++)
            writer.Write(_freeStack[i]);
    }

    private void Grow()
    {
        int newSize = _data.Length * 2;
        Array.Resize(ref _data, newSize);
        Array.Resize(ref _generations, newSize);
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltLinePoolTests"`
Expected: PASS(6 个)。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/BeltLineId.cs sim/Faketorio.Sim/Belts/BeltLine.cs sim/Faketorio.Sim/Belts/BeltLinePool.cs sim/Faketorio.Sim.Tests/BeltLinePoolTests.cs
git commit -m "feat(sim): BeltLineId + BeltLine holder + BeltLinePool (generational ref pool)

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

### Task 2: `TileToLineIndex`

**Files:**
- Create: `sim/Faketorio.Sim/Belts/TileToLineIndex.cs`
- Test: `sim/Faketorio.Sim.Tests/TileToLineIndexTests.cs`

**Interfaces:**
- Consumes: `BeltLineId`(Task 1)。
- Produces: `sealed class TileToLineIndex`:`BeltLineId Get(int x, int y)`(无记录返回 `BeltLineId.Invalid`);`void Set(int x, int y, BeltLineId id)`。

- [ ] **Step 1: 写失败测试**

新建 `sim/Faketorio.Sim.Tests/TileToLineIndexTests.cs`:

```csharp
using Faketorio.Sim.Belts;

namespace Faketorio.Sim.Tests;

public class TileToLineIndexTests
{
    private static readonly BeltLineId A = new(3, 1);
    private static readonly BeltLineId B = new(4, 1);

    [Fact]
    public void Get_Unset_ReturnsInvalid()
        => Assert.Equal(BeltLineId.Invalid, new TileToLineIndex().Get(5, 5));

    [Fact]
    public void SetThenGet_RoundTrips()
    {
        var ix = new TileToLineIndex();
        ix.Set(10, 20, A);
        Assert.Equal(A, ix.Get(10, 20));
        Assert.Equal(BeltLineId.Invalid, ix.Get(11, 20)); // 邻格未设
    }

    [Fact]
    public void Set_Overwrites()
    {
        var ix = new TileToLineIndex();
        ix.Set(1, 1, A);
        ix.Set(1, 1, B);
        Assert.Equal(B, ix.Get(1, 1));
    }

    [Fact]
    public void NegativeAndCrossChunkCoordinates_Work()
    {
        var ix = new TileToLineIndex();
        ix.Set(-1, -1, A);
        ix.Set(-33, -33, B);   // 另一个 chunk,且为负
        Assert.Equal(A, ix.Get(-1, -1));
        Assert.Equal(B, ix.Get(-33, -33));
        Assert.Equal(BeltLineId.Invalid, ix.Get(-32, -32));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~TileToLineIndexTests"`
Expected: 编译失败 —— `TileToLineIndex` 不存在。

- [ ] **Step 3: 实现**

新建 `sim/Faketorio.Sim/Belts/TileToLineIndex.cs`:

```csharp
namespace Faketorio.Sim.Belts;

// tile 坐标 → 覆盖该格的 BeltLineId 的稀疏索引,按 32×32 chunk 组织
// (与 WorldGrid 同套 chunk 键 / 下标算法)。只做点查(合并时判断邻格
// 属于哪条线),从不遍历,所以内部用 Dictionary 不影响确定性。缺省
// 返回 BeltLineId.Invalid。
public sealed class TileToLineIndex
{
    private const int ChunkSize = 32;
    private readonly Dictionary<long, BeltLineId[]> _chunks = new();

    private static long ChunkKey(int tileX, int tileY)
    {
        int cx = tileX >> 5, cy = tileY >> 5;   // 32 = 2^5,向负无穷取整
        return ((long)cx << 32) | (uint)cy;
    }

    private static int TileIndex(int tileX, int tileY)
    {
        int lx = tileX & (ChunkSize - 1), ly = tileY & (ChunkSize - 1);
        return ly * ChunkSize + lx;
    }

    public BeltLineId Get(int x, int y)
        => _chunks.TryGetValue(ChunkKey(x, y), out var c) ? c[TileIndex(x, y)] : BeltLineId.Invalid;

    public void Set(int x, int y, BeltLineId id)
    {
        long key = ChunkKey(x, y);
        if (!_chunks.TryGetValue(key, out var c))
        {
            c = new BeltLineId[ChunkSize * ChunkSize];
            Array.Fill(c, BeltLineId.Invalid);
            _chunks.Add(key, c);
        }
        c[TileIndex(x, y)] = id;
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~TileToLineIndexTests"`
Expected: PASS(4 个)。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/TileToLineIndex.cs sim/Faketorio.Sim.Tests/TileToLineIndexTests.cs
git commit -m "feat(sim): TileToLineIndex — chunked tile to BeltLineId sparse map

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

### Task 3: `BeltNetwork` 骨架 + 单格 `AddBelt`(无合并)+ `WriteState`

**Files:**
- Create: `sim/Faketorio.Sim/Belts/BeltNetwork.cs`
- Test: `sim/Faketorio.Sim.Tests/BeltNetworkTests.cs`

**Interfaces:**
- Consumes: `BeltLine` / `BeltLineId` / `BeltLinePool`(Task 1)、`TileToLineIndex`(Task 2)、既有 `BeltLane`、`IStateWriter` / `Fnv1aHashWriter`。
- Produces: `sealed class BeltNetwork`:
  - `BeltLineId AddBelt(int x, int y, byte direction)` — 登记一条长 256 的单格线并写 tile 索引,返回其 id。(Task 4/5 在同一方法里接合并分支。)
  - `int Capacity { get; }`;`bool IsAliveAtIndex(int index)`;`BeltLine GetAtIndex(int index)`。
  - `BeltLineId GetLineAt(int x, int y)`;`BeltLine GetLine(BeltLineId id)`。
  - `void WriteState(IStateWriter writer)`。
  - `private static (int dx, int dy) Delta(byte d)` — `0→(0,-1) 1→(1,0) 2→(0,1) 3→(-1,0)`,其它抛 `ArgumentOutOfRangeException`。

- [ ] **Step 1: 写失败测试**

新建 `sim/Faketorio.Sim.Tests/BeltNetworkTests.cs`:

```csharp
using Faketorio.Sim.Belts;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class BeltNetworkTests
{
    private const byte N = 0, E = 1, S = 2, W = 3;

    private static ulong Hash(BeltNetwork net)
    {
        var w = new Fnv1aHashWriter();
        net.WriteState(w);
        return w.Hash;
    }

    private static List<BeltLine> LiveLines(BeltNetwork net)
    {
        var result = new List<BeltLine>();
        for (int i = 0; i < net.Capacity; i++)
            if (net.IsAliveAtIndex(i)) result.Add(net.GetAtIndex(i));
        return result;
    }

    [Fact]
    public void AddBelt_SingleTile_CreatesOneLineCoveringThatTile()
    {
        var net = new BeltNetwork();
        var id = net.AddBelt(5, 3, E);

        var lines = LiveLines(net);
        Assert.Single(lines);
        Assert.Equal(E, lines[0].Direction);
        Assert.Equal(new[] { (5, 3) }, lines[0].Tiles);
        Assert.Equal(256, lines[0].LengthSubTiles);
        Assert.Equal(id, net.GetLineAt(5, 3));
        Assert.Same(lines[0], net.GetLine(id));
    }

    [Fact]
    public void AddBelt_SingleTile_LanesAreUsableLength256()
    {
        var net = new BeltNetwork();
        var line = net.GetLine(net.AddBelt(0, 0, E));
        Assert.True(line.LaneA.TryInsertAtBack());
        Assert.True(line.LaneB.TryInsertAtBack());
        Assert.Equal(new[] { 192 }, line.LaneA.Gaps); // 256 - 64
    }

    [Fact]
    public void GetLineAt_EmptyTile_ReturnsInvalid()
        => Assert.False(new BeltNetwork().GetLineAt(9, 9).IsValid);

    [Fact]
    public void TwoNonAdjacentBelts_StayTwoLines()
    {
        var net = new BeltNetwork();
        net.AddBelt(0, 0, E);
        net.AddBelt(10, 10, E);
        Assert.Equal(2, LiveLines(net).Count);
    }

    [Fact]
    public void WriteState_SameBuildSequence_SameHash()
    {
        BeltNetwork Build()
        {
            var n = new BeltNetwork();
            n.AddBelt(0, 0, E);
            n.AddBelt(4, 7, N);
            return n;
        }
        Assert.Equal(Hash(Build()), Hash(Build()));
    }

    [Fact]
    public void WriteState_DifferentContent_DifferentHash()
    {
        var a = new BeltNetwork(); a.AddBelt(0, 0, E);
        var b = new BeltNetwork(); b.AddBelt(0, 0, E); b.AddBelt(1, 9, E);
        Assert.NotEqual(Hash(a), Hash(b));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltNetworkTests"`
Expected: 编译失败 —— `BeltNetwork` 不存在。

- [ ] **Step 3: 实现**

新建 `sim/Faketorio.Sim/Belts/BeltNetwork.cs`:

```csharp
using Faketorio.Sim.State;

namespace Faketorio.Sim.Belts;

// 传送带线的容器:持有 BeltLine 池 + tile→线 索引。放置传送带时 AddBelt
// 触发合并(设计文档第 5 节);每 tick 推进与状态哈希按池索引序遍历。
// 本类不知道 Simulation / Entities 的存在(Plan 3b 边界)。
public sealed class BeltNetwork
{
    private readonly BeltLinePool _pool = new();
    private readonly TileToLineIndex _tiles = new();

    public int Capacity => _pool.Capacity;
    public bool IsAliveAtIndex(int index) => _pool.IsAliveAtIndex(index);
    public BeltLine GetAtIndex(int index) => _pool.GetAtIndex(index);

    public BeltLineId GetLineAt(int x, int y) => _tiles.Get(x, y);
    public BeltLine GetLine(BeltLineId id) => _pool.Get(id);

    // 放置一格朝 direction 的传送带,返回它最终所属的线。
    // 前置条件:(x, y) 未被任何传送带线占用(见 Global Constraints)。
    public BeltLineId AddBelt(int x, int y, byte direction)
    {
        var t = new BeltLine(direction,
            new List<(int X, int Y)> { (x, y) },
            new BeltLane(BeltLine.TileSubTiles),
            new BeltLane(BeltLine.TileSubTiles));
        var tid = _pool.Create(t);
        _tiles.Set(x, y, tid);

        // Task 4/5 在此接合并分支。当前:无合并,单格线即结果。
        return tid;
    }

    // 规范序列化(spec 铁律 4)。先写分配器簿记,再按池索引序写每条存活线的
    // 内容:Direction、Tiles(数量 + 每格坐标)、两条 lane 的 WriteState。
    // 与 Simulation.WriteState 写 Entities 的模式一致。
    public void WriteState(IStateWriter writer)
    {
        _pool.WriteState(writer);
        for (int i = 0; i < _pool.Capacity; i++)
        {
            if (!_pool.IsAliveAtIndex(i)) continue;
            var line = _pool.GetAtIndex(i);
            writer.Write(i);
            writer.Write(_pool.GenerationAtIndex(i));
            writer.Write(line.Direction);
            writer.Write(line.Tiles.Count);
            foreach (var (tx, ty) in line.Tiles)
            {
                writer.Write(tx);
                writer.Write(ty);
            }
            line.LaneA.WriteState(writer);
            line.LaneB.WriteState(writer);
        }
    }

    // 方向 -> 单位位移。屏幕坐标(y 向下):北 = -y,南 = +y。
    private static (int dx, int dy) Delta(byte d) => d switch
    {
        0 => (0, -1),
        1 => (1, 0),
        2 => (0, 1),
        3 => (-1, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(d)),
    };
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltNetworkTests"`
Expected: PASS(6 个)。`Delta` 目前未被调用会触发编译器未使用警告吗?——`private static` 方法未使用**不报警告**(CS 只对未使用的私有字段/局部变量报警);保留它,Task 4 会用。若你的构建把警告当错误且确实拦下,把 `Delta` 挪到 Task 4 Step 3 再加。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/BeltNetwork.cs sim/Faketorio.Sim.Tests/BeltNetworkTests.cs
git commit -m "feat(sim): BeltNetwork skeleton — single-tile AddBelt + WriteState

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

### Task 4: `AddBelt` 单侧合并(接出口端 / 接入口端)+ 物品保持 + 负例

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltNetwork.cs`(在 `AddBelt` 里 `return tid;` 之前接合并分支)
- Test: `sim/Faketorio.Sim.Tests/BeltNetworkTests.cs`(追加)

**Interfaces:**
- Consumes: Task 3 的 `BeltNetwork`;既有 `BeltLane.ExtendBack(int)` / `BeltLane.ExtendFront(int)`(Plan 3a);`BeltLine.Tiles` / `.Direction` / `.LengthSubTiles`。
- Produces: `AddBelt` 新增行为——放置格 T 的上游邻格 `B = (x,y) - delta(D)`、下游邻格 `F = (x,y) + delta(D)`:
  - `B` 上有存活线 `L` 且 `L.Direction == D` 且 `L.Tiles[0] == B` ⇒ **只命中上游**:`L.LaneA/LaneB` 各 `ExtendFront(256)`,`L.Tiles.Insert(0, (x,y))`,tile 索引里 `(x,y)` 改指向该 `L` 的 id,销毁 T 临时线,返回 `L` 的 id。
  - `F` 上有存活线 `L` 且 `L.Direction == D` 且 `L.Tiles[^1] == F` ⇒ **只命中下游**:`L.LaneA/LaneB` 各 `ExtendBack(256)`,`L.Tiles.Add((x,y))`,tile 索引里 `(x,y)` 改指向 `L` 的 id,销毁 T 临时线,返回 `L` 的 id。
  - 两侧都命中 ⇒ 留给 Task 5(本任务保持 `return tid;`)。
  - 两侧都不命中 ⇒ 返回 `tid`(单格线)。

- [ ] **Step 1: 写失败测试**

追加到 `BeltNetworkTests.cs`:

```csharp
// 顺着 E 方向从出口往入口一格格建:每次新格的下游邻格正好是已建线的入口端。
private static (BeltNetwork net, BeltLineId id) BuildEastLine(params (int x, int y)[] tilesFrontToBack)
{
    var net = new BeltNetwork();
    var id = net.AddBelt(tilesFrontToBack[0].x, tilesFrontToBack[0].y, E);
    for (int i = 1; i < tilesFrontToBack.Length; i++)
        id = net.AddBelt(tilesFrontToBack[i].x, tilesFrontToBack[i].y, E);
    return (net, id);
}

[Fact]
public void AddBelt_BehindEntry_MergesOntoEntryEnd()
{
    var net = new BeltNetwork();
    var id = net.AddBelt(5, 3, E);      // 线 = [(5,3)]
    var id2 = net.AddBelt(4, 3, E);     // (4,3) 的下游邻格 (5,3) 是线的入口端

    Assert.Equal(id, id2);
    Assert.Single(LiveLines(net));
    var line = net.GetLine(id);
    Assert.Equal(new[] { (5, 3), (4, 3) }, line.Tiles);
    Assert.Equal(512, line.LengthSubTiles);
    Assert.Equal(id, net.GetLineAt(4, 3));
    Assert.Equal(id, net.GetLineAt(5, 3));
}

[Fact]
public void AddBelt_AheadOfExit_MergesOntoExitEnd()
{
    var net = new BeltNetwork();
    var id = net.AddBelt(5, 3, E);      // 线 = [(5,3)]
    var id2 = net.AddBelt(6, 3, E);     // (6,3) 的上游邻格 (5,3) 是线的出口端

    Assert.Equal(id, id2);
    Assert.Single(LiveLines(net));
    var line = net.GetLine(id);
    Assert.Equal(new[] { (6, 3), (5, 3) }, line.Tiles); // 新格成为新的出口
    Assert.Equal(512, line.LengthSubTiles);
    Assert.Equal(id, net.GetLineAt(6, 3));
}

[Fact]
public void Build3TileLine_ByRepeatedEntryEndMerge()
{
    var (net, id) = BuildEastLine((5, 3), (4, 3), (3, 3));
    Assert.Single(LiveLines(net));
    var line = net.GetLine(id);
    Assert.Equal(new[] { (5, 3), (4, 3), (3, 3) }, line.Tiles);
    Assert.Equal(768, line.LengthSubTiles);
    foreach (var (tx, ty) in line.Tiles)
        Assert.Equal(id, net.GetLineAt(tx, ty));
}

[Fact]
public void MergeOntoEntryEnd_PreservesItemPositions()
{
    var net = new BeltNetwork();
    var id = net.AddBelt(5, 3, E);
    net.GetLine(id).LaneA.TryInsertAtBack();          // LaneA gaps = [192]
    net.AddBelt(4, 3, E);                              // ExtendBack:不动物品
    Assert.Equal(new[] { 192 }, net.GetLine(id).LaneA.Gaps);
    Assert.Equal(new[] { 192 }, net.GetLine(id).LaneA.ToAbsolutePositions());
    Assert.Equal(512, net.GetLine(id).LengthSubTiles);
}

[Fact]
public void MergeOntoExitEnd_ShiftsItemAwayFromNewExit()
{
    var net = new BeltNetwork();
    var id = net.AddBelt(5, 3, E);
    net.GetLine(id).LaneA.TryInsertAtBack();          // gaps = [192]
    net.AddBelt(6, 3, E);                              // ExtendFront:gaps[0] += 256
    Assert.Equal(new[] { 448 }, net.GetLine(id).LaneA.Gaps);
    Assert.Equal(new[] { 448 }, net.GetLine(id).LaneA.ToAbsolutePositions());
}

[Fact]
public void WrongDirectionNeighbour_DoesNotMerge()
{
    var net = new BeltNetwork();
    net.AddBelt(5, 3, E);
    net.AddBelt(6, 3, S);   // 上游邻格是 (5,3),但方向不同
    Assert.Equal(2, LiveLines(net).Count);
}

[Fact]
public void PerpendicularNeighbour_DoesNotMerge()
{
    var (net, _) = BuildEastLine((5, 3), (4, 3), (3, 3));
    net.AddBelt(4, 2, E);   // 上/下游邻格 (5,2)/(3,2) 都不是那条线的端点
    Assert.Equal(2, LiveLines(net).Count);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltNetworkTests"`
Expected: 新增的合并测试 FAIL（当前 `AddBelt` 从不合并，`LiveLines` 数量、`Tiles`、`LengthSubTiles` 断言全部不符）。

- [ ] **Step 3: 实现**

`BeltNetwork.AddBelt` 里把 `// Task 4/5 在此接合并分支...` 那两行替换为:

```csharp
        var (dx, dy) = Delta(direction);
        var back = (x - dx, y - dy);    // B:T 的上游邻格
        var front = (x + dx, y + dy);   // F:T 的下游邻格

        var bId = _tiles.Get(back.Item1, back.Item2);
        var fId = _tiles.Get(front.Item1, front.Item2);

        bool bHit = bId.IsValid && _pool.IsAlive(bId)
            && _pool.Get(bId).Direction == direction
            && _pool.Get(bId).Tiles[0] == back;
        bool fHit = fId.IsValid && _pool.IsAlive(fId)
            && _pool.Get(fId).Direction == direction
            && _pool.Get(fId).Tiles[^1] == front;

        if (bHit && fHit)
            return tid; // Task 5:三路合并

        if (bHit)
        {
            var l = _pool.Get(bId);
            l.LaneA.ExtendFront(BeltLine.TileSubTiles);
            l.LaneB.ExtendFront(BeltLine.TileSubTiles);
            l.Tiles.Insert(0, (x, y));
            _tiles.Set(x, y, bId);
            _pool.Destroy(tid);
            return bId;
        }
        if (fHit)
        {
            var l = _pool.Get(fId);
            l.LaneA.ExtendBack(BeltLine.TileSubTiles);
            l.LaneB.ExtendBack(BeltLine.TileSubTiles);
            l.Tiles.Add((x, y));
            _tiles.Set(x, y, fId);
            _pool.Destroy(tid);
            return fId;
        }

        return tid;
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltNetworkTests"`
然后 `dotnet test`
Expected: PASS(全部 BeltNetworkTests + 全量套件绿)。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/BeltNetwork.cs sim/Faketorio.Sim.Tests/BeltNetworkTests.cs
git commit -m "feat(sim): BeltNetwork.AddBelt single-side merge — extend line at endpoint

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

### Task 5: `AddBelt` 三路合并(拼接两条带物品的线)

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltNetwork.cs`(把 Task 4 里 `if (bHit && fHit) return tid;` 换成真正的三路合并;新增私有 `ConcatLanes`)
- Test: `sim/Faketorio.Sim.Tests/BeltNetworkTests.cs`(追加)

**Interfaces:**
- Consumes: Task 4 的 `AddBelt`;既有 `BeltLane.ToAbsolutePositions()` / `BeltLane.FromAbsolutePositions(int, IReadOnlyList<int>)` / `BeltLane.Count`(Plan 3a);`BeltLine.LengthSubTiles`。
- Produces: `AddBelt` 中 `bHit && fHit` 分支——`L_up = _pool.Get(bId)`(B 侧,上游),`L_down = _pool.Get(fId)`(F 侧,下游);沿流向 `L_up → T → L_down`。
  - `combinedLen = L_up.LengthSubTiles + 256 + L_down.LengthSubTiles`。
  - 对 A、B 两侧:`newLane = ConcatLanes(L_up.LaneX, L_down.LaneX, L_down.LengthSubTiles, combinedLen)`。
  - `newTiles = L_down.Tiles ++ [(x,y)] ++ L_up.Tiles`;`merged = new BeltLine(direction, newTiles, newA, newB)`;`newId = _pool.Create(merged)`。
  - tile 索引里 `newTiles` 的**每一格**改指向 `newId`;`_pool.Destroy(bId)` / `_pool.Destroy(fId)` / `_pool.Destroy(tid)`;返回 `newId`。
  - `private static BeltLane ConcatLanes(BeltLane up, BeltLane down, int downLen, int combinedLen)`：`down` 的前沿绝对距离原样;`up` 的每项 `+ (256 + downLen)`;拼成一个升序列表交给 `FromAbsolutePositions`。

- [ ] **Step 1: 写失败测试**

追加到 `BeltNetworkTests.cs`:

```csharp
// 建一条 E 向线,tiles 前到后给出。第一格是出口。
private static (BeltNetwork net, BeltLineId id) EastLineOn(BeltNetwork net, params (int x, int y)[] frontToBack)
{
    var id = net.AddBelt(frontToBack[0].x, frontToBack[0].y, E);
    for (int i = 1; i < frontToBack.Length; i++)
        id = net.AddBelt(frontToBack[i].x, frontToBack[i].y, E);
    return (net, id);
}

[Fact]
public void AddBelt_FillsOneTileGapBetweenTwoLines_MergesAllThree()
{
    var net = new BeltNetwork();
    EastLineOn(net, (6, 3), (5, 3));           // L_down:出口 (6,3),入口 (5,3)
    EastLineOn(net, (3, 3), (2, 3));           // L_up:  出口 (3,3),入口 (2,3)

    var id = net.AddBelt(4, 3, E);             // 补空隙:上游邻格 (3,3)=L_up 出口,下游邻格 (5,3)=L_down 入口

    Assert.Single(LiveLines(net));
    var line = net.GetLine(id);
    Assert.Equal(new[] { (6, 3), (5, 3), (4, 3), (3, 3), (2, 3) }, line.Tiles);
    Assert.Equal(1280, line.LengthSubTiles);
    foreach (var (tx, ty) in line.Tiles)
        Assert.Equal(id, net.GetLineAt(tx, ty));
}

[Fact]
public void ThreeWayMerge_ConcatenatesItemsAtCorrectAbsolutePositions()
{
    var net = new BeltNetwork();
    var (_, downId) = EastLineOn(net, (6, 3), (5, 3));   // 长 512
    var (_, upId)   = EastLineOn(net, (3, 3), (2, 3));   // 长 512

    net.GetLine(downId).LaneA.TryInsertAtBack();          // L_down.LaneA gaps=[448] -> abs [448]
    net.GetLine(upId).LaneA.TryInsertAtBack();            // L_up.LaneA   gaps=[448] -> abs [448]

    var id = net.AddBelt(4, 3, E);
    var lane = net.GetLine(id).LaneA;

    // down 原样 [448];up 后移 256 + 512 = 768 -> [1216]
    Assert.Equal(new[] { 448, 1216 }, lane.ToAbsolutePositions());
    Assert.Equal(new[] { 448, 704 }, lane.Gaps);          // 448, 1216-448-64
    Assert.Equal(1280, net.GetLine(id).LengthSubTiles);
}

[Fact]
public void ThreeWayMerge_ReleasesAllThreeOldSlots()
{
    var net = new BeltNetwork();
    var (_, downId) = EastLineOn(net, (6, 3), (5, 3));
    var (_, upId)   = EastLineOn(net, (3, 3), (2, 3));
    var id = net.AddBelt(4, 3, E);

    Assert.False(net.GetLineAt(4, 3).Equals(BeltLineId.Invalid)); // 新线有效
    Assert.Single(LiveLines(net));
    Assert.Equal(id, net.GetLineAt(2, 3));
    Assert.Equal(id, net.GetLineAt(6, 3));
    Assert.NotEqual(id, downId);
    Assert.NotEqual(id, upId);
}

[Fact]
public void ThreeWayMerge_Deterministic()
{
    BeltNetwork Build()
    {
        var n = new BeltNetwork();
        EastLineOn(n, (6, 3), (5, 3));
        EastLineOn(n, (3, 3), (2, 3));
        n.AddBelt(4, 3, E);
        return n;
    }
    Assert.Equal(Hash(Build()), Hash(Build()));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltNetworkTests.ThreeWay"` 加 `dotnet test --filter "FullyQualifiedName~BeltNetworkTests.AddBelt_FillsOneTileGap"`
Expected: FAIL —— 当前 `bHit && fHit` 分支只 `return tid`,合并没发生(`LiveLines` 是 3 不是 1,`Tiles` / abs 位置断言不符)。

- [ ] **Step 3: 实现**

`BeltNetwork` 里把 Task 4 的

```csharp
        if (bHit && fHit)
            return tid; // Task 5:三路合并
```

替换为:

```csharp
        if (bHit && fHit)
        {
            var lUp = _pool.Get(bId);
            var lDown = _pool.Get(fId);
            int combinedLen = lUp.LengthSubTiles + BeltLine.TileSubTiles + lDown.LengthSubTiles;

            var newA = ConcatLanes(lUp.LaneA, lDown.LaneA, lDown.LengthSubTiles, combinedLen);
            var newB = ConcatLanes(lUp.LaneB, lDown.LaneB, lDown.LengthSubTiles, combinedLen);

            var newTiles = new List<(int X, int Y)>(lDown.Tiles.Count + 1 + lUp.Tiles.Count);
            newTiles.AddRange(lDown.Tiles);
            newTiles.Add((x, y));
            newTiles.AddRange(lUp.Tiles);

            var merged = new BeltLine(direction, newTiles, newA, newB);
            var newId = _pool.Create(merged);
            foreach (var (tx, ty) in newTiles)
                _tiles.Set(tx, ty, newId);

            _pool.Destroy(bId);
            _pool.Destroy(fId);
            _pool.Destroy(tid);
            return newId;
        }
```

并在 `Delta` 上方新增私有方法:

```csharp
    // 把 up(上游,拼在物理后侧)与 down(下游,拼在出口侧)两条带物品的
    // lane 拼成一条长 combinedLen 的新 lane。前沿绝对距离:down 的原样,
    // up 的每项整体后移 (256 + downLen)——越过新格 T 和整条 down。
    // 拼出的列表天然升序(up 最靠前项移位后仍远在 down 最靠后项之后),
    // 直接交给 FromAbsolutePositions。
    private static BeltLane ConcatLanes(BeltLane up, BeltLane down, int downLen, int combinedLen)
    {
        var pos = new List<int>(down.Count + up.Count);
        pos.AddRange(down.ToAbsolutePositions());
        int shift = BeltLine.TileSubTiles + downLen;
        foreach (int p in up.ToAbsolutePositions())
            pos.Add(p + shift);
        return BeltLane.FromAbsolutePositions(combinedLen, pos);
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltNetworkTests"`
然后 `dotnet test`
Expected: PASS（全部 BeltNetworkTests + 全量套件绿）。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/BeltNetwork.cs sim/Faketorio.Sim.Tests/BeltNetworkTests.cs
git commit -m "feat(sim): BeltNetwork.AddBelt three-way merge — splice two item-bearing lines

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01T6L1wPVq8yfAnpYU3Pd9n3"
```

---

## Self-Review

**Spec coverage(设计文档 §4 / §5 / §8 / §11 Plan 3b 条目):**

| 要求 | 任务 |
|---|---|
| `BeltLineId(Index, Generation)` + `Invalid` | Task 1 |
| `BeltLinePool`(引用类型代数池,`EntityPool<T>` 不改) | Task 1 |
| `BeltLine`:`Direction` / `Tiles`(前到后,`Tiles[0]` 出口) / `LaneA` / `LaneB`;共线直线不变式;`len = Tiles.Count × 256` | Task 1(`LengthSubTiles`) |
| `BeltNetwork` 持池 + `tile → BeltLineId` 分块索引 | Task 2(索引)+ Task 3(容器) |
| `AddBelt` 先登记 T 单格线 | Task 3 |
| 精确端点判据:方向相等 + 邻格坐标 == `Tiles[0]` / `Tiles[^1]`;不查 prototype;贴中段不连 | Task 4(`bHit`/`fHit` + 负例测试) |
| 单侧合并:`ExtendFront`/`ExtendBack` + `Tiles` 拼接 + **tile 索引重指向 T 那格** + 释放 T 临时线 | Task 4 |
| 三路合并:`ToAbsolutePositions`/`FromAbsolutePositions` 拼接 + `Tiles = L_down ++ [T] ++ L_up` + **tile 索引重指向每一格** + 释放 3 个旧槽位 | Task 5 |
| 自环几何不可能 ⇒ 无该分支 | 隐式:测试构造不可能产生;`bHit && fHit` 里 `bId`/`fId` 必为不同 id(§5 论证),代码不特判 |
| `BeltNetwork.WriteState`:分配器簿记 + 按池索引序写 `Direction` / `Tiles` / 两 lane 的 `WriteState` | Task 3 |
| §11 显式条目:"合并后 tile 索引重指向所有并入格子 + 释放旧线槽位" + 覆盖测试 | Task 4(`net.GetLineAt(4,3)==id`、`Single(LiveLines)`)、Task 5(`foreach Tiles: GetLineAt==id`、`ReleasesAllThreeOldSlots`) |
| 不做:`Simulation` 接入 / `EntityId→线` / 拆分 / 每 tick / 速度 | 全计划不涉及 |

**Placeholder scan:** 无 TBD/TODO;每个代码步骤是完整可编译代码;每个测试都有具体断言。

**Type consistency:**
- `BeltLine(byte, List<(int X, int Y)>, BeltLane, BeltLane)` 构造签名在 Task 1 定义,Task 3(单格线)、Task 5(三路 `merged`)一致使用。
- `BeltLine.TileSubTiles`(256)在 Task 1 定义,Task 3/4/5 全程用它,不出现裸 256(测试断言里的 `256`/`512`/`768`/`1280` 是期望值,非魔法常量)。
- `BeltLinePool` 成员(`Create`/`Destroy`/`IsAlive`/`Get`/`Capacity`/`IsAliveAtIndex`/`GetAtIndex`/`GenerationAtIndex`/`WriteState`)在 Task 1 定义,Task 3 的 `BeltNetwork` 按此调用。
- `TileToLineIndex.Get`/`Set` 在 Task 2 定义,Task 3/4/5 一致使用(本计划不需要 `Clear`,留给 3c)。
- `BeltNetwork` 对外面(测试)暴露 `AddBelt(int,int,byte)` / `Capacity` / `IsAliveAtIndex` / `GetAtIndex` / `GetLineAt` / `GetLine` / `WriteState`——Task 3 定义,Task 4/5 不改签名只加分支。
- `ConcatLanes(BeltLane up, BeltLane down, int downLen, int combinedLen)` 在 Task 5 定义并调用,`downLen` 来自 `L_down.LengthSubTiles`。
- 依赖的 Plan 3a `BeltLane` 成员:`ExtendBack(int)` / `ExtendFront(int)` / `ToAbsolutePositions()` / `FromAbsolutePositions(int, IReadOnlyList<int>)` / `Count` / `Gaps` / `TryInsertAtBack()` / `WriteState(IStateWriter)` / `ItemWidthSubTiles`——均在 3a 已合并,签名对齐(`FromAbsolutePositions` 收 `IReadOnlyList<int>`,传 `List<int>` 合法)。

**三路合并数值复核**(`ThreeWayMerge_ConcatenatesItems...`):`L_down` 长 512、`LaneA` 插 1 物品 → `gaps=[512-64=448]` → abs `[448]`。`L_up` 长 512、插 1 → abs `[448]`。`combinedLen = 512 + 256 + 512 = 1280`。`shift = 256 + 512 = 768`。`pos = [448] ++ [448+768=1216] = [448, 1216]`。`FromAbsolutePositions(1280, [448,1216])`:`gaps[0]=448`;`gaps[1]=1216-(448+64)=704`;末项尾沿 `1216+64=1280 ≤ 1280` ✓。断言 `ToAbsolutePositions()==[448,1216]`、`Gaps==[448,704]` 成立。

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-09-01-m1-plan3b-beltline-merge.md`. Two execution options:

1. **Subagent-Driven(推荐)** — 每个任务派一个全新 subagent,任务之间我审查,迭代快。
2. **Inline Execution** — 在当前会话里按 executing-plans 批量执行,带检查点。

Which approach?
