# M1 Plan 7 — 电网(ElectricGrid + 燃料发电机)Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an electrical grid to the simulation — pole connectivity (full recompute), Chebyshev supply-area coverage resolved by position query, a per-network per-tick settlement across the six `UsagePriority` tiers (M1 exercises only `PrimaryOutput`/`PrimaryInput`), and a fuel generator that burns exactly its allocated share. Ships alongside a generic player↔entity item-transfer command reused for fuel loading.

**Architecture:** `ElectricGrid` is a standalone object (parallel to `ResourceGrid`/`Inventories`) that knows nothing about `Simulation`/`Entities`/`Prototypes` — poles register themselves by raw position, and it answers `FindNetworkAt(x,y)` by walking a lazily-rebuilt (dirty-flag-gated) union-find over registered poles. Producers/consumers (in M1: only the fuel generator, driven from `Simulation`'s per-tick scan of the entity pool — mirroring how belts are scanned) register supply/demand against a network id resolved from their own position, then read back an allocation/satisfaction after `Settle()`. The fuel generator's only persistent state (`_fuelBufferJ`, an energy accumulator) lives inside `ElectricGrid` and is the only thing `WriteState` serializes — the pole graph and the per-tick settlement results are pure derived data, recomputed every tick or on topology change, never persisted.

**Tech Stack:** C# / .NET 8, xUnit. No Godot dependency in `Faketorio.Sim`. Deterministic fixed-tick simulation — canonical serialization through `IStateWriter` / `Fnv1aHashWriter` (FNV-1a).

**Spec:** [`docs/superpowers/specs/2026-09-04-m1-plan7-electric-grid-design.md`](../specs/2026-09-04-m1-plan7-electric-grid-design.md)

## Global Constraints

- **Determinism 铁律:** no `float`/`double` anywhere in grid math — distances are integer tile coordinates (no ×256 sub-tile scaling; only the player has sub-tile precision), ratios use `Q16` (`Faketorio.Sim.Q16`, already exists). No `System.Random`. Pole connectivity is recomputed via a sorted (`EntityId.Index` ascending) union-find pass, not `Dictionary` enumeration order; the final `_networks` list is explicitly sorted by each network's smallest pole `EntityId.Index` so `Dictionary` iteration order during grouping never leaks into behavior.
- **Namespace:** `ElectricGrid`, `UsagePriority`, `NetworkId` in `namespace Faketorio.Sim.Electric;` under `sim/Faketorio.Sim/Electric/`. `ElectricPolePrototype`, `FuelGeneratorPrototype` in `namespace Faketorio.Sim.Prototypes;`. Tests in `namespace Faketorio.Sim.Tests;`.
- **Wire distance:** Euclidean, `ValueNoise.Isqrt` (existing, `sim/Faketorio.Sim/World/ValueNoise.cs`), plain `int` tile coordinates — buildings have no sub-tile precision (`EntityData.X/Y` are whole tiles), so no `×256` scaling anywhere in this plan's distance math (unlike the player's `MineTarget` reach check in `PlayerMine`, which does scale).
- **Supply-area coverage:** Chebyshev (`Math.Max(Math.Abs(dx), Math.Abs(dy))`), not Euclidean.
- **`UsagePriority` ordering:** `Solar=0, PrimaryOutput=1, SecondaryOutput=2, PrimaryInput=3, SecondaryInput=4, Tertiary=5`. Supply tiers are consumed in ascending enum order; demand tiers absorb any shortfall in **descending** enum order (`Tertiary` first).
- **`ElectricGrid` layering:** it does not reference `Simulation`, `Entities`, `Inventories`, or `Prototypes`. It only knows raw `EntityId` + `int` positions + `long` amounts + `Q16` ratios. All prototype/inventory lookups happen in `Simulation`.
- **`ElectricGrid.WriteState`:** writes only `_fuelBufferJ`, sorted by `EntityId.Index`. The pole graph (`_poles`/`_networks`) and per-tick registration/settlement state (`_registrations`/`_allocatedSupply`/`_satisfaction`) are never serialized — they are either static configuration (re-derived from live poles every time topology is dirty) or reset every `Settle()` call.
- **Command struct:** `Command` gains `public int Count;` — no existing `CommandType` reads it, so this is backward compatible.
- **`Inventories.AddContainer` overload:** gains `bool readOnly = false, int filterItemProtoId = 0` — existing single-arg callers (`ContainerPrototype`) are unaffected.
- **Reach check pattern:** identical math to `Simulation.PlayerMine`'s existing reach gate — `long ddx = Player.X - (tx * 256 + 128); long ddy = Player.Y - (ty * 256 + 128); ValueNoise.Isqrt(ddx * ddx + ddy * ddy) > _playerProto.ReachSubTiles` rejects.
- **Commit trailer:** every commit message ends with:
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01PTp9xsBoFKWvGqksfjuUUp
  ```
- **Test command:** `dotnet test sim/Faketorio.Sim.Tests` from repo root. Single: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~<Class>.<Method>"`.
- **Baseline:** `dotnet test` is green at **276 passing** before Task 1. `data/**` is copied to the test output dir, so new files under `data/base/` are picked up automatically.
- **Plan deviation from spec §11 (flagged):** the spec's Task 3 lists "`ElectricGrid` 结算 + `WriteState`". `WriteState` only has meaningful content once `_fuelBufferJ` exists (Task 4's generator). This plan moves `WriteState` to Task 4 — Task 3 ships the settlement math with no serialization surface yet, which is correct for its scope, not a placeholder.

## File Structure

| File | Responsibility |
|---|---|
| `sim/Faketorio.Sim/Electric/UsagePriority.cs` (create) | 6-tier `enum UsagePriority : byte`. |
| `sim/Faketorio.Sim/Electric/NetworkId.cs` (create) | `readonly record struct NetworkId(int Index)`, `Invalid`, `IsValid`. |
| `sim/Faketorio.Sim/Electric/ElectricGrid.cs` (create) | Pole connectivity, `FindNetworkAt`, supply/demand registration + `Settle`, generator fuel-buffer bookkeeping, `WriteState`. Contains internal `PoleInfo`/`Network`. |
| `sim/Faketorio.Sim/Prototypes/ElectricPolePrototype.cs` (create) | `MaximumWireDistanceTiles`, `SupplyAreaDistanceTiles`. |
| `sim/Faketorio.Sim/Prototypes/FuelGeneratorPrototype.cs` (create) | `PowerOutputJPerTick`, `FuelItemName` → resolved `FuelItemProtoId`. |
| `data/base/electric.json` (create) | one electric pole + one fuel generator prototype. |
| `sim/Faketorio.Sim/Items/Inventories.cs` (modify) | `AddContainer` gains `readOnly`/`filterItemProtoId` optional params. |
| `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` (modify) | `Parse` gains `"electric-pole"`/`"fuel-generator"` arms; new `ResolveAndValidateElectric` pass. |
| `sim/Faketorio.Sim/Commands/Command.cs` (modify) | `CommandType` gains `TransferToEntity`, `TransferFromEntity`; `Command` gains `Count`. |
| `sim/Faketorio.Sim/Simulation.cs` (modify) | `ElectricGrid` property; `PlaceEntity`/`RemoveEntity` pole/generator hooks; 2 new `Apply` cases; electric-tick segment in `Step`; `WriteState` addition. |
| `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` (modify) | electric prototype load + validation negatives. |
| `sim/Faketorio.Sim.Tests/InventoryTests.cs` or `InventoriesTests.cs` (modify) | `AddContainer` filter/readOnly behavior (`InventoriesTests.cs` — the class-level facade tests; `Inventory`'s own filter/readOnly behavior is already covered from P4). |
| `sim/Faketorio.Sim.Tests/ElectricGridTests.cs` (create) | connectivity, `FindNetworkAt`, settlement. |
| `sim/Faketorio.Sim.Tests/SimulationTests.cs` (modify) | pole/generator placement, transfer command, generator+demand integration. |
| `sim/Faketorio.Sim.Tests/DeterminismTests.cs` (modify) | `RunElectricScenario` two-run equality. |

---

## Task 1: Prototypes + `Inventories.AddContainer` extension + data + loader

**Files:**
- Create: `sim/Faketorio.Sim/Electric/UsagePriority.cs`, `sim/Faketorio.Sim/Prototypes/ElectricPolePrototype.cs`, `sim/Faketorio.Sim/Prototypes/FuelGeneratorPrototype.cs`, `data/base/electric.json`
- Modify: `sim/Faketorio.Sim/Items/Inventories.cs`, `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`
- Test: `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs`, `sim/Faketorio.Sim.Tests/InventoriesTests.cs`

**Interfaces:**
- Consumes: existing `PrototypeBase`, `EntityPrototype` (`TileWidth`/`TileHeight`), `PrototypeRegistry` (`Get<T>`, `TryGet<T>`, `GetById`, `Count`), `ItemPrototype`, `Units.ParsePower(string) → long`, `Inventory(int slotCount, bool readOnly = false, int filterItemProtoId = 0)` (existing ctor, P4), `InventoryPool`/`InventoryId` (existing).
- Produces (later tasks rely on these):
  - `public enum UsagePriority : byte { Solar = 0, PrimaryOutput = 1, SecondaryOutput = 2, PrimaryInput = 3, SecondaryInput = 4, Tertiary = 5 }`
  - `public sealed class ElectricPolePrototype : EntityPrototype { public int MaximumWireDistanceTiles { get; init; } public int SupplyAreaDistanceTiles { get; init; } }`
  - `public sealed class FuelGeneratorPrototype : EntityPrototype { public long PowerOutputJPerTick { get; init; } public required string FuelItemName { get; init; } public int FuelItemProtoId { get; internal set; } }`
  - `Inventories.AddContainer(EntityId entity, int slotCount, bool readOnly = false, int filterItemProtoId = 0) → InventoryId`

- [ ] **Step 1: Create `UsagePriority.cs`**

```csharp
namespace Faketorio.Sim.Electric;

// 完整 6 档;M1 只有 PrimaryOutput(发电机)/PrimaryInput(未来消费者)非空。
// 供给侧按数值升序消耗;需求侧吸收缺口按数值降序(Tertiary 先牺牲)。
public enum UsagePriority : byte
{
    Solar = 0,
    PrimaryOutput = 1,
    SecondaryOutput = 2,
    PrimaryInput = 3,
    SecondaryInput = 4,
    Tertiary = 5,
}
```

- [ ] **Step 2: Create `ElectricPolePrototype.cs` and `FuelGeneratorPrototype.cs`**

```csharp
// sim/Faketorio.Sim/Prototypes/ElectricPolePrototype.cs
namespace Faketorio.Sim.Prototypes;

public sealed class ElectricPolePrototype : EntityPrototype
{
    public int MaximumWireDistanceTiles { get; init; }
    public int SupplyAreaDistanceTiles { get; init; }
}
```

```csharp
// sim/Faketorio.Sim/Prototypes/FuelGeneratorPrototype.cs
namespace Faketorio.Sim.Prototypes;

public sealed class FuelGeneratorPrototype : EntityPrototype
{
    public long PowerOutputJPerTick { get; init; }
    public required string FuelItemName { get; init; }
    public int FuelItemProtoId { get; internal set; }   // 解析 pass 填,同配方名字解析模式
}
```

- [ ] **Step 3: Write the failing `Inventories.AddContainer` filter/readOnly tests**

Append to `sim/Faketorio.Sim.Tests/InventoriesTests.cs` (inside the class; if this file does not yet have a `Reg()`/registry helper, use `new Inventories()` directly and construct `EntityId` literals like the file's other tests do):

```csharp
    [Fact]
    public void AddContainer_WithFilter_RejectsWrongItem_AcceptsRightItem()
    {
        var inv = new Inventories();
        var id = inv.AddContainer(new EntityId(1, 1), 1, filterItemProtoId: 42);
        var container = inv.Get(id);
        Assert.Equal(0, container.Insert(7, 5, 50));    // wrong item -> rejected
        Assert.Equal(5, container.Insert(42, 5, 50));   // right item -> accepted
    }

    [Fact]
    public void AddContainer_ReadOnly_RejectsAllInserts()
    {
        var inv = new Inventories();
        var id = inv.AddContainer(new EntityId(1, 1), 1, readOnly: true);
        Assert.Equal(0, inv.Get(id).Insert(42, 5, 50));
    }

    [Fact]
    public void AddContainer_DefaultArgs_Unfiltered_Writable()
    {
        var inv = new Inventories();
        var id = inv.AddContainer(new EntityId(1, 1), 1);   // 现有单参数调用形态不变
        Assert.Equal(5, inv.Get(id).Insert(42, 5, 50));
    }
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoriesTests.AddContainer_WithFilter"`
Expected: FAIL — `AddContainer` has no `readOnly`/`filterItemProtoId` overload (compile error).

- [ ] **Step 5: Extend `Inventories.AddContainer`**

In `sim/Faketorio.Sim/Items/Inventories.cs`, change:

```csharp
    public InventoryId AddContainer(EntityId entity, int slotCount)
    {
        var id = _pool.Create(new Inventory(slotCount));
```

to:

```csharp
    public InventoryId AddContainer(EntityId entity, int slotCount, bool readOnly = false, int filterItemProtoId = 0)
    {
        var id = _pool.Create(new Inventory(slotCount, readOnly, filterItemProtoId));
```

(the rest of the method body is unchanged).

- [ ] **Step 6: Run the `Inventories` tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoriesTests.AddContainer"`
Expected: all PASS.

- [ ] **Step 7: Create `data/base/electric.json`**

```json
[
  { "type": "electric-pole", "name": "small-electric-pole", "tileWidth": 1, "tileHeight": 1,
    "maximumWireDistanceTiles": 7, "supplyAreaDistanceTiles": 2 },
  { "type": "fuel-generator", "name": "burner-generator", "tileWidth": 2, "tileHeight": 2,
    "powerOutput": "90kW", "fuelItemName": "coal" }
]
```

- [ ] **Step 8: Write the failing loader tests**

Append to `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` (inside the class; `Load()` and `AssertLoadThrows(json)` helpers already exist):

```csharp
    [Fact]
    public void LoadsElectricPole()
    {
        var pole = Load().Get<ElectricPolePrototype>("small-electric-pole");
        Assert.Equal(7, pole.MaximumWireDistanceTiles);
        Assert.Equal(2, pole.SupplyAreaDistanceTiles);
    }

    [Fact]
    public void LoadsFuelGeneratorWithResolvedFuelId()
    {
        var gen = Load().Get<FuelGeneratorPrototype>("burner-generator");
        int coalId = Load().Get<ItemPrototype>("coal").Id;
        Assert.Equal(1500, gen.PowerOutputJPerTick);   // 90kW / 60 ticks-per-second
        Assert.Equal(coalId, gen.FuelItemProtoId);
    }

    [Fact]
    public void PoleZeroWireDistance_Throws() => AssertLoadThrows(
        "[{ \"type\": \"electric-pole\", \"name\": \"p\", \"maximumWireDistanceTiles\": 0, \"supplyAreaDistanceTiles\": 2 }]");

    [Fact]
    public void PoleZeroSupplyArea_Throws() => AssertLoadThrows(
        "[{ \"type\": \"electric-pole\", \"name\": \"p\", \"maximumWireDistanceTiles\": 7, \"supplyAreaDistanceTiles\": 0 }]");

    [Fact]
    public void GeneratorZeroPowerOutput_Throws() => AssertLoadThrows(
        "[{ \"type\": \"item\", \"name\": \"coal\", \"stackSize\": 50 }, " +
        "{ \"type\": \"fuel-generator\", \"name\": \"g\", \"fuelItemName\": \"coal\" }]");

    [Fact]
    public void GeneratorUnknownFuelItem_Throws() => AssertLoadThrows(
        "[{ \"type\": \"fuel-generator\", \"name\": \"g\", \"powerOutput\": \"90kW\", \"fuelItemName\": \"nonexistent\" }]");
```

- [ ] **Step 9: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~PrototypeLoaderTests.LoadsElectric"`
Expected: FAIL — `ElectricPolePrototype`/`FuelGeneratorPrototype` unknown (compile error) or `"electric-pole"` type unrecognized (`InvalidDataException`).

- [ ] **Step 10: Add the `"electric-pole"`/`"fuel-generator"` parse arms**

In `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`, add to the `type switch` in `Parse` (before the `_ =>` throw):

```csharp
            "electric-pole" => ValidateFootprint(new ElectricPolePrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                MaximumWireDistanceTiles = GetInt(el, "maximumWireDistanceTiles", 0),
                SupplyAreaDistanceTiles  = GetInt(el, "supplyAreaDistanceTiles", 0),
            }),
            "fuel-generator" => ValidateFootprint(new FuelGeneratorPrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                PowerOutputJPerTick = el.TryGetProperty("powerOutput", out var po) ? Units.ParsePower(po.GetString()!) : 0L,
                FuelItemName = el.GetProperty("fuelItemName").GetString()!,
            }),
```

- [ ] **Step 11: Add `ResolveAndValidateElectric` and call it**

In `LoadFromDirectory`, add the call after the existing `ResolveAndValidateRecipesAndPlayer(registry);`:

```csharp
        registry.AssignIds();
        ResolveAndValidateMapGen(registry);
        ResolveAndValidateRecipesAndPlayer(registry);
        ResolveAndValidateElectric(registry);
        return registry;
```

Add the method:

```csharp
    // AssignIds() 之后:校验电线杆字段,解析 + 校验发电机的燃料物品名。
    private static void ResolveAndValidateElectric(PrototypeRegistry registry)
    {
        for (int i = 0; i < registry.Count; i++)
        {
            switch (registry.GetById(i))
            {
                case ElectricPolePrototype pole:
                    if (pole.MaximumWireDistanceTiles < 1)
                        throw new InvalidDataException($"Electric pole '{pole.Name}': maximumWireDistanceTiles must be >= 1");
                    if (pole.SupplyAreaDistanceTiles < 1)
                        throw new InvalidDataException($"Electric pole '{pole.Name}': supplyAreaDistanceTiles must be >= 1");
                    break;
                case FuelGeneratorPrototype gen:
                    if (gen.PowerOutputJPerTick < 1)
                        throw new InvalidDataException($"Fuel generator '{gen.Name}': powerOutput must be positive");
                    if (!registry.TryGet<ItemPrototype>(gen.FuelItemName, out var fuelItem))
                        throw new InvalidDataException($"Fuel generator '{gen.Name}': fuelItemName '{gen.FuelItemName}' has no matching item");
                    gen.FuelItemProtoId = fuelItem.Id;
                    break;
            }
        }
    }
```

- [ ] **Step 12: Run the loader tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~PrototypeLoaderTests"`
Expected: all PASS (existing + new).

- [ ] **Step 13: Run the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. 276 + `InventoriesTests` (3) + `PrototypeLoaderTests` (6).

- [ ] **Step 14: Commit**

```bash
git add sim/Faketorio.Sim/Electric/UsagePriority.cs sim/Faketorio.Sim/Prototypes/ElectricPolePrototype.cs sim/Faketorio.Sim/Prototypes/FuelGeneratorPrototype.cs sim/Faketorio.Sim/Items/Inventories.cs sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs data/base/electric.json sim/Faketorio.Sim.Tests/InventoriesTests.cs sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs
git commit -m "$(cat <<'EOF'
feat(proto): ElectricPolePrototype + FuelGeneratorPrototype + Inventories filter

UsagePriority (6-tier enum, M1 only exercises PrimaryOutput/PrimaryInput),
electric pole (wire distance / supply area, both required >= 1), fuel
generator (powerOutput -> Units.ParsePower, fuelItemName resolved to
FuelItemProtoId in a post-AssignIds pass). Inventories.AddContainer gains
optional readOnly/filterItemProtoId (backward compatible) so the
generator's fuel slot can filter to its one fuel item.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01PTp9xsBoFKWvGqksfjuUUp
EOF
)"
```

---

## Task 2: Pole connectivity + `FindNetworkAt`

**Files:**
- Create: `sim/Faketorio.Sim/Electric/NetworkId.cs`, `sim/Faketorio.Sim/Electric/ElectricGrid.cs`
- Test: `sim/Faketorio.Sim.Tests/ElectricGridTests.cs`

**Interfaces:**
- Consumes: `ValueNoise.Isqrt(long) → int` (existing), `EntityId` (existing — `readonly record struct(int Index, int Generation)`), `ElectricPolePrototype` (Task 1 — used only in tests to construct fixtures; `ElectricGrid` itself takes raw ints, not the prototype).
- Produces (Tasks 3–4 rely on these):
  - `public readonly record struct NetworkId(int Index) { public static readonly NetworkId Invalid = new(-1); public bool IsValid => Index >= 0; }`
  - `public sealed class ElectricGrid`:
    - `void RegisterPole(EntityId id, int x, int y, int maximumWireDistanceTiles, int supplyAreaDistanceTiles)`
    - `void UnregisterPole(EntityId id)`
    - `NetworkId FindNetworkAt(int x, int y)`

- [ ] **Step 1: Create `NetworkId.cs`**

```csharp
namespace Faketorio.Sim.Electric;

// 一次 EnsureTopology() 结果里的网络索引;不跨 tick 保留身份。
public readonly record struct NetworkId(int Index)
{
    public static readonly NetworkId Invalid = new(-1);
    public bool IsValid => Index >= 0;
}
```

- [ ] **Step 2: Write the failing connectivity + `FindNetworkAt` tests**

Create `sim/Faketorio.Sim.Tests/ElectricGridTests.cs`:

```csharp
using Faketorio.Sim.Electric;
using Faketorio.Sim.Entities;

namespace Faketorio.Sim.Tests;

public class ElectricGridTests
{
    [Fact]
    public void TwoPolesWithinWireRange_SameNetwork()
    {
        var grid = new ElectricGrid();
        var a = new EntityId(0, 1); var b = new EntityId(1, 1);
        grid.RegisterPole(a, 0, 0, maximumWireDistanceTiles: 7, supplyAreaDistanceTiles: 2);
        grid.RegisterPole(b, 5, 0, maximumWireDistanceTiles: 7, supplyAreaDistanceTiles: 2);

        var na = grid.FindNetworkAt(0, 0);
        var nb = grid.FindNetworkAt(5, 0);
        Assert.True(na.IsValid);
        Assert.Equal(na, nb);
    }

    [Fact]
    public void TwoPolesOutOfWireRange_DifferentNetworks()
    {
        var grid = new ElectricGrid();
        grid.RegisterPole(new EntityId(0, 1), 0, 0, 5, 2);
        grid.RegisterPole(new EntityId(1, 1), 20, 0, 5, 2);

        var na = grid.FindNetworkAt(0, 0);
        var nb = grid.FindNetworkAt(20, 0);
        Assert.True(na.IsValid);
        Assert.True(nb.IsValid);
        Assert.NotEqual(na, nb);
    }

    [Fact]
    public void ChainOfThreePoles_OneNetwork()
    {
        var grid = new ElectricGrid();
        grid.RegisterPole(new EntityId(0, 1), 0, 0, 7, 2);
        grid.RegisterPole(new EntityId(1, 1), 6, 0, 7, 2);    // 0<->6: dist 6 <= 7
        grid.RegisterPole(new EntityId(2, 1), 12, 0, 7, 2);   // 6<->12: dist 6 <= 7; 0<->12: dist 12 > 7 (not direct)

        Assert.Equal(grid.FindNetworkAt(0, 0), grid.FindNetworkAt(12, 0));
    }

    [Fact]
    public void AsymmetricWireDistance_UsesStricterSide()
    {
        var grid = new ElectricGrid();
        grid.RegisterPole(new EntityId(0, 1), 0, 0, maximumWireDistanceTiles: 10, supplyAreaDistanceTiles: 2);
        grid.RegisterPole(new EntityId(1, 1), 7, 0, maximumWireDistanceTiles: 5, supplyAreaDistanceTiles: 2);

        Assert.NotEqual(grid.FindNetworkAt(0, 0), grid.FindNetworkAt(7, 0));
    }

    [Fact]
    public void SupplyArea_ChebyshevSquare_NotEuclideanCircle()
    {
        var grid = new ElectricGrid();
        grid.RegisterPole(new EntityId(0, 1), 0, 0, 7, supplyAreaDistanceTiles: 2);

        Assert.True(grid.FindNetworkAt(2, 2).IsValid);    // Chebyshev max(2,2)=2 <= 2
        Assert.False(grid.FindNetworkAt(3, 0).IsValid);   // Chebyshev 3 > 2
    }

    [Fact]
    public void PositionOutsideAnyPole_ReturnsInvalid()
        => Assert.False(new ElectricGrid().FindNetworkAt(0, 0).IsValid);

    [Fact]
    public void OverlappingSupplyAreas_LowerPoleIndexNetworkWins()
    {
        var grid = new ElectricGrid();
        var polyHighIndex = new EntityId(5, 1);
        var polyLowIndex = new EntityId(2, 1);
        grid.RegisterPole(polyHighIndex, 0, 0, maximumWireDistanceTiles: 1, supplyAreaDistanceTiles: 3);
        grid.RegisterPole(polyLowIndex, 2, 0, maximumWireDistanceTiles: 1, supplyAreaDistanceTiles: 3);
        // distance between poles = 2 > wireDistance 1 -> NOT connected, two separate networks

        var nHighOnly = grid.FindNetworkAt(-3, 0);   // only covered by polyHighIndex
        var nLowOnly  = grid.FindNetworkAt(5, 0);    // only covered by polyLowIndex
        var nOverlap  = grid.FindNetworkAt(1, 0);    // covered by both

        Assert.NotEqual(nHighOnly, nLowOnly);
        Assert.Equal(nLowOnly, nOverlap);            // lower EntityId.Index network wins the tie
    }

    [Fact]
    public void UnregisterPole_RemovesItsCoverage()
    {
        var grid = new ElectricGrid();
        var a = new EntityId(0, 1); var b = new EntityId(1, 1);
        grid.RegisterPole(a, 0, 0, 7, 2);
        grid.RegisterPole(b, 6, 0, 7, 2);
        Assert.Equal(grid.FindNetworkAt(0, 0), grid.FindNetworkAt(6, 0));

        grid.UnregisterPole(a);
        Assert.False(grid.FindNetworkAt(0, 0).IsValid);
        Assert.True(grid.FindNetworkAt(6, 0).IsValid);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ElectricGridTests"`
Expected: FAIL — `ElectricGrid` does not exist (compile error).

- [ ] **Step 4: Create `ElectricGrid.cs` (connectivity part)**

```csharp
using Faketorio.Sim.Entities;
using Faketorio.Sim.World;

namespace Faketorio.Sim.Electric;

// 电网管理器。刻意不知道 Simulation/Entities/Inventories/Prototypes——只收
// 裸 EntityId + int 坐标 + long 能量。连通分量全量重算(拓扑变化才重算,
// 用 _topologyDirty 门控),不跨 tick 保留网络身份。
public sealed class ElectricGrid
{
    private readonly Dictionary<EntityId, PoleInfo> _poles = new();
    private bool _topologyDirty = true;
    private List<Network> _networks = new();

    public void RegisterPole(EntityId id, int x, int y, int maximumWireDistanceTiles, int supplyAreaDistanceTiles)
    {
        _poles[id] = new PoleInfo(x, y, maximumWireDistanceTiles, supplyAreaDistanceTiles);
        _topologyDirty = true;
    }

    public void UnregisterPole(EntityId id)
    {
        _poles.Remove(id);
        _topologyDirty = true;
    }

    // 方形(Chebyshev)供电覆盖区。网络索引序 + 网络内 EntityId.Index 序,
    // 重叠覆盖区第一个命中的赢——确定、可复现。
    public NetworkId FindNetworkAt(int x, int y)
    {
        EnsureTopology();
        for (int ni = 0; ni < _networks.Count; ni++)
            foreach (var poleId in _networks[ni].Poles)
            {
                var p = _poles[poleId];
                if (Math.Max(Math.Abs(x - p.X), Math.Abs(y - p.Y)) <= p.SupplyAreaDistanceTiles)
                    return new NetworkId(ni);
            }
        return NetworkId.Invalid;
    }

    private void EnsureTopology()
    {
        if (!_topologyDirty) return;
        _topologyDirty = false;

        var ids = new List<EntityId>(_poles.Keys);
        ids.Sort((a, b) => a.Index.CompareTo(b.Index));

        var parent = new Dictionary<EntityId, EntityId>();
        foreach (var id in ids) parent[id] = id;

        EntityId Find(EntityId x)
        {
            while (parent[x] != x) x = parent[x];
            return x;
        }
        void Union(EntityId a, EntityId b)
        {
            var ra = Find(a); var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        for (int i = 0; i < ids.Count; i++)
            for (int j = i + 1; j < ids.Count; j++)
            {
                var a = _poles[ids[i]]; var b = _poles[ids[j]];
                long dx = a.X - b.X, dy = a.Y - b.Y;
                int dist = ValueNoise.Isqrt(dx * dx + dy * dy);
                int maxDist = Math.Min(a.MaximumWireDistanceTiles, b.MaximumWireDistanceTiles);
                if (dist <= maxDist) Union(ids[i], ids[j]);
            }

        var groups = new Dictionary<EntityId, List<EntityId>>();
        foreach (var id in ids)   // ids 已按 Index 升序,同组内追加顺序即升序
        {
            var root = Find(id);
            if (!groups.TryGetValue(root, out var list))
                groups[root] = list = new List<EntityId>();
            list.Add(id);
        }

        var networks = new List<Network>(groups.Count);
        foreach (var list in groups.Values)   // Dictionary 遍历序无所谓,下面显式排序
            networks.Add(new Network { Poles = list });
        networks.Sort((x, y) => x.Poles[0].Index.CompareTo(y.Poles[0].Index));
        _networks = networks;
    }
}

internal readonly record struct PoleInfo(int X, int Y, int MaximumWireDistanceTiles, int SupplyAreaDistanceTiles);

internal sealed class Network
{
    public List<EntityId> Poles = new();
}
```

- [ ] **Step 5: Run the `ElectricGridTests` connectivity tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ElectricGridTests"`
Expected: all PASS.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. Baseline + Task 1 + `ElectricGridTests` (8).

- [ ] **Step 7: Commit**

```bash
git add sim/Faketorio.Sim/Electric/NetworkId.cs sim/Faketorio.Sim/Electric/ElectricGrid.cs sim/Faketorio.Sim.Tests/ElectricGridTests.cs
git commit -m "$(cat <<'EOF'
feat(electric): ElectricGrid pole connectivity + FindNetworkAt

Full-recompute union-find over registered poles (Euclidean wire distance,
integer tile coords, dirty-flag gated), Chebyshev square supply-area
coverage resolved by a stateless position query. Network list is
explicitly sorted by each component's smallest pole EntityId.Index so
Dictionary grouping order never leaks into the result. No dependency on
Simulation/Entities/Inventories/Prototypes.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01PTp9xsBoFKWvGqksfjuUUp
EOF
)"
```

---

## Task 3: Settlement (`RegisterSupply`/`RegisterDemand`/`Settle`/queries)

**Files:**
- Modify: `sim/Faketorio.Sim/Electric/ElectricGrid.cs`
- Test: `sim/Faketorio.Sim.Tests/ElectricGridTests.cs`

**Interfaces:**
- Consumes: `FindNetworkAt` (Task 2), `Faketorio.Sim.Q16` (existing — `Q16.One`, `Q16.Zero`, `Q16.FromRatio(long, long)`, `.Mul(long)`), `UsagePriority` (Task 1).
- Produces (Task 4 relies on these):
  - `void RegisterSupply(EntityId id, int x, int y, UsagePriority priority, long maxJThisTick)`
  - `void RegisterDemand(EntityId id, int x, int y, UsagePriority priority, long amountJ)`
  - `void Settle()`
  - `long GetAllocatedSupply(EntityId id)`
  - `Q16 GetSatisfaction(EntityId id)`

- [ ] **Step 1: Write the failing settlement tests**

Append to `sim/Faketorio.Sim.Tests/ElectricGridTests.cs` (inside the class):

```csharp
    private static ElectricGrid GridWithOnePole()
    {
        var grid = new ElectricGrid();
        grid.RegisterPole(new EntityId(0, 1), 0, 0, maximumWireDistanceTiles: 7, supplyAreaDistanceTiles: 5);
        return grid;
    }

    [Fact]
    public void SupplyMeetsExactDemand_FullSatisfaction_NoOverproduction()
    {
        var grid = GridWithOnePole();
        var producer = new EntityId(1, 1); var consumer = new EntityId(2, 1);

        grid.RegisterSupply(producer, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 1000);
        grid.RegisterDemand(consumer, 0, 0, UsagePriority.PrimaryInput, amountJ: 1000);
        grid.Settle();

        Assert.Equal(Q16.One, grid.GetSatisfaction(consumer));
        Assert.Equal(1000, grid.GetAllocatedSupply(producer));
    }

    [Fact]
    public void SupplyExceedsDemand_ProducerThrottlesDown()
    {
        var grid = GridWithOnePole();
        var producer = new EntityId(1, 1); var consumer = new EntityId(2, 1);

        grid.RegisterSupply(producer, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 1000);
        grid.RegisterDemand(consumer, 0, 0, UsagePriority.PrimaryInput, amountJ: 400);
        grid.Settle();

        Assert.Equal(Q16.One, grid.GetSatisfaction(consumer));
        Assert.Equal(400, grid.GetAllocatedSupply(producer));   // 不多烧
    }

    [Fact]
    public void DemandExceedsSupply_ProducerFullOutput_ConsumerPartialSatisfaction()
    {
        var grid = GridWithOnePole();
        var producer = new EntityId(1, 1); var consumer = new EntityId(2, 1);

        grid.RegisterSupply(producer, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 500);
        grid.RegisterDemand(consumer, 0, 0, UsagePriority.PrimaryInput, amountJ: 1000);
        grid.Settle();

        Assert.Equal(500, grid.GetAllocatedSupply(producer));
        Assert.Equal(Q16.FromRatio(500, 1000), grid.GetSatisfaction(consumer));
    }

    [Fact]
    public void TwoProducersSameTier_ShareProportionally()
    {
        var grid = GridWithOnePole();
        var p1 = new EntityId(1, 1); var p2 = new EntityId(2, 1); var consumer = new EntityId(3, 1);

        grid.RegisterSupply(p1, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 1000);
        grid.RegisterSupply(p2, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 1000);
        grid.RegisterDemand(consumer, 0, 0, UsagePriority.PrimaryInput, amountJ: 1000);
        grid.Settle();

        Assert.Equal(500, grid.GetAllocatedSupply(p1));   // 各自一半,不是一个满一个空
        Assert.Equal(500, grid.GetAllocatedSupply(p2));
    }

    [Fact]
    public void ShortfallAbsorbedByLowestPriorityDemandFirst()
    {
        var grid = GridWithOnePole();
        var producer = new EntityId(1, 1);
        var primaryConsumer = new EntityId(2, 1);
        var tertiaryConsumer = new EntityId(3, 1);

        grid.RegisterSupply(producer, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 800);
        grid.RegisterDemand(primaryConsumer, 0, 0, UsagePriority.PrimaryInput, amountJ: 800);
        grid.RegisterDemand(tertiaryConsumer, 0, 0, UsagePriority.Tertiary, amountJ: 400);
        grid.Settle();

        Assert.Equal(Q16.One, grid.GetSatisfaction(primaryConsumer));
        Assert.Equal(Q16.Zero, grid.GetSatisfaction(tertiaryConsumer));
    }

    [Fact]
    public void UncoveredProducerAndConsumer_ZeroAllocation()
    {
        var grid = new ElectricGrid();   // no poles at all
        var producer = new EntityId(0, 1); var consumer = new EntityId(1, 1);
        grid.RegisterSupply(producer, 0, 0, UsagePriority.PrimaryOutput, 1000);
        grid.RegisterDemand(consumer, 0, 0, UsagePriority.PrimaryInput, 1000);
        grid.Settle();

        Assert.Equal(0, grid.GetAllocatedSupply(producer));
        Assert.Equal(Q16.Zero, grid.GetSatisfaction(consumer));
    }

    [Fact]
    public void Settle_ClearsRegistrationsForNextTick()
    {
        var grid = GridWithOnePole();
        var producer = new EntityId(1, 1); var consumer = new EntityId(2, 1);
        grid.RegisterSupply(producer, 0, 0, UsagePriority.PrimaryOutput, 1000);
        grid.RegisterDemand(consumer, 0, 0, UsagePriority.PrimaryInput, 1000);
        grid.Settle();
        Assert.Equal(Q16.One, grid.GetSatisfaction(consumer));

        grid.Settle();   // 没有新登记就结算 -> 上一轮的结果不应该继续生效
        Assert.Equal(Q16.Zero, grid.GetSatisfaction(consumer));
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ElectricGridTests.SupplyMeetsExactDemand"`
Expected: FAIL — `RegisterSupply`/`RegisterDemand`/`Settle` do not exist (compile error).

- [ ] **Step 3: Implement settlement in `ElectricGrid.cs`**

Add `using Faketorio.Sim;` at the top (for `Q16`). Add fields and methods to `ElectricGrid`:

```csharp
    private readonly Dictionary<NetworkId, Registration> _registrations = new();
    private readonly Dictionary<EntityId, long> _allocatedSupply = new();
    private readonly Dictionary<EntityId, Q16> _satisfaction = new();

    // 登记本 tick 的供给/需求。查不到网络(没接上电线杆)->直接记 0/Zero,不进结算。
    // 每个实体每 tick 只登记一次(生产者/消费者各自 tick 逻辑只调一次)。
    public void RegisterSupply(EntityId id, int x, int y, UsagePriority priority, long maxJThisTick)
    {
        var net = FindNetworkAt(x, y);
        if (!net.IsValid) { _allocatedSupply[id] = 0; return; }
        GetOrCreateRegistration(net).Supply.Add((id, priority, maxJThisTick));
    }

    public void RegisterDemand(EntityId id, int x, int y, UsagePriority priority, long amountJ)
    {
        var net = FindNetworkAt(x, y);
        if (!net.IsValid) { _satisfaction[id] = Q16.Zero; return; }
        GetOrCreateRegistration(net).Demand.Add((id, priority, amountJ));
    }

    public long GetAllocatedSupply(EntityId id) => _allocatedSupply.TryGetValue(id, out var v) ? v : 0;
    public Q16 GetSatisfaction(EntityId id) => _satisfaction.TryGetValue(id, out var v) ? v : Q16.Zero;

    // 每 tick 调一次。先清空上一轮的结果——否则本 tick 没重新登记的实体会
    // 读到上一轮的陈旧 allocated/satisfaction 而不是"未登记"的默认值 0/Zero
    // (M1 里发电机每 tick 都会重新登记,不会踩到;但 ElectricGrid 独立测试
    // 一旦某个实体某 tick 不登记,必须正确掉回默认值,不能读到旧数据)。
    // 每个网络独立结算,结果互不影响,故 _registrations 的处理顺序不影响
    // 正确性;仍按 NetworkId.Index 排序处理,避免任何疑虑。
    public void Settle()
    {
        _allocatedSupply.Clear();
        _satisfaction.Clear();
        var nets = new List<NetworkId>(_registrations.Keys);
        nets.Sort((a, b) => a.Index.CompareTo(b.Index));
        foreach (var net in nets)
            SettleNetwork(_registrations[net]);
        _registrations.Clear();
    }

    private static readonly UsagePriority[] SupplyTierOrder =
        { UsagePriority.Solar, UsagePriority.PrimaryOutput, UsagePriority.SecondaryOutput };
    private static readonly UsagePriority[] DemandTierReverseOrder =
        { UsagePriority.Tertiary, UsagePriority.SecondaryInput, UsagePriority.PrimaryInput };

    private void SettleNetwork(Registration reg)
    {
        long totalDemand = 0;
        foreach (var (_, _, amount) in reg.Demand) totalDemand += amount;

        long remaining = totalDemand;
        foreach (var tier in SupplyTierOrder)
        {
            var tierEntries = reg.Supply.FindAll(e => e.Priority == tier);
            long tierCapacity = 0;
            foreach (var e in tierEntries) tierCapacity += e.Amount;
            long used = Math.Min(tierCapacity, remaining);
            if (tierCapacity > 0)
            {
                var ratio = Q16.FromRatio(used, tierCapacity);
                foreach (var e in tierEntries)
                    _allocatedSupply[e.Id] = ratio.Mul(e.Amount);
            }
            remaining -= used;
        }
        long shortfall = remaining;

        foreach (var tier in DemandTierReverseOrder)
        {
            var tierEntries = reg.Demand.FindAll(e => e.Priority == tier);
            long tierDemand = 0;
            foreach (var e in tierEntries) tierDemand += e.Amount;
            long absorbed = Math.Min(tierDemand, shortfall);
            Q16 satisfaction = tierDemand > 0 ? Q16.FromRatio(tierDemand - absorbed, tierDemand) : Q16.One;
            foreach (var e in tierEntries) _satisfaction[e.Id] = satisfaction;
            shortfall -= absorbed;
        }
    }

    private Registration GetOrCreateRegistration(NetworkId net)
    {
        if (!_registrations.TryGetValue(net, out var reg))
            _registrations[net] = reg = new Registration();
        return reg;
    }
```

Add the internal type (near `Network`/`PoleInfo`):

```csharp
internal sealed class Registration
{
    public List<(EntityId Id, UsagePriority Priority, long Amount)> Supply = new();
    public List<(EntityId Id, UsagePriority Priority, long Amount)> Demand = new();
}
```

- [ ] **Step 4: Run the settlement tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ElectricGridTests"`
Expected: all PASS.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. Baseline + Task 1/2 + 7 new settlement tests.

- [ ] **Step 6: Commit**

```bash
git add sim/Faketorio.Sim/Electric/ElectricGrid.cs sim/Faketorio.Sim.Tests/ElectricGridTests.cs
git commit -m "$(cat <<'EOF'
feat(electric): ElectricGrid settlement — RegisterSupply/Demand + Settle

Per-network per-tick settlement: supply tiers consumed ascending
(Solar->PrimaryOutput->SecondaryOutput), each tier's producers throttled
proportionally to what's actually needed (no wasted fuel when supply
exceeds demand); any shortfall absorbed by demand tiers in descending
priority (Tertiary sacrificed first, PrimaryInput protected). Networks
settle independently so Dictionary enumeration order during Settle is
harmless, but iteration is still sorted by NetworkId for clarity.
Settle() clears registrations so stale results never leak into a tick
with no new registrations.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01PTp9xsBoFKWvGqksfjuUUp
EOF
)"
```

---

## Task 4: Fuel generator + transfer commands + `Simulation` wiring

**Files:**
- Modify: `sim/Faketorio.Sim/Electric/ElectricGrid.cs`, `sim/Faketorio.Sim/Commands/Command.cs`, `sim/Faketorio.Sim/Simulation.cs`
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`, `sim/Faketorio.Sim.Tests/DeterminismTests.cs`

**Interfaces:**
- Consumes: `ElectricGrid` full API (Tasks 2–3), `ElectricPolePrototype`/`FuelGeneratorPrototype` (Task 1), `Inventories.AddContainer` with filter (Task 1), `ItemPrototype.FuelValueJ` (existing), `ValueNoise.Isqrt` (existing), `Player.X/Y`, `_playerProto.ReachSubTiles` (existing, P5).
- Produces: `Simulation.ElectricGrid` property; `CommandType.TransferToEntity`/`TransferFromEntity`.

- [ ] **Step 1: Add `Command.Count` and the 2 new `CommandType` values**

In `sim/Faketorio.Sim/Commands/Command.cs`:

```csharp
public enum CommandType : byte
{
    PlaceEntity = 1,
    RemoveEntity = 2,
    MovePlayer = 3,
    StopPlayer = 4,
    MineStart = 5,
    MineStop = 6,
    CraftEnqueue = 7,
    TransferToEntity = 8,
    TransferFromEntity = 9,
}

public struct Command
{
    public CommandType Type;
    public int ProtoId;
    public int X;
    public int Y;
    public byte Rotation;
    public int Count;
}
```

- [ ] **Step 2: Add generator fuel-buffer bookkeeping to `ElectricGrid`**

Add fields and methods to `ElectricGrid.cs`:

```csharp
    private readonly Dictionary<EntityId, long> _fuelBufferJ = new();

    public void RegisterGenerator(EntityId id) => _fuelBufferJ[id] = 0;
    public void UnregisterGenerator(EntityId id) => _fuelBufferJ.Remove(id);
    public long GetFuelBufferJ(EntityId id) => _fuelBufferJ.TryGetValue(id, out var v) ? v : 0;
    public void SetFuelBufferJ(EntityId id, long value) => _fuelBufferJ[id] = value;

    // 只序列化燃料缓冲——连通分量/结算结果都是每 tick 派生数据,不持久化。
    public void WriteState(Faketorio.Sim.State.IStateWriter writer)
    {
        var ids = new List<EntityId>(_fuelBufferJ.Keys);
        ids.Sort((a, b) => a.Index.CompareTo(b.Index));
        writer.Write(ids.Count);
        foreach (var id in ids)
        {
            writer.Write(id.Index);
            writer.Write(id.Generation);
            writer.Write(_fuelBufferJ[id]);
        }
    }
```

- [ ] **Step 3: Write the failing `SimulationTests`**

Append to `sim/Faketorio.Sim.Tests/SimulationTests.cs` (inside `SimulationTests`; `NewSim()` and other helpers already exist). Add `using Faketorio.Sim.Electric;` at the top if not present.

```csharp
    private static Command PlacePole(Simulation sim, int x, int y) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<ElectricPolePrototype>("small-electric-pole").Id,
        X = x, Y = y, Rotation = 0,
    };

    private static Command PlaceGenerator(Simulation sim, int x, int y) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<FuelGeneratorPrototype>("burner-generator").Id,
        X = x, Y = y, Rotation = 0,
    };

    private static Command TransferTo(int x, int y, int itemId, int count) => new()
        { Type = CommandType.TransferToEntity, X = x, Y = y, ProtoId = itemId, Count = count };

    [Fact]
    public void PlaceTwoPoles_WithinRange_SameNetwork()
    {
        var sim = NewSim();
        sim.Submit(PlacePole(sim, 0, 0));
        sim.Submit(PlacePole(sim, 5, 0));
        sim.Step();
        Assert.Equal(sim.ElectricGrid.FindNetworkAt(0, 0), sim.ElectricGrid.FindNetworkAt(5, 0));
    }

    [Fact]
    public void RemovePole_LeavesTheOtherPoleAlone()
    {
        var sim = NewSim();
        sim.Submit(PlacePole(sim, 0, 0));
        sim.Submit(PlacePole(sim, 6, 0));
        sim.Step();
        Assert.Equal(sim.ElectricGrid.FindNetworkAt(0, 0), sim.ElectricGrid.FindNetworkAt(6, 0));

        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 0, Y = 0 });
        sim.Step();
        Assert.False(sim.ElectricGrid.FindNetworkAt(0, 0).IsValid);
        Assert.True(sim.ElectricGrid.FindNetworkAt(6, 0).IsValid);
    }

    [Fact]
    public void TransferToEntity_FillsGeneratorFuelSlot()
    {
        var sim = NewSim();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        sim.Player.Inventory.Insert(coal, 5, sim.Prototypes.Get<ItemPrototype>("coal").StackSize);
        sim.Submit(PlaceGenerator(sim, 0, 0));
        sim.Step();
        sim.Submit(TransferTo(0, 0, coal, 3));
        sim.Step();

        var genId = sim.World.GetEntityAt(0, 0);
        var fuelInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(genId));
        Assert.Equal(3, fuelInv.CountOf(coal));
    }

    [Fact]
    public void TransferToEntity_OutOfReach_IsRejected()
    {
        var sim = NewSim();
        sim.Player.Inventory.Insert(sim.Prototypes.Get<ItemPrototype>("coal").Id, 5, 50);
        sim.Submit(PlaceChest(sim, 20, 0));
        sim.Step();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        sim.Submit(TransferTo(20, 0, coal, 1));
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
    }

    [Fact]
    public void GeneratorWithFuel_PowersManuallyRegisteredDemand_BurnsProportionally()
    {
        var sim = NewSim();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        sim.Player.Inventory.Insert(coal, 5, coalStack);

        sim.Submit(PlacePole(sim, 0, 0));
        sim.Submit(PlaceGenerator(sim, 2, 0));   // Chebyshev distance 2 <= pole's supplyAreaDistanceTiles 2
        sim.Step();
        sim.Submit(TransferTo(2, 0, coal, 5));
        sim.Step();

        var genId = sim.World.GetEntityAt(2, 0);
        long powerPerTick = sim.Prototypes.Get<FuelGeneratorPrototype>("burner-generator").PowerOutputJPerTick;
        var fakeConsumer = new EntityId(999, 1);

        sim.ElectricGrid.RegisterDemand(fakeConsumer, 0, 0, UsagePriority.PrimaryInput, powerPerTick / 2);
        sim.Step();   // Step 内:发电机登记供给(此时已有煤)-> Settle(与上面手动登记的需求一起结算)-> 烧油

        Assert.Equal(Q16.One, sim.ElectricGrid.GetSatisfaction(fakeConsumer));
        Assert.True(sim.ElectricGrid.GetFuelBufferJ(genId) > 0);   // 只烧了一半,缓冲还有剩(一块煤够很多 tick)
    }
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests.PlaceTwoPoles"`
Expected: FAIL — `Simulation.ElectricGrid` does not exist (compile error).

- [ ] **Step 5: Wire `ElectricGrid` into `Simulation`**

In `sim/Faketorio.Sim/Simulation.cs`:

1. Add `using Faketorio.Sim.Electric;` to the using block.

2. Add the property near `Player`:

```csharp
    public Player Player { get; }
    public ElectricGrid ElectricGrid { get; } = new();
```

3. In `Apply` → `case CommandType.PlaceEntity`, after the existing `if (proto is ContainerPrototype cp) Inventories.AddContainer(id, cp.InventorySize);`, add:

```csharp
                if (proto is ElectricPolePrototype pole)
                    ElectricGrid.RegisterPole(id, command.X, command.Y, pole.MaximumWireDistanceTiles, pole.SupplyAreaDistanceTiles);
                if (proto is FuelGeneratorPrototype gen)
                {
                    Inventories.AddContainer(id, 1, filterItemProtoId: gen.FuelItemProtoId);
                    ElectricGrid.RegisterGenerator(id);
                }
```

4. In `DestroyEntityAt`, add the pole/generator flags and hooks:

```csharp
    private void DestroyEntityAt(EntityId id, EntityPrototype proto)
    {
        ref var data = ref Entities.Get(id);
        int bx = data.X, by = data.Y;
        bool isBelt = proto is TransportBeltPrototype;
        bool isContainer = proto is ContainerPrototype;
        bool isPole = proto is ElectricPolePrototype;
        bool isGenerator = proto is FuelGeneratorPrototype;
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
    }
```

5. Add 2 cases to `Apply` (before `default:`):

```csharp
            case CommandType.TransferToEntity:
            {
                long ddx = Player.X - (command.X * 256 + 128);
                long ddy = Player.Y - (command.Y * 256 + 128);
                if (ValueNoise.Isqrt(ddx * ddx + ddy * ddy) > _playerProto.ReachSubTiles
                    || !Prototypes.TryGetById(command.ProtoId, out var ip) || ip is not ItemPrototype itemProto
                    || command.Count <= 0)
                {
                    RejectedCommandCount++;
                    return;
                }
                var eid = World.GetEntityAt(command.X, command.Y);
                var targetInvId = eid.IsValid ? Inventories.GetInventoryId(eid) : InventoryId.Invalid;
                if (!targetInvId.IsValid)
                {
                    RejectedCommandCount++;
                    return;
                }
                int amount = Math.Min(command.Count, Player.Inventory.CountOf(command.ProtoId));
                if (amount <= 0)
                {
                    RejectedCommandCount++;
                    return;
                }
                var targetInv = Inventories.Get(targetInvId);
                int inserted = targetInv.Insert(command.ProtoId, amount, itemProto.StackSize);
                Player.Inventory.Remove(command.ProtoId, inserted);
                return;
            }
            case CommandType.TransferFromEntity:
            {
                long ddx2 = Player.X - (command.X * 256 + 128);
                long ddy2 = Player.Y - (command.Y * 256 + 128);
                if (ValueNoise.Isqrt(ddx2 * ddx2 + ddy2 * ddy2) > _playerProto.ReachSubTiles
                    || !Prototypes.TryGetById(command.ProtoId, out var ip2) || ip2 is not ItemPrototype itemProto2
                    || command.Count <= 0)
                {
                    RejectedCommandCount++;
                    return;
                }
                var eid2 = World.GetEntityAt(command.X, command.Y);
                var sourceInvId = eid2.IsValid ? Inventories.GetInventoryId(eid2) : InventoryId.Invalid;
                if (!sourceInvId.IsValid)
                {
                    RejectedCommandCount++;
                    return;
                }
                var sourceInv = Inventories.Get(sourceInvId);
                int amount2 = Math.Min(command.Count, sourceInv.CountOf(command.ProtoId));
                if (amount2 <= 0)
                {
                    RejectedCommandCount++;
                    return;
                }
                int removed = sourceInv.Remove(command.ProtoId, amount2);
                int inserted2 = Player.Inventory.Insert(command.ProtoId, removed, itemProto2.StackSize);
                if (inserted2 < removed)
                    sourceInv.Insert(command.ProtoId, removed - inserted2, itemProto2.StackSize);
                return;
            }
```

6. In `Step`, add the electric-tick segment after `PlayerCraft();` and before the belt-advance loop:

```csharp
        PlayerWalk();
        PlayerMine();
        PlayerCraft();

        // 电网:① 发电机登记供给 ② 结算 ③ 烧油结算
        ElectricGeneratorsRegisterSupply();
        ElectricGrid.Settle();
        ElectricGeneratorsBurnFuel();

        // 传送带:推进 ...
```

Add the two helper methods (private, near `PlayerCraft`):

```csharp
    // 扫实体池找发电机(同 belt 推进/WriteState 的索引序扫法,不给 ElectricGrid
    // 塞 Inventories/Prototypes 依赖)。登记「有燃料就能出满功率,没燃料出 0」。
    private void ElectricGeneratorsRegisterSupply()
    {
        for (int i = 0; i < Entities.Capacity; i++)
        {
            if (!Entities.IsAliveAtIndex(i)) continue;
            ref var data = ref Entities.GetAtIndex(i);
            if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not FuelGeneratorPrototype gen) continue;
            var id = new EntityId(i, Entities.GenerationAtIndex(i));
            long buf = ElectricGrid.GetFuelBufferJ(id);
            var fuelInv = Inventories.Get(Inventories.GetInventoryId(id));
            bool hasFuel = buf > 0 || fuelInv.CountOf(gen.FuelItemProtoId) > 0;
            ElectricGrid.RegisterSupply(id, data.X, data.Y, UsagePriority.PrimaryOutput, hasFuel ? gen.PowerOutputJPerTick : 0);
        }
    }

    private void ElectricGeneratorsBurnFuel()
    {
        for (int i = 0; i < Entities.Capacity; i++)
        {
            if (!Entities.IsAliveAtIndex(i)) continue;
            ref var data = ref Entities.GetAtIndex(i);
            if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not FuelGeneratorPrototype gen) continue;
            var id = new EntityId(i, Entities.GenerationAtIndex(i));
            long actual = ElectricGrid.GetAllocatedSupply(id);
            long buf = ElectricGrid.GetFuelBufferJ(id);
            var fuelInv = Inventories.Get(Inventories.GetInventoryId(id));
            var fuelItemProto = (ItemPrototype)Prototypes.GetById(gen.FuelItemProtoId);
            while (buf < actual && fuelInv.CountOf(gen.FuelItemProtoId) > 0)
            {
                fuelInv.Remove(gen.FuelItemProtoId, 1);
                buf += fuelItemProto.FuelValueJ;
            }
            long delivered = Math.Min(actual, buf);
            ElectricGrid.SetFuelBufferJ(id, buf - delivered);
        }
    }
```

7. In `WriteState`, add after `Player.WriteState(writer);`:

```csharp
        ElectricGrid.WriteState(writer);
```

- [ ] **Step 6: Run the `SimulationTests`**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests"`
Expected: all PASS (existing + new).

- [ ] **Step 7: Run the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. No regressions.

- [ ] **Step 8: Commit**

```bash
git add sim/Faketorio.Sim/Electric/ElectricGrid.cs sim/Faketorio.Sim/Commands/Command.cs sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "$(cat <<'EOF'
feat(sim): fuel generator + TransferToEntity/TransferFromEntity + wiring

Simulation gains ElectricGrid; PlaceEntity/RemoveEntity hook up poles and
generators (generator's 1-slot fuel inventory filtered to its
FuelItemProtoId). A new electric-tick segment (after the player tick,
before belt advance) scans the entity pool for generators -- mirroring
the belt-advance scan, keeping ElectricGrid free of Inventories/Prototypes
-- to register supply, settle, then burn exactly the allocated share
(refueling from the filtered inventory mid-tick as needed). Command gains
a generic TransferToEntity/TransferFromEntity pair (reach-gated, same math
as HandMine) for loading fuel now and any future entity interaction.
ElectricGrid.WriteState appended last.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01PTp9xsBoFKWvGqksfjuUUp
EOF
)"
```

- [ ] **Step 9: Write the failing determinism test**

Append to `sim/Faketorio.Sim.Tests/DeterminismTests.cs` (inside `DeterminismTests`):

```csharp
    private static List<ulong> RunElectricScenario(long seed)
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"), seed);
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        sim.Player.Inventory.Insert(coal, 5, coalStack);
        var hashes = new List<ulong>();
        for (int t = 0; t < 30; t++)
        {
            if (t == 0)
            {
                sim.Submit(new Command { Type = CommandType.PlaceEntity,
                    ProtoId = sim.Prototypes.Get<ElectricPolePrototype>("small-electric-pole").Id, X = 0, Y = 0 });
                sim.Submit(new Command { Type = CommandType.PlaceEntity,
                    ProtoId = sim.Prototypes.Get<FuelGeneratorPrototype>("burner-generator").Id, X = 2, Y = 0 });
            }
            if (t == 5)
                sim.Submit(new Command { Type = CommandType.TransferToEntity, X = 2, Y = 0, ProtoId = coal, Count = 5 });
            if (t >= 10)
                sim.ElectricGrid.RegisterDemand(new EntityId(9999, 1), 0, 0, UsagePriority.PrimaryInput, 500);
            sim.Step();
            hashes.Add(sim.ComputeStateHash());
        }
        return hashes;
    }

    [Fact]
    public void ElectricScenario_SameSeedSameCommands_SameHashEveryTick()
        => Assert.Equal(RunElectricScenario(4242), RunElectricScenario(4242));
```

Add `using Faketorio.Sim.Electric;` and `using Faketorio.Sim.Entities;` to the top of `DeterminismTests.cs` if not already present.

- [ ] **Step 10: Run the determinism test + full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~DeterminismTests"`
Expected: all PASS — the new scenario is equal run-vs-run, and all 5 pre-existing golden scenarios (`SameCommands_SameHashEveryTick`, `BeltScenario_...`, `InventoryScenario_...`, `ResourceScenario_...`, `PlayerScenario_...`) still pass (their helpers are untouched; `Simulation.WriteState` gains a trailing `ElectricGrid` block that shifts absolute hashes identically across both runs of each comparison).

- [ ] **Step 11: Run the whole suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. 276 baseline + Task 1 (9) + Task 2 (8) + Task 3 (7) + Task 4 (`SimulationTests` 5, `DeterminismTests` 1).

- [ ] **Step 12: Commit**

```bash
git add sim/Faketorio.Sim.Tests/DeterminismTests.cs
git commit -m "$(cat <<'EOF'
test(sim): determinism coverage for the electric grid

Pole + generator placement, fuel transfer, and a manually-registered
demand over 30 ticks, hash-equal at the same seed. Golden scenarios
untouched.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01PTp9xsBoFKWvGqksfjuUUp
EOF
)"
```

---

## Self-Review

**1. Spec coverage:**

| Spec section | Task / step |
|---|---|
| §2 file structure | Tasks 1–4 |
| §3 pole connectivity (full recompute, Euclidean, dirty-flag gated, stricter-side wire distance, deterministic network ordering) | Task 2 Step 4 |
| §4 `FindNetworkAt` (Chebyshev, network-index then pole-index tie-break) | Task 2 Step 4 |
| §5 `UsagePriority` 6 tiers, `RegisterSupply`/`RegisterDemand` (network-not-found → 0/Zero), `Settle()` algorithm (supply ascending throttled proportionally, shortfall absorbed by demand descending) | Task 1 Step 1, Task 3 Step 3 |
| §5 "登记结果暂存... `Settle()` 后清空... 每个实体每 tick 只登记一次" | Task 3 Step 3 (`Registration`/`_registrations.Clear()`) + `Settle_ClearsRegistrationsForNextTick` test |
| §6 `FuelGeneratorPrototype`, per-tick registration/burn algorithm, `_fuelBufferJ` | Task 1 Step 2, Task 4 Steps 2/5.6 |
| §7 `TransferToEntity`/`TransferFromEntity`, `Command.Count`, `AddContainer` overload | Task 1 Step 5, Task 4 Steps 1/5.5 |
| §8 `Simulation` wiring (property, `Step` order, `Apply` cases, `PlaceEntity`/`RemoveEntity` hooks, `WriteState` position) | Task 4 Step 5 |
| §9 determinism (no float, `Q16` ratios, sorted iteration, `WriteState` scope) | Task 2 Step 4, Task 3 Step 3, Task 4 Step 2 |
| §10 tests | Tasks 1–4 test steps |
| §11 four-task split | Tasks 1/2/3/4 match §11, with the flagged `WriteState`-to-Task-4 move documented in Global Constraints |

No gaps.

**2. Placeholder scan:** No "TBD"/"TODO"/"handle edge cases". Every code step has complete code; every test step has full bodies.

**3. Type consistency:**
- `UsagePriority` values — defined Task 1 Step 1; used identically in Task 3 (`SupplyTierOrder`/`DemandTierReverseOrder` arrays) and Task 4 (`RegisterSupply(..., UsagePriority.PrimaryOutput, ...)`, test's `UsagePriority.PrimaryInput`).
- `ElectricPolePrototype`/`FuelGeneratorPrototype` fields — defined Task 1 Step 2; read in Task 4 Step 5.3 (`pole.MaximumWireDistanceTiles`, `gen.FuelItemProtoId`, `gen.PowerOutputJPerTick`) exactly as named.
- `ElectricGrid.RegisterPole(EntityId, int, int, int, int)` — defined Task 2 Step 4; called Task 4 Step 5.3 with `(id, command.X, command.Y, pole.MaximumWireDistanceTiles, pole.SupplyAreaDistanceTiles)` — 5 args, matches.
- `ElectricGrid.RegisterSupply/RegisterDemand/Settle/GetAllocatedSupply/GetSatisfaction` — defined Task 3 Step 3; consumed Task 4 Steps 5.6 (helpers) and test Step 3 (manual registration). Signatures match exactly.
- `ElectricGrid.RegisterGenerator/UnregisterGenerator/GetFuelBufferJ/SetFuelBufferJ/WriteState` — defined Task 4 Step 2; consumed Task 4 Step 5 (`PlaceEntity`/`DestroyEntityAt`/helpers/`WriteState`). Consistent.
- `Inventories.AddContainer(EntityId, int, bool, int)` — defined Task 1 Step 5; called Task 4 Step 5.3 with `(id, 1, filterItemProtoId: gen.FuelItemProtoId)` — named-argument call skipping `readOnly`, valid against the optional-params signature.
- `Command.Count` — defined Task 4 Step 1; used in `TransferToEntity`/`TransferFromEntity` cases and every test helper that builds those commands.
- `NetworkId(int Index)`, `Invalid`, `IsValid` — defined Task 2 Step 1; used throughout Tasks 2–4.
- `Q16.FromRatio`/`Q16.One`/`Q16.Zero`/`.Mul` — existing (`sim/Faketorio.Sim/Q16.cs`); used exactly as existing signatures in Task 3.
- `ValueNoise.Isqrt(long) → int` — existing; used in Task 2 (`dx*dx+dy*dy` as `long`) and Task 4 (reach check, same pattern as `PlayerMine`).
- Data: `small-electric-pole`/`burner-generator` names used consistently across `electric.json`, loader tests, `SimulationTests`, `DeterminismTests`.

Consistent throughout.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-09-04-m1-plan7-electric-grid.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

**Which approach?**
