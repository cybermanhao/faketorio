# Faketorio

A Factorio-style factory-building simulation game, built with Godot 4.5.1 Mono + C#/.NET 8.

## Overview

Faketorio is a from-scratch factory automation sim: mining → smelting → assembly → belt logistics → electricity, all built around one **iron rule** — the simulation layer must be fully deterministic: the same seed plus the same command sequence produces byte-identical state, on any run, on any machine.

## Architecture

Three separated layers:

| Layer | Tech | Responsibility |
|---|---|---|
| **Data** | JSON + plain C# POCOs (`Prototypes`) | Mirrors Factorio's prototype system, engine-agnostic, can be generated/validated in bulk outside the editor |
| **Simulation** | `Faketorio.Sim` (pure C#, zero Godot dependency) | Fixed 60 UPS, headless-testable (xUnit), state canonically serialized through `IStateWriter` + FNV-1a hashing |
| **Presentation** | Godot nodes | Read-only rendering of simulation state; every player action goes through the command queue, never mutates state directly |

Why POCO + JSON instead of Godot `Resource`: (1) Godot C# `Resource` subclasses can't be instantiated outside the engine runtime, which would break headless xUnit testing; (2) data can be generated/validated in bulk outside the editor; (3) Factorio itself keeps its data external (Lua) rather than baked into the engine.

### The determinism rule

- Fixed tick (60 UPS); the simulation layer **forbids `float`/`double` in any state-affecting computation** — positions, energy, and progress are all `int`/`long`; ratios use Q16.16 fixed-point.
- No reliance on containers with unspecified iteration order (`Dictionary` is point-lookup only; anything serialized is explicitly sorted first).
- Same seed + same command sequence must produce an identical full-state hash at every tick — this is a mandatory acceptance gate before any subsystem merges.

See [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](docs/superpowers/specs/2026-07-03-faketorio-design.md) for the full design doc.

## Current Status

M1 (vertical slice) loop: mining → smelting → assembly → storage, all electricity-driven. Merged so far:

- **P1** Simulation core foundation (prototype system / entity pool / world grid / command queue / state hashing)
- **P2/P3** Transport belts (FFF-176 gap representation, merge/split, corner hand-off)
- **P4** Inventory (`ItemStack` + `Inventory` + chest wiring)
- **P5** Player (movement + collision, hand-mining, hand-crafting, starting kit)
- **P6** Ore generation (fixed-point value noise, seed-deterministic)
- **P7** Electrical grid (pole connectivity + supply/demand settlement + fuel generator)

See the progress snapshot and remaining roadmap in [`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md).

## Getting Started

Requires the .NET 8 SDK.

```bash
# Run the full simulation test suite
dotnet test sim/Faketorio.Sim.Tests

# Run a single test class/method
dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests"

# Release build (should be 0 warnings, 0 errors)
dotnet build -c Release

# Benchmark + regression baseline (state-hash golden + UPS/per-phase timings)
dotnet run -c Release --project sim/Faketorio.Sim.Bench      # see bench/README.md
```

Open the repo root in the Godot editor (requires Godot 4.5.1 Mono).

## Project Structure

```
sim/
  Faketorio.Sim/            Simulation core (pure C#, zero Godot dependency)
    Prototypes/             Data prototypes (items/entities/recipes/resources/player/electric grid...)
    Entities/               Generic generational-id entity pool
    World/                  World grid + ore generation (value noise)
    Belts/                  Transport belts (FFF-176 transport lines)
    Items/                  Inventory system
    Player/                 Player state (movement/mining/crafting)
    Electric/               Electrical grid (connectivity/settlement/generators)
    Commands/               Command queue (the sole entry point for player actions)
    State/                  Canonical serialization + FNV-1a hashing
  Faketorio.Sim.Tests/      Headless xUnit tests
data/base/                  Game data (JSON prototypes)
docs/superpowers/
  specs/                    Architecture design docs (design baseline per subsystem)
  plans/                    Implementation plans (task breakdown, with merge status noted)
```

## Documentation

- Master design doc: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](docs/superpowers/specs/2026-07-03-faketorio-design.md)
- M1 remaining roadmap + progress snapshot: [`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md)
- Per-subsystem design specs and implementation plans live under `docs/superpowers/specs/` and `docs/superpowers/plans/`, dated by filename; each merged plan's header notes the corresponding commit range on `main`.

See [`README.md`](README.md) for the Chinese version.
