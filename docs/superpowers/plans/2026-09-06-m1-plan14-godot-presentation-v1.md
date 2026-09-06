# M1 Plan 14 — Godot 表现层 v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 `Faketorio.Sim` 加第一个 Godot 表现层子项:只读渲染世界(网格/地块/矿脉/实体占位色块/传送带物品/玩家)+ 双模式相机骨架(Follow / Free 地图模式)+ 最小放置命令(左键放 wooden-chest / 右键拆)。

**Architecture:** 单进程。新增纯 C# 库 `Faketorio.Presentation.Core`(`TickAccumulator` 固定步长累加器 + `WorldTransform` 坐标变换,xUnit 可测)。新增 Godot 4.5.1 Mono 工程 `game/`,`<ProjectReference>` `Faketorio.Sim` + `Presentation.Core`;一个 autoload 单例 `SimHost` 持有唯一 `Simulation` 并在 `_Process` 按 1/60 步长调 `Step()`;`WorldView` 立即模式 `_Draw` 每帧从 sim 现读现画;`BuildController` 把点击转成 `Command` 提交。sim 层唯一改动:`ResourceGrid.PeekResourceAt`(无副作用读,防渲染器平移改哈希)。v1 snap 不插值。

**Tech Stack:** C# / .NET 8,Godot 4.5.1 Mono(`Godot.NET.Sdk/4.5.1`),xUnit 2.9。`Presentation.Core` 零依赖;`game/` 不进 CI(无 Godot runner),手动验收。

**Spec:** [`docs/superpowers/specs/2026-09-06-m1-plan14-godot-presentation-v1-design.md`](../specs/2026-09-06-m1-plan14-godot-presentation-v1-design.md)

## Global Constraints

- **只读**:`WorldView` / `CameraController` / `BuildController` 只调 sim 读方法和 `SimHost.Submit`;世界变更一律走 `Submit(Command)`。
- **无副作用读**:渲染器读世界不得改 sim 状态 / 影响 `ComputeStateHash`。用 `ResourceGrid.PeekResourceAt`(不进 `_chunks`)+ `WorldGrid.GetEntityAt`(本就无副作用)。
- **`PeekResourceAt` 是纯新增**:不改 `GetResourceAt` / `Extract` / `Ensure` / `Generate` / `WriteState` 的任何现有行为。全套 sim 测试 + `dotnet run --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json` 实现前后不变(`exit 0, gate PASS`)。
- sim 层不引入 `float`/`double`。表现层的 `double`(累加器、坐标)只在 `Presentation.Core` 和 `game/`,不回流 sim。
- **tick 语义**:命令经 `Submit` 在下一个 `Sim.Step()` 开头按提交顺序统一 apply(`Simulation` 已实现,不改)。
- `Faketorio.Presentation.Core` 零 Godot 依赖、零其它依赖(net8.0 BCL;测试项目 + xUnit)。`game/Game.csproj` 只依赖 `Faketorio.Sim` + `Faketorio.Presentation.Core` + Godot SDK。
- `game/` **不**进 `Faketorio.sln`、不进 CI。`Presentation.Core[.Tests]` 进 sln + CI `test` job;`test` job 命令改为 `dotnet test Faketorio.sln -c Release`。
- CI actions / 平台不变(`ubuntu-latest`、`actions/*@v4`、`dotnet 8.0.x`)。
- 提交信息结尾带:
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01KwHR5m3PgiyWtkMRcC21Jb
  ```

## 参考:现有代码事实(照此用,勿猜)

- `Simulation`:`Simulation(PrototypeRegistry prototypes, long worldSeed = 0)`;`void Submit(in Command)`;`void Step()`;`ulong ComputeStateHash()`;`long Tick`;属性 `Prototypes` / `Entities` / `Belts` / `Player` / `Resources`(`ResourceGrid`)/ `World`(`WorldGrid`)/ `ElectricGrid` / `Inventories` / `RejectedCommandCount`(int)。
- `Command`(`struct`,`namespace Faketorio.Sim.Commands`):`CommandType Type; int ProtoId; int X; int Y; byte Rotation; int Count;`。`CommandType.PlaceEntity = 1`、`RemoveEntity = 2`。
- `EntityData`:`int ProtoId; int X; int Y; byte Rotation;`。
- `EntityPool<EntityData>`(`Simulation.Entities`):`int Capacity`;`bool IsAliveAtIndex(int)`;`ref EntityData GetAtIndex(int)`;`int GenerationAtIndex(int)`;`ref EntityData Get(EntityId)`。
- `EntityId`:`readonly record struct EntityId(int Index, int Generation)`,`bool IsValid`。
- `WorldGrid`(`Simulation.World`):`EntityId GetEntityAt(int x, int y)`(chunk 不存在返回 `EntityId.Invalid`,**不生成**)。
- `ResourceGrid`(`Simulation.Resources`,`namespace Faketorio.Sim.World`):`ResourceCell GetResourceAt(int x, int y)`(惰性生成+缓存);`int GeneratedChunkCount`;`bool IsChunkGenerated(int x, int y)`。私有 `static long ChunkKey(int,int)`、`static int TileIndex(int,int)`、`ResourceChunk Ensure(int,int)`、`void Generate(int anyX, int anyY, ResourceChunk chunk)`(填满 32×32,`(seed, chunk 基点)` 纯函数)。`internal sealed class ResourceChunk { int[] TypeId; int[] Amount; }`(`TypeId[i]==0` 为空)。`const int Size = WorldGrid.ChunkSize` (=32)。
- `ResourceCell`(`readonly record struct ResourceCell(int ResourceProtoId, int Amount)`):`static readonly ResourceCell Empty = default;`、`bool IsEmpty => Amount == 0;`。
- `BeltNetwork`(`Simulation.Belts`):`int Capacity`;`bool IsAliveAtIndex(int)`;`BeltLine GetAtIndex(int)`。
- `BeltLine`:`const int TileSubTiles = 256`;`readonly byte Direction`(0/1/2/3=N/E/S/W);`readonly List<(int X, int Y)> Tiles`(`Tiles[0]`=出口格,`Tiles[^1]`=入口格);`readonly BeltLane LaneA` / `LaneB`;`int LengthSubTiles`。
- `BeltLane`:`bool TryInsertAtBack(int itemProtoId)`;`IReadOnlyList<PositionedItem> ToAbsolutePositions()` 其中 `PositionedItem` 是 `readonly record struct PositionedItem(int LeadingEdgeSubTiles, int ItemProtoId)`(嵌套在 `BeltLane`);`const int ItemWidthSubTiles = 64`。
- `Player`(`Simulation.Player`):`int X` / `int Y`(亚格坐标)。
- `Prototypes`(`PrototypeRegistry`):`T Get<T>(string name) where T : PrototypeBase`(有 `.Id`);`PrototypeBase GetById(int)`。prototype 类:`ContainerPrototype`("wooden-chest" 16 槽、"large-chest")、`MiningDrillPrototype`("electric-mining-drill" 2×2)、`FurnacePrototype`("stone-furnace" 2×2)、`TransportBeltPrototype`("transport-belt-basic")、`InserterPrototype`("inserter-basic")、`ElectricPolePrototype`("small-electric-pole")、`FuelGeneratorPrototype`("burner-generator" 2×2)、`ItemPrototype`("iron-plate" stackSize 100、"coal")。所有 `EntityPrototype` 有 `int TileWidth` / `int TileHeight`。
- `Inventories`:`InventoryId GetInventoryId(EntityId entity, int role = 0)`;`Inventory Get(InventoryId)`。`Inventory.Insert(int itemProtoId, int count, int stackSize) -> int`。
- `ElectricGrid.SetFuelBufferJ(EntityId id, long value)`。`ItemPrototype("coal")` 的燃料值:用 `FuelValueJ` 字段(P7 代码里怎么读的照抄;coal.json 是 `"fuelValue": "4MJ"` → 4_000_000)。
- `PrototypeLoader.LoadFromDirectory(string path) -> PrototypeRegistry`。
- 现有测试基线:`dotnet test sim/Faketorio.Sim.Tests -c Release` ~433 passing(P13 合并后);`dotnet build -c Release` 0/0。

---

### Task 1: `Faketorio.Presentation.Core` 项目 + `TickAccumulator` + sln 接线

**Files:**
- Create: `presentation/Faketorio.Presentation.Core/Faketorio.Presentation.Core.csproj`
- Create: `presentation/Faketorio.Presentation.Core/TickAccumulator.cs`
- Create: `presentation/Faketorio.Presentation.Core.Tests/Faketorio.Presentation.Core.Tests.csproj`
- Create: `presentation/Faketorio.Presentation.Core.Tests/TickAccumulatorTests.cs`
- Modify: `Faketorio.sln`(`dotnet sln add` 两个新项目)

**Interfaces:**
- Produces: `namespace Faketorio.Presentation.Core; public sealed class TickAccumulator { public const double TickSeconds = 1.0/60.0; public int MaxCatchUpTicks { get; init; } = 5; public int Advance(double deltaSeconds); public double Alpha { get; } }`

- [ ] **Step 1: 建两个 csproj**

`presentation/Faketorio.Presentation.Core/Faketorio.Presentation.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

`presentation/Faketorio.Presentation.Core.Tests/Faketorio.Presentation.Core.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Faketorio.Presentation.Core\Faketorio.Presentation.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 写失败测试**

`presentation/Faketorio.Presentation.Core.Tests/TickAccumulatorTests.cs`:
```csharp
using Faketorio.Presentation.Core;

namespace Faketorio.Presentation.Core.Tests;

public class TickAccumulatorTests
{
    [Fact]
    public void SteadyRate_YieldsOneTickPerFrame()
    {
        var acc = new TickAccumulator();
        for (int i = 0; i < 100; i++)
        {
            int n = acc.Advance(TickAccumulator.TickSeconds);
            Assert.Equal(1, n);
        }
        Assert.True(acc.Alpha < 0.01);
    }

    [Fact]
    public void HalfRate_YieldsTickEveryOtherFrame_AlphaTracks()
    {
        var acc = new TickAccumulator();
        Assert.Equal(0, acc.Advance(TickAccumulator.TickSeconds / 2));
        Assert.InRange(acc.Alpha, 0.49, 0.51);
        Assert.Equal(1, acc.Advance(TickAccumulator.TickSeconds / 2));
        Assert.True(acc.Alpha < 0.01);
    }

    [Fact]
    public void HugeDelta_ClampsToMaxCatchUp_AndDiscardsRemainder()
    {
        var acc = new TickAccumulator { MaxCatchUpTicks = 5 };
        int n = acc.Advance(1.0);   // 1 秒 ≈ 60 tick,远超上限
        Assert.Equal(5, n);
        Assert.True(acc.Alpha < 1.0, "追不上的累积时间必须被丢弃,Alpha 保持 [0,1)");
    }

    [Fact]
    public void ZeroDelta_NoTick_NoAlphaChange()
    {
        var acc = new TickAccumulator();
        acc.Advance(TickAccumulator.TickSeconds * 0.3);
        double a0 = acc.Alpha;
        Assert.Equal(0, acc.Advance(0));
        Assert.Equal(a0, acc.Alpha);
    }
}
```

- [ ] **Step 3: 跑,确认失败** — `dotnet test presentation/Faketorio.Presentation.Core.Tests` 编译失败(`TickAccumulator` 不存在)。

- [ ] **Step 4: 实现 `TickAccumulator.cs`**
```csharp
namespace Faketorio.Presentation.Core;

/// 把变帧率的 delta 秒累积,吐出这一帧要跑几个固定步长的 sim tick。
public sealed class TickAccumulator
{
    public const double TickSeconds = 1.0 / 60.0;
    public int MaxCatchUpTicks { get; init; } = 5;

    private double _acc;

    /// 返回 [0, MaxCatchUpTicks] 个要执行的 tick;超出的累积时间被丢弃(不追实时)。
    public int Advance(double deltaSeconds)
    {
        _acc += deltaSeconds;
        int n = 0;
        while (_acc >= TickSeconds && n < MaxCatchUpTicks)
        {
            _acc -= TickSeconds;
            n++;
        }
        if (_acc >= TickSeconds) _acc %= TickSeconds;   // 夹紧:丢弃追不上的部分,Alpha 保持 [0,1)
        return n;
    }

    /// 距下一个 tick 的分数进度 [0, 1),给未来 lerp 用;v1 不消费。
    public double Alpha => _acc / TickSeconds;
}
```

- [ ] **Step 5: sln 接线 + 跑测试**

Run: `dotnet sln Faketorio.sln add presentation/Faketorio.Presentation.Core/Faketorio.Presentation.Core.csproj presentation/Faketorio.Presentation.Core.Tests/Faketorio.Presentation.Core.Tests.csproj`
Run: `dotnet test Faketorio.sln -c Release`
Expected: 全绿(现有 ~433 + 新 4)。

- [ ] **Step 6: commit**
```bash
git add presentation/ Faketorio.sln
git commit -m "feat(presentation): Faketorio.Presentation.Core + TickAccumulator

<trailer>"
```

---

### Task 2: `Vec2` / `RectI` / `WorldTransform` + 测试

**Files:**
- Create: `presentation/Faketorio.Presentation.Core/Geometry.cs`(`Vec2` + `RectI`)
- Create: `presentation/Faketorio.Presentation.Core/WorldTransform.cs`
- Create: `presentation/Faketorio.Presentation.Core.Tests/WorldTransformTests.cs`

**Interfaces:**
- Consumes: 无(Task 1 的项目已存在)。
- Produces:
  - `namespace Faketorio.Presentation.Core; public readonly record struct Vec2(double X, double Y); public readonly record struct RectI(int MinX, int MinY, int MaxX, int MaxY);`(`RectI` 含 Min 不含 Max)
  - `public sealed class WorldTransform { public const int SubTilesPerTile = 256; public double PixelsPerTile { get; set; } public Vec2 CameraCenterTile { get; set; } public Vec2 ViewportSizePx { get; set; } public Vec2 WorldSubToScreen(long subX, long subY); public Vec2 TileToScreen(int tileX, int tileY); public (int TileX, int TileY) ScreenToTile(Vec2 screenPx); public RectI VisibleTileRect(int marginTiles = 3); }`

- [ ] **Step 1: 写失败测试**

`presentation/Faketorio.Presentation.Core.Tests/WorldTransformTests.cs`:
```csharp
using Faketorio.Presentation.Core;

namespace Faketorio.Presentation.Core.Tests;

public class WorldTransformTests
{
    private static WorldTransform Make(double ppt, double cx, double cy, double vw, double vh)
        => new() { PixelsPerTile = ppt, CameraCenterTile = new Vec2(cx, cy), ViewportSizePx = new Vec2(vw, vh) };

    [Theory]
    [InlineData(4)]
    [InlineData(32)]
    [InlineData(64)]
    public void TileToScreen_ScreenToTile_RoundTrips_AtTileCenter(double ppt)
    {
        var t = Make(ppt, 10, -7, 800, 600);
        foreach (var (tx, ty) in new[] { (0, 0), (10, -7), (25, 40), (-13, -2) })
        {
            var s = t.TileToScreen(tx, ty);
            // 采样格中心(+半格像素),避开边界取整歧义
            var back = t.ScreenToTile(new Vec2(s.X + ppt / 2, s.Y + ppt / 2));
            Assert.Equal((tx, ty), back);
        }
    }

    [Fact]
    public void CameraCenter_MapsToViewportCenter()
    {
        var t = Make(32, 3.5, -1.25, 800, 600);
        var s = t.WorldSubToScreen(3L * 256 + 128, -2L * 256 + 192);   // = tile (3.5, -1.25) 亚格
        Assert.InRange(s.X, 399.9, 400.1);
        Assert.InRange(s.Y, 299.9, 300.1);
    }

    [Fact]
    public void ScreenToTile_FloorsAtTileBoundary()
    {
        var t = Make(32, 0, 0, 800, 600);   // 视口中心屏幕 (400,300) = tile (0,0) 左上角
        Assert.Equal((0, 0), t.ScreenToTile(new Vec2(400 + 31.9, 300 + 0)));
        Assert.Equal((1, 0), t.ScreenToTile(new Vec2(400 + 32.1, 300 + 0)));
        Assert.Equal((-1, 0), t.ScreenToTile(new Vec2(400 - 0.1, 300 + 0)));
    }

    [Fact]
    public void VisibleTileRect_CoversViewportPlusMargin()
    {
        var t = Make(32, 0, 0, 800, 600);   // 半视口 = 400x300 px = 12.5 x 9.375 tile
        var r0 = t.VisibleTileRect(0);
        Assert.True(r0.MinX <= -12 && r0.MaxX >= 13);
        Assert.True(r0.MinY <= -9 && r0.MaxY >= 10);
        var r3 = t.VisibleTileRect(3);
        Assert.Equal(r0.MinX - 3, r3.MinX);
        Assert.Equal(r0.MaxX + 3, r3.MaxX);
    }
}
```

- [ ] **Step 2: 跑,确认失败** — 编译失败。

- [ ] **Step 3: 实现**

`presentation/Faketorio.Presentation.Core/Geometry.cs`:
```csharp
namespace Faketorio.Presentation.Core;

/// double 版 2D 向量(表现层坐标;sim 层永远不引入 float/double)。
public readonly record struct Vec2(double X, double Y);

/// 整数 tile 矩形。含 Min,不含 Max(半开区间)。
public readonly record struct RectI(int MinX, int MinY, int MaxX, int MaxY);
```

`presentation/Faketorio.Presentation.Core/WorldTransform.cs`:
```csharp
namespace Faketorio.Presentation.Core;

/// world 坐标(tile int / 亚格 long)↔ 屏幕像素。参数由相机每帧喂入。
public sealed class WorldTransform
{
    public const int SubTilesPerTile = 256;   // 与 BeltLine.TileSubTiles 一致

    public double PixelsPerTile { get; set; } = 32;
    public Vec2 CameraCenterTile { get; set; }   // 相机中心的 world tile 坐标(可含小数)
    public Vec2 ViewportSizePx { get; set; }

    public Vec2 WorldSubToScreen(long subX, long subY)
    {
        double tileX = subX / (double)SubTilesPerTile;
        double tileY = subY / (double)SubTilesPerTile;
        return new Vec2(
            (tileX - CameraCenterTile.X) * PixelsPerTile + ViewportSizePx.X / 2,
            (tileY - CameraCenterTile.Y) * PixelsPerTile + ViewportSizePx.Y / 2);
    }

    public Vec2 TileToScreen(int tileX, int tileY)
        => WorldSubToScreen((long)tileX * SubTilesPerTile, (long)tileY * SubTilesPerTile);

    public (int TileX, int TileY) ScreenToTile(Vec2 screenPx)
    {
        double tileX = (screenPx.X - ViewportSizePx.X / 2) / PixelsPerTile + CameraCenterTile.X;
        double tileY = (screenPx.Y - ViewportSizePx.Y / 2) / PixelsPerTile + CameraCenterTile.Y;
        return ((int)Math.Floor(tileX), (int)Math.Floor(tileY));
    }

    /// 可见 tile 矩形 + marginTiles 圈边距(骑边缘的大 footprint 实体 / 传送带物品 / 相机 tween 稳定)。
    public RectI VisibleTileRect(int marginTiles = 3)
    {
        var tl = ScreenToTile(new Vec2(0, 0));
        var br = ScreenToTile(ViewportSizePx);
        return new RectI(
            tl.TileX - marginTiles, tl.TileY - marginTiles,
            br.TileX + marginTiles + 1, br.TileY + marginTiles + 1);
    }
}
```

- [ ] **Step 4: 跑测试** — `dotnet test Faketorio.sln -c Release` 全绿(+~6)。

- [ ] **Step 5: commit**
```bash
git add presentation/Faketorio.Presentation.Core/Geometry.cs presentation/Faketorio.Presentation.Core/WorldTransform.cs presentation/Faketorio.Presentation.Core.Tests/WorldTransformTests.cs
git commit -m "feat(presentation): Vec2/RectI/WorldTransform 坐标变换

<trailer>"
```

---

### Task 3: `ResourceGrid.PeekResourceAt`(sim 层唯一改动)

**Files:**
- Modify: `sim/Faketorio.Sim/World/ResourceGrid.cs`
- Modify: `sim/Faketorio.Sim.Tests/ResourceGridTests.cs`

**Interfaces:**
- Produces: `public ResourceCell PeekResourceAt(int x, int y)` on `ResourceGrid` —— 已生成 chunk 直接读;未生成则临时 `Generate` 一个瞬时 chunk 读完丢弃,**不进 `_chunks`、不置 `_keysDirty`**。

- [ ] **Step 1: 写失败测试**（加到 `ResourceGridTests.cs`）
```csharp
    [Fact]
    public void PeekResourceAt_MatchesGetResourceAt_AndHasNoSideEffect()
    {
        var protos = PrototypeLoader.LoadFromDirectory("data/base");
        var grid = new ResourceGrid(123456789L, protos);

        // 选一批"远处"坐标(跨多个未生成 chunk)
        var probes = new (int x, int y)[] { (500, 500), (501, 500), (-800, 320), (77, -1234), (2048, 2048) };

        int chunksBefore = grid.GeneratedChunkCount;
        var peeked = probes.Select(p => grid.PeekResourceAt(p.x, p.y)).ToArray();

        // 无副作用:Peek 不生成 chunk
        Assert.Equal(chunksBefore, grid.GeneratedChunkCount);

        // 内容一致:Peek 的结果 == 之后 GetResourceAt 的结果
        for (int i = 0; i < probes.Length; i++)
            Assert.Equal(grid.GetResourceAt(probes[i].x, probes[i].y), peeked[i]);

        // 已生成 chunk 上:Peek == Get
        Assert.Equal(grid.GetResourceAt(500, 500), grid.PeekResourceAt(500, 500));
    }

    [Fact]
    public void PeekResourceAt_DoesNotChangeStateHash()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"), 123456789L);
        sim.Step();
        ulong before = sim.ComputeStateHash();

        for (int gx = -3; gx <= 3; gx++)
            for (int gy = -3; gy <= 3; gy++)
                sim.Resources.PeekResourceAt(gx * 40 + 1000, gy * 40 + 1000);   // 一堆远处虚拟坐标

        Assert.Equal(before, sim.ComputeStateHash());
    }
```
（`ResourceGridTests.cs` 顶部若缺 `using System.Linq;` / `using Faketorio.Sim;` / `using Faketorio.Sim.Prototypes;` 补上。）

- [ ] **Step 2: 跑,确认失败** — `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ResourceGridTests.PeekResourceAt"` 编译失败(`PeekResourceAt` 不存在)。

- [ ] **Step 3: 实现**

`sim/Faketorio.Sim/World/ResourceGrid.cs`,在 `GetResourceAt` 之后加:
```csharp
    // 无副作用读:已生成 chunk 直接读;未生成则临时 Generate 一个瞬时 chunk 读完丢弃,
    // 不进 _chunks、不置 _keysDirty —— 供表现层在"地图模式"下自由平移查矿而不改状态哈希。
    // Generate 是 (seed, chunk 基点) 纯函数,所以 Peek 结果与之后 GetResourceAt 一致。
    public ResourceCell PeekResourceAt(int x, int y)
    {
        long key = ChunkKey(x, y);
        if (!_chunks.TryGetValue(key, out var chunk))
        {
            chunk = new ResourceChunk();
            Generate(x, y, chunk);
        }
        int i = TileIndex(x, y);
        return chunk.TypeId[i] == 0 ? ResourceCell.Empty : new ResourceCell(chunk.TypeId[i], chunk.Amount[i]);
    }
```

- [ ] **Step 4: 跑 — filter + 全套 + golden HARD GATE**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ResourceGridTests.PeekResourceAt"` → PASS(2)。
Run: `dotnet test sim/Faketorio.Sim.Tests -c Release` → 全绿(尤其 `ResourceGridTests` 现有断言、`DeterminismTests`)。
Run: `dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json --report /tmp/r.json`
Expected: **exit 0,gate PASS**(哈希不变——`PeekResourceAt` 纯新增,不该动任何东西)。

- [ ] **Step 5: commit**
```bash
git add sim/Faketorio.Sim/World/ResourceGrid.cs sim/Faketorio.Sim.Tests/ResourceGridTests.cs
git commit -m "feat(sim): ResourceGrid.PeekResourceAt — 无副作用矿脉读

<trailer>"
```

---

### Task 4: CI `test` job 跑整个 sln + `.gitignore`

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `.gitignore`

**Interfaces:** 无代码。

- [ ] **Step 1: 改 `.github/workflows/ci.yml`**

把 `test` job 里的
```yaml
      - run: dotnet test sim/Faketorio.Sim.Tests -c Release
```
改成
```yaml
      - run: dotnet test Faketorio.sln -c Release
```
`bench` job、触发条件、`permissions` / `concurrency` / `timeout-minutes`(P12 fix wave 加的)全部不动。

- [ ] **Step 2: 改 `.gitignore`**

在 `# Godot` 段(现有 `game/.godot/` `*.uid`)补:
```
game/.godot/
game/.mono/
game/bin/
game/obj/
*.uid
```
（`game/.godot/` 已有则不重复;确保 `game/bin/` `game/obj/` `game/.mono/` 在。）

- [ ] **Step 3: 验证**

Run: `dotnet test Faketorio.sln -c Release`
Expected: 一次跑完 `Faketorio.Sim.Tests` + `Faketorio.Sim.Bench`(若它有 test,没有就只 build)+ `Faketorio.Presentation.Core.Tests`,全绿。
（本地无法验证 GitHub Actions;yaml 改动是一行替换,人工确认语法。）

- [ ] **Step 4: commit**
```bash
git add .github/workflows/ci.yml .gitignore
git commit -m "ci(presentation): test job 跑整个 Faketorio.sln;gitignore 加 game/ 产物

<trailer>"
```

---

### Task 5: `game/` Godot 工程(scaffold + 全部脚本 + 场景 + InputMap)

**这个 task 没有单元测试可迭代 —— 实现者一次性写全,自审对照 spec §4–6 + sim API,用 `dotnet build` 做编译检查。运行验收在 Task 6(人工)。**

**Files(全部 Create,在 `game/` 下):**
- `game/project.godot`
- `game/Game.csproj`
- `game/Main.tscn`
- `game/SimHost.cs`
- `game/CameraController.cs`
- `game/ICameraTarget.cs`
- `game/PlayerCameraTarget.cs`
- `game/WorldView.cs`
- `game/BuildController.cs`
- `game/RenderPalette.cs`
- `game/GodotExtensions.cs`（`Vec2 ↔ Godot.Vector2` 转换）

**Interfaces:**
- Consumes: `Faketorio.Sim`(见"参考"节全部)、`Faketorio.Presentation.Core`(`TickAccumulator`、`WorldTransform`、`Vec2`、`RectI`)。
- Produces(节点间):`SimHost`(autoload,`Simulation Sim`、`double Alpha`、`void Submit(in Command)`);`CameraController`(`WorldTransform Transform`、`CameraMode Mode`);`BuildController`(`(int X,int Y) HoverTile`、`bool LastCommandRejected`)。

- [ ] **Step 1: `game/project.godot`**
```ini
; Engine configuration file.
config_version=5

[application]
config/name="Faketorio"
run/main_scene="res://Main.tscn"
config/features=PackedStringArray("4.5", "C#", "Forward Plus")

[autoload]
SimHost="*res://SimHost.cs"

[dotnet]
project/assembly_name="Faketorio.Game"

[input]
map_toggle={
"deadzone": 0.5,
"events": [ { "type": "key", "keycode": 77 } ]
}
```
（`keycode 77` = `M`。若 Godot 4.5 的 InputEvent 序列化格式不同,用编辑器加一个 `map_toggle` action 绑 `M` 再存盘;平移用鼠标中键、缩放用滚轮直接在代码里读 `InputEventMouseButton`,不进 InputMap。）

- [ ] **Step 2: `game/Game.csproj`**
```xml
<Project Sdk="Godot.NET.Sdk/4.5.1">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <Nullable>enable</Nullable>
    <RootNamespace>Faketorio.Game</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\sim\Faketorio.Sim\Faketorio.Sim.csproj" />
    <ProjectReference Include="..\presentation\Faketorio.Presentation.Core\Faketorio.Presentation.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: `game/GodotExtensions.cs`**
```csharp
using Godot;
using CoreVec2 = Faketorio.Presentation.Core.Vec2;

namespace Faketorio.Game;

public static class GodotExtensions
{
    public static Vector2 ToGodot(this CoreVec2 v) => new((float)v.X, (float)v.Y);
    public static CoreVec2 ToCore(this Vector2 v) => new(v.X, v.Y);
}
```

- [ ] **Step 4: `game/SimHost.cs`**
```csharp
using Godot;
using Faketorio.Sim;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Entities;
using Faketorio.Sim.Prototypes;
using Faketorio.Presentation.Core;

namespace Faketorio.Game;

/// autoload 单例。持有唯一 Simulation,是唯一调 Step() 的地方。
public partial class SimHost : Node
{
    [Export] public long Seed = 20260906L;

    public Simulation Sim { get; private set; } = null!;
    public double Alpha => _acc.Alpha;
    public void Submit(in Command c) => Sim.Submit(c);

    private readonly TickAccumulator _acc = new();
    private int _feederTick;
    private int _ironPlateId;
    private int _beltLineCount;

    public override void _Ready()
    {
        string dataPath = ProjectSettings.GlobalizePath("res://../data/base");
        Sim = new Simulation(PrototypeLoader.LoadFromDirectory(dataPath), Seed);
        SubmitStartupScene();
    }

    public override void _Process(double delta)
    {
        for (int n = _acc.Advance(delta); n-- > 0;) Sim.Step();

        // v1 调试喂料:每 60 tick 往第一条传送带丢一个铁板,保证画面永远有东西在动。
        // (未来接缝:真物流链落地后删掉。)
        if (_beltLineCount > 0 && ++_feederTick >= 60)
        {
            _feederTick = 0;
            for (int i = 0; i < Sim.Belts.Capacity; i++)
            {
                if (!Sim.Belts.IsAliveAtIndex(i)) continue;
                Sim.Belts.GetAtIndex(i).LaneA.TryInsertAtBack(_ironPlateId);
                break;
            }
        }
    }

    private void SubmitStartupScene()
    {
        int drill = Sim.Prototypes.Get<MiningDrillPrototype>("electric-mining-drill").Id;
        int furnace = Sim.Prototypes.Get<FurnacePrototype>("stone-furnace").Id;
        int belt = Sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id;
        int chest = Sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        int pole = Sim.Prototypes.Get<ElectricPolePrototype>("small-electric-pole").Id;
        int gen = Sim.Prototypes.Get<FuelGeneratorPrototype>("burner-generator").Id;
        _ironPlateId = Sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;

        void Place(int protoId, int x, int y, byte rot = 0)
            => Sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = protoId, X = x, Y = y, Rotation = rot });

        // 一条 5 格向东的传送带 (0,0)..(4,0)
        for (int x = 0; x < 5; x++) Place(belt, x, 0, rot: 1);
        // 几个"装饰"实体,渲染多样性;空转无所谓
        Place(drill, 0, -3);
        Place(drill, 2, -3);
        Place(furnace, 6, -2);
        Place(chest, 6, 0);
        Place(gen, 0, 3);
        Place(pole, 3, 1);

        Sim.Step();   // drain 放置命令 —— 之后实体存在

        // 建造期一次性注入(同 bench ScenarioBuilder 的手法):发电机灌满燃料。
        var genId = Sim.World.GetEntityAt(0, 3);
        if (genId.IsValid)
        {
            long coalFuelJ = ((ItemPrototype)Sim.Prototypes.GetById(
                Sim.Prototypes.Get<ItemPrototype>("coal").Id)).FuelValueJ;
            Sim.ElectricGrid.SetFuelBufferJ(genId, 50L * coalFuelJ);
        }

        // 数一下传送带线数,给 _Process 的喂料用
        for (int i = 0; i < Sim.Belts.Capacity; i++)
            if (Sim.Belts.IsAliveAtIndex(i)) _beltLineCount++;
    }
}
```
（`ItemPrototype.FuelValueJ` 的确切字段名以 `sim/Faketorio.Sim/Prototypes/ItemPrototype.cs` 为准;若不是 `FuelValueJ` 就按实际的改,或直接硬编码 `4_000_000L`。装饰实体的坐标若和传送带撞导致命令被拒,挪开——Task 6 验收时看 `RejectedCommandCount`。）

- [ ] **Step 5: `game/ICameraTarget.cs` + `game/PlayerCameraTarget.cs`**
```csharp
namespace Faketorio.Game;

public interface ICameraTarget
{
    (long SubX, long SubY) WorldSub { get; }
}

public sealed class PlayerCameraTarget(SimHost host) : ICameraTarget
{
    public (long SubX, long SubY) WorldSub => (host.Sim.Player.X, host.Sim.Player.Y);
}
```

- [ ] **Step 6: `game/CameraController.cs`**
```csharp
using Godot;
using Faketorio.Presentation.Core;

namespace Faketorio.Game;

public enum CameraMode { Follow, Free }

public partial class CameraController : Camera2D
{
    public CameraMode Mode { get; private set; } = CameraMode.Follow;
    public WorldTransform Transform { get; } = new();

    private ICameraTarget? _followTarget;
    private SimHost _host = null!;

    // Follow 近景窄区间;Free 放宽
    private const double FollowMinPpt = 32, FollowMaxPpt = 64;
    private const double FreeMinPpt = 4, FreeMaxPpt = 64;

    private double _ppt = 48;
    private Vector2 _freeCenterPx;      // Free 模式下相机中心的屏幕->世界像素(未除 ppt)
    private Vector2 _followCenterTile;  // 上一帧 follow 中心(切 Free 时保持)
    private bool _panning;
    private Tween? _snapTween;

    public override void _Ready()
    {
        _host = GetNode<SimHost>("/root/SimHost");
        _followTarget = new PlayerCameraTarget(_host);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("map_toggle")) { ToggleMode(); return; }

        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) Zoom(+1);
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) Zoom(-1);
            else if (mb.ButtonIndex == MouseButton.Middle && Mode == CameraMode.Free) _panning = mb.Pressed;
        }
        if (e is InputEventMouseMotion mm && _panning && Mode == CameraMode.Free)
            GlobalPosition -= mm.Relative / (float)_ppt * (float)WorldTransform.SubTilesPerTile / (float)WorldTransform.SubTilesPerTile;
    }

    private void ToggleMode()
    {
        if (Mode == CameraMode.Follow)
        {
            Mode = CameraMode.Free;
            _panning = false;
        }
        else
        {
            Mode = CameraMode.Follow;
            var (sx, sy) = _followTarget!.WorldSub;
            var target = new Vector2(
                (float)(sx / (double)WorldTransform.SubTilesPerTile),
                (float)(sy / (double)WorldTransform.SubTilesPerTile));
            _snapTween?.Kill();
            _snapTween = CreateTween();
            _snapTween.TweenProperty(this, "global_position", target, 0.25)
                      .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        }
    }

    private void Zoom(int dir)
    {
        double min = Mode == CameraMode.Follow ? FollowMinPpt : FreeMinPpt;
        double max = Mode == CameraMode.Follow ? FollowMaxPpt : FreeMaxPpt;
        _ppt = Mathf.Clamp(_ppt * (dir > 0 ? 1.2 : 1 / 1.2), min, max);
    }

    public override void _Process(double delta)
    {
        if (Mode == CameraMode.Follow && (_snapTween is null || !_snapTween.IsRunning()))
        {
            var (sx, sy) = _followTarget!.WorldSub;
            GlobalPosition = new Vector2(
                (float)(sx / (double)WorldTransform.SubTilesPerTile),
                (float)(sy / (double)WorldTransform.SubTilesPerTile));
        }

        var vp = GetViewportRect().Size;
        Transform.PixelsPerTile = _ppt;
        Transform.CameraCenterTile = new Vec2(GlobalPosition.X, GlobalPosition.Y);
        Transform.ViewportSizePx = new Vec2(vp.X, vp.Y);

        // Camera2D.Zoom 让 Godot 自己也缩放(WorldView 用像素直接画,所以 zoom 保持 1;
        // 我们完全用 WorldTransform 控制映射)。这里不设 Zoom。
    }
}
```
（`Camera2D` 在 Godot 4 里 `GlobalPosition` 单位是"世界坐标";我们把"世界坐标"就当成 tile 坐标用,`WorldView` 完全靠 `WorldTransform`(参数含 `GlobalPosition` 当 tile 中心)算像素,`Camera2D.Zoom` 保持 1。若发现 `Camera2D` 的存在导致 `WorldView` 的 `_Draw` 坐标被二次变换,把 `WorldView` 挂在 `CanvasLayer` 下或让它 `TopLevel = true` 脱离相机变换 —— Task 6 验收时确认。平移那行 `mm.Relative` 的换算按实际手感调,目标是"拖多少屏幕像素,世界移动等量")。

- [ ] **Step 7: `game/RenderPalette.cs`**
```csharp
using Godot;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Game;

/// prototype 运行时类型 -> 占位颜色。v1 手填,无 sprite。
public static class RenderPalette
{
    public static Color ForEntity(PrototypeBase proto) => proto switch
    {
        MiningDrillPrototype   => new Color("d98e5b"),
        FurnacePrototype       => new Color("c0523a"),
        var p when p.GetType().Name.Contains("Assembl") => new Color("5b8ad9"),
        TransportBeltPrototype => new Color("7a7a4a"),
        InserterPrototype      => new Color("d9c85b"),
        ElectricPolePrototype  => new Color("8a6d3b"),
        FuelGeneratorPrototype => new Color("6b4a2a"),
        ContainerPrototype     => new Color("9a7b3b"),
        _                      => new Color("888888"),
    };

    public static Color ForResource(int resourceProtoId, PrototypeBase proto) => proto is ResourcePrototype rp
        ? rp.Name switch
        {
            "iron-ore"   => new Color(0.55f, 0.6f, 0.7f, 0.5f),
            "copper-ore" => new Color(0.8f, 0.5f, 0.3f, 0.5f),
            "coal"       => new Color(0.15f, 0.15f, 0.15f, 0.6f),
            "stone"      => new Color(0.6f, 0.55f, 0.45f, 0.5f),
            _            => new Color(0.5f, 0.5f, 0.5f, 0.4f),
        }
        : new Color(0.5f, 0.5f, 0.5f, 0.4f);

    public static Color ForItem(int itemProtoId) => new Color("cfd3d6");
}
```
（`ResourcePrototype` 的 `Name` 属性名以实际为准;`AssemblingMachinePrototype` 用 `GetType().Name.Contains("Assembl")` 兜底,避免猜确切类名。配色随便,占位。）

- [ ] **Step 8: `game/WorldView.cs`**
```csharp
using Godot;
using Faketorio.Sim.World;
using Faketorio.Sim.Prototypes;
using Faketorio.Presentation.Core;

namespace Faketorio.Game;

/// 立即模式只读渲染。每帧 QueueRedraw + _Draw,从 sim 现读现画。零 mutation。
public partial class WorldView : Node2D
{
    [Export] public bool ShowGrid = true;

    private SimHost _host = null!;
    private CameraController _cam = null!;
    private BuildController _build = null!;
    private readonly System.Collections.Generic.Dictionary<long, ResourceCell[]> _oreCache = new();

    public override void _Ready()
    {
        _host = GetNode<SimHost>("/root/SimHost");
        _cam = GetNode<CameraController>("../CameraController");
        _build = GetNode<BuildController>("../BuildController");
        TopLevel = true;   // 脱离 Camera2D 变换,完全用 WorldTransform 画像素
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var t = _cam.Transform;
        var vis = t.VisibleTileRect(marginTiles: 3);
        var sim = _host.Sim;

        // 1. 地块底色
        for (int y = vis.MinY; y < vis.MaxY; y++)
            for (int x = vis.MinX; x < vis.MaxX; x++)
            {
                var s = t.TileToScreen(x, y).ToGodot();
                DrawRect(new Rect2(s, new Vector2((float)t.PixelsPerTile, (float)t.PixelsPerTile)),
                         new Color(0.11f, 0.12f, 0.11f));
            }

        // 2. 网格 gizmo
        if (ShowGrid && t.PixelsPerTile >= 6)
        {
            var faint = new Color(1, 1, 1, 0.06f);
            var mid = new Color(1, 1, 1, 0.12f);
            for (int x = vis.MinX; x <= vis.MaxX; x++)
            {
                var a = t.TileToScreen(x, vis.MinY).ToGodot();
                var b = t.TileToScreen(x, vis.MaxY).ToGodot();
                DrawLine(a, b, (x & 7) == 0 ? mid : faint, 1);
            }
            for (int y = vis.MinY; y <= vis.MaxY; y++)
            {
                var a = t.TileToScreen(vis.MinX, y).ToGodot();
                var b = t.TileToScreen(vis.MaxX, y).ToGodot();
                DrawLine(a, b, (y & 7) == 0 ? mid : faint, 1);
            }
        }

        // 3. 矿脉(经 PeekResourceAt,无副作用)
        for (int y = vis.MinY; y < vis.MaxY; y++)
            for (int x = vis.MinX; x < vis.MaxX; x++)
            {
                var cell = PeekOreCached(x, y);
                if (cell.IsEmpty) continue;
                var proto = sim.Prototypes.GetById(cell.ResourceProtoId);
                var s = t.TileToScreen(x, y).ToGodot();
                DrawRect(new Rect2(s, new Vector2((float)t.PixelsPerTile, (float)t.PixelsPerTile)),
                         RenderPalette.ForResource(cell.ResourceProtoId, proto));
            }

        // 4. 实体
        for (int i = 0; i < sim.Entities.Capacity; i++)
        {
            if (!sim.Entities.IsAliveAtIndex(i)) continue;
            ref readonly var d = ref sim.Entities.GetAtIndex(i);
            if (d.X < vis.MinX || d.X >= vis.MaxX || d.Y < vis.MinY || d.Y >= vis.MaxY) continue;
            var proto = sim.Prototypes.GetById(d.ProtoId);
            int w = 1, h = 1;
            if (proto is EntityPrototype ep) { w = ep.TileWidth; h = ep.TileHeight; }
            var s = t.TileToScreen(d.X, d.Y).ToGodot();
            var size = new Vector2((float)(t.PixelsPerTile * w) - 2, (float)(t.PixelsPerTile * h) - 2);
            DrawRect(new Rect2(s + new Vector2(1, 1), size), RenderPalette.ForEntity(proto));
            DrawOrientation(s, size, d.Rotation);
        }

        // 5. 传送带上物品
        for (int i = 0; i < sim.Belts.Capacity; i++)
        {
            if (!sim.Belts.IsAliveAtIndex(i)) continue;
            var line = sim.Belts.GetAtIndex(i);
            DrawLaneItems(t, line, line.LaneA, laneOffset: -0.22);
            DrawLaneItems(t, line, line.LaneB, laneOffset: +0.22);
        }

        // 6. 玩家
        {
            var p = t.WorldSubToScreen(sim.Player.X, sim.Player.Y).ToGodot();
            DrawCircle(p, (float)t.PixelsPerTile * 0.35f, new Color("e8e8e8"));
        }

        // 7. 光标格高亮
        {
            var (hx, hy) = _build.HoverTile;
            var s = t.TileToScreen(hx, hy).ToGodot();
            var r = new Rect2(s, new Vector2((float)t.PixelsPerTile, (float)t.PixelsPerTile));
            DrawRect(r, _build.LastCommandRejected ? new Color(1, 0.3f, 0.3f) : new Color(1, 1, 1, 0.8f), filled: false, 2);
        }
    }

    private ResourceCell PeekOreCached(int x, int y)
    {
        long ck = ((long)(x >> 5) << 32) | (uint)(y >> 5);
        if (!_oreCache.TryGetValue(ck, out var arr))
        {
            arr = new ResourceCell[32 * 32];
            int bx = (x >> 5) << 5, by = (y >> 5) << 5;
            for (int ly = 0; ly < 32; ly++)
                for (int lx = 0; lx < 32; lx++)
                    arr[ly * 32 + lx] = _host.Sim.Resources.PeekResourceAt(bx + lx, by + ly);
            _oreCache[ck] = arr;
        }
        return arr[(y & 31) * 32 + (x & 31)];
    }

    private void DrawLaneItems(WorldTransform t, Faketorio.Sim.Belts.BeltLine line, Faketorio.Sim.Belts.BeltLane lane, double laneOffset)
    {
        var items = lane.ToAbsolutePositions();
        foreach (var it in items)
        {
            // LeadingEdgeSubTiles 沿 line.Tiles(Tiles[0]=出口)反算 world 亚格
            int tileIdx = it.LeadingEdgeSubTiles / Faketorio.Sim.Belts.BeltLine.TileSubTiles;
            int within = it.LeadingEdgeSubTiles % Faketorio.Sim.Belts.BeltLine.TileSubTiles;
            if (tileIdx >= line.Tiles.Count) tileIdx = line.Tiles.Count - 1;
            var (tx, ty) = line.Tiles[line.Tiles.Count - 1 - tileIdx];   // Tiles[^1] 是入口,前沿从出口数
            long subX = (long)tx * 256 + within;
            long subY = (long)ty * 256 + (long)(laneOffset * 256);
            var s = t.WorldSubToScreen(subX, subY).ToGodot();
            float sz = (float)t.PixelsPerTile * 0.22f;
            DrawRect(new Rect2(s - new Vector2(sz / 2, sz / 2), new Vector2(sz, sz)), RenderPalette.ForItem(it.ItemProtoId));
        }
    }

    private void DrawOrientation(Vector2 topLeft, Vector2 size, byte rot)
    {
        var c = topLeft + size / 2;
        float r = Mathf.Min(size.X, size.Y) * 0.25f;
        Vector2 tip = rot switch
        {
            1 => c + new Vector2(r, 0),
            2 => c + new Vector2(0, r),
            3 => c + new Vector2(-r, 0),
            _ => c + new Vector2(0, -r),
        };
        DrawLine(c, tip, new Color(0, 0, 0, 0.7f), 2);
    }
}
```
（`ToAbsolutePositions` 沿 lane 的定位、`Tiles` 索引方向,是最容易画错的地方 —— Task 6 验收看"物品是否沿带向出口方向移动、位置连续"。若方向反了,调 `line.Tiles[...]` 的索引;若 lane A/B 偏移方向不对,调 `laneOffset` 符号。`EntityPrototype` 是不是所有实体原型的基类、`TileWidth`/`TileHeight` 在哪层,以实际继承为准。）

- [ ] **Step 9: `game/BuildController.cs`**

拒绝检测思路:命令在下一个 `Sim.Step()` 才 apply(`Step()` 在 `SimHost._Process`)。所以**每帧轮询 `Sim.RejectedCommandCount`**,若比上一帧大 → 刚有命令被拒 → 起一个闪红计时器。不用 `CallDeferred`。

```csharp
using Godot;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Prototypes;
using Faketorio.Presentation.Core;

namespace Faketorio.Game;

/// 最小放置命令:左键放硬编码 proto,右键拆。无 UI。
public partial class BuildController : Node
{
    public (int X, int Y) HoverTile { get; private set; }
    public bool LastCommandRejected { get; private set; }

    private SimHost _host = null!;
    private CameraController _cam = null!;
    private int _chestProtoId;
    private int _rejectedSeen;
    private double _flashRemaining;

    public override void _Ready()
    {
        _host = GetNode<SimHost>("/root/SimHost");
        _cam = GetNode<CameraController>("../CameraController");
        _chestProtoId = _host.Sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        _rejectedSeen = _host.Sim.RejectedCommandCount;
    }

    public override void _Process(double delta)
    {
        HoverTile = _cam.Transform.ScreenToTile(GetViewport().GetMousePosition().ToCore());

        int rejectedNow = _host.Sim.RejectedCommandCount;
        if (rejectedNow > _rejectedSeen) _flashRemaining = 0.15;   // 上一帧 Step() 里刚拒了命令
        _rejectedSeen = rejectedNow;

        if (_flashRemaining > 0) _flashRemaining -= delta;
        LastCommandRejected = _flashRemaining > 0;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventMouseButton mb || !mb.Pressed) return;
        var (x, y) = _cam.Transform.ScreenToTile(mb.Position.ToCore());
        if (mb.ButtonIndex == MouseButton.Left)
            _host.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = _chestProtoId, X = x, Y = y });
        else if (mb.ButtonIndex == MouseButton.Right)
            _host.Submit(new Command { Type = CommandType.RemoveEntity, X = x, Y = y });
    }
}
```
（`InputEventMouseButton.Position` 是视口坐标,和 `GetMousePosition()` 一致。滚轮事件也是 `InputEventMouseButton`(`WheelUp`/`WheelDown`),但那些被 `CameraController._UnhandledInput` 先处理 —— 两个节点都收 `_UnhandledInput`,`CameraController` 处理滚轮/中键/`M`,`BuildController` 处理左右键,互不干扰;若发现事件被吞,把 `CameraController` 在场景里放 `BuildController` 之前,或在各自处理后 `GetViewport().SetInputAsHandled()`。Task 6 验收看左右键放/拆是否生效。）

- [ ] **Step 10: `game/Main.tscn`**
```
[gd_scene load_steps=5 format=3]

[ext_resource type="Script" path="res://CameraController.cs" id="1"]
[ext_resource type="Script" path="res://WorldView.cs" id="2"]
[ext_resource type="Script" path="res://BuildController.cs" id="3"]

[node name="Main" type="Node2D"]

[node name="CameraController" type="Camera2D" parent="."]
script = ExtResource("1")

[node name="BuildController" type="Node" parent="."]
script = ExtResource("3")

[node name="WorldView" type="Node2D" parent="."]
script = ExtResource("2")
```
（节点顺序:`CameraController` 在 `WorldView` 之前 → `WorldView._Draw` 读到本帧更新过的 `Transform`。`SimHost` 是 autoload,不在场景里。若 Godot 4.5 的 `.tscn` 头 `load_steps` 数不对,用编辑器建场景再存。）

- [ ] **Step 11: 编译检查**

Run:（在能跑 Godot 的机器上,或直接 dotnet)
```
dotnet build game/Game.csproj -c Debug
```
Expected: 编译通过(可能有 Godot source generator 的 warning,但 0 error)。若报 `Godot.NET.Sdk/4.5.1` 找不到 —— 确认本机 Godot 4.5.1 Mono 已装、`nuget` 能拉 `Godot.NET.Sdk`;实在不行让实现者用本机 Godot 的 `--headless --build-solutions` 编。
编译报的 API 错误(字段名/类名猜错)在这里暴露,按"参考"节和实际 sim 源码逐个修正。

- [ ] **Step 12: commit**
```bash
git add game/
git commit -m "feat(game): Godot 表现层 v1 —— SimHost/Camera/WorldView/BuildController

<trailer>"
```

---

### Task 6: 人工验收 + roadmap

**Files:**
- Modify: `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`

**这个 task 由控制器 + 人类做:实现者跑不了 Godot 渲染 + 交互。**

- [ ] **Step 1: 人工验收清单**（在 Godot 4.5.1 Mono 编辑器里打开 `game/`,F5)

对照 spec §7.4 逐条:
1. 运行无报错;看到网格 + 启动场景摆的实体 + 玩家圆点。
2. 传送带上有铁板方块在移动(sim 在 tick;`SimHost` 的调试喂料每 60 tick 补一个)。
3. 默认 `Follow`(锁定玩家格)。滚轮缩放生效。`M` 切 `Free`:中键拖拽平移生效、缩放范围放宽。再 `M` 切回 `Follow`:相机 tween 平滑回到玩家。
4. 左键点空格 → 下一帧出现一个箱子(色块 + 朝向线)。右键点它 → 消失。
5. 左键点已被占的格(比如传送带上)→ 光标格闪红一下、世界不变。
6. `Free` 模式拖到远处(几百格外)→ 看到程序化生成的矿脉色块。此前/此后在调试控制台各打一次 `SimHost.Sim.ComputeStateHash()`,两值**相同**(渲染读世界无副作用)。

任何一条不过 → 记录现象,进 fix 循环(改 `game/` 脚本;§Task 5 的括号注释列了最可能出错的点:相机变换二次叠加、传送带物品定位方向、拒绝闪红时序、prototype 字段名)。

- [ ] **Step 2: roadmap 更新**

`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`:
- "下一步" 段的 "⑤ 转向表现层" 改成 "✅ P14:Godot 表现层 v1 已合并(只读渲染 + 双模式相机骨架 + 最小放置命令)";把表现层后续(玩家 WASD 控制 + 相机跟随移动、视觉插值 lerp、真美术、完整命令 UI、保留模式渲染)列成新的候选。
- 加一段 P14 执行期确认:`Presentation.Core`(累加器 + 坐标变换,可测)+ `game/`(Godot,手动验收,不进 CI);sim 唯一改动 `ResourceGrid.PeekResourceAt`(无副作用读,golden 不变);`test` job 改跑 `dotnet test Faketorio.sln`;v1 snap 不插值、`TickAccumulator.Alpha` 预留;`SimHost` 有个每 60 tick 的调试喂料(未来接缝,真物流链落地删)。

- [ ] **Step 3: commit**
```bash
git add docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md
git commit -m "docs(p14): roadmap —— 表现层 v1 已落地

<trailer>"
```

---

## Self-Review

**1. Spec coverage:**

| spec 章节 | 对应 task |
|---|---|
| §2 `Presentation.Core` 项目 + `TickAccumulator` + `WorldTransform` | Task 1 + Task 2 |
| §2 sim 唯一改动 `ResourceGrid.PeekResourceAt` | Task 3 |
| §2 CI `test` job 改 `dotnet test Faketorio.sln` + sln 接线 + `.gitignore` | Task 1(sln add)+ Task 4(ci.yml + gitignore) |
| §3.1 `TickAccumulator`(TickSeconds / MaxCatchUpTicks / Advance / Alpha / 丢弃追不上部分) | Task 1 |
| §3.2 `WorldTransform`(SubTilesPerTile / WorldSubToScreen / TileToScreen / ScreenToTile / VisibleTileRect+margin) | Task 2 |
| §4 `SimHost`(autoload / data 路径 / SubmitStartupScene / _Process 按累加器 Step / 唯一 Step 点) | Task 5 Step 4 |
| §5 `CameraController` 双模式 + `ICameraTarget` + `PlayerCameraTarget` + M 切换 + tween | Task 5 Step 5–6 |
| §6 `WorldView` 立即模式七层绘制 + `RenderPalette` + 显示侧 chunk 缓存 | Task 5 Step 7–8 |
| §6 `BuildController` 左右键 → PlaceEntity/RemoveEntity + 拒绝闪红 | Task 5 Step 9 |
| §7.1 `Presentation.Core.Tests` | Task 1 + Task 2 |
| §7.2 `PeekResourceAt` 无副作用 + 哈希不变断言 | Task 3 Step 1 |
| §7.3 回归 + golden gate | Task 3 Step 4、Task 4 Step 3 |
| §7.4 `game/` 手动验收清单 | Task 6 Step 1 |
| §8 非目标 | 复制进 Global Constraints;未做的确实没做(无 lerp/无玩家控制/无 sprite/无保留模式/无迷雾) |
| §9 全局约束 | 复制进本 plan Global Constraints |
| §10 开放项 | Task 5 各 Step 的括号注释逐条给了具体取值 + "不符就按实际改"的指示(csproj 名、autoload 注册、Camera2D zoom 语义、启动场景坐标、data 路径回退、配色、Vec2 自带) |

覆盖完整。

**2. Placeholder scan:** 无 TBD / "类似 Task N" / "加错误处理" / 写坏的占位代码。所有 Godot 脚本给的是完整可编译的实现;Task 5 各 Step 的括号注释是"Godot 4.5.1 API 细节以实际为准 + 最可能出错的点 + 验收时看什么",不是留白。Godot 脚本没有 TDD 循环,靠 Task 5 Step 11 `dotnet build` 编译检查 + Task 6 人工验收兜。

**3. Type consistency:**
- `TickAccumulator` / `WorldTransform` / `Vec2` / `RectI` —— Task 1/2 定义,Task 5 的 `SimHost` / `CameraController` / `WorldView` / `BuildController` 一致消费(`Transform.ScreenToTile((int,int))`、`Transform.VisibleTileRect(3)`、`_acc.Advance(delta)`)。
- `SimHost.Sim` / `.Alpha` / `.Submit` —— Task 5 内部一致;`CameraController` / `WorldView` / `BuildController` 都 `GetNode<SimHost>("/root/SimHost")`。
- `CameraController.Transform`(`WorldTransform`)/ `.Mode` —— `WorldView` / `BuildController` 读一致。
- `BuildController.HoverTile`(`(int X,int Y)`)/ `.LastCommandRejected`(`bool`)—— `WorldView` Step 8 第 7 层读一致。
- sim API 全部来自"参考"节,签名照抄;不确定的(`ItemPrototype.FuelValueJ`、`ResourcePrototype.Name`、`EntityPrototype` 基类、`CallDeferred` 传参)都在括号里标了"以实际为准"。
- 提交 message 前缀:Task 1–2 `feat(presentation)`,Task 3 `feat(sim)`,Task 4 `ci(presentation)`,Task 5 `feat(game)`,Task 6 `docs(p14)` —— 一致。

一致性 OK。Godot 侧的类型/字段猜测风险集中在 Task 5,已用括号注释 + 编译检查 + 人工验收三重兜住。
