# 建造成本机制 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 放置建筑消耗玩家库存物品、拆除建筑归还物品，新增 sim 命令 `BuildFromInventory` 承载这套经济机制,`PlaceEntity` 保持免费不变(继续给 bench/demo 场景搭建用)。

**Architecture:** sim 层新增 `CommandType.BuildFromInventory`,处理逻辑为"距离检查 → 占地检查 → 库存检查 → 扣 1 个物品 → 创建实体",内部复用从 `PlaceEntity` 抽出来的共享实体创建/注册 helper。7 种建筑补上物品原型/配方/`minableResult`(复用已有的手挖机制解决"归还"这一半,不新写机制)。`PrototypeLoader.cs` 里除了 `container` 之外的 6 种实体类型分支目前都不解析 `minableResult`/`miningTimeSeconds` 字段,需要先补上这个解析能力,数据才生效。最后 `game/BuildController.cs` 接入新命令(左键)+ 复用手挖模式接长按右键拆除。

**Tech Stack:** C# / .NET 8,xUnit(sim 层 TDD),Godot 4.5.1 Mono(game 层,人工 F5 验收,无自动化测试)。

**Spec:** [`docs/superpowers/specs/2026-09-17-build-cost-design.md`](../specs/2026-09-17-build-cost-design.md)

## Global Constraints

- `PlaceEntity` 命令的行为**完全不能变**——不加任何检查、不消耗库存。`ScenarioBuilder`(bench)和 `SubmitStartupScene`(默认演示场景)都直接依赖它免费摆放,这是本专项唯一的硬红线。
- 所有 sim 层状态变化必须走 `Command` 队列(`Sim.Submit` → 下一次 `Step()` 才 apply)——不新增任何绕过队列的直接写入 API。
- `data/base/*.json` 里新增的物品/配方/`minableResult` 字段名必须跟 `PrototypeLoader.cs` 现有解析用的字符串完全一致(`minableResult`、`miningTimeSeconds`、`placeResult`、`stackSize`、`ingredients`、`results`、`amount`、`name`)——大小写敏感,`System.Text.Json` 默认区分大小写。
- `game/BuildController.cs`/`game/PlayerInputController.cs` 是 Godot C# 代码,不进 `Faketorio.sln`、不进 CI,只能 `dotnet build -c Release game/Game.csproj` 编译检查 + 人工 F5 验收,不写自动化测试。
- `EntityPrototype.MinableResult` 是 `string?`(可空),`GetString(el, "minableResult")`(已存在的 helper,`PrototypeLoader.cs:407`)本身就是 nullable-safe,字段缺失时返回 `null`——不需要额外判空逻辑。

---

## Task 1: PrototypeLoader 补齐 minableResult/miningTimeSeconds 解析

**Files:**
- Modify: `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs:250-340`(`transport-belt`/`electric-pole`/`fuel-generator`/`furnace`/`assembling-machine`/`mining-drill`/`inserter` 七个分支)
- Test: `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs`

**Interfaces:**
- Consumes: `GetString(JsonElement, string)`(`PrototypeLoader.cs:407`,已存在)、`GetDouble(JsonElement, string, double)`(已存在,同文件,跟 `GetInt` 同一风格)、`Units.SecondsToTicks(double)`(已存在)、`EntityPrototype.MinableResult`/`MiningTimeTicks`(已存在字段,`EntityPrototype.cs:7-8`)
- Produces: 7 种实体类型的 `PrototypeLoader` 解析分支现在会读 `minableResult`/`miningTimeSeconds` JSON 字段(之前只有 `container`/`resource` 两个分支读)——Task 2 的数据文件改动依赖这个解析能力才会生效。

现状(未修复前):`container` 分支(`PrototypeLoader.cs:240-249`)已经有

```csharp
MinableResult = GetString(el, "minableResult"),
MiningTimeTicks = Units.SecondsToTicks(GetDouble(el, "miningTimeSeconds", 0)),
```

其余 6 个实体分支(`transport-belt`/`electric-pole`/`fuel-generator`/`furnace`/`assembling-machine`/`mining-drill`/`inserter`)完全没有这两行——JSON 里就算写了 `minableResult` 字段也会被静默忽略。

- [ ] **Step 1: 写一条会失败的测试,确认 mining-drill 类型目前解析不出 minableResult**

在 `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` 末尾(`public class PrototypeLoaderTests` 的闭合 `}` 前)加:

```csharp
[Fact]
public void MiningDrill_ParsesMinableResultAndMiningTime()
{
    string dir = Path.Combine(Path.GetTempPath(), $"proto-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "drill.json"), """
        [{ "type": "mining-drill", "name": "test-drill", "tileWidth": 2, "tileHeight": 2,
           "energyUsage": "10kW", "minableResult": "test-drill", "miningTimeSeconds": 2.5 }]
        """);
    File.WriteAllText(Path.Combine(dir, "item.json"), """
        [{ "type": "item", "name": "test-drill", "stackSize": 50 }]
        """);
    File.WriteAllText(Path.Combine(dir, "player.json"), """
        [{ "type": "player", "name": "player", "inventorySize": 10 }]
        """);

    var registry = PrototypeLoader.LoadFromDirectory(dir);
    var drill = registry.Get<Faketorio.Sim.Prototypes.MiningDrillPrototype>("test-drill");

    Assert.Equal("test-drill", drill.MinableResult);
    Assert.Equal(150, drill.MiningTimeTicks);   // 2.5s * 60 ticks/s

    Directory.Delete(dir, recursive: true);
}
```

如果文件顶部没有 `using System;` / `using System.IO;`,加上(检查文件现有 `using` 块,大概率已经有,因为其它测试也用临时目录模式)。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~MiningDrill_ParsesMinableResultAndMiningTime"`
Expected: FAIL —— `drill.MinableResult` 是 `null`,不等于 `"test-drill"`。

- [ ] **Step 3: 给 7 个分支补上这两行**

在 `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` 里,对以下 7 处 `ValidateFootprint(new XxxPrototype { ... })` 的对象初始化器,在 `CollisionInsetSubTiles = GetInt(el, "collisionInsetSubTiles", 0),` 这一行后面插入两行:

```csharp
                MinableResult = GetString(el, "minableResult"),
                MiningTimeTicks = Units.SecondsToTicks(GetDouble(el, "miningTimeSeconds", 0)),
```

7 处位置(改之前用 `grep -n '"transport-belt" =>\|"electric-pole" =>\|"fuel-generator" =>\|"furnace" =>\|"assembling-machine" =>\|"mining-drill" =>\|"inserter" =>' sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` 重新核对行号,文件在这轮改动过程中会变):

1. `"transport-belt" => ValidateFootprint(new TransportBeltPrototype { ... })`
2. `"electric-pole" => ValidateFootprint(new ElectricPolePrototype { ... })`
3. `"fuel-generator" => ValidateFootprint(new FuelGeneratorPrototype { ... })`
4. `"furnace" => ValidateFootprint(new FurnacePrototype { ... })`
5. `"assembling-machine" => ValidateFootprint(new AssemblingMachinePrototype { ... })`
6. `"mining-drill" => ValidateFootprint(new MiningDrillPrototype { ... })`
7. `"inserter" => ValidateFootprint(new InserterPrototype { ... })`

每处都插在该对象初始化器的 `CollisionInsetSubTiles = GetInt(el, "collisionInsetSubTiles", 0),` 那一行之后、下一个字段(比如 `MaximumWireDistanceTiles`/`PowerOutputJPerTick`/`Category`/`EnergyUsageJPerTick`)之前。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~MiningDrill_ParsesMinableResultAndMiningTime"`
Expected: PASS

- [ ] **Step 5: 跑全量 sim 测试,确认没有破坏既有行为**

Run: `dotnet test -c Release Faketorio.sln`
Expected: 全部通过(改动前的基线是 517 个 `Faketorio.Sim.Tests` + 40 个 `Faketorio.Sim.McpServer.Tests` + 26 个 `Faketorio.Presentation.Core.Tests`,加上本任务新增的 1 条,应为 518+40+26)。

- [ ] **Step 6: Commit**

```bash
git add sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs
git commit -m "feat(sim): PrototypeLoader 补齐 7 种实体类型的 minableResult/miningTimeSeconds 解析"
```

---

## Task 2: 建筑物品/配方/可拆数据 + 开局物资包

**Files:**
- Modify: `data/base/items.json`(加 7 条物品)
- Modify: `data/base/recipes.json`(加 7 条配方)
- Modify: `data/base/entities.json`(`transport-belt-basic` 补 `minableResult`/`miningTimeSeconds`)
- Modify: `data/base/electric.json`(`small-electric-pole` 补 `minableResult`/`miningTimeSeconds`)
- Modify: `data/base/machines.json`(`stone-furnace`/`assembling-machine-1` 各补 `minableResult`/`miningTimeSeconds`)
- Modify: `data/base/mining-drill.json`(`electric-mining-drill` 补 `minableResult`/`miningTimeSeconds`)
- Modify: `data/base/inserter.json`(`inserter-basic` 补 `minableResult`/`miningTimeSeconds`)
- Modify: `data/base/electric.json`(`burner-generator` 补 `minableResult`/`miningTimeSeconds`——跟 `small-electric-pole` 同一个文件,两条都要改)
- Modify: `data/base/player.json`(`startingInventory` 扩充)
- Test: `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs`

**Interfaces:**
- Consumes: Task 1 已经让全部 7 种实体类型的 loader 分支支持解析 `minableResult`/`miningTimeSeconds`。
- Produces: `sim.Prototypes.Get<ItemPrototype>("electric-mining-drill")` 等 7 个新物品名可查;`sim.Prototypes.Get<RecipePrototype>("electric-mining-drill")` 等 7 个新配方名可查;7 个实体类型的 `MinableResult` 字段非空;`sim.Player.Inventory` 初始持有 `electric-mining-drill` x1、`stone-furnace` x1、`burner-generator` x1、`small-electric-pole` x2、`coal` x20(加上原有 `iron-plate` x8、`wooden-chest` x1)。Task 4 的 `BuildFromInventory` 测试直接依赖这些物品/配方存在。

- [ ] **Step 1: 改 `data/base/items.json`,在数组末尾(闭合 `]` 前)加 7 条**

```json
  { "type": "item", "name": "electric-mining-drill", "stackSize": 50, "placeResult": "electric-mining-drill" },
  { "type": "item", "name": "stone-furnace", "stackSize": 50, "placeResult": "stone-furnace" },
  { "type": "item", "name": "assembling-machine-1", "stackSize": 50, "placeResult": "assembling-machine-1" },
  { "type": "item", "name": "transport-belt-basic", "stackSize": 50, "placeResult": "transport-belt-basic" },
  { "type": "item", "name": "inserter-basic", "stackSize": 50, "placeResult": "inserter-basic" },
  { "type": "item", "name": "small-electric-pole", "stackSize": 50, "placeResult": "small-electric-pole" },
  { "type": "item", "name": "burner-generator", "stackSize": 50, "placeResult": "burner-generator" }
```

（每条前一条末尾补逗号,注意 JSON 语法——数组最后一个元素后不能有逗号。）

- [ ] **Step 2: 改 `data/base/recipes.json`,在数组末尾加 7 条**

```json
  { "type": "recipe", "name": "transport-belt-basic", "category": "crafting", "energyRequiredSeconds": 0.5,
    "ingredients": [ { "name": "iron-plate", "amount": 1 }, { "name": "iron-gear-wheel", "amount": 1 } ],
    "results": [ { "name": "transport-belt-basic", "amount": 1 } ] },
  { "type": "recipe", "name": "inserter-basic", "category": "crafting", "energyRequiredSeconds": 0.5,
    "ingredients": [ { "name": "iron-plate", "amount": 1 }, { "name": "iron-gear-wheel", "amount": 1 } ],
    "results": [ { "name": "inserter-basic", "amount": 1 } ] },
  { "type": "recipe", "name": "small-electric-pole", "category": "crafting", "energyRequiredSeconds": 0.5,
    "ingredients": [ { "name": "iron-plate", "amount": 1 } ],
    "results": [ { "name": "small-electric-pole", "amount": 1 } ] },
  { "type": "recipe", "name": "stone-furnace", "category": "crafting", "energyRequiredSeconds": 1.0,
    "ingredients": [ { "name": "stone", "amount": 5 } ],
    "results": [ { "name": "stone-furnace", "amount": 1 } ] },
  { "type": "recipe", "name": "electric-mining-drill", "category": "crafting", "energyRequiredSeconds": 2.0,
    "ingredients": [ { "name": "iron-plate", "amount": 5 }, { "name": "iron-gear-wheel", "amount": 5 } ],
    "results": [ { "name": "electric-mining-drill", "amount": 1 } ] },
  { "type": "recipe", "name": "assembling-machine-1", "category": "crafting", "energyRequiredSeconds": 2.0,
    "ingredients": [ { "name": "iron-plate", "amount": 5 }, { "name": "iron-gear-wheel", "amount": 3 } ],
    "results": [ { "name": "assembling-machine-1", "amount": 1 } ] },
  { "type": "recipe", "name": "burner-generator", "category": "crafting", "energyRequiredSeconds": 2.0,
    "ingredients": [ { "name": "iron-plate", "amount": 5 }, { "name": "iron-gear-wheel", "amount": 2 } ],
    "results": [ { "name": "burner-generator", "amount": 1 } ] }
```

- [ ] **Step 3: 给 7 个实体条目补 `minableResult`/`miningTimeSeconds`**

`data/base/entities.json` 的 `transport-belt-basic` 条目(倒数第二个字段 `speedSubTilesPerTick` 后加逗号,插入两个新字段):

```json
  { "type": "transport-belt", "name": "transport-belt-basic", "tileWidth": 1, "tileHeight": 1,
    "speedSubTilesPerTick": 8, "collisionInsetSubTiles": 128,
    "minableResult": "transport-belt-basic", "miningTimeSeconds": 0.3 }
```

`data/base/electric.json` 的 `small-electric-pole` 条目:

```json
  { "type": "electric-pole", "name": "small-electric-pole", "tileWidth": 1, "tileHeight": 1,
    "maximumWireDistanceTiles": 7, "supplyAreaDistanceTiles": 2,
    "minableResult": "small-electric-pole", "miningTimeSeconds": 0.3 }
```

`data/base/electric.json` 的 `burner-generator` 条目:

```json
  { "type": "fuel-generator", "name": "burner-generator", "tileWidth": 2, "tileHeight": 2,
    "powerOutput": "90kW", "fuelItemName": "coal", "collisionInsetSubTiles": 64,
    "minableResult": "burner-generator", "miningTimeSeconds": 1.5 }
```

`data/base/machines.json` 的 `stone-furnace` 条目:

```json
  { "type": "furnace", "name": "stone-furnace", "tileWidth": 2, "tileHeight": 2,
    "category": "smelting", "inputSlots": 1, "outputSlots": 1, "energyUsage": "90kW",
    "collisionInsetSubTiles": 64, "minableResult": "stone-furnace", "miningTimeSeconds": 1.5 }
```

`data/base/machines.json` 的 `assembling-machine-1` 条目:

```json
  { "type": "assembling-machine", "name": "assembling-machine-1", "tileWidth": 3, "tileHeight": 3,
    "category": "crafting", "inputSlots": 2, "outputSlots": 1, "energyUsage": "75kW",
    "collisionInsetSubTiles": 96, "minableResult": "assembling-machine-1", "miningTimeSeconds": 2.0 }
```

`data/base/mining-drill.json` 的 `electric-mining-drill` 条目:

```json
  { "type": "mining-drill", "name": "electric-mining-drill", "tileWidth": 2, "tileHeight": 2,
    "energyUsage": "90kW", "collisionInsetSubTiles": 64,
    "minableResult": "electric-mining-drill", "miningTimeSeconds": 2.0 }
```

`data/base/inserter.json` 的 `inserter-basic` 条目:

```json
  { "type": "inserter", "name": "inserter-basic", "tileWidth": 1, "tileHeight": 1,
    "rotationTimeSeconds": 0.5, "energyUsage": "5kW", "collisionInsetSubTiles": 128,
    "minableResult": "inserter-basic", "miningTimeSeconds": 0.3 }
```

改动前用 `Read` 工具打开每个文件确认现有字段的准确排列(上面给的是最终内容,不是逐字段 diff——直接改成这个最终形态,保持文件其余部分不变)。

- [ ] **Step 4: 扩充 `data/base/player.json` 的 `startingInventory`**

```json
[{ "type": "player", "name": "player",
   "inventorySize": 60, "reachSubTiles": 1536, "walkSpeedSubTilesPerTick": 38, "craftQueueCap": 32,
   "startingInventory": [
     { "name": "iron-plate", "amount": 8 },
     { "name": "wooden-chest", "amount": 1 },
     { "name": "electric-mining-drill", "amount": 1 },
     { "name": "stone-furnace", "amount": 1 },
     { "name": "burner-generator", "amount": 1 },
     { "name": "small-electric-pole", "amount": 2 },
     { "name": "coal", "amount": 20 }
   ] }]
```

- [ ] **Step 5: 写测试确认数据加载正确**

在 `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` 加:

```csharp
[Fact]
public void RealData_SevenBuildingItemsAndRecipesExist()
{
    var registry = PrototypeLoader.LoadFromDirectory("data/base");
    string[] names = {
        "electric-mining-drill", "stone-furnace", "assembling-machine-1",
        "transport-belt-basic", "inserter-basic", "small-electric-pole", "burner-generator",
    };
    foreach (var n in names)
    {
        var item = registry.Get<Faketorio.Sim.Prototypes.ItemPrototype>(n);
        Assert.Equal(n, item.PlaceResult);
        var recipe = registry.Get<Faketorio.Sim.Prototypes.RecipePrototype>(n);
        Assert.Equal("crafting", recipe.Category);
        Assert.NotEmpty(recipe.ResolvedResults);
    }
}

[Fact]
public void RealData_SevenBuildingEntitiesHaveMinableResult()
{
    var registry = PrototypeLoader.LoadFromDirectory("data/base");
    Assert.Equal("electric-mining-drill",
        registry.Get<Faketorio.Sim.Prototypes.MiningDrillPrototype>("electric-mining-drill").MinableResult);
    Assert.Equal("stone-furnace",
        registry.Get<Faketorio.Sim.Prototypes.FurnacePrototype>("stone-furnace").MinableResult);
    Assert.Equal("assembling-machine-1",
        registry.Get<Faketorio.Sim.Prototypes.AssemblingMachinePrototype>("assembling-machine-1").MinableResult);
    Assert.Equal("transport-belt-basic",
        registry.Get<Faketorio.Sim.Prototypes.TransportBeltPrototype>("transport-belt-basic").MinableResult);
    Assert.Equal("inserter-basic",
        registry.Get<Faketorio.Sim.Prototypes.InserterPrototype>("inserter-basic").MinableResult);
    Assert.Equal("small-electric-pole",
        registry.Get<Faketorio.Sim.Prototypes.ElectricPolePrototype>("small-electric-pole").MinableResult);
    Assert.Equal("burner-generator",
        registry.Get<Faketorio.Sim.Prototypes.FuelGeneratorPrototype>("burner-generator").MinableResult);
}

[Fact]
public void RealData_StartingInventoryIncludesStarterBuildings()
{
    var sim = new Faketorio.Sim.Simulation(PrototypeLoader.LoadFromDirectory("data/base"));
    var drillItem = sim.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("electric-mining-drill");
    var furnaceItem = sim.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("stone-furnace");
    var genItem = sim.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("burner-generator");
    var poleItem = sim.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("small-electric-pole");
    var coalItem = sim.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("coal");

    Assert.Equal(1, sim.Player.Inventory.CountOf(drillItem.Id));
    Assert.Equal(1, sim.Player.Inventory.CountOf(furnaceItem.Id));
    Assert.Equal(1, sim.Player.Inventory.CountOf(genItem.Id));
    Assert.Equal(2, sim.Player.Inventory.CountOf(poleItem.Id));
    Assert.Equal(20, sim.Player.Inventory.CountOf(coalItem.Id));
}
```

- [ ] **Step 6: 运行新测试确认通过**

Run: `dotnet test sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~RealData"`
Expected: 3 个新测试全部 PASS。

- [ ] **Step 7: 跑全量测试 + bench golden 确认没有破坏既有场景**

Run: `dotnet test -c Release Faketorio.sln`
Expected: 全部通过。

Run: `dotnet build -c Release sim/Faketorio.Sim.Bench/Faketorio.Sim.Bench.csproj && dotnet sim/Faketorio.Sim.Bench/bin/Release/net8.0/Faketorio.Sim.Bench.dll --scale 50 --ticks 5000 --golden bench/golden.json`
Expected: `gate PASS`(bench 场景不消费玩家开局物资包这些新增数据,新物品/配方/`minableResult` 的加入本身不改变任何现有实体的确定性行为,golden hash 应该逐位不变——如果 gate 不是 PASS,说明数据改动意外影响了某处现有逻辑,需要停下来排查,不要跳过)。

- [ ] **Step 8: Commit**

```bash
git add data/base/items.json data/base/recipes.json data/base/entities.json data/base/electric.json data/base/machines.json data/base/mining-drill.json data/base/inserter.json data/base/player.json sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs
git commit -m "feat(data): 7 种建筑的物品原型/配方/minableResult + 开局物资包扩充"
```

---

## Task 3: 从 PlaceEntity 抽出共享的实体创建/注册 helper

**Files:**
- Modify: `sim/Faketorio.Sim/Simulation.cs:366-405`(`PlaceEntity` 分支)
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`

**Interfaces:**
- Consumes: 无新依赖,纯内部重构。
- Produces: 私有方法 `private EntityId CreateAndRegisterEntity(EntityPrototype proto, int protoId, int x, int y, byte rotation)`,Task 4 的 `BuildFromInventory` 分支会调用它。

这一步是纯重构,不改变任何可观察行为——先写一条锁定 `PlaceEntity` 当前行为的回归测试,再重构,重构后这条测试必须继续通过(证明行为没变)。

- [ ] **Step 1: 写一条覆盖 PlaceEntity 全部实体类型副作用的回归测试**

在 `sim/Faketorio.Sim.Tests/SimulationTests.cs` 里找到测试 `PlaceEntity` 相关行为的区域(搜索 `CommandType.PlaceEntity` 或已有的 `PlaceChest`/`PlaceLargeChest` helper 附近),加:

```csharp
[Fact]
public void PlaceEntity_AllEntityTypes_RegisterCorrectly_RegressionForRefactor()
{
    // 重构 CreateAndRegisterEntity 前的行为快照——belt/container/pole/generator/
    // machine/drill/inserter 各摆一个,确认注册到了各自的子系统里。重构后这条
    // 必须继续通过,证明抽 helper 没有改变任何实际行为。
    var sim = NewSim();

    int belt = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id;
    int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
    int pole = sim.Prototypes.Get<ElectricPolePrototype>("small-electric-pole").Id;
    int gen = sim.Prototypes.Get<FuelGeneratorPrototype>("burner-generator").Id;
    int furnace = sim.Prototypes.Get<FurnacePrototype>("stone-furnace").Id;
    int drill = sim.Prototypes.Get<MiningDrillPrototype>("electric-mining-drill").Id;
    int inserter = sim.Prototypes.Get<InserterPrototype>("inserter-basic").Id;

    sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = belt, X = 0, Y = 0, Rotation = 1 });
    sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 1, Y = 0 });
    sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = pole, X = 2, Y = 0 });
    sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = gen, X = 3, Y = 0 });
    sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = furnace, X = 6, Y = 0 });
    sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = drill, X = 9, Y = 0 });
    sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = inserter, X = 12, Y = 0 });
    sim.Step();

    Assert.Equal(0, sim.RejectedCommandCount);
    var beltId = sim.World.GetEntityAt(0, 0);
    Assert.True(sim.Belts.GetLineAt(0, 0).IsValid, "belt should register in BeltNetwork");
    var chestId = sim.World.GetEntityAt(1, 0);
    Assert.True(sim.Inventories.GetInventoryId(chestId).IsValid, "chest should get an inventory");
    Assert.True(sim.ElectricGrid.FindNetworkAt(2, 0).IsValid, "pole should register in ElectricGrid");
    var genId = sim.World.GetEntityAt(3, 0);
    Assert.Contains(genId, sim.ElectricGrid.GeneratorIds);
    var furnaceId = sim.World.GetEntityAt(6, 0);
    Assert.True(sim.Inventories.GetInventoryId(furnaceId, role: 1).IsValid, "furnace should get input inventory");
    Assert.True(sim.Inventories.GetInventoryId(furnaceId, role: 2).IsValid, "furnace should get output inventory");
    var drillId = sim.World.GetEntityAt(9, 0);
    Assert.Contains(drillId, sim.MiningDrills.ActiveIds);
    var inserterId = sim.World.GetEntityAt(12, 0);
    Assert.Contains(inserterId, sim.Inserters.ActiveIds);
}
```

如果 `sim.Inserters.ActiveIds`/`sim.MiningDrills.ActiveIds` 属性名跟实际代码不一致,以 `grep -n "ActiveIds" sim/Faketorio.Sim/Inserters.cs sim/Faketorio.Sim/MiningDrills.cs` 的实际结果为准调整这条测试(这两个类前面 Task 已经确认过都有 `public IReadOnlyList<EntityId> ActiveIds` 风格的属性,命名应该一致)。

- [ ] **Step 2: 运行测试确认通过(重构前,验证测试本身写对了)**

Run: `dotnet test sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~PlaceEntity_AllEntityTypes_RegisterCorrectly_RegressionForRefactor"`
Expected: PASS(这一步测的是重构前的现状,本来就应该过;如果不过,说明测试本身写错了,要先修好测试,不要带着错误的测试往下走)。

- [ ] **Step 3: 抽取 CreateAndRegisterEntity helper**

把 `sim/Faketorio.Sim/Simulation.cs` 里 `PlaceEntity` 分支的这部分(原第 375-403 行左右,`Entities.Create` 到最后一个 `if (proto is InserterPrototype) Inserters.RegisterInserter(id);`):

```csharp
                var id = Entities.Create(new EntityData
                {
                    ProtoId = command.ProtoId,
                    X = command.X,
                    Y = command.Y,
                    Rotation = command.Rotation,
                });
                World.OccupyArea(command.X, command.Y, proto.TileWidth, proto.TileHeight, id);
                if (proto is TransportBeltPrototype)
                    Belts.AddBelt(command.X, command.Y, command.Rotation);
                if (proto is ContainerPrototype cp)
                    Inventories.AddContainer(id, cp.InventorySize);
                if (proto is ElectricPolePrototype pole)
                    ElectricGrid.RegisterPole(id, command.X, command.Y, pole.MaximumWireDistanceTiles, pole.SupplyAreaDistanceTiles);
                if (proto is FuelGeneratorPrototype gen)
                {
                    Inventories.AddContainer(id, 1, filterItemProtoId: gen.FuelItemProtoId);
                    ElectricGrid.RegisterGenerator(id);
                }
                if (proto is CraftingMachinePrototype cmp)
                {
                    Inventories.AddContainer(id, cmp.InputSlots, role: 1);
                    Inventories.AddContainer(id, cmp.OutputSlots, role: 2);
                    Machines.RegisterMachine(id);
                }
                if (proto is MiningDrillPrototype)
                    MiningDrills.RegisterDrill(id);
                if (proto is InserterPrototype)
                    Inserters.RegisterInserter(id);
```

替换成一个方法调用 `var id = CreateAndRegisterEntity(proto, command.ProtoId, command.X, command.Y, command.Rotation);`,并在 `Apply` 方法后面(比如紧跟着 `Apply` 方法闭合的 `}` 之后)新增私有方法:

```csharp
    // PlaceEntity 和 BuildFromInventory 共用的实体创建 + 按原型类型分派注册逻辑。
    // 调用方已经做完全部前置检查(占地/距离/库存),这里只管创建。
    private EntityId CreateAndRegisterEntity(EntityPrototype proto, int protoId, int x, int y, byte rotation)
    {
        var id = Entities.Create(new EntityData
        {
            ProtoId = protoId,
            X = x,
            Y = y,
            Rotation = rotation,
        });
        World.OccupyArea(x, y, proto.TileWidth, proto.TileHeight, id);
        if (proto is TransportBeltPrototype)
            Belts.AddBelt(x, y, rotation);
        if (proto is ContainerPrototype cp)
            Inventories.AddContainer(id, cp.InventorySize);
        if (proto is ElectricPolePrototype pole)
            ElectricGrid.RegisterPole(id, x, y, pole.MaximumWireDistanceTiles, pole.SupplyAreaDistanceTiles);
        if (proto is FuelGeneratorPrototype gen)
        {
            Inventories.AddContainer(id, 1, filterItemProtoId: gen.FuelItemProtoId);
            ElectricGrid.RegisterGenerator(id);
        }
        if (proto is CraftingMachinePrototype cmp)
        {
            Inventories.AddContainer(id, cmp.InputSlots, role: 1);
            Inventories.AddContainer(id, cmp.OutputSlots, role: 2);
            Machines.RegisterMachine(id);
        }
        if (proto is MiningDrillPrototype)
            MiningDrills.RegisterDrill(id);
        if (proto is InserterPrototype)
            Inserters.RegisterInserter(id);
        return id;
    }
```

`PlaceEntity` 分支重构后变成:

```csharp
            case CommandType.PlaceEntity:
            {
                if (!Prototypes.TryGetById(command.ProtoId, out var p) || p is not EntityPrototype proto
                    || command.Rotation > 3
                    || !World.IsAreaFree(command.X, command.Y, proto.TileWidth, proto.TileHeight))
                {
                    RejectedCommandCount++;
                    return;
                }
                CreateAndRegisterEntity(proto, command.ProtoId, command.X, command.Y, command.Rotation);
                return;
            }
```

- [ ] **Step 4: 运行回归测试 + 全量测试确认行为完全不变**

Run: `dotnet test -c Release Faketorio.sln`
Expected: 全部通过,包括 Step 1 写的那条回归测试和全部既有测试——这是纯重构,任何一条既有测试失败都说明抽取过程改变了行为,需要停下来排查。

- [ ] **Step 5: Commit**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "refactor(sim): 从 PlaceEntity 抽出 CreateAndRegisterEntity 共享 helper"
```

---

## Task 4: 新命令 BuildFromInventory

**Files:**
- Modify: `sim/Faketorio.Sim/Commands/Command.cs`(`CommandType` 枚举加一个值)
- Modify: `sim/Faketorio.Sim/Prototypes/PrototypeRegistry.cs`(加一个按名字查实体原型的方法——见 Step 1 前的说明)
- Modify: `sim/Faketorio.Sim/Simulation.cs`(`Apply` 方法加一个 `case` 分支)
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`,`sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` 或新建 `PrototypeRegistryTests.cs`(看现有测试文件组织习惯,`grep -rn "class.*Tests" sim/Faketorio.Sim.Tests/*.cs` 确认一下有没有专门测 `PrototypeRegistry` 的文件,没有就直接把新测试加进 `PrototypeLoaderTests.cs`,因为它已经在用 `PrototypeRegistry`)

**Interfaces:**
- Consumes: Task 3 的 `CreateAndRegisterEntity(EntityPrototype, int, int, int, byte)`;`Prototypes.TryGetById(int, out PrototypeBase)`;`ItemPrototype.PlaceResult`(`string?`);`Player.Inventory.CountOf(int)`/`Remove(int, int)`;`_playerProto.ReachSubTiles`;`ValueNoise.Isqrt(long)`(已在 `TransferToEntity`/`PlayerMine` 里用过的同一套距离公式)。
- Produces: `CommandType.BuildFromInventory = 12`;`PrototypeRegistry.TryGetEntityByName(string, out EntityPrototype)`(新方法,`game/BuildController.cs` 未来若要按名字反查实体也能复用);`game/BuildController.cs`(Task 5)会提交 `BuildFromInventory` 命令。

**关键坑,写代码前必须知道**:`PrototypeRegistry` 的 `_byTypeAndName` 字典按 `(具体 CLR 类型, name)` 做键(`PrototypeRegistry.cs:6,34-42`)——`Get<T>`/`TryGet<T>` 要求 `T` 精确匹配注册时的类型,不支持多态查询。比如 `small-electric-pole` 是用 `ElectricPolePrototype` 类型注册的,`Prototypes.TryGet<EntityPrototype>("small-electric-pole", out _)` 会查 `(typeof(EntityPrototype), "small-electric-pole")` 这个键,但字典里存的键是 `(typeof(ElectricPolePrototype), "small-electric-pole")`——永远查不到,`TryGet` 恒返回 `false`。`EntityPrototype` 是抽象基类,不会有任何原型直接以它为注册类型。所以不能直接用 `TryGet<EntityPrototype>` 按名字反查"随便哪种实体"——需要先给 `PrototypeRegistry` 加一个按名字查、按 `is EntityPrototype` 过滤的专用方法。

- [ ] **Step 1: 加枚举值**

`sim/Faketorio.Sim/Commands/Command.cs` 的 `CommandType` 枚举末尾加:

```csharp
    RotateEntity = 11,
    BuildFromInventory = 12,
```

- [ ] **Step 2: 给 PrototypeRegistry 加按名字查实体原型的方法(不区分具体子类型)**

在 `sim/Faketorio.Sim/Prototypes/PrototypeRegistry.cs` 里加一个字段和一个方法。字段初始化放在 `AssignIds()` 里(那是唯一一个"全部原型都注册完了"的时间点):

```csharp
    private readonly Dictionary<string, EntityPrototype> _entitiesByName = new();
```

`AssignIds()` 方法体末尾(`for` 循环结束、方法闭合 `}` 之前)加:

```csharp
        foreach (var p in all)
            if (p is EntityPrototype ep) _entitiesByName[ep.Name] = ep;
```

在 `TryGetById` 方法后面加新方法:

```csharp
    // 按名字查任意子类型的 EntityPrototype,不要求调用方知道具体是哪个子类——
    // Get<T>/TryGet<T> 按 (具体 CLR 类型, name) 做键,查不到抽象基类;这个方法
    // 专门补上"我只有个名字,不知道是哪种实体"这个场景(BuildFromInventory 用)。
    public bool TryGetEntityByName(string name, out EntityPrototype proto)
        => _entitiesByName.TryGetValue(name, out proto!);
```

- [ ] **Step 3: 写一条测试锁定这个新方法的行为**

在 `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` 加:

```csharp
[Fact]
public void TryGetEntityByName_FindsEntityAcrossConcreteSubtypes()
{
    var registry = PrototypeLoader.LoadFromDirectory("data/base");

    Assert.True(registry.TryGetEntityByName("small-electric-pole", out var pole));
    Assert.IsType<Faketorio.Sim.Prototypes.ElectricPolePrototype>(pole);

    Assert.True(registry.TryGetEntityByName("stone-furnace", out var furnace));
    Assert.IsType<Faketorio.Sim.Prototypes.FurnacePrototype>(furnace);

    Assert.False(registry.TryGetEntityByName("iron-plate", out _));   // 物品不是实体
    Assert.False(registry.TryGetEntityByName("does-not-exist", out _));
}
```

Run: `dotnet test sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~TryGetEntityByName_FindsEntityAcrossConcreteSubtypes"`
Expected: PASS(这一步的实现已经在 Step 2 写完,顺序上是先写实现再补这条针对性测试,跟本 Task 后面"先写测试再实现"的 TDD 顺序不同——因为这个方法是 `BuildFromInventory` 的必要前置基础设施,不是本任务要验证的主逻辑,直接实现+补测试更省事)。

- [ ] **Step 4: 写 7 条失败的测试**

在 `sim/Faketorio.Sim.Tests/SimulationTests.cs` 加(放在 Task 3 那条回归测试附近,同一个"建造相关"区域):

```csharp
    private static Command BuildFromInventory(Simulation sim, string itemName, int x, int y, byte rotation = 0) => new()
    {
        Type = CommandType.BuildFromInventory,
        ProtoId = sim.Prototypes.Get<ItemPrototype>(itemName).Id,
        X = x, Y = y, Rotation = rotation,
    };

    [Fact]
    public void BuildFromInventory_Succeeds_ConsumesItemAndCreatesEntity()
    {
        var sim = NewSim();
        int before = sim.Player.Inventory.CountOf(sim.Prototypes.Get<ItemPrototype>("small-electric-pole").Id);
        Assert.True(before > 0, "starter kit should include small-electric-pole per Task 2");

        sim.Submit(BuildFromInventory(sim, "small-electric-pole", 0, 0));
        sim.Step();

        Assert.Equal(0, sim.RejectedCommandCount);
        Assert.Equal(before - 1, sim.Player.Inventory.CountOf(sim.Prototypes.Get<ItemPrototype>("small-electric-pole").Id));
        Assert.True(sim.World.GetEntityAt(0, 0).IsValid);
        Assert.True(sim.ElectricGrid.FindNetworkAt(0, 0).IsValid);
    }

    [Fact]
    public void BuildFromInventory_OutOfReach_Rejected()
    {
        var sim = NewSim();   // 玩家在 (0,0),ReachSubTiles 1536 = 6 tile
        int before = sim.Player.Inventory.CountOf(sim.Prototypes.Get<ItemPrototype>("small-electric-pole").Id);

        sim.Submit(BuildFromInventory(sim, "small-electric-pole", 20, 0));
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.Equal(before, sim.Player.Inventory.CountOf(sim.Prototypes.Get<ItemPrototype>("small-electric-pole").Id));
        Assert.False(sim.World.GetEntityAt(20, 0).IsValid);
    }

    [Fact]
    public void BuildFromInventory_NoPlaceResult_Rejected()
    {
        var sim = NewSim();
        int ironPlateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;   // 铁板没有 placeResult

        sim.Submit(new Command { Type = CommandType.BuildFromInventory, ProtoId = ironPlateId, X = 0, Y = 0 });
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.False(sim.World.GetEntityAt(0, 0).IsValid);
    }

    [Fact]
    public void BuildFromInventory_InsufficientInventory_Rejected()
    {
        var sim = NewSim();
        int drillId = sim.Prototypes.Get<ItemPrototype>("electric-mining-drill").Id;
        int have = sim.Player.Inventory.CountOf(drillId);   // starter kit 给了 1
        for (int i = 0; i < have; i++)
        {
            sim.Submit(BuildFromInventory(sim, "electric-mining-drill", 0, -2 - i * 3));
            sim.Step();
        }
        Assert.Equal(0, sim.Player.Inventory.CountOf(drillId));

        sim.Submit(BuildFromInventory(sim, "electric-mining-drill", 0, -20));
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.False(sim.World.GetEntityAt(0, -20).IsValid);
    }

    [Fact]
    public void BuildFromInventory_AreaNotFree_Rejected()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 0, 0));   // 先占住 (0,0)
        sim.Step();
        int before = sim.Player.Inventory.CountOf(sim.Prototypes.Get<ItemPrototype>("small-electric-pole").Id);

        sim.Submit(BuildFromInventory(sim, "small-electric-pole", 0, 0));   // 撞上刚放的箱子
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.Equal(before, sim.Player.Inventory.CountOf(sim.Prototypes.Get<ItemPrototype>("small-electric-pole").Id));
    }

    [Fact]
    public void BuildFromInventory_MultipleEntityTypes_RegistersCorrectly()
    {
        // 跟 Task 3 的 PlaceEntity 回归测试对称——确认 BuildFromInventory 复用的
        // CreateAndRegisterEntity 对每种实体类型都注册对了。
        var sim = NewSim();

        sim.Submit(BuildFromInventory(sim, "transport-belt-basic", 0, 0, rotation: 1));
        sim.Submit(BuildFromInventory(sim, "small-electric-pole", 2, 0));
        sim.Submit(BuildFromInventory(sim, "burner-generator", 3, 0));
        sim.Submit(BuildFromInventory(sim, "stone-furnace", 6, 0));
        sim.Submit(BuildFromInventory(sim, "electric-mining-drill", 0, -2));
        sim.Submit(BuildFromInventory(sim, "inserter-basic", 0, 3));
        sim.Step();

        Assert.Equal(0, sim.RejectedCommandCount);
        Assert.True(sim.Belts.GetLineAt(0, 0).IsValid);
        Assert.True(sim.ElectricGrid.FindNetworkAt(2, 0).IsValid);
        Assert.Contains(sim.World.GetEntityAt(3, 0), sim.ElectricGrid.GeneratorIds);
        var furnaceId = sim.World.GetEntityAt(6, 0);
        Assert.True(sim.Inventories.GetInventoryId(furnaceId, role: 1).IsValid);
        Assert.Contains(sim.World.GetEntityAt(0, -2), sim.MiningDrills.ActiveIds);
        Assert.Contains(sim.World.GetEntityAt(0, 3), sim.Inserters.ActiveIds);
    }

    [Fact]
    public void PlaceEntity_StillFree_NoInventoryCheck()
    {
        // 回归测试:确认 BuildFromInventory 落地后 PlaceEntity 完全没受影响。
        var sim = NewSim();
        int beforePlates = sim.Player.Inventory.CountOf(sim.Prototypes.Get<ItemPrototype>("iron-plate").Id);
        int drillProtoId = sim.Prototypes.Get<MiningDrillPrototype>("electric-mining-drill").Id;

        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = drillProtoId, X = 0, Y = -2, Rotation = 2 });
        sim.Step();

        Assert.Equal(0, sim.RejectedCommandCount);
        Assert.True(sim.World.GetEntityAt(0, -2).IsValid);
        Assert.Equal(beforePlates, sim.Player.Inventory.CountOf(sim.Prototypes.Get<ItemPrototype>("iron-plate").Id));
    }
```

- [ ] **Step 5: 运行测试确认全部失败(命令类型还没实现)**

Run: `dotnet test sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~BuildFromInventory"`
Expected: FAIL(`CommandType.BuildFromInventory` 这个 `case` 分支在 `Apply` 里还没有,命令会走到 `switch` 的默认行为——需要先确认 `Simulation.Apply` 的 `switch` 对未识别的 `CommandType` 值是不是直接 no-op 不拒绝也不生效,如果是,这些测试会因为"什么都没发生"而在断言处失败,这是预期的失败模式)。

- [ ] **Step 6: 实现 BuildFromInventory 分支**

在 `sim/Faketorio.Sim/Simulation.cs` 的 `Apply` 方法 `switch` 里,`case CommandType.PlaceEntity: { ... return; }` 之后(或者 `switch` 里任意位置,紧邻其它 case 即可)加:

```csharp
            case CommandType.BuildFromInventory:
            {
                if (!Prototypes.TryGetById(command.ProtoId, out var itemP) || itemP is not ItemPrototype itemProto
                    || itemProto.PlaceResult is null
                    || !Prototypes.TryGetEntityByName(itemProto.PlaceResult, out var entityProto)
                    || command.Rotation > 3)
                {
                    RejectedCommandCount++;
                    return;
                }

                long ddx = Player.X - (command.X * 256 + 128);
                long ddy = Player.Y - (command.Y * 256 + 128);
                if (ValueNoise.Isqrt(ddx * ddx + ddy * ddy) > _playerProto.ReachSubTiles)
                {
                    RejectedCommandCount++;
                    return;
                }

                if (!World.IsAreaFree(command.X, command.Y, entityProto.TileWidth, entityProto.TileHeight))
                {
                    RejectedCommandCount++;
                    return;
                }

                if (Player.Inventory.CountOf(command.ProtoId) <= 0)
                {
                    RejectedCommandCount++;
                    return;
                }

                Player.Inventory.Remove(command.ProtoId, 1);
                CreateAndRegisterEntity(entityProto, entityProto.Id, command.X, command.Y, command.Rotation);
                return;
            }
```

注意 `CreateAndRegisterEntity` 的第二个参数是**实体原型的 id**(`entityProto.Id`),不是 `command.ProtoId`(那是物品原型 id)——两者是不同的 `PrototypeBase` 子类实例,id 空间共享但数值不同,这里如果传错会导致创建出来的 `EntityData.ProtoId` 指向物品原型而不是实体原型,后续任何读取这个实体的代码(比如 `Prototypes.GetById(data.ProtoId) as EntityPrototype`)都会失败。

`Prototypes.TryGetEntityByName(string, out EntityPrototype)` 就是 Step 2 刚加的方法——**不要**用 `Prototypes.TryGet<EntityPrototype>(...)`,那个方法按精确 CLR 类型做键、查不到任何具体子类型注册的实体(前面"关键坑"那段已经解释过原因)。

- [ ] **Step 7: 运行测试确认全部通过**

Run: `dotnet test sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj --filter "FullyQualifiedName~BuildFromInventory|FullyQualifiedName~PlaceEntity_StillFree"`
Expected: 8 个测试(7 条 `BuildFromInventory_*` + 1 条 `PlaceEntity_StillFree_NoInventoryCheck`)全部 PASS。

- [ ] **Step 8: 跑全量测试 + bench golden**

Run: `dotnet test -c Release Faketorio.sln`
Expected: 全部通过。

Run: `dotnet sim/Faketorio.Sim.Bench/bin/Release/net8.0/Faketorio.Sim.Bench.dll --scale 50 --ticks 5000 --golden bench/golden.json`
Expected: `gate PASS`(`ScenarioBuilder` 不使用 `BuildFromInventory`,golden 不受影响)。

- [ ] **Step 9: Commit**

```bash
git add sim/Faketorio.Sim/Commands/Command.cs sim/Faketorio.Sim/Prototypes/PrototypeRegistry.cs sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs
git commit -m "feat(sim): 新命令 BuildFromInventory —— 距离+库存+占地三重检查后扣物品建实体"
```

---

## Task 5: game/BuildController.cs 接入

**Files:**
- Modify: `game/BuildController.cs`

**Interfaces:**
- Consumes: `CommandType.BuildFromInventory`(Task 4)、`CommandType.MineStart`/`MineStop`(已存在,`Command.cs`)、`Input.IsMouseButtonPressed(MouseButton)`(Godot API)。
- Produces: 无(这是终端消费者,game 层最外层的输入处理)。

本任务零自动化测试(Godot C# 代码不进 `Faketorio.sln`),改完只能 `dotnet build` 编译检查 + 人工 F5。

- [ ] **Step 1: 把左键放置从 PlaceEntity 改成 BuildFromInventory**

`game/BuildController.cs` 现状:

```csharp
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
```

改成(`_chestProtoId` 现在存物品原型 id,不是实体原型 id,变量名同步改成 `_chestItemProtoId` 避免误导):

```csharp
    private SimHost _host = null!;
    private CameraController _cam = null!;
    private int _chestItemProtoId;
    private int _rejectedSeen;
    private double _flashRemaining;

    public override void _Ready()
    {
        _host = GetNode<SimHost>("/root/SimHost");
        _cam = GetNode<CameraController>("../CameraController");
        _chestItemProtoId = _host.Sim.Prototypes.Get<ItemPrototype>("wooden-chest").Id;
        _rejectedSeen = _host.Sim.RejectedCommandCount;
    }
```

（`ItemPrototype` 来自 `Faketorio.Sim.Prototypes`,文件顶部已经 `using Faketorio.Sim.Prototypes;`,不需要加新 using。这一轮左键仍然固定放 wooden-chest——真正的"选建筑"UI 是下一个子项目(快捷栏)的范围,这里只是把命令类型换掉,验证 `BuildFromInventory` 在真实游戏里能跑通。）

`_UnhandledInput` 方法里左键分支:

```csharp
        if (mb.ButtonIndex == MouseButton.Left)
            _host.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = _chestProtoId, X = x, Y = y });
```

改成:

```csharp
        if (mb.ButtonIndex == MouseButton.Left)
            _host.Submit(new Command { Type = CommandType.BuildFromInventory, ProtoId = _chestItemProtoId, X = x, Y = y });
```

- [ ] **Step 2: 右键从瞬间 RemoveEntity 改成长按触发手挖**

删掉 `_UnhandledInput` 里右键分支(原来的 `else _host.Submit(new Command { Type = CommandType.RemoveEntity, X = x, Y = y });`,以及这个方法里判断 `mb.ButtonIndex != MouseButton.Left && mb.ButtonIndex != MouseButton.Right` 那行里的 `MouseButton.Right` 也要去掉,因为右键不再走这个"点按一次触发一次"的路径了)。

`_UnhandledInput` 改成只处理左键:

```csharp
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventMouseButton mb || !mb.Pressed) return;
        if (mb.ButtonIndex != MouseButton.Left) return;

        var (x, y) = _cam.WorldXform.ScreenToTile(mb.Position.ToCore());
        _host.Submit(new Command { Type = CommandType.BuildFromInventory, ProtoId = _chestItemProtoId, X = x, Y = y });

        GetViewport().SetInputAsHandled();
    }
```

在 `_Process` 方法里加长按右键的轮询逻辑(参照 `game/PlayerInputController.cs` 的 `UpdateMining()` 同一套"按住→MineStart,光标格变了重发,松开→MineStop"模式):

```csharp
    private bool _demolishHeld;
    private (int X, int Y) _demolishTile;

    public override void _Process(double delta)
    {
        HoverTile = _cam.WorldXform.ScreenToTile(GetViewport().GetMousePosition().ToCore());

        int rejectedNow = _host.Sim.RejectedCommandCount;
        if (rejectedNow > _rejectedSeen) _flashRemaining = 0.15;
        _rejectedSeen = rejectedNow;

        if (_flashRemaining > 0) _flashRemaining -= delta;
        LastCommandRejected = _flashRemaining > 0;

        UpdateDemolish();
    }

    // 长按右键 = 拆除——复用手挖(MineStart/MineStop)同一套 sim 逻辑(距离检查 +
    // 进度累积 + minableResult 物品归还),不新写机制,同 PlayerInputController.UpdateMining()。
    private void UpdateDemolish()
    {
        bool held = Input.IsMouseButtonPressed(MouseButton.Right);
        if (!held)
        {
            if (_demolishHeld) { _host.Submit(new Command { Type = CommandType.MineStop }); _demolishHeld = false; }
            return;
        }

        if (!_demolishHeld || HoverTile != _demolishTile)
        {
            _host.Submit(new Command { Type = CommandType.MineStart, X = HoverTile.X, Y = HoverTile.Y });
            _demolishHeld = true;
            _demolishTile = HoverTile;
        }
    }
```

（`HoverTile` 是这个类已有的公有属性,`_Process` 顶部已经每帧更新,`UpdateDemolish` 直接读它,不用重复算一次 `ScreenToTile`。）

- [ ] **Step 3: 编译确认无误**

Run: `dotnet build -c Release game/Game.csproj`
Expected: `0 个警告 0 个错误`。

- [ ] **Step 4: 编译整个 solution 确认没有破坏其它项目**

Run: `dotnet build -c Release Faketorio.sln`
Expected: `0 个警告 0 个错误`。

- [ ] **Step 5: 人工 F5 验收**

启动 Godot(`"C:\Program Files\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe" --path game`),F5 进场景,依次验证:

1. 左键在空地放置 wooden-chest——库存有铁板(开局带 8 个,配方消耗 2 个/个)成功放置,库存计数应该看不到直接变化(这轮 wooden-chest 走的是 `BuildFromInventory` 消耗物品本身,不是配方;库存里的 `wooden-chest` 物品数量应该从 1 减到 0,第二次左键点击应该被拒绝——`LastCommandRejected` 触发红色闪烁,因为开局只给了 1 个 wooden-chest 物品)。
2. 库存耗尽后再次左键放置,确认触发拒绝闪烁(红色 0.15 秒)。
3. 长按右键对准任意已放置实体(比如刚放的 wooden-chest 或开局摆好的采矿机/传送带),确认过一小段时间(对应 `miningTimeSeconds`)后实体消失,且能验证背包物品数量变化(如果游戏里没有背包 UI 看不到数字,至少确认实体确实被拆除、没有立即消失——长按期间实体应该还在,这是跟旧的"瞬间 RemoveEntity"行为的关键区别)。
4. 右键松开(或移到别的格子)能正确中断,不会在错误的格子上继续挖。
5. 确认原有的碰撞盒 debug 覆盖层(`` ` `` 键)、传送带带人移动等此前已验收过的功能没有被这次改动影响。

如果验收发现问题,记录现象,不要自己臆测原因就动手改——回到这个任务的实现代码逐行核对。

- [ ] **Step 6: Commit**

```bash
git add game/BuildController.cs
git commit -m "feat(game): BuildController 接入 BuildFromInventory(左键)+ 长按右键复用手挖拆除"
```

---

## 完成后

全部 5 个任务合并后,回到 `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`,在"下一步"之前追加一条记录这次改动的执行期确认(参照文档里其它条目的写法:做了什么、发现了什么、最终效果),更新"下一步"那一行,把"建造成本机制"标记为已完成,并说明下一个子项目(快捷栏 UI)现在可以在 `BuildFromInventory` 之上接选择界面了。
