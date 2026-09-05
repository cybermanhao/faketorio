# M1 Plan 9 — 加工状态机(熔炉 + 装配机)Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **状态: ✅ 已合并 main · 已验证** — 主线提交 `a9ed5c4`..`0c93ac1`(见 `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md` 进度快照)。下方 `- [ ]` 复选框为执行期工件,不代表当前状态。

**Goal:** Give the simulation a shared processing state machine for furnaces and assembling machines — recipe selection (furnace auto-matches, assembling machine needs an explicit `SetRecipe` command) → electricity check (registers demand, reads back satisfaction from `ElectricGrid`) → progress advance (throttled proportionally by satisfaction, frozen rather than reset when ingredients run short) → atomic all-or-nothing output placement → pending-output retry when the output is full.

**Architecture:** `CraftingMachinePrototype` (abstract, `FurnacePrototype`/`AssemblingMachinePrototype` concrete) carries the static machine data. A new `Machines` class (flat `Faketorio.Sim` namespace — see Task 2's namespace-collision note) is a pure `Dictionary<EntityId, MachineRuntimeState>` runtime-state container, deliberately blind to `Simulation`/`Prototypes`/`Inventories` (same isolation discipline as `ElectricGrid`). `Inventories` grows a `role` parameter so one entity can own two independent inventories (machine input = role 1, output = role 2) without inventing a new storage type. `Simulation.MachinesTick()` is split into two full entity-pool scans — `MachinesTickPreSettle` (recipe selection + demand registration) and `MachinesTickPostSettle` (progress + completion) — bracketing `ElectricGrid.Settle()`, mirroring the existing fuel-generator register/settle/burn ordering.

**Tech Stack:** C# / .NET 8, xUnit. No Godot dependency in `Faketorio.Sim`. Deterministic fixed-tick simulation — canonical serialization through `IStateWriter` / `Fnv1aHashWriter` (FNV-1a).

**Spec:** [`docs/superpowers/specs/2026-09-05-m1-plan9-processing-machines-design.md`](../specs/2026-09-05-m1-plan9-processing-machines-design.md)

## Global Constraints

- **Determinism 铁律:** no `float`/`double` anywhere in machine-tick math. `Progress`/`threshold` are `long`, representing Q16.16 fixed-point "tick count" (not raw ticks) — `CraftingSpeed` (`Q16`) and `satisfaction` (`Q16`, from `ElectricGrid.GetSatisfaction`) multiply via `Q16.Mul`, producing a Q16.16 delta added directly to `Progress`. `threshold = (long)recipe.EnergyRequiredTicks << 16`.
- Furnace auto-match iterates `RecipePrototype`s by ascending `Id` (i.e. `for (int rid = 0; rid < Prototypes.Count; rid++)`) — first satisfiable recipe wins, deterministic regardless of any unsorted container.
- All entity-pool scans use `Entities.Capacity`/`IsAliveAtIndex`/`GetAtIndex` index order (same convention as the existing belt-advance and fuel-generator scans in `Simulation.cs`) — no active-list optimization in this plan.
- `ElectricGrid.RegisterDemand` must be called **exactly once per machine per tick**, unconditionally (constant standby draw whether or not a recipe is running) — this is a hard contract from `ElectricGrid`'s own docs (P7).
- **Namespace collision (binding, do not deviate):** the `Machines` class MUST be declared in flat `namespace Faketorio.Sim;`, NOT `namespace Faketorio.Sim.Machines;`. A class named `Machines` inside a `Faketorio.Sim.Machines` namespace self-collides when referenced unqualified from `Simulation.cs` (`namespace Faketorio.Sim`) — C# resolves the bare name `Machines` to the namespace segment, not the type. This is the exact same bug P5 hit with `Player`/`namespace Faketorio.Sim.Player`, fixed the same way there. The physical file still lives at `sim/Faketorio.Sim/Machines/Machines.cs` — only the `namespace` declaration inside it is flat.
- **Recipe stickiness:** furnace's `CurrentRecipeProtoId` is transient (re-derived every cycle, cleared on completion via `RestartCycle(id, clearRecipe: true)`). Assembling machine's `CurrentRecipeProtoId` is persistent (set once via `SetRecipe`, survives completion and any future revalidation via `RestartCycle(id, clearRecipe: false)`, changed only by another `SetRecipe`).
- **Progress gating:** progress only advances when the input inventory (role 1) *currently* satisfies `recipe.ResolvedIngredients` — checked every tick before advancing, not only at the completion threshold. Insufficient ingredients freeze `Progress` (no reset, no data loss) rather than aborting the cycle. This applies uniformly to furnaces and assembling machines.
- `data/base/recipes.json` is **not modified** by this plan — `iron-plate` (category `"smelting"`, 192 ticks) already exists and is unreachable by `CraftEnqueue` (which only accepts `"crafting"`-category recipes); the furnace in this plan's test data consumes it directly.
- Every commit ends with:
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01BXC5RZ5wsWirQpprXn9KZV
  ```
- Baseline before Task 1: 307 tests passing (`dotnet test sim/Faketorio.Sim.Tests`), `dotnet build -c Release` 0 warnings/0 errors.

---

## Task 1: Prototypes + loader validation + data + `Inventories` role support

**Files:**
- Create: `sim/Faketorio.Sim/Prototypes/CraftingMachinePrototype.cs`
- Create: `data/base/machines.json`
- Modify: `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`
- Modify: `sim/Faketorio.Sim/Items/Inventories.cs`
- Test: `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs`
- Test: `sim/Faketorio.Sim.Tests/InventoriesTests.cs`

**Interfaces:**
- Produces (for Task 3): `CraftingMachinePrototype { Category, InputSlots, OutputSlots, CraftingSpeed, EnergyUsageJPerTick }`, `FurnacePrototype`, `AssemblingMachinePrototype` (both `sealed`, no extra fields — behavior branches on `is FurnacePrototype`/`is AssemblingMachinePrototype`).
- Produces (for Task 3): `Inventories.AddContainer(EntityId entity, int slotCount, bool readOnly = false, int filterItemProtoId = 0, int role = 0)`, `Inventories.GetInventoryId(EntityId entity, int role = 0)`, `Inventories.RemoveContainer(EntityId entity, int role = 0)`.
- Produces (data): `data/base/machines.json` with prototypes named `"stone-furnace"` and `"assembling-machine-1"`.
- Consumes: nothing from other P9 tasks. Does not touch `Simulation.cs`.

- [ ] **Step 1: Create `CraftingMachinePrototype.cs`**

```csharp
namespace Faketorio.Sim.Prototypes;

// 熔炉/装配机的共用字段。行为分支由具体类型决定(is FurnacePrototype /
// is AssemblingMachinePrototype),不用 bool 标志——同 P7 ElectricPolePrototype/
// FuelGeneratorPrototype 的先例。
public abstract class CraftingMachinePrototype : EntityPrototype
{
    public required string Category { get; init; }      // 必须匹配某些 RecipePrototype.Category
    public int InputSlots { get; init; }
    public int OutputSlots { get; init; }
    public Q16 CraftingSpeed { get; init; } = Q16.One;   // 进度倍率,M1 数据恒 1.0(JSON 不解析这个字段)
    public long EnergyUsageJPerTick { get; init; }        // 每 tick 向电网登记的 PrimaryInput 需求
}

public sealed class FurnacePrototype : CraftingMachinePrototype { }           // 自动匹配配方
public sealed class AssemblingMachinePrototype : CraftingMachinePrototype { } // 需要 SetRecipe
```

- [ ] **Step 2: Add the two `Parse` arms in `PrototypeLoader.cs`**

Open `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`. In the `Parse` method's `switch` expression, insert two new arms immediately after the existing `"fuel-generator" => ValidateFootprint(new FuelGeneratorPrototype { ... }),` arm and before the `_ => throw new InvalidDataException(...)` default arm:

```csharp
            "furnace" => ValidateFootprint(new FurnacePrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                Category = el.GetProperty("category").GetString()!,
                InputSlots = GetInt(el, "inputSlots", 0),
                OutputSlots = GetInt(el, "outputSlots", 0),
                EnergyUsageJPerTick = el.TryGetProperty("energyUsage", out var fEu) ? Units.ParsePower(fEu.GetString()!) : 0L,
            }),
            "assembling-machine" => ValidateFootprint(new AssemblingMachinePrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                Category = el.GetProperty("category").GetString()!,
                InputSlots = GetInt(el, "inputSlots", 0),
                OutputSlots = GetInt(el, "outputSlots", 0),
                EnergyUsageJPerTick = el.TryGetProperty("energyUsage", out var aEu) ? Units.ParsePower(aEu.GetString()!) : 0L,
            }),
```

- [ ] **Step 3: Add the `ResolveAndValidateCraftingMachines` sibling pass**

In the same file, add a new private static method (place it right after `ResolveAndValidateElectric`, before `ResolveAndValidateMapGen`):

```csharp
    // AssignIds() 之后:校验熔炉/装配机的公共字段。不校验 Category 一定有配方存在——
    // 允许先加机器后加配方的数据组织顺序,运行时匹配不到只是空转,不是加载期错误。
    private static void ResolveAndValidateCraftingMachines(PrototypeRegistry registry)
    {
        for (int i = 0; i < registry.Count; i++)
        {
            if (registry.GetById(i) is not CraftingMachinePrototype m) continue;
            if (string.IsNullOrEmpty(m.Category))
                throw new InvalidDataException($"Crafting machine '{m.Name}': category must be non-empty");
            if (m.InputSlots < 1)
                throw new InvalidDataException($"Crafting machine '{m.Name}': inputSlots must be >= 1");
            if (m.OutputSlots < 1)
                throw new InvalidDataException($"Crafting machine '{m.Name}': outputSlots must be >= 1");
            if (m.EnergyUsageJPerTick < 0)
                throw new InvalidDataException($"Crafting machine '{m.Name}': energyUsage must be >= 0");
        }
    }
```

Then update `LoadFromDirectory` to call it — change:

```csharp
        ResolveAndValidateMapGen(registry);
        ResolveAndValidateRecipesAndPlayer(registry);
        ResolveAndValidateElectric(registry);
        return registry;
```

to:

```csharp
        ResolveAndValidateMapGen(registry);
        ResolveAndValidateRecipesAndPlayer(registry);
        ResolveAndValidateElectric(registry);
        ResolveAndValidateCraftingMachines(registry);
        return registry;
```

- [ ] **Step 4: Create `data/base/machines.json`**

```json
[
  { "type": "furnace", "name": "stone-furnace", "tileWidth": 2, "tileHeight": 2,
    "category": "smelting", "inputSlots": 1, "outputSlots": 1, "energyUsage": "90kW" },
  { "type": "assembling-machine", "name": "assembling-machine-1", "tileWidth": 3, "tileHeight": 3,
    "category": "crafting", "inputSlots": 2, "outputSlots": 1, "energyUsage": "75kW" }
]
```

- [ ] **Step 5: Run the full suite to confirm nothing broke and the new prototypes load**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS, 307 (baseline — no new tests reference the new types yet, but the loader now parses `machines.json` on every `Load()` call across the whole suite, so this also proves the new prototypes don't break anything else that loads `data/base`).

- [ ] **Step 6: Write `PrototypeLoaderTests.cs` additions**

Append to `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` (inside the `PrototypeLoaderTests` class, near the existing electric-pole/fuel-generator tests):

```csharp
    [Fact]
    public void LoadsFurnace()
    {
        var furnace = Load().Get<FurnacePrototype>("stone-furnace");
        Assert.Equal("smelting", furnace.Category);
        Assert.Equal(1, furnace.InputSlots);
        Assert.Equal(1, furnace.OutputSlots);
        Assert.Equal(1500, furnace.EnergyUsageJPerTick);   // 90kW / 60 ticks-per-second
    }

    [Fact]
    public void LoadsAssemblingMachine()
    {
        var asm = Load().Get<AssemblingMachinePrototype>("assembling-machine-1");
        Assert.Equal("crafting", asm.Category);
        Assert.Equal(2, asm.InputSlots);
        Assert.Equal(1, asm.OutputSlots);
        Assert.Equal(1250, asm.EnergyUsageJPerTick);   // 75kW / 60 ticks-per-second
    }

    [Fact]
    public void CraftingMachineEmptyCategory_Throws() => AssertLoadThrows(
        "[{ \"type\": \"furnace\", \"name\": \"f\", \"category\": \"\", \"inputSlots\": 1, \"outputSlots\": 1 }]");

    [Fact]
    public void CraftingMachineZeroInputSlots_Throws() => AssertLoadThrows(
        "[{ \"type\": \"furnace\", \"name\": \"f\", \"category\": \"smelting\", \"inputSlots\": 0, \"outputSlots\": 1 }]");

    [Fact]
    public void CraftingMachineZeroOutputSlots_Throws() => AssertLoadThrows(
        "[{ \"type\": \"assembling-machine\", \"name\": \"a\", \"category\": \"crafting\", \"inputSlots\": 1, \"outputSlots\": 0 }]");

    [Fact]
    public void CraftingMachineNegativeEnergyUsage_Throws() => AssertLoadThrows(
        "[{ \"type\": \"furnace\", \"name\": \"f\", \"category\": \"smelting\", \"inputSlots\": 1, \"outputSlots\": 1, \"energyUsage\": \"-120W\" }]");
```

(Note on the last case: `Units.ParseEnergy("-120W")` = -120, and `ParsePower` divides by 60 with C# integer truncation-toward-zero, giving exactly -2 — safely negative. A `"-1W"` input would truncate to 0 and not trigger the validation, so the magnitude matters here.)

- [ ] **Step 7: Run the new loader tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~PrototypeLoaderTests"`
Expected: PASS, all `PrototypeLoaderTests` including the 6 new ones.

- [ ] **Step 8: Rewrite `Inventories.cs` with `role` support**

Replace the full contents of `sim/Faketorio.Sim/Items/Inventories.cs` with:

```csharp
using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Items;

// 库存的实体层门面:持有 InventoryPool + 两张对齐/反向的索引表。
// _byEntityRole 以 (EntityId.Index, role) 为键(哪个实体的哪个角色库存 -> InventoryId),
// _ownerByIndex 以池索引为键(库存 -> 所属实体 + role),互为反向。只点查、不遍历,
// 确定性不受影响。role 语义由调用方约定(Inventories 自己不解释):
// 0 = 默认/箱子/发电机燃料槽,1 = 机器输入,2 = 机器输出(P9)。
public sealed class Inventories
{
    private readonly InventoryPool _pool = new();
    private readonly Dictionary<(int EntityIndex, int Role), InventoryId> _byEntityRole = new();
    private (EntityId Owner, int Role)[] _ownerByIndex;   // 缺省 (Invalid, 0);与 _pool 索引对齐

    public Inventories(int initialCapacity = 64)
    {
        _ownerByIndex = new (EntityId, int)[initialCapacity];
        Array.Fill(_ownerByIndex, (EntityId.Invalid, 0));
    }

    // 建一个 slotCount 槽的库存并绑给 (entity, role)。前置:该 (entity, role) 尚未有库存。
    public InventoryId AddContainer(EntityId entity, int slotCount, bool readOnly = false, int filterItemProtoId = 0, int role = 0)
    {
        var id = _pool.Create(new Inventory(slotCount, readOnly, filterItemProtoId));
        _byEntityRole[(entity.Index, role)] = id;
        EnsureOwnerByIndex(id.Index);
        _ownerByIndex[id.Index] = (entity, role);
        return id;
    }

    // 毁掉 (entity, role) 的库存,返回它当时的物品总数。前置:该 (entity, role) 有库存。
    public int RemoveContainer(EntityId entity, int role = 0)
    {
        var key = (entity.Index, role);
        var id = _byEntityRole[key];
        int total = _pool.Get(id).TotalItems();
        _pool.Destroy(id);
        _byEntityRole.Remove(key);
        _ownerByIndex[id.Index] = (EntityId.Invalid, 0);
        return total;
    }

    public InventoryId GetInventoryId(EntityId entity, int role = 0)
        => _byEntityRole.TryGetValue((entity.Index, role), out var id) ? id : InventoryId.Invalid;

    public Inventory Get(InventoryId id) => _pool.Get(id);

    public int Capacity => _pool.Capacity;
    public bool IsAliveAtIndex(int index) => _pool.IsAliveAtIndex(index);
    public Inventory GetAtIndex(int index) => _pool.GetAtIndex(index);

    // 先写池分配器簿记,再按池索引序对每个存活库存写:
    // i / 代数 / 所属 EntityId(Index 再 Generation)/ role / ReadOnly(byte)/
    // FilterItemProtoId(int)/ inv.WriteState(自带槽数前缀 + 槽内容)。
    public void WriteState(IStateWriter writer)
    {
        _pool.WriteState(writer);
        for (int i = 0; i < _pool.Capacity; i++)
        {
            if (!_pool.IsAliveAtIndex(i)) continue;
            var item = _pool.GetAtIndex(i);
            var (owner, role) = _ownerByIndex[i];
            writer.Write(i);
            writer.Write(_pool.GenerationAtIndex(i));
            writer.Write(owner.Index);
            writer.Write(owner.Generation);
            writer.Write(role);
            writer.Write((byte)(item.ReadOnly ? 1 : 0));
            writer.Write(item.FilterItemProtoId);
            item.WriteState(writer);
        }
    }

    // Array.Resize 是零填充——每次扩容的新增段必须显式填 (Invalid, 0),
    // 否则未绑定的池索引会"看起来属于 EntityId(0,0)、role 0"。
    private void EnsureOwnerByIndex(int index)
    {
        if (index < _ownerByIndex.Length) return;
        int oldLen = _ownerByIndex.Length;
        int newLen = oldLen == 0 ? 1 : oldLen;
        while (newLen <= index) newLen *= 2;
        Array.Resize(ref _ownerByIndex, newLen);
        Array.Fill(_ownerByIndex, (EntityId.Invalid, 0), oldLen, newLen - oldLen);
    }
}
```

- [ ] **Step 9: Run the full suite to confirm the `Inventories` rewrite is backward-compatible**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS, 307/307 — every existing call site (`AddContainer(id, size)`, `AddContainer(id, 1, filterItemProtoId: x)`, `GetInventoryId(id)`, `RemoveContainer(id)`) omits `role` and gets the default `0`, so behavior for containers and the fuel generator's fuel slot is unchanged. If this fails, do not proceed — it means the role rewrite broke role-0 backward compatibility, which is this step's only job to verify.

- [ ] **Step 10: Write `InventoriesTests.cs` additions**

Append to `sim/Faketorio.Sim.Tests/InventoriesTests.cs` (inside the `InventoriesTests` class):

```csharp
    [Fact]
    public void AddContainer_DifferentRoles_AreIndependent()
    {
        var inv = new Inventories();
        var e = new EntityId(5, 1);
        var inputId = inv.AddContainer(e, 1, role: 1);
        var outputId = inv.AddContainer(e, 1, role: 2);

        inv.Get(inputId).Insert(Iron, Stack, Stack);   // fill role 1's one slot fully

        Assert.NotEqual(inputId, outputId);
        Assert.Equal(Stack, inv.Get(inputId).CountOf(Iron));
        Assert.Equal(0, inv.Get(outputId).CountOf(Iron));
        Assert.True(inv.Get(outputId).CanInsert(Iron, Stack, Stack));   // role 2 untouched by role 1 being full
    }

    [Fact]
    public void GetInventoryId_UnknownRole_ReturnsInvalid()
    {
        var inv = new Inventories();
        var e = new EntityId(6, 1);
        inv.AddContainer(e, 1, role: 1);

        Assert.False(inv.GetInventoryId(e, role: 2).IsValid);
        Assert.True(inv.GetInventoryId(e, role: 1).IsValid);
    }

    [Fact]
    public void RemoveContainer_WithRole_OnlyRemovesThatRole()
    {
        var inv = new Inventories();
        var e = new EntityId(7, 1);
        inv.AddContainer(e, 1, role: 1);
        var outputId = inv.AddContainer(e, 1, role: 2);

        inv.RemoveContainer(e, role: 1);

        Assert.False(inv.GetInventoryId(e, role: 1).IsValid);
        Assert.True(inv.GetInventoryId(e, role: 2).IsValid);
        Assert.Equal(outputId, inv.GetInventoryId(e, role: 2));
    }

    [Fact]
    public void DefaultRole_UnaffectedByOtherRolesOnSameEntity()
    {
        var inv = new Inventories();
        var e = new EntityId(8, 1);
        var defaultId = inv.AddContainer(e, 16);          // role 0, same as every pre-P9 call site
        inv.AddContainer(e, 1, role: 1);

        Assert.Equal(defaultId, inv.GetInventoryId(e));    // GetInventoryId(e) still means role 0
        Assert.Equal(defaultId, inv.GetInventoryId(e, role: 0));
    }
```

- [ ] **Step 11: Run the new inventory tests, then the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoriesTests"`
Expected: PASS, all `InventoriesTests` including the 4 new ones.

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS, 317/317 (307 baseline + 6 loader + 4 inventory).

Run: `dotnet build -c Release`
Expected: 0 warnings, 0 errors.

- [ ] **Step 12: Commit**

```bash
git add sim/Faketorio.Sim/Prototypes/CraftingMachinePrototype.cs \
        sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs \
        sim/Faketorio.Sim/Items/Inventories.cs \
        data/base/machines.json \
        sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs \
        sim/Faketorio.Sim.Tests/InventoriesTests.cs
git commit -m "$(cat <<'EOF'
feat(proto): CraftingMachinePrototype (furnace/assembling-machine) + Inventories roles

Adds the furnace/assembling-machine prototype pair and a fourth
PrototypeLoader resolve/validate pass, plus data/base/machines.json.
Inventories.AddContainer/GetInventoryId/RemoveContainer gain an optional
role parameter so one entity can own independent input/output inventories
(P9's machines) without a new storage type — backward compatible, every
existing call site defaults to role 0 unchanged.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01BXC5RZ5wsWirQpprXn9KZV
EOF
)"
```

---

## Task 2: `Machines` runtime-state subsystem

**Files:**
- Create: `sim/Faketorio.Sim/Machines/Machines.cs`
- Test: `sim/Faketorio.Sim.Tests/MachinesTests.cs`

**Interfaces:**
- Consumes: nothing (pure, no dependency on Task 1's or any other task's types — only `Faketorio.Sim.Entities.EntityId` and `Faketorio.Sim.State.IStateWriter`, both pre-existing).
- Produces (for Task 3): `Machines` class (flat `namespace Faketorio.Sim;` — see Global Constraints) with `RegisterMachine(EntityId)`, `UnregisterMachine(EntityId)`, `GetCurrentRecipe(EntityId) -> int` (-1 = none), `GetProgress(EntityId) -> long`, `IsCompleted(EntityId) -> bool`, `SetRecipe(EntityId, int recipeProtoId)`, `AddProgress(EntityId, long delta)`, `MarkCompleted(EntityId)`, `RestartCycle(EntityId, bool clearRecipe)`, `WriteState(IStateWriter)`.

Does not touch `Simulation.cs`, `Inventories.cs`, or any prototype file — fully independent of Task 1.

- [ ] **Step 1: Create `Machines.cs`**

**Important — read the Global Constraints namespace note before writing this file.** The class must be declared in flat `namespace Faketorio.Sim;`, not `Faketorio.Sim.Machines`, even though the file lives in the `Machines/` folder.

```csharp
using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim;   // 扁平——不是 Faketorio.Sim.Machines,理由见 Global Constraints

// 加工状态机的运行时状态容器。刻意不知道 Simulation/Prototypes/Inventories——
// 只收裸 EntityId/int/long,配方匹配、库存读写、电网查询都在 Simulation.MachinesTick
// 里做(同 ElectricGrid 的隔离原则)。
public sealed class Machines
{
    private readonly Dictionary<EntityId, MachineRuntimeState> _states = new();

    public void RegisterMachine(EntityId id) => _states[id] = new MachineRuntimeState(-1, 0, false);
    public void UnregisterMachine(EntityId id) => _states.Remove(id);

    public int GetCurrentRecipe(EntityId id) => _states.TryGetValue(id, out var s) ? s.CurrentRecipeProtoId : -1;
    public long GetProgress(EntityId id) => _states.TryGetValue(id, out var s) ? s.Progress : 0;
    public bool IsCompleted(EntityId id) => _states.TryGetValue(id, out var s) && s.Completed;

    public void SetRecipe(EntityId id, int recipeProtoId) => _states[id] = new MachineRuntimeState(recipeProtoId, 0, false);

    public void AddProgress(EntityId id, long delta)
    {
        var s = _states[id];
        _states[id] = s with { Progress = s.Progress + delta };
    }

    public void MarkCompleted(EntityId id)
    {
        var s = _states[id];
        _states[id] = s with { Completed = true };
    }

    // clearRecipe: 熔炉传 true(配方每轮从输入内容现推,轮次间不粘滞);装配机传 false
    // (配方是玩家 SetRecipe 配置的持久选择,一直循环同一配方,直到玩家再发一次
    // SetRecipe——同真实 Factorio 装配机行为)。
    public void RestartCycle(EntityId id, bool clearRecipe)
    {
        int recipe = clearRecipe ? -1 : GetCurrentRecipe(id);
        _states[id] = new MachineRuntimeState(recipe, 0, false);
    }

    // 按 EntityId.Index 排序后写:index/代数/配方 id/进度/是否已完成。
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
            writer.Write(s.CurrentRecipeProtoId);
            writer.Write(s.Progress);
            writer.Write(s.Completed ? (byte)1 : (byte)0);
        }
    }
}

internal readonly record struct MachineRuntimeState(int CurrentRecipeProtoId, long Progress, bool Completed);
```

- [ ] **Step 2: Write `MachinesTests.cs`**

```csharp
using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class MachinesTests
{
    [Fact]
    public void RegisterMachine_StartsWithNoRecipeZeroProgressNotCompleted()
    {
        var m = new Machines();
        var id = new EntityId(1, 1);
        m.RegisterMachine(id);

        Assert.Equal(-1, m.GetCurrentRecipe(id));
        Assert.Equal(0, m.GetProgress(id));
        Assert.False(m.IsCompleted(id));
    }

    [Fact]
    public void UnregisteredEntity_FallsBackToDefaults()
    {
        var m = new Machines();
        var id = new EntityId(2, 1);

        Assert.Equal(-1, m.GetCurrentRecipe(id));
        Assert.Equal(0, m.GetProgress(id));
        Assert.False(m.IsCompleted(id));
    }

    [Fact]
    public void SetRecipe_ResetsProgressAndCompleted()
    {
        var m = new Machines();
        var id = new EntityId(3, 1);
        m.RegisterMachine(id);
        m.SetRecipe(id, 1);
        m.AddProgress(id, 100);
        m.MarkCompleted(id);

        m.SetRecipe(id, 42);

        Assert.Equal(42, m.GetCurrentRecipe(id));
        Assert.Equal(0, m.GetProgress(id));
        Assert.False(m.IsCompleted(id));
    }

    [Fact]
    public void AddProgress_Accumulates()
    {
        var m = new Machines();
        var id = new EntityId(4, 1);
        m.RegisterMachine(id);
        m.SetRecipe(id, 7);

        m.AddProgress(id, 30);
        m.AddProgress(id, 12);

        Assert.Equal(42, m.GetProgress(id));
    }

    [Fact]
    public void MarkCompleted_KeepsRecipeAndProgress()
    {
        var m = new Machines();
        var id = new EntityId(5, 1);
        m.RegisterMachine(id);
        m.SetRecipe(id, 9);
        m.AddProgress(id, 1000);

        m.MarkCompleted(id);

        Assert.True(m.IsCompleted(id));
        Assert.Equal(9, m.GetCurrentRecipe(id));
        Assert.Equal(1000, m.GetProgress(id));
    }

    [Fact]
    public void RestartCycle_ClearRecipeTrue_ClearsEverything()
    {
        var m = new Machines();
        var id = new EntityId(6, 1);
        m.RegisterMachine(id);
        m.SetRecipe(id, 3);
        m.AddProgress(id, 500);
        m.MarkCompleted(id);

        m.RestartCycle(id, clearRecipe: true);

        Assert.Equal(-1, m.GetCurrentRecipe(id));
        Assert.Equal(0, m.GetProgress(id));
        Assert.False(m.IsCompleted(id));
    }

    [Fact]
    public void RestartCycle_ClearRecipeFalse_KeepsRecipeResetsProgress()
    {
        var m = new Machines();
        var id = new EntityId(7, 1);
        m.RegisterMachine(id);
        m.SetRecipe(id, 5);
        m.AddProgress(id, 500);
        m.MarkCompleted(id);

        m.RestartCycle(id, clearRecipe: false);

        Assert.Equal(5, m.GetCurrentRecipe(id));
        Assert.Equal(0, m.GetProgress(id));
        Assert.False(m.IsCompleted(id));
    }

    [Fact]
    public void UnregisterMachine_RemovesState()
    {
        var m = new Machines();
        var id = new EntityId(8, 1);
        m.RegisterMachine(id);
        m.SetRecipe(id, 1);

        m.UnregisterMachine(id);

        Assert.Equal(-1, m.GetCurrentRecipe(id));
        Assert.Equal(0, m.GetProgress(id));
        Assert.False(m.IsCompleted(id));
    }

    [Fact]
    public void WriteState_SortsByEntityIndex_RegistrationOrderDoesNotMatter()
    {
        var idHigh = new EntityId(9, 1);
        var idLow = new EntityId(2, 1);

        var m1 = new Machines();
        m1.RegisterMachine(idHigh);
        m1.SetRecipe(idHigh, 11);
        m1.RegisterMachine(idLow);
        m1.SetRecipe(idLow, 22);
        var w1 = new Fnv1aHashWriter();
        m1.WriteState(w1);

        var m2 = new Machines();
        m2.RegisterMachine(idLow);
        m2.SetRecipe(idLow, 22);
        m2.RegisterMachine(idHigh);
        m2.SetRecipe(idHigh, 11);
        var w2 = new Fnv1aHashWriter();
        m2.WriteState(w2);

        Assert.Equal(w1.Hash, w2.Hash);
    }
}
```

- [ ] **Step 3: Run the new tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~MachinesTests"`
Expected: PASS, all 9 `MachinesTests`. If the build fails with an error about `Machines` being a namespace rather than a type somewhere, re-check Step 1's `namespace Faketorio.Sim;` line — that is the exact bug this step guards against.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS, 326/326 (317 from Task 1 + 9 new).

Run: `dotnet build -c Release`
Expected: 0 warnings, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add sim/Faketorio.Sim/Machines/Machines.cs sim/Faketorio.Sim.Tests/MachinesTests.cs
git commit -m "$(cat <<'EOF'
feat(machines): Machines runtime-state container for the processing state machine

Pure EntityId-keyed state (current recipe / progress / completed flag),
isolated from Simulation/Prototypes/Inventories same as ElectricGrid.
Declared in flat namespace Faketorio.Sim (not Faketorio.Sim.Machines) to
avoid the same self-shadowing bug P5 hit with Player/namespace
Faketorio.Sim.Player.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01BXC5RZ5wsWirQpprXn9KZV
EOF
)"
```

---

## Task 3: `SetRecipe` command + `Simulation` wiring

**Files:**
- Modify: `sim/Faketorio.Sim/Commands/Command.cs`
- Modify: `sim/Faketorio.Sim/Simulation.cs`
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`
- Test: `sim/Faketorio.Sim.Tests/DeterminismTests.cs`

**Interfaces:**
- Consumes: Task 1's `CraftingMachinePrototype`/`FurnacePrototype`/`AssemblingMachinePrototype` and `Inventories`' `role` parameter; Task 2's `Machines` class and its exact method names (`RegisterMachine`, `UnregisterMachine`, `GetCurrentRecipe`, `GetProgress`, `IsCompleted`, `SetRecipe`, `AddProgress`, `MarkCompleted`, `RestartCycle`, `WriteState`).
- Produces: `Simulation.Machines` property; `CommandType.SetRecipe`; a fully wired processing tick.

This is the last task — depends on both Task 1 and Task 2 being merged first.

- [ ] **Step 1: Add `SetRecipe` to `Command.cs`**

In `sim/Faketorio.Sim/Commands/Command.cs`, change:

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
```

to:

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
    SetRecipe = 10,
}
```

No new fields on the `Command` struct — `SetRecipe` reuses `ProtoId` (the recipe's prototype id) and `X`/`Y` (the target machine's tile).

- [ ] **Step 2: Add the `Machines` property to `Simulation.cs`**

In `sim/Faketorio.Sim/Simulation.cs`, change:

```csharp
    public Player Player { get; }
    public ElectricGrid ElectricGrid { get; } = new();
```

to:

```csharp
    public Player Player { get; }
    public ElectricGrid ElectricGrid { get; } = new();
    public Machines Machines { get; } = new();
```

(No new `using` needed — `Machines` is in the flat `Faketorio.Sim` namespace per Task 2, same as `Player`.)

- [ ] **Step 3: Wire `PlaceEntity` to register machines and their input/output inventories**

In `Simulation.cs`'s `Apply` method, `CommandType.PlaceEntity` case, change:

```csharp
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
                return;
```

to:

```csharp
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
                return;
```

- [ ] **Step 4: Wire `DestroyEntityAt` to unregister machines**

In `Simulation.cs`, change `DestroyEntityAt`:

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

to:

```csharp
    private void DestroyEntityAt(EntityId id, EntityPrototype proto)
    {
        ref var data = ref Entities.Get(id);
        int bx = data.X, by = data.Y;
        bool isBelt = proto is TransportBeltPrototype;
        bool isContainer = proto is ContainerPrototype;
        bool isPole = proto is ElectricPolePrototype;
        bool isGenerator = proto is FuelGeneratorPrototype;
        bool isMachine = proto is CraftingMachinePrototype;
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
    }
```

- [ ] **Step 5: Route `TransferToEntity`/`TransferFromEntity` to the machine input/output roles**

In `Simulation.cs`'s `Apply` method, change the `CommandType.TransferToEntity` case from:

```csharp
                var eid = World.GetEntityAt(command.X, command.Y);
                var targetInvId = eid.IsValid ? Inventories.GetInventoryId(eid) : InventoryId.Invalid;
                if (!targetInvId.IsValid)
```

to:

```csharp
                var eid = World.GetEntityAt(command.X, command.Y);
                int targetRole = eid.IsValid && Prototypes.TryGetById(Entities.Get(eid).ProtoId, out var targetProto)
                    && targetProto is CraftingMachinePrototype ? 1 : 0;
                var targetInvId = eid.IsValid ? Inventories.GetInventoryId(eid, targetRole) : InventoryId.Invalid;
                if (!targetInvId.IsValid)
```

(everything else in that case body is unchanged). And change the `CommandType.TransferFromEntity` case from:

```csharp
                var eid2 = World.GetEntityAt(command.X, command.Y);
                var sourceInvId = eid2.IsValid ? Inventories.GetInventoryId(eid2) : InventoryId.Invalid;
                if (!sourceInvId.IsValid)
```

to:

```csharp
                var eid2 = World.GetEntityAt(command.X, command.Y);
                int sourceRole = eid2.IsValid && Prototypes.TryGetById(Entities.Get(eid2).ProtoId, out var sourceProto)
                    && sourceProto is CraftingMachinePrototype ? 2 : 0;
                var sourceInvId = eid2.IsValid ? Inventories.GetInventoryId(eid2, sourceRole) : InventoryId.Invalid;
                if (!sourceInvId.IsValid)
```

(everything else in that case body is unchanged).

- [ ] **Step 6: Add the `SetRecipe` case to `Apply`**

In `Simulation.cs`'s `Apply` method, add a new case right before `case CommandType.CraftEnqueue:`:

```csharp
            case CommandType.SetRecipe:
            {
                var mid = World.GetEntityAt(command.X, command.Y);
                if (!mid.IsValid || !Entities.IsAlive(mid)
                    || !Prototypes.TryGetById(Entities.Get(mid).ProtoId, out var mp) || mp is not AssemblingMachinePrototype amp
                    || !Prototypes.TryGetById(command.ProtoId, out var rp2) || rp2 is not RecipePrototype recipe2
                    || recipe2.Category != amp.Category)
                {
                    RejectedCommandCount++;
                    return;
                }
                Machines.SetRecipe(mid, command.ProtoId);
                return;
            }
```

- [ ] **Step 7: Add `MachineTickPreSettle`/`MachineTickPostSettle` and their pool-scan wrappers**

Add these four new private methods to `Simulation.cs` (place them right after `ElectricGeneratorsBurnFuel`, at the end of the class before the final closing brace):

```csharp
    // 加工:配方选定 + 电力需求登记(Settle() 之前)。两趟扫描的第一趟——全部机器
    // 先登记完需求,ElectricGrid.Settle() 才能看到本 tick 完整的需求总量。
    private void MachinesTickPreSettle()
    {
        for (int i = 0; i < Entities.Capacity; i++)
        {
            if (!Entities.IsAliveAtIndex(i)) continue;
            ref var data = ref Entities.GetAtIndex(i);
            if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not CraftingMachinePrototype proto) continue;
            var id = new EntityId(i, Entities.GenerationAtIndex(i));
            MachineTickPreSettle(id, proto, data.X, data.Y);
        }
    }

    private void MachineTickPreSettle(EntityId id, CraftingMachinePrototype proto, int x, int y)
    {
        // 第 1 步:flush 已完成的产出
        if (Machines.IsCompleted(id))
        {
            var doneRecipe = (RecipePrototype)Prototypes.GetById(Machines.GetCurrentRecipe(id));
            var outputInv = Inventories.Get(Inventories.GetInventoryId(id, 2));
            bool fits = true;
            foreach (var res in doneRecipe.ResolvedResults)
            {
                int stack = ((ItemPrototype)Prototypes.GetById(res.ItemProtoId)).StackSize;
                if (!outputInv.CanInsert(res.ItemProtoId, res.Amount, stack)) { fits = false; break; }
            }
            if (fits)
            {
                foreach (var res in doneRecipe.ResolvedResults)
                {
                    int stack = ((ItemPrototype)Prototypes.GetById(res.ItemProtoId)).StackSize;
                    outputInv.Insert(res.ItemProtoId, res.Amount, stack);
                }
                Machines.RestartCycle(id, clearRecipe: proto is FurnacePrototype);
                // 本 tick 继续走到第 2 步(不用等下一 tick),fits==true 时不 return。
            }
            else
            {
                // 输出堵塞:跳过第 2 步(不重新匹配/不能开始新一轮),但仍登记待机能耗。
                ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
                return;
            }
        }

        // 第 2 步:配方选定(仅当当前没有配方)
        if (Machines.GetCurrentRecipe(id) == -1 && proto is FurnacePrototype)
        {
            var inputInv = Inventories.Get(Inventories.GetInventoryId(id, 1));
            for (int rid = 0; rid < Prototypes.Count; rid++)
            {
                if (Prototypes.GetById(rid) is not RecipePrototype recipe || recipe.Category != proto.Category) continue;
                bool satisfied = true;
                foreach (var ing in recipe.ResolvedIngredients)
                    if (inputInv.CountOf(ing.ItemProtoId) < ing.Amount) { satisfied = false; break; }
                if (satisfied) { Machines.SetRecipe(id, recipe.Id); break; }
            }
        }
        // 装配机:不自动匹配,只用玩家此前 SetRecipe 设置的结果(可能仍是 -1)。

        // 第 3 步:电力需求登记(无条件——恒定待机能耗)
        ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
    }

    // 加工:进度推进 + 完成校验(Settle() 之后,可读 satisfaction)。两趟扫描的第二趟。
    private void MachinesTickPostSettle()
    {
        for (int i = 0; i < Entities.Capacity; i++)
        {
            if (!Entities.IsAliveAtIndex(i)) continue;
            ref var data = ref Entities.GetAtIndex(i);
            if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not CraftingMachinePrototype proto) continue;
            var id = new EntityId(i, Entities.GenerationAtIndex(i));
            MachineTickPostSettle(id, proto);
        }
    }

    private void MachineTickPostSettle(EntityId id, CraftingMachinePrototype proto)
    {
        int recipeId = Machines.GetCurrentRecipe(id);
        if (recipeId == -1 || Machines.IsCompleted(id)) return;   // 空转,或本 tick 刚 flush 失败仍在等

        var recipe = (RecipePrototype)Prototypes.GetById(recipeId);
        var inputInv = Inventories.Get(Inventories.GetInventoryId(id, 1));

        bool satisfied = true;
        foreach (var ing in recipe.ResolvedIngredients)
            if (inputInv.CountOf(ing.ItemProtoId) < ing.Amount) { satisfied = false; break; }
        if (!satisfied) return;   // 缺料:本 tick 冻结进度,不清零、不重置配方,等原料备齐

        long threshold = (long)recipe.EnergyRequiredTicks << 16;
        var satisfaction = ElectricGrid.GetSatisfaction(id);
        long delta = proto.CraftingSpeed.Mul(satisfaction.Raw);
        if (Machines.GetProgress(id) < threshold) Machines.AddProgress(id, delta);
        if (Machines.GetProgress(id) < threshold) return;   // 阻塞在阈值,不再累加

        // 完成前重校验(单线程 tick 内必然通过,是给未来机械臂/传送带的防御)+ 消耗原料
        bool stillSatisfied = true;
        foreach (var ing in recipe.ResolvedIngredients)
            if (inputInv.CountOf(ing.ItemProtoId) < ing.Amount) { stillSatisfied = false; break; }

        if (stillSatisfied)
        {
            foreach (var ing in recipe.ResolvedIngredients)
                inputInv.Remove(ing.ItemProtoId, ing.Amount);
            Machines.MarkCompleted(id);
        }
        else
        {
            Machines.RestartCycle(id, clearRecipe: proto is FurnacePrototype);
        }
    }
```

- [ ] **Step 8: Splice the processing tick into `Step()`**

In `Simulation.cs`'s `Step()`, change:

```csharp
        // 电网:① 发电机登记供给 ② 结算 ③ 烧油结算
        ElectricGeneratorsRegisterSupply();
        ElectricGrid.Settle();
        ElectricGeneratorsBurnFuel();
```

to:

```csharp
        // 电网 + 加工:① 发电机登记供给 ② 机器登记需求 ③ 结算 ④ 发电机烧油 ⑤ 机器推进+完成
        ElectricGeneratorsRegisterSupply();
        MachinesTickPreSettle();
        ElectricGrid.Settle();
        ElectricGeneratorsBurnFuel();
        MachinesTickPostSettle();
```

- [ ] **Step 9: Append `Machines.WriteState` to `WriteState()`**

In `Simulation.cs`'s `WriteState`, change:

```csharp
        Belts.WriteState(writer);
        Inventories.WriteState(writer);
        Resources.WriteState(writer);
        Player.WriteState(writer);
        ElectricGrid.WriteState(writer);
    }
```

to:

```csharp
        Belts.WriteState(writer);
        Inventories.WriteState(writer);
        Resources.WriteState(writer);
        Player.WriteState(writer);
        ElectricGrid.WriteState(writer);
        Machines.WriteState(writer);
    }
```

- [ ] **Step 10: Build to catch compile errors before writing tests**

Run: `dotnet build sim/Faketorio.Sim`
Expected: 0 errors. Fix any before proceeding — do not write tests against code that doesn't compile.

- [ ] **Step 11: Write `SimulationTests.cs` additions**

Append to `sim/Faketorio.Sim.Tests/SimulationTests.cs` (inside the `SimulationTests` class; it already has `NewSim()`, `PlaceChest`, `TransferTo`/`TransferFrom`-style helpers and a `Q16`/`ElectricGrid`/`UsagePriority` import from the P7 tests — reuse the existing `NewSim()`):

```csharp
    private static Command PlaceFurnace(Simulation sim, int x, int y) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<FurnacePrototype>("stone-furnace").Id,
        X = x, Y = y, Rotation = 0,
    };

    private static Command PlaceAssembler(Simulation sim, int x, int y) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<AssemblingMachinePrototype>("assembling-machine-1").Id,
        X = x, Y = y, Rotation = 0,
    };

    private static Command SetRecipe(int x, int y, int recipeProtoId) => new()
        { Type = CommandType.SetRecipe, X = x, Y = y, ProtoId = recipeProtoId };

    // Shared setup: pole(0,0) + generator(2,0) fueled with coal, then a
    // furnace or assembler at (0,2) — Chebyshev distance 2 from the pole,
    // matching small-electric-pole's supplyAreaDistanceTiles (2). Every test
    // below that expects a machine to actually make progress needs this —
    // ElectricGrid.GetSatisfaction defaults to Q16.Zero for any demand with
    // no reachable network AND for any demand on a network with zero
    // registered supply, so a machine with no pole+generator never advances
    // at all (this is the exact mistake an earlier draft of this plan made
    // and is why every progress-observing test below places power first).
    private static void PlacePoweredMachineInfra(Simulation sim)
    {
        sim.Submit(PlacePole(sim, 0, 0));
        sim.Submit(PlaceGenerator(sim, 2, 0));
        sim.Step();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        sim.Submit(TransferTo(2, 0, coal, 5));
        sim.Step();
    }

    [Fact]
    public void Furnace_AutoMatchesAndSmeltsIronOre()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(0, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));
        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        inputInv.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);

        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        var outputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 2));

        // iron-plate is 192 ticks (3.2s); the generator's 1500 J/tick output
        // exactly matches the furnace's 1500 J/tick demand (both "90kW" in
        // data/base), so with no competing consumer it runs at full
        // satisfaction — 192 ticks to complete + 1 more tick for the next
        // PreSettle to flush the output. 200 gives comfortable headroom.
        for (int t = 0; t < 200; t++) sim.Step();

        Assert.Equal(1, outputInv.CountOf(plateId));
        Assert.Equal(0, inputInv.CountOf(oreId));   // consumed
    }

    [Fact]
    public void AssemblingMachine_WithoutSetRecipe_NeverConsumesInput()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 0, 0));
        sim.Step();

        var asmId = sim.World.GetEntityAt(0, 0);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 1));
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        inputInv.Insert(plateId, 2, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);

        for (int t = 0; t < 100; t++) sim.Step();

        Assert.Equal(2, inputInv.CountOf(plateId));   // untouched — no recipe ever set
    }

    [Fact]
    public void AssemblingMachine_SetRecipe_LoopsSameRecipeAcrossBatches()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceAssembler(sim, 0, 2));
        sim.Step();

        var asmId = sim.World.GetEntityAt(0, 2);
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int gearId = sim.Prototypes.Get<ItemPrototype>("iron-gear-wheel").Id;
        int gearRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;

        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 1));
        var outputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 2));
        inputInv.Insert(plateId, 4, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);   // 2 batches worth

        sim.Submit(SetRecipe(0, 2, gearRecipeId));
        sim.Step();

        // iron-gear-wheel is 30 ticks (0.5s); the generator's 1500 J/tick
        // output covers the assembler's 1250 J/tick demand fully (satisfaction
        // == One), so each batch completes in 30 ticks + 1 flush tick; two
        // batches back-to-back (assembler recipe stays set — no second
        // SetRecipe needed) comfortably fit in 100 more ticks.
        for (int t = 0; t < 100; t++) sim.Step();

        Assert.Equal(2, outputInv.CountOf(gearId));
        Assert.Equal(0, inputInv.CountOf(plateId));
    }

    [Fact]
    public void SetRecipe_RejectsFurnaceTarget()
    {
        var sim = NewSim();
        sim.Submit(PlaceFurnace(sim, 0, 0));
        sim.Step();
        int recipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;

        sim.Submit(SetRecipe(0, 0, recipeId));
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount);
    }

    [Fact]
    public void SetRecipe_RejectsNonMachineTarget()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 0, 0));
        sim.Step();
        int recipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;

        sim.Submit(SetRecipe(0, 0, recipeId));
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount);
    }

    [Fact]
    public void SetRecipe_RejectsCategoryMismatch()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 0, 0));
        sim.Step();
        int smeltingRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-plate").Id;   // category "smelting"

        sim.Submit(SetRecipe(0, 0, smeltingRecipeId));
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount);
    }

    [Fact]
    public void Machine_UnderpoweredSatisfaction_TakesTwiceAsLong()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(0, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));
        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        inputInv.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);

        // A second PrimaryInput consumer demanding exactly the furnace's own
        // demand (both "90kW" = 1500 J/tick) doubles total demand (3000)
        // against the generator's fixed 1500 J/tick supply, giving BOTH
        // consumers satisfaction == 0.5 (Settle() broadcasts one ratio per
        // tier, not a per-registrant split — see ElectricGrid.SettleNetwork).
        // Progress advances at half the per-tick rate, so completion takes
        // ~2x as many ticks as the unthrottled 192 + 1 flush tick baseline.
        long furnaceDemand = sim.Prototypes.Get<FurnacePrototype>("stone-furnace").EnergyUsageJPerTick;
        var fakeConsumer = new EntityId(999, 1);
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        var outputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 2));

        int tick = 0;
        while (outputInv.CountOf(plateId) == 0 && tick < 500)
        {
            sim.ElectricGrid.RegisterDemand(fakeConsumer, 0, 0, UsagePriority.PrimaryInput, furnaceDemand);
            sim.Step();
            tick++;
        }

        // Comfortably brackets the ~385-tick expected value (2*192 + 1 flush
        // tick) while staying far above the 193-tick full-speed baseline, so
        // this still catches a "throttling doesn't work" regression without
        // depending on an exact off-by-one in the flush-timing arithmetic.
        Assert.InRange(tick, 300, 450);
    }

    [Fact]
    public void Machine_StandbyEnergyRegisteredEvenWhenIdle()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();   // furnace has no ore yet — idles, but must still register demand

        var fakeConsumer = new EntityId(999, 1);
        long furnaceDemand = sim.Prototypes.Get<FurnacePrototype>("stone-furnace").EnergyUsageJPerTick;

        // The generator's output (1500 J/tick) exactly equals furnaceDemand.
        // If the idle furnace registered 0 demand, this consumer alone would
        // fully consume the generator's output (satisfaction == One); since
        // the idle furnace's standby draw competes for the same 1500 J/tick,
        // this consumer is throttled to half instead.
        sim.ElectricGrid.RegisterDemand(fakeConsumer, 0, 0, UsagePriority.PrimaryInput, furnaceDemand);
        sim.Step();

        Assert.NotEqual(Q16.One, sim.ElectricGrid.GetSatisfaction(fakeConsumer));
    }

    [Fact]
    public void Machine_OutputBlocked_HoldsCompletedUntilSpaceFrees()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(0, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));
        var outputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 2));
        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;

        inputInv.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);
        outputInv.Insert(plateId, plateStack, plateStack);   // pre-fill the furnace's one output slot

        for (int t = 0; t < 200; t++) sim.Step();

        Assert.Equal(0, inputInv.CountOf(oreId));            // consumed at completion
        Assert.Equal(plateStack, outputInv.CountOf(plateId)); // still full — flush blocked

        outputInv.Remove(plateId, plateStack);   // free the output
        sim.Step();

        Assert.Equal(1, outputInv.CountOf(plateId));   // flushed on the next tick
    }

    [Fact]
    public void AssemblingMachine_MissingIngredients_FreezesProgressWithoutResetting()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceAssembler(sim, 0, 2));
        sim.Step();

        var asmId = sim.World.GetEntityAt(0, 2);
        int gearRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;
        sim.Submit(SetRecipe(0, 2, gearRecipeId));
        sim.Step();

        for (int t = 0; t < 50; t++) sim.Step();   // input inventory still empty the whole time

        Assert.Equal(0, sim.Machines.GetProgress(asmId));
        Assert.Equal(gearRecipeId, sim.Machines.GetCurrentRecipe(asmId));   // not reset

        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 1));
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        inputInv.Insert(plateId, 2, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);

        sim.Step();
        Assert.True(sim.Machines.GetProgress(asmId) > 0);   // now advancing from 0
    }
```

- [ ] **Step 12: Run the new simulation tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests"`
Expected: PASS, all `SimulationTests` including the 10 new ones. If `Machine_UnderpoweredSatisfaction_TakesTwiceAsLong` is flaky on the exact tick-count range, widen the `Assert.InRange` bounds rather than chasing an exact multiplier — the point of the test is "roughly doubled", not an exact tick count.

- [ ] **Step 13: Write the `DeterminismTests.cs` addition**

Append to `sim/Faketorio.Sim.Tests/DeterminismTests.cs` (inside the test class, following the exact structure of the existing `RunElectricScenario`/`ElectricScenario_SameSeedSameCommands_SameHashEveryTick` pair):

```csharp
    private static List<ulong> RunMachineScenario(long seed)
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"), seed);
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        int ore = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        int oreStack = sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize;
        sim.Player.Inventory.Insert(coal, 5, coalStack);
        sim.Player.Inventory.Insert(ore, 5, oreStack);

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
                    ProtoId = sim.Prototypes.Get<FurnacePrototype>("stone-furnace").Id, X = 0, Y = 2 });
            }
            if (t == 5)
                sim.Submit(new Command { Type = CommandType.TransferToEntity, X = 2, Y = 0, ProtoId = coal, Count = 5 });
            if (t == 6)
                sim.Submit(new Command { Type = CommandType.TransferToEntity, X = 0, Y = 2, ProtoId = ore, Count = 3 });
            sim.Step();
            hashes.Add(sim.ComputeStateHash());
        }
        return hashes;
    }

    [Fact]
    public void MachineScenario_SameSeedSameCommands_SameHashEveryTick()
        => Assert.Equal(RunMachineScenario(4242), RunMachineScenario(4242));
```

(The furnace at `(0, 2)` and the pole at `(0, 0)` are Chebyshev distance 2 apart, matching `small-electric-pole`'s `supplyAreaDistanceTiles: 2` from `data/base/electric.json` — same coverage-radius reasoning the existing `RunElectricScenario` already relies on for the generator at `(2, 0)`.)

- [ ] **Step 14: Run the new determinism test, then the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~DeterminismTests"`
Expected: PASS, all `DeterminismTests` including the new one.

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS, 336/336 (326 from Task 2 + 10 SimulationTests + 1 DeterminismTests — 11 new here).

Run: `dotnet build -c Release`
Expected: 0 warnings, 0 errors.

- [ ] **Step 15: Commit**

```bash
git add sim/Faketorio.Sim/Commands/Command.cs \
        sim/Faketorio.Sim/Simulation.cs \
        sim/Faketorio.Sim.Tests/SimulationTests.cs \
        sim/Faketorio.Sim.Tests/DeterminismTests.cs
git commit -m "$(cat <<'EOF'
feat(machines): wire the processing state machine into Simulation

SetRecipe command (assembling machines only — furnaces auto-match).
Two-pass MachinesTickPreSettle/PostSettle bracket ElectricGrid.Settle()
in Step(), mirroring the fuel-generator register/settle/burn ordering.
Progress advance is gated on live ingredient availability every tick
(freezes rather than resets on shortage) so a configured-but-unfed
assembling machine idles instead of wasting full cycles. Furnace recipes
are transient (re-derived per cycle); assembling-machine recipes persist
across cycles until the next SetRecipe.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01BXC5RZ5wsWirQpprXn9KZV
EOF
)"
```

---

## Self-Review Notes

- **Spec coverage:** §3 prototypes → Task 1 Step 1-2. §4 `Inventories` roles → Task 1 Step 8. §5 `Machines` + namespace fix → Task 2. §6 tick algorithm (including the progress-gating correction) → Task 3 Step 7. §7 `SetRecipe` → Task 3 Step 1, 6. §8 data → Task 1 Step 4 (no recipe changes, per the corrected spec). §9 determinism → covered throughout (index-order scans, sorted `WriteState`, `Q16`-only math). §10 tests → every bullet has a corresponding test in Task 1/2/3.
- **Placeholder scan:** no TBD/TODO; every step has literal code, not a description of code.
- **Type consistency:** `Machines` method names used in Task 3 (`RegisterMachine`, `UnregisterMachine`, `GetCurrentRecipe`, `GetProgress`, `IsCompleted`, `SetRecipe`, `AddProgress`, `MarkCompleted`, `RestartCycle`, `WriteState`) match Task 2's declarations exactly. `Inventories`' `role` parameter name and default (`role = 0`) match between Task 1's declaration and Task 3's call sites. `CraftingMachinePrototype`/`FurnacePrototype`/`AssemblingMachinePrototype` field names (`Category`, `InputSlots`, `OutputSlots`, `CraftingSpeed`, `EnergyUsageJPerTick`) match between Task 1's declaration and Task 3's usage.
