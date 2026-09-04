# M1 Plan 4 — 库存(ItemStack + Inventory)Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **状态: ✅ 已合并 main · 已验证** — 主线提交 `55539e8`..`58f65f1`(见 `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md` 进度快照)。下方 `- [ ]` 复选框为执行期工件,不代表当前状态。

**Goal:** Give the simulation layer a slot-based inventory (`ItemStack`, `Inventory`, `InventoryPool`, `EntityId → InventoryId` reverse lookup) and wire container entities to it (build on place, destroy on remove).

**Architecture:** Three new files under `sim/Faketorio.Sim/Items/`. `ItemStack` is a `readonly record struct` (item proto id + count, `Empty == default`). `Inventory` is a `sealed class` holding a fixed-length `ItemStack[]`, prototype-agnostic like `BeltLane` (stack size is passed in per call). `InventoryPool` is a deliberate parallel of `BeltLinePool` (generational-id pool for a reference type, since `EntityPool<T>` is `where T : struct`). `Inventories` is the container class that owns the pool plus two index-aligned sparse arrays (`_byEntity` keyed by `EntityId.Index`, `_ownerByIndex` keyed by pool index) and is the only place `Simulation` touches. `Simulation.Apply` gains container hooks in `PlaceEntity`/`RemoveEntity`; `Simulation.WriteState` appends `Inventories.WriteState` after `Belts.WriteState`. `Step` is unchanged — containers are passive in M1.

**Tech Stack:** C# / .NET 8, xUnit. No Godot dependency in `Faketorio.Sim`. Deterministic fixed-tick simulation — canonical serialization through `IStateWriter` / `Fnv1aHashWriter` (FNV-1a).

**Spec:** [`docs/superpowers/specs/2026-09-02-m1-plan4-inventory-design.md`](../specs/2026-09-02-m1-plan4-inventory-design.md)

## Global Constraints

- **Determinism 铁律:** fixed tick; no `float`/`double` in simulation state; no non-deterministic container iteration (no `Dictionary` enumeration in any `WriteState`); every new piece of state must be written into a `WriteState` reachable from `Simulation.WriteState`, or determinism tests cannot cover it.
- **Namespace:** all new production types live in `namespace Faketorio.Sim.Items;` under `sim/Faketorio.Sim/Items/`. Tests live in `namespace Faketorio.Sim.Tests;` under `sim/Faketorio.Sim.Tests/`.
- **Trust-the-precondition style:** match the existing belt code — validate at the command boundary (`Simulation.Apply`), not deep in `Inventory`. `Inventory`'s out-of-range slot index throws the native `IndexOutOfRangeException`; `Insert`/`Remove` guard only `count <= 0` (return 0, do not throw).
- **`ItemStack` invariant:** no code path produces `Count == 0 && ItemProtoId != 0`. `Insert` never writes a zero-count slot; `Remove` writes the whole `ItemStack.Empty` when a slot hits 0. So `slot == ItemStack.Empty` ⟺ `slot.IsEmpty`.
- **Pool parity:** `InventoryId` / `InventoryPool` mirror `BeltLineId` (`sim/Faketorio.Sim/Belts/BeltLineId.cs`) / `BeltLinePool` (`sim/Faketorio.Sim/Belts/BeltLinePool.cs`) member-for-member. `Invalid = new(-1, 0)`, `IsValid => Index >= 0`, generations even=dead / odd=alive.
- **Sparse-handle arrays:** `_byEntity` (`InventoryId[]`) and `_ownerByIndex` (`EntityId[]`) are generational-handle arrays. `Array.Resize` zero-fills, and `default(InventoryId)` `(0,0)` has `IsValid == true` — a latent wrong answer. Initial allocation **and every resize tail** must `Array.Fill(..., Invalid, oldLen, newLen - oldLen)`.
- **Commit trailer:** every commit message ends with:
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
  ```
- **Test command:** `dotnet test sim/Faketorio.Sim.Tests` from repo root. Single test: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~<ClassName>.<MethodName>"`.
- **Baseline:** `dotnet test` is green at 151 passing before Task 1.

## File Structure

| File | Responsibility |
|---|---|
| `sim/Faketorio.Sim/Items/ItemStack.cs` (create) | Value type: `(int ItemProtoId, int Count)`, `Empty`, `IsEmpty`. |
| `sim/Faketorio.Sim/Items/Inventory.cs` (create) | Fixed-length slot array; `Insert` / `Remove` / `CountOf` / `TotalItems` / indexer / `WriteState`. Prototype-agnostic. Exposes `ReadOnly` and `FilterItemProtoId` as read-only properties (the `Inventories` layer writes them into the hash). |
| `sim/Faketorio.Sim/Items/InventoryId.cs` (create) | Stable handle `(int Index, int Generation)`, `Invalid`, `IsValid`. |
| `sim/Faketorio.Sim/Items/InventoryPool.cs` (create) | Generational-id pool for `Inventory` (parallel of `BeltLinePool`). |
| `sim/Faketorio.Sim/Items/Inventories.cs` (create) | Owns `InventoryPool` + `_byEntity` + `_ownerByIndex`; `AddContainer` / `RemoveContainer` / `GetInventoryId` / `Get` / index-order accessors / `WriteState`. Only surface `Simulation` uses. |
| `sim/Faketorio.Sim/Simulation.cs` (modify) | Add `Inventories` property; container hooks in `Apply`; `Inventories.WriteState` in `WriteState`. |
| `sim/Faketorio.Sim.Tests/InventoryTests.cs` (create) | Unit tests for `Inventory`. |
| `sim/Faketorio.Sim.Tests/InventoryPoolTests.cs` (create) | Unit tests for `InventoryPool` (mirror `BeltLinePoolTests`). |
| `sim/Faketorio.Sim.Tests/InventoriesTests.cs` (create) | Unit tests for the `Inventories` container class — `RemoveContainer` return value, `_byEntity` resize + `Invalid` fill, pool-slot reuse generation bump. (See Ruling in Task 3.) |
| `sim/Faketorio.Sim.Tests/SimulationTests.cs` (modify) | Integration: place chest → inventory exists with right slot count; remove chest → inventory destroyed; remove non-container → `Inventories` untouched. |
| `sim/Faketorio.Sim.Tests/DeterminismTests.cs` (modify) | Inventory scenario 60-tick two-run equality; hash changes when an item is inserted. |

---

## Task 1: `ItemStack` + `Inventory` + unit tests

**Files:**
- Create: `sim/Faketorio.Sim/Items/ItemStack.cs`
- Create: `sim/Faketorio.Sim/Items/Inventory.cs`
- Test: `sim/Faketorio.Sim.Tests/InventoryTests.cs`

**Interfaces:**
- Consumes: `Faketorio.Sim.State.IStateWriter` (existing — `Write(byte)`, `Write(int)`, `Write(long)`), `Faketorio.Sim.State.Fnv1aHashWriter` (existing — `Hash` getter).
- Produces (later tasks rely on these exact signatures):
  - `readonly record struct ItemStack(int ItemProtoId, int Count)` with `static readonly ItemStack Empty` and `bool IsEmpty`.
  - `sealed class Inventory`:
    - `Inventory(int slotCount, bool readOnly = false, int filterItemProtoId = 0)`
    - `int SlotCount { get; }`
    - `bool ReadOnly { get; }`
    - `int FilterItemProtoId { get; }`
    - `ItemStack this[int slot] { get; }`
    - `int Insert(int itemProtoId, int count, int stackSize)` — returns actually inserted (`<= count`)
    - `int Remove(int itemProtoId, int count)` — returns actually removed (`<= count`)
    - `int CountOf(int itemProtoId)`
    - `int TotalItems()`
    - `void WriteState(IStateWriter writer)`

- [ ] **Step 1: Create `ItemStack.cs`**

```csharp
namespace Faketorio.Sim.Items;

// 一个物品堆:物品 prototype id + 数量。空槽 ⟺ Count == 0。
// 不变式:没有代码路径产生 Count == 0 && ItemProtoId != 0,
// 所以 (slot == Empty) 与 slot.IsEmpty 永远等价。堆叠上限不进 struct
// (它是 ItemPrototype.StackSize,由调用方传给 Inventory.Insert)。
public readonly record struct ItemStack(int ItemProtoId, int Count)
{
    public static readonly ItemStack Empty = default;   // (0, 0)
    public bool IsEmpty => Count == 0;
}
```

- [ ] **Step 2: Write the failing `Inventory` construction + indexer test**

Create `sim/Faketorio.Sim.Tests/InventoryTests.cs`:

```csharp
using Faketorio.Sim.Items;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class InventoryTests
{
    private const int Iron = 100;   // 任意 item proto id — Inventory 是 prototype 无关的
    private const int Copper = 200;
    private const int Stack = 50;

    private static ulong Hash(Inventory inv)
    {
        var w = new Fnv1aHashWriter();
        inv.WriteState(w);
        return w.Hash;
    }

    [Fact]
    public void NewInventory_HasRequestedSlotCount_AllEmpty()
    {
        var inv = new Inventory(4);
        Assert.Equal(4, inv.SlotCount);
        for (int i = 0; i < inv.SlotCount; i++)
            Assert.True(inv[i].IsEmpty);
        Assert.Equal(ItemStack.Empty, inv[0]);
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoryTests.NewInventory_HasRequestedSlotCount_AllEmpty"`
Expected: FAIL — `Inventory` does not exist (compile error).

- [ ] **Step 4: Create `Inventory.cs` with the full implementation**

```csharp
using Faketorio.Sim.State;

namespace Faketorio.Sim.Items;

// 定长槽位库存。prototype 无关(和 BeltLane 一样):堆叠上限每次调用由外面传。
// 越界索引抛原生 IndexOutOfRangeException(信任调用方);Insert/Remove 只
// 防御 count <= 0(返回 0,不抛)。
public sealed class Inventory
{
    private readonly ItemStack[] _slots;   // 定长,长度 = 构造时槽数

    public bool ReadOnly { get; }
    public int FilterItemProtoId { get; }  // 0 = 不过滤;>0 = 只收这种。Inventories 层写进哈希。

    public Inventory(int slotCount, bool readOnly = false, int filterItemProtoId = 0)
    {
        _slots = new ItemStack[slotCount];
        ReadOnly = readOnly;
        FilterItemProtoId = filterItemProtoId;
    }

    public int SlotCount => _slots.Length;

    public ItemStack this[int slot] => _slots[slot];

    // 先补持有 itemProtoId 的未满槽(封顶 stackSize),再占空槽(每槽最多 stackSize)。
    // ReadOnly 或过滤不匹配 → 0。返回实际放入数(<= count);调用方保留 count - 返回值。
    public int Insert(int itemProtoId, int count, int stackSize)
    {
        if (ReadOnly) return 0;
        if (FilterItemProtoId != 0 && itemProtoId != FilterItemProtoId) return 0;
        if (count <= 0) return 0;

        int remaining = count;

        // 第一轮:补同类未满槽
        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (_slots[i].ItemProtoId != itemProtoId || _slots[i].Count >= stackSize) continue;
            int space = stackSize - _slots[i].Count;
            int put = space < remaining ? space : remaining;
            _slots[i] = new ItemStack(itemProtoId, _slots[i].Count + put);
            remaining -= put;
        }

        // 第二轮:占空槽
        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (!_slots[i].IsEmpty) continue;
            int put = stackSize < remaining ? stackSize : remaining;
            _slots[i] = new ItemStack(itemProtoId, put);
            remaining -= put;
        }

        return count - remaining;
    }

    // 从前往后扣持有 itemProtoId 的槽,扣到 0 的槽整个置 ItemStack.Empty。
    // count <= 0 → 0(与 Insert 对称;缺了它 Remove(x,-5) 会扣 min(3,-5)=-5,
    // 槽数量反而增加、返回负,静默污染)。返回实际取出数(<= count)。
    public int Remove(int itemProtoId, int count)
    {
        if (count <= 0) return 0;

        int remaining = count;
        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (_slots[i].ItemProtoId != itemProtoId || _slots[i].Count == 0) continue;
            int take = _slots[i].Count < remaining ? _slots[i].Count : remaining;
            int left = _slots[i].Count - take;
            _slots[i] = left == 0 ? ItemStack.Empty : new ItemStack(itemProtoId, left);
            remaining -= take;
        }
        return count - remaining;
    }

    public int CountOf(int itemProtoId)
    {
        int total = 0;
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i].ItemProtoId == itemProtoId) total += _slots[i].Count;
        return total;
    }

    // 所有非空槽的 Count 之和(拆箱返回用)。
    public int TotalItems()
    {
        int total = 0;
        for (int i = 0; i < _slots.Length; i++) total += _slots[i].Count;
        return total;
    }

    // 写槽数前缀 + 逐槽 (ItemProtoId, Count)。不写 ReadOnly / FilterItemProtoId
    // ——那两个由 Inventories.WriteState 在实体层写一次(哈希防御)。
    public void WriteState(IStateWriter writer)
    {
        writer.Write(_slots.Length);
        for (int i = 0; i < _slots.Length; i++)
        {
            writer.Write(_slots[i].ItemProtoId);
            writer.Write(_slots[i].Count);
        }
    }
}
```

- [ ] **Step 5: Run the construction test to verify it passes**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoryTests.NewInventory_HasRequestedSlotCount_AllEmpty"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add sim/Faketorio.Sim/Items/ItemStack.cs sim/Faketorio.Sim/Items/Inventory.cs sim/Faketorio.Sim.Tests/InventoryTests.cs
git commit -m "$(cat <<'EOF'
feat(items): ItemStack value type + Inventory slot container

Prototype-agnostic fixed-length slot inventory. Insert fills partial
same-type stacks first then empty slots; Remove decrements front-to-back
and clears drained slots to ItemStack.Empty. count <= 0 guarded on both.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

- [ ] **Step 7: Write the `Insert` behavior tests**

Append to `InventoryTests.cs` (inside the class):

```csharp
    [Fact]
    public void Insert_IntoEmpty_FillsFirstSlot_ReturnsCount()
    {
        var inv = new Inventory(4);
        int put = inv.Insert(Iron, 10, Stack);
        Assert.Equal(10, put);
        Assert.Equal(new ItemStack(Iron, 10), inv[0]);
        Assert.True(inv[1].IsEmpty);
    }

    [Fact]
    public void Insert_TopsUpPartialSameTypeSlotBeforeEmptySlot()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 45, Stack);        // slot 0 -> 45
        int put = inv.Insert(Iron, 10, Stack);
        Assert.Equal(10, put);
        Assert.Equal(new ItemStack(Iron, 50), inv[0]);   // topped up to stackSize
        Assert.Equal(new ItemStack(Iron, 5), inv[1]);     // overflow to next slot
    }

    [Fact]
    public void Insert_CapsEachSlotAtStackSize()
    {
        var inv = new Inventory(4);
        int put = inv.Insert(Iron, 120, Stack);
        Assert.Equal(120, put);
        Assert.Equal(new ItemStack(Iron, 50), inv[0]);
        Assert.Equal(new ItemStack(Iron, 50), inv[1]);
        Assert.Equal(new ItemStack(Iron, 20), inv[2]);
    }

    [Fact]
    public void Insert_IntoFull_ReturnsOnlyWhatFit()
    {
        var inv = new Inventory(2);
        int put = inv.Insert(Iron, 250, Stack);   // capacity is 100
        Assert.Equal(100, put);
        Assert.Equal(100, inv.TotalItems());
    }

    [Fact]
    public void Insert_ReadOnly_ReturnsZero_NoStateChange()
    {
        var inv = new Inventory(4, readOnly: true);
        var h = Hash(inv);
        Assert.Equal(0, inv.Insert(Iron, 10, Stack));
        Assert.Equal(h, Hash(inv));
    }

    [Fact]
    public void Insert_FilterMismatch_ReturnsZero()
    {
        var inv = new Inventory(4, filterItemProtoId: Iron);
        Assert.Equal(0, inv.Insert(Copper, 10, Stack));
        Assert.Equal(10, inv.Insert(Iron, 10, Stack));
    }

    [Fact]
    public void Insert_NonPositiveCountOrStackSize_ReturnsZero_NoStateChange()
    {
        var inv = new Inventory(4);
        var h = Hash(inv);
        Assert.Equal(0, inv.Insert(Iron, -1, Stack));
        Assert.Equal(0, inv.Insert(Iron, 0, Stack));
        Assert.Equal(0, inv.Insert(Iron, 10, 0));      // stackSize 0: nothing fits
        Assert.Equal(h, Hash(inv));
    }
```

- [ ] **Step 8: Run the `Insert` tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoryTests.Insert"`
Expected: all PASS (implementation already written in Step 4).

- [ ] **Step 9: Write the `Remove` / `CountOf` / `TotalItems` tests**

Append to `InventoryTests.cs`:

```csharp
    [Fact]
    public void Remove_PartialFromSingleSlot()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 30, Stack);
        int got = inv.Remove(Iron, 10);
        Assert.Equal(10, got);
        Assert.Equal(new ItemStack(Iron, 20), inv[0]);
    }

    [Fact]
    public void Remove_DrainsSlotToEmpty()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 10, Stack);
        int got = inv.Remove(Iron, 10);
        Assert.Equal(10, got);
        Assert.Equal(ItemStack.Empty, inv[0]);
        Assert.True(inv[0].IsEmpty);
    }

    [Fact]
    public void Remove_SpansMultipleSlots_FrontToBack()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 120, Stack);            // slots: 50 / 50 / 20
        int got = inv.Remove(Iron, 75);
        Assert.Equal(75, got);
        Assert.Equal(ItemStack.Empty, inv[0]);
        Assert.Equal(new ItemStack(Iron, 25), inv[1]);
        Assert.Equal(new ItemStack(Iron, 20), inv[2]);
    }

    [Fact]
    public void Remove_MoreThanPresent_ReturnsOnlyWhatWasThere()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 15, Stack);
        Assert.Equal(15, inv.Remove(Iron, 999));
        Assert.Equal(0, inv.TotalItems());
    }

    [Fact]
    public void Remove_WrongType_ReturnsZero()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 15, Stack);
        Assert.Equal(0, inv.Remove(Copper, 5));
        Assert.Equal(15, inv.CountOf(Iron));
    }

    [Fact]
    public void Remove_NonPositiveCount_ReturnsZero_NoStateChange()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 3, Stack);
        var h = Hash(inv);
        Assert.Equal(0, inv.Remove(Iron, -5));
        Assert.Equal(0, inv.Remove(Iron, 0));
        Assert.Equal(h, Hash(inv));
        Assert.Equal(3, inv.CountOf(Iron));   // 没有变成 3 - (-5)
    }

    [Fact]
    public void CountOf_SumsAcrossSlots()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 120, Stack);         // 50 / 50 / 20
        Assert.Equal(120, inv.CountOf(Iron));
        Assert.Equal(0, inv.CountOf(Copper));
    }

    [Fact]
    public void TotalItems_SumsEveryNonEmptySlot()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 60, Stack);
        inv.Insert(Copper, 10, Stack);
        Assert.Equal(70, inv.TotalItems());
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var inv = new Inventory(2);
        Assert.Throws<IndexOutOfRangeException>(() => inv[5]);
    }
```

- [ ] **Step 10: Run the `Remove` / query tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoryTests"`
Expected: all PASS.

- [ ] **Step 11: Write the `WriteState` tests**

Append to `InventoryTests.cs`:

```csharp
    [Fact]
    public void WriteState_SameSlotContents_SameHash()
    {
        var a = new Inventory(4);
        var b = new Inventory(4);
        a.Insert(Iron, 30, Stack);
        b.Insert(Iron, 30, Stack);
        Assert.Equal(Hash(a), Hash(b));
    }

    [Fact]
    public void WriteState_DifferentSlotContents_DifferentHash()
    {
        var a = new Inventory(4);
        var b = new Inventory(4);
        a.Insert(Iron, 30, Stack);
        b.Insert(Iron, 31, Stack);
        Assert.NotEqual(Hash(a), Hash(b));
    }

    [Fact]
    public void WriteState_DifferentSlotCount_DifferentHash()
    {
        Assert.NotEqual(Hash(new Inventory(4)), Hash(new Inventory(5)));
    }
```

- [ ] **Step 12: Run the full `Inventory` suite**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoryTests"`
Expected: all PASS.

- [ ] **Step 13: Run the whole test suite (no regressions)**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS, count = 151 + new `InventoryTests` cases.

- [ ] **Step 14: Commit**

```bash
git add sim/Faketorio.Sim.Tests/InventoryTests.cs
git commit -m "$(cat <<'EOF'
test(items): Inventory insert/remove/query/WriteState coverage

Partial-stack top-up, stackSize cap, overflow return value, ReadOnly and
filter rejection, count <= 0 guards on both Insert and Remove, front-to-back
Remove across slots, WriteState hash sensitivity.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

---

## Task 2: `InventoryId` + `InventoryPool` + unit tests

**Files:**
- Create: `sim/Faketorio.Sim/Items/InventoryId.cs`
- Create: `sim/Faketorio.Sim/Items/InventoryPool.cs`
- Test: `sim/Faketorio.Sim.Tests/InventoryPoolTests.cs`

**Interfaces:**
- Consumes: `Inventory` (Task 1), `IStateWriter` (existing).
- Produces (Task 3 relies on these):
  - `readonly record struct InventoryId(int Index, int Generation)` with `static readonly InventoryId Invalid = new(-1, 0)` and `bool IsValid => Index >= 0`.
  - `sealed class InventoryPool`:
    - `InventoryPool(int initialCapacity = 64)`
    - `int Capacity { get; }` — high-water mark, for `[0, Capacity)` index-order iteration
    - `InventoryId Create(Inventory inv)`
    - `void Destroy(InventoryId id)` — throws `InvalidOperationException` on a dead id
    - `bool IsAlive(InventoryId id)`
    - `Inventory Get(InventoryId id)` — throws `InvalidOperationException` on a dead id
    - `bool IsAliveAtIndex(int index)`
    - `Inventory GetAtIndex(int index)`
    - `int GenerationAtIndex(int index)`
    - `void WriteState(IStateWriter writer)`

> **Optional (not required):** the spec (§5, §9) notes the allocator bookkeeping is now triplicated (`EntityPool<T>`, `BeltLinePool`, `InventoryPool`). Extracting a shared non-generic `GenerationTable` is allowed here but explicitly optional — skip it unless the duplication is bad enough to be worth a separate reviewable change. This plan implements `InventoryPool` standalone.

- [ ] **Step 1: Create `InventoryId.cs`**

```csharp
namespace Faketorio.Sim.Items;

// 一个 Inventory 的稳定句柄:池槽位 + 代数。用前校验代数一致,拦截"指向
// 已删除并被复用的槽位"。与 Belts 的 BeltLineId 同构(库存有独立的池)。
public readonly record struct InventoryId(int Index, int Generation)
{
    public static readonly InventoryId Invalid = new(-1, 0);
    public bool IsValid => Index >= 0;
}
```

- [ ] **Step 2: Write the failing pool tests**

Create `sim/Faketorio.Sim.Tests/InventoryPoolTests.cs`:

```csharp
using Faketorio.Sim.Items;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class InventoryPoolTests
{
    private static Inventory MakeInv() => new(4);

    [Fact]
    public void CreateThenGet_ReturnsSameInstance()
    {
        var pool = new InventoryPool();
        var inv = MakeInv();
        var id = pool.Create(inv);
        Assert.True(pool.IsAlive(id));
        Assert.Same(inv, pool.Get(id));
    }

    [Fact]
    public void Destroy_MakesIdStale()
    {
        var pool = new InventoryPool();
        var id = pool.Create(MakeInv());
        pool.Destroy(id);
        Assert.False(pool.IsAlive(id));
        Assert.Throws<InvalidOperationException>(() => pool.Get(id));
    }

    [Fact]
    public void ReusedSlot_BumpsGeneration_StaleIdStaysDead()
    {
        var pool = new InventoryPool();
        var a = pool.Create(MakeInv());
        pool.Destroy(a);
        var b = pool.Create(MakeInv());
        Assert.Equal(a.Index, b.Index);
        Assert.NotEqual(a.Generation, b.Generation);
        Assert.False(pool.IsAlive(a));
        Assert.True(pool.IsAlive(b));
    }

    [Fact]
    public void IndexOrderIteration_SeesLiveSlotsOnly()
    {
        var pool = new InventoryPool();
        var a = pool.Create(MakeInv());
        var b = pool.Create(MakeInv());
        var c = pool.Create(MakeInv());
        pool.Destroy(b);

        var seen = new List<int>();
        for (int i = 0; i < pool.Capacity; i++)
            if (pool.IsAliveAtIndex(i)) seen.Add(i);

        Assert.Equal(new[] { a.Index, c.Index }, seen);
    }

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        var pool = new InventoryPool(initialCapacity: 2);
        for (int i = 0; i < 50; i++) pool.Create(MakeInv());
        Assert.True(pool.Capacity >= 50);
    }

    private static ulong Hash(InventoryPool pool)
    {
        var w = new Fnv1aHashWriter();
        pool.WriteState(w);
        return w.Hash;
    }

    [Fact]
    public void WriteState_StableThenChangesWithAllocatorState()
    {
        var pool = new InventoryPool();
        var a = pool.Create(MakeInv());
        var b = pool.Create(MakeInv());
        pool.Destroy(a);
        pool.Destroy(b);

        var h1 = Hash(pool);
        Assert.Equal(h1, Hash(pool));

        pool.Create(MakeInv());
        Assert.NotEqual(h1, Hash(pool));
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoryPoolTests"`
Expected: FAIL — `InventoryPool` does not exist (compile error).

- [ ] **Step 4: Create `InventoryPool.cs`**

Mirror of `sim/Faketorio.Sim/Belts/BeltLinePool.cs` with `BeltLine` → `Inventory`, `BeltLineId` → `InventoryId`:

```csharp
using Faketorio.Sim.State;

namespace Faketorio.Sim.Items;

// 装 Inventory(引用类型)的代数 ID 池。刻意平行于 BeltLinePool——
// EntityPool<T> 约束 where T : struct,容不下 Inventory。确定性簿记
// (代数数组 + 空闲栈 + 高水位)只在这一处维护。
public sealed class InventoryPool
{
    private Inventory?[] _data;
    private int[] _generations;   // 偶数=空槽,奇数=存活(create+destroy 各 +1)
    private int[] _freeStack;
    private int _freeCount;
    private int _count;

    public InventoryPool(int initialCapacity = 64)
    {
        _data = new Inventory?[initialCapacity];
        _generations = new int[initialCapacity];
        _freeStack = new int[initialCapacity];
    }

    // 高水位:已用过的最大槽位数。按索引序遍历 [0, Capacity) 用。
    public int Capacity => _count;

    public InventoryId Create(Inventory inv)
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
        _data[index] = inv;
        return new InventoryId(index, _generations[index]);
    }

    public void Destroy(InventoryId id)
    {
        if (!IsAlive(id)) throw new InvalidOperationException($"Destroy on dead InventoryId {id}");
        _generations[id.Index]++; // 奇 -> 偶:空槽
        _data[id.Index] = null;
        if (_freeCount == _freeStack.Length) Array.Resize(ref _freeStack, _freeStack.Length * 2);
        _freeStack[_freeCount++] = id.Index;
    }

    public bool IsAlive(InventoryId id)
        => id.Index >= 0 && id.Index < _count && _generations[id.Index] == id.Generation
           && (id.Generation & 1) == 1;

    public Inventory Get(InventoryId id)
    {
        if (!IsAlive(id)) throw new InvalidOperationException($"Get on dead InventoryId {id}");
        return _data[id.Index]!;
    }

    // 按索引序确定遍历(状态哈希用)。
    public bool IsAliveAtIndex(int index) => (_generations[index] & 1) == 1;
    public Inventory GetAtIndex(int index) => _data[index]!;
    public int GenerationAtIndex(int index) => _generations[index];

    // 分配器簿记(高水位/空闲栈/全部代数,含死槽)。参照 BeltLinePool.WriteState。
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

- [ ] **Step 5: Run the pool tests to verify they pass**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoryPoolTests"`
Expected: all PASS.

- [ ] **Step 6: Run the whole suite (no regressions)**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS, count = Task 1 total + `InventoryPoolTests` cases.

- [ ] **Step 7: Commit**

```bash
git add sim/Faketorio.Sim/Items/InventoryId.cs sim/Faketorio.Sim/Items/InventoryPool.cs sim/Faketorio.Sim.Tests/InventoryPoolTests.cs
git commit -m "$(cat <<'EOF'
feat(items): InventoryId + InventoryPool generational-id pool

Deliberate parallel of BeltLinePool (EntityPool<T> is struct-constrained).
Even generation = dead slot, odd = alive; free-stack slot reuse bumps
generation so stale handles stay dead. WriteState serializes allocator
bookkeeping for deterministic Create() ordering.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

---

## Task 3: `Inventories` container + Simulation wiring + integration/determinism tests

**Files:**
- Create: `sim/Faketorio.Sim/Items/Inventories.cs`
- Modify: `sim/Faketorio.Sim/Simulation.cs`
- Create: `sim/Faketorio.Sim.Tests/InventoriesTests.cs`
- Modify: `sim/Faketorio.Sim.Tests/SimulationTests.cs`
- Modify: `sim/Faketorio.Sim.Tests/DeterminismTests.cs`

**Interfaces:**
- Consumes: `InventoryPool`, `InventoryId` (Task 2), `Inventory` (Task 1), `Faketorio.Sim.Entities.EntityId` (existing — `readonly record struct (int Index, int Generation)`, `Invalid = new(-1, 0)`), `Faketorio.Sim.Prototypes.ContainerPrototype` (existing — `int InventorySize { get; init; }`), `IStateWriter`.
- Produces:
  - `sealed class Inventories`:
    - `Inventories(int initialCapacity = 64)`
    - `InventoryId AddContainer(EntityId entity, int slotCount)` — precondition: entity has no inventory yet
    - `int RemoveContainer(EntityId entity)` — precondition: entity has an inventory; returns `TotalItems()` at removal
    - `InventoryId GetInventoryId(EntityId entity)` — `Invalid` if none
    - `Inventory Get(InventoryId id)`
    - `int Capacity { get; }`
    - `bool IsAliveAtIndex(int index)`
    - `Inventory GetAtIndex(int index)`
    - `void WriteState(IStateWriter writer)`
  - `Simulation.Inventories` — `public Inventories Inventories { get; }`

**Ruling (plan interpretation of spec §8/§9):** spec §8 lists every new test under "`SimulationTests` 追加", but two of them — `RemoveContainer` returning the item total, and the `_byEntity` resize + `Invalid`-fill path — assert on the `Inventories` class directly (the return value is discarded inside `Simulation.Apply`; the resize needs a small `initialCapacity` the `Simulation`-owned instance doesn't expose). This plan puts those in a dedicated `InventoriesTests.cs` (matching how the codebase already separates `BeltLinePoolTests` from `SimulationTests`), and keeps the genuinely end-to-end cases in `SimulationTests` / `DeterminismTests`. Total coverage is a superset of §8.

- [ ] **Step 1: Write the failing `Inventories` unit tests**

Create `sim/Faketorio.Sim.Tests/InventoriesTests.cs`:

```csharp
using Faketorio.Sim.Entities;
using Faketorio.Sim.Items;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class InventoriesTests
{
    private const int Iron = 100;
    private const int Stack = 50;

    [Fact]
    public void AddContainer_BindsEntity_GetReturnsRequestedSize()
    {
        var inv = new Inventories();
        var e = new EntityId(7, 1);
        var id = inv.AddContainer(e, 16);

        Assert.True(id.IsValid);
        Assert.Equal(id, inv.GetInventoryId(e));
        Assert.Equal(16, inv.Get(id).SlotCount);
    }

    [Fact]
    public void GetInventoryId_UnknownEntity_ReturnsInvalid()
    {
        var inv = new Inventories();
        Assert.Equal(InventoryId.Invalid, inv.GetInventoryId(new EntityId(3, 1)));
        Assert.False(inv.GetInventoryId(new EntityId(3, 1)).IsValid);
    }

    [Fact]
    public void RemoveContainer_DestroysInventory_ReturnsItemTotalAtRemoval()
    {
        var inv = new Inventories();
        var e = new EntityId(2, 1);
        var id = inv.AddContainer(e, 16);
        inv.Get(id).Insert(Iron, 70, Stack);   // 50 + 20 across two slots

        int returned = inv.RemoveContainer(e);

        Assert.Equal(70, returned);
        Assert.Equal(InventoryId.Invalid, inv.GetInventoryId(e));
        Assert.Throws<InvalidOperationException>(() => inv.Get(id));  // pool slot destroyed
    }

    [Fact]
    public void RemoveThenAddContainer_ReusesPoolSlot_BumpsGeneration()
    {
        var inv = new Inventories();
        var e1 = new EntityId(1, 1);
        var first = inv.AddContainer(e1, 16);
        inv.Get(first).Insert(Iron, 10, Stack);
        inv.RemoveContainer(e1);

        var e2 = new EntityId(4, 1);
        var second = inv.AddContainer(e2, 16);

        Assert.Equal(first.Index, second.Index);          // slot reused
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.Equal(0, inv.Get(second).TotalItems());    // fresh inventory, not the old contents
    }

    [Fact]
    public void ByEntity_GrowsToHighEntityIndex_EarlierUnboundEntitiesStayInvalid()
    {
        var inv = new Inventories(initialCapacity: 4);   // force a resize
        var high = new EntityId(500, 1);
        inv.AddContainer(high, 16);

        // an earlier index that was never bound must NOT look like it points at pool slot 0
        Assert.Equal(InventoryId.Invalid, inv.GetInventoryId(new EntityId(1, 1)));
        Assert.False(inv.GetInventoryId(new EntityId(300, 1)).IsValid);
        Assert.True(inv.GetInventoryId(high).IsValid);
    }

    private static ulong Hash(Inventories inv)
    {
        var w = new Fnv1aHashWriter();
        inv.WriteState(w);
        return w.Hash;
    }

    [Fact]
    public void WriteState_ChangesWhenBoundOwnerDiffers()
    {
        var a = new Inventories();
        a.AddContainer(new EntityId(1, 1), 4);
        var b = new Inventories();
        b.AddContainer(new EntityId(2, 1), 4);   // same inventory shape, different owner
        Assert.NotEqual(Hash(a), Hash(b));
    }
```

Close the class with `}`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoriesTests"`
Expected: FAIL — `Inventories` does not exist (compile error).

- [ ] **Step 3: Create `Inventories.cs`**

```csharp
using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Items;

// 库存的实体层门面:持有 InventoryPool + 两张索引对齐的稀疏表。
// _byEntity 以 EntityId.Index 为键(实体 -> 库存),_ownerByIndex 以池索引
// 为键(库存 -> 实体),互为反向。只点查、不遍历,确定性不受影响。
public sealed class Inventories
{
    private readonly InventoryPool _pool = new();
    private InventoryId[] _byEntity;      // 缺省 InventoryId.Invalid
    private EntityId[] _ownerByIndex;     // 缺省 EntityId.Invalid;与 _pool 索引对齐

    public Inventories(int initialCapacity = 64)
    {
        _byEntity = new InventoryId[initialCapacity];
        Array.Fill(_byEntity, InventoryId.Invalid);
        _ownerByIndex = new EntityId[initialCapacity];
        Array.Fill(_ownerByIndex, EntityId.Invalid);
    }

    // 建一个 slotCount 槽的库存并绑给 entity。前置:该 entity 尚未有库存。
    public InventoryId AddContainer(EntityId entity, int slotCount)
    {
        var id = _pool.Create(new Inventory(slotCount));
        EnsureByEntity(entity.Index);
        _byEntity[entity.Index] = id;
        EnsureOwnerByIndex(id.Index);
        _ownerByIndex[id.Index] = entity;
        return id;
    }

    // 毁掉 entity 的库存,返回它当时的物品总数。前置:该 entity 有库存。
    // 只用 entity.Index 索引 _byEntity,不解引用实体本身——Simulation 在
    // Entities.Destroy(id) 之后调用它是安全的。
    public int RemoveContainer(EntityId entity)
    {
        var id = _byEntity[entity.Index];
        int total = _pool.Get(id).TotalItems();
        _pool.Destroy(id);
        _byEntity[entity.Index] = InventoryId.Invalid;
        _ownerByIndex[id.Index] = EntityId.Invalid;
        return total;
    }

    public InventoryId GetInventoryId(EntityId entity)
        => entity.Index >= 0 && entity.Index < _byEntity.Length
            ? _byEntity[entity.Index]
            : InventoryId.Invalid;

    public Inventory Get(InventoryId id) => _pool.Get(id);

    public int Capacity => _pool.Capacity;
    public bool IsAliveAtIndex(int index) => _pool.IsAliveAtIndex(index);
    public Inventory GetAtIndex(int index) => _pool.GetAtIndex(index);

    // 先写池分配器簿记,再按池索引序对每个存活库存写:
    // i / 代数 / 所属 EntityId(Index 再 Generation)/ ReadOnly(byte)/
    // FilterItemProtoId(int)/ inv.WriteState(自带槽数前缀 + 槽内容)。
    // 写所属 EntityId:哈希防御(误绑会被抓到)+ 给 M2 存读档留绑定锚。
    public void WriteState(IStateWriter writer)
    {
        _pool.WriteState(writer);
        for (int i = 0; i < _pool.Capacity; i++)
        {
            if (!_pool.IsAliveAtIndex(i)) continue;
            var item = _pool.GetAtIndex(i);
            var owner = _ownerByIndex[i];
            writer.Write(i);
            writer.Write(_pool.GenerationAtIndex(i));
            writer.Write(owner.Index);
            writer.Write(owner.Generation);
            writer.Write((byte)(item.ReadOnly ? 1 : 0));
            writer.Write(item.FilterItemProtoId);
            item.WriteState(writer);
        }
    }

    // Array.Resize 是零填充,default(InventoryId) = (0,0) 且 (0,0).IsValid == true
    // ——每次扩容的新增段必须显式填 Invalid,否则未绑定的实体会"看起来指向池索引 0"。
    private void EnsureByEntity(int index)
    {
        if (index < _byEntity.Length) return;
        int oldLen = _byEntity.Length;
        int newLen = oldLen == 0 ? 1 : oldLen;
        while (newLen <= index) newLen *= 2;
        Array.Resize(ref _byEntity, newLen);
        Array.Fill(_byEntity, InventoryId.Invalid, oldLen, newLen - oldLen);
    }

    private void EnsureOwnerByIndex(int index)
    {
        if (index < _ownerByIndex.Length) return;
        int oldLen = _ownerByIndex.Length;
        int newLen = oldLen == 0 ? 1 : oldLen;
        while (newLen <= index) newLen *= 2;
        Array.Resize(ref _ownerByIndex, newLen);
        Array.Fill(_ownerByIndex, EntityId.Invalid, oldLen, newLen - oldLen);
    }
}
```

- [ ] **Step 4: Run the `Inventories` unit tests to verify they pass**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~InventoriesTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add sim/Faketorio.Sim/Items/Inventories.cs sim/Faketorio.Sim.Tests/InventoriesTests.cs
git commit -m "$(cat <<'EOF'
feat(items): Inventories entity-layer facade over InventoryPool

_byEntity (EntityId.Index -> InventoryId) and _ownerByIndex (pool index ->
EntityId) sparse tables, both explicitly Array.Fill'd with Invalid after
every resize (Array.Resize zero-fills, and (0,0).IsValid is true).
RemoveContainer returns the item total and only touches entity.Index, so
it is safe to call after Entities.Destroy. WriteState records the owning
EntityId per inventory as hash defense.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

- [ ] **Step 6: Write the failing Simulation integration tests**

Append to `sim/Faketorio.Sim.Tests/SimulationTests.cs` (inside the `SimulationTests` class; `PlaceChest`, `PlaceLargeChest`, `PlaceBelt`, `LiveLineCount` helpers already exist in the file):

```csharp
    [Fact]
    public void PlaceChest_CreatesInventory_WithSixteenSlots()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 3, 4));
        sim.Step();
        var e = sim.World.GetEntityAt(3, 4);
        var invId = sim.Inventories.GetInventoryId(e);
        Assert.True(invId.IsValid);
        Assert.Equal(16, sim.Inventories.Get(invId).SlotCount);
    }

    [Fact]
    public void PlaceLargeChest_CreatesInventory_WithFortyEightSlots()
    {
        var sim = NewSim();
        sim.Submit(PlaceLargeChest(sim, -33, -33));
        sim.Step();
        var e = sim.World.GetEntityAt(-33, -33);
        var invId = sim.Inventories.GetInventoryId(e);
        Assert.True(invId.IsValid);
        Assert.Equal(48, sim.Inventories.Get(invId).SlotCount);
    }

    [Fact]
    public void RemoveChest_DestroysInventory()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 0, 0));
        sim.Step();
        var e = sim.World.GetEntityAt(0, 0);
        var invId = sim.Inventories.GetInventoryId(e);

        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 0, Y = 0 });
        sim.Step();

        Assert.Equal(InventoryId.Invalid, sim.Inventories.GetInventoryId(e));
        Assert.False(sim.Inventories.IsAliveAtIndex(invId.Index));
        Assert.Equal(0, sim.RejectedCommandCount);
    }

    [Fact]
    public void RemoveNonContainerEntity_DoesNotTouchInventories()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 5, E));
        sim.Step();
        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 5, Y = 5 });
        sim.Step();   // isContainer 守卫:不得抛
        Assert.Equal(0, sim.Inventories.Capacity);
        Assert.Equal(0, sim.RejectedCommandCount);
    }
```

Add the required `using` at the top of `SimulationTests.cs` if not already present:

```csharp
using Faketorio.Sim.Items;
```

- [ ] **Step 7: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests.PlaceChest_CreatesInventory_WithSixteenSlots"`
Expected: FAIL — `Simulation.Inventories` does not exist (compile error).

- [ ] **Step 8: Wire `Inventories` into `Simulation`**

In `sim/Faketorio.Sim/Simulation.cs`:

1. Add `using Faketorio.Sim.Items;` to the using block.

2. Add the property right after the `Belts` property (line 15). Include the layering note:

```csharp
    public BeltNetwork Belts { get; } = new();

    // 分层说明:BeltNetwork 刻意"不知道 Simulation / Entities";Inventories
    // 反过来直接收 EntityId。这是有意的偏差——EntityId 只是个裸 readonly
    // record struct(不依赖 EntityPool),拿它当键不引入对实体池的依赖。
    public Inventories Inventories { get; } = new();
```

3. In `Apply` → `case CommandType.PlaceEntity`, after `World.OccupyArea(...)` and the existing belt hook, add the container hook (order belt → container, matching RemoveEntity):

```csharp
                World.OccupyArea(command.X, command.Y, proto.TileWidth, proto.TileHeight, id);
                if (proto is TransportBeltPrototype)
                    Belts.AddBelt(command.X, command.Y, command.Rotation);
                if (proto is ContainerPrototype cp)
                    Inventories.AddContainer(id, cp.InventorySize);
                return;
```

4. In `Apply` → `case CommandType.RemoveEntity`, capture `isContainer` next to `isBelt` (before `Entities.Destroy`), and add the removal hook after the belt hook:

```csharp
                int bx = data.X, by = data.Y;
                bool isBelt = proto is TransportBeltPrototype;
                bool isContainer = proto is ContainerPrototype;
                World.ClearArea(data.X, data.Y, proto.TileWidth, proto.TileHeight);
                Entities.Destroy(id);
                if (isBelt)
                    Belts.RemoveBelt(bx, by);
                if (isContainer)
                    Inventories.RemoveContainer(id);   // M1: 返回的物品总数丢弃(策略层 P5 起再定)
                return;
```

5. In `WriteState`, after `Belts.WriteState(writer);` (line 98):

```csharp
        Belts.WriteState(writer);
        Inventories.WriteState(writer);
```

- [ ] **Step 9: Run the Simulation integration tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests"`
Expected: all PASS (existing + 4 new).

- [ ] **Step 10: Commit**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "$(cat <<'EOF'
feat(sim): wire Inventories into Simulation — container place/remove hooks

PlaceEntity on a ContainerPrototype calls Inventories.AddContainer with
cp.InventorySize; RemoveEntity captures isContainer before Entities.Destroy
then calls RemoveContainer (discarded item total = M1 discard policy).
WriteState appends Inventories.WriteState after Belts. Step unchanged —
containers are passive in M1.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

- [ ] **Step 11: Write the failing determinism tests**

Append to `sim/Faketorio.Sim.Tests/DeterminismTests.cs` (inside the `DeterminismTests` class):

```csharp
    // 箱子 + 物品插入 + 拆除的代表性场景。物品直接经 Inventories 写入
    // (不走命令)——与 RunBeltScenario 里直接调 LaneA.TryInsertAtBack 同理。
    private static List<ulong> RunInventoryScenario()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"));
        int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        var hashes = new List<ulong>();

        for (int t = 0; t < 60; t++)
        {
            if (t == 0) sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 2, Y = 2 });
            if (t == 1) sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 5, Y = 2 });
            if (t == 40) sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 5, Y = 2 });

            if (t is >= 2 and < 40 && t % 4 == 2)
            {
                var a = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(2, 2)));
                a.Insert(plate, 7, plateStack);
                var b = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(5, 2)));
                b.Insert(coal, 3, coalStack);
            }
            if (t is >= 10 and < 40 && t % 9 == 1)
            {
                var a = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(2, 2)));
                a.Remove(plate, 5);
            }

            sim.Step();
            hashes.Add(sim.ComputeStateHash());
        }
        return hashes;
    }

    [Fact]
    public void InventoryScenario_SameCommands_SameHashEveryTick()
    {
        Assert.Equal(RunInventoryScenario(), RunInventoryScenario());
    }

    [Fact]
    public void HashChangesWhenItemInserted()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"));
        int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;

        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 1, Y = 1 });
        sim.Step();
        var before = sim.ComputeStateHash();

        var inv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(1, 1)));
        inv.Insert(plate, 5, plateStack);

        Assert.NotEqual(before, sim.ComputeStateHash());
    }
```

`DeterminismTests.cs` already has `using Faketorio.Sim.Commands;` and `using Faketorio.Sim.Prototypes;`. Add if missing:

```csharp
using Faketorio.Sim.Items;
```

(Only needed if a symbol requires it — `ItemPrototype` / `ContainerPrototype` are in `Faketorio.Sim.Prototypes`, already imported, so this may be unnecessary. Add only if the build complains.)

- [ ] **Step 12: Run the determinism tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~DeterminismTests"`
Expected: all PASS (existing golden scenarios unchanged — `RunScenario` / `RunBeltScenario` were not modified; `Inventories.WriteState` on an empty `Inventories` only appends `_count=0`, `_freeCount=0`, so their hashes shift but both runs shift identically and the tests are relative comparisons).

- [ ] **Step 13: Run the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. Count = 151 + all new `InventoryTests` + `InventoryPoolTests` + `InventoriesTests` + 4 `SimulationTests` + 2 `DeterminismTests`.

- [ ] **Step 14: Commit**

```bash
git add sim/Faketorio.Sim.Tests/DeterminismTests.cs
git commit -m "$(cat <<'EOF'
test(sim): determinism coverage for inventory state

60-tick chest place / item insert+remove / chest remove scenario, two runs
hash-equal every tick. Plus HashChangesWhenItemInserted — proves slot
contents actually reach the state writer.

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
| §3 `ItemStack` (fields, `Empty`, `IsEmpty`, invariant, `stackSize > 0` precondition) | Task 1 Step 1; `count <= 0` / `stackSize` guards Task 1 Step 4; tests Step 7 (`Insert_NonPositiveCountOrStackSize`) |
| §4 `Inventory` (ctor, `SlotCount`, indexer, `Insert`, `Remove`, `CountOf`, `TotalItems`, `WriteState`) | Task 1 Step 4; `FilterItemProtoId`/`ReadOnly` as public getters for §4.3 cross-layer write |
| §4.1 `Insert` semantics (readonly → 0, filter → 0, `count<=0` → 0, pass 1 top-up, pass 2 empty slots) | Task 1 Step 4 + tests Steps 7–8 |
| §4.2 `Remove` semantics (`count<=0` guard + corruption note, front-to-back, clear to `Empty`) | Task 1 Step 4 + tests Steps 9–10 (`Remove_NonPositiveCount_ReturnsZero_NoStateChange`) |
| §4.3 `WriteState` (`_slots.Length` prefix + per-slot; no `ReadOnly`/filter here) | Task 1 Step 4 + tests Step 11 |
| §5 `InventoryId` + `InventoryPool` (parallel of `BeltLinePool`, all members) | Task 2 Steps 1, 4 + tests Step 2 |
| §5 optional `GenerationTable` extraction | Task 2 note — explicitly optional, not implemented |
| §6 `Inventories` (`_pool`, `_byEntity`, `_ownerByIndex`, `AddContainer`, `RemoveContainer` returns `TotalItems`, `GetInventoryId`, `Get`, index-order accessors, `WriteState` with owner `EntityId` + `ReadOnly` + filter) | Task 3 Step 3 + tests Step 1 |
| §6 `_byEntity` / `_ownerByIndex` `Array.Fill(Invalid)` after every resize | Task 3 Step 3 (`EnsureByEntity` / `EnsureOwnerByIndex`) + test `ByEntity_GrowsToHighEntityIndex_...` |
| §6 Simulation: `Inventories` property + layering note | Task 3 Step 8.2 |
| §6 Simulation: `PlaceEntity` container hook | Task 3 Step 8.3 |
| §6 Simulation: `RemoveEntity` — capture `isContainer` before `Destroy`, call after; belt → container order | Task 3 Step 8.4 |
| §6 Simulation: `WriteState` appends `Inventories.WriteState` after `Belts` | Task 3 Step 8.5 |
| §6 `Step` unchanged | Not modified — confirmed in Task 3 Step 8 (no `Step` edit) |
| §6 multi-tile chest `InventorySize=48` unrelated to footprint | Task 3 Step 6 `PlaceLargeChest_CreatesInventory_WithFortyEightSlots` |
| §7 determinism (index-order traversal, no `Dictionary` iteration; `Simulation.WriteState` fixed position; existing relative tests unaffected) | Task 3 Step 3 `WriteState` (index loop) + Step 12 note |
| §7 M2 save/load out of scope | No task — correctly excluded |
| §8 `InventoryTests` | Task 1 Steps 2, 7, 9, 11 |
| §8 `InventoryPoolTests` (mirror `BeltLinePoolTests`) | Task 2 Step 2 |
| §8 `SimulationTests` additions (place chest → valid + 16 slots; large-chest → 48; remove → destroyed; remove non-container no-op) | Task 3 Step 6 |
| §8 `_byEntity` resize path test | Task 3 Step 1 `ByEntity_GrowsToHighEntityIndex_...` (Ruling: in `InventoriesTests`) |
| §8 `Inventories`-layer slot reuse (generation bump) test | Task 3 Step 1 `RemoveThenAddContainer_ReusesPoolSlot_BumpsGeneration` |
| §8 `RemoveContainer` returns item total test | Task 3 Step 1 `RemoveContainer_DestroysInventory_ReturnsItemTotalAtRemoval` (Ruling: in `InventoriesTests`) |
| §8 `DeterminismTests` additions (60-tick two-run equal; hash changes on insert) | Task 3 Step 11 |
| §8 no `AddContainer` overload for filter/readonly (tests construct `new Inventory(...)` directly) | Task 1 Steps 7, 9 use `new Inventory(4, readOnly: true)` / `filterItemProtoId:` directly; `AddContainer` only takes `slotCount` (Task 3 Step 3) |
| §9 Task split (3 tasks) | Tasks 1 / 2 / 3 match §9 exactly |

No gaps.

**2. Placeholder scan:** No "TBD"/"TODO"/"handle edge cases"/"similar to Task N". Every code step has full code. Every test step has full test bodies.

**3. Type consistency:**
- `InventoryId` shape `(int Index, int Generation)`, `Invalid = new(-1, 0)`, `IsValid => Index >= 0` — identical in Task 2 Step 1, consumed in Task 3.
- `InventoryPool.Create(Inventory)`, `Destroy(InventoryId)`, `IsAlive`, `Get`, `Capacity`, `IsAliveAtIndex`, `GetAtIndex`, `GenerationAtIndex`, `WriteState` — defined Task 2 Step 4, all consumed by `Inventories` Task 3 Step 3 with matching names.
- `Inventory` ctor `(int slotCount, bool readOnly = false, int filterItemProtoId = 0)` — Task 1 Step 4; `Inventories.AddContainer` calls `new Inventory(slotCount)` (Task 3 Step 3) ✓; tests call `new Inventory(4, readOnly: true)` and `new Inventory(4, filterItemProtoId: Iron)` ✓.
- `Inventory.ReadOnly` / `Inventory.FilterItemProtoId` — public getters (Task 1 Step 4); read by `Inventories.WriteState` (Task 3 Step 3) ✓.
- `Inventory.Insert(int, int, int)` / `Remove(int, int)` / `CountOf(int)` / `TotalItems()` / `WriteState` — consistent across Task 1 impl, Task 1 tests, Task 3 `InventoriesTests` and `DeterminismTests`.
- `Inventories.AddContainer(EntityId, int)` / `RemoveContainer(EntityId)` / `GetInventoryId(EntityId)` / `Get(InventoryId)` / `Capacity` / `IsAliveAtIndex(int)` — defined Task 3 Step 3, consumed in Task 3 Steps 6, 11 and `Simulation` Step 8 with matching signatures.
- `Simulation.Inventories` property — added Task 3 Step 8.2, used in Task 3 Steps 6, 11.
- `ContainerPrototype.InventorySize` — existing (`sim/Faketorio.Sim/Prototypes/EntityPrototype.cs:13`); used Task 3 Step 8.3.
- `EntityId` — existing (`sim/Faketorio.Sim/Entities/EntityId.cs`), `Invalid = new(-1, 0)`; used in `Inventories` and its tests.
- `ItemPrototype.StackSize` — existing (`sim/Faketorio.Sim/Prototypes/ItemPrototype.cs:5`); used in `DeterminismTests` Task 3 Step 11.
- Item names used in tests: `iron-plate`, `coal` exist in `data/base/items.json`. Entity names `wooden-chest`, `large-chest`, `transport-belt-basic` exist in `data/base/entities.json`.
- `IStateWriter` has only `Write(byte)`, `Write(int)`, `Write(long)` — `Inventories.WriteState` casts `ReadOnly` to `byte`, everything else is `int` ✓.

Consistent throughout.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-09-04-m1-plan4-inventory.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

**Which approach?**
