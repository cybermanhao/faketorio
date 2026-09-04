# M1 Plan 5 — 玩家 + 手挖 + 手搓 + 开局物资包 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **状态: ✅ 已合并 main · 已验证** — 主线提交 `2ad2b1f`..`a5f569d`(见 `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md` 进度快照)。下方 `- [ ]` 复选框为执行期工件,不代表当前状态。

**Goal:** Add a player to the simulation — sub-tile position, walk-intent movement with collision, hand-mining (entities and resources), a bounded hand-craft queue, and a data-driven starting inventory — closing the first playable loop over P4 inventory + P6 resources + recipes + commands.

**Architecture:** `Player` is a standalone `Simulation` object (not in `EntityPool` — it doesn't occupy tiles). It holds a sub-tile `(X, Y)` position, a walk-intent state, an int64 mining-progress accumulator, a FIFO `List<CraftJob>` craft queue, and its own `Inventory` (P4). New commands (`MovePlayer`/`StopPlayer`/`MineStart`/`MineStop`/`CraftEnqueue`) set intent; a new "player tick" segment in `Simulation.Step` — between the command-apply loop and the belt advance — resolves walking (with whole-step collision against `WorldGrid`), mining progress + completion (entity removal via a shared `DestroyEntityAt` helper, or `ResourceGrid.Extract`), and craft-queue head progress + completion (blocks when the inventory is full). All math is integer/int64; `Player.WriteState` is appended to `Simulation.WriteState` after `Resources.WriteState`.

**Tech Stack:** C# / .NET 8, xUnit. No Godot dependency in `Faketorio.Sim`. Deterministic fixed-tick simulation — canonical serialization through `IStateWriter` / `Fnv1aHashWriter` (FNV-1a).

**Spec:** [`docs/superpowers/specs/2026-09-04-m1-plan5-player-hand-actions-design.md`](../specs/2026-09-04-m1-plan5-player-hand-actions-design.md)

## Global Constraints

- **Determinism 铁律:** no `float`/`double` in simulation state or in any tick logic — positions, speeds, reach, and all progress accumulators are `int`/`long`; distance uses `ValueNoise.Isqrt` (P6, integer). No `System.Random`. No `Dictionary` enumeration in any `WriteState`. Every new piece of state must be written into a `WriteState` reachable from `Simulation.WriteState`.
- **Namespaces:** `Player`, `CraftJob` in `namespace Faketorio.Sim.Player;` under `sim/Faketorio.Sim/Player/`. `PlayerPrototype` in `namespace Faketorio.Sim.Prototypes;`. Tests in `namespace Faketorio.Sim.Tests;`.
- **`Player` position:** sub-tile integers, 256 sub-tiles per tile (`Units.SubTilesPerTile == 256`). Spawn at `(0, 0)`. Tile of a sub-tile coord is `coord >> 8` (arithmetic shift — floors for negatives, matching `WorldGrid`).
- **Walk direction:** `byte` 0..7 = N / NE / E / SE / S / SW / W / NW, where N is `-Y`. The 8-vector table scales each non-zero component by the full `WalkSpeedSubTilesPerTick` (diagonals move √2× faster — an accepted M1 approximation).
- **Collision:** whole-step reject. Compute the destination `(nx, ny)`; if `World.GetEntityAt(nx >> 8, ny >> 8)` is a valid `EntityId`, the player does not move this tick (no wall-sliding). The player is a point (no footprint).
- **Mining reach:** Euclidean, `ValueNoise.Isqrt((long)ddx*ddx + (long)ddy*ddy) > PlayerPrototype.ReachSubTiles` → progress does not advance this tick (retained). `ddx = player.X - (tx*256 + 128)`, `ddy = player.Y - (ty*256 + 128)` (target tile centre).
- **Mining completion:** on `MineProgress >= threshold`, insert one `MinableResult` item; if `Inventory.Insert` returns 0 (full), `MineProgress` stays at `threshold` and nothing is produced/cleared — retried next tick. Entity target: threshold `= EntityPrototype.MiningTimeTicks`, on success call `DestroyEntityAt`. Resource target: threshold `= ResourcePrototype.MiningTimeTicks`, on success call `Resources.Extract(tx, ty, 1)` then `MineProgress = 0` and continue.
- **Craft enqueue:** reject (`RejectedCommandCount++`) if the recipe id is out of range / not a `RecipePrototype` / `count < 1` / `recipe.Category != "crafting"` / `!recipe.Enabled` / queue is at `CraftQueueCap` / the inventory lacks `amount * count` of any ingredient. On a valid enqueue, deduct all ingredients (`amount * count` each) immediately and append `CraftJob(recipeId, count, 0)`.
- **Craft completion:** head job `Progress += 1` per tick; at `Progress >= recipe.EnergyRequiredTicks`, if every result fits (`Inventory.CanInsert`), insert all results, `job.Count -= 1`, then drop the head at `Count == 0` else reset `job.Progress = 0`; if a result does not fit, the job blocks (`Progress` stays at threshold, queue not advanced). M1 crafting recipes are single-result — multi-result pre-check is a known approximation.
- **`player` prototype:** exactly one required. `PrototypeLoader` throws `InvalidDataException` if it sees more than one; `Simulation`'s constructor throws `InvalidOperationException` if the registry has none.
- **Command struct is unchanged** — reuse existing fields: `MovePlayer` uses `Rotation` (dir 0..7); `MineStart` uses `X`, `Y`; `CraftEnqueue` uses `ProtoId` (recipe id) and `X` (count). `StopPlayer` / `MineStop` use no fields.
- **`Player.WriteState` order:** `X` (int), `Y` (int), `WalkDir` (byte), `Walking` (byte 0/1), `Mining` (byte 0/1), `MineTargetX` (int), `MineTargetY` (int), `MineProgress` (long), `CraftQueue.Count` (int), then per job `RecipeProtoId` (int) / `Count` (int) / `Progress` (long), then `Inventory.WriteState(writer)`.
- **`Simulation.WriteState`:** `Player.WriteState(writer)` is the last line, after `Resources.WriteState(writer)`.
- **Player tick position in `Step`:** immediately after the `for (…) Apply(in commands[i]);` loop and before the belt advance loop. Order within it: (1) walk, (2) mining, (3) craft.
- **Commit trailer:** every commit message ends with:
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
  ```
- **Test command:** `dotnet test sim/Faketorio.Sim.Tests` from repo root. Single: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~<Class>.<Method>"`.
- **Baseline:** `dotnet test` is green at **235 passing** before Task 1. `data/**` is copied to the test output dir, so new files under `data/base/` are picked up automatically. Adding `data/base/player.json` makes every existing `Simulation` test construct a `Player` (they all load `data/base`), and `Simulation.WriteState` gains a trailing idle-`Player` block — existing determinism tests are relative comparisons and are unaffected, exactly as with the P4/P6 additions.

## File Structure

| File | Responsibility |
|---|---|
| `sim/Faketorio.Sim/Prototypes/PlayerPrototype.cs` (create) | `PlayerPrototype : PrototypeBase` — `InventorySize`, `ReachSubTiles`, `WalkSpeedSubTilesPerTick`, `CraftQueueCap`, `StartingInventory`. |
| `sim/Faketorio.Sim/Prototypes/RecipePrototype.cs` (modify) | Add `readonly record struct ResolvedAmount(int ItemProtoId, int Amount)` and `ResolvedIngredients` / `ResolvedResults` (`{ get; internal set; }`). |
| `sim/Faketorio.Sim/Prototypes/ResourcePrototype.cs` (modify) | Add `int MiningTimeTicks { get; init; } = 60` to `ResourcePrototype` (spec §2 misnamed the file as `EntityPrototype.cs` — the class lives here). |
| `sim/Faketorio.Sim/Items/Inventory.cs` (modify) | Add `public bool CanInsert(int itemProtoId, int count, int stackSize)` — read-only two-pass simulation. |
| `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` (modify) | `Parse` gains a `"player"` arm; the `"resource"` arm reads `miningTimeSeconds`; a new `ResolveAndValidateRecipesAndPlayer(registry)` runs after `ResolveAndValidateMapGen`. |
| `sim/Faketorio.Sim/Commands/Command.cs` (modify) | `CommandType` gains `MovePlayer = 3, StopPlayer = 4, MineStart = 5, MineStop = 6, CraftEnqueue = 7`. |
| `sim/Faketorio.Sim/Player/CraftJob.cs` (create) | `record struct CraftJob(int RecipeProtoId, int Count, long Progress)`. |
| `sim/Faketorio.Sim/Player/Player.cs` (create) | `sealed class Player` — position, walk/mine/craft state, `Inventory`, granular `internal` mutators, `WriteState`, `static WalkDelta`. |
| `sim/Faketorio.Sim/Simulation.cs` (modify) | `Player` property + ctor construction + `InvalidOperationException` guard; `DestroyEntityAt` helper extracted from `RemoveEntity`; player-tick segment in `Step`; 5 new `Apply` cases; `Player.WriteState` in `WriteState`; starter-kit fill (Task 5). |
| `data/base/player.json` (create) | one `player` prototype + starting inventory. |
| `data/base/recipes.json` (modify) | add `iron-gear-wheel` and `wooden-chest` `"crafting"` recipes. |
| `data/base/items.json` (modify) | add `iron-gear-wheel`. |
| `data/base/map-gen.json` (modify) | append a near-origin `coal` starter patch at `(1, -1)`. |
| `sim/Faketorio.Sim.Tests/PlayerTests.cs` (create) | `Player` class unit tests (walk vectors, `WriteState` sensitivity). |
| `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` (modify) | recipe resolution + `player` load + validation negatives; bump the P6 starter-patch count 4→5. |
| `sim/Faketorio.Sim.Tests/InventoryTests.cs` (modify) | `CanInsert` cases. |
| `sim/Faketorio.Sim.Tests/SimulationTests.cs` (modify) | movement, collision, HandMine (entity + resource + reach), HandCraft, full loop. |
| `sim/Faketorio.Sim.Tests/DeterminismTests.cs` (modify) | `RunPlayerScenario` two-run equality. |

---

## Task 1: Prototypes + recipe resolution + `Inventory.CanInsert` + data + loader

**Files:**
- Create: `sim/Faketorio.Sim/Prototypes/PlayerPrototype.cs`, `data/base/player.json`
- Modify: `sim/Faketorio.Sim/Prototypes/RecipePrototype.cs`, `sim/Faketorio.Sim/Prototypes/ResourcePrototype.cs`, `sim/Faketorio.Sim/Items/Inventory.cs`, `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`, `data/base/recipes.json`, `data/base/items.json`, `data/base/map-gen.json`
- Test: `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs`, `sim/Faketorio.Sim.Tests/InventoryTests.cs`

**Interfaces:**
- Consumes: existing `PrototypeBase` (`Name` required, `Id { get; internal set; }`), `PrototypeRegistry` (`Get<T>(name)`, `TryGet<T>(name, out T)`, `GetById(int)`, `Count`), `ItemPrototype` (`StackSize`), `ItemAmount` (`readonly struct { string Name; int Amount; }`), `Units.SecondsToTicks(double)`.
- Produces (later tasks rely on these):
  - `public sealed class PlayerPrototype : PrototypeBase { public int InventorySize { get; init; } = 60; public int ReachSubTiles { get; init; } = 1536; public int WalkSpeedSubTilesPerTick { get; init; } = 38; public int CraftQueueCap { get; init; } = 32; public IReadOnlyList<ItemAmount> StartingInventory { get; init; } = Array.Empty<ItemAmount>(); }`
  - `public readonly record struct ResolvedAmount(int ItemProtoId, int Amount);` (in `RecipePrototype.cs`)
  - `RecipePrototype.ResolvedIngredients` / `RecipePrototype.ResolvedResults` — `IReadOnlyList<ResolvedAmount> { get; internal set; }`, fully populated after `PrototypeLoader.LoadFromDirectory` returns.
  - `ResourcePrototype.MiningTimeTicks` — `int { get; init; } = 60`.
  - `Inventory.CanInsert(int itemProtoId, int count, int stackSize) → bool`.

- [ ] **Step 1: Create `PlayerPrototype.cs`**

```csharp
namespace Faketorio.Sim.Prototypes;

// 单例,约定 name = "player"。PrototypeLoader 校验字段;Simulation 构造期
// 要求 registry 恰好有一条(无 -> InvalidOperationException)。
public sealed class PlayerPrototype : PrototypeBase
{
    public int InventorySize            { get; init; } = 60;
    public int ReachSubTiles            { get; init; } = 1536;  // 6 tile × 256
    public int WalkSpeedSubTilesPerTick { get; init; } = 38;    // ≈0.15 tile/tick
    public int CraftQueueCap            { get; init; } = 32;
    public IReadOnlyList<ItemAmount> StartingInventory { get; init; } = Array.Empty<ItemAmount>();
}
```

- [ ] **Step 2: Add `ResolvedAmount` + resolved lists to `RecipePrototype.cs`**

In `sim/Faketorio.Sim/Prototypes/RecipePrototype.cs`, add the record struct (after `ItemAmount`) and two properties to `RecipePrototype`:

```csharp
public readonly record struct ResolvedAmount(int ItemProtoId, int Amount);
```

```csharp
    // PrototypeLoader 的解析 pass 后填好(Name -> ItemPrototype.Id)。构造后为空。
    public IReadOnlyList<ResolvedAmount> ResolvedIngredients { get; internal set; } = Array.Empty<ResolvedAmount>();
    public IReadOnlyList<ResolvedAmount> ResolvedResults     { get; internal set; } = Array.Empty<ResolvedAmount>();
```

- [ ] **Step 3: Add `MiningTimeTicks` to `ResourcePrototype`**

In `sim/Faketorio.Sim/Prototypes/ResourcePrototype.cs`, add one property to `ResourcePrototype`:

```csharp
    public int MiningTimeTicks { get; init; } = 60;   // 矿脉每单位挖掘 tick 数
```

- [ ] **Step 4: Write the failing `Inventory.CanInsert` tests**

Append to `sim/Faketorio.Sim.Tests/InventoryTests.cs` (inside the class; `Iron`/`Copper`/`Stack` consts and the `Hash` helper already exist):

```csharp
    [Fact]
    public void CanInsert_EmptyInventory_FitsUpToCapacity()
    {
        var inv = new Inventory(2);
        Assert.True(inv.CanInsert(Iron, 100, Stack));    // 2 slots × 50
        Assert.False(inv.CanInsert(Iron, 101, Stack));
    }

    [Fact]
    public void CanInsert_CountsPartialSameTypeSlots()
    {
        var inv = new Inventory(2);
        inv.Insert(Iron, 45, Stack);                     // slot 0: 45/50
        Assert.True(inv.CanInsert(Iron, 55, Stack));     // 5 top-up + 50 in slot 1
        Assert.False(inv.CanInsert(Iron, 56, Stack));
    }

    [Fact]
    public void CanInsert_ZeroCount_True_NegativeOrBadStack_False()
    {
        var inv = new Inventory(2);
        Assert.True(inv.CanInsert(Iron, 0, Stack));
        Assert.False(inv.CanInsert(Iron, -1, Stack));
        Assert.False(inv.CanInsert(Iron, 10, 0));
    }

    [Fact]
    public void CanInsert_ReadOnlyOrFilterMismatch_False()
    {
        Assert.False(new Inventory(4, readOnly: true).CanInsert(Iron, 1, Stack));
        Assert.False(new Inventory(4, filterItemProtoId: Iron).CanInsert(Copper, 1, Stack));
    }

    [Fact]
    public void CanInsert_DoesNotMutate()
    {
        var inv = new Inventory(2);
        var h = Hash(inv);
        inv.CanInsert(Iron, 40, Stack);
        Assert.Equal(h, Hash(inv));
    }
```

- [ ] **Step 5: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoryTests.CanInsert"`
Expected: FAIL — `Inventory.CanInsert` does not exist (compile error).

- [ ] **Step 6: Implement `Inventory.CanInsert`**

In `sim/Faketorio.Sim/Items/Inventory.cs`, add after `Insert`:

```csharp
    // 只读模拟 Insert 的两轮:能否把 count 件全放下。不改状态(§6.2 手搓完成前预检)。
    public bool CanInsert(int itemProtoId, int count, int stackSize)
    {
        if (ReadOnly) return false;
        if (FilterItemProtoId != 0 && itemProtoId != FilterItemProtoId) return false;
        if (count < 0 || stackSize <= 0) return false;
        if (count == 0) return true;

        int remaining = count;
        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (_slots[i].ItemProtoId != itemProtoId || _slots[i].Count >= stackSize) continue;
            remaining -= Math.Min(stackSize - _slots[i].Count, remaining);
        }
        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (!_slots[i].IsEmpty) continue;
            remaining -= Math.Min(stackSize, remaining);
        }
        return remaining == 0;
    }
```

- [ ] **Step 7: Run `CanInsert` tests to verify they pass**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoryTests.CanInsert"`
Expected: all PASS.

- [ ] **Step 8: Create the data files**

`data/base/player.json`:

```json
[{ "type": "player", "name": "player",
   "inventorySize": 60, "reachSubTiles": 1536, "walkSpeedSubTilesPerTick": 38, "craftQueueCap": 32,
   "startingInventory": [
     { "name": "iron-plate", "amount": 8 },
     { "name": "wooden-chest", "amount": 1 }
   ] }]
```

Add to `data/base/items.json` before the closing `]` (keep the comma after the `stone` line):

```json
  { "type": "item", "name": "iron-gear-wheel", "stackSize": 100 }
```

Replace `data/base/recipes.json` with (adds two `"crafting"` recipes; keeps `iron-plate`):

```json
[
  { "type": "recipe", "name": "iron-plate", "category": "smelting", "energyRequiredSeconds": 3.2,
    "ingredients": [ { "name": "iron-ore", "amount": 1 } ],
    "results": [ { "name": "iron-plate", "amount": 1 } ] },
  { "type": "recipe", "name": "iron-gear-wheel", "category": "crafting", "energyRequiredSeconds": 0.5,
    "ingredients": [ { "name": "iron-plate", "amount": 2 } ],
    "results": [ { "name": "iron-gear-wheel", "amount": 1 } ] },
  { "type": "recipe", "name": "wooden-chest", "category": "crafting", "energyRequiredSeconds": 0.5,
    "ingredients": [ { "name": "iron-plate", "amount": 2 } ],
    "results": [ { "name": "wooden-chest", "amount": 1 } ] }
]
```

In `data/base/map-gen.json`, append one entry to `starterPatches` (after the `stone` line, add a comma to that line):

```json
     { "resource": "coal", "centerX": 1, "centerY": -1, "radius": 2, "centerAmount": 800 }
```

- [ ] **Step 9: Write the failing loader tests**

Append to `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` (inside the class; `Load()` and `AssertLoadThrows(json)` helpers already exist):

```csharp
    [Fact]
    public void ResolvesRecipeIngredientAndResultItemIds()
    {
        var reg = Load();
        var gear = reg.Get<RecipePrototype>("iron-gear-wheel");
        int plateId = reg.Get<ItemPrototype>("iron-plate").Id;
        int gearId = reg.Get<ItemPrototype>("iron-gear-wheel").Id;
        Assert.Equal(new[] { new ResolvedAmount(plateId, 2) }, gear.ResolvedIngredients);
        Assert.Equal(new[] { new ResolvedAmount(gearId, 1) }, gear.ResolvedResults);
    }

    [Fact]
    public void LoadsPlayerPrototype()
    {
        var p = Load().Get<PlayerPrototype>("player");
        Assert.Equal(60, p.InventorySize);
        Assert.Equal(1536, p.ReachSubTiles);
        Assert.Equal(2, p.StartingInventory.Count);
        Assert.Equal("iron-plate", p.StartingInventory[0].Name);
    }

    [Fact]
    public void RecipeWithUnknownItem_Throws() => AssertLoadThrows(
        "[{ \"type\": \"recipe\", \"name\": \"bad\", \"category\": \"crafting\", " +
        "\"ingredients\": [ { \"name\": \"nonexistent\", \"amount\": 1 } ], " +
        "\"results\": [ { \"name\": \"nonexistent\", \"amount\": 1 } ] }]");

    [Fact]
    public void TwoPlayerPrototypes_Throws() => AssertLoadThrows(
        "[{ \"type\": \"player\", \"name\": \"a\" }, { \"type\": \"player\", \"name\": \"b\" }]");

    [Fact]
    public void PlayerInventorySizeZero_Throws() => AssertLoadThrows(
        "[{ \"type\": \"player\", \"name\": \"player\", \"inventorySize\": 0 }]");

    [Fact]
    public void PlayerStartingInventoryUnknownItem_Throws() => AssertLoadThrows(
        "[{ \"type\": \"player\", \"name\": \"player\", " +
        "\"startingInventory\": [ { \"name\": \"nonexistent\", \"amount\": 1 } ] }]");

    [Fact]
    public void PlayerReachZero_Throws() => AssertLoadThrows(
        "[{ \"type\": \"player\", \"name\": \"player\", \"reachSubTiles\": 0 }]");
```

Also update the existing P6 test `LoadsMapGenWithStarterPatches` (currently `Assert.Equal(4, mg.StarterPatches.Count);`) to `Assert.Equal(5, mg.StarterPatches.Count);` — leave its `StarterPatches[0]` assertion (still coal at (6,-8), since the new patch is appended).

- [ ] **Step 10: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~PrototypeLoaderTests"`
Expected: the new tests FAIL (compile error — `PlayerPrototype` / `ResolvedAmount` unknown); `LoadsMapGenWithStarterPatches` fails on the 4→5 count once it compiles.

- [ ] **Step 11: Add the `"player"` parse arm and `miningTimeSeconds` to `PrototypeLoader.Parse`**

In `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`, add a `"player"` arm to the `type switch` in `Parse` (before the `_ =>` throw):

```csharp
            "player" => new PlayerPrototype
            {
                Name = name,
                InventorySize            = GetInt(el, "inventorySize", 60),
                ReachSubTiles            = GetInt(el, "reachSubTiles", 1536),
                WalkSpeedSubTilesPerTick = GetInt(el, "walkSpeedSubTilesPerTick", 38),
                CraftQueueCap            = GetInt(el, "craftQueueCap", 32),
                StartingInventory        = ParseAmounts(el.TryGetProperty("startingInventory", out var si)
                    ? si : default).AsReadOnly(),
            },
```

`ParseAmounts` currently requires a valid array element. Change its signature to tolerate an undefined element:

```csharp
    private static List<ItemAmount> ParseAmounts(JsonElement arr)
    {
        var list = new List<ItemAmount>();
        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray())
                list.Add(new ItemAmount
                {
                    Name = el.GetProperty("name").GetString()!,
                    Amount = el.GetProperty("amount").GetInt32(),
                });
        return list;
    }
```

(Recipe `ingredients`/`results` are still passed a real array via `el.GetProperty(...)`, so this is backward-compatible; a missing `startingInventory` yields an empty list.)

In the `"resource"` arm, add `MiningTimeTicks`:

```csharp
            "resource" => new ResourcePrototype
            {
                Name = name,
                MinableResult = el.GetProperty("minableResult").GetString()!,
                RichnessBase  = GetInt(el, "richnessBase", 0),
                RichnessScale = GetInt(el, "richnessScale", 0),
                MiningTimeTicks = Units.SecondsToTicks(GetDouble(el, "miningTimeSeconds", 1.0)),
                Layer = ParseNoiseLayer(el),
            },
```

- [ ] **Step 12: Add `ResolveAndValidateRecipesAndPlayer` and call it**

In `LoadFromDirectory`, add the call right after the existing `ResolveAndValidateMapGen(registry);`:

```csharp
        registry.AssignIds();
        ResolveAndValidateMapGen(registry);
        ResolveAndValidateRecipesAndPlayer(registry);
        return registry;
```

Add the method:

```csharp
    // AssignIds() 之后:把每个 RecipePrototype 的 Ingredients/Results 名字解析成
    // proto id,再校验 player prototype。与 ResolveAndValidateMapGen 同风格。
    private static void ResolveAndValidateRecipesAndPlayer(PrototypeRegistry registry)
    {
        int playerCount = 0;
        PlayerPrototype? player = null;
        for (int i = 0; i < registry.Count; i++)
        {
            switch (registry.GetById(i))
            {
                case RecipePrototype r:
                    r.ResolvedIngredients = Resolve(registry, r, r.Ingredients, "ingredient");
                    r.ResolvedResults     = Resolve(registry, r, r.Results, "result");
                    break;
                case PlayerPrototype p:
                    player = p; playerCount++;
                    break;
            }
        }

        if (playerCount > 1)
            throw new InvalidDataException("More than one 'player' prototype");

        if (player is not null)
        {
            if (player.InventorySize < 1)
                throw new InvalidDataException("player: inventorySize must be >= 1");
            if (player.ReachSubTiles < 1)
                throw new InvalidDataException("player: reachSubTiles must be >= 1");
            if (player.WalkSpeedSubTilesPerTick < 1)
                throw new InvalidDataException("player: walkSpeedSubTilesPerTick must be >= 1");
            if (player.CraftQueueCap < 1)
                throw new InvalidDataException("player: craftQueueCap must be >= 1");
            foreach (var ia in player.StartingInventory)
                if (!registry.TryGet<ItemPrototype>(ia.Name, out _))
                    throw new InvalidDataException($"player: startingInventory item '{ia.Name}' has no matching item");
        }
    }

    private static IReadOnlyList<ResolvedAmount> Resolve(
        PrototypeRegistry registry, RecipePrototype r, List<ItemAmount> src, string role)
    {
        var list = new List<ResolvedAmount>(src.Count);
        foreach (var ia in src)
        {
            if (ia.Amount < 1)
                throw new InvalidDataException($"Recipe '{r.Name}': {role} '{ia.Name}' amount must be >= 1");
            if (!registry.TryGet<ItemPrototype>(ia.Name, out var item))
                throw new InvalidDataException($"Recipe '{r.Name}': {role} '{ia.Name}' has no matching item");
            list.Add(new ResolvedAmount(item.Id, ia.Amount));
        }
        return list;
    }
```

- [ ] **Step 13: Run the loader + inventory tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~PrototypeLoaderTests"` then `--filter "FullyQualifiedName~InventoryTests"`
Expected: all PASS.

- [ ] **Step 14: Run the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. 235 + new `PrototypeLoaderTests` (7) + `InventoryTests` (5). The 4→5 starter-patch change is the only pre-existing test touched. No `Simulation`/`DeterminismTests` regressions (they don't yet construct `Player`).

- [ ] **Step 15: Commit**

```bash
git add sim/Faketorio.Sim/Prototypes/PlayerPrototype.cs sim/Faketorio.Sim/Prototypes/RecipePrototype.cs sim/Faketorio.Sim/Prototypes/ResourcePrototype.cs sim/Faketorio.Sim/Items/Inventory.cs sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs data/base/player.json data/base/recipes.json data/base/items.json data/base/map-gen.json sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs sim/Faketorio.Sim.Tests/InventoryTests.cs
git commit -m "$(cat <<'EOF'
feat(proto): PlayerPrototype + recipe name->id resolution + Inventory.CanInsert

New player prototype (validated: one only, positive fields, resolvable
starting-inventory items). RecipePrototype gains ResolvedIngredients/
ResolvedResults filled by a post-AssignIds pass. ResourcePrototype gains
MiningTimeTicks (from miningTimeSeconds, default 60). Inventory.CanInsert
read-only-simulates the two-pass insert. Data: player.json,
iron-gear-wheel/wooden-chest crafting recipes, a near-origin coal starter
patch so spawn is in hand-mining reach of ore.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

---

## Task 2: `Player` class + movement + collision + `Simulation` wiring + `DestroyEntityAt`

**Files:**
- Create: `sim/Faketorio.Sim/Player/Player.cs`, `sim/Faketorio.Sim/Player/CraftJob.cs`
- Modify: `sim/Faketorio.Sim/Commands/Command.cs`, `sim/Faketorio.Sim/Simulation.cs`
- Test: `sim/Faketorio.Sim.Tests/PlayerTests.cs`, `sim/Faketorio.Sim.Tests/SimulationTests.cs`

**Interfaces:**
- Consumes: `PlayerPrototype` (Task 1), `Inventory` (P4), `IStateWriter`, `WorldGrid.GetEntityAt(int x, int y) → EntityId` (`EntityId.IsValid`), `Command` (`Type`, `Rotation`, `X`, `Y`, `ProtoId`), existing `Simulation` shape.
- Produces (Tasks 3–5 rely on these):
  - `public record struct CraftJob(int RecipeProtoId, int Count, long Progress);`
  - `public sealed class Player`:
    - `Player(int inventorySize)` — inventory `new Inventory(inventorySize)`, position `(0,0)`, all state cleared
    - `int X { get; }`, `int Y { get; }`, `byte WalkDir { get; }`, `bool Walking { get; }`
    - `bool Mining { get; }`, `int MineTargetX { get; }`, `int MineTargetY { get; }`, `long MineProgress { get; }`
    - `IReadOnlyList<CraftJob> CraftQueue { get; }`
    - `readonly Inventory Inventory`
    - `internal void SetWalk(byte dir)` / `internal void StopWalk()` / `internal void MoveTo(int x, int y)`
    - `internal void SetMineTarget(int x, int y)` — if `(x,y) != (MineTargetX, MineTargetY)` reset `MineProgress` to 0; set target; `Mining = true`
    - `internal void StopMining()` — `Mining = false`, `MineProgress = 0`
    - `internal void TickMineProgress()` — `MineProgress++`
    - `internal void ClearMineProgress()` — `MineProgress = 0`
    - `internal void EnqueueCraft(int recipeProtoId, int count)` — append `CraftJob(recipeProtoId, count, 0)`
    - `internal void TickCraftHeadProgress()` — `_queue[0] = _queue[0] with { Progress = _queue[0].Progress + 1 }`
    - `internal void CompleteOneCraftUnit()` — decrement head `Count`; drop head if 0 else `Progress = 0`
    - `void WriteState(IStateWriter writer)`
    - `internal static (int dx, int dy) WalkDelta(byte dir, int speed)`
  - `Simulation.Player` — `public Player Player { get; }`
  - `Simulation.DestroyEntityAt(EntityId id, EntityPrototype proto)` — `private`, extracted from `RemoveEntity`

- [ ] **Step 1: Create `CraftJob.cs`**

```csharp
namespace Faketorio.Sim.Player;

// 一份合成任务:配方 id + 剩余份数 + 当前这份的进度(int64 定点累加,§6.2)。
public record struct CraftJob(int RecipeProtoId, int Count, long Progress);
```

- [ ] **Step 2: Add the new `CommandType` values**

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
}
```

- [ ] **Step 3: Write the failing `PlayerTests`**

Create `sim/Faketorio.Sim.Tests/PlayerTests.cs`:

```csharp
using Faketorio.Sim.Player;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class PlayerTests
{
    private static ulong Hash(Player p)
    {
        var w = new Fnv1aHashWriter();
        p.WriteState(w);
        return w.Hash;
    }

    [Fact]
    public void NewPlayer_AtOrigin_IdleWithEmptyInventory()
    {
        var p = new Player(10);
        Assert.Equal(0, p.X);
        Assert.Equal(0, p.Y);
        Assert.False(p.Walking);
        Assert.False(p.Mining);
        Assert.Empty(p.CraftQueue);
        Assert.Equal(10, p.Inventory.SlotCount);
        Assert.Equal(0, p.Inventory.TotalItems());
    }

    [Theory]
    [InlineData(0, 0, -5)]
    [InlineData(1, 5, -5)]
    [InlineData(2, 5, 0)]
    [InlineData(3, 5, 5)]
    [InlineData(4, 0, 5)]
    [InlineData(5, -5, 5)]
    [InlineData(6, -5, 0)]
    [InlineData(7, -5, -5)]
    public void WalkDelta_EightDirections(byte dir, int expectedDx, int expectedDy)
    {
        var (dx, dy) = Player.WalkDelta(dir, 5);
        Assert.Equal(expectedDx, dx);
        Assert.Equal(expectedDy, dy);
    }

    [Fact]
    public void MoveTo_UpdatesPosition_ChangesHash()
    {
        var p = new Player(10);
        var h = Hash(p);
        p.MoveTo(38, -76);
        Assert.Equal(38, p.X);
        Assert.Equal(-76, p.Y);
        Assert.NotEqual(h, Hash(p));
    }

    [Fact]
    public void WriteState_SensitiveToWalkAndInventory()
    {
        var a = new Player(10);
        var b = new Player(10);
        Assert.Equal(Hash(a), Hash(b));
        a.SetWalk(3);
        Assert.NotEqual(Hash(a), Hash(b));
        b.SetWalk(3);
        Assert.Equal(Hash(a), Hash(b));
        a.Inventory.Insert(100, 1, 50);
        Assert.NotEqual(Hash(a), Hash(b));
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~PlayerTests"`
Expected: FAIL — `Player` does not exist (compile error).

- [ ] **Step 5: Create `Player.cs`**

```csharp
using Faketorio.Sim.Items;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Player;

// 模拟层玩家。不进 EntityPool(不占地)。所有状态变更方法 internal——只有
// Simulation 的"玩家 tick"调。全整数 / int64,无 float。
public sealed class Player
{
    public int X { get; private set; }
    public int Y { get; private set; }
    public byte WalkDir { get; private set; }
    public bool Walking { get; private set; }

    public bool Mining { get; private set; }
    public int MineTargetX { get; private set; }
    public int MineTargetY { get; private set; }
    public long MineProgress { get; private set; }

    private readonly List<CraftJob> _craftQueue = new();
    public IReadOnlyList<CraftJob> CraftQueue => _craftQueue;

    public readonly Inventory Inventory;

    public Player(int inventorySize) => Inventory = new Inventory(inventorySize);

    // 八向单位向量(子格),对角分量取满速(§4.2 近似)。0=北(-Y),顺时针。
    internal static (int dx, int dy) WalkDelta(byte dir, int speed) => dir switch
    {
        0 => (0, -speed),
        1 => (speed, -speed),
        2 => (speed, 0),
        3 => (speed, speed),
        4 => (0, speed),
        5 => (-speed, speed),
        6 => (-speed, 0),
        7 => (-speed, -speed),
        _ => (0, 0),
    };

    internal void SetWalk(byte dir) { Walking = true; WalkDir = dir; }
    internal void StopWalk() => Walking = false;
    internal void MoveTo(int x, int y) { X = x; Y = y; }

    internal void SetMineTarget(int x, int y)
    {
        if (x != MineTargetX || y != MineTargetY) MineProgress = 0;
        MineTargetX = x;
        MineTargetY = y;
        Mining = true;
    }
    internal void StopMining() { Mining = false; MineProgress = 0; }
    internal void TickMineProgress() => MineProgress++;
    internal void ClearMineProgress() => MineProgress = 0;

    internal void EnqueueCraft(int recipeProtoId, int count)
        => _craftQueue.Add(new CraftJob(recipeProtoId, count, 0));
    internal void TickCraftHeadProgress()
        => _craftQueue[0] = _craftQueue[0] with { Progress = _craftQueue[0].Progress + 1 };
    internal void CompleteOneCraftUnit()
    {
        var head = _craftQueue[0];
        if (head.Count <= 1) _craftQueue.RemoveAt(0);
        else _craftQueue[0] = head with { Count = head.Count - 1, Progress = 0 };
    }

    public void WriteState(IStateWriter writer)
    {
        writer.Write(X);
        writer.Write(Y);
        writer.Write(WalkDir);
        writer.Write((byte)(Walking ? 1 : 0));
        writer.Write((byte)(Mining ? 1 : 0));
        writer.Write(MineTargetX);
        writer.Write(MineTargetY);
        writer.Write(MineProgress);
        writer.Write(_craftQueue.Count);
        for (int i = 0; i < _craftQueue.Count; i++)
        {
            writer.Write(_craftQueue[i].RecipeProtoId);
            writer.Write(_craftQueue[i].Count);
            writer.Write(_craftQueue[i].Progress);
        }
        Inventory.WriteState(writer);
    }
}
```

- [ ] **Step 6: Run `PlayerTests` to verify they pass**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~PlayerTests"`
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add sim/Faketorio.Sim/Player/Player.cs sim/Faketorio.Sim/Player/CraftJob.cs sim/Faketorio.Sim/Commands/Command.cs sim/Faketorio.Sim.Tests/PlayerTests.cs
git commit -m "$(cat <<'EOF'
feat(player): Player class + CraftJob + player command types

Standalone Simulation object: sub-tile position, walk/mine/craft state,
own Inventory, granular internal mutators, deterministic WriteState, and
the 8-direction walk-vector table. CommandType gains MovePlayer/StopPlayer
/MineStart/MineStop/CraftEnqueue. No Simulation wiring yet.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

- [ ] **Step 8: Write the failing movement/collision integration tests**

Append to `sim/Faketorio.Sim.Tests/SimulationTests.cs` (inside `SimulationTests`; `NewSim()`, `PlaceChest`, `E` helpers already exist). Add `using Faketorio.Sim.Player;` at the top if not present.

```csharp
    private static Command Move(byte dir) => new() { Type = CommandType.MovePlayer, Rotation = dir };
    private static Command StopMove() => new() { Type = CommandType.StopPlayer };

    [Fact]
    public void MovePlayer_ThenStep_AdvancesByWalkSpeed()
    {
        var sim = NewSim();
        int speed = sim.Prototypes.Get<PlayerPrototype>("player").WalkSpeedSubTilesPerTick;
        sim.Submit(Move(2));   // east = +X
        sim.Step();
        Assert.Equal(speed, sim.Player.X);
        Assert.Equal(0, sim.Player.Y);
    }

    [Fact]
    public void StopPlayer_HaltsMovement()
    {
        var sim = NewSim();
        sim.Submit(Move(2)); sim.Step();
        int x = sim.Player.X;
        sim.Submit(StopMove()); sim.Step();
        Assert.Equal(x, sim.Player.X);
    }

    [Fact]
    public void MovePlayer_OutOfRangeDirection_IsRejected()
    {
        var sim = NewSim();
        sim.Submit(new Command { Type = CommandType.MovePlayer, Rotation = 8 });
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.Equal(0, sim.Player.X);
    }

    [Fact]
    public void MovePlayer_IntoOccupiedTile_IsBlocked()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 1, 0));   // tile (1,0), east of the player at sub-tile (0,0)
        sim.Step();
        for (int t = 0; t < 20; t++) { sim.Submit(Move(2)); sim.Step(); }
        // walking east must never enter tile x>=1
        Assert.True(sim.Player.X >> 8 < 1, $"player X sub-tile {sim.Player.X} entered an occupied tile");
    }
```

- [ ] **Step 9: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests.MovePlayer"`
Expected: FAIL — `Simulation.Player` does not exist (compile error).

- [ ] **Step 10: Wire `Player` into `Simulation`**

In `sim/Faketorio.Sim/Simulation.cs`:

1. Add `using Faketorio.Sim.Player;` to the using block.

2. Add the property and a field near `Resources`:

```csharp
    public ResourceGrid Resources { get; }
    public Player Player { get; }

    private readonly long _worldSeed;
    private readonly PlayerPrototype _playerProto;
```

3. In the constructor, after `Resources = new ResourceGrid(worldSeed, prototypes);`:

```csharp
        if (!prototypes.TryGet<PlayerPrototype>("player", out var pp))
            throw new InvalidOperationException("No 'player' prototype in the registry");
        _playerProto = pp;
        Player = new Player(pp.InventorySize);
        // 开局物资包在 Task 5 填;这里先留空。
```

4. In `Step`, insert the player-tick segment right after the `Apply` loop and before the belt advance loop:

```csharp
        var commands = _commands.BeginTick();
        for (int i = 0; i < commands.Length; i++)
            Apply(in commands[i]);

        // 玩家 tick:① 行走(碰撞) ② 挖掘(Task 3) ③ 合成(Task 4)
        PlayerWalk();

        // 传送带:推进 ...
```

Add the `PlayerWalk` method (private, near `ResolveBeltSpeed`):

```csharp
    private void PlayerWalk()
    {
        if (!Player.Walking) return;
        var (dx, dy) = Player.WalkDelta(Player.WalkDir, _playerProto.WalkSpeedSubTilesPerTick);
        int nx = Player.X + dx, ny = Player.Y + dy;
        if (World.GetEntityAt(nx >> 8, ny >> 8).IsValid) return;   // 整步拒绝,不滑墙
        Player.MoveTo(nx, ny);
    }
```

5. Add the `MovePlayer` / `StopPlayer` cases to `Apply` (before `default:`):

```csharp
            case CommandType.MovePlayer:
                if (command.Rotation > 7) { RejectedCommandCount++; return; }
                Player.SetWalk(command.Rotation);
                return;
            case CommandType.StopPlayer:
                Player.StopWalk();
                return;
```

6. Extract `DestroyEntityAt` from the `RemoveEntity` case. Replace the body of `case CommandType.RemoveEntity:` with:

```csharp
            case CommandType.RemoveEntity:
            {
                var id = World.GetEntityAt(command.X, command.Y);
                if (!id.IsValid || !Entities.IsAlive(id))
                {
                    RejectedCommandCount++;
                    return;
                }
                ref var data = ref Entities.Get(id);
                if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not EntityPrototype proto)
                {
                    RejectedCommandCount++;
                    return;
                }
                DestroyEntityAt(id, proto);
                return;
            }
```

Add the helper (private):

```csharp
    // 移除一个实体:清占地 + 销毁 + belt/container 后处理。命令路径(RemoveEntity)
    // 和手挖(Task 3)共用。调用方保证 id 存活、proto 匹配。
    private void DestroyEntityAt(EntityId id, EntityPrototype proto)
    {
        ref var data = ref Entities.Get(id);
        int bx = data.X, by = data.Y;
        bool isBelt = proto is TransportBeltPrototype;
        bool isContainer = proto is ContainerPrototype;
        World.ClearArea(data.X, data.Y, proto.TileWidth, proto.TileHeight);
        Entities.Destroy(id);
        if (isBelt) Belts.RemoveBelt(bx, by);
        if (isContainer) Inventories.RemoveContainer(id);
    }
```

7. In `WriteState`, add after `Resources.WriteState(writer);`:

```csharp
        Player.WriteState(writer);
```

- [ ] **Step 11: Run the movement tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests.MovePlayer"` then `--filter "FullyQualifiedName~SimulationTests.StopPlayer"`
Expected: all PASS.

- [ ] **Step 12: Run the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. Every existing `Simulation`/`Determinism` test now constructs a `Player` (idle, empty) and its `WriteState` gains a trailing block — relative-comparison tests still pass; golden `RunScenario` / `RunBeltScenario` / `RunInventoryScenario` / `RunResourceScenario` unchanged and still equal run-vs-run.

- [ ] **Step 13: Commit**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "$(cat <<'EOF'
feat(sim): wire Player into Simulation — construction, walk tick, WriteState

Simulation constructs a Player from the 'player' prototype (throws if
absent). New player-tick segment after the command-apply loop runs
PlayerWalk: 8-direction step by WalkSpeedSubTilesPerTick, whole-step
reject on an occupied destination tile. MovePlayer/StopPlayer Apply cases.
RemoveEntity's body extracted to DestroyEntityAt (shared with hand-mining
in the next task). Player.WriteState appended after Resources.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

---

## Task 3: HandMine

**Files:**
- Modify: `sim/Faketorio.Sim/Simulation.cs`
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`

**Interfaces:**
- Consumes: `Player` mining API (Task 2 — `SetMineTarget`, `StopMining`, `TickMineProgress`, `ClearMineProgress`, `Mining`, `MineTargetX/Y`, `MineProgress`), `Simulation.DestroyEntityAt` (Task 2), `ResourceGrid.GetResourceAt(int,int) → ResourceCell` / `Extract(int,int,int) → int` (P6), `ValueNoise.Isqrt(long) → int` (P6), `EntityPrototype.MinableResult` / `MiningTimeTicks`, `ResourcePrototype.MinableResult` / `MiningTimeTicks` (Task 1), `ItemPrototype.StackSize`.
- Produces: `Simulation` handles `MineStart` / `MineStop`; the player tick advances mining between walk and (future) craft.

- [ ] **Step 1: Write the failing HandMine tests**

Append to `sim/Faketorio.Sim.Tests/SimulationTests.cs` (inside `SimulationTests`). Helpers `Move`/`StopMove` from Task 2 exist; add:

```csharp
    private static Command MineAt(int x, int y) => new() { Type = CommandType.MineStart, X = x, Y = y };

    [Fact]
    public void HandMine_Entity_RemovesItAndYieldsItem()
    {
        var sim = NewSim();
        int chestItem = sim.Prototypes.Get<ItemPrototype>("wooden-chest").Id;
        int miningTicks = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").MiningTimeTicks;
        sim.Submit(PlaceChest(sim, 1, 0));      // adjacent to player at (0,0), inside reach
        sim.Step();

        for (int t = 0; t < miningTicks + 2; t++) { sim.Submit(MineAt(1, 0)); sim.Step(); }

        Assert.False(sim.World.GetEntityAt(1, 0).IsValid);
        Assert.Equal(1, sim.Player.Inventory.CountOf(chestItem));
        Assert.Equal(0, sim.RejectedCommandCount);
    }

    [Fact]
    public void HandMine_Resource_ExtractsOneAndYieldsItem()
    {
        var sim = NewSim();
        int coalItem = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int miningTicks = sim.Prototypes.Get<ResourcePrototype>("coal").MiningTimeTicks;
        int before = sim.Resources.GetResourceAt(1, -1).Amount;   // near-origin starter patch
        Assert.True(before > 0);

        for (int t = 0; t < miningTicks + 2; t++) { sim.Submit(MineAt(1, -1)); sim.Step(); }

        Assert.Equal(1, sim.Player.Inventory.CountOf(coalItem));
        Assert.Equal(before - 1, sim.Resources.GetResourceAt(1, -1).Amount);
    }

    [Fact]
    public void HandMine_OutOfReach_MakesNoProgress()
    {
        var sim = NewSim();
        for (int t = 0; t < 200; t++) { sim.Submit(MineAt(6, -8)); sim.Step(); }   // ~10 tiles, reach is 6
        Assert.Equal(0, sim.Player.MineProgress);
        Assert.Equal(0, sim.Player.Inventory.TotalItems());
    }

    [Fact]
    public void HandMine_Resource_InventoryFull_Pauses()
    {
        var sim = NewSim();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        int miningTicks = sim.Prototypes.Get<ResourcePrototype>("coal").MiningTimeTicks;
        // fill the whole player inventory with coal
        var inv = sim.Player.Inventory;
        inv.Insert(coal, inv.SlotCount * coalStack, coalStack);
        int amtBefore = sim.Resources.GetResourceAt(1, -1).Amount;

        for (int t = 0; t < miningTicks + 5; t++) { sim.Submit(MineAt(1, -1)); sim.Step(); }

        Assert.Equal(amtBefore, sim.Resources.GetResourceAt(1, -1).Amount);   // nothing extracted
        Assert.Equal(miningTicks, sim.Player.MineProgress);                    // parked at threshold
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests.HandMine"`
Expected: FAIL — `MineStart` falls through to `default:` so `RejectedCommandCount` climbs and nothing mines.

- [ ] **Step 3: Add `MineStart` / `MineStop` to `Apply`**

In `sim/Faketorio.Sim/Simulation.cs`, add to `Apply` (before `default:`):

```csharp
            case CommandType.MineStart:
                Player.SetMineTarget(command.X, command.Y);
                return;
            case CommandType.MineStop:
                Player.StopMining();
                return;
```

- [ ] **Step 4: Add the mining segment to the player tick**

In `Step`, change the player-tick segment to call `PlayerMine()` after `PlayerWalk()`:

```csharp
        PlayerWalk();
        PlayerMine();
```

Add the method (private):

```csharp
    private void PlayerMine()
    {
        if (!Player.Mining) return;
        int tx = Player.MineTargetX, ty = Player.MineTargetY;

        // reach:玩家点到目标格中心的欧氏距离(整数 sqrt,P6)
        long ddx = Player.X - (tx * 256 + 128);
        long ddy = Player.Y - (ty * 256 + 128);
        if (ValueNoise.Isqrt(ddx * ddx + ddy * ddy) > _playerProto.ReachSubTiles) return;

        // 解析目标:优先实体,其次矿脉
        var eid = World.GetEntityAt(tx, ty);
        EntityPrototype? entityProto = null;
        if (eid.IsValid && Entities.IsAlive(eid)
            && Prototypes.TryGetById(Entities.Get(eid).ProtoId, out var ep)
            && ep is EntityPrototype epx && epx.MinableResult is not null)
            entityProto = epx;

        ResourcePrototype? resProto = null;
        if (entityProto is null)
        {
            var cell = Resources.GetResourceAt(tx, ty);
            if (!cell.IsEmpty)
                resProto = (ResourcePrototype)Prototypes.GetById(cell.ResourceProtoId);
        }

        if (entityProto is null && resProto is null) return;   // 空转

        int threshold = entityProto?.MiningTimeTicks ?? resProto!.MiningTimeTicks;
        Player.TickMineProgress();
        if (Player.MineProgress < threshold) return;

        string resultName = entityProto?.MinableResult ?? resProto!.MinableResult;
        var itemProto = Prototypes.Get<ItemPrototype>(resultName);
        if (Player.Inventory.Insert(itemProto.Id, 1, itemProto.StackSize) == 0) return;   // 背包满:停在阈值

        if (entityProto is not null)
        {
            DestroyEntityAt(eid, entityProto);   // 目标失效,下 tick 空转
        }
        else
        {
            Resources.Extract(tx, ty, 1);        // 必返回 1(刚查过非空)
            Player.ClearMineProgress();           // 继续挖
        }
    }
```

Note: `Prototypes.Get<ItemPrototype>(resultName)` — `MinableResult` for entities and resources is an item *name*. The `wooden-chest` container's `MinableResult` is `"wooden-chest"`, which resolves to the `ItemPrototype` "wooden-chest" (item and entity share the name; `Get<ItemPrototype>` disambiguates by type).

- [ ] **Step 5: Run the HandMine tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests.HandMine"`
Expected: all PASS.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. No regressions (mining is inert unless `MineStart` is submitted).

- [ ] **Step 7: Commit**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "$(cat <<'EOF'
feat(sim): HandMine — MineStart/MineStop + progress + entity/resource yield

Player-tick mining segment after walking: Euclidean reach gate
(ValueNoise.Isqrt), target resolves to a minable entity first then a
resource cell, int64 progress to the target's MiningTimeTicks. On
completion one MinableResult item is inserted; a full inventory parks
progress at the threshold. Entity target -> DestroyEntityAt; resource
target -> ResourceGrid.Extract(.,.,1) then progress resets to keep mining.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

---

## Task 4: HandCraft

**Files:**
- Modify: `sim/Faketorio.Sim/Simulation.cs`
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`

**Interfaces:**
- Consumes: `Player` craft API (Task 2 — `EnqueueCraft`, `TickCraftHeadProgress`, `CompleteOneCraftUnit`, `CraftQueue`), `RecipePrototype` (`Category`, `Enabled`, `EnergyRequiredTicks`, `ResolvedIngredients`, `ResolvedResults` — Task 1), `Inventory.CountOf` / `Remove` / `Insert` / `CanInsert` (Task 1), `PlayerPrototype.CraftQueueCap`, `ItemPrototype.StackSize`.
- Produces: `Simulation` handles `CraftEnqueue`; the player tick advances the craft-queue head after mining.

- [ ] **Step 1: Write the failing HandCraft tests**

Append to `sim/Faketorio.Sim.Tests/SimulationTests.cs` (inside `SimulationTests`):

```csharp
    private static Command Craft(int recipeId, int count) => new()
        { Type = CommandType.CraftEnqueue, ProtoId = recipeId, X = count };

    [Fact]
    public void HandCraft_DeductsIngredientsAtEnqueue_ProducesResultsOverTime()
    {
        var sim = NewSim();
        // starter kit gives 8 iron-plate; craft 2 gears (2 plate each)
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int gear = sim.Prototypes.Get<ItemPrototype>("iron-gear-wheel").Id;
        var recipe = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel");
        int gearRecipeId = recipe.Id;

        sim.Submit(Craft(gearRecipeId, 2));
        sim.Step();
        Assert.Equal(4, sim.Player.Inventory.CountOf(plate));      // 8 - 2*2, deducted immediately
        Assert.Single(sim.Player.CraftQueue);

        for (int t = 0; t < 2 * recipe.EnergyRequiredTicks + 2; t++) sim.Step();
        Assert.Equal(2, sim.Player.Inventory.CountOf(gear));
        Assert.Empty(sim.Player.CraftQueue);
    }

    [Fact]
    public void HandCraft_NotEnoughIngredients_IsRejected_NoDeduction()
    {
        var sim = NewSim();
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int gearRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;
        sim.Submit(Craft(gearRecipeId, 99));      // needs 198 plate, has 8
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.Equal(8, sim.Player.Inventory.CountOf(plate));
        Assert.Empty(sim.Player.CraftQueue);
    }

    [Fact]
    public void HandCraft_SmeltingRecipe_IsRejected()
    {
        var sim = NewSim();
        int ironPlateRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-plate").Id;  // category "smelting"
        sim.Submit(Craft(ironPlateRecipeId, 1));
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
    }

    [Fact]
    public void HandCraft_QueueFull_IsRejected()
    {
        var sim = NewSim();
        int cap = sim.Prototypes.Get<PlayerPrototype>("player").CraftQueueCap;
        int chestRecipeId = sim.Prototypes.Get<RecipePrototype>("wooden-chest").Id;
        // give the player plenty of plate
        sim.Player.Inventory.Insert(sim.Prototypes.Get<ItemPrototype>("iron-plate").Id, 500, 100);
        for (int i = 0; i < cap; i++) { sim.Submit(Craft(chestRecipeId, 1)); sim.Step(); }
        Assert.Equal(cap, sim.Player.CraftQueue.Count);
        sim.Submit(Craft(chestRecipeId, 1)); sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.Equal(cap, sim.Player.CraftQueue.Count);
    }

    [Fact]
    public void HandCraft_InventoryFull_BlocksHeadJob()
    {
        var sim = NewSim();
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int gear = sim.Prototypes.Get<ItemPrototype>("iron-gear-wheel").Id;
        var recipe = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel");
        // exactly enough plate for one gear, then jam every slot with a non-gear item so the result can't land
        var inv = sim.Player.Inventory;
        inv.Insert(plate, 2, 100);
        // fill remaining slots: keep 0 free for gear. Use coal to fill all-but-none.
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        inv.Insert(coal, inv.SlotCount * 50, 50);   // fills every slot not holding the 2 plate; the plate slot has room for coal? no - different type
        // The plate slot holds iron-plate; coal fills the other SlotCount-1 slots. One partial? ensure zero empty:
        // (SlotCount-1) slots * 50 coal fills them; plate slot stays iron-plate(2). No empty slot, no partial gear-compatible slot.

        sim.Submit(Craft(recipe.Id, 1));
        sim.Step();
        for (int t = 0; t < recipe.EnergyRequiredTicks + 5; t++) sim.Step();

        Assert.Equal(0, sim.Player.Inventory.CountOf(gear));   // blocked
        Assert.Single(sim.Player.CraftQueue);
        Assert.Equal(recipe.EnergyRequiredTicks, sim.Player.CraftQueue[0].Progress);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests.HandCraft"`
Expected: FAIL — `CraftEnqueue` falls through to `default:`.

- [ ] **Step 3: Add `CraftEnqueue` to `Apply`**

In `sim/Faketorio.Sim/Simulation.cs`, add to `Apply` (before `default:`):

```csharp
            case CommandType.CraftEnqueue:
            {
                if (command.X < 1
                    || !Prototypes.TryGetById(command.ProtoId, out var rp) || rp is not RecipePrototype recipe
                    || recipe.Category != "crafting" || !recipe.Enabled
                    || Player.CraftQueue.Count >= _playerProto.CraftQueueCap)
                {
                    RejectedCommandCount++;
                    return;
                }
                // 检查能否付清 X 份的全部输入
                foreach (var ing in recipe.ResolvedIngredients)
                    if (Player.Inventory.CountOf(ing.ItemProtoId) < ing.Amount * command.X)
                    {
                        RejectedCommandCount++;
                        return;
                    }
                foreach (var ing in recipe.ResolvedIngredients)
                    Player.Inventory.Remove(ing.ItemProtoId, ing.Amount * command.X);
                Player.EnqueueCraft(command.ProtoId, command.X);
                return;
            }
```

- [ ] **Step 4: Add the craft segment to the player tick**

In `Step`, extend the player-tick segment:

```csharp
        PlayerWalk();
        PlayerMine();
        PlayerCraft();
```

Add the method (private):

```csharp
    private void PlayerCraft()
    {
        if (Player.CraftQueue.Count == 0) return;
        var job = Player.CraftQueue[0];
        var recipe = (RecipePrototype)Prototypes.GetById(job.RecipeProtoId);

        Player.TickCraftHeadProgress();
        if (Player.CraftQueue[0].Progress < recipe.EnergyRequiredTicks) return;

        // 全有或全无预检(M1 配方单产物)
        foreach (var res in recipe.ResolvedResults)
        {
            var ip = Prototypes.GetById(res.ItemProtoId);
            int stack = ((ItemPrototype)ip).StackSize;
            if (!Player.Inventory.CanInsert(res.ItemProtoId, res.Amount, stack)) return;   // 阻塞
        }
        foreach (var res in recipe.ResolvedResults)
        {
            int stack = ((ItemPrototype)Prototypes.GetById(res.ItemProtoId)).StackSize;
            Player.Inventory.Insert(res.ItemProtoId, res.Amount, stack);
        }
        Player.CompleteOneCraftUnit();
    }
```

- [ ] **Step 5: Run the HandCraft tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests.HandCraft"`
Expected: all PASS. If `HandCraft_InventoryFull_BlocksHeadJob`'s inventory-jamming setup leaves an empty slot (making the result fit), adjust the fill so every slot is occupied and none can take a gear — the assertion (blocked, progress parked) must not be weakened.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. No regressions (crafting is inert unless `CraftEnqueue` is submitted).

- [ ] **Step 7: Commit**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "$(cat <<'EOF'
feat(sim): HandCraft — CraftEnqueue + bounded FIFO queue + blocking

CraftEnqueue validates (crafting category, enabled, queue cap, affordable)
and deducts all ingredients * count immediately, appending one CraftJob.
Player-tick craft segment after mining advances the head job +1/tick; at
EnergyRequiredTicks it does an all-or-nothing CanInsert pre-check, inserts
every result, decrements the job, and drops it at zero. A full inventory
parks the head job at the threshold.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

---

## Task 5: Starter kit + determinism + full-loop tests

**Files:**
- Modify: `sim/Faketorio.Sim/Simulation.cs`
- Test: `sim/Faketorio.Sim.Tests/SimulationTests.cs`, `sim/Faketorio.Sim.Tests/DeterminismTests.cs`

**Interfaces:**
- Consumes: `PlayerPrototype.StartingInventory` (Task 1), `Player.Inventory` (Task 2), all command handlers (Tasks 2–4).
- Produces: the `Simulation` constructor fills the player inventory from `StartingInventory`.

- [ ] **Step 1: Write the failing starter-kit + full-loop tests**

Append to `sim/Faketorio.Sim.Tests/SimulationTests.cs` (inside `SimulationTests`):

```csharp
    [Fact]
    public void NewSimulation_FillsPlayerInventoryFromStartingKit()
    {
        var sim = NewSim();
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int chest = sim.Prototypes.Get<ItemPrototype>("wooden-chest").Id;
        Assert.Equal(8, sim.Player.Inventory.CountOf(plate));
        Assert.Equal(1, sim.Player.Inventory.CountOf(chest));
    }

    [Fact]
    public void FullLoop_PlaceChest_HandMineItBack_ThenCraftAnother()
    {
        var sim = NewSim();
        int chestItem = sim.Prototypes.Get<ItemPrototype>("wooden-chest").Id;
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int chestRecipe = sim.Prototypes.Get<RecipePrototype>("wooden-chest").Id;
        int miningTicks = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").MiningTimeTicks;
        var chestEnergy = sim.Prototypes.Get<RecipePrototype>("wooden-chest").EnergyRequiredTicks;

        // place the starter chest at (1,0), then mine it back
        sim.Submit(new Command
        {
            Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id,
            X = 1, Y = 0, Rotation = 0,
        });
        sim.Step();
        Assert.Equal(0, sim.Player.Inventory.CountOf(chestItem));   // the 1 starter chest was placed
        for (int t = 0; t < miningTicks + 2; t++) { sim.Submit(MineAt(1, 0)); sim.Step(); }
        Assert.Equal(1, sim.Player.Inventory.CountOf(chestItem));   // mined back

        // craft a second chest from starter plate (8 available, needs 2)
        sim.Submit(Craft(chestRecipe, 1));
        sim.Step();
        for (int t = 0; t < chestEnergy + 2; t++) sim.Step();
        Assert.Equal(2, sim.Player.Inventory.CountOf(chestItem));
        Assert.Equal(6, sim.Player.Inventory.CountOf(plate));
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests.NewSimulation_FillsPlayerInventoryFromStartingKit"`
Expected: FAIL — the player inventory is empty (Task 2 left the kit unfilled).

- [ ] **Step 3: Fill the starter kit in the `Simulation` constructor**

In `sim/Faketorio.Sim/Simulation.cs`, replace the `// 开局物资包在 Task 5 填;这里先留空。` comment (from Task 2 Step 10.3) with:

```csharp
        foreach (var ia in pp.StartingInventory)
        {
            var item = prototypes.Get<ItemPrototype>(ia.Name);
            Player.Inventory.Insert(item.Id, ia.Amount, item.StackSize);   // 放不下静默丢弃(InventorySize >= 1 已校验)
        }
```

- [ ] **Step 4: Run the starter-kit + full-loop tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests.NewSimulation_FillsPlayerInventoryFromStartingKit"` then `--filter "FullyQualifiedName~SimulationTests.FullLoop"`
Expected: all PASS.

- [ ] **Step 5: Write the failing determinism test**

Append to `sim/Faketorio.Sim.Tests/DeterminismTests.cs` (inside `DeterminismTests`):

```csharp
    private static List<ulong> RunPlayerScenario(long seed)
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"), seed);
        int gearRecipe = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;
        var hashes = new List<ulong>();
        for (int t = 0; t < 60; t++)
        {
            if (t < 8)  sim.Submit(new Command { Type = CommandType.MovePlayer, Rotation = 2 });  // walk east
            if (t == 8) sim.Submit(new Command { Type = CommandType.StopPlayer });
            if (t == 10) sim.Submit(new Command { Type = CommandType.MineStart, X = 1, Y = -1 });  // near coal patch
            if (t == 40) sim.Submit(new Command { Type = CommandType.MineStop });
            if (t == 12) sim.Submit(new Command { Type = CommandType.CraftEnqueue, ProtoId = gearRecipe, X = 1 });
            sim.Step();
            hashes.Add(sim.ComputeStateHash());
        }
        return hashes;
    }

    [Fact]
    public void PlayerScenario_SameSeedSameCommands_SameHashEveryTick()
        => Assert.Equal(RunPlayerScenario(4242), RunPlayerScenario(4242));
```

- [ ] **Step 6: Run the determinism test + full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~DeterminismTests"`
Expected: all PASS — the new scenario is equal run-vs-run, and the 4 pre-existing golden scenarios (`SameCommands_SameHashEveryTick`, `BeltScenario_SameCommands_SameHashEveryTick`, `InventoryScenario_SameCommands_SameHashEveryTick`, `ResourceScenario_SameSeedSameCommands_SameHashEveryTick`) still pass (their helpers were not edited; `Simulation.WriteState` now ends with a `Player` block that shifts absolute hashes identically across both runs).

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. 235 baseline + Task 1 (12) + Task 2 (`PlayerTests` 11 incl. Theory rows, `SimulationTests` +4) + Task 3 (+4) + Task 4 (+5) + Task 5 (`SimulationTests` +2, `DeterminismTests` +1).

- [ ] **Step 8: Commit**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs sim/Faketorio.Sim.Tests/DeterminismTests.cs
git commit -m "$(cat <<'EOF'
feat(sim): starter kit + player determinism + full-loop coverage

Simulation constructor fills the player inventory from
PlayerPrototype.StartingInventory. RunPlayerScenario exercises walk /
hand-mine / hand-craft over 60 ticks, hash-equal at the same seed. Full
loop test: place the starter chest, hand-mine it back, hand-craft a
second one from starter plate.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

---

## Self-Review

**1. Spec coverage:**

| Spec section | Task / step |
|---|---|
| §2 file structure (2 create + 6 modify prod, 4 data, tests) | Tasks 1–5 |
| §3 `PlayerPrototype` fields + `data/base/player.json` | Task 1 Steps 1, 8 |
| §3 crafting recipes + `iron-gear-wheel` item + near-origin coal patch | Task 1 Step 8 |
| §3.1 recipe name→id resolution (post-`AssignIds`, `Amount < 1` + unknown-item throws) | Task 1 Step 12 `Resolve` |
| §3.1 `player` prototype rules (>1 → `InvalidDataException` at load; none → `InvalidOperationException` at ctor; field validation) | Task 1 Step 12 (`playerCount > 1`, field checks) + Task 2 Step 10.3 (ctor guard) |
| §2 `RecipePrototype.ResolvedAmount`/`Resolved*` | Task 1 Step 2 |
| §2 `ResourcePrototype.MiningTimeTicks` (spec misnamed the file — it's `ResourcePrototype.cs`) | Task 1 Step 3 + Step 11 (`miningTimeSeconds` parse) |
| §2 `Inventory.CanInsert` | Task 1 Step 6 |
| §4 `Player` class (position, walk state, mine state, craft queue, inventory, `WriteState`) | Task 2 Step 5 |
| §4.1 `MovePlayer` (`Rotation` 0..7, `>7` reject) / `StopPlayer` | Task 2 Step 10.5 |
| §4.2 8-direction walk vectors (diagonal full-speed), whole-step collision via `World.GetEntityAt(nx>>8, ny>>8)` | Task 2 Step 5 (`WalkDelta`) + Step 10.4 (`PlayerWalk`) |
| §5.1 `MineStart` (reset progress on target change) / `MineStop` (zero progress) | Task 2 Step 5 (`SetMineTarget`/`StopMining`) + Task 3 Step 3 |
| §5.2 reach gate (`ValueNoise.Isqrt` to tile centre), entity-first target resolution, threshold, completion, inventory-full pause, entity→`DestroyEntityAt`, resource→`Extract`+reset | Task 3 Step 4 (`PlayerMine`) |
| §5.3 mining `WriteState` fields | Task 2 Step 5 (`WriteState`) |
| §6.1 `CraftEnqueue` validation + immediate ingredient deduction | Task 4 Step 3 |
| §6.2 head-job progress, all-or-nothing `CanInsert` pre-check, insert results, decrement, block-on-full | Task 4 Step 4 (`PlayerCraft`) |
| §6.3 craft `WriteState` (queue count + per job) | Task 2 Step 5 (`WriteState`) |
| §7 `Simulation` — `Player` property, ctor construction + guard, `Step` player-tick order (walk→mine→craft, after Apply, before belts), `Apply` 5 cases, `WriteState` last, `DestroyEntityAt` extraction | Task 2 Step 10 + Tasks 3/4 Step 3–4 |
| §6 starter kit fill in ctor | Task 5 Step 3 |
| §8 determinism (all int/long, no `Dictionary` in `WriteState`, fixed append position, golden scenarios unaffected) | Task 2 Step 5 (`WriteState` fixed-order) + Task 5 Steps 5–6 |
| §9 tests (`PlayerTests`, `PrototypeLoaderTests`+, `InventoryTests`+, `SimulationTests`+, `DeterminismTests`+) | Tasks 1–5 test steps |
| §9 bump P6 starter-patch count 4→5 | Task 1 Step 9 |
| §10 five-task split | Tasks 1 / 2 / 3 / 4 / 5 match §10 |
| §11 quickbar/cursor/presentation deferred | No code — correctly excluded |

No gaps. One spec-filename correction flagged inline (Task 1 Step 3 / coverage table): `MiningTimeTicks` goes in `ResourcePrototype.cs`, not `EntityPrototype.cs` as §2 says (the class is defined in the former).

**2. Placeholder scan:** No "TBD"/"TODO"/"handle edge cases"/"similar to Task N". Every code step has complete code; every test step has full bodies. Task 4 Step 1's `HandCraft_InventoryFull_BlocksHeadJob` carries an inline reasoning comment about the inventory-jamming setup and Step 5 names the adjustment if a slot stays free — this is a concrete test-tuning note, not an unspecified blank.

**3. Type consistency:**
- `PlayerPrototype` — fields defined Task 1 Step 1; read in Task 2 (`pp.InventorySize`, `_playerProto.WalkSpeedSubTilesPerTick`), Task 3 (`_playerProto.ReachSubTiles`), Task 4 (`_playerProto.CraftQueueCap`), Task 5 (`pp.StartingInventory`). Consistent.
- `ResolvedAmount(int ItemProtoId, int Amount)` — Task 1 Step 2; consumed in Task 4 Step 3 (`ing.ItemProtoId`, `ing.Amount`) and Step 4 (`res.ItemProtoId`, `res.Amount`). Consistent.
- `RecipePrototype.ResolvedIngredients` / `ResolvedResults` (`IReadOnlyList<ResolvedAmount>`) — Task 1; iterated in Task 4. `Category` / `Enabled` / `EnergyRequiredTicks` / `Id` are existing. Consistent.
- `ResourcePrototype.MiningTimeTicks` — Task 1 Step 3; read in Task 3 Step 4. `MinableResult` existing on both `EntityPrototype` and `ResourcePrototype`. Consistent.
- `Inventory.CanInsert(int, int, int) → bool` — Task 1 Step 6; called in Task 4 Step 4. Consistent.
- `CraftJob(int RecipeProtoId, int Count, long Progress)` `record struct` — Task 2 Step 1; used via `_craftQueue[0] with { … }` in `Player` (Task 2 Step 5) and read in Task 4 tests (`CraftQueue[0].Progress`). Consistent.
- `Player` API — `X`/`Y`/`WalkDir`/`Walking`/`Mining`/`MineTargetX`/`MineTargetY`/`MineProgress`/`CraftQueue`/`Inventory` (getters), `SetWalk`/`StopWalk`/`MoveTo`/`SetMineTarget`/`StopMining`/`TickMineProgress`/`ClearMineProgress`/`EnqueueCraft`/`TickCraftHeadProgress`/`CompleteOneCraftUnit`/`WriteState`/`WalkDelta` — all defined Task 2 Step 5; consumed in Task 2 Step 10 (`PlayerWalk`), Task 3 Step 4 (`PlayerMine`), Task 4 Step 4 (`PlayerCraft`), Task 5. Every name matches.
- `Simulation.DestroyEntityAt(EntityId, EntityPrototype)` — Task 2 Step 10.6; called in Task 3 Step 4. Consistent.
- `Simulation.Player` property — Task 2 Step 10.2; used in every later task's tests.
- `CommandType.MovePlayer/StopPlayer/MineStart/MineStop/CraftEnqueue` — Task 2 Step 2; switched on in Task 2/3/4 `Apply` additions and built in test helpers. Consistent.
- `ValueNoise.Isqrt(long) → int` — existing (P6); called Task 3 Step 4 with `ddx*ddx + ddy*ddy` where `ddx`/`ddy` are `long`. Consistent.
- `ResourceGrid.GetResourceAt(int,int) → ResourceCell` (`ResourceProtoId`, `Amount`, `IsEmpty`) / `Extract(int,int,int) → int` — existing (P6); used Task 3 Step 4. Consistent.
- `PrototypeRegistry` — `Get<T>(name)`, `TryGet<T>(name, out T)`, `TryGetById(int, out PrototypeBase)`, `GetById(int)`, `Count` — all exist. Used throughout. Consistent.
- `Units.SecondsToTicks(double) → int` — existing; Task 1 Step 11. `Units.SubTilesPerTile == 256` — existing; the plan uses the literal `256` / `>> 8` (matches). Consistent.
- Data item/entity names: `iron-plate`, `iron-ore`, `coal`, `wooden-chest` exist; `iron-gear-wheel` added Task 1. Recipe names `iron-plate` (smelting, existing), `iron-gear-wheel`/`wooden-chest` (crafting, added Task 1).

Consistent throughout.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-09-04-m1-plan5-player-hand-actions.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

**Which approach?**
