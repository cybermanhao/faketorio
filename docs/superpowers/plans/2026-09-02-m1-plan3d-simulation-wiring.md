# M1 Plan 3d — 传送带接入 Simulation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把已建好的 `BeltNetwork`(合并/拆分)接进 `Simulation`——放置/拆除传送带触发 `AddBelt`/`RemoveBelt`,每 tick 在 `Simulation.Step` 里推进所有线并做拐角处的线间交接,`Simulation.WriteState` 覆盖传送带状态。配集成 + 确定性测试。

**Architecture:** 推进与线间交接**直接写在 `Simulation.Step` 里**(不新增 `BeltNetwork.Advance`)。`BeltNetwork` 只把 `Delta(byte)` 暴露成 `public static`;其余靠 3b 已有的 `Capacity`/`IsAliveAtIndex`/`GetAtIndex`/`GetLineAt`/`GetLine`。速度按线解析:私有 `Simulation.ResolveBeltSpeed(line)` 走 `line.Tiles[0]` → 实体 → `TransportBeltPrototype`。线间交接只认"前方邻格 == 下游线入口端"(`down.Tiles[^1] == front`),**不检查方向**(同向端点相邻的两条线在放置时已被合并,不会共存;交接服务的是拐角)。

**Tech Stack:** C# / .NET 8,xUnit 2.9,FNV-1a 状态哈希。

**Spec:** `docs/superpowers/specs/2026-08-28-belt-line-merge-split-design.md`(第 4 节末"取消 EntityId→线"、第 7 节"每 tick 系统更新"、第 8 节"IStateWriter + Simulation 接线"、第 11 节 Plan 3d 条目)

## Global Constraints

- 模拟层**不引用任何 Godot 类型**;**禁止 `float`/`double`** 参与状态计算——全部 `int`。
- 单位:`BeltLine.TileSubTiles` = 256;`BeltLane.ItemWidthSubTiles` = 64。方向 `byte` 0/1/2/3=北/东/南/西(`Command.Rotation` 约定);`BeltNetwork.Delta`:0→(0,-1) 1→(1,0) 2→(0,1) 3→(-1,0)。
- 确定性:`Step` 的推进与交接、`WriteState` 都按 `Belts` 池的**索引序**遍历;`tile → BeltLineId` 索引只点查。同种子 + 同命令序列必须逐 tick 哈希一致。
- **不做**:`EntityId → 线` 反查数组(第 4 节评审取消);机械臂 / 箱子 / 玩家取物;物品去向策略(`RemoveBelt` 返回的丢弃计数在 M1 直接忽略 = 丢弃,见第 10 节);多带种的精确分段速度(整条线按出口格速度跑)。
- **只改** `sim/Faketorio.Sim/Belts/BeltNetwork.cs`(`Delta` 可见性)、`sim/Faketorio.Sim/Simulation.cs`。不改 `BeltLane.cs` / `BeltLine.cs` / `EntityPool.cs` / `WorldGrid.cs`。
- 冷路径(放置/拆除)允许分配;`Step` 的推进/交接是热路径,按索引序遍历、不 LINQ、不闭包。

## File Structure

| 文件 | 本计划的改动 |
|---|---|
| `sim/Faketorio.Sim/Belts/BeltNetwork.cs` | `private static (int,int) Delta(byte)` → `public static` |
| `sim/Faketorio.Sim/Simulation.cs` | `using Faketorio.Sim.Belts;`;`public BeltNetwork Belts { get; } = new();`;`Apply` 的 `PlaceEntity`/`RemoveEntity` 加传送带钩子;`Step` 加推进 + 交接两趟;私有 `ResolveBeltSpeed`;`WriteState` 加 `Belts.WriteState(writer);` |
| `sim/Faketorio.Sim.Tests/SimulationTests.cs` | Task 1 追加(放置/拆除接线) |
| `sim/Faketorio.Sim.Tests/BeltIntegrationTests.cs`（新建） | Task 2（推进）+ Task 3（L 形、环形） |
| `sim/Faketorio.Sim.Tests/DeterminismTests.cs` | Task 3 追加(传送带场景) |

运行(工作目录 `C:\code\faketorio`,PowerShell):

- 单类:`dotnet test --filter "FullyQualifiedName~SimulationTests"`(换 `BeltIntegrationTests` / `DeterminismTests`)
- 全量:`dotnet test`

Plan 3c 合并后基线 `dotnet test` = 140。`data/base` 里已有 `transport-belt-basic`(`speedSubTilesPerTick: 8`,整除 64)。已有测试模式:`SimulationTests.NewSim()`、`sim.Prototypes.Get<T>("name").Id`、`sim.Prototypes.GetById(int) → PrototypeBase`。`DeterminismTests` 已 `using Faketorio.Sim.Commands;` 和 `Faketorio.Sim.Prototypes;`。

---

### Task 1: `BeltNetwork.Delta` public + `Simulation.Belts` + `Apply` 放置/拆除接线

**Files:**
- Modify: `sim/Faketorio.Sim/Belts/BeltNetwork.cs`（`Delta` 改 `public static`）
- Modify: `sim/Faketorio.Sim/Simulation.cs`（usings、`Belts` 属性、`Apply` 两个分支）
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`（追加）

**Interfaces:**
- Consumes: 既有 `BeltNetwork`（`AddBelt(int,int,byte)`、`RemoveBelt(int,int) → int`、`GetLineAt(int,int) → BeltLineId`、`GetLine(BeltLineId) → BeltLine`、`Capacity`、`IsAliveAtIndex(int)`）；`TransportBeltPrototype`（`Faketorio.Sim.Prototypes`）；`Simulation.Apply` 里既有的 `proto`（类型 `EntityPrototype`）、`command`、`data`、`id`。
- Produces:
  - `BeltNetwork.Delta(byte d) → (int dx, int dy)` 改为 `public static`（其它一切不变）。
  - `Simulation.Belts` —— `public BeltNetwork Belts { get; } = new();`。
  - `Apply` / `PlaceEntity`:`World.OccupyArea(...)` 之后,`if (proto is TransportBeltPrototype) Belts.AddBelt(command.X, command.Y, command.Rotation);`。
  - `Apply` / `RemoveEntity`:在 `Entities.Destroy(id)` **之前**存 `int bx = data.X, by = data.Y; bool isBelt = proto is TransportBeltPrototype;`;`World.ClearArea` + `Entities.Destroy` 照跑;之后 `if (isBelt) Belts.RemoveBelt(bx, by);`。

- [ ] **Step 1: 写失败测试**

追加到 `SimulationTests.cs`(文件顶部 usings 加 `using Faketorio.Sim.Belts;`;`Faketorio.Sim.Prototypes` 已在):

```csharp
    private const byte E = 1;

    private static Command PlaceBelt(Simulation sim, int x, int y, byte rot) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id,
        X = x, Y = y, Rotation = rot,
    };

    private static int LiveLineCount(BeltNetwork b)
    {
        int c = 0;
        for (int i = 0; i < b.Capacity; i++) if (b.IsAliveAtIndex(i)) c++;
        return c;
    }

    [Fact]
    public void PlaceBelt_RegistersLineInNetwork()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 5, E));
        sim.Step();
        var id = sim.Belts.GetLineAt(5, 5);
        Assert.True(id.IsValid);
        Assert.Equal(new[] { (5, 5) }, sim.Belts.GetLine(id).Tiles);
    }

    [Fact]
    public void PlaceTwoAdjacentSameDirBelts_MergeToOneLine()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 5, E));
        sim.Step();
        sim.Submit(PlaceBelt(sim, 6, 5, E));
        sim.Step();
        Assert.Equal(1, LiveLineCount(sim.Belts));
        var id = sim.Belts.GetLineAt(5, 5);
        Assert.Equal(new[] { (6, 5), (5, 5) }, sim.Belts.GetLine(id).Tiles);
    }

    [Fact]
    public void RemoveMiddleBelt_SplitsNetworkLine()
    {
        var sim = NewSim();
        foreach (var x in new[] { 5, 6, 7 }) { sim.Submit(PlaceBelt(sim, x, 5, E)); sim.Step(); }
        Assert.Equal(1, LiveLineCount(sim.Belts));

        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 6, Y = 5 });
        sim.Step();

        Assert.Equal(2, LiveLineCount(sim.Belts));
        Assert.False(sim.Belts.GetLineAt(6, 5).IsValid);
        Assert.False(sim.World.GetEntityAt(6, 5).IsValid);
    }

    [Fact]
    public void RemoveNonBeltEntity_DoesNotTouchNetwork()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 0, 0));
        sim.Step();
        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 0, Y = 0 });
        sim.Step(); // isBelt 守卫:不得抛
        Assert.Equal(0, LiveLineCount(sim.Belts));
        Assert.Equal(0, sim.RejectedCommandCount);
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~SimulationTests.PlaceBelt"` 加 `dotnet test --filter "FullyQualifiedName~SimulationTests.RemoveMiddleBelt"`
Expected: 编译失败 —— `Simulation` 无 `Belts` 属性。

- [ ] **Step 3: 实现**

`BeltNetwork.cs` —— 把

```csharp
    private static (int dx, int dy) Delta(byte d) => d switch
```

改成

```csharp
    public static (int dx, int dy) Delta(byte d) => d switch
```

`Simulation.cs` —— 顶部 usings 加一行:

```csharp
using Faketorio.Sim.Belts;
```

在 `public EntityPool<EntityData> Entities { get; } = new();` 下面加:

```csharp
    public BeltNetwork Belts { get; } = new();
```

`Apply` 的 `PlaceEntity` 分支,`World.OccupyArea(command.X, command.Y, proto.TileWidth, proto.TileHeight, id);` 之后、`return;` 之前加:

```csharp
                if (proto is TransportBeltPrototype)
                    Belts.AddBelt(command.X, command.Y, command.Rotation);
```

`Apply` 的 `RemoveEntity` 分支,把

```csharp
                World.ClearArea(data.X, data.Y, proto.TileWidth, proto.TileHeight);
                Entities.Destroy(id);
                return;
```

换成

```csharp
                int bx = data.X, by = data.Y;
                bool isBelt = proto is TransportBeltPrototype;
                World.ClearArea(data.X, data.Y, proto.TileWidth, proto.TileHeight);
                Entities.Destroy(id);
                if (isBelt)
                    Belts.RemoveBelt(bx, by);
                return;
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~SimulationTests"` 然后 `dotnet test`
Expected: PASS(新增 4 个 + 全量绿）。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Belts/BeltNetwork.cs sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "feat(sim): wire BeltNetwork into Simulation — place/remove belt hooks; Delta public

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01KnrfQy6r2okVbSP4xNNT8M"
```

---

### Task 2: `Simulation.Step` 推进趟 + `ResolveBeltSpeed`

**Files:**
- Modify: `sim/Faketorio.Sim/Simulation.cs`（`Step` 加推进循环;新增私有 `ResolveBeltSpeed`）
- Test: `sim/Faketorio.Sim.Tests/BeltIntegrationTests.cs`（新建）

**Interfaces:**
- Consumes: Task 1 的 `Simulation.Belts`;既有 `BeltLine.Tiles`（`List<(int X,int Y)>`）/ `LaneA` / `LaneB`;`BeltLane.Advance(int)`;`World.GetEntityAt(int,int)`;`Entities.Get(EntityId)`;`Prototypes.GetById(int) → PrototypeBase`;`TransportBeltPrototype.SpeedSubTilesPerTick`。
- Produces:
  - `Simulation.Step()` 在 `Apply` 循环之后、`Tick++` 之前,按 `Belts` 池索引序对每条存活线 `line.LaneA.Advance(speed); line.LaneB.Advance(speed);`,`speed = ResolveBeltSpeed(line)`。
  - `private int ResolveBeltSpeed(BeltLine line)` —— `line.Tiles[0]` 解构成 `(ex, ey)`,`World.GetEntityAt(ex, ey)` 得 `eid`,`Entities.Get(eid).ProtoId` 得 proto id,`(TransportBeltPrototype)Prototypes.GetById(id)` 取 `SpeedSubTilesPerTick`。严格,靠不变式(出口格上一定是传送带实体),不做 fallback。

- [ ] **Step 1: 写失败测试**

新建 `sim/Faketorio.Sim.Tests/BeltIntegrationTests.cs`:

```csharp
using Faketorio.Sim.Belts;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Sim.Tests;

public class BeltIntegrationTests
{
    private const byte N = 0, E = 1, S = 2, W = 3;

    private static Simulation NewSim() => new(PrototypeLoader.LoadFromDirectory("data/base"));

    // 放一格传送带并 Step 一次(命令在下一 tick 应用)。
    private static void PlaceBelt(Simulation sim, int x, int y, byte rot)
    {
        sim.Submit(new Command
        {
            Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id,
            X = x, Y = y, Rotation = rot,
        });
        sim.Step();
    }

    private static BeltLine LineAt(Simulation sim, int x, int y)
        => sim.Belts.GetLine(sim.Belts.GetLineAt(x, y));

    [Fact]
    public void SingleBelt_ItemAdvancesAtBeltSpeed()
    {
        var sim = NewSim();
        PlaceBelt(sim, 0, 0, E);
        LineAt(sim, 0, 0).LaneA.TryInsertAtBack(); // gaps=[192]
        sim.Step();
        Assert.Equal(new[] { 184 }, LineAt(sim, 0, 0).LaneA.Gaps); // speed 8: 192-8
        sim.Step();
        Assert.Equal(new[] { 176 }, LineAt(sim, 0, 0).LaneA.Gaps);
    }

    [Fact]
    public void Belt_ItemReachesExitAndStops_NoDownstream()
    {
        var sim = NewSim();
        PlaceBelt(sim, 0, 0, E);
        LineAt(sim, 0, 0).LaneA.TryInsertAtBack(); // gaps=[192]
        for (int t = 0; t < 24; t++) sim.Step(); // 192 / 8 = 24
        Assert.True(LineAt(sim, 0, 0).LaneA.IsFrontReady);
        Assert.Equal(new[] { 0 }, LineAt(sim, 0, 0).LaneA.Gaps);
        sim.Step(); // 无下游线,原地不动
        Assert.Equal(new[] { 0 }, LineAt(sim, 0, 0).LaneA.Gaps);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltIntegrationTests"`
Expected: FAIL —— `Step` 还没推进传送带,物品的 `Gaps` 停在 `[192]`。

- [ ] **Step 3: 实现**

`Simulation.cs` `Step()`,把注释

```csharp
        // 后续计划在此追加系统更新(传送带、机器、电网……)
```

换成

```csharp
        // 传送带:推进(按 Belts 池索引序,确定)
        for (int bi = 0; bi < Belts.Capacity; bi++)
        {
            if (!Belts.IsAliveAtIndex(bi)) continue;
            var line = Belts.GetAtIndex(bi);
            int speed = ResolveBeltSpeed(line);
            line.LaneA.Advance(speed);
            line.LaneB.Advance(speed);
        }
```

在 `Apply` 方法之前(或 `WriteState` 之后)加私有方法:

```csharp
    // 一条线按其出口格(Tiles[0])的传送带 prototype 速度跑(设计文档第 7 节)。
    // 严格:出口格上一定是传送带实体,不做 fallback。
    private int ResolveBeltSpeed(BeltLine line)
    {
        var (ex, ey) = line.Tiles[0];
        var eid = World.GetEntityAt(ex, ey);
        ref var d = ref Entities.Get(eid);
        return ((TransportBeltPrototype)Prototypes.GetById(d.ProtoId)).SpeedSubTilesPerTick;
    }
```

注:`Simulation.cs` 顶部在 Task 1 已加 `using Faketorio.Sim.Belts;`,`BeltLine` 直接可用。命名空间 `Faketorio.Sim.Belts` 的最后一段与属性 `Belts` 同名,但 C# 里"命名空间片段 vs 成员名"在类型位置不会冲突(类型解析走 `using`),`ResolveBeltSpeed` 里也不引用 `Belts` 属性。若某个编译器版本仍报解析歧义,把参数类型写成全限定 `Faketorio.Sim.Belts.BeltLine`。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltIntegrationTests"` 然后 `dotnet test`
Expected: PASS(新增 2 个 + 全量绿;既有 `DeterminismTests` 仍绿——空 `Belts` 的推进循环 0 次迭代)。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/BeltIntegrationTests.cs
git commit -m "feat(sim): Simulation.Step advances belt lines; ResolveBeltSpeed from exit tile

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01KnrfQy6r2okVbSP4xNNT8M"
```

---

### Task 3: `Simulation.Step` 线间交接趟 + `WriteState` 覆盖 + L 形/环形/确定性测试

**Files:**
- Modify: `sim/Faketorio.Sim/Simulation.cs`（`Step` 加交接循环;`WriteState` 加 `Belts.WriteState`）
- Test: `sim/Faketorio.Sim.Tests/BeltIntegrationTests.cs`（追加 L 形 + 环形）、`sim/Faketorio.Sim.Tests/DeterminismTests.cs`（追加传送带场景）

**Interfaces:**
- Consumes: Task 2 的 `Step` 推进趟;`BeltNetwork.Delta(byte) → (int,int)`（Task 1 公开）;`BeltLine.Direction` / `Tiles` / `LaneA` / `LaneB`;`BeltLane.IsFrontReady` / `TryInsertAtBack()` / `RemoveFront()`;`BeltNetwork.WriteState(IStateWriter)`（3b 已实现）;`Simulation.ComputeStateHash()`。
- Produces:
  - `Simulation.Step()` 在推进趟之后、`Tick++` 之前,按 `Belts` 池索引序:对每条存活线,`front = Tiles[0] + BeltNetwork.Delta(line.Direction)`;`downId = Belts.GetLineAt(front)`;若 `downId.IsValid` 且 `Belts.GetLine(downId).Tiles[^1] == front`,则 `while (line.LaneA.IsFrontReady && down.LaneA.TryInsertAtBack()) line.LaneA.RemoveFront();`(LaneB 同)。**不检查 `down.Direction`**。
  - `Simulation.WriteState(IStateWriter writer)` 在 `WorldGrid` 那段之后加 `Belts.WriteState(writer);`。

- [ ] **Step 1: 写失败测试**

追加到 `BeltIntegrationTests.cs`:

```csharp
    private static int LiveLineCount(Simulation sim)
    {
        int c = 0;
        for (int i = 0; i < sim.Belts.Capacity; i++) if (sim.Belts.IsAliveAtIndex(i)) c++;
        return c;
    }

    [Fact]
    public void LShapeCorner_ItemFlowsFromEastLineToSouthLine()
    {
        var sim = NewSim();
        // 东向线 A:(0,0)(1,0)(2,0),出口 (2,0),长 768
        PlaceBelt(sim, 0, 0, E); PlaceBelt(sim, 1, 0, E); PlaceBelt(sim, 2, 0, E);
        // 南向线 B:(3,0)(3,1)(3,2),入口 Tiles[^1]=(3,0),出口 (3,2),长 768
        PlaceBelt(sim, 3, 0, S); PlaceBelt(sim, 3, 1, S); PlaceBelt(sim, 3, 2, S);
        Assert.Equal(2, LiveLineCount(sim)); // 方向不同,两条独立线

        LineAt(sim, 0, 0).LaneA.TryInsertAtBack(); // 放在 A 入口

        // A 上走 704 (=3*256-64) 亚格,过拐角,再在 B 上走 704;每 tick 8;留足余量
        for (int t = 0; t < 704 / 8 + 704 / 8 + 20; t++) sim.Step();

        Assert.Equal(0, LineAt(sim, 0, 0).LaneA.Count);  // A 已空
        Assert.Equal(1, LineAt(sim, 3, 2).LaneA.Count);  // 物品到了 B
    }

    [Fact]
    public void FourTileLoop_ItemKeepsCirculating()
    {
        var sim = NewSim();
        PlaceBelt(sim, 0, 0, E);
        PlaceBelt(sim, 1, 0, S);
        PlaceBelt(sim, 1, 1, W);
        PlaceBelt(sim, 0, 1, N);
        Assert.Equal(4, LiveLineCount(sim)); // 四格四向,互不合并

        LineAt(sim, 0, 0).LaneA.TryInsertAtBack();
        for (int t = 0; t < 300; t++) sim.Step(); // 一圈 ≈ 4*24 tick,跑多圈

        int total = LineAt(sim, 0, 0).LaneA.Count + LineAt(sim, 1, 0).LaneA.Count
                  + LineAt(sim, 1, 1).LaneA.Count + LineAt(sim, 0, 1).LaneA.Count;
        Assert.Equal(1, total); // 物品一直在环里,不多不少
    }

    [Fact]
    public void FourTileLoop_PackedFull_Freezes()
    {
        var sim = NewSim();
        PlaceBelt(sim, 0, 0, E);
        PlaceBelt(sim, 1, 0, S);
        PlaceBelt(sim, 1, 1, W);
        PlaceBelt(sim, 0, 1, N);

        foreach (var (x, y) in new[] { (0, 0), (1, 0), (1, 1), (0, 1) })
            while (LineAt(sim, x, y).LaneA.TryInsertAtBack()) { } // 每条线塞满 (256/64=4)

        for (int t = 0; t < 40; t++) sim.Step(); // 压到出口
        var frozen = sim.ComputeStateHash();
        for (int t = 0; t < 40; t++) sim.Step();
        Assert.Equal(frozen, sim.ComputeStateHash()); // 塞满 ⇒ 不再变化
    }
```

追加到 `DeterminismTests.cs`(顶部 usings 已含 `Commands` 与 `Prototypes`):

```csharp
    private static List<ulong> RunBeltScenario()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"));
        int belt = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id;
        var hashes = new List<ulong>();
        for (int t = 0; t < 60; t++)
        {
            if (t < 6) // 放一串 6 格东向带 (0,0)..(5,0)
                sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = belt, X = t, Y = 0, Rotation = 1 });
            if (t == 10) // 中间拆一格 -> 拆成两条线
                sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 3, Y = 0 });
            if (t == 20) // 补回 (3,0) -> 三路合并回一条
                sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = belt, X = 3, Y = 0, Rotation = 1 });
            sim.Step();
            hashes.Add(sim.ComputeStateHash());
        }
        return hashes;
    }

    [Fact]
    public void BeltScenario_SameCommands_SameHashEveryTick()
    {
        Assert.Equal(RunBeltScenario(), RunBeltScenario());
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter "FullyQualifiedName~BeltIntegrationTests.LShapeCorner"` 加 `dotnet test --filter "FullyQualifiedName~BeltIntegrationTests.FourTileLoop"`
Expected: FAIL —— 还没有交接趟,物品堵在 A 的出口,进不了 B(`L 形` 断言 `A.Count==0` 失败);环形物品第一次到某段出口就卡住(`环里总数` 仍是 1 但位置不动——实际 `FourTileLoop_ItemKeepsCirculating` 可能"碰巧"通过,`LShapeCorner` 一定失败,以它为准)。`BeltScenario` 确定性测试此时应已能过(推进是确定的),但 `WriteState` 还没覆盖传送带——两遍仍相等,不算失败;真正驱动本步的是 `LShapeCorner`。

- [ ] **Step 3: 实现**

`Simulation.cs` `Step()`,在 Task 2 加的推进循环之后、`Tick++;` 之前加:

```csharp
        // 传送带:线间交接(拐角处把出口物品传给下游线;不检查方向)
        for (int bi = 0; bi < Belts.Capacity; bi++)
        {
            if (!Belts.IsAliveAtIndex(bi)) continue;
            var line = Belts.GetAtIndex(bi);
            var (dx, dy) = BeltNetwork.Delta(line.Direction);
            var (ex, ey) = line.Tiles[0];
            int fx = ex + dx, fy = ey + dy;
            var downId = Belts.GetLineAt(fx, fy);
            if (!downId.IsValid) continue;
            var down = Belts.GetLine(downId);
            if (down.Tiles[^1] != (fx, fy)) continue;
            while (line.LaneA.IsFrontReady && down.LaneA.TryInsertAtBack()) line.LaneA.RemoveFront();
            while (line.LaneB.IsFrontReady && down.LaneB.TryInsertAtBack()) line.LaneB.RemoveFront();
        }
```

`Simulation.cs` `WriteState(...)`,在世界网格那段(`for (int k = 0; k < keys.Count; k++) { ... }`)之后加:

```csharp
        Belts.WriteState(writer);
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~BeltIntegrationTests"` 加 `dotnet test --filter "FullyQualifiedName~DeterminismTests"` 然后 `dotnet test`
Expected: PASS(新增 4 个 + 全量绿;既有 `DeterminismTests` 的箱子场景仍绿——`Belts.WriteState` 对空网络只多写两个 `0`,两遍一致)。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/BeltIntegrationTests.cs sim/Faketorio.Sim.Tests/DeterminismTests.cs
git commit -m "feat(sim): Simulation.Step line-to-line handoff; WriteState covers BeltNetwork

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01KnrfQy6r2okVbSP4xNNT8M"
```

---

## Self-Review

**Spec coverage(设计文档 §4 末 / §7 / §8 / §11 Plan 3d 条目):**

| 要求 | 任务 |
|---|---|
| 不做 `EntityId → 线` 反查 | 全计划不涉及(交接只用 `Belts.GetLineAt`) |
| `Simulation.Belts` 属性 | Task 1 |
| `Apply/PlaceEntity` 传送带 → `AddBelt(X,Y,Rotation)` | Task 1 |
| `Apply/RemoveEntity` `Destroy` 前抓 `(x,y)+isBelt`,`Destroy` 后 `RemoveBelt` | Task 1 |
| `BeltNetwork.Delta` → `public static` | Task 1 |
| `Step` 推进趟:按池索引序,每线两 lane `Advance(speed)` | Task 2 |
| `ResolveBeltSpeed`:`Tiles[0]` → 实体 → `TransportBeltPrototype.SpeedSubTilesPerTick`,私有、严格 | Task 2 |
| `Step` 交接趟:`front = Tiles[0] + Delta(Direction)`;`down.Tiles[^1] == front`;**不查方向**;`while (IsFrontReady && TryInsertAtBack) RemoveFront` | Task 3 |
| `WriteState` 追加 `Belts.WriteState`(WorldGrid 段之后) | Task 3 |
| 确定性测试(放一串带 + 中间拆 + 补回三路合并 + 多 tick,两遍 hash 全等) | Task 3 `BeltScenario_SameCommands_SameHashEveryTick` |
| L 形拐角物品交接集成测试 | Task 3 `LShapeCorner_ItemFlowsFromEastLineToSouthLine` |
| 矩形环循环 + 塞满冻结集成测试 | Task 3 `FourTileLoop_ItemKeepsCirculating` / `FourTileLoop_PackedFull_Freezes` |
| `_openIndex` 归零确定性——已由 3a `WriteState` 单测覆盖 | 不在本计划(spec §11 已注明) |

**Placeholder scan:** 无 TBD/TODO;每个代码步骤是完整可编译代码;Task 2 的 RED 说明里"L 形以它为准"是对测试预期的诚实描述,不是占位。

**Type consistency:**
- `BeltNetwork.Delta(byte) → (int dx, int dy)`:Task 1 改可见性,Task 3 调用。
- `Simulation.Belts`(`BeltNetwork`):Task 1 定义,Task 2/3 用其 `Capacity`/`IsAliveAtIndex`/`GetAtIndex`/`GetLineAt`/`GetLine`(全是 3b 的 public 成员)。
- `ResolveBeltSpeed(BeltLine) → int`:Task 2 定义并调用。
- `PlaceBelt` helper:`SimulationTests.cs` 里返回 `Command`(Task 1);`BeltIntegrationTests.cs` 里 `void` 且自带 `sim.Step()`(Task 2)——两个不同文件、不同签名,互不冲突;`LineAt`/`LiveLineCount`/`LiveLineCount(Simulation)` 都在 `BeltIntegrationTests.cs` 内。
- `E/N/S/W` 常量:`SimulationTests.cs` 里 Task 1 只加了 `E`;`BeltIntegrationTests.cs` 里 Task 2 加全 4 个。
- 依赖的既有成员:`BeltLine.Tiles/.Direction/.LaneA/.LaneB`;`BeltLane.Advance/.Gaps/.IsFrontReady/.TryInsertAtBack/.RemoveFront/.Count`;`BeltNetwork.RemoveBelt/.AddBelt/.GetLineAt/.GetLine/.Capacity/.IsAliveAtIndex/.GetAtIndex/.WriteState`;`Prototypes.Get<T>/.GetById`;`World.GetEntityAt/.ClearArea/.OccupyArea`;`Entities.Get/.Destroy`——全部已在 `main`。

**行为复核:**
- L 形不合并:(2,0)E 和 (3,0)S 端点相邻但方向不同 → §5 合并谓词的 `Direction == direction` 挡住 → 两条线。交接:A 出口 (2,0),`front = (2,0)+Delta(E)=(3,0)`;`Belts.GetLineAt(3,0)` = B;`B.Tiles[^1]`(B=[(3,0),(3,1),(3,2)],入口是 Tiles[^1]=(3,2)?)——**不对**。
  ——B 南向,`EastLineOn`-式建法:第一格 `(3,0)` 是出口(`Tiles[0]`)。`PlaceBelt(3,0,S)` → 线 [(3,0)]。`PlaceBelt(3,1,S)`:T=(3,1),`Delta(S)=(0,1)`,back=(3,0)=线 Tiles[0] → bHit → `ExtendFront`,`Insert(0,(3,1))` → [(3,1),(3,0)]。`PlaceBelt(3,2,S)` → back=(3,1)=Tiles[0] → [(3,2),(3,1),(3,0)]。所以 B = [(3,2),(3,1),(3,0)],出口 (3,2),入口 `Tiles[^1]=(3,0)`。交接条件 `down.Tiles[^1] == front` ⟺ `(3,0) == (3,0)` ✓。物品 A→B,在 B 上朝南走到出口 (3,2)。测试断言 `LineAt(sim,3,2)`(= B)`.LaneA.Count == 1` ✓。
- 四格环:每格不同方向 → 4 条 1 格线(`Delta` 谓词挡合并;放 (0,1)N 时 front=(0,0) 有 E 线但方向不同 → 不合并)。`Capacity == 4`。每条 1 格线 `Tiles[0]==Tiles[^1]==该格`。交接:(0,0)E front=(1,0)=线(1,0)S 的 `Tiles[^1]`=(1,0) ✓ → 传给 (1,0)S;依次成环。
- 塞满冻结:每条 256 lane 4 个物品,`Advance` 后全 `gaps==[0,0,0,0]`,`IsFrontReady` 真,但下游 `TryInsertAtBack` 满 → false → `while` 不执行 → `RemoveFront` 不调用 → 状态不变 → 哈希稳定。
- 既有确定性测试:`Belts.WriteState` 对空网络写 `count=0` + `freeCount=0`(两个 `int` 0)。绝对哈希值变了,但 `DeterminismTests` 全是相对比较(两遍相等 / 变化前后不等),不受影响。

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-09-02-m1-plan3d-simulation-wiring.md`. Two execution options:

1. **Subagent-Driven(推荐)** — 每任务派新 subagent,任务间审查。
2. **Inline Execution** — 当前会话按 executing-plans 批量执行,带检查点。

Which approach?
