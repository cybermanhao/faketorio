# M1 Plan 16 — 玩家碰撞盒 + 传送带带人移动 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 sim 层玩家移动加一个"比放置占地更小"的实体碰撞盒(传送带/机械臂碰撞盒为空 → 可通行;密铺 3×3 机器之间留窄缝),并让站在传送带上的玩家沿带方向被施加位移(顺快逆慢,站着漂移),该位移由 `Player.Anchored` 开关可整体关掉。

**Architecture:** `EntityPrototype` 加 `CollisionInsetSubTiles` init-prop(默认 0 = 历史行为),`data/base` 的实体 JSON 逐条配。`Player` 加 `Anchored` bool(进 `WriteState`,本子项恒 false,只留缝)。`Simulation.PlayerWalk` 重写:玩家自走位移 + 传送带带人位移向量相加,目标子格点用"点所在 tile 的 `GetEntityAt`"测是否落在该实体的碰撞盒内(inset≥0 保证盒⊆占地,不用扫相邻 tile),落在盒内则整步拒绝。`Player.Anchored` 入 `WriteState` 会改状态哈希字节布局 → golden 主动重基线。

**Tech Stack:** C# / .NET 8,xUnit 2.9。纯 sim 改动,`game/` 与 `Faketorio.Presentation.Core` 零改动。

**Spec:** [`docs/superpowers/specs/2026-09-06-m1-plan16-player-collision-belt-carry-design.md`](../specs/2026-09-06-m1-plan16-player-collision-belt-carry-design.md)

## Global Constraints

- 只改 `sim/` + `data/base/*.json` + `bench/golden.json`。`game/`、`presentation/` 零改动。
- sim 状态不引入 `float`/`double`。`PlayerPointBlocked` 全整数比较。
- `CollisionInsetSubTiles` 是纯新增 init-prop,默认 `0` = 碰撞盒 == 占地 == 历史行为;不改 `IsAreaFree`/`OccupyArea`/放置占用语义。负 inset 加载期拒绝(`InvalidDataException`)。
- 8 方向 / 子格约定不变:1 tile = 256 子格。用 `BeltLine.TileSubTiles`(= 256,`namespace Faketorio.Sim.Belts`,`Simulation.cs` 已 `using` 该命名空间)作 sub-tile 常量,不新增常量。
- 传送带方向:`BeltNetwork.Delta(byte)` — 北=(0,-1) 东=(1,0) 南=(0,1) 西=(-1,0)。带速 = `Simulation.ResolveBeltSpeed(BeltLine)`(私有,已存在;基础带返回 8)。
- **golden 是主动重基线**(因 `Player.Anchored` 入 `WriteState` 多写一个 byte),不是"保持绿":Task 2 重生成 `bench/golden.json`、把 `baselineNsPerTick` 手动改回 `0`、更新 `BenchScenarioTests.GOLDEN_TICK_800`,之后门禁必须绿,`DeterminismTests` 必须全绿。Task 3 之后 golden 门禁应仍 PASS(bench 玩家停在空的 tile (0,0),`PlayerWalk` 新逻辑走 early-return,不再改哈希)。
- bench 场景全部实体在 x≈2046 附近(`UnitOriginX=2048`,`SpineX=2046`);tile (0,0) 为空;bench `ScenarioBuilder` 从不发 `MovePlayer`。
- 提交信息结尾带这两行(任务 commit 步骤里写作 `<trailer>`,逐字替换成这两行):
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_016Z7a7Yd8hWCT3UaWXorJQW
  ```

## 参考:现有代码事实(照此用,勿猜)

- `sim/Faketorio.Sim/Prototypes/EntityPrototype.cs` 当前全文:
  ```csharp
  namespace Faketorio.Sim.Prototypes;

  public abstract class EntityPrototype : PrototypeBase
  {
      public int TileWidth { get; init; } = 1;
      public int TileHeight { get; init; } = 1;
      public string? MinableResult { get; init; }
      public int MiningTimeTicks { get; init; }
  }

  public sealed class ContainerPrototype : EntityPrototype
  {
      public int InventorySize { get; init; }
  }
  ```
- `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` 的 `type switch`(约 `:221-333`):8 个 `EntityPrototype` 子类构造 arm,每个形如
  ```csharp
  "container" => ValidateFootprint(new ContainerPrototype
  {
      Name = name,
      TileWidth = GetInt(el, "tileWidth", 1),
      TileHeight = GetInt(el, "tileHeight", 1),
      // …其它字段
  }),
  ```
  8 个 type:`"container"`、`"transport-belt"`、`"electric-pole"`、`"fuel-generator"`、`"furnace"`、`"assembling-machine"`、`"mining-drill"`、`"inserter"`。`GetInt(JsonElement el, string prop, int fallback)` 已存在。`ValidateFootprint<T>(T proto) where T : EntityPrototype` 在 `:367`,现内容:
  ```csharp
  private static T ValidateFootprint<T>(T proto) where T : EntityPrototype
  {
      if (proto.TileWidth <= 0 || proto.TileHeight <= 0)
          throw new InvalidDataException(
              $"Entity prototype '{proto.Name}' has non-positive footprint " +
              $"(tileWidth={proto.TileWidth}, tileHeight={proto.TileHeight})");
      return proto;
  }
  ```
- `data/base` 实体分布在多个文件:`entities.json`(wooden-chest 1×1 / large-chest 2×3 / transport-belt-basic 1×1)、`electric.json`(small-electric-pole 1×1 / burner-generator 2×2)、`inserter.json`(inserter-basic 1×1)、`machines.json`(stone-furnace 2×2 / assembling-machine-1 3×3)、`mining-drill.json`(electric-mining-drill 2×2)。
- `sim/Faketorio.Sim/Player/Player.cs`:`public sealed class Player`,`namespace Faketorio.Sim`。属性 `int X/Y`(`private set`)、`byte WalkDir`、`bool Walking`、`bool Mining`、`int MineTargetX/Y`、`long MineProgress`。状态变更方法全 `internal`(`MoveTo(int,int)`、`SetWalk(byte)` 等)。`WriteState(IStateWriter writer)` 当前依次写:`X, Y, WalkDir, (byte)Walking, (byte)Mining, MineTargetX, MineTargetY, MineProgress, _craftQueue.Count + 每项, Inventory`。
- `sim/Faketorio.Sim/Simulation.cs`:
  - `PlayerWalk()`(`:178-185`)当前全文见 Spec §2。
  - `_playerProto`(私有 `PlayerPrototype` 字段),`_playerProto.WalkSpeedSubTilesPerTick` = 38。
  - `Player.WalkDelta(byte dir, int speed) -> (int dx, int dy)`(`internal static`)。
  - `ResolveBeltSpeed(BeltLine line) -> int`(`:285`,私有)。
  - 字段 `Belts`(`BeltNetwork`)、`World`(`WorldGrid`)、`Entities`、`Prototypes`。
  - `Belts.GetLineAt(int x, int y) -> BeltLineId`;`Belts.GetLine(BeltLineId) -> BeltLine`;`BeltLineId.IsValid`;`BeltLine.Direction`(byte)。
  - `World.GetEntityAt(int x, int y) -> EntityId`;`EntityId.IsValid`。
  - `Entities.Get(EntityId) -> ref EntityData`;`EntityData { int ProtoId; int X; int Y; byte Rotation; }`(`X,Y` = footprint 左上角 tile)。
  - `Prototypes.GetById(int) -> PrototypeBase`。
- `sim/Faketorio.Sim/AssemblyMarker.cs`:`[assembly: InternalsVisibleTo("Faketorio.Sim.Tests")]` —— 测试可直接调 `Player.MoveTo` 等 internal 成员。
- 测试文件:`sim/Faketorio.Sim.Tests/{PlayerTests.cs, PrototypeLoaderTests.cs, SimulationTests.cs, DeterminismTests.cs, BenchScenarioTests.cs}`。
- `BenchScenarioTests.cs:79`:`private const ulong GOLDEN_TICK_800 = 17279884607642786012UL;`;`:59` `SmallScale_HashSequence_IsStableAcrossRuns` 断言 `h1[799] == GOLDEN_TICK_800`。`DeterminismTests.PlayerScenario_SameSeedSameCommands_SameHashEveryTick` 必须保持绿。
- golden 重生成:`dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --update-golden --golden bench/golden.json`(会顺带把 `baselineNsPerTick` 写成本机实测值 —— 之后手动改回 `0`)。校验:`dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json` → `exit 0`、输出含 `gate PASS`。
- 全量测试:`dotnet test Faketorio.sln -c Release`。

## File Structure

| 文件 | 改动 | 责任 |
|---|---|---|
| `sim/Faketorio.Sim/Prototypes/EntityPrototype.cs` | Modify | 加 `CollisionInsetSubTiles` init-prop |
| `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` | Modify | 8 个 arm 读 `collisionInsetSubTiles`;`ValidateFootprint` 加负值校验 |
| `data/base/entities.json` | Modify | transport-belt-basic inset 128 |
| `data/base/inserter.json` | Modify | inserter-basic inset 128 |
| `data/base/machines.json` | Modify | stone-furnace 64 / assembling-machine-1 96 |
| `data/base/mining-drill.json` | Modify | electric-mining-drill 64 |
| `data/base/electric.json` | Modify | burner-generator 64(pole 不写) |
| `sim/Faketorio.Sim/Player/Player.cs` | Modify | `Anchored` + `SetAnchored` + `WriteState` +1 byte |
| `sim/Faketorio.Sim/Simulation.cs` | Modify | `PlayerWalk` 重写 + `PlayerPointBlocked` 新增 |
| `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` | Modify | inset 加载 + 负值校验用例 |
| `sim/Faketorio.Sim.Tests/PlayerTests.cs` | Modify | `Anchored` 默认/翻转/`WriteState` 用例 |
| `sim/Faketorio.Sim.Tests/SimulationTests.cs` | Modify | 碰撞 + 带人移动 + 锚定 用例 |
| `bench/golden.json` | Regenerate | Task 2 重基线(`baselineNsPerTick` 保持 0) |
| `sim/Faketorio.Sim.Tests/BenchScenarioTests.cs` | Modify | `GOLDEN_TICK_800` 更新 + 注释 |

3 个任务:**1** prototype 碰撞盒字段 + 数据(无 golden 影响)→ **2** `Player.Anchored` + `WriteState` + golden 重基线 → **3** `PlayerWalk` 重写 + `PlayerPointBlocked`。

---

## Task 1: `EntityPrototype.CollisionInsetSubTiles` + 加载 + 校验 + 数据

**Files:**
- Modify: `sim/Faketorio.Sim/Prototypes/EntityPrototype.cs`
- Modify: `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`
- Modify: `data/base/entities.json`, `data/base/inserter.json`, `data/base/machines.json`, `data/base/mining-drill.json`, `data/base/electric.json`
- Test: `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs`

**Interfaces:**
- Consumes: 无(第一个任务)。
- Produces: `EntityPrototype.CollisionInsetSubTiles`(`int`,`{ get; init; }`,默认 0)。含义:玩家碰撞盒 = 占地矩形四边各向内缩这么多子格。`data/base` 里 transport-belt-basic 与 inserter-basic 为 128,electric-mining-drill / stone-furnace / burner-generator 为 64,assembling-machine-1 为 96,其余(chest / pole)不写(0)。

- [ ] **Step 1: Write the failing test**

在 `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` 加(文件顶部若无 `using System.IO;` 则加):

```csharp
    [Fact]
    public void CollisionInsetSubTiles_LoadsFromJson_DefaultsToZero()
    {
        var protos = PrototypeLoader.LoadFromDirectory("data/base");

        // 显式配了的
        Assert.Equal(128, protos.Get<TransportBeltPrototype>("transport-belt-basic").CollisionInsetSubTiles);
        Assert.Equal(128, protos.Get<InserterPrototype>("inserter-basic").CollisionInsetSubTiles);
        Assert.Equal(64,  protos.Get<MiningDrillPrototype>("electric-mining-drill").CollisionInsetSubTiles);
        Assert.Equal(64,  protos.Get<FurnacePrototype>("stone-furnace").CollisionInsetSubTiles);
        Assert.Equal(64,  protos.Get<FuelGeneratorPrototype>("burner-generator").CollisionInsetSubTiles);
        Assert.Equal(96,  protos.Get<AssemblingMachinePrototype>("assembling-machine-1").CollisionInsetSubTiles);

        // 没配的 → 0
        Assert.Equal(0, protos.Get<ContainerPrototype>("wooden-chest").CollisionInsetSubTiles);
        Assert.Equal(0, protos.Get<ElectricPolePrototype>("small-electric-pole").CollisionInsetSubTiles);
    }

    [Fact]
    public void CollisionInsetSubTiles_Negative_ThrowsAtLoad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "faketorio-neg-inset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 只放一个非法实体文件 + 其它必需类别的最小副本会很繁琐;直接复制 data/base 再覆盖一个文件。
            foreach (var f in Directory.GetFiles("data/base", "*.json"))
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            File.WriteAllText(Path.Combine(dir, "entities.json"),
                "[{ \"type\": \"transport-belt\", \"name\": \"transport-belt-basic\", " +
                "\"tileWidth\": 1, \"tileHeight\": 1, \"speedSubTilesPerTick\": 8, " +
                "\"collisionInsetSubTiles\": -1 }]");

            var ex = Assert.ThrowsAny<InvalidDataException>(() => PrototypeLoader.LoadFromDirectory(dir));
            Assert.Contains("collisionInsetSubTiles", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
```

(若 `PrototypeLoaderTests.cs` 已有别的 `using`/命名空间,沿用即可;`protos.Get<T>(name)` 是既有 API — 全仓库多处在用。若某个 `Get<T>` 的确切类型名不确定,照 `sim/Faketorio.Sim/Prototypes/` 里的类名:`TransportBeltPrototype`/`InserterPrototype`/`MiningDrillPrototype`/`FurnacePrototype`/`FuelGeneratorPrototype`/`AssemblingMachinePrototype`/`ContainerPrototype`/`ElectricPolePrototype`。)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test sim/Faketorio.Sim.Tests -c Release --filter "FullyQualifiedName~CollisionInsetSubTiles"`
Expected: FAIL — `CollisionInsetSubTiles` 不存在(编译错误)。

- [ ] **Step 3: 加字段到 `EntityPrototype`**

`sim/Faketorio.Sim/Prototypes/EntityPrototype.cs`,在 `MiningTimeTicks` 之后加:

```csharp
    // 玩家碰撞盒 = 完整占地矩形四边各向内缩这么多子格(1 tile = 256)。
    // 0 = 碰撞盒 == 占地(默认,历史行为)。大到某轴 min >= max 时碰撞盒为空,
    // 该实体对玩家完全可通行。只影响玩家移动;放置/占用仍用 TileWidth × TileHeight。
    public int CollisionInsetSubTiles { get; init; }
```

- [ ] **Step 4: 8 个 arm 读 JSON + `ValidateFootprint` 加校验**

`sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`:在 `"container"`、`"transport-belt"`、`"electric-pole"`、`"fuel-generator"`、`"furnace"`、`"assembling-machine"`、`"mining-drill"`、`"inserter"` **这 8 个 arm 每个的 `TileHeight = GetInt(el, "tileHeight", 1),` 那一行之后**加:

```csharp
                CollisionInsetSubTiles = GetInt(el, "collisionInsetSubTiles", 0),
```

`ValidateFootprint` 改成:

```csharp
    private static T ValidateFootprint<T>(T proto) where T : EntityPrototype
    {
        if (proto.TileWidth <= 0 || proto.TileHeight <= 0)
            throw new InvalidDataException(
                $"Entity prototype '{proto.Name}' has non-positive footprint " +
                $"(tileWidth={proto.TileWidth}, tileHeight={proto.TileHeight})");
        if (proto.CollisionInsetSubTiles < 0)
            throw new InvalidDataException(
                $"Entity prototype '{proto.Name}' has negative collisionInsetSubTiles " +
                $"({proto.CollisionInsetSubTiles}) — 碰撞盒不能大于占地");
        return proto;
    }
```

- [ ] **Step 5: 数据文件逐条加 `collisionInsetSubTiles`**

`data/base/entities.json` — transport-belt-basic 这条加 `"collisionInsetSubTiles": 128`:
```json
  { "type": "transport-belt", "name": "transport-belt-basic", "tileWidth": 1, "tileHeight": 1,
    "speedSubTilesPerTick": 8, "collisionInsetSubTiles": 128 }
```
(wooden-chest / large-chest 两条**不动**。)

`data/base/inserter.json`:
```json
  { "type": "inserter", "name": "inserter-basic", "tileWidth": 1, "tileHeight": 1,
    "rotationTimeSeconds": 0.5, "energyUsage": "5kW", "collisionInsetSubTiles": 128 }
```

`data/base/machines.json`:
```json
  { "type": "furnace", "name": "stone-furnace", "tileWidth": 2, "tileHeight": 2,
    "category": "smelting", "inputSlots": 1, "outputSlots": 1, "energyUsage": "90kW",
    "collisionInsetSubTiles": 64 },
  { "type": "assembling-machine", "name": "assembling-machine-1", "tileWidth": 3, "tileHeight": 3,
    "category": "crafting", "inputSlots": 2, "outputSlots": 1, "energyUsage": "75kW",
    "collisionInsetSubTiles": 96 }
```

`data/base/mining-drill.json`:
```json
  { "type": "mining-drill", "name": "electric-mining-drill", "tileWidth": 2, "tileHeight": 2,
    "energyUsage": "90kW", "collisionInsetSubTiles": 64 }
```

`data/base/electric.json` — burner-generator 这条加 `"collisionInsetSubTiles": 64`(small-electric-pole **不动**):
```json
  { "type": "electric-pole", "name": "small-electric-pole", "tileWidth": 1, "tileHeight": 1,
    "maximumWireDistanceTiles": 7, "supplyAreaDistanceTiles": 2 },
  { "type": "fuel-generator", "name": "burner-generator", "tileWidth": 2, "tileHeight": 2,
    "powerOutput": "90kW", "fuelItemName": "coal", "collisionInsetSubTiles": 64 }
```

保持每个文件原有 JSON 格式(缩进、逗号)。

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test sim/Faketorio.Sim.Tests -c Release --filter "FullyQualifiedName~CollisionInsetSubTiles"`
Expected: PASS(2 个用例)。

- [ ] **Step 7: 全量测试 + golden 门禁(应无变化)**

Run: `dotnet test Faketorio.sln -c Release`
Expected: 全绿(prototype 不进 `WriteState`,哈希不变;sim 测试数 = 基线 + 2)。

Run: `dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json`
Expected: `exit 0`,`gate PASS`(本任务不改哈希)。

- [ ] **Step 8: Commit**

```bash
git add sim/Faketorio.Sim/Prototypes/EntityPrototype.cs sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs data/base/entities.json data/base/inserter.json data/base/machines.json data/base/mining-drill.json data/base/electric.json sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs
git commit -m "feat(sim): EntityPrototype.CollisionInsetSubTiles —— 玩家碰撞盒 <= 放置占地

<trailer>"
```
(`<trailer>` = Global Constraints 里那两行,逐字替换。)

---

## Task 2: `Player.Anchored` + `WriteState` byte + golden 重基线

**Files:**
- Modify: `sim/Faketorio.Sim/Player/Player.cs`
- Regenerate: `bench/golden.json`
- Modify: `sim/Faketorio.Sim.Tests/BenchScenarioTests.cs`(`GOLDEN_TICK_800`)
- Test: `sim/Faketorio.Sim.Tests/PlayerTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `CollisionInsetSubTiles`(不直接用,只是同分支基线)。
- Produces: `Player.Anchored`(`bool`,`{ get; private set; }`,默认 `false`);`internal void Player.SetAnchored(bool value)`。`WriteState` 在 `Mining` 那个 byte 之后、`MineTargetX` 之前多写一个 `(byte)(Anchored ? 1 : 0)`。

- [ ] **Step 1: Write the failing test**

在 `sim/Faketorio.Sim.Tests/PlayerTests.cs` 加:

```csharp
    [Fact]
    public void Anchored_DefaultsFalse_AndSetAnchoredToggles()
    {
        var p = new Player(10);
        Assert.False(p.Anchored);
        p.SetAnchored(true);
        Assert.True(p.Anchored);
        p.SetAnchored(false);
        Assert.False(p.Anchored);
    }

    [Fact]
    public void Anchored_IsCoveredByWriteState()
    {
        static ulong Hash(Player pl)
        {
            var w = new Faketorio.Sim.State.Fnv1aHashWriter();
            pl.WriteState(w);
            return w.Hash;
        }

        var a = new Player(10);
        var b = new Player(10);
        Assert.Equal(Hash(a), Hash(b));      // 同状态同哈希

        b.SetAnchored(true);
        Assert.NotEqual(Hash(a), Hash(b));   // Anchored 进了序列化
    }
```

(`Fnv1aHashWriter` 是既有类型,`DeterminismTests` / `BeltNetworkTests` 都在用;若 `PlayerTests.cs` 里已有取哈希的 helper,沿用它。)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test sim/Faketorio.Sim.Tests -c Release --filter "FullyQualifiedName~Anchored"`
Expected: FAIL — `Anchored` / `SetAnchored` 不存在。

- [ ] **Step 3: 加 `Anchored` + `SetAnchored` + `WriteState`**

`sim/Faketorio.Sim/Player/Player.cs`:

在 `public bool Walking { get; private set; }` 之后加属性:
```csharp
    // 锚定:true 时传送带不带着玩家走。由将来的"锚定模块"子项翻转;本子项恒 false。
    public bool Anchored { get; private set; }
```

在 `internal void StopWalk() => Walking = false;` 附近加:
```csharp
    internal void SetAnchored(bool value) => Anchored = value;
```

`WriteState` —— 在 `writer.Write((byte)(Mining ? 1 : 0));` 之后、`writer.Write(MineTargetX);` 之前,插入:
```csharp
        writer.Write((byte)(Anchored ? 1 : 0));
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test sim/Faketorio.Sim.Tests -c Release --filter "FullyQualifiedName~Anchored"`
Expected: PASS(2 个)。

- [ ] **Step 5: 确认哈希红线现在失败(预期)**

Run: `dotnet test sim/Faketorio.Sim.Tests -c Release --filter "FullyQualifiedName~SmallScale_HashSequence_IsStableAcrossRuns"`
Expected: **FAIL** —— `h1[799]` != 旧 `GOLDEN_TICK_800`(WriteState 多了一个 byte)。**这是预期的**,下一步重基线。
把失败输出里的 `Actual:` 值记下(新的 tick-800 哈希)。

Run: `dotnet test sim/Faketorio.Sim.Tests -c Release --filter "FullyQualifiedName~DeterminismTests"`
Expected: **全绿** —— 新逻辑是确定的,同 seed 跑两遍仍逐 tick 一致。若这里有 FAIL,停下报 BLOCKED(说明 `Anchored` 引入了非确定性,不该发生)。

- [ ] **Step 6: 重生成 golden.json**

Run: `dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --update-golden --golden bench/golden.json`
Expected: `exit 0`。

然后 **手动编辑 `bench/golden.json`**:把 `"baselineNsPerTick"` 的值改回 `0`(`--update-golden` 会写成本机实测值;项目约定保持 `0` = 性能门禁 SKIP,只跑哈希门禁。`recordedAtCommit` / `recordedAtUtc` 保持 `--update-golden` 写的新值)。

- [ ] **Step 7: 更新 `GOLDEN_TICK_800`**

`sim/Faketorio.Sim.Tests/BenchScenarioTests.cs`:把
```csharp
    private const ulong GOLDEN_TICK_800 = 17279884607642786012UL;
```
换成(用 Step 5 记下的新哈希):
```csharp
    // 因 Player.Anchored 入 WriteState(多一个 byte)+ 玩家碰撞模型改动重基线(P16)。
    // determinism 断言(h1 == h2)不受影响,只有这个锚点值移动。
    private const ulong GOLDEN_TICK_800 = <新的十进制哈希>UL;
```

- [ ] **Step 8: 验证门禁 + 全量**

Run: `dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json`
Expected: `exit 0`,`gate PASS`。

Run: `dotnet test Faketorio.sln -c Release`
Expected: 全绿(含 `SmallScale_HashSequence_IsStableAcrossRuns` 现在对新锚点通过)。

- [ ] **Step 9: Commit**

```bash
git add sim/Faketorio.Sim/Player/Player.cs bench/golden.json sim/Faketorio.Sim.Tests/BenchScenarioTests.cs sim/Faketorio.Sim.Tests/PlayerTests.cs
git commit -m "feat(sim): Player.Anchored —— 锚定开关(留给后续锚定模块)+ golden 重基线

<trailer>"
```

---

## Task 3: `PlayerWalk` 重写 + `PlayerPointBlocked`

**Files:**
- Modify: `sim/Faketorio.Sim/Simulation.cs`
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`

**Interfaces:**
- Consumes: Task 1 `EntityPrototype.CollisionInsetSubTiles`;Task 2 `Player.Anchored` + `Player.SetAnchored`。
- Produces: 无(内部行为改动)。

- [ ] **Step 1: Write the failing tests**

在 `sim/Faketorio.Sim.Tests/SimulationTests.cs` 加(沿用文件里既有的 `NewSim()` / `PlaceBelt` / `PlaceChest` / 常量 helper;若某 helper 名不同,照文件里实际的用)。以下用例假设 `NewSim()` 造一个默认 seed 的 `Simulation`,`sim.Player` 可读,`sim.Player.MoveTo(x, y)` / `sim.Player.SetWalk(dir)` / `sim.Player.StopWalk()` / `sim.Player.SetAnchored(bool)` 是可用的 internal:

```csharp
    // P16: 玩家碰撞盒 + 传送带带人移动 --------------------------------------

    private const int St = Faketorio.Sim.Belts.BeltLine.TileSubTiles; // 256

    // 放一台 3×3 装配机(左上角在 (x,y))。没有现成 Place* helper,取 id 用和其它
    // Place* 一样的 sim.Prototypes.Get<T>("name").Id。
    private static Command PlaceAssembler(Simulation sim, int x, int y) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<Faketorio.Sim.Prototypes.AssemblingMachinePrototype>("assembling-machine-1").Id,
        X = x, Y = y,
    };

    [Fact]
    public void Player_BlockedByChest_WholeStepReject()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 3, 0));
        sim.Step();

        // 玩家在 (2,0) 格中心,朝东走(chest inset 0 → 硬挡)
        sim.Player.MoveTo(2 * St + St / 2, 0 * St + St / 2);
        sim.Player.SetWalk(2); // 东
        int x0 = sim.Player.X;
        for (int t = 0; t < 20; t++) sim.Step();

        // 没能走进 (3,0) 的占地:x 前沿始终 < 3*256
        Assert.True(sim.Player.X < 3 * St, $"expected blocked before x=768, got {sim.Player.X}");
    }

    [Fact]
    public void Player_WalksOntoBelt_AndDriftsWhenIdle()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 0, 1)); // 单格带,rot 1 = 东(Command.Rotation: 0/1/2/3 = 北/东/南/西;BeltNetwork.Delta(1)=(1,0))
        sim.Step();

        sim.Player.MoveTo(5 * St + St / 2, 0 * St + St / 2); // 站在带上
        sim.Player.StopWalk();                                // 不走
        int y0 = sim.Player.Y;

        for (int t = 0; t < 10; t++) sim.Step();

        // 每 tick 沿带方向漂移 carry(基础带 8);10 tick → +80 子格,Y 不变
        Assert.Equal(5 * St + St / 2 + 10 * 8, sim.Player.X);
        Assert.Equal(y0, sim.Player.Y);
    }

    [Fact]
    public void Player_WalkingWithBelt_AddsCarry()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 0, 1)); // 东
        sim.Step();
        sim.Player.MoveTo(5 * St + St / 2, 0 * St + St / 2);
        sim.Player.SetWalk(2); // 东,顺带
        int x0 = sim.Player.X;
        sim.Step();
        Assert.Equal(x0 + 38 + 8, sim.Player.X); // walk 38 + carry 8
    }

    [Fact]
    public void Player_WalkingAgainstBelt_NetSlower()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 0, 1)); // 东
        sim.Step();
        sim.Player.MoveTo(5 * St + St / 2, 0 * St + St / 2);
        sim.Player.SetWalk(6); // 西,逆带
        int x0 = sim.Player.X;
        sim.Step();
        Assert.Equal(x0 - 38 + 8, sim.Player.X); // -30:仍向西,但更慢
    }

    [Fact]
    public void Player_Anchored_NotCarriedByBelt()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 0, 1));
        sim.Step();
        sim.Player.MoveTo(5 * St + St / 2, 0 * St + St / 2);
        sim.Player.StopWalk();
        sim.Player.SetAnchored(true);
        int x0 = sim.Player.X, y0 = sim.Player.Y;
        for (int t = 0; t < 10; t++) sim.Step();
        Assert.Equal(x0, sim.Player.X);
        Assert.Equal(y0, sim.Player.Y);
    }

    [Fact]
    public void Player_PassesThroughSeamBetweenTiledAssemblers()
    {
        var sim = NewSim();
        // 两台 3×3 装配机,原点 (10,0) 与 (13,0) —— 密铺,东西相邻无 gap
        sim.Submit(PlaceAssembler(sim, 10, 0));
        sim.Submit(PlaceAssembler(sim, 13, 0));
        sim.Step();

        // 两机之间的 X 缝:A.box maxX = 13*256-96 = 3232;B.box minX = 13*256+96 = 3424。
        // 缝中点 X = 13*256 = 3328。玩家在缝里,从南(y 大)往北(y 小)穿过 3×3 的整段高度。
        int seamX = 13 * St;
        sim.Player.MoveTo(seamX, 3 * St);     // 机器占 y∈[0,3),从 y=3*256 起(机器南边外)
        sim.Player.SetWalk(0);                // 北
        for (int t = 0; t < 60; t++) sim.Step();

        // 穿过去了:y 前沿越过了机器北边 y=0
        Assert.True(sim.Player.Y < 0, $"expected to pass the seam to y<0, got {sim.Player.Y}");
        Assert.Equal(seamX, sim.Player.X);   // 没有横向漂移
    }

    [Fact]
    public void Player_BlockedByAssemblerCenter()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 10, 0));
        sim.Step();

        // 正对机器几何中心那一列 (x = 10*256 + 3*128 = 2944) 从南往北走 → 被实心块挡
        int centerX = 10 * St + 3 * St / 2;
        sim.Player.MoveTo(centerX, 4 * St);
        sim.Player.SetWalk(0); // 北
        for (int t = 0; t < 60; t++) sim.Step();

        // box: minY = 96, maxY = 3*256-96 = 672。玩家从 y=1024 往北,应停在 maxY(672) 前沿附近,不穿过。
        Assert.True(sim.Player.Y >= 3 * St - 96, $"expected blocked at/after box maxY=672, got {sim.Player.Y}");
    }

    [Fact]
    public void PlayerCollisionBox_RightEdgeIsHalfOpen()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 10, 0));
        sim.Step();

        // 装配机 (10,0) 3×3。碰撞盒 maxX = 13*256 - 96 = 3232,y 盒 [96, 672)。
        // 半开:玩家能恰好停在 x == 3232,停不进 3231。
        // 从东侧 x=3346 朝西走(每 tick -38):3308 -> 3270 -> 3232(恰在 maxX,不算撞,可停)
        //   -> 下一步 3194 落入盒 -> 整步拒绝。最终停在 3232。
        // 若判定误用 px <= maxX,则 3232 也算撞,玩家会停在 3270。
        sim.Player.MoveTo(3232 + 38 * 3, 1 * St);   // (3346, 256)
        sim.Player.SetWalk(6);                        // 西(八向 6 = 西)
        for (int t = 0; t < 10; t++) sim.Step();

        Assert.Equal(3232, sim.Player.X);
        Assert.Equal(1 * St, sim.Player.Y);
    }
```

> **实现者注意**:
> - `SimulationTests.cs` 里既有 `NewSim()`、`PlaceBelt(sim, x, y, rot)`、`PlaceChest(sim, x, y)`。用例顶部的 `PlaceAssembler` helper 需要你加进该文件(和其它 `Place*` 放一起,已在 Step 1 代码块给出)。若这些 helper 的确切名字/签名与文件里不符,照文件里实际的改。
> - `sim.Player.SetWalk(dir)` 的 `dir` 是**八向**(`Player.WalkDelta` 约定:`0=北,2=东,4=南,6=西`),用例里 `SetWalk(2)` = 东。`PlaceBelt` 的 `rot` 是**四向** `Command.Rotation`(`0/1/2/3 = 北/东/南/西`),用例里 `1` = 东,`BeltNetwork.Delta(1)=(1,0)`。两套约定都对,别混。若 `PlaceBelt` 的 `rot` 形参不是直传 `Command.Rotation`,核对 `PlaceBelt` 定义后按实际改,**以 `BeltNetwork.Delta` 为最终依据**。
> - `NewSim()` 默认种子下,(3,0)/(5,0)/(10,0)/(13,0) 这些格子放实体不受矿脉影响;若某放置命令被拒(`sim.RejectedCommandCount` 增加),换空地坐标并同步调整断言常量。

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test sim/Faketorio.Sim.Tests -c Release --filter "FullyQualifiedName~SimulationTests&(FullyQualifiedName~Player_Blocked|FullyQualifiedName~Player_Walks|FullyQualifiedName~Player_Walking|FullyQualifiedName~Player_Anchored|FullyQualifiedName~Player_Passes|FullyQualifiedName~PlayerCollisionBox)"`
Expected: FAIL —— 目前 `PlayerWalk` 撞任何实体就整步拒绝、不认碰撞盒、无带人逻辑,所以 `Player_WalksOntoBelt_*` / `Player_Passes*` 等会失败(玩家进不去带/缝)。

- [ ] **Step 3: 重写 `PlayerWalk` + 加 `PlayerPointBlocked`**

`sim/Faketorio.Sim/Simulation.cs`,把 `PlayerWalk()`(`:178-185`)整体替换为:

```csharp
    private void PlayerWalk()
    {
        // 1. 玩家自己的行走位移(不走路则 0)
        int wdx = 0, wdy = 0;
        if (Player.Walking)
            (wdx, wdy) = Player.WalkDelta(Player.WalkDir, _playerProto.WalkSpeedSubTilesPerTick);

        // 2. 传送带带人位移:玩家当前所在 tile 是传送带、且未锚定
        int cdx = 0, cdy = 0;
        if (!Player.Anchored)
        {
            // 子格 -> tile 用算术右移(负数也是向下取整,与 Simulation.cs:184 的 nx>>8 一致)
            var lineId = Belts.GetLineAt(Player.X >> 8, Player.Y >> 8);
            if (lineId.IsValid)
            {
                var line = Belts.GetLine(lineId);
                var (bdx, bdy) = BeltNetwork.Delta(line.Direction);
                int carry = ResolveBeltSpeed(line);
                cdx = bdx * carry;
                cdy = bdy * carry;
            }
        }

        if (wdx == 0 && wdy == 0 && cdx == 0 && cdy == 0) return;

        int nx = Player.X + wdx + cdx;
        int ny = Player.Y + wdy + cdy;

        if (PlayerPointBlocked(nx, ny)) return;   // 整步拒绝,不滑墙
        Player.MoveTo(nx, ny);
    }

    // 目标子格点 (px,py) 是否落在某实体的玩家碰撞盒内。
    // CollisionInsetSubTiles >= 0 保证碰撞盒 ⊆ 占地,故点若在某盒内,该实体必占
    // 点所在的那个 tile —— 只查 GetEntityAt(该 tile) 即可,无需扫相邻 tile。
    private bool PlayerPointBlocked(int px, int py)
    {
        const int St = BeltLine.TileSubTiles;

        var id = World.GetEntityAt(px >> 8, py >> 8);   // 算术右移 = 向下取整,负坐标也对
        if (!id.IsValid) return false;

        ref readonly var data = ref Entities.Get(id);
        var proto = (EntityPrototype)Prototypes.GetById(data.ProtoId);
        int inset = proto.CollisionInsetSubTiles;

        long minX = (long)data.X * St + inset;
        long maxX = (long)(data.X + proto.TileWidth) * St - inset;
        long minY = (long)data.Y * St + inset;
        long maxY = (long)(data.Y + proto.TileHeight) * St - inset;
        if (minX >= maxX || minY >= maxY) return false;   // 碰撞盒为空 → 可通行

        return px >= minX && px < maxX && py >= minY && py < maxY;
    }
```

要点:
- 顶部 `using` :`Simulation.cs` 现已有 `using Faketorio.Sim.Belts;`(用了 `BeltNetwork`)和 `using Faketorio.Sim.Prototypes;`。`BeltLine.TileSubTiles` 在 `Faketorio.Sim.Belts`。`EntityData` 在 `Faketorio.Sim.Entities` —— 若 `Simulation.cs` 未 `using` 该命名空间(现有代码里已多处 `ref var data = ref Entities.GetAtIndex(...)`,大概率已 using),缺则加。
- `>> 8`(算术右移)= 子格向下取整到 tile,负数也正确(`-1 >> 8 == -1`),与既有 `Simulation.cs:184` 的 `nx >> 8` 同款,不要用 `/ 256`(负数向 0 取整会错)。
- `ref readonly var data = ref Entities.Get(id)` —— `Entities.Get` 返回 `ref EntityData`,只读取用 `ref readonly` narrow(`ref var` 也行)。
- `long` 中间量防大坐标乘 256 溢出 int(测试规模不会,但 game 里玩家可跑远)。`px`(int)与 `long` 比较自动提升。

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test sim/Faketorio.Sim.Tests -c Release --filter "FullyQualifiedName~SimulationTests&(FullyQualifiedName~Player_Blocked|FullyQualifiedName~Player_Walks|FullyQualifiedName~Player_Walking|FullyQualifiedName~Player_Anchored|FullyQualifiedName~Player_Passes|FullyQualifiedName~PlayerCollisionBox)"`
Expected: PASS(8 个)。若某个断言的方向/数值因 `PlaceBelt` 方向约定或 `NewSim` 的种子矿脉布局不符,按 Step 1 的实现者注意调整用例常量(不是改产品代码)。

- [ ] **Step 5: 全量测试 + 确定性 + golden 门禁**

Run: `dotnet test Faketorio.sln -c Release`
Expected: 全绿。特别是 `DeterminismTests.PlayerScenario_SameSeedSameCommands_SameHashEveryTick` 和 `BenchScenarioTests.SmallScale_HashSequence_IsStableAcrossRuns` —— 后者应对 Task 2 定的新 `GOLDEN_TICK_800` **仍然通过**(bench 玩家停在空 tile (0,0),新 `PlayerWalk` 走 early-return,`Player.X/Y` 不变,哈希不因本任务再变)。

Run: `dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json`
Expected: `exit 0`,`gate PASS`。
**若门禁 FAIL**:说明 bench 里 (0,0) 意外有传送带、或 `PlayerWalk` 逻辑有副作用。先确认是不是"玩家在 (0,0) 被带漂移"这一种合法变化(检查 bench 布局);是则 `--update-golden` 重来一次 + 再更新 `GOLDEN_TICK_800`(带注释);不是则停下报 BLOCKED。

- [ ] **Step 6: Commit**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "feat(sim): PlayerWalk 重写 —— 碰撞盒(点 vs 盒)+ 传送带带人移动

<trailer>"
```

---

## Definition of Done

- [ ] `EntityPrototype.CollisionInsetSubTiles` 加载正确(belt/inserter 128、机器 64/96、chest/pole 0);负值加载期抛 `InvalidDataException`。
- [ ] `Player.Anchored` 默认 false、`SetAnchored` 翻转、进 `WriteState`(设 true 时哈希不同)。
- [ ] `PlayerWalk`:走向 inset=0 实体被整步拒绝(旧行为保持);走上 inset=128 的带 → 进得去 + 沿带漂移;顺带净速 = walk+carry、逆带 = carry−walk;`Anchored=true` 不漂移;密铺两台 3×3 机器之间能单列穿过、正对中心被挡;碰撞盒右/下边半开。
- [ ] `dotnet test Faketorio.sln -c Release` 全绿;`DeterminismTests` 全绿。
- [ ] `bench/golden.json` 已重生成,`baselineNsPerTick` = `0`;`BenchScenarioTests.GOLDEN_TICK_800` 已同步(带注释);`dotnet run ... --golden bench/golden.json` → `exit 0, gate PASS`。
- [ ] `game/`、`presentation/` 无改动;`git diff --stat <base>` 只含 `sim/**`、`data/base/*.json`、`bench/golden.json`、`docs/`(plan 头 stamp,收尾时)。
- [ ] sim 状态无 `float`/`double`。
