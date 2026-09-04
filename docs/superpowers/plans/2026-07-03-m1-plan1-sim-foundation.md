# M1 Plan 1: 模拟核心地基 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **状态: ✅ 已合并 main · 已验证** — 主线提交 `95db82c`..`180aa3c`(见 `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md` 进度快照)。下方 `- [ ]` 复选框为执行期工件,不代表当前状态。

**Goal:** 建立可无头运行的确定性模拟核心:prototype 数据管线、代数 ID 实体池、chunk 世界网格、命令队列 + 固定 tick 循环、状态哈希,全部有 xUnit 测试覆盖。

**Architecture:** 纯 .NET 类库 `Faketorio.Sim`(零 Godot 依赖)+ xUnit 测试工程。数据层为 POCO + JSON;世界变更只经命令队列在 tick 边界应用;所有状态经 `IStateWriter` 规范序列化(哈希与未来存档共用)。本计划是 M1 四个计划中的第 1 个,后续计划(传送带、电网+机器、表现层)在此地基上叠加。

**Tech Stack:** .NET 8(C# 12)、xUnit、System.Text.Json。spec 见 `docs/superpowers/specs/2026-07-03-faketorio-design.md`。

## Global Constraints

- 模拟层与数据层**不引用任何 Godot 类型**(spec 铁律 1)
- 命令在**下一 tick 开始时按提交顺序统一应用**(spec 铁律 2)
- 模拟层禁止 `float`/`double` 参与影响状态的运行时计算;数据**加载期**可用 double 换算(一次性、确定)(spec 5.6)
- 数值表示:能量 int64 焦耳;功率 int64 焦耳/tick;1 tile = 256 亚格单位;60 UPS(spec 5.6)
- 热路径禁 LINQ/闭包/装箱;稳态 tick 零托管分配(spec 5.5)
- 所有模拟状态可经 `IStateWriter` 序列化(spec 铁律 4)
- 哈希/序列化的迭代顺序必须确定(排序 chunk 键、按池索引序)

---

### Task 1: 解决方案脚手架

**Files:**
- Create: `Faketorio.sln`
- Create: `sim/Faketorio.Sim/Faketorio.Sim.csproj`
- Create: `sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj`
- Test: `sim/Faketorio.Sim.Tests/SmokeTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: 命名空间 `Faketorio.Sim`;后续所有任务在这两个工程中添加文件

- [ ] **Step 1: 确认 .NET SDK**

Run: `dotnet --version`
Expected: 8.0.x 或更高。若无,先安装 .NET 8 SDK 再继续。

- [ ] **Step 2: 创建解决方案与工程**

```powershell
dotnet new sln -n Faketorio -o C:\code\faketorio
dotnet new classlib -n Faketorio.Sim -o C:\code\faketorio\sim\Faketorio.Sim -f net8.0
dotnet new xunit -n Faketorio.Sim.Tests -o C:\code\faketorio\sim\Faketorio.Sim.Tests -f net8.0
dotnet sln C:\code\faketorio\Faketorio.sln add C:\code\faketorio\sim\Faketorio.Sim C:\code\faketorio\sim\Faketorio.Sim.Tests
dotnet add C:\code\faketorio\sim\Faketorio.Sim.Tests reference C:\code\faketorio\sim\Faketorio.Sim
```

删除模板生成的 `sim/Faketorio.Sim/Class1.cs` 和 `sim/Faketorio.Sim.Tests/UnitTest1.cs`。

- [ ] **Step 3: 写冒烟测试**

`sim/Faketorio.Sim.Tests/SmokeTests.cs`:
```csharp
namespace Faketorio.Sim.Tests;

public class SmokeTests
{
    [Fact]
    public void TestProjectReferencesSim()
    {
        Assert.Equal("Faketorio.Sim", typeof(Faketorio.Sim.AssemblyMarker).Assembly.GetName().Name);
    }
}
```

`sim/Faketorio.Sim/AssemblyMarker.cs`:
```csharp
namespace Faketorio.Sim;

public static class AssemblyMarker { }
```

- [ ] **Step 4: 运行测试**

Run: `dotnet test C:\code\faketorio\Faketorio.sln`
Expected: 1 passed

- [ ] **Step 5: Commit**

```powershell
git -C C:\code\faketorio add -A
git -C C:\code\faketorio commit -m "chore: .NET solution scaffold (Sim library + xUnit tests)"
```

---

### Task 2: 单位与定点数(Units / Q16)

**Files:**
- Create: `sim/Faketorio.Sim/Units.cs`
- Create: `sim/Faketorio.Sim/Q16.cs`
- Test: `sim/Faketorio.Sim.Tests/UnitsTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `Units.TicksPerSecond = 60`, `Units.SubTilesPerTile = 256`
  - `long Units.ParseEnergy(string)`("4MJ"→4_000_000 焦耳)
  - `long Units.ParsePower(string)`("90kW"→1500 焦耳/tick)
  - `int Units.SecondsToTicks(double)`(0.5→30;仅加载期使用)
  - `Q16.FromRatio(long num, long den)`, `long Q16.Mul(long value)`, `Q16.One`

- [ ] **Step 1: 写失败测试**

`sim/Faketorio.Sim.Tests/UnitsTests.cs`:
```csharp
namespace Faketorio.Sim.Tests;

public class UnitsTests
{
    [Theory]
    [InlineData("4MJ", 4_000_000L)]
    [InlineData("100J", 100L)]
    [InlineData("5kJ", 5_000L)]
    [InlineData("1.5MJ", 1_500_000L)]
    public void ParseEnergy_ConvertsToJoules(string input, long expected)
        => Assert.Equal(expected, Units.ParseEnergy(input));

    [Theory]
    [InlineData("90kW", 1500L)]   // 90000 W / 60 tick
    [InlineData("60W", 1L)]
    public void ParsePower_ConvertsToJoulesPerTick(string input, long expected)
        => Assert.Equal(expected, Units.ParsePower(input));

    [Fact]
    public void SecondsToTicks_HalfSecondIs30() => Assert.Equal(30, Units.SecondsToTicks(0.5));

    [Fact]
    public void Q16_HalfRatio_HalvesValue()
    {
        var half = Q16.FromRatio(1, 2);
        Assert.Equal(500L, half.Mul(1000L));
    }

    [Fact]
    public void Q16_One_IsIdentity() => Assert.Equal(1234L, Q16.One.Mul(1234L));
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~UnitsTests"`
Expected: 编译失败(Units/Q16 不存在)

- [ ] **Step 3: 实现**

`sim/Faketorio.Sim/Units.cs`:
```csharp
using System.Globalization;

namespace Faketorio.Sim;

public static class Units
{
    public const int TicksPerSecond = 60;
    public const int SubTilesPerTile = 256;

    // "4MJ" -> 4_000_000. 仅数据加载期调用(double 换算一次性、确定)。
    public static long ParseEnergy(string s)
    {
        var span = s.AsSpan().Trim();
        if (span[^1] is 'J' or 'W') span = span[..^1];
        long multiplier = span[^1] switch
        {
            'k' => 1_000L,
            'M' => 1_000_000L,
            'G' => 1_000_000_000L,
            _ => 1L,
        };
        if (multiplier != 1L) span = span[..^1];
        double value = double.Parse(span, CultureInfo.InvariantCulture);
        return (long)Math.Round(value * multiplier);
    }

    // "90kW" -> 焦耳/tick(不整除时向下取整)
    public static long ParsePower(string s) => ParseEnergy(s) / TicksPerSecond;

    public static int SecondsToTicks(double seconds)
        => (int)Math.Round(seconds * TicksPerSecond);
}
```

`sim/Faketorio.Sim/Q16.cs`:
```csharp
namespace Faketorio.Sim;

// Q16.16 定点数,用于 satisfaction 等比例系数(spec 5.6)
public readonly struct Q16
{
    public readonly int Raw;
    private Q16(int raw) => Raw = raw;

    public static readonly Q16 One = new(1 << 16);
    public static readonly Q16 Zero = new(0);

    public static Q16 FromRatio(long numerator, long denominator)
        => new((int)(numerator * (1 << 16) / denominator));

    public long Mul(long value) => value * Raw >> 16;
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~UnitsTests"`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```powershell
git -C C:\code\faketorio add -A
git -C C:\code\faketorio commit -m "feat(sim): units and Q16 fixed-point (joules, J/tick, subtiles)"
```

---

### Task 3: Prototype POCO + JSON 加载器 + 注册表

**Files:**
- Create: `sim/Faketorio.Sim/Prototypes/PrototypeBase.cs`
- Create: `sim/Faketorio.Sim/Prototypes/ItemPrototype.cs`
- Create: `sim/Faketorio.Sim/Prototypes/RecipePrototype.cs`
- Create: `sim/Faketorio.Sim/Prototypes/EntityPrototype.cs`(含 ContainerPrototype)
- Create: `sim/Faketorio.Sim/Prototypes/PrototypeRegistry.cs`
- Create: `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`
- Create: `data/base/items.json`, `data/base/entities.json`, `data/base/recipes.json`
- Modify: `sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj`(复制 data 到输出目录)
- Test: `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs`

**Interfaces:**
- Consumes: `Units.ParseEnergy/ParsePower/SecondsToTicks`(Task 2)
- Produces:
  - `PrototypeBase { string Name; int Id }`(Id 由注册表按 Name 序分配,跨运行稳定)
  - `ItemPrototype : PrototypeBase { int StackSize; string? PlaceResult; long FuelValueJ; string? FuelCategory }`
  - `RecipePrototype : PrototypeBase { string Category; int EnergyRequiredTicks; List<ItemAmount> Ingredients; List<ItemAmount> Results; bool Enabled }`,`struct ItemAmount { string Name; int Amount }`
  - `EntityPrototype : PrototypeBase { int TileWidth; int TileHeight; string? MinableResult; int MiningTimeTicks }`(abstract)
  - `ContainerPrototype : EntityPrototype { int InventorySize }`
  - `PrototypeRegistry { T Get<T>(string name); PrototypeBase GetById(int id); int Count }`
  - `PrototypeRegistry PrototypeLoader.LoadFromDirectory(string dir)`

- [ ] **Step 1: 建数据文件**

`data/base/items.json`:
```json
[
  { "type": "item", "name": "iron-ore", "stackSize": 50 },
  { "type": "item", "name": "iron-plate", "stackSize": 100 },
  { "type": "item", "name": "coal", "stackSize": 50, "fuelValue": "4MJ", "fuelCategory": "chemical" },
  { "type": "item", "name": "wooden-chest", "stackSize": 50, "placeResult": "wooden-chest" }
]
```

`data/base/entities.json`:
```json
[
  { "type": "container", "name": "wooden-chest", "tileWidth": 1, "tileHeight": 1,
    "inventorySize": 16, "minableResult": "wooden-chest", "miningTimeSeconds": 0.5 }
]
```

`data/base/recipes.json`:
```json
[
  { "type": "recipe", "name": "iron-plate", "category": "smelting", "energyRequiredSeconds": 3.2,
    "ingredients": [ { "name": "iron-ore", "amount": 1 } ],
    "results": [ { "name": "iron-plate", "amount": 1 } ] }
]
```

`sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj` 的 `<Project>` 内追加:
```xml
<ItemGroup>
  <Content Include="..\..\data\**" CopyToOutputDirectory="PreserveNewest" LinkBase="data" />
</ItemGroup>
```

- [ ] **Step 2: 写失败测试**

`sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs`:
```csharp
using Faketorio.Sim.Prototypes;

namespace Faketorio.Sim.Tests;

public class PrototypeLoaderTests
{
    private static PrototypeRegistry Load() => PrototypeLoader.LoadFromDirectory("data/base");

    [Fact]
    public void LoadsItemWithFuelValueInJoules()
    {
        var coal = Load().Get<ItemPrototype>("coal");
        Assert.Equal(4_000_000L, coal.FuelValueJ);
        Assert.Equal("chemical", coal.FuelCategory);
        Assert.Equal(50, coal.StackSize);
    }

    [Fact]
    public void LoadsContainerWithMiningTimeInTicks()
    {
        var chest = Load().Get<ContainerPrototype>("wooden-chest");
        Assert.Equal(16, chest.InventorySize);
        Assert.Equal(30, chest.MiningTimeTicks); // 0.5s * 60
        Assert.Equal(1, chest.TileWidth);
    }

    [Fact]
    public void LoadsRecipeWithTicksAndAmounts()
    {
        var recipe = Load().Get<RecipePrototype>("iron-plate");
        Assert.Equal(192, recipe.EnergyRequiredTicks); // 3.2s * 60
        Assert.Equal("smelting", recipe.Category);
        Assert.Single(recipe.Ingredients);
        Assert.Equal("iron-ore", recipe.Ingredients[0].Name);
    }

    [Fact]
    public void IdsAreStableAndOrderedByName()
    {
        var reg = Load();
        for (int i = 0; i < reg.Count; i++)
            Assert.Equal(i, reg.GetById(i).Id);
        // item 与 entity 同名共存(wooden-chest),各自独立注册
        Assert.NotNull(reg.Get<ItemPrototype>("wooden-chest"));
        Assert.NotNull(reg.Get<ContainerPrototype>("wooden-chest"));
    }

    [Fact]
    public void UnknownTypeThrows()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bad.json"), "[{ \"type\": \"nonsense\", \"name\": \"x\" }]");
        Assert.Throws<InvalidDataException>(() => PrototypeLoader.LoadFromDirectory(dir));
    }
}
```

- [ ] **Step 3: 运行确认失败**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~PrototypeLoaderTests"`
Expected: 编译失败(类型不存在)

- [ ] **Step 4: 实现 POCO**

`sim/Faketorio.Sim/Prototypes/PrototypeBase.cs`:
```csharp
namespace Faketorio.Sim.Prototypes;

public abstract class PrototypeBase
{
    public required string Name { get; init; }
    public int Id { get; internal set; } = -1;
}
```

`sim/Faketorio.Sim/Prototypes/ItemPrototype.cs`:
```csharp
namespace Faketorio.Sim.Prototypes;

public sealed class ItemPrototype : PrototypeBase
{
    public int StackSize { get; init; } = 50;
    public string? PlaceResult { get; init; }
    public long FuelValueJ { get; init; }
    public string? FuelCategory { get; init; }
}
```

`sim/Faketorio.Sim/Prototypes/RecipePrototype.cs`:
```csharp
namespace Faketorio.Sim.Prototypes;

public readonly struct ItemAmount
{
    public required string Name { get; init; }
    public required int Amount { get; init; }
}

public sealed class RecipePrototype : PrototypeBase
{
    public string Category { get; init; } = "crafting";
    public int EnergyRequiredTicks { get; init; } = 30;
    public required List<ItemAmount> Ingredients { get; init; }
    public required List<ItemAmount> Results { get; init; }
    public bool Enabled { get; init; } = true;
}
```

`sim/Faketorio.Sim/Prototypes/EntityPrototype.cs`:
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

- [ ] **Step 5: 实现注册表与加载器**

`sim/Faketorio.Sim/Prototypes/PrototypeRegistry.cs`:
```csharp
namespace Faketorio.Sim.Prototypes;

public sealed class PrototypeRegistry
{
    // 键: (具体 CLR 类型, name)。item 与 entity 允许同名。
    private readonly Dictionary<(Type, string), PrototypeBase> _byTypeAndName = new();
    private readonly List<PrototypeBase> _byId = new();

    public int Count => _byId.Count;

    internal void Register(PrototypeBase proto)
    {
        if (!_byTypeAndName.TryAdd((proto.GetType(), proto.Name), proto))
            throw new InvalidDataException($"Duplicate prototype: {proto.GetType().Name} '{proto.Name}'");
    }

    // 全部注册完后调用:按(类型名, name)序分配稳定 Id
    internal void AssignIds()
    {
        var all = new List<PrototypeBase>(_byTypeAndName.Values);
        all.Sort(static (a, b) =>
        {
            int c = string.CompareOrdinal(a.GetType().Name, b.GetType().Name);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });
        _byId.Clear();
        for (int i = 0; i < all.Count; i++)
        {
            all[i].Id = i;
            _byId.Add(all[i]);
        }
    }

    public T Get<T>(string name) where T : PrototypeBase
        => (T)_byTypeAndName[(typeof(T), name)];

    public bool TryGet<T>(string name, out T proto) where T : PrototypeBase
    {
        if (_byTypeAndName.TryGetValue((typeof(T), name), out var p) && p is T t)
        {
            proto = t;
            return true;
        }
        proto = null!;
        return false;
    }

    public PrototypeBase GetById(int id) => _byId[id];
}
```

`sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`:
```csharp
using System.Text.Json;

namespace Faketorio.Sim.Prototypes;

public static class PrototypeLoader
{
    public static PrototypeRegistry LoadFromDirectory(string dir)
    {
        var registry = new PrototypeRegistry();
        var files = Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal); // 确定的加载顺序
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var el in doc.RootElement.EnumerateArray())
                registry.Register(Parse(el));
        }
        registry.AssignIds();
        return registry;
    }

    private static PrototypeBase Parse(JsonElement el)
    {
        string type = el.GetProperty("type").GetString()!;
        string name = el.GetProperty("name").GetString()!;
        return type switch
        {
            "item" => new ItemPrototype
            {
                Name = name,
                StackSize = GetInt(el, "stackSize", 50),
                PlaceResult = GetString(el, "placeResult"),
                FuelValueJ = el.TryGetProperty("fuelValue", out var fv) ? Units.ParseEnergy(fv.GetString()!) : 0L,
                FuelCategory = GetString(el, "fuelCategory"),
            },
            "recipe" => new RecipePrototype
            {
                Name = name,
                Category = GetString(el, "category") ?? "crafting",
                EnergyRequiredTicks = Units.SecondsToTicks(GetDouble(el, "energyRequiredSeconds", 0.5)),
                Ingredients = ParseAmounts(el.GetProperty("ingredients")),
                Results = ParseAmounts(el.GetProperty("results")),
                Enabled = !el.TryGetProperty("enabled", out var en) || en.GetBoolean(),
            },
            "container" => new ContainerPrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                MinableResult = GetString(el, "minableResult"),
                MiningTimeTicks = Units.SecondsToTicks(GetDouble(el, "miningTimeSeconds", 0)),
                InventorySize = GetInt(el, "inventorySize", 0),
            },
            _ => throw new InvalidDataException($"Unknown prototype type '{type}' (name '{name}')"),
        };
    }

    private static List<ItemAmount> ParseAmounts(JsonElement arr)
    {
        var list = new List<ItemAmount>();
        foreach (var el in arr.EnumerateArray())
            list.Add(new ItemAmount
            {
                Name = el.GetProperty("name").GetString()!,
                Amount = el.GetProperty("amount").GetInt32(),
            });
        return list;
    }

    private static int GetInt(JsonElement el, string prop, int fallback)
        => el.TryGetProperty(prop, out var v) ? v.GetInt32() : fallback;

    private static double GetDouble(JsonElement el, string prop, double fallback)
        => el.TryGetProperty(prop, out var v) ? v.GetDouble() : fallback;

    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) ? v.GetString() : null;
}
```

- [ ] **Step 6: 运行确认通过**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~PrototypeLoaderTests"`
Expected: 5 passed

- [ ] **Step 7: Commit**

```powershell
git -C C:\code\faketorio add -A
git -C C:\code\faketorio commit -m "feat(sim): prototype POCOs, JSON loader, registry with stable ids"
```

---

### Task 4: 代数 ID 实体池(EntityPool)

**Files:**
- Create: `sim/Faketorio.Sim/Entities/EntityId.cs`
- Create: `sim/Faketorio.Sim/Entities/EntityPool.cs`
- Test: `sim/Faketorio.Sim.Tests/EntityPoolTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `readonly record struct EntityId(int Index, int Generation)`,`EntityId.Invalid`
  - `EntityPool<T> where T : struct`:`EntityId Create(in T)`、`void Destroy(EntityId)`、`bool IsAlive(EntityId)`、`ref T Get(EntityId)`、`int Capacity`、`bool IsAliveAtIndex(int)`、`ref T GetAtIndex(int)`、`int GenerationAtIndex(int)`(后三者供哈希/系统按索引序确定遍历)

- [ ] **Step 1: 写失败测试**

`sim/Faketorio.Sim.Tests/EntityPoolTests.cs`:
```csharp
using Faketorio.Sim.Entities;

namespace Faketorio.Sim.Tests;

public class EntityPoolTests
{
    private struct Dummy { public int Value; }

    [Fact]
    public void CreateThenGet_ReturnsValue()
    {
        var pool = new EntityPool<Dummy>();
        var id = pool.Create(new Dummy { Value = 42 });
        Assert.True(pool.IsAlive(id));
        Assert.Equal(42, pool.Get(id).Value);
    }

    [Fact]
    public void Destroy_MakesIdStale()
    {
        var pool = new EntityPool<Dummy>();
        var id = pool.Create(new Dummy { Value = 1 });
        pool.Destroy(id);
        Assert.False(pool.IsAlive(id));
    }

    [Fact]
    public void ReusedSlot_BumpsGeneration_StaleIdStaysDead()
    {
        var pool = new EntityPool<Dummy>();
        var a = pool.Create(new Dummy { Value = 1 });
        pool.Destroy(a);
        var b = pool.Create(new Dummy { Value = 2 });
        Assert.Equal(a.Index, b.Index);          // 槽位复用
        Assert.NotEqual(a.Generation, b.Generation);
        Assert.False(pool.IsAlive(a));           // 旧 ID 永久失效
        Assert.Equal(2, pool.Get(b).Value);
    }

    [Fact]
    public void Get_MutatesInPlace()
    {
        var pool = new EntityPool<Dummy>();
        var id = pool.Create(new Dummy { Value = 1 });
        pool.Get(id).Value = 99;
        Assert.Equal(99, pool.Get(id).Value);
    }

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        var pool = new EntityPool<Dummy>(initialCapacity: 2);
        for (int i = 0; i < 100; i++)
            pool.Create(new Dummy { Value = i });
        Assert.True(pool.Capacity >= 100);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~EntityPoolTests"`
Expected: 编译失败

- [ ] **Step 3: 实现**

`sim/Faketorio.Sim/Entities/EntityId.cs`:
```csharp
namespace Faketorio.Sim.Entities;

public readonly record struct EntityId(int Index, int Generation)
{
    public static readonly EntityId Invalid = new(-1, 0);
    public bool IsValid => Index >= 0;
}
```

`sim/Faketorio.Sim/Entities/EntityPool.cs`:
```csharp
namespace Faketorio.Sim.Entities;

// SoA 风格实体池:数据连续存储,代数 ID 防悬垂引用(spec 5.1)。
// 稳态零分配:数组按需翻倍,不在 tick 内分配。
public sealed class EntityPool<T> where T : struct
{
    private T[] _data;
    private int[] _generations;   // 偶数=空槽,奇数=存活(create+destroy 各 +1)
    private int[] _freeStack;
    private int _freeCount;
    private int _count;

    public EntityPool(int initialCapacity = 256)
    {
        _data = new T[initialCapacity];
        _generations = new int[initialCapacity];
        _freeStack = new int[initialCapacity];
    }

    public int Capacity => _count;

    public EntityId Create(in T value)
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
        _generations[index]++;   // 偶 -> 奇: 存活
        _data[index] = value;
        return new EntityId(index, _generations[index]);
    }

    public void Destroy(EntityId id)
    {
        if (!IsAlive(id)) throw new InvalidOperationException($"Destroy on dead id {id}");
        _generations[id.Index]++; // 奇 -> 偶: 空槽
        _data[id.Index] = default;
        if (_freeCount == _freeStack.Length) Array.Resize(ref _freeStack, _freeStack.Length * 2);
        _freeStack[_freeCount++] = id.Index;
    }

    public bool IsAlive(EntityId id)
        => id.Index >= 0 && id.Index < _count && _generations[id.Index] == id.Generation
           && (id.Generation & 1) == 1;

    public ref T Get(EntityId id)
    {
        if (!IsAlive(id)) throw new InvalidOperationException($"Get on dead id {id}");
        return ref _data[id.Index];
    }

    // 按索引序确定遍历(哈希/系统更新用)
    public bool IsAliveAtIndex(int index) => (_generations[index] & 1) == 1;
    public ref T GetAtIndex(int index) => ref _data[index];
    public int GenerationAtIndex(int index) => _generations[index];

    private void Grow()
    {
        int newSize = _data.Length * 2;
        Array.Resize(ref _data, newSize);
        Array.Resize(ref _generations, newSize);
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~EntityPoolTests"`
Expected: 5 passed

- [ ] **Step 5: Commit**

```powershell
git -C C:\code\faketorio add -A
git -C C:\code\faketorio commit -m "feat(sim): generational entity pool with index iteration"
```

---

### Task 5: 世界网格(chunk + footprint 占位)

**Files:**
- Create: `sim/Faketorio.Sim/World/Chunk.cs`
- Create: `sim/Faketorio.Sim/World/WorldGrid.cs`
- Test: `sim/Faketorio.Sim.Tests/WorldGridTests.cs`

**Interfaces:**
- Consumes: `EntityId`(Task 4)
- Produces:
  - `WorldGrid.ChunkSize = 32`
  - `EntityId WorldGrid.GetEntityAt(int x, int y)`(空位返回 `EntityId.Invalid`;支持负坐标)
  - `bool WorldGrid.IsAreaFree(int x, int y, int w, int h)`
  - `void WorldGrid.OccupyArea(int x, int y, int w, int h, EntityId id)`
  - `void WorldGrid.ClearArea(int x, int y, int w, int h)`
  - `IReadOnlyList<long> WorldGrid.SortedChunkKeys()` + `Chunk WorldGrid.GetChunkByKey(long)`(哈希用确定遍历)
  - `Chunk { EntityId[] Tiles }`(长度 32×32,行主序)

- [ ] **Step 1: 写失败测试**

`sim/Faketorio.Sim.Tests/WorldGridTests.cs`:
```csharp
using Faketorio.Sim.Entities;
using Faketorio.Sim.World;

namespace Faketorio.Sim.Tests;

public class WorldGridTests
{
    private static readonly EntityId SomeId = new(7, 1);

    [Fact]
    public void EmptyTile_ReturnsInvalid()
        => Assert.Equal(EntityId.Invalid, new WorldGrid().GetEntityAt(5, 5));

    [Fact]
    public void OccupyArea_AllTilesReturnId()
    {
        var grid = new WorldGrid();
        grid.OccupyArea(10, 20, 2, 3, SomeId);
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                Assert.Equal(SomeId, grid.GetEntityAt(10 + dx, 20 + dy));
        Assert.Equal(EntityId.Invalid, grid.GetEntityAt(12, 20)); // footprint 外
    }

    [Fact]
    public void IsAreaFree_DetectsPartialOverlap()
    {
        var grid = new WorldGrid();
        grid.OccupyArea(0, 0, 2, 2, SomeId);
        Assert.False(grid.IsAreaFree(1, 1, 2, 2)); // 有重叠
        Assert.True(grid.IsAreaFree(2, 0, 2, 2));  // 相邻不重叠
    }

    [Fact]
    public void ClearArea_FreesTiles()
    {
        var grid = new WorldGrid();
        grid.OccupyArea(0, 0, 2, 2, SomeId);
        grid.ClearArea(0, 0, 2, 2);
        Assert.True(grid.IsAreaFree(0, 0, 2, 2));
    }

    [Fact]
    public void NegativeCoordinates_Work()
    {
        var grid = new WorldGrid();
        grid.OccupyArea(-33, -1, 2, 2, SomeId); // 跨 chunk 边界且为负
        Assert.Equal(SomeId, grid.GetEntityAt(-33, -1));
        Assert.Equal(SomeId, grid.GetEntityAt(-32, 0));
    }

    [Fact]
    public void SortedChunkKeys_IsSortedAndComplete()
    {
        var grid = new WorldGrid();
        grid.OccupyArea(100, 100, 1, 1, SomeId);
        grid.OccupyArea(-100, -100, 1, 1, SomeId);
        var keys = grid.SortedChunkKeys();
        Assert.Equal(2, keys.Count);
        Assert.True(keys[0] < keys[1]);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~WorldGridTests"`
Expected: 编译失败

- [ ] **Step 3: 实现**

`sim/Faketorio.Sim/World/Chunk.cs`:
```csharp
using Faketorio.Sim.Entities;

namespace Faketorio.Sim.World;

public sealed class Chunk
{
    public readonly EntityId[] Tiles; // 行主序 32×32

    public Chunk()
    {
        Tiles = new EntityId[WorldGrid.ChunkSize * WorldGrid.ChunkSize];
        Array.Fill(Tiles, EntityId.Invalid);
    }
}
```

`sim/Faketorio.Sim/World/WorldGrid.cs`:
```csharp
using Faketorio.Sim.Entities;

namespace Faketorio.Sim.World;

public sealed class WorldGrid
{
    public const int ChunkSize = 32;

    private readonly Dictionary<long, Chunk> _chunks = new();
    private readonly List<long> _sortedKeys = new();
    private bool _keysDirty;

    private static long ChunkKey(int tileX, int tileY)
    {
        // 向负无穷取整的 chunk 坐标
        int cx = tileX >> 5, cy = tileY >> 5; // 32 = 2^5
        return ((long)cx << 32) | (uint)cy;
    }

    private static int TileIndex(int tileX, int tileY)
    {
        int lx = tileX & (ChunkSize - 1), ly = tileY & (ChunkSize - 1);
        return ly * ChunkSize + lx;
    }

    public EntityId GetEntityAt(int x, int y)
        => _chunks.TryGetValue(ChunkKey(x, y), out var c) ? c.Tiles[TileIndex(x, y)] : EntityId.Invalid;

    public bool IsAreaFree(int x, int y, int w, int h)
    {
        for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++)
                if (GetEntityAt(x + dx, y + dy).IsValid)
                    return false;
        return true;
    }

    public void OccupyArea(int x, int y, int w, int h, EntityId id)
    {
        for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++)
                GetOrCreateChunk(x + dx, y + dy).Tiles[TileIndex(x + dx, y + dy)] = id;
    }

    public void ClearArea(int x, int y, int w, int h)
    {
        for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++)
                if (_chunks.TryGetValue(ChunkKey(x + dx, y + dy), out var c))
                    c.Tiles[TileIndex(x + dx, y + dy)] = EntityId.Invalid;
    }

    public IReadOnlyList<long> SortedChunkKeys()
    {
        if (_keysDirty)
        {
            _sortedKeys.Clear();
            foreach (var k in _chunks.Keys) _sortedKeys.Add(k);
            _sortedKeys.Sort();
            _keysDirty = false;
        }
        return _sortedKeys;
    }

    public Chunk GetChunkByKey(long key) => _chunks[key];

    private Chunk GetOrCreateChunk(int tileX, int tileY)
    {
        long key = ChunkKey(tileX, tileY);
        if (!_chunks.TryGetValue(key, out var chunk))
        {
            chunk = new Chunk();
            _chunks.Add(key, chunk);
            _keysDirty = true;
        }
        return chunk;
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~WorldGridTests"`
Expected: 6 passed

- [ ] **Step 5: Commit**

```powershell
git -C C:\code\faketorio add -A
git -C C:\code\faketorio commit -m "feat(sim): chunked world grid with footprint occupancy"
```

---

### Task 6: 命令队列 + Simulation tick 循环

**Files:**
- Create: `sim/Faketorio.Sim/Commands/Command.cs`
- Create: `sim/Faketorio.Sim/Commands/CommandQueue.cs`
- Create: `sim/Faketorio.Sim/Entities/EntityData.cs`
- Create: `sim/Faketorio.Sim/Simulation.cs`
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`

**Interfaces:**
- Consumes: `PrototypeRegistry`/`EntityPrototype`(Task 3)、`EntityPool`/`EntityId`(Task 4)、`WorldGrid`(Task 5)
- Produces:
  - `enum CommandType : byte { PlaceEntity = 1, RemoveEntity = 2 }`
  - `struct Command { CommandType Type; int ProtoId; int X; int Y; byte Rotation }`
  - `struct EntityData { int ProtoId; int X; int Y; byte Rotation }`
  - `Simulation(PrototypeRegistry)`:`long Tick`、`void Submit(in Command)`、`void Step()`、`WorldGrid World`、`EntityPool<EntityData> Entities`、`int RejectedCommandCount`
  - 语义:Submit 的命令在**下一次 Step 开始时**按序应用(铁律 2);放置校验 footprint 空闲,失败则拒绝(计数,不抛)

- [ ] **Step 1: 写失败测试**

`sim/Faketorio.Sim.Tests/SimulationTests.cs`:
```csharp
using Faketorio.Sim.Commands;
using Faketorio.Sim.Entities;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Sim.Tests;

public class SimulationTests
{
    private static Simulation NewSim()
        => new(PrototypeLoader.LoadFromDirectory("data/base"));

    private static Command PlaceChest(Simulation sim, int x, int y) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id,
        X = x, Y = y, Rotation = 0,
    };

    [Fact]
    public void Command_NotAppliedUntilNextStep()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 3, 4));
        Assert.Equal(EntityId.Invalid, sim.World.GetEntityAt(3, 4)); // 尚未应用
        sim.Step();
        Assert.True(sim.World.GetEntityAt(3, 4).IsValid);
    }

    [Fact]
    public void Step_IncrementsTick()
    {
        var sim = NewSim();
        Assert.Equal(0, sim.Tick);
        sim.Step();
        sim.Step();
        Assert.Equal(2, sim.Tick);
    }

    [Fact]
    public void PlacedEntity_HasCorrectData()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 3, 4));
        sim.Step();
        var id = sim.World.GetEntityAt(3, 4);
        ref var data = ref sim.Entities.Get(id);
        Assert.Equal(3, data.X);
        Assert.Equal(4, data.Y);
        Assert.Equal("wooden-chest", sim.Prototypes.GetById(data.ProtoId).Name);
    }

    [Fact]
    public void PlaceOnOccupiedTile_IsRejected()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 0, 0));
        sim.Submit(PlaceChest(sim, 0, 0)); // 同 tick 第二个,应被拒
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
    }

    [Fact]
    public void RemoveEntity_FreesTilesAndPool()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 0, 0));
        sim.Step();
        var id = sim.World.GetEntityAt(0, 0);
        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 0, Y = 0 });
        sim.Step();
        Assert.Equal(EntityId.Invalid, sim.World.GetEntityAt(0, 0));
        Assert.False(sim.Entities.IsAlive(id));
    }

    [Fact]
    public void RemoveOnEmptyTile_IsRejected()
    {
        var sim = NewSim();
        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 9, Y = 9 });
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~SimulationTests"`
Expected: 编译失败

- [ ] **Step 3: 实现**

`sim/Faketorio.Sim/Commands/Command.cs`:
```csharp
namespace Faketorio.Sim.Commands;

public enum CommandType : byte
{
    PlaceEntity = 1,
    RemoveEntity = 2,
}

public struct Command
{
    public CommandType Type;
    public int ProtoId;
    public int X;
    public int Y;
    public byte Rotation; // 0/1/2/3 = 北/东/南/西
}
```

`sim/Faketorio.Sim/Commands/CommandQueue.cs`:
```csharp
namespace Faketorio.Sim.Commands;

// 双缓冲命令队列:Submit 写入 pending,tick 开始时整体交换后读取。零稳态分配。
public sealed class CommandQueue
{
    private Command[] _pending = new Command[256];
    private Command[] _applying = new Command[256];
    private int _pendingCount;
    private int _applyingCount;

    public void Enqueue(in Command command)
    {
        if (_pendingCount == _pending.Length)
            Array.Resize(ref _pending, _pending.Length * 2);
        _pending[_pendingCount++] = command;
    }

    // 交换缓冲,返回本 tick 待应用的命令(按提交顺序)
    public ReadOnlySpan<Command> BeginTick()
    {
        (_pending, _applying) = (_applying, _pending);
        _applyingCount = _pendingCount;
        _pendingCount = 0;
        return _applying.AsSpan(0, _applyingCount);
    }
}
```

`sim/Faketorio.Sim/Entities/EntityData.cs`:
```csharp
namespace Faketorio.Sim.Entities;

public struct EntityData
{
    public int ProtoId;
    public int X;      // footprint 左上角 tile
    public int Y;
    public byte Rotation;
}
```

`sim/Faketorio.Sim/Simulation.cs`:
```csharp
using Faketorio.Sim.Commands;
using Faketorio.Sim.Entities;
using Faketorio.Sim.Prototypes;
using Faketorio.Sim.World;

namespace Faketorio.Sim;

public sealed class Simulation
{
    public PrototypeRegistry Prototypes { get; }
    public WorldGrid World { get; } = new();
    public EntityPool<EntityData> Entities { get; } = new();
    public long Tick { get; private set; }
    public int RejectedCommandCount { get; private set; }

    private readonly CommandQueue _commands = new();

    public Simulation(PrototypeRegistry prototypes) => Prototypes = prototypes;

    public void Submit(in Command command) => _commands.Enqueue(command);

    public void Step()
    {
        var commands = _commands.BeginTick();
        for (int i = 0; i < commands.Length; i++)
            Apply(in commands[i]);
        // 后续计划在此追加系统更新(传送带、机器、电网……)
        Tick++;
    }

    private void Apply(in Command command)
    {
        switch (command.Type)
        {
            case CommandType.PlaceEntity:
            {
                if (Prototypes.GetById(command.ProtoId) is not EntityPrototype proto
                    || !World.IsAreaFree(command.X, command.Y, proto.TileWidth, proto.TileHeight))
                {
                    RejectedCommandCount++;
                    return;
                }
                var id = Entities.Create(new EntityData
                {
                    ProtoId = command.ProtoId,
                    X = command.X,
                    Y = command.Y,
                    Rotation = command.Rotation,
                });
                World.OccupyArea(command.X, command.Y, proto.TileWidth, proto.TileHeight, id);
                return;
            }
            case CommandType.RemoveEntity:
            {
                var id = World.GetEntityAt(command.X, command.Y);
                if (!id.IsValid || !Entities.IsAlive(id))
                {
                    RejectedCommandCount++;
                    return;
                }
                ref var data = ref Entities.Get(id);
                var proto = (EntityPrototype)Prototypes.GetById(data.ProtoId);
                World.ClearArea(data.X, data.Y, proto.TileWidth, proto.TileHeight);
                Entities.Destroy(id);
                return;
            }
            default:
                RejectedCommandCount++;
                return;
        }
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~SimulationTests"`
Expected: 6 passed

- [ ] **Step 5: 全量回归**

Run: `dotnet test C:\code\faketorio\Faketorio.sln`
Expected: 此前所有测试仍全部通过

- [ ] **Step 6: Commit**

```powershell
git -C C:\code\faketorio add -A
git -C C:\code\faketorio commit -m "feat(sim): command queue with tick-boundary semantics, place/remove"
```

---

### Task 7: 状态序列化接口 + FNV 哈希 + 确定性测试

**Files:**
- Create: `sim/Faketorio.Sim/State/IStateWriter.cs`
- Create: `sim/Faketorio.Sim/State/Fnv1aHashWriter.cs`
- Modify: `sim/Faketorio.Sim/Simulation.cs`(追加 `WriteState` 与 `ComputeStateHash`)
- Test: `sim/Faketorio.Sim.Tests/DeterminismTests.cs`

**Interfaces:**
- Consumes: Task 4-6 全部
- Produces:
  - `IStateWriter { void Write(byte); void Write(int); void Write(long); }`(未来存档序列化器实现同一接口——spec 铁律 4)
  - `Fnv1aHashWriter : IStateWriter { ulong Hash }`
  - `void Simulation.WriteState(IStateWriter)`(遍历顺序确定:tick → 实体池按索引序 → chunk 按键序)
  - `ulong Simulation.ComputeStateHash()`

- [ ] **Step 1: 写失败测试**

`sim/Faketorio.Sim.Tests/DeterminismTests.cs`:
```csharp
using Faketorio.Sim.Commands;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Sim.Tests;

public class DeterminismTests
{
    // 有代表性的场景:多 tick、放置(含被拒)、拆除、跨 chunk 与负坐标
    private static List<ulong> RunScenario()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"));
        int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        var hashes = new List<ulong>();

        for (int t = 0; t < 50; t++)
        {
            if (t % 3 == 0)
                sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = t - 25, Y = -t });
            if (t % 7 == 0)
                sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 0, Y = 0 }); // t>0 时被拒
            if (t == 30) // 拆除 t=6 时放在 (-19,-6) 的箱子,覆盖成功拆除路径
                sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 6 - 25, Y = -6 });
            sim.Step();
            hashes.Add(sim.ComputeStateHash());
        }
        return hashes;
    }

    [Fact]
    public void SameCommands_SameHashEveryTick()
    {
        var first = RunScenario();
        var second = RunScenario();
        Assert.Equal(first, second);
    }

    [Fact]
    public void HashChangesWhenWorldChanges()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"));
        int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        sim.Step();
        var before = sim.ComputeStateHash();
        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 1, Y = 1 });
        sim.Step();
        Assert.NotEqual(before, sim.ComputeStateHash());
    }

    [Fact]
    public void HashCoversEntityRotation()
    {
        ulong Run(byte rotation)
        {
            var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"));
            int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
            sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 0, Y = 0, Rotation = rotation });
            sim.Step();
            return sim.ComputeStateHash();
        }
        Assert.NotEqual(Run(0), Run(1));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~DeterminismTests"`
Expected: 编译失败(IStateWriter/ComputeStateHash 不存在)

- [ ] **Step 3: 实现**

`sim/Faketorio.Sim/State/IStateWriter.cs`:
```csharp
namespace Faketorio.Sim.State;

// 规范序列化的唯一出口:哈希与存档共用(spec 铁律 4)。
// 新增模拟状态必须写进对应的 WriteState,否则确定性测试覆盖不到它。
public interface IStateWriter
{
    void Write(byte value);
    void Write(int value);
    void Write(long value);
}
```

`sim/Faketorio.Sim/State/Fnv1aHashWriter.cs`:
```csharp
namespace Faketorio.Sim.State;

public sealed class Fnv1aHashWriter : IStateWriter
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    private ulong _hash = OffsetBasis;

    public ulong Hash => _hash;

    public void Write(byte value)
    {
        _hash ^= value;
        _hash *= Prime;
    }

    public void Write(int value)
    {
        Write((byte)value);
        Write((byte)(value >> 8));
        Write((byte)(value >> 16));
        Write((byte)(value >> 24));
    }

    public void Write(long value)
    {
        Write((int)value);
        Write((int)(value >> 32));
    }
}
```

`sim/Faketorio.Sim/Simulation.cs` 追加(类内、`Apply` 之前),并在文件头部加 `using Faketorio.Sim.State;`:
```csharp
    public ulong ComputeStateHash()
    {
        var writer = new Fnv1aHashWriter();
        WriteState(writer);
        return writer.Hash;
    }

    public void WriteState(IStateWriter writer)
    {
        writer.Write(Tick);
        writer.Write(RejectedCommandCount);

        // 实体池:按索引序(确定)
        for (int i = 0; i < Entities.Capacity; i++)
        {
            if (!Entities.IsAliveAtIndex(i)) continue;
            writer.Write(i);
            writer.Write(Entities.GenerationAtIndex(i));
            ref var data = ref Entities.GetAtIndex(i);
            writer.Write(data.ProtoId);
            writer.Write(data.X);
            writer.Write(data.Y);
            writer.Write(data.Rotation);
        }

        // 世界网格:chunk 按键序(确定)
        var keys = World.SortedChunkKeys();
        for (int k = 0; k < keys.Count; k++)
        {
            writer.Write(keys[k]);
            var tiles = World.GetChunkByKey(keys[k]).Tiles;
            for (int i = 0; i < tiles.Length; i++)
            {
                writer.Write(tiles[i].Index);
                writer.Write(tiles[i].Generation);
            }
        }
    }
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test C:\code\faketorio\Faketorio.sln --filter "FullyQualifiedName~DeterminismTests"`
Expected: 3 passed

- [ ] **Step 5: 全量回归**

Run: `dotnet test C:\code\faketorio\Faketorio.sln`
Expected: 全部通过(约 26 个)

- [ ] **Step 6: Commit**

```powershell
git -C C:\code\faketorio add -A
git -C C:\code\faketorio commit -m "feat(sim): canonical state writer, FNV-1a hash, determinism tests"
```

---

## 完成标准

- `dotnet test` 全绿,不依赖 Godot
- 同命令序列两次运行逐 tick 哈希一致(确定性地基就位)
- 后续计划(传送带/电网+机器/表现层)在 `Simulation.Step()` 的系统更新点和 `WriteState` 上叠加
