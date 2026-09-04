# M1 Plan 6 — 矿脉生成(ResourceGrid + 定点值噪声)Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a deterministic, seed-driven, lazily-generated ore layer to the simulation — fixed-point value-noise generation, a chunked `ResourceGrid` holding mutable per-tile ore amounts, and `GetResourceAt` / `Extract` queries.

**Architecture:** Two standalone integer/fixed-point primitives (`DeterministicHash` splitmix64, `ValueNoise` Q16.16 fBm) — no `float`/`double` anywhere. Each `ResourcePrototype` carries its own `NoiseLayer` (independent field, threshold, richness); per tile the resource with the largest normalized excess wins, ties by prototype Id. `ResourceGrid` mirrors `WorldGrid`'s 32×32 chunking, generates a chunk (a pure function of `(seed, tileX, tileY)`) on first touch, then overlays seed-independent starter patches near the origin so the P5 bootstrap always has ore. `Simulation` gains a `worldSeed` ctor arg and a `Resources` property; `WriteState` writes the seed and the generated chunks; `Step` is unchanged.

**Tech Stack:** C# / .NET 8, xUnit. No Godot dependency in `Faketorio.Sim`. Deterministic fixed-tick simulation — canonical serialization through `IStateWriter` / `Fnv1aHashWriter` (FNV-1a).

**Spec:** [`docs/superpowers/specs/2026-09-04-m1-plan6-resource-generation-design.md`](../specs/2026-09-04-m1-plan6-resource-generation-design.md)

## Global Constraints

- **Determinism 铁律:** no `float`/`double` in simulation state OR in any generation math that feeds state — all noise is `int`/`long` + shifts. No `System.Random` (unstable across .NET versions). No `Dictionary` enumeration in any `WriteState` — iterate sorted keys. Every new piece of state must be written into a `WriteState` reachable from `Simulation.WriteState`.
- **Namespaces:** `DeterministicHash`, `ValueNoise`, `ResourceCell`, `ResourceGrid` (+ internal `ResourceChunk`) live in `namespace Faketorio.Sim.World;` under `sim/Faketorio.Sim/World/`. `ResourcePrototype`, `MapGenPrototype`, `NoiseLayer`, `StarterPatch` live in `namespace Faketorio.Sim.Prototypes;` under `sim/Faketorio.Sim/Prototypes/`. Tests in `namespace Faketorio.Sim.Tests;` under `sim/Faketorio.Sim.Tests/`.
- **`DeterministicHash` constants — copy verbatim, do not "improve":**
  - `Mix`: `x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL; x ^= x >> 27; x *= 0x94D049BB133111EBUL; x ^= x >> 31;`
  - `Hash(seed, field, x, y)`: `h = Mix((ulong)seed ^ 0x9E3779B97F4A7C15UL);` then `h = Mix(h ^ ((ulong)(uint)field * 0x9E3779B97F4A7C15UL));` then `h = Mix(h ^ ((ulong)(uint)x * 0xFF51AFD7ED558CCDUL));` then `h = Mix(h ^ ((ulong)(uint)y * 0xC4CEB9FE1A85EC53UL));`
- **Q16.16 fixed point:** `One = 65536`. Lattice values are raw `[0, 65535]`. All fixed-point multiplies use `long` intermediates: `(int)(((long)a * b) >> 16)`.
- **fBm normalization:** `long norm = Σ_{k=0}^{K-1} (65536L >> k);` computed by loop — NOT the closed form (`K == 1` divides by zero).
- **`ResourceCell` invariant** (mirrors `ItemStack`): no code path produces `ResourceProtoId != 0 && Amount == 0`. So `cell == ResourceCell.Empty` ⟺ `cell.IsEmpty` (`IsEmpty => Amount == 0`).
- **`Extract` guard:** `count <= 0` → return 0, no state change (same trust-the-precondition style as `Inventory.Insert`/`Remove`).
- **Chunk math:** `ResourceGrid` uses the exact same chunk key and tile-index math as `WorldGrid` (`sim/Faketorio.Sim/World/WorldGrid.cs:13-24`): `cx = x >> 5, cy = y >> 5`, `key = ((long)cx << 32) | (uint)cy`, `tileIndex = (y & 31) * 32 + (x & 31)`, `WorldGrid.ChunkSize == 32`.
- **`worldSeed` in the hash:** `Simulation.WriteState` writes `_worldSeed` once, right after `writer.Write(RejectedCommandCount);` and before `Entities.WriteState(writer);`.
- **Commit trailer:** every commit message ends with:
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
  ```
- **Test command:** `dotnet test sim/Faketorio.Sim.Tests` from repo root. Single test: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~<ClassName>.<MethodName>"`.
- **Baseline:** `dotnet test` is green at **189 passing** before Task 1. `data/**` is copied to the test output dir by `sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj` (`<Content Include="..\..\data\**" ...>`), so new files under `data/base/` are picked up automatically.

## File Structure

| File | Responsibility |
|---|---|
| `sim/Faketorio.Sim/World/DeterministicHash.cs` (create) | `static Hash(long seed, int field, int x, int y) → ulong`; splitmix64 mixer. The only entropy source. |
| `sim/Faketorio.Sim/World/ValueNoise.cs` (create) | `static` fixed-point value noise: `Isqrt`, `Ease`, `Lerp`, `Octave`, `Fbm`. Pure, integer/`long` only. |
| `sim/Faketorio.Sim/World/ResourceCell.cs` (create) | `readonly record struct ResourceCell(int ResourceProtoId, int Amount)` + `Empty` / `IsEmpty`. |
| `sim/Faketorio.Sim/World/ResourceGrid.cs` (create) | Chunked storage (`Dictionary<long, ResourceChunk>`), lazy per-chunk generation, starter-patch overlay, `GetResourceAt` / `Extract` / `WriteState` / `GeneratedChunkCount` / `IsChunkGenerated`. Contains `internal sealed class ResourceChunk`. |
| `sim/Faketorio.Sim/Prototypes/ResourcePrototype.cs` (create) | `NoiseLayer` record, `StarterPatch` record struct, `ResourcePrototype`, `MapGenPrototype`. |
| `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` (modify) | Parse `"resource"` + `"map-gen"`; add a post-`AssignIds` resolve+validate pass. |
| `data/base/resources.json` (create) | coal / iron-ore / copper-ore / stone resource prototypes. |
| `data/base/map-gen.json` (create) | one `map-gen`: defaults + starter patches. |
| `data/base/items.json` (modify) | add `copper-ore`, `stone` items. |
| `sim/Faketorio.Sim/Simulation.cs` (modify) | `worldSeed` ctor arg, `_worldSeed` field, `Resources` property, two `WriteState` additions. |
| `sim/Faketorio.Sim.Tests/DeterministicHashTests.cs` (create) | Hash determinism + sensitivity + one pinned regression value. |
| `sim/Faketorio.Sim.Tests/ValueNoiseTests.cs` (create) | `Isqrt` / `Ease` / `Lerp` / `Fbm` correctness and range. |
| `sim/Faketorio.Sim.Tests/ResourceGridTests.cs` (create) | Generation consistency, `Extract`, access-order independence, starter patches, seed sensitivity, `WriteState`. |
| `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` (modify) | Resolved-layer load + one negative case per validation rule. |
| `sim/Faketorio.Sim.Tests/SimulationTests.cs` (modify) | `Resources` present; query generates a chunk; `WriteState` covers it. |
| `sim/Faketorio.Sim.Tests/DeterminismTests.cs` (modify) | Same-seed two-run equality; different-seed inequality. Golden scenarios unchanged. |

---

## Task 1: `DeterministicHash` + `ValueNoise` + unit tests

**Files:**
- Create: `sim/Faketorio.Sim/World/DeterministicHash.cs`, `sim/Faketorio.Sim/World/ValueNoise.cs`
- Test: `sim/Faketorio.Sim.Tests/DeterministicHashTests.cs`, `sim/Faketorio.Sim.Tests/ValueNoiseTests.cs`

**Interfaces:**
- Consumes: nothing (leaf primitives).
- Produces:
  - `static class DeterministicHash { public static ulong Hash(long seed, int field, int x, int y); }`
  - `static class ValueNoise` with:
    - `public static int Isqrt(long n)` — integer floor sqrt; `n < 0` throws `ArgumentOutOfRangeException`
    - `public static int Ease(int t)` — Q16 smoothstep, input/output raw `[0, 65536]`
    - `public static int Lerp(int p, int q, int t)` — `p + (int)(((long)(q - p) * t) >> 16)`, `t` raw Q16
    - `public static int Fbm(long seed, int field, int tileX, int tileY, int latticeSize, int octaves)` — Q16 result in `[0, 65536)`

- [ ] **Step 1: Create `DeterministicHash.cs`**

```csharp
namespace Faketorio.Sim.World;

// splitmix64 式整数混合哈希。矿脉生成的唯一熵源(铁律 3:禁用 System.Random)。
// 全 ulong 位运算,C# 语义完全定义、跨平台位一致。常量勿改。
public static class DeterministicHash
{
    private static ulong Mix(ulong x)
    {
        x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27; x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return x;
    }

    public static ulong Hash(long seed, int field, int x, int y)
    {
        ulong h = Mix((ulong)seed ^ 0x9E3779B97F4A7C15UL);
        h = Mix(h ^ ((ulong)(uint)field * 0x9E3779B97F4A7C15UL));
        h = Mix(h ^ ((ulong)(uint)x     * 0xFF51AFD7ED558CCDUL));
        h = Mix(h ^ ((ulong)(uint)y     * 0xC4CEB9FE1A85EC53UL));
        return h;
    }
}
```

- [ ] **Step 2: Write the failing `DeterministicHashTests`**

Create `sim/Faketorio.Sim.Tests/DeterministicHashTests.cs`:

```csharp
using Faketorio.Sim.World;

namespace Faketorio.Sim.Tests;

public class DeterministicHashTests
{
    [Fact]
    public void SameInputs_SameOutput()
    {
        Assert.Equal(
            DeterministicHash.Hash(123456789L, 7, -3, 42),
            DeterministicHash.Hash(123456789L, 7, -3, 42));
    }

    [Fact]
    public void EachParameterAffectsOutput()
    {
        ulong baseline = DeterministicHash.Hash(1L, 1, 1, 1);
        Assert.NotEqual(baseline, DeterministicHash.Hash(2L, 1, 1, 1));
        Assert.NotEqual(baseline, DeterministicHash.Hash(1L, 2, 1, 1));
        Assert.NotEqual(baseline, DeterministicHash.Hash(1L, 1, 2, 1));
        Assert.NotEqual(baseline, DeterministicHash.Hash(1L, 1, 1, 2));
    }

    [Fact]
    public void PinnedRegressionValue()
    {
        // 钉死值:改了 Mix 常量或混合顺序就会红。首次运行填入实际值(见步骤 3)。
        Assert.Equal(0UL, DeterministicHash.Hash(123456789L, 7, -3, 42));
    }
}
```

- [ ] **Step 3: Run `DeterministicHashTests`; fill the pinned value**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~DeterministicHashTests"`
Expected: `SameInputs_SameOutput` and `EachParameterAffectsOutput` PASS; `PinnedRegressionValue` FAILS with a message like `Expected: 0UL  Actual: 14876...UL`. Copy the actual value from the failure message and replace `0UL` in `PinnedRegressionValue` with it (keep the `UL` suffix). Re-run the filter — all 3 PASS.

- [ ] **Step 4: Commit**

```bash
git add sim/Faketorio.Sim/World/DeterministicHash.cs sim/Faketorio.Sim.Tests/DeterministicHashTests.cs
git commit -m "$(cat <<'EOF'
feat(world): DeterministicHash — splitmix64 coordinate hash

The single entropy source for resource generation. Integer-only, no
System.Random. Pinned regression value guards the mix constants.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

- [ ] **Step 5: Write the failing `ValueNoiseTests`**

Create `sim/Faketorio.Sim.Tests/ValueNoiseTests.cs`:

```csharp
using Faketorio.Sim.World;

namespace Faketorio.Sim.Tests;

public class ValueNoiseTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(10, 3)]
    [InlineData(15, 3)]
    [InlineData(16, 4)]
    [InlineData(1_000_000, 1000)]
    [InlineData(1_000_002, 1000)]
    public void Isqrt_FloorRoot(long n, int expected)
        => Assert.Equal(expected, ValueNoise.Isqrt(n));

    [Fact]
    public void Isqrt_Negative_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ValueNoise.Isqrt(-1));

    [Fact]
    public void Lerp_Endpoints_And_Midpoint()
    {
        Assert.Equal(100, ValueNoise.Lerp(100, 200, 0));
        Assert.Equal(200, ValueNoise.Lerp(100, 200, 65536));
        Assert.Equal(150, ValueNoise.Lerp(100, 200, 32768));
    }

    [Fact]
    public void Ease_Endpoints_And_Monotonic()
    {
        Assert.Equal(0, ValueNoise.Ease(0));
        Assert.Equal(65536, ValueNoise.Ease(65536));
        int prev = -1;
        for (int t = 0; t <= 65536; t += 512)
        {
            int e = ValueNoise.Ease(t);
            Assert.True(e >= prev, $"Ease not monotonic at t={t}: {e} < {prev}");
            prev = e;
        }
    }

    [Fact]
    public void Fbm_Deterministic()
        => Assert.Equal(
            ValueNoise.Fbm(42L, 3, 100, -200, 64, 3),
            ValueNoise.Fbm(42L, 3, 100, -200, 64, 3));

    [Fact]
    public void Fbm_InRange_0_to_65536()
    {
        for (int x = -300; x <= 300; x += 37)
            for (int y = -300; y <= 300; y += 41)
            {
                int v = ValueNoise.Fbm(7L, 1, x, y, 64, 3);
                Assert.InRange(v, 0, 65535);
            }
    }

    [Fact]
    public void Fbm_DifferentSeedOrField_ChangesField()
    {
        // 在一片区域上,换 seed 或换 field 至少有一格不同
        bool seedDiff = false, fieldDiff = false;
        for (int x = 0; x < 128 && !(seedDiff && fieldDiff); x += 8)
            for (int y = 0; y < 128; y += 8)
            {
                int a = ValueNoise.Fbm(1L, 1, x, y, 64, 3);
                if (ValueNoise.Fbm(2L, 1, x, y, 64, 3) != a) seedDiff = true;
                if (ValueNoise.Fbm(1L, 2, x, y, 64, 3) != a) fieldDiff = true;
            }
        Assert.True(seedDiff);
        Assert.True(fieldDiff);
    }

    [Fact]
    public void Fbm_SingleOctave_NoDivideByZero()
        => Assert.InRange(ValueNoise.Fbm(1L, 1, 5, 5, 64, 1), 0, 65535);
}
```

- [ ] **Step 6: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ValueNoiseTests"`
Expected: FAIL — `ValueNoise` does not exist (compile error).

- [ ] **Step 7: Create `ValueNoise.cs`**

```csharp
using System.Numerics;

namespace Faketorio.Sim.World;

// 定点值噪声(Q16.16)。纯函数,全 int/long + 位移,无 float/double(铁律 3)。
public static class ValueNoise
{
    // 整数下取整平方根。
    public static int Isqrt(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (n == 0) return 0;
        long x = (long)Math.Sqrt(n);            // 近似起点(仅整数结果参与状态,double 不入状态)
        while (x * x > n) x--;
        while ((x + 1) * (x + 1) <= n) x++;
        return (int)x;
    }

    // smoothstep 缓动 ease(t) = t·t·(3 − 2t),t 原始 Q16 [0, 65536]。
    public static int Ease(int t)
    {
        long t2 = ((long)t * t) >> 16;
        return (int)((t2 * ((3L << 16) - 2L * t)) >> 16);
    }

    // 定点线性插值,t 原始 Q16。
    public static int Lerp(int p, int q, int t)
        => p + (int)(((long)(q - p) * t) >> 16);

    private static int LatticeQ16(long seed, int field, int lx, int ly)
        => (int)(DeterministicHash.Hash(seed, field, lx, ly) & 0xFFFF);

    // 单倍频:格距 S = latticeSize >> k(latticeSize 是 2 的幂)。
    private static int Octave(long seed, int field, int tx, int ty, int latticeSize, int k)
    {
        int shift = BitOperations.Log2((uint)latticeSize) - k;   // 调用方保证 >= 0
        int s = 1 << shift;
        int lx0 = tx >> shift, ly0 = ty >> shift;

        int v00 = LatticeQ16(seed, field, lx0,     ly0);
        int v10 = LatticeQ16(seed, field, lx0 + 1, ly0);
        int v01 = LatticeQ16(seed, field, lx0,     ly0 + 1);
        int v11 = LatticeQ16(seed, field, lx0 + 1, ly0 + 1);

        int fxQ = (int)(((long)(tx & (s - 1)) << 16) / s);
        int fyQ = (int)(((long)(ty & (s - 1)) << 16) / s);
        int ux = Ease(fxQ), uy = Ease(fyQ);

        int a = Lerp(v00, v10, ux);
        int b = Lerp(v01, v11, ux);
        return Lerp(a, b, uy);
    }

    // 多倍频 fBm。归一化用循环求和(闭式在 octaves == 1 时除零)。
    public static int Fbm(long seed, int field, int tileX, int tileY, int latticeSize, int octaves)
    {
        long sum = 0, norm = 0;
        for (int k = 0; k < octaves; k++)
        {
            sum += ((long)Octave(seed, field, tileX, tileY, latticeSize, k)) >> k;
            norm += 65536L >> k;
        }
        return (int)((sum << 16) / norm);
    }
}
```

Note on `Isqrt`: `Math.Sqrt` is used only to seed the integer refinement loop; its `double` result never leaves the function and never enters simulation state. The returned `int` is exact (the two `while` loops correct any rounding). This matches how `Units.ParseEnergy` already uses `double` at load time only.

- [ ] **Step 8: Run `ValueNoiseTests` to verify they pass**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ValueNoiseTests"`
Expected: all PASS.

- [ ] **Step 9: Run the whole suite (no regressions)**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS, 189 + `DeterministicHashTests` (3) + `ValueNoiseTests` (9 incl. Theory rows).

- [ ] **Step 10: Commit**

```bash
git add sim/Faketorio.Sim/World/ValueNoise.cs sim/Faketorio.Sim.Tests/ValueNoiseTests.cs
git commit -m "$(cat <<'EOF'
feat(world): ValueNoise — fixed-point value-noise fBm

Q16.16 lattice value noise with smoothstep easing and bilinear interp,
integer-only. isqrt for starter-patch distance. Loop-summed fBm
normalization (closed form divides by zero at octaves == 1).

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

---

## Task 2: Prototypes + data files + `PrototypeLoader` resolve/validate pass

**Files:**
- Create: `sim/Faketorio.Sim/Prototypes/ResourcePrototype.cs`, `data/base/resources.json`, `data/base/map-gen.json`
- Modify: `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`, `data/base/items.json`
- Test: `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs`

**Interfaces:**
- Consumes: existing `PrototypeBase` (`Name` required, `Id { get; internal set; }`), `PrototypeRegistry` (`Get<T>(name)`, `TryGet<T>(name, out T)`, `GetById(int)`, `Count`), `ItemPrototype`.
- Produces:
  - `public sealed record NoiseLayer(int FieldId, int LatticeSize, int Octaves, int ThresholdQ16, bool Warp = false, bool Ridge = false);`
  - `public readonly record struct StarterPatch(string Resource, int CenterX, int CenterY, int Radius, int CenterAmount);`
  - `public sealed class ResourcePrototype : PrototypeBase { public required string MinableResult { get; init; } public NoiseLayer Layer { get; internal set; } public int RichnessBase { get; init; } public int RichnessScale { get; init; } }`
  - `public sealed class MapGenPrototype : PrototypeBase { public int DefaultLatticeSize { get; init; } public int DefaultOctaves { get; init; } public IReadOnlyList<StarterPatch> StarterPatches { get; init; } }`
  - After `PrototypeLoader.LoadFromDirectory` returns, every `ResourcePrototype.Layer` has all fields concrete (no zero sentinels): `FieldId` = explicit or `1 + Id`; `LatticeSize` / `Octaves` = explicit or the `MapGenPrototype` default.

- [ ] **Step 1: Create `ResourcePrototype.cs`**

```csharp
namespace Faketorio.Sim.Prototypes;

// 一个噪声特征层的配置。P6 只有矿在用;未来水域 / 树木套同一形状。
// 字段为 0 表示"未设置",由 PrototypeLoader 的解析 pass 补齐(见 spec §4.1)。
public sealed record NoiseLayer(
    int FieldId,
    int LatticeSize,      // S0,必须是 2 的幂
    int Octaves,          // K
    int ThresholdQ16,     // 场值超过它这格才有该特征
    bool Warp  = false,   // 预留,P6 不支持 —— 加载期校验为 true 即抛
    bool Ridge = false);

public readonly record struct StarterPatch(
    string Resource, int CenterX, int CenterY, int Radius, int CenterAmount);

public sealed class ResourcePrototype : PrototypeBase
{
    public required string MinableResult { get; init; }   // 挖出来的 item name
    public NoiseLayer Layer  { get; internal set; } = new(0, 0, 0, 0);  // 解析 pass 后全字段具体
    public int RichnessBase  { get; init; }               // 有矿时的最低矿量(>= 1)
    public int RichnessScale { get; init; }               // 额外矿量 = RichnessScale·excess >> 16
}

public sealed class MapGenPrototype : PrototypeBase       // 单例,约定 name = "default"
{
    public int DefaultLatticeSize { get; init; } = 64;
    public int DefaultOctaves     { get; init; } = 3;
    public IReadOnlyList<StarterPatch> StarterPatches { get; init; } = Array.Empty<StarterPatch>();
}
```

- [ ] **Step 2: Create `data/base/resources.json`**

```json
[
  { "type": "resource", "name": "coal",       "minableResult": "coal",
    "noise": { "thresholdQ16": 44000 }, "richnessBase": 400, "richnessScale": 6000 },
  { "type": "resource", "name": "iron-ore",   "minableResult": "iron-ore",
    "noise": { "thresholdQ16": 45000 }, "richnessBase": 400, "richnessScale": 6000 },
  { "type": "resource", "name": "copper-ore", "minableResult": "copper-ore",
    "noise": { "thresholdQ16": 45000 }, "richnessBase": 400, "richnessScale": 6000 },
  { "type": "resource", "name": "stone",      "minableResult": "stone",
    "noise": { "thresholdQ16": 46000 }, "richnessBase": 300, "richnessScale": 4000 }
]
```

- [ ] **Step 3: Create `data/base/map-gen.json`**

```json
[{ "type": "map-gen", "name": "default", "defaultLatticeSize": 64, "defaultOctaves": 3,
   "starterPatches": [
     { "resource": "coal",       "centerX":  6, "centerY": -8, "radius": 4, "centerAmount": 1500 },
     { "resource": "iron-ore",   "centerX": -9, "centerY": -6, "radius": 5, "centerAmount": 2000 },
     { "resource": "copper-ore", "centerX": -8, "centerY":  9, "radius": 4, "centerAmount": 1500 },
     { "resource": "stone",      "centerX":  9, "centerY":  7, "radius": 3, "centerAmount": 1000 }
   ]}]
```

- [ ] **Step 4: Add `copper-ore` and `stone` to `data/base/items.json`**

Current file is:

```json
[
  { "type": "item", "name": "iron-ore", "stackSize": 50 },
  { "type": "item", "name": "iron-plate", "stackSize": 100 },
  { "type": "item", "name": "coal", "stackSize": 50, "fuelValue": "4MJ", "fuelCategory": "chemical" },
  { "type": "item", "name": "wooden-chest", "stackSize": 50, "placeResult": "wooden-chest" }
]
```

Add two entries before the closing `]`:

```json
  { "type": "item", "name": "copper-ore", "stackSize": 50 },
  { "type": "item", "name": "stone", "stackSize": 50 }
```

(Remember the comma after the `wooden-chest` line.)

- [ ] **Step 5: Write the failing loader tests**

Append to `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` (inside the class; `Load()` helper already exists):

```csharp
    [Fact]
    public void LoadsResourceWithResolvedNoiseLayer()
    {
        var reg = Load();
        var coal = reg.Get<ResourcePrototype>("coal");
        Assert.Equal("coal", coal.MinableResult);
        Assert.Equal(400, coal.RichnessBase);
        Assert.Equal(44000, coal.Layer.ThresholdQ16);
        Assert.Equal(64, coal.Layer.LatticeSize);     // 从 map-gen 默认补齐
        Assert.Equal(3, coal.Layer.Octaves);          // 从 map-gen 默认补齐
        Assert.Equal(1 + coal.Id, coal.Layer.FieldId); // fieldId 省略 -> 1 + Id
        Assert.False(coal.Layer.Warp);
    }

    [Fact]
    public void LoadsMapGenWithStarterPatches()
    {
        var mg = Load().Get<MapGenPrototype>("default");
        Assert.Equal(4, mg.StarterPatches.Count);
        Assert.Equal(new StarterPatch("coal", 6, -8, 4, 1500), mg.StarterPatches[0]);
    }

    private static void AssertLoadThrows(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "gen.json"), json);
        Assert.Throws<InvalidDataException>(() => PrototypeLoader.LoadFromDirectory(dir));
    }

    // 一个最小的自洽数据集:1 item + 1 map-gen + 1 resource。各负例只改坏其中一处。
    private const string ValidItem = "{ \"type\": \"item\", \"name\": \"coal\", \"stackSize\": 50 }";
    private const string ValidResource =
        "{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", " +
        "\"noise\": { \"thresholdQ16\": 44000 }, \"richnessBase\": 400, \"richnessScale\": 6000 }";

    [Fact]
    public void TwoMapGen_Throws() => AssertLoadThrows(
        $"[{ValidItem}, {ValidResource}, " +
        "{ \"type\": \"map-gen\", \"name\": \"a\" }, { \"type\": \"map-gen\", \"name\": \"b\" }]");

    [Fact]
    public void ResourcePresentButNoMapGen_Throws() => AssertLoadThrows(
        $"[{ValidItem}, {ValidResource}]");

    [Fact]
    public void NonPowerOfTwoLatticeSize_Throws() => AssertLoadThrows(
        $"[{ValidItem}, " +
        "{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", " +
        "\"noise\": { \"thresholdQ16\": 44000, \"latticeSize\": 48 }, \"richnessBase\": 400, \"richnessScale\": 6000 }, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\" }]");

    [Fact]
    public void OctavesTooLargeForLattice_Throws() => AssertLoadThrows(
        $"[{ValidItem}, " +
        "{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", " +
        "\"noise\": { \"thresholdQ16\": 44000, \"latticeSize\": 64, \"octaves\": 8 }, \"richnessBase\": 400, \"richnessScale\": 6000 }, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\" }]");

    [Fact]
    public void WarpTrue_Throws() => AssertLoadThrows(
        $"[{ValidItem}, " +
        "{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", " +
        "\"noise\": { \"thresholdQ16\": 44000, \"warp\": true }, \"richnessBase\": 400, \"richnessScale\": 6000 }, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\" }]");

    [Fact]
    public void ThresholdOutOfRange_Throws() => AssertLoadThrows(
        $"[{ValidItem}, " +
        "{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", " +
        "\"noise\": { \"thresholdQ16\": 0 }, \"richnessBase\": 400, \"richnessScale\": 6000 }, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\" }]");

    [Fact]
    public void RichnessBaseZero_Throws() => AssertLoadThrows(
        $"[{ValidItem}, " +
        "{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", " +
        "\"noise\": { \"thresholdQ16\": 44000 }, \"richnessBase\": 0, \"richnessScale\": 6000 }, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\" }]");

    [Fact]
    public void ResourceMinableResultMissingItem_Throws() => AssertLoadThrows(
        "[{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"nonexistent\", " +
        "\"noise\": { \"thresholdQ16\": 44000 }, \"richnessBase\": 400, \"richnessScale\": 6000 }, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\" }]");

    [Fact]
    public void StarterPatchUnknownResource_Throws() => AssertLoadThrows(
        $"[{ValidItem}, {ValidResource}, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\", \"starterPatches\": " +
        "[{ \"resource\": \"nonexistent\", \"centerX\": 0, \"centerY\": 0, \"radius\": 3, \"centerAmount\": 100 }] }]");
```

Also add to the top `using` block of the file if not present: `using Faketorio.Sim.Prototypes;` is already there.

- [ ] **Step 6: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~PrototypeLoaderTests"`
Expected: the new tests FAIL (compile error — `ResourcePrototype` / `MapGenPrototype` unknown), existing loader tests unaffected once it compiles.

- [ ] **Step 7: Add `"resource"` + `"map-gen"` parsing to `PrototypeLoader.Parse`**

In `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`, add two arms to the `type switch` in `Parse` (before the `_ =>` throw), and two helpers:

```csharp
            "resource" => new ResourcePrototype
            {
                Name = name,
                MinableResult = el.GetProperty("minableResult").GetString()!,
                RichnessBase  = GetInt(el, "richnessBase", 0),
                RichnessScale = GetInt(el, "richnessScale", 0),
                Layer = ParseNoiseLayer(el),
            },
            "map-gen" => new MapGenPrototype
            {
                Name = name,
                DefaultLatticeSize = GetInt(el, "defaultLatticeSize", 64),
                DefaultOctaves     = GetInt(el, "defaultOctaves", 3),
                StarterPatches     = ParseStarterPatches(el),
            },
```

```csharp
    // 缺省字段存 0(哨兵),由 ResolveAndValidateMapGen 补齐。
    private static NoiseLayer ParseNoiseLayer(JsonElement el)
    {
        if (!el.TryGetProperty("noise", out var n))
            return new NoiseLayer(0, 0, 0, 0);
        return new NoiseLayer(
            FieldId:      GetInt(n, "fieldId", 0),
            LatticeSize:  GetInt(n, "latticeSize", 0),
            Octaves:      GetInt(n, "octaves", 0),
            ThresholdQ16: GetInt(n, "thresholdQ16", 0),
            Warp:  n.TryGetProperty("warp",  out var w) && w.GetBoolean(),
            Ridge: n.TryGetProperty("ridge", out var r) && r.GetBoolean());
    }

    private static IReadOnlyList<StarterPatch> ParseStarterPatches(JsonElement el)
    {
        var list = new List<StarterPatch>();
        if (el.TryGetProperty("starterPatches", out var arr))
            foreach (var p in arr.EnumerateArray())
                list.Add(new StarterPatch(
                    p.GetProperty("resource").GetString()!,
                    p.GetProperty("centerX").GetInt32(),
                    p.GetProperty("centerY").GetInt32(),
                    p.GetProperty("radius").GetInt32(),
                    p.GetProperty("centerAmount").GetInt32()));
        return list;
    }
```

- [ ] **Step 8: Add the resolve/validate pass to `LoadFromDirectory`**

In `LoadFromDirectory`, change the tail from `registry.AssignIds(); return registry;` to:

```csharp
        registry.AssignIds();
        ResolveAndValidateMapGen(registry);
        return registry;
```

Add the method:

```csharp
    // spec §4.1 / §4.3:AssignIds() 之后跑一次。把每个 ResourcePrototype.Layer 的
    // 0 哨兵补成具体值(field = explicit 或 1+Id;格距/倍频 = explicit 或 map-gen 默认),
    // 再校验解析后的有效值。跨引用校验(starter patch / minableResult)也在这里。
    private static void ResolveAndValidateMapGen(PrototypeRegistry registry)
    {
        var resources = new List<ResourcePrototype>();
        MapGenPrototype? mapGen = null;
        int mapGenCount = 0;
        for (int i = 0; i < registry.Count; i++)
        {
            switch (registry.GetById(i))
            {
                case ResourcePrototype r: resources.Add(r); break;
                case MapGenPrototype m:   mapGen = m; mapGenCount++; break;
            }
        }

        if (mapGenCount > 1)
            throw new InvalidDataException("More than one 'map-gen' prototype");
        if (resources.Count == 0)
            return;                       // 没有矿:map-gen 可有可无,无需解析
        if (mapGen is null)
            throw new InvalidDataException("'resource' prototypes present but no 'map-gen' prototype");

        foreach (var r in resources)
        {
            var L = r.Layer;
            int fieldId = L.FieldId != 0 ? L.FieldId : 1 + r.Id;
            int lattice = L.LatticeSize != 0 ? L.LatticeSize : mapGen.DefaultLatticeSize;
            int octaves = L.Octaves != 0 ? L.Octaves : mapGen.DefaultOctaves;

            if (lattice <= 0 || (lattice & (lattice - 1)) != 0)
                throw new InvalidDataException($"Resource '{r.Name}': latticeSize {lattice} is not a positive power of two");
            int log2 = System.Numerics.BitOperations.Log2((uint)lattice);
            if (octaves < 1 || octaves > log2 + 1)
                throw new InvalidDataException($"Resource '{r.Name}': octaves {octaves} out of range 1..{log2 + 1} for latticeSize {lattice}");
            if (L.Warp || L.Ridge)
                throw new InvalidDataException($"Resource '{r.Name}': noise warp/ridge not supported in M1");
            if (L.ThresholdQ16 < 1 || L.ThresholdQ16 > 65535)
                throw new InvalidDataException($"Resource '{r.Name}': thresholdQ16 {L.ThresholdQ16} out of range 1..65535");
            if (r.RichnessBase < 1)
                throw new InvalidDataException($"Resource '{r.Name}': richnessBase must be >= 1");
            if (!registry.TryGet<ItemPrototype>(r.MinableResult, out _))
                throw new InvalidDataException($"Resource '{r.Name}': minableResult '{r.MinableResult}' has no matching item");

            r.Layer = L with { FieldId = fieldId, LatticeSize = lattice, Octaves = octaves };
        }

        foreach (var sp in mapGen.StarterPatches)
        {
            if (!registry.TryGet<ResourcePrototype>(sp.Resource, out _))
                throw new InvalidDataException($"Starter patch references unknown resource '{sp.Resource}'");
            if (sp.Radius < 1)
                throw new InvalidDataException($"Starter patch for '{sp.Resource}': radius must be >= 1");
            if (sp.CenterAmount < 1)
                throw new InvalidDataException($"Starter patch for '{sp.Resource}': centerAmount must be >= 1");
        }
    }
```

**Plan note (extends spec §4.3):** the spec says a missing `map-gen` always throws; this plan narrows that to "throws only when `resource` prototypes are present." Reason: existing `PrototypeLoaderTests` negative cases (`UnknownTypeThrows`, `ZeroTileWidthThrows`) load temp dirs with no `map-gen` and no resources — an unconditional throw would make them pass for the wrong reason and would couple `map-gen` to every data set. `>1 map-gen` still always throws. Also added: `ThresholdQ16` must be in `[1, 65535]` (spec §4.3 omits it, but a `0` threshold — the value a missing `noise` object yields — makes every tile that resource; catching it is consistent with spec intent).

- [ ] **Step 9: Run the loader tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~PrototypeLoaderTests"`
Expected: all PASS (existing + new).

- [ ] **Step 10: Run the whole suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. 189 + Task 1 tests + new `PrototypeLoaderTests` cases.

- [ ] **Step 11: Commit**

```bash
git add sim/Faketorio.Sim/Prototypes/ResourcePrototype.cs sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs data/base/resources.json data/base/map-gen.json data/base/items.json sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs
git commit -m "$(cat <<'EOF'
feat(proto): ResourcePrototype + MapGenPrototype + noise-layer resolve pass

resource / map-gen prototype types; NoiseLayer with zero-sentinel fields
resolved after AssignIds (fieldId -> 1+Id, latticeSize/octaves -> map-gen
defaults), then validated (power-of-two lattice, octave range, warp/ridge
rejected, threshold 1..65535, cross-refs). map-gen required only when
resources exist. coal/iron/copper/stone data + copper-ore/stone items.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

---

## Task 3: `ResourceGrid` + `ResourceChunk` + unit tests

**Files:**
- Create: `sim/Faketorio.Sim/World/ResourceCell.cs`, `sim/Faketorio.Sim/World/ResourceGrid.cs`
- Test: `sim/Faketorio.Sim.Tests/ResourceGridTests.cs`

**Interfaces:**
- Consumes: `ValueNoise.Fbm` / `ValueNoise.Isqrt` (Task 1); resolved `ResourcePrototype.Layer` / `MapGenPrototype.StarterPatches` (Task 2); `PrototypeRegistry`; `IStateWriter`; `WorldGrid.ChunkSize` (== 32).
- Produces:
  - `public readonly record struct ResourceCell(int ResourceProtoId, int Amount) { public static readonly ResourceCell Empty = default; public bool IsEmpty => Amount == 0; }`
  - `public sealed class ResourceGrid`:
    - `ResourceGrid(long seed, PrototypeRegistry protos)`
    - `ResourceCell GetResourceAt(int x, int y)` — generates the containing chunk if needed
    - `int Extract(int x, int y, int count)` — returns actually removed; clears the cell at 0; `count <= 0` → 0
    - `void WriteState(IStateWriter writer)`
    - `int GeneratedChunkCount { get; }`
    - `bool IsChunkGenerated(int x, int y)`

- [ ] **Step 1: Create `ResourceCell.cs`**

```csharp
namespace Faketorio.Sim.World;

// 一格的矿:矿 prototype id + 矿量。空格 ⟺ Amount == 0。
// 不变式(同 ItemStack):没有路径产生 ResourceProtoId != 0 && Amount == 0。
public readonly record struct ResourceCell(int ResourceProtoId, int Amount)
{
    public static readonly ResourceCell Empty = default;   // (0, 0)
    public bool IsEmpty => Amount == 0;
}
```

- [ ] **Step 2: Write the failing `ResourceGridTests`**

Create `sim/Faketorio.Sim.Tests/ResourceGridTests.cs`:

```csharp
using Faketorio.Sim.Prototypes;
using Faketorio.Sim.State;
using Faketorio.Sim.World;

namespace Faketorio.Sim.Tests;

public class ResourceGridTests
{
    private static PrototypeRegistry Reg() => PrototypeLoader.LoadFromDirectory("data/base");

    private static ulong Hash(ResourceGrid g)
    {
        var w = new Fnv1aHashWriter();
        g.WriteState(w);
        return w.Hash;
    }

    [Fact]
    public void GetResourceAt_IsRepeatable()
    {
        var g = new ResourceGrid(12345, Reg());
        var a = g.GetResourceAt(200, -140);
        var b = g.GetResourceAt(200, -140);
        Assert.Equal(a, b);
    }

    [Fact]
    public void GetResourceAt_GeneratesContainingChunk()
    {
        var g = new ResourceGrid(1, Reg());
        Assert.Equal(0, g.GeneratedChunkCount);
        g.GetResourceAt(70, 70);
        Assert.True(g.IsChunkGenerated(70, 70));
        Assert.Equal(1, g.GeneratedChunkCount);
        g.GetResourceAt(71, 71);                 // same chunk
        Assert.Equal(1, g.GeneratedChunkCount);
    }

    [Fact]
    public void StarterPatch_CoalAtOrigin_RegardlessOfSeed()
    {
        var reg = Reg();
        int coalId = reg.Get<ResourcePrototype>("coal").Id;
        foreach (long seed in new long[] { 0, 1, 999, -50 })
        {
            var cell = new ResourceGrid(seed, reg).GetResourceAt(6, -8);   // starter patch centre
            Assert.Equal(coalId, cell.ResourceProtoId);
            Assert.Equal(1500, cell.Amount);      // CenterAmount * (radius - 0) / radius
        }
    }

    [Fact]
    public void StarterPatch_CentreRicherThanEdge()
    {
        var g = new ResourceGrid(0, Reg());
        int centre = g.GetResourceAt(6, -8).Amount;       // dist 0 -> 1500
        int near   = g.GetResourceAt(6, -8 + 3).Amount;   // dist 3, radius 4 -> 1500*1/4 = 375
        Assert.Equal(1500, centre);
        Assert.Equal(375, near);
        Assert.True(centre > near);
    }

    [Fact]
    public void Extract_PartialThenFull_ClearsCell()
    {
        var g = new ResourceGrid(0, Reg());
        Assert.Equal(1500, g.GetResourceAt(6, -8).Amount);

        Assert.Equal(100, g.Extract(6, -8, 100));
        Assert.Equal(1400, g.GetResourceAt(6, -8).Amount);

        Assert.Equal(1400, g.Extract(6, -8, 999999));      // over-extract returns what's left
        Assert.Equal(ResourceCell.Empty, g.GetResourceAt(6, -8));
        Assert.True(g.GetResourceAt(6, -8).IsEmpty);

        Assert.Equal(0, g.Extract(6, -8, 10));             // depleted
    }

    [Fact]
    public void Extract_NonPositiveCount_ReturnsZero_NoChange()
    {
        var g = new ResourceGrid(0, Reg());
        var h = Hash(g);                                   // nothing generated yet
        Assert.Equal(0, g.Extract(6, -8, 0));
        Assert.Equal(0, g.Extract(6, -8, -5));
        // 上面两次调用触发了 chunk 生成但没改矿量;和"生成后未 extract"的哈希一致
        var g2 = new ResourceGrid(0, Reg());
        g2.GetResourceAt(6, -8);
        Assert.Equal(Hash(g2), Hash(g));
    }

    [Fact]
    public void Extract_EmptyTile_ReturnsZero()
    {
        var g = new ResourceGrid(0, Reg());
        int ex = 0, ey = 5000;
        for (int x = 0; x < 4096; x++)                     // find a tile with no resource
            if (g.GetResourceAt(x, ey).IsEmpty) { ex = x; break; }
        Assert.True(g.GetResourceAt(ex, ey).IsEmpty);
        Assert.Equal(0, g.Extract(ex, ey, 50));
    }

    [Fact]
    public void AccessOrderIndependent_SameWriteStateHash()
    {
        var reg = Reg();
        var tiles = new List<(int, int)>();
        for (int cx = -1; cx <= 1; cx++)
            for (int cy = -1; cy <= 1; cy++)
                tiles.Add((cx * 40 + 3, cy * 40 + 7));      // spread across 9 chunks

        var g1 = new ResourceGrid(555, reg);
        foreach (var (x, y) in tiles) g1.GetResourceAt(x, y);

        var g2 = new ResourceGrid(555, reg);
        for (int i = tiles.Count - 1; i >= 0; i--) g2.GetResourceAt(tiles[i].Item1, tiles[i].Item2);

        Assert.Equal(Hash(g1), Hash(g2));
    }

    [Fact]
    public void DifferentSeed_DifferentWriteStateHash()
    {
        var reg = Reg();
        ulong H(long seed)
        {
            var g = new ResourceGrid(seed, reg);
            for (int x = 200; x < 328; x += 4)             // 128-wide pure-noise area, no starter patches
                for (int y = 200; y < 328; y += 4)
                    g.GetResourceAt(x, y);
            return Hash(g);
        }
        Assert.NotEqual(H(1), H(2));
    }

    [Fact]
    public void WriteState_ChangesAfterExtract()
    {
        var g = new ResourceGrid(0, Reg());
        g.GetResourceAt(6, -8);
        var before = Hash(g);
        g.Extract(6, -8, 50);
        Assert.NotEqual(before, Hash(g));
    }

    [Fact]
    public void OverlappingStarterPatches_ListOrderWins()
    {
        // 两块重叠的启动矿斑,同一格上列表靠前的胜
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "gen.json"),
            "[{ \"type\": \"item\", \"name\": \"coal\", \"stackSize\": 50 }," +
            " { \"type\": \"item\", \"name\": \"stone\", \"stackSize\": 50 }," +
            " { \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", \"noise\": { \"thresholdQ16\": 44000 }, \"richnessBase\": 400, \"richnessScale\": 6000 }," +
            " { \"type\": \"resource\", \"name\": \"stone\", \"minableResult\": \"stone\", \"noise\": { \"thresholdQ16\": 46000 }, \"richnessBase\": 300, \"richnessScale\": 4000 }," +
            " { \"type\": \"map-gen\", \"name\": \"default\", \"starterPatches\": [" +
            "   { \"resource\": \"coal\",  \"centerX\": 0, \"centerY\": 0, \"radius\": 5, \"centerAmount\": 900 }," +
            "   { \"resource\": \"stone\", \"centerX\": 2, \"centerY\": 0, \"radius\": 5, \"centerAmount\": 900 } ] }]");
        var reg = PrototypeLoader.LoadFromDirectory(dir);
        var g = new ResourceGrid(0, reg);
        // (1,0) is inside both patches; coal is listed first
        Assert.Equal(reg.Get<ResourcePrototype>("coal").Id, g.GetResourceAt(1, 0).ResourceProtoId);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ResourceGridTests"`
Expected: FAIL — `ResourceGrid` does not exist (compile error).

- [ ] **Step 4: Create `ResourceGrid.cs`**

```csharp
using Faketorio.Sim.Prototypes;
using Faketorio.Sim.State;

namespace Faketorio.Sim.World;

// 惰性无限矿脉层。每个 chunk 首次被查询 / Extract 时生成;chunk 内容是
// (seed, tileX, tileY) 的纯函数,无邻块依赖 -> 访问顺序无关(spec §7)。
public sealed class ResourceGrid
{
    private const int Size = WorldGrid.ChunkSize;               // 32

    private readonly long _seed;
    private readonly ResourcePrototype[] _resources;            // 按 Id 升序,决胜用
    private readonly IReadOnlyList<StarterPatch> _starterPatches;
    private readonly int[] _starterPatchResId;                  // 与 _starterPatches 平行:各斑的矿 proto Id

    private readonly Dictionary<long, int[]> _typeId = new();   // key -> 1024 格矿种(0=空)
    private readonly Dictionary<long, int[]> _amount = new();   // key -> 1024 格矿量
    private readonly List<long> _sortedKeys = new();
    private bool _keysDirty;

    public ResourceGrid(long seed, PrototypeRegistry protos)
    {
        _seed = seed;

        var res = new List<ResourcePrototype>();
        MapGenPrototype? mapGen = null;
        for (int i = 0; i < protos.Count; i++)
        {
            switch (protos.GetById(i))
            {
                case ResourcePrototype r: res.Add(r); break;
                case MapGenPrototype m:   mapGen = m; break;
            }
        }
        res.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        _resources = res.ToArray();

        _starterPatches = mapGen?.StarterPatches ?? Array.Empty<StarterPatch>();
        _starterPatchResId = new int[_starterPatches.Count];
        for (int i = 0; i < _starterPatches.Count; i++)
            _starterPatchResId[i] = protos.Get<ResourcePrototype>(_starterPatches[i].Resource).Id;
    }

    public int GeneratedChunkCount => _typeId.Count;
    public bool IsChunkGenerated(int x, int y) => _typeId.ContainsKey(ChunkKey(x, y));

    public ResourceCell GetResourceAt(int x, int y)
    {
        var (type, amount) = Ensure(x, y);
        int i = TileIndex(x, y);
        return type[i] == 0 ? ResourceCell.Empty : new ResourceCell(type[i], amount[i]);
    }

    public int Extract(int x, int y, int count)
    {
        if (count <= 0) return 0;
        var (type, amount) = Ensure(x, y);
        int i = TileIndex(x, y);
        if (type[i] == 0) return 0;
        int take = Math.Min(amount[i], count);
        amount[i] -= take;
        if (amount[i] == 0) type[i] = 0;
        return take;
    }

    public void WriteState(IStateWriter writer)
    {
        if (_keysDirty)
        {
            _sortedKeys.Clear();
            foreach (var k in _typeId.Keys) _sortedKeys.Add(k);
            _sortedKeys.Sort();
            _keysDirty = false;
        }
        for (int s = 0; s < _sortedKeys.Count; s++)
        {
            long key = _sortedKeys[s];
            writer.Write(key);
            int[] type = _typeId[key];
            int[] amount = _amount[key];
            for (int i = 0; i < type.Length; i++)
            {
                writer.Write(type[i]);
                writer.Write(amount[i]);
            }
        }
    }

    // ---- chunk math:与 WorldGrid 完全一致(spec Global Constraints) ----
    private static long ChunkKey(int x, int y)
    {
        int cx = x >> 5, cy = y >> 5;
        return ((long)cx << 32) | (uint)cy;
    }

    private static int TileIndex(int x, int y)
        => (y & (Size - 1)) * Size + (x & (Size - 1));

    private (int[] type, int[] amount) Ensure(int x, int y)
    {
        long key = ChunkKey(x, y);
        if (_typeId.TryGetValue(key, out var type))
            return (type, _amount[key]);

        type = new int[Size * Size];
        var amount = new int[Size * Size];
        Generate(x, y, type, amount);
        _typeId[key] = type;
        _amount[key] = amount;
        _keysDirty = true;
        return (type, amount);
    }

    private void Generate(int anyX, int anyY, int[] type, int[] amount)
    {
        int baseX = (anyX >> 5) << 5;
        int baseY = (anyY >> 5) << 5;

        for (int ly = 0; ly < Size; ly++)
            for (int lx = 0; lx < Size; lx++)
            {
                int wx = baseX + lx, wy = baseY + ly;
                int i = ly * Size + lx;

                // --- 噪声:每种矿独立场,归一化超出量最大者胜,平手取 Id 小 ---
                int bestIdx = -1, bestScore = 0, bestExcess = 0;
                for (int r = 0; r < _resources.Length; r++)
                {
                    var L = _resources[r].Layer;
                    int v = ValueNoise.Fbm(_seed, L.FieldId, wx, wy, L.LatticeSize, L.Octaves);
                    int excess = v - L.ThresholdQ16;
                    if (excess <= 0) continue;
                    int score = (int)(((long)excess << 16) / (65536 - L.ThresholdQ16));
                    if (bestIdx < 0 || score > bestScore)
                    {
                        bestIdx = r; bestScore = score; bestExcess = excess;
                    }
                }
                if (bestIdx >= 0)
                {
                    var rp = _resources[bestIdx];
                    type[i] = rp.Id;
                    amount[i] = rp.RichnessBase + (int)(((long)rp.RichnessScale * bestExcess) >> 16);
                }

                // --- 启动矿斑叠加:列表顺序,靠前的胜,覆盖噪声 ---
                for (int p = 0; p < _starterPatches.Count; p++)
                {
                    var sp = _starterPatches[p];
                    long dx = wx - sp.CenterX, dy = wy - sp.CenterY;
                    int dist = ValueNoise.Isqrt(dx * dx + dy * dy);
                    if (dist >= sp.Radius) continue;
                    int amt = sp.CenterAmount * (sp.Radius - dist) / sp.Radius;
                    if (amt <= 0) continue;
                    type[i] = _starterPatchResId[p];
                    amount[i] = amt;
                    break;
                }
            }
    }
}
```

- [ ] **Step 5: Run `ResourceGridTests` to verify they pass**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ResourceGridTests"`
Expected: all PASS. If `Extract_EmptyTile_ReturnsZero` cannot find an empty tile in `x in [0, 4096)` at `y = 5000` (very unlikely — thresholds are high), widen the scan range; do not weaken the assertion.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. Baseline + Task 1/2 + `ResourceGridTests`.

- [ ] **Step 7: Commit**

```bash
git add sim/Faketorio.Sim/World/ResourceCell.cs sim/Faketorio.Sim/World/ResourceGrid.cs sim/Faketorio.Sim.Tests/ResourceGridTests.cs
git commit -m "$(cat <<'EOF'
feat(world): ResourceGrid — lazy per-chunk ore generation + Extract

32x32 chunks keyed like WorldGrid, generated on first touch as a pure
function of (seed, x, y): per-resource independent noise field, largest
normalized excess wins (ties by proto Id), then seed-independent starter
patches overlaid (linear falloff, list order wins). Extract mirrors
Inventory.Remove — clears the cell at 0, guards count <= 0. WriteState
walks sorted chunk keys.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

---

## Task 4: `Simulation` wiring + integration/determinism tests

**Files:**
- Modify: `sim/Faketorio.Sim/Simulation.cs`
- Modify: `sim/Faketorio.Sim.Tests/SimulationTests.cs`, `sim/Faketorio.Sim.Tests/DeterminismTests.cs`

**Interfaces:**
- Consumes: `ResourceGrid(long seed, PrototypeRegistry)` / `GetResourceAt` / `Extract` / `GeneratedChunkCount` / `IsChunkGenerated` / `WriteState` (Task 3); `ResourcePrototype` (Task 2).
- Produces:
  - `Simulation(PrototypeRegistry prototypes, long worldSeed = 0)`
  - `Simulation.Resources` — `public ResourceGrid Resources { get; }`

- [ ] **Step 1: Write the failing Simulation integration tests**

Append to `sim/Faketorio.Sim.Tests/SimulationTests.cs` (inside `SimulationTests`; `NewSim()` helper already exists):

```csharp
    [Fact]
    public void Resources_StartsWithNoGeneratedChunks()
    {
        Assert.Equal(0, NewSim().Resources.GeneratedChunkCount);
    }

    [Fact]
    public void GetResourceAt_GeneratesChunk()
    {
        var sim = NewSim();
        sim.Resources.GetResourceAt(6, -8);
        Assert.True(sim.Resources.IsChunkGenerated(6, -8));
        Assert.Equal(1, sim.Resources.GeneratedChunkCount);
    }

    [Fact]
    public void WriteState_CoversResourceLayer()
    {
        var sim = NewSim();
        var before = sim.ComputeStateHash();
        sim.Resources.GetResourceAt(6, -8);      // starter patch -> non-empty chunk
        Assert.NotEqual(before, sim.ComputeStateHash());
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests.Resources_StartsWithNoGeneratedChunks"`
Expected: FAIL — `Simulation.Resources` does not exist (compile error).

- [ ] **Step 3: Wire `ResourceGrid` into `Simulation`**

In `sim/Faketorio.Sim/Simulation.cs`:

1. Add `using Faketorio.Sim.World;` — already present (`Simulation.cs:6`).
2. Add field + property near `Belts` / the other layer properties, and set `Resources` in the constructor (it needs the seed, so it can't be a `= new()` initializer). Current ctor is `public Simulation(PrototypeRegistry prototypes) => Prototypes = prototypes;` — change to a block body:

```csharp
    public BeltNetwork Belts { get; } = new();
    public Inventories Inventories { get; } = new();
    public ResourceGrid Resources { get; }

    private readonly long _worldSeed;

    public long Tick { get; private set; }
    public int RejectedCommandCount { get; private set; }

    private readonly CommandQueue _commands = new();

    public Simulation(PrototypeRegistry prototypes, long worldSeed = 0)
    {
        Prototypes = prototypes;
        _worldSeed = worldSeed;
        Resources = new ResourceGrid(worldSeed, prototypes);
    }
```

(Keep the existing `Prototypes` / `World` / `Entities` property declarations exactly as they are; only `Resources` + `_worldSeed` are new, and the ctor changes from expression body to block body.)

3. In `WriteState`, after `writer.Write(RejectedCommandCount);` add:

```csharp
        writer.Write(_worldSeed);
```

4. At the end of `WriteState`, after `Inventories.WriteState(writer);` add:

```csharp
        Resources.WriteState(writer);
```

- [ ] **Step 4: Run the Simulation integration tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests"`
Expected: all PASS (existing + 3 new).

- [ ] **Step 5: Commit**

```bash
git add sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/SimulationTests.cs
git commit -m "$(cat <<'EOF'
feat(sim): wire ResourceGrid into Simulation — worldSeed + Resources

Simulation gains a worldSeed ctor arg (default 0, existing callers
unchanged) and a Resources property. WriteState writes worldSeed after
RejectedCommandCount and the generated resource chunks after Inventories.
Step unchanged — the ore layer is passive; mining is P5/P10.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_013Gy1wD4anyWko5qaKDAaaa
EOF
)"
```

- [ ] **Step 6: Write the failing determinism tests**

Append to `sim/Faketorio.Sim.Tests/DeterminismTests.cs` (inside `DeterminismTests`):

```csharp
    private static List<ulong> RunResourceScenario(long seed)
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"), seed);
        var hashes = new List<ulong>();
        for (int t = 0; t < 30; t++)
        {
            if (t == 0)
                for (int x = 180; x < 260; x += 8)          // pure-noise sweep across chunks
                    for (int y = 180; y < 260; y += 8)
                        sim.Resources.GetResourceAt(x, y);
            if (t == 5)  sim.Resources.Extract(6, -8, 40);   // coal starter patch
            if (t == 12) sim.Resources.Extract(-9, -6, 40);  // iron starter patch
            if (t == 20) sim.Resources.Extract(6, -8, 25);
            sim.Step();
            hashes.Add(sim.ComputeStateHash());
        }
        return hashes;
    }

    [Fact]
    public void ResourceScenario_SameSeedSameCommands_SameHashEveryTick()
        => Assert.Equal(RunResourceScenario(777), RunResourceScenario(777));

    [Fact]
    public void ResourceScenario_DifferentSeed_DifferentHash()
        => Assert.NotEqual(RunResourceScenario(1), RunResourceScenario(2));
```

- [ ] **Step 7: Run the determinism tests**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~DeterminismTests"`
Expected: all PASS. The pre-existing golden scenarios (`SameCommands_SameHashEveryTick`, `BeltScenario_SameCommands_SameHashEveryTick`, `HashChangesWhenWorldChanges`, `HashCoversEntityRotation`) still pass — `RunScenario` / `RunBeltScenario` were not edited; `Simulation.WriteState` now writes one extra `long` (`_worldSeed == 0`) plus an empty `Resources` (no generated chunks), which shifts their absolute hashes identically across both runs, and every golden assertion is a run-vs-run comparison.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test sim/Faketorio.Sim.Tests`
Expected: PASS. 189 baseline + all new: `DeterministicHashTests` (3) + `ValueNoiseTests` (9) + `PrototypeLoaderTests` (+11) + `ResourceGridTests` (11) + `SimulationTests` (+3) + `DeterminismTests` (+2).

- [ ] **Step 9: Commit**

```bash
git add sim/Faketorio.Sim.Tests/DeterminismTests.cs
git commit -m "$(cat <<'EOF'
test(sim): determinism coverage for the resource layer

Same seed + same command/extract sequence -> identical per-tick hashes
over 30 ticks; different seed -> different hashes (pure-noise sweep plus
starter-patch extraction). Golden scenarios untouched.

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
| §2 file structure (7 new files, 3 modified) | Task 1 (DeterministicHash, ValueNoise), Task 2 (ResourcePrototype, resources.json, map-gen.json, items.json, PrototypeLoader), Task 3 (ResourceCell, ResourceGrid), Task 4 (Simulation) |
| §3.1 `DeterministicHash` verbatim constants + `Mix` + `Hash` order | Task 1 Step 1; Global Constraints; pinned test Step 2-3 |
| §3.2 single-octave value noise (lattice coords via arithmetic shift, `LatticeQ16 & 0xFFFF`, `fxQ`, `Ease`, bilinear `Lerp`) | Task 1 Step 7 `Octave` |
| §3.3 fBm loop-summed `norm` (no closed form) | Task 1 Step 7 `Fbm`; Global Constraints; `Fbm_SingleOctave_NoDivideByZero` test |
| §3.4 `Isqrt`, `n < 0` throws | Task 1 Step 7 + `ValueNoiseTests` |
| §4 `NoiseLayer` / `StarterPatch` / `ResourcePrototype` (`Layer { get; internal set; }`) / `MapGenPrototype` | Task 2 Step 1 |
| §4.1 post-`AssignIds` resolve pass (fieldId 0→1+Id, lattice/octaves 0→map-gen default) | Task 2 Step 8 `ResolveAndValidateMapGen` |
| §4.2 data files (resources.json, map-gen.json, items.json additions) | Task 2 Steps 2-4 |
| §4.3 validation (pow2 lattice, octave range, warp/ridge reject, starter-patch refs, minableResult ref, richnessBase ≥ 1) + plan additions (map-gen required only w/ resources; threshold 1..65535) | Task 2 Step 8 + the plan note after it; `PrototypeLoaderTests` Step 5 (one negative per rule) |
| §5 `ResourceGrid` — ctor, lazy `Ensure`/`Generate`, §5.2 per-tile resource decision, §5.3 starter-patch overlay, §5.4 `GetResourceAt`, §5.5 `Extract` (`count<=0` guard, clear at 0), §5.6 `WriteState` sorted keys | Task 3 Step 4 |
| §5 `ResourceCell` invariant | Task 3 Step 1 + `Extract_*` tests |
| §5 chunk math identical to `WorldGrid` | Task 3 Step 4 (`ChunkKey`/`TileIndex`); Global Constraints |
| §5 graceful with no resources / no map-gen | Task 3 Step 4 ctor (`mapGen?.StarterPatches ?? Array.Empty`, empty `_resources` → all cells empty) |
| §6 `Simulation` — `worldSeed` ctor arg, `_worldSeed` field, `Resources` property, `WriteState` seed after `RejectedCommandCount`, `Resources.WriteState` after `Inventories`, `Step` unchanged | Task 4 Step 3 |
| §7 determinism (no float/double in state-feeding math, no Dictionary iteration in WriteState, no System.Random, lazy-gen order independence, worldSeed in hash, golden scenarios unaffected) | Task 1 (integer noise), Task 3 Step 4 (sorted-key WriteState), Task 4 Steps 6-7; `ResourceGridTests.AccessOrderIndependent_*` / `DifferentSeed_*`; `DeterminismTests` additions |
| §8 future layers (trees/water) — deferred, only `NoiseLayer.Warp`/`Ridge` reserved and rejected | Task 2 Step 1 (`NoiseLayer` has `Warp`/`Ridge`), Step 8 (rejected in validation) — no code |
| §9 tests (`DeterministicHashTests`, `ValueNoiseTests`, `ResourceGridTests`, `PrototypeLoaderTests`+, `SimulationTests`+, `DeterminismTests`+) | Tasks 1-4 test steps |
| §10 four-task split | Tasks 1 / 2 / 3 / 4 match §10 |

No gaps. Two deliberate plan extensions of spec §4.3 are flagged inline (Task 2 Step 8 note): map-gen required only when resources exist; `ThresholdQ16` range check.

**2. Placeholder scan:** No "TBD"/"TODO"/"handle edge cases"/"similar to Task N". Every code step has complete code. Every test step has full bodies. The one fill-in — the pinned hash value in `DeterministicHashTests` — has explicit capture-and-paste instructions (Step 3), which is the standard golden-value pattern, not an unspecified blank.

**3. Type consistency:**
- `DeterministicHash.Hash(long, int, int, int) → ulong` — defined Task 1 Step 1, called in Task 1 Step 7 (`LatticeQ16`).
- `ValueNoise.Fbm(long seed, int field, int tileX, int tileY, int latticeSize, int octaves) → int` — defined Task 1 Step 7, called in Task 3 Step 4 `Generate` with `(_seed, L.FieldId, wx, wy, L.LatticeSize, L.Octaves)`. Matches.
- `ValueNoise.Isqrt(long) → int` — defined Task 1 Step 7, called in Task 3 Step 4 with `Isqrt(dx*dx + dy*dy)` where `dx`/`dy` are `long`. Matches.
- `NoiseLayer(int FieldId, int LatticeSize, int Octaves, int ThresholdQ16, bool Warp = false, bool Ridge = false)` — defined Task 2 Step 1; constructed in Task 2 Step 7 (`ParseNoiseLayer`, named args) and mutated via `with { }` in Step 8; read in Task 3 Step 4 (`L.FieldId`, `L.LatticeSize`, `L.Octaves`, `L.ThresholdQ16`). Matches.
- `ResourcePrototype` members `MinableResult` / `Layer` (`internal set`) / `RichnessBase` / `RichnessScale` — defined Task 2 Step 1; `Layer` reassigned in Step 8; read in Task 3 Step 4 (`_resources[r].Layer`, `rp.RichnessBase`, `rp.RichnessScale`, `rp.Id`). Matches.
- `MapGenPrototype` members `DefaultLatticeSize` / `DefaultOctaves` / `StarterPatches` — defined Task 2 Step 1; read in Step 8 and Task 3 Step 4 ctor. Matches.
- `StarterPatch(string Resource, int CenterX, int CenterY, int Radius, int CenterAmount)` — defined Task 2 Step 1; constructed in Step 7 (`ParseStarterPatches`); read in Task 3 Step 4 (`sp.CenterX` etc.) and asserted in `PrototypeLoaderTests` Step 5 (`new StarterPatch("coal", 6, -8, 4, 1500)`). Matches.
- `ResourceCell(int ResourceProtoId, int Amount)` + `Empty` + `IsEmpty` — defined Task 3 Step 1; used in Task 3 Step 4 and all of `ResourceGridTests` / `SimulationTests`. Matches.
- `ResourceGrid(long seed, PrototypeRegistry protos)` + `GetResourceAt` / `Extract` / `WriteState` / `GeneratedChunkCount` / `IsChunkGenerated` — defined Task 3 Step 4; consumed in Task 4 Step 3 (`new ResourceGrid(worldSeed, prototypes)`) and Steps 1/6 tests. Matches.
- `Simulation(PrototypeRegistry, long worldSeed = 0)` + `Resources` — defined Task 4 Step 3; `new Simulation(..., seed)` in `DeterminismTests` Step 6; existing `new Simulation(prototypes)` still valid (default arg). Matches.
- `PrototypeRegistry` methods used: `Get<T>(name)`, `TryGet<T>(name, out T)`, `GetById(int)`, `Count` — all exist in `sim/Faketorio.Sim/Prototypes/PrototypeRegistry.cs`. `GetById` returns `PrototypeBase`, pattern-matched with `is ResourcePrototype` / `is MapGenPrototype`. Matches.
- `IStateWriter.Write(long)` / `Write(int)` — both exist; `ResourceGrid.WriteState` uses `Write(long)` for the key and `Write(int)` per cell. Matches.
- `WorldGrid.ChunkSize` — `public const int ChunkSize = 32` (`WorldGrid.cs:7`). Used as `WorldGrid.ChunkSize` in Task 3. Matches.
- `Fnv1aHashWriter` — `new Fnv1aHashWriter()` + `.Hash`, used in `ResourceGridTests` (`Hash` helper). Matches existing usage in `BeltLinePoolTests` / `InventoryTests`.
- Item names in data/tests: `coal`, `iron-ore` exist; `copper-ore`, `stone` added Task 2 Step 4. Resource names `coal`/`iron-ore`/`copper-ore`/`stone` added Task 2 Step 2.

Consistent throughout.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-09-04-m1-plan6-resource-generation.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

**Which approach?**
