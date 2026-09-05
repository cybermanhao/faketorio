# M1 Plan 12 — CI 基准场景 + 回归基线 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给仓库加一个独立 benchmark 项目 + 固定种子中型工厂场景 + `IStepProfiler` 分阶段计时 + `bench/golden.json`(状态哈希 golden 值 + 自校准性能红线)+ 仓库首个 GitHub Actions CI(test job + bench job)。

**Architecture:** `Faketorio.Sim` 只新增一个 `IStepProfiler?` 接口引用,`Step()` 每阶段包一对 `Begin/End`,`null` 时零成本、不进哈希。新 console 项目 `Faketorio.Sim.Bench` 里:可组合 `IScenarioPart` 拼出场景(采矿链 + 加工链解耦,建造期两处一次性直接注入解决燃料自举死锁)、`StopwatchStepProfiler` 实现计时、CLI 跑"warmup → 净计时趟(定顶线)→ 分阶段趟(只进报告)"、比对 `golden.json`、按退出码表红/绿。`Faketorio.Sim.Tests` 引用 Bench 项目复用 `ScenarioBuilder`,加小规模哈希兜底测试。

**Tech Stack:** C# / .NET 8 (`net8.0`)、xUnit 2.9、`System.Text.Json`(BCL,无新 NuGet)、GitHub Actions(`actions/checkout@v4`、`actions/setup-dotnet@v4`、`actions/upload-artifact@v4`)。

**Spec:** [`docs/superpowers/specs/2026-09-05-m1-plan12-ci-benchmark-baseline-design.md`](../specs/2026-09-05-m1-plan12-ci-benchmark-baseline-design.md)

## Global Constraints

- **确定性铁律**:`IStepProfiler` 及其实现只持有 `long[]` 计时累加,绝不触碰任何进 `WriteState` 的状态;`_profiler` 不进 `WriteState`。sim 层不得因 `_profiler` 是否为 `null` 走不同的**状态相关**分支——唯一允许的差异是"是否调用计时钩子"。测试 `ProfilerAttached_DoesNotChangeHash` 强制这一点。
- sim 层不引入 `float`/`double` 参与任何状态计算。计时的 ns 换算只在 `Faketorio.Sim.Bench` 里做,不回流 sim。
- 场景构造用 public `Command`;**唯二**允许的建造期直接注入:(a) `ElectricGrid.SetFuelBufferJ(genId, joules)` 给发电机灌初始燃料缓冲;(b) `Inventory.Insert(ironOreId, n, stackSize)` 给每个单元「铁料源」large-chest 灌初始铁矿石。两者都只在 `ScenarioBuilder.Build` 期间发生一次,建造完成后的 step 阶段零注入。
- 所有场景遍历 / `Submit` 顺序固定,不依赖任何 `Dictionary` 迭代序或哈希序。同 `(scale, seed)` 必然产生逐 tick 字节级相同的模拟。
- `Faketorio.Sim` 保持零 Godot 依赖;`Faketorio.Sim.Bench` 只依赖 `Faketorio.Sim` + BCL,无额外 NuGet。
- 新 CI 用 `actions/checkout@v4`、`actions/setup-dotnet@v4`(`dotnet-version: '8.0.x'`)、`actions/upload-artifact@v4`,单平台 `ubuntu-latest`,触发 `push: [main]` + 所有 `pull_request`。
- 每个 commit message 结尾带:
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01KwHR5m3PgiyWtkMRcC21Jb
  ```
- 不改现有 `DeterminismTests` / `DeterministicHashTests`。
- `bench/golden.json` 里 `baselineNsPerTick <= 0` 表示"未校准" → 性能门禁 SKIP;哈希门禁始终生效。

## 参考:现有 sim API(实现时照这个签名用,勿猜)

- `new Simulation(PrototypeRegistry prototypes, long worldSeed = 0)` — `Simulation.cs:38`。构造后 `Submit` 命令,`Step()` 时才 apply。
- `sim.Submit(in Command)`;`Command { CommandType Type; int ProtoId; int X; int Y; byte Rotation; int Count; }`;`CommandType.PlaceEntity = 1`(用 `ProtoId`/`X`/`Y`/`Rotation`)。
- `sim.Step()`;`sim.Tick`(long);`sim.ComputeStateHash() -> ulong`;`sim.RejectedCommandCount`(int)。
- `sim.Prototypes.Get<T>(string name) -> T`(`T : PrototypeBase`,有 `.Id`);`sim.Prototypes.TryGet<T>(name, out T)`;`sim.Prototypes.GetById(int) -> PrototypeBase`。
- `sim.World.GetEntityAt(int x, int y) -> EntityId`(`EntityId { int Index; int Generation; bool IsValid; }`,`EntityId.Invalid`)。**实体只有在包含其 `PlaceEntity` 的那次 `Step()` 之后才存在。**
- `sim.Inventories.GetInventoryId(EntityId entity, int role = 0) -> InventoryId`;`sim.Inventories.Get(InventoryId) -> Inventory`。role 1 = 机器输入,role 2 = 机器输出,role 0 = 容器 / 发电机燃料。
- `Inventory.Insert(int itemProtoId, int count, int stackSize) -> int`(实收数);`Inventory.CountOf(int itemProtoId) -> int`;`Inventory.SlotCount`。
- `sim.ElectricGrid.SetFuelBufferJ(EntityId, long)`;`sim.ElectricGrid.GetFuelBufferJ(EntityId) -> long`;`sim.ElectricGrid.GetSatisfaction(EntityId) -> Q16`;`sim.ElectricGrid.FindNetworkAt(int x,int y) -> NetworkId`(`.IsValid`)。
- `sim.Resources.GetResourceAt(int x, int y) -> ResourceCell`(`ResourceCell { int ResourceProtoId; int Amount; bool IsEmpty; }`)。惰性生成,`Step` 前即可查。
- `Q16 { int Raw; static Q16 One; static Q16 Zero; long Mul(long); static Q16 FromRatio(long,long); }`。
- prototypes(`data/base/`):`electric-mining-drill`(2×2, `MiningDrillPrototype.EnergyUsageJPerTick`, `MiningSpeed = Q16.One`)、`stone-furnace`(2×2, `category "smelting"`, in 1 / out 1, 90kW)、`transport-belt-basic`(1×1, `speedSubTilesPerTick 8`)、`inserter-basic`(1×1, `rotationTimeSeconds 0.5`, 5kW)、`small-electric-pole`(1×1, wire 7, supply 2 Chebyshev)、`burner-generator`(2×2, `powerOutput 90kW` → `PowerOutputJPerTick` = 90000/60 = 1500, `fuelItemName "coal"`)、`wooden-chest`(1×1, 16 slots)、`large-chest`(2×3, 48 slots)。
- `coal` item `fuelValue "4MJ"` = 4_000_000 J,`stackSize 50`。`iron-ore` / `iron-plate` `stackSize` 50 / 100。recipe `iron-plate`:`category "smelting"`,`3.2s`,`1 iron-ore -> 1 iron-plate`。**熔炉自动匹配 recipe,不需要 `SetRecipe`。**
- 采矿机产出格 = `(x + Delta(rot).dx * TileWidth, y + Delta(rot).dy * TileHeight)`,产出格上有带则塞带、否则塞该格容器(`Simulation.cs:660`)。`BeltNetwork.Delta(byte)`:`0/1/2/3 = N/E/S/W = (0,-1)/(1,0)/(0,1)/(-1,0)`。
- 机械臂(P11):`pickup 格 = anchor - Delta(rot)`,`dropoff 格 = anchor + Delta(rot)`,伸手各 1 格,身后抓身前放。
- 传送带线间交接(`Simulation.cs:88-102`):上游线出口格 `Tiles[0] + Delta(Direction)` 命中下游线的 `Tiles[^1]` 时,把上游 front 物品塞给下游 back。两条垂直的带这样接成拐角。相邻同向带自动 merge 成一条线。

---

### Task 1: `IStepProfiler` + wire into `Simulation.Step()`

**Files:**
- Create: `sim/Faketorio.Sim/Profiling/StepPhase.cs`
- Create: `sim/Faketorio.Sim/Profiling/IStepProfiler.cs`
- Modify: `sim/Faketorio.Sim/Simulation.cs`(字段区 ~`:30`、构造 `:38`、`Step()` `:56`-`:104`)
- Test: `sim/Faketorio.Sim.Tests/StepProfilerWiringTests.cs`

**Interfaces:**
- Consumes: 现有 `Simulation` / `Command` / `CommandType` API(见上)。
- Produces:
  - `namespace Faketorio.Sim.Profiling; public enum StepPhase { Commands, Player, Electric, Machines, MiningDrills, Inserters, BeltAdvance, BeltHandoff }`(8 值,顺序固定)。
  - `namespace Faketorio.Sim.Profiling; public interface IStepProfiler { void Begin(StepPhase phase); void End(StepPhase phase); }`
  - `Simulation` 构造新签名:`public Simulation(PrototypeRegistry prototypes, long worldSeed = 0, IStepProfiler? profiler = null)`。

- [ ] **Step 1: 写失败测试**

`sim/Faketorio.Sim.Tests/StepProfilerWiringTests.cs`:

```csharp
using Faketorio.Sim.Commands;
using Faketorio.Sim.Entities;
using Faketorio.Sim.Prototypes;
using Faketorio.Sim.Profiling;

namespace Faketorio.Sim.Tests;

public class StepProfilerWiringTests
{
    private sealed class RecordingProfiler : IStepProfiler
    {
        public readonly int[] Begins = new int[8];
        public readonly int[] Ends = new int[8];
        private readonly int[] _open = new int[8];
        public int MaxConcurrent;

        public void Begin(StepPhase p) { Begins[(int)p]++; _open[(int)p]++; MaxConcurrent = Math.Max(MaxConcurrent, _open[(int)p]); }
        public void End(StepPhase p) { Ends[(int)p]++; _open[(int)p]--; }
        public void ResetCounts() { Array.Clear(Begins); Array.Clear(Ends); Array.Clear(_open); MaxConcurrent = 0; }
    }

    private static Simulation NewSim(IStepProfiler? prof = null)
        => new(PrototypeLoader.LoadFromDirectory("data/base"), 0, prof);

    [Fact]
    public void RecordingProfiler_EveryPhaseBalancedEachTick()
    {
        var prof = new RecordingProfiler();
        var sim = NewSim(prof);
        int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        int belt = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id;
        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 0, Y = 0 });
        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = belt, X = 2, Y = 0, Rotation = 1 });
        sim.Step();                 // drain placement
        prof.ResetCounts();

        for (int t = 0; t < 5; t++) sim.Step();

        for (int p = 0; p < 8; p++)
            Assert.Equal(prof.Begins[p], prof.Ends[p]);          // 配对
        Assert.Equal(0, prof.MaxConcurrent > 1 ? 0 : 0);         // 占位:见下
        Assert.True(prof.MaxConcurrent <= 1, "phase 不应嵌套/重入");
        Assert.Equal(5, prof.Begins[(int)StepPhase.Commands]);
        Assert.Equal(5, prof.Begins[(int)StepPhase.Player]);
        Assert.Equal(5, prof.Begins[(int)StepPhase.Electric]);
        Assert.Equal(5, prof.Begins[(int)StepPhase.BeltAdvance]);
        Assert.Equal(5, prof.Begins[(int)StepPhase.BeltHandoff]);
        Assert.Equal(10, prof.Begins[(int)StepPhase.Machines]);      // pre + post
        Assert.Equal(10, prof.Begins[(int)StepPhase.MiningDrills]);
        Assert.Equal(10, prof.Begins[(int)StepPhase.Inserters]);
    }

    [Fact]
    public void ProfilerAttached_DoesNotChangeHash()
    {
        List<ulong> Run(bool withProfiler)
        {
            var sim = NewSim(withProfiler ? new RecordingProfiler() : null);
            int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
            var hashes = new List<ulong>();
            for (int t = 0; t < 30; t++)
            {
                if (t % 4 == 0)
                    sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = t, Y = 1 });
                sim.Step();
                hashes.Add(sim.ComputeStateHash());
            }
            return hashes;
        }
        Assert.Equal(Run(false), Run(true));
    }
}
```

（删掉那行占位 `Assert.Equal(0, ...)` —— 它是提醒你别留废断言，实现时直接去掉。）

- [ ] **Step 2: 跑，确认编译失败**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~StepProfilerWiringTests"`
Expected: 编译失败——`Faketorio.Sim.Profiling` / `IStepProfiler` 不存在,`Simulation` 构造没有第三个参数。

- [ ] **Step 3: 建两个新文件**

`sim/Faketorio.Sim/Profiling/StepPhase.cs`:
```csharp
namespace Faketorio.Sim.Profiling;

/// Step() 内的计时阶段。顺序即 (int) 索引,勿重排。
public enum StepPhase
{
    Commands = 0,
    Player = 1,
    Electric = 2,
    Machines = 3,
    MiningDrills = 4,
    Inserters = 5,
    BeltAdvance = 6,
    BeltHandoff = 7,
}
```

`sim/Faketorio.Sim/Profiling/IStepProfiler.cs`:
```csharp
namespace Faketorio.Sim.Profiling;

/// Step() 每阶段前后回调。实现只做计时,绝不碰任何进 WriteState 的状态。
/// 同一 phase 一个 tick 内可被 Begin/End 多次(如 Machines 的 pre + post),累加。
/// 不嵌套:一对 Begin/End 之间不会再来同 phase 的 Begin。
public interface IStepProfiler
{
    void Begin(StepPhase phase);
    void End(StepPhase phase);
}
```

- [ ] **Step 4: `Simulation` 加字段 + 构造参数**

`sim/Faketorio.Sim/Simulation.cs`:顶部加 `using Faketorio.Sim.Profiling;`。字段区(`_worldSeed` 附近):
```csharp
    private readonly IStepProfiler? _profiler;
```
构造(`:38`):
```csharp
    public Simulation(PrototypeRegistry prototypes, long worldSeed = 0, IStepProfiler? profiler = null)
    {
        _profiler = profiler;
        // ... 现有函数体不变 ...
```

- [ ] **Step 5: `Step()` 每阶段包 `Begin/End`**

`Step()` 里按下表插入调用(执行顺序、逻辑一律不变,只加钩子)。用局部 helper 保持可读:
```csharp
    public void Step()
    {
        _profiler?.Begin(StepPhase.Commands);
        var commands = _commands.BeginTick();
        for (int i = 0; i < commands.Length; i++) Apply(in commands[i]);
        _profiler?.End(StepPhase.Commands);

        _profiler?.Begin(StepPhase.Player);
        PlayerWalk(); PlayerMine(); PlayerCraft();
        _profiler?.End(StepPhase.Player);

        _profiler?.Begin(StepPhase.Electric);
        ElectricGeneratorsRegisterSupply();
        _profiler?.End(StepPhase.Electric);

        _profiler?.Begin(StepPhase.Machines);     MachinesTickPreSettle();      _profiler?.End(StepPhase.Machines);
        _profiler?.Begin(StepPhase.MiningDrills); MiningDrillsTickPreSettle();  _profiler?.End(StepPhase.MiningDrills);
        _profiler?.Begin(StepPhase.Inserters);    InsertersTickPreSettle();     _profiler?.End(StepPhase.Inserters);

        _profiler?.Begin(StepPhase.Electric);
        ElectricGrid.Settle();
        ElectricGeneratorsBurnFuel();
        _profiler?.End(StepPhase.Electric);

        _profiler?.Begin(StepPhase.Machines);     MachinesTickPostSettle();     _profiler?.End(StepPhase.Machines);
        _profiler?.Begin(StepPhase.MiningDrills); MiningDrillsTickPostSettle(); _profiler?.End(StepPhase.MiningDrills);
        _profiler?.Begin(StepPhase.Inserters);    InsertersTickPostSettle();    _profiler?.End(StepPhase.Inserters);

        _profiler?.Begin(StepPhase.BeltAdvance);
        for (int bi = 0; bi < Belts.Capacity; bi++) { /* 现有推进循环体不变 */ }
        _profiler?.End(StepPhase.BeltAdvance);

        _profiler?.Begin(StepPhase.BeltHandoff);
        for (int bi = 0; bi < Belts.Capacity; bi++) { /* 现有交接循环体不变 */ }
        _profiler?.End(StepPhase.BeltHandoff);

        Tick++;
    }
```
注意 `Electric` phase 出现两次(供给登记 / Settle+烧油),`Begin/End` 各配对,`RecordingProfiler` 里 `Begins[Electric] == 2 * ticks` —— 但 Step 1 的测试断言写的是 `5`(每 tick 1 次)。**改测试断言为 `10`**,并在测试注释里说明 Electric 也是分两段。若你选择把供给登记和 Settle 合并成一个 `Electric` 区间(中间夹着 Machines/Drills/Inserters 的 pre 段就不行了——pre 段在 Settle 之前),则不可行。保持两段、断言 `10`。

- [ ] **Step 6: 跑测试**

先按 Step 5 的说明把 `RecordingProfiler_EveryPhaseBalancedEachTick` 里 `Electric` 的期望值改成 `10`,删占位断言。
Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~StepProfilerWiringTests"`
Expected: PASS(2 个测试)。

- [ ] **Step 7: 跑全套,确认无回归**

Run: `dotnet test sim/Faketorio.Sim.Tests -c Release`
Expected: 全绿(现有 ~401 + 新 2)。

- [ ] **Step 8: commit**

```bash
git add sim/Faketorio.Sim/Profiling/ sim/Faketorio.Sim/Simulation.cs sim/Faketorio.Sim.Tests/StepProfilerWiringTests.cs
git commit -m "feat(bench): IStepProfiler + Step() 分阶段计时钩子

<Co-Authored-By / Claude-Session trailer>"
```

---

### Task 2: `Faketorio.Sim.Bench` 项目骨架 + `StopwatchStepProfiler`

**Files:**
- Create: `sim/Faketorio.Sim.Bench/Faketorio.Sim.Bench.csproj`
- Create: `sim/Faketorio.Sim.Bench/Program.cs`(先只打印用法)
- Create: `sim/Faketorio.Sim.Bench/StopwatchStepProfiler.cs`
- Modify: `Faketorio.sln`(`dotnet sln add`)
- Modify: `sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj`(加 ProjectReference 到 Bench)
- Test: `sim/Faketorio.Sim.Tests/StepProfilerContractTests.cs`

**Interfaces:**
- Consumes: `Faketorio.Sim.Profiling.{StepPhase, IStepProfiler}`(Task 1)。
- Produces:
  - `namespace Faketorio.Sim.Bench; public sealed class StopwatchStepProfiler : IStepProfiler` —
    `void Begin(StepPhase)`, `void End(StepPhase)`, `void Reset()`,
    `IReadOnlyDictionary<StepPhase, long> SnapshotNs()`(返回 8 个 key,ns)。
  - console 项目,`OutputType Exe`,`AssemblyName Faketorio.Sim.Bench`。

- [ ] **Step 1: 写失败测试**

`sim/Faketorio.Sim.Tests/StepProfilerContractTests.cs`:
```csharp
using System.Diagnostics;
using Faketorio.Sim.Bench;
using Faketorio.Sim.Profiling;

namespace Faketorio.Sim.Tests;

public class StepProfilerContractTests
{
    [Fact]
    public void SnapshotNs_HasAllEightPhases()
    {
        var p = new StopwatchStepProfiler();
        var snap = p.SnapshotNs();
        Assert.Equal(8, snap.Count);
        foreach (StepPhase phase in Enum.GetValues<StepPhase>())
            Assert.True(snap.ContainsKey(phase));
    }

    [Fact]
    public void Accumulates_AcrossMultipleIntervalsOfSamePhase()
    {
        var p = new StopwatchStepProfiler();
        for (int i = 0; i < 3; i++)
        {
            p.Begin(StepPhase.Machines);
            SpinWaitMicros(200);
            p.End(StepPhase.Machines);
        }
        Assert.True(p.SnapshotNs()[StepPhase.Machines] > 0);
        Assert.Equal(0, p.SnapshotNs()[StepPhase.Player]);
    }

    [Fact]
    public void Reset_ZeroesEverything()
    {
        var p = new StopwatchStepProfiler();
        p.Begin(StepPhase.Electric); SpinWaitMicros(100); p.End(StepPhase.Electric);
        p.Reset();
        Assert.Equal(0, p.SnapshotNs()[StepPhase.Electric]);
    }

    private static void SpinWaitMicros(long micros)
    {
        long ticks = micros * Stopwatch.Frequency / 1_000_000;
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetTimestamp() - start < ticks) { }
    }
}
```

- [ ] **Step 2: 跑,确认失败**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~StepProfilerContractTests"`
Expected: 编译失败——`Faketorio.Sim.Bench` 命名空间/程序集不存在。

- [ ] **Step 3: 建 csproj**

`sim/Faketorio.Sim.Bench/Faketorio.Sim.Bench.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Faketorio.Sim\Faketorio.Sim.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="..\..\data\**" CopyToOutputDirectory="PreserveNewest" LinkBase="data" />
  </ItemGroup>

</Project>
```
（`data/**` 拷进输出目录——`PrototypeLoader.LoadFromDirectory("data/base")` 要能在 bench 的工作目录找到。）

- [ ] **Step 4: 加进 solution**

Run: `dotnet sln Faketorio.sln add sim/Faketorio.Sim.Bench/Faketorio.Sim.Bench.csproj`
确认 `Faketorio.sln` 多了 Bench 项目条目。

- [ ] **Step 5: `Program.cs` 占位**

`sim/Faketorio.Sim.Bench/Program.cs`:
```csharp
// Faketorio 模拟层 benchmark。完整 CLI 见 Task 7。
Console.WriteLine("faketorio bench — 用法见 bench/README.md(尚未实现 CLI)");
return 0;
```

- [ ] **Step 6: `StopwatchStepProfiler`**

`sim/Faketorio.Sim.Bench/StopwatchStepProfiler.cs`:
```csharp
using System.Diagnostics;
using Faketorio.Sim.Profiling;

namespace Faketorio.Sim.Bench;

/// IStepProfiler 的计时实现。每 phase 一个累加槽(Stopwatch tick 单位)。
/// 不嵌套 → 每 phase 一个 begin 时间戳槽即可。
public sealed class StopwatchStepProfiler : IStepProfiler
{
    private readonly long[] _accum = new long[8];
    private readonly long[] _begin = new long[8];

    public void Begin(StepPhase phase) => _begin[(int)phase] = Stopwatch.GetTimestamp();

    public void End(StepPhase phase) => _accum[(int)phase] += Stopwatch.GetTimestamp() - _begin[(int)phase];

    public void Reset() => Array.Clear(_accum);

    public IReadOnlyDictionary<StepPhase, long> SnapshotNs()
    {
        double nsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
        var d = new Dictionary<StepPhase, long>(8);
        foreach (StepPhase p in Enum.GetValues<StepPhase>())
            d[p] = (long)(_accum[(int)p] * nsPerTick);
        return d;
    }
}
```
(`double` 只用于把 Stopwatch tick 换算成 ns 的报告数字,不进任何 sim 状态,符合 Global Constraints。)

- [ ] **Step 7: Tests 项目引用 Bench**

`sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj` 的 `<ItemGroup>` 里,现有 `ProjectReference` 旁加:
```xml
    <ProjectReference Include="..\Faketorio.Sim.Bench\Faketorio.Sim.Bench.csproj" />
```

- [ ] **Step 8: 跑测试 + 冒烟**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~StepProfilerContractTests"`
Expected: PASS(3)。
Run: `dotnet run -c Release --project sim/Faketorio.Sim.Bench`
Expected: 打印 "faketorio bench — 用法见 bench/README.md...",退出码 0。

- [ ] **Step 9: 跑全套 + commit**

Run: `dotnet test sim/Faketorio.Sim.Tests -c Release` → 全绿。
```bash
git add sim/Faketorio.Sim.Bench/ Faketorio.sln sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj sim/Faketorio.Sim.Tests/StepProfilerContractTests.cs
git commit -m "feat(bench): Faketorio.Sim.Bench 项目骨架 + StopwatchStepProfiler

<trailer>"
```

---

### Task 3: `IScenarioPart` + `ScenarioProtoIds` + `IronUnitPart`

**Files:**
- Create: `sim/Faketorio.Sim.Bench/Scenario/IScenarioPart.cs`
- Create: `sim/Faketorio.Sim.Bench/Scenario/ScenarioProtoIds.cs`
- Create: `sim/Faketorio.Sim.Bench/Scenario/IronUnitPart.cs`
- Test: `sim/Faketorio.Sim.Tests/ScenarioPartTests.cs`(本 task 只加 IronUnit 一个测试;Task 4 补另外两个)

**Interfaces:**
- Consumes: sim API(见"参考"节)。
- Produces:
  - `namespace Faketorio.Sim.Bench.Scenario;`
  - `public readonly record struct ScenarioPartFacts(int EntitiesPlaced, IReadOnlyDictionary<string,int> EntityCountByType, long RatedPowerSupplyJPerTick, long RatedPowerDemandJPerTick, long MinableOreUnderDrills, long SeededIronOre, int FedDrillCount)`
  - `public interface IScenarioPart { ScenarioPartFacts Place(Simulation sim, int anchorX, int anchorY); }`
  - `public sealed class ScenarioProtoIds`,构造 `ScenarioProtoIds(PrototypeRegistry protos)`,字段:`int Drill, Furnace, Belt, Inserter, Pole, Generator, WoodenChest, LargeChest, IronOre, IronPlate, Coal; int IronOreStackSize; long DrillEnergyJPerTick, FurnaceEnergyJPerTick, InserterEnergyJPerTick, GeneratorOutputJPerTick; long CoalFuelValueJ;`
  - `public sealed class IronUnitPart(ScenarioProtoIds ids) : IScenarioPart` — 常量:`public const int CellWidth = 36; public const int CellHeight = 32; public const long SeedIronOrePerUnit = 1500;`
  - `Place` 的契约:所有实体用 `sim.Submit(PlaceEntity ...)`。**`Place` 不 `Step`、不注入库存**——它返回 `ScenarioPartFacts`,其中 `SeededIronOre = SeedIronOrePerUnit`(声明意图),真正的 `Inventory.Insert` 由 `ScenarioBuilder`(Task 5)在 drain-step 之后照 facts 执行。`SetFuelBufferJ` 同理(PowerDistrict 的 facts)。
  - **不变式**:`Place(sim, ax, ay)` 只占用 `[ax, ax+CellWidth) × [ay, ay+CellHeight)` 内的格子,不越界到邻格。

- [ ] **Step 1: 写失败测试**

`sim/Faketorio.Sim.Tests/ScenarioPartTests.cs`:
```csharp
using Faketorio.Sim.Bench.Scenario;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Sim.Tests;

public class ScenarioPartTests
{
    private static Simulation NewSim()
        => new(PrototypeLoader.LoadFromDirectory("data/base"), ScenarioBuilderSeedForTests());

    // Task 5 会把真正选定的种子写进 ScenarioBuilder.WorldSeed;在那之前测试用同一个候选值。
    private static long ScenarioBuilderSeedForTests() => 20260905L;

    [Fact]
    public void IronUnit_ProducesPlates()
    {
        var sim = NewSim();
        var ids = new ScenarioProtoIds(sim.Prototypes);

        // 供电:一台发电机灌满燃料缓冲 + 一根电线杆,覆盖单元锚点。
        // 单元放在 (0,0);电力设施放在负坐标一侧避免撞单元。
        int gen = ids.Generator; int pole = ids.Pole;
        sim.Submit(new Faketorio.Sim.Commands.Command { Type = Faketorio.Sim.Commands.CommandType.PlaceEntity, ProtoId = gen, X = -4, Y = 4 });
        sim.Submit(new Faketorio.Sim.Commands.Command { Type = Faketorio.Sim.Commands.CommandType.PlaceEntity, ProtoId = pole, X = -1, Y = 4 });

        var unit = new IronUnitPart(ids);
        var facts = unit.Place(sim, 0, 0);
        sim.Step();   // drain placement

        // 建造期注入:发电机燃料缓冲 + 单元铁料源(测试自己做 Builder 在 Task 5 才做的事)
        var genId = sim.World.GetEntityAt(-4, 4);
        sim.ElectricGrid.SetFuelBufferJ(genId, 200L * 1_000_000L);   // 200 coal 焦耳,够用
        // 铁料源 large-chest 的位置由 IronUnitPart 暴露一个静态 helper 给出(见 Step 3):
        var (srcX, srcY) = IronUnitPart.IronSourceChestTile(0, 0);
        var srcId = sim.World.GetEntityAt(srcX, srcY);
        var srcInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(srcId));
        srcInv.Insert(ids.IronOre, (int)facts.SeededIronOre, ids.IronOreStackSize);

        var (outX, outY) = IronUnitPart.OutputChestTile(0, 0);

        for (int t = 0; t < 4000; t++) sim.Step();

        var outId = sim.World.GetEntityAt(outX, outY);
        var outInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(outId));
        Assert.True(outInv.CountOf(ids.IronPlate) > 0,
            $"4000 tick 后输出箱应有铁板,实际 {outInv.CountOf(ids.IronPlate)}");
        Assert.True(facts.RatedPowerDemandJPerTick > 0);
        Assert.True(facts.EntitiesPlaced >= 12);
    }
}
```

- [ ] **Step 2: 跑,确认失败**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ScenarioPartTests"`
Expected: 编译失败(类型不存在)。

- [ ] **Step 3: 实现 `IScenarioPart.cs` / `ScenarioProtoIds.cs` / `IronUnitPart.cs`**

`IScenarioPart.cs`:
```csharp
namespace Faketorio.Sim.Bench.Scenario;

public readonly record struct ScenarioPartFacts(
    int EntitiesPlaced,
    IReadOnlyDictionary<string, int> EntityCountByType,
    long RatedPowerSupplyJPerTick,
    long RatedPowerDemandJPerTick,
    long MinableOreUnderDrills,
    long SeededIronOre,
    int FedDrillCount);

public interface IScenarioPart
{
    /// 用 sim.Submit 放实体;不 Step、不写库存。返回自报数据。
    ScenarioPartFacts Place(Simulation sim, int anchorX, int anchorY);
}
```

`ScenarioProtoIds.cs`:一次性从 `PrototypeRegistry` 解析所有要用的 id + 数值,避免每次 `Get<T>` 字符串查找。字段见 Interfaces。`GeneratorOutputJPerTick` 从 `FuelGeneratorPrototype.PowerOutputJPerTick` 取;`CoalFuelValueJ` 从 `ItemPrototype("coal")` 的 fuelValue(用现有 `Units.ParseEnergy` 或 prototype 已解析字段——照 P7 代码里怎么读的来)。`IronOreStackSize` 从 `ItemPrototype("iron-ore").StackSize`。能耗字段从各 prototype 的 `EnergyUsageJPerTick`(furnace/drill/inserter 都有;名字以实际 prototype 类为准)。

`IronUnitPart.cs` —— 固定几何(**坐标相对 anchor `(ax,ay)`;下列为一版具体布局,若 `IronUnit_ProducesPlates` 因几何/朝向没通,按 P10/P11 先例实测微调 `Rotation`/偏移,并把可用值写进代码注释,但不得放宽测试断言**):

采矿链(4 台 drill,2×2,朝南 `Rotation = 2` → 产出格 `(x, y+2)`):
```
d0 (ax+0,  ay+0)  产出格 (ax+0,  ay+2)   → wooden-chest @ (ax+0, ay+2)
d1 (ax+4,  ay+0)  产出格 (ax+4,  ay+2)   → wooden-chest @ (ax+4, ay+2)
d2 (ax+8,  ay+0)  产出格 (ax+8,  ay+2)   → wooden-chest @ (ax+8, ay+2)
d3 (ax+12, ay+0)  产出格 (ax+12, ay+2)   → wooden-chest @ (ax+12, ay+2)
```
矿在 drill 自己脚下 2×2 内才会被采(`Simulation.cs:698`);放完后统计每台 drill 4 格 `GetResourceAt` 里 `ResourceProtoId == ids.IronOre` 的格子,有则 `FedDrillCount++`、`MinableOreUnderDrills += Σ Amount`。(采矿链只压 drill tick + drill→container 插入,不接下游——这些矿最终堆在 wooden-chest 里,可能填满,drill 随之堵塞,确定性,可接受。)

加工链(固定,不搜索):
```
铁料源 large-chest (2×3)  @ (ax+16, ay+0)      占 (ax+16..17, ay+0..2)
   IronUnitPart.IronSourceChestTile(ax,ay) => (ax+16, ay+0)

入料机械臂 insA  @ (ax+18, ay+1)  Rotation=1(东)
   pickup = anchor - Delta(1) = (ax+17, ay+1)  → 铁料源
   dropoff = anchor + Delta(1) = (ax+19, ay+1) → 水平带首格

水平带(东, Rotation=1):(ax+19, ay+1),(ax+20, ay+1),(ax+21, ay+1)
竖直带(南, Rotation=2):(ax+22, ay+1),(ax+22, ay+2) ... (ax+22, ay+6)
   拐角:水平带出口 (ax+21,ay+1)+Delta(1)=(ax+22,ay+1) 命中竖直带 Tiles[^1] → 交接

熔炉 f0 (2×2) @ (ax+23, ay+2)   熔炉 f1 (2×2) @ (ax+23, ay+5)

上料机械臂 insB @ (ax+22, ay+3) Rotation=1  pickup (ax+21,ay+3)? —— 需对准竖直带某格
上料机械臂 insC @ (ax+22, ay+6) Rotation=1
   (insB/insC 从竖直带取,放进对应熔炉 role 1 输入格;熔炉 anchor 即其输入侧)

下料机械臂 insD @ (ax+26, ay+3) Rotation=1  从 f0 role 2 取 → 放输出带
下料机械臂 insE @ (ax+26, ay+6) Rotation=1  从 f1 role 2 取 → 放输出带

输出带(南):(ax+27, ay+2)..(ax+27, ay+7)
输出 large-chest (2×3) @ (ax+28, ay+7)
   IronUnitPart.OutputChestTile(ax,ay) => (ax+28, ay+7)
   末端机械臂 insF @ (ax+27, ay+8) 从输出带取 → 放输出箱  （或让输出带末端直接顶着箱子,靠 drill→container 那种"带末端塞不进就堵"——不行,带不会自动塞容器;必须有 insF）
```
**几何是这版 task 里最可能需要实测微调的部分。** 熔炉的 role-1 输入格 / role-2 输出格具体是 2×2 footprint 的哪一格,以及机械臂对准竖直带哪一子格,照 P11 的 `SimulationTests` 里 `PlaceInserter` + 熔炉的测试怎么摆的来抄。实现顺序建议:先只连"铁料源 → insA → 一小段带 → insB → f0 → insD → 短带 → insF → 输出箱"跑通 `IronUnit_ProducesPlates`,再加第二台熔炉和拐角。拐角务必保留(sentinel 不检查它,但 spec 要求覆盖线间交接)。

`Place` 汇总:`EntitiesPlaced` = 实际 submit 的 PlaceEntity 数(4 drill + 4 chest? no ——4 drill + 4 wooden-chest + 1 铁料源 + 1 输出箱 + ~6 inserter + ~15 belt ≈ 31);`EntityCountByType` 用 prototype name 计数;`RatedPowerDemandJPerTick` = `4*ids.DrillEnergyJPerTick + 2*ids.FurnaceEnergyJPerTick + inserterCount*ids.InserterEnergyJPerTick`;`RatedPowerSupplyJPerTick = 0`;`SeededIronOre = SeedIronOrePerUnit`。

- [ ] **Step 4: 跑 IronUnit 测试,实测微调几何直到通过**

Run: `dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~ScenarioPartTests.IronUnit_ProducesPlates"`
Expected: PASS。不过就调朝向/偏移(见 Step 3 说明),把 working 值写进注释。

- [ ] **Step 5: 跑全套 + commit**

Run: `dotnet test sim/Faketorio.Sim.Tests -c Release` → 全绿。
```bash
git add sim/Faketorio.Sim.Bench/Scenario/ sim/Faketorio.Sim.Tests/ScenarioPartTests.cs
git commit -m "feat(bench): IScenarioPart + ScenarioProtoIds + IronUnitPart

<trailer>"
```

---

### Task 4: `PowerDistrictPart` + `PoleBackbonePart`

**Files:**
- Create: `sim/Faketorio.Sim.Bench/Scenario/PowerDistrictPart.cs`
- Create: `sim/Faketorio.Sim.Bench/Scenario/PoleBackbonePart.cs`
- Modify: `sim/Faketorio.Sim.Tests/ScenarioPartTests.cs`(加 2 个测试)

**Interfaces:**
- Consumes: `IScenarioPart` / `ScenarioPartFacts` / `ScenarioProtoIds`(Task 3)。
- Produces:
  - `public sealed class PowerDistrictPart(ScenarioProtoIds ids, int generatorCount) : IScenarioPart` —
    常量 `public const int GeneratorPitch = 3;`(发电机 2×2,3 格一台,横排)。
    `Place`:横排放 `generatorCount` 台 `burner-generator`,每 ~2 台之间夹一根 `small-electric-pole`(supply 2 → 覆盖两侧发电机;wire 7 → 串起来)。
    facts:`RatedPowerSupplyJPerTick = generatorCount * ids.GeneratorOutputJPerTick`,`RatedPowerDemandJPerTick = 0`,`SeededIronOre = 0`;`EntitiesPlaced` = 发电机 + 电线杆数。
    **额外**:暴露 `IReadOnlyList<(int x,int y)> GeneratorTiles`(最近一次 `Place` 放的发电机 anchor 列表),供 `ScenarioBuilder` drain-step 后 `SetFuelBufferJ`。
  - `public sealed class PoleBackbonePart(ScenarioProtoIds ids, IReadOnlyList<(int x,int y)> waypoints) : IScenarioPart` —
    `Place` 忽略传入 anchor,沿相邻 waypoint 之间每 ≤6 格放一根 `small-electric-pole`(wire 7 留 1 格余量),跳过已占格(自己维护一个 `HashSet<(int,int)>`)。facts:只填 `EntitiesPlaced` + `EntityCountByType`,其余 0。

- [ ] **Step 1: 写失败测试**（加到 `ScenarioPartTests`）
```csharp
    [Fact]
    public void PowerDistrict_ReachesFullSatisfaction()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"), ScenarioBuilderSeedForTests());
        var ids = new ScenarioProtoIds(sim.Prototypes);
        var pd = new PowerDistrictPart(ids, generatorCount: 2);
        var facts = pd.Place(sim, 0, 0);

        // 一个假消费者(用 IronUnitPart 放一台熔炉)靠这片电供电
        var unit = new IronUnitPart(ids);
        unit.Place(sim, 6, 0);
        sim.Step();  // drain

        foreach (var (gx, gy) in pd.GeneratorTiles)
            sim.ElectricGrid.SetFuelBufferJ(sim.World.GetEntityAt(gx, gy), 200L * 1_000_000L);

        for (int t = 0; t < 300; t++) sim.Step();

        // 熔炉 anchor 处的 satisfaction 应接近 1(Q16.One = 65536)
        var furnace = sim.World.GetEntityAt(6 + 23, 0 + 2);   // 与 IronUnitPart f0 位置一致
        Assert.True(sim.ElectricGrid.GetSatisfaction(furnace).Raw >= 60000,
            $"satisfaction={sim.ElectricGrid.GetSatisfaction(furnace).Raw}");
        Assert.True(facts.RatedPowerSupplyJPerTick > 0);
    }

    [Fact]
    public void PoleBackbone_ConnectsTwoUnitsToPower()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"), ScenarioBuilderSeedForTests());
        var ids = new ScenarioProtoIds(sim.Prototypes);
        var pd = new PowerDistrictPart(ids, 3);
        pd.Place(sim, 0, 0);
        var u0 = new IronUnitPart(ids); u0.Place(sim, 10, 0);
        var u1 = new IronUnitPart(ids); u1.Place(sim, 10 + IronUnitPart.CellWidth + 4, 0);
        var backbone = new PoleBackbonePart(ids, new[] { (2, 0), (10, 0), (10 + IronUnitPart.CellWidth + 4, 0) });
        backbone.Place(sim, 0, 0);
        sim.Step();
        foreach (var (gx, gy) in pd.GeneratorTiles)
            sim.ElectricGrid.SetFuelBufferJ(sim.World.GetEntityAt(gx, gy), 200L * 1_000_000L);
        for (int t = 0; t < 200; t++) sim.Step();

        var f0 = sim.World.GetEntityAt(10 + 23, 2);
        var f1 = sim.World.GetEntityAt(10 + IronUnitPart.CellWidth + 4 + 23, 2);
        Assert.True(sim.ElectricGrid.GetSatisfaction(f0).Raw > 0);
        Assert.True(sim.ElectricGrid.GetSatisfaction(f1).Raw > 0);
    }
```

- [ ] **Step 2: 跑,确认失败** — `dotnet test ... --filter ScenarioPartTests`,编译失败。

- [ ] **Step 3: 实现两个 part**（按 Interfaces 描述）。注意 `PoleBackbonePart` 的去重集合、`PowerDistrictPart.GeneratorTiles` 的暴露。

- [ ] **Step 4: 跑 3 个 part 测试** — `dotnet test ... --filter ScenarioPartTests` → PASS(IronUnit + 2 新)。几何/间距不对就调电线杆 pitch。

- [ ] **Step 5: 全套 + commit**
```bash
git add sim/Faketorio.Sim.Bench/Scenario/PowerDistrictPart.cs sim/Faketorio.Sim.Bench/Scenario/PoleBackbonePart.cs sim/Faketorio.Sim.Tests/ScenarioPartTests.cs
git commit -m "feat(bench): PowerDistrictPart + PoleBackbonePart

<trailer>"
```

---

### Task 5: `ScenarioBuilder` + `ScenarioSentinel` + 选定世界种子

**Files:**
- Create: `sim/Faketorio.Sim.Bench/Scenario/ScenarioBuilder.cs`
- Create: `sim/Faketorio.Sim.Bench/Scenario/ScenarioSentinel.cs`
- Create: `sim/Faketorio.Sim.Tests/ScenarioBuilderTests.cs`

**Interfaces:**
- Consumes: 三个 part(Task 3/4)。
- Produces:
  - `public static class ScenarioBuilder` —
    `public const long WorldSeed = <Step 3 实测选定>;`
    `public const int DefaultScale = 200;`
    `public const int DefaultTicks = 20000;`
    `public static BuiltScenario Build(int scale, long seed = WorldSeed);`
  - `public sealed record BuiltScenario(Simulation Sim, IReadOnlyList<ScenarioPartFacts> PartFacts, ScenarioTotals Totals);`
  - `public readonly record struct ScenarioTotals(int Entities, int FedDrillCount, long RatedSupply, long RatedDemand, long MinableOre, IReadOnlyDictionary<string,int> CountByType);`
  - `public static class ScenarioSentinel { public static void Assert(int scale, Simulation sim, ScenarioTotals totals); }` — 抛 `ScenarioSentinelException`(新 `public sealed class ScenarioSentinelException : Exception`)。
- `Build` 流程(**顺序固定**):
  1. `var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"), seed);`
  2. `var ids = new ScenarioProtoIds(sim.Prototypes);`
  3. `int cols = (int)Math.Ceiling(Math.Sqrt(scale));`
  4. 电力区:`int genCount = (int)Math.Ceiling(totalDemandEstimate / (double)ids.GeneratorOutputJPerTick) + margin;` —— 但 `totalDemand` 依赖单元数,单元 demand 是常量(`IronUnitPart` 的 `RatedPowerDemandJPerTick` 与坐标无关),所以 `genCount = ceil(scale * perUnitDemand / genOutput) + 2`。`perUnitDemand` 从一个 `new IronUnitPart(ids)` 的 `RatedPowerDemandJPerTick` 拿(放一个 throwaway sim 里 Place 一次读 facts,或让 `IronUnitPart` 暴露 `static long PerUnitRatedDemand(ScenarioProtoIds)`)。用后者,免 throwaway。
  5. `var pd = new PowerDistrictPart(ids, genCount); var pdFacts = pd.Place(sim, PowerOriginX, PowerOriginY);`
  6. `for (int u = 0; u < scale; u++) { int col = u % cols, row = u / cols; int ax = UnitOriginX + col * (IronUnitPart.CellWidth + UnitGapX); int ay = UnitOriginY + row * (IronUnitPart.CellHeight + UnitGapY); unitFacts.Add(new IronUnitPart(ids).Place(sim, ax, ay)); unitAnchors.Add((ax, ay)); }`
  7. 骨干:waypoints = 电力区中点 + 每行第一个单元的 anchor + 每行最后一个单元的 anchor(足够连通);`new PoleBackbonePart(ids, waypoints).Place(sim, 0, 0);`
  8. `sim.Step();` —— drain 所有 placement(一次即可:命令在一个 tick 内全 apply)。
  9. **建造期注入(唯二)**:
     - `foreach ((gx,gy) in pd.GeneratorTiles) sim.ElectricGrid.SetFuelBufferJ(sim.World.GetEntityAt(gx,gy), FuelSeedJ);` 其中 `FuelSeedJ = 50L * ids.CoalFuelValueJ;`(一满组煤)。
     - `for u: var (sx,sy) = IronUnitPart.IronSourceChestTile(ax,ay); var inv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(sx,sy))); inv.Insert(ids.IronOre, (int)IronUnitPart.SeedIronOrePerUnit, ids.IronOreStackSize);`
  10. 汇总 `ScenarioTotals`(从 `pdFacts` + `unitFacts` 加总;`FedDrillCount` 来自 unitFacts;`Entities` = `sim` 里实际存活实体数 —— 用 `sim.Entities` 的存活计数,若无现成 API 就累加 facts 的 `EntitiesPlaced`)。
  11. `ScenarioSentinel.Assert(scale, sim, totals);`
  12. `return new BuiltScenario(sim, unitFacts, totals);`
- `ScenarioSentinel.Assert` 四条(spec §4.3):
  0. `if (sim.RejectedCommandCount != 0) throw new ScenarioSentinelException($"建造期有 {sim.RejectedCommandCount} 条命令被拒(占位冲突)");`
  1. `scale == DefaultScale` 时:`totals.Entities` 与各类型计数 == 钉死常量(Step 4 实测填);其它 scale:`totals.Entities == ExpectedEntities(scale)`(用公式 `pdEntities(genCount) + scale * IronUnitPart.EntitiesPerUnit + backbonePoles(scale)` —— 每项都是可算的常量/线性式)。
  2. `scale == DefaultScale` 时:`totals.FedDrillCount >= ExpectedFedDrillCount * 9 / 10`(`ExpectedFedDrillCount` Step 3 实测填)且 `totals.MinableOre > (long)totals.FedDrillCount * DefaultTicks`(极安全下界:每 drill 每 tick 采不到 1 个)。
  3. `totals.RatedSupply >= totals.RatedDemand`。
- 常量:`PowerOriginX/Y`, `UnitOriginX/Y`, `UnitGapX/Y`(建议 `UnitGapX = 4`, `UnitGapY = 4`),`PowerOrigin` 放在单元阵列左侧留足空隙(如 `UnitOriginX = 0`,`PowerOriginX = -8 - genCount*PowerDistrictPart.GeneratorPitch`)。所有常量在文件顶部 `const`。

- [ ] **Step 1: 写失败测试**

`sim/Faketorio.Sim.Tests/ScenarioBuilderTests.cs`:
```csharp
using Faketorio.Sim.Bench.Scenario;

namespace Faketorio.Sim.Tests;

public class ScenarioBuilderTests
{
    [Fact]
    public void Build_Scale1_Deterministic_AndSentinelPasses()
    {
        var a = ScenarioBuilder.Build(1);
        var b = ScenarioBuilder.Build(1);
        // 逐 tick 哈希一致
        for (int t = 0; t < 200; t++) { a.Sim.Step(); b.Sim.Step(); Assert.Equal(a.Sim.ComputeStateHash(), b.Sim.ComputeStateHash()); }
    }

    [Fact]
    public void Build_Scale1_EntityMix_MatchesConstants()
    {
        var s = ScenarioBuilder.Build(1);
        // 具体数值实现时实测填入(小到能人眼核对)
        Assert.Equal(ScenarioBuilder.ExpectedEntities(1), s.Totals.Entities);
        Assert.True(s.Totals.CountByType["electric-mining-drill"] == 4);
        Assert.True(s.Totals.CountByType["stone-furnace"] == 2);
    }

    [Fact]
    public void Build_DefaultScale_SentinelPasses_AndRunsAShortWhile()
    {
        var s = ScenarioBuilder.Build(ScenarioBuilder.DefaultScale);
        Assert.Equal(0, s.Sim.RejectedCommandCount);
        Assert.True(s.Totals.RatedSupply >= s.Totals.RatedDemand);
        Assert.True(s.Totals.FedDrillCount > 0);
        for (int t = 0; t < 50; t++) s.Sim.Step();   // 冒烟:不炸
    }

    [Fact]
    public void Build_NonDefaultScale_StillBuilds()
    {
        var s = ScenarioBuilder.Build(9);
        Assert.Equal(0, s.Sim.RejectedCommandCount);
    }
}
```
(`ExpectedEntities(int scale)` 设为 `ScenarioBuilder` 的 public static 方法,sentinel 和测试共用。)

- [ ] **Step 2: 跑,确认失败** — 编译失败。

- [ ] **Step 3: 实现 `ScenarioBuilder` + `ScenarioSentinel`;选世界种子**

先用候选 `20260905L` 实现全部逻辑。然后写一个**临时**探针(可以是 `ScenarioBuilderTests` 里一个 `[Fact(Skip="probe")]` 或本地 `dotnet run` 小脚本)遍历 `seed ∈ {20260905, 1, 2, 42, 1000, 123456789, ...}` 各 `Build(DefaultScale)`,打印 `FedDrillCount` / `Entities` / `MinableOre` / `RejectedCommandCount`。挑一个满足:`RejectedCommandCount == 0` 且 `FedDrillCount` 占总 drill 数 ≥ ~55% 且 `MinableOre` 充裕。把它写进 `ScenarioBuilder.WorldSeed`,把实测的 `ExpectedFedDrillCount`、default-scale 各类型计数写进 sentinel 常量。删探针。
（若候选 `20260905L` 直接满足,就用它,省事。）

- [ ] **Step 4: 填常量,跑 `ScenarioBuilderTests`** — PASS(4)。

- [ ] **Step 5: 把 `ScenarioPartTests` 里的 `ScenarioBuilderSeedForTests()` 改成 `ScenarioBuilder.WorldSeed`**,重跑 `ScenarioPartTests` 确认仍绿(种子变了几何不受影响,但矿量可能变——若 `IronUnit_ProducesPlates` 因新种子矿更少而变慢,把该测试的 tick 数从 4000 提到 8000,不放宽断言)。

- [ ] **Step 6: 全套 + commit**
```bash
git add sim/Faketorio.Sim.Bench/Scenario/ScenarioBuilder.cs sim/Faketorio.Sim.Bench/Scenario/ScenarioSentinel.cs sim/Faketorio.Sim.Tests/ScenarioBuilderTests.cs sim/Faketorio.Sim.Tests/ScenarioPartTests.cs
git commit -m "feat(bench): ScenarioBuilder + ScenarioSentinel + 选定世界种子

<trailer>"
```

---

### Task 6: `GoldenFile` + `BenchReport`

**Files:**
- Create: `sim/Faketorio.Sim.Bench/GoldenFile.cs`
- Create: `sim/Faketorio.Sim.Bench/BenchReport.cs`
- Test: `sim/Faketorio.Sim.Tests/GoldenFileTests.cs`

**Interfaces:**
- Produces:
  - `namespace Faketorio.Sim.Bench;`
  - `public sealed class GoldenFile { public int Scale; public int Ticks; public int[] SampleTicks; public string FinalHash; public Dictionary<string,string> SampleHashes; public long BaselineNsPerTick; public double PerfFailMultiplier; public string RecordedAtCommit; public string RecordedAtUtc; }`
    - `public static GoldenFile Load(string path)`(`System.Text.Json`,`JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true }`)。文件不存在/坏 → 抛 `GoldenFileException`。
    - `public void Save(string path)`。
    - `public static string FormatHash(ulong h) => "0x" + h.ToString("X16");`
    - `public static ulong ParseHash(string s) => ulong.Parse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber);`
  - `public sealed class BenchReport { int Scale; int Ticks; long MinNsPerTick; long MedianNsPerTick; long BaselineNsPerTick; double Ratio; double PerfFailMultiplier; string Gate; PhaseCost[] Phases; Dictionary<string,string> Hashes; string Commit; string Utc; }`
    `public readonly record struct PhaseCost(string Phase, long Ns, double PctOfTick);`
    `public string ToJson()`(indented);`public string ToMarkdownSummary()`(给 `$GITHUB_STEP_SUMMARY` 的表格)。
  - `enum` 值放字符串:`Gate ∈ "PASS" | "HASH_FAIL" | "PERF_FAIL" | "NONDETERMINISM" | "SKIPPED"`。
  - `GoldenFile.Compare(GoldenFile golden, string actualFinalHash, IReadOnlyDictionary<int,string> actualSampleHashes) -> (bool ok, string? firstMismatch)`。

- [ ] **Step 1: 失败测试**

`sim/Faketorio.Sim.Tests/GoldenFileTests.cs`:
```csharp
using Faketorio.Sim.Bench;

namespace Faketorio.Sim.Tests;

public class GoldenFileTests
{
    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var g = new GoldenFile {
            Scale = 200, Ticks = 20000, SampleTicks = new[] { 1000, 20000 },
            FinalHash = "0x00000000DEADBEEF",
            SampleHashes = new() { ["1000"] = "0x0000000000000001", ["20000"] = "0x00000000DEADBEEF" },
            BaselineNsPerTick = 0, PerfFailMultiplier = 3.0,
            RecordedAtCommit = "abc1234", RecordedAtUtc = "2026-09-05T00:00:00Z",
        };
        string path = Path.Combine(Path.GetTempPath(), $"golden-{Guid.NewGuid():N}.json");
        g.Save(path);
        var back = GoldenFile.Load(path);
        File.Delete(path);
        Assert.Equal(200, back.Scale);
        Assert.Equal("0x00000000DEADBEEF", back.FinalHash);
        Assert.Equal(3.0, back.PerfFailMultiplier);
    }

    [Fact]
    public void FormatParseHash_RoundTrips()
    {
        ulong h = 0x1234_5678_9ABC_DEF0UL;
        Assert.Equal(h, GoldenFile.ParseHash(GoldenFile.FormatHash(h)));
    }

    [Fact]
    public void Compare_DetectsSampleMismatch()
    {
        var g = new GoldenFile {
            Scale = 1, Ticks = 10, SampleTicks = new[] { 5, 10 },
            FinalHash = "0x000000000000000A",
            SampleHashes = new() { ["5"] = "0x0000000000000005", ["10"] = "0x000000000000000A" },
            BaselineNsPerTick = 0, PerfFailMultiplier = 3.0, RecordedAtCommit = "x", RecordedAtUtc = "x",
        };
        var (ok, mismatch) = GoldenFile.Compare(g, "0x000000000000000A",
            new Dictionary<int,string> { [5] = "0x0000000000000099", [10] = "0x000000000000000A" });
        Assert.False(ok);
        Assert.Contains("5", mismatch);
    }

    [Fact]
    public void Load_MissingFile_Throws()
        => Assert.Throws<GoldenFileException>(() => GoldenFile.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".json")));
}
```

- [ ] **Step 2: 跑，失败** — 编译失败。

- [ ] **Step 3: 实现两个文件**（照 Interfaces；`System.Text.Json` 是 BCL，无新依赖）。

- [ ] **Step 4: 跑 `GoldenFileTests`** — PASS(4)。

- [ ] **Step 5: 全套 + commit**
```bash
git add sim/Faketorio.Sim.Bench/GoldenFile.cs sim/Faketorio.Sim.Bench/BenchReport.cs sim/Faketorio.Sim.Tests/GoldenFileTests.cs
git commit -m "feat(bench): GoldenFile + BenchReport(读写/比对/JSON/markdown)

<trailer>"
```

---

### Task 7: `Program.cs` — CLI + 计时协议 + 退出码

**Files:**
- Modify: `sim/Faketorio.Sim.Bench/Program.cs`(替换占位)
- Create: `sim/Faketorio.Sim.Bench/BenchRunner.cs`(可测的核心:参数结构 + `Run` 返回退出码,不碰 `Console`/`Environment.Exit`)
- Test: `sim/Faketorio.Sim.Tests/BenchScenarioTests.cs`

**Interfaces:**
- Consumes: `ScenarioBuilder`(Task 5)、`StopwatchStepProfiler`(Task 2)、`GoldenFile`/`BenchReport`(Task 6)。
- Produces:
  - `namespace Faketorio.Sim.Bench;`
  - `public sealed record BenchOptions(int Scale, int Ticks, int Warmup, int Iterations, string GoldenPath, string ReportPath, bool UpdateGolden, bool Json, bool SelfTest)` + `public static BenchOptions Parse(string[] args)`(默认 `Scale=ScenarioBuilder.DefaultScale`,`Ticks=ScenarioBuilder.DefaultTicks`,`Warmup=1`,`Iterations=5`,`GoldenPath="bench/golden.json"`,`ReportPath="bench/bench-report.json"`)。未知参数 → 抛 `ArgumentException`。
  - `public static class BenchRunner { public static int Run(BenchOptions opt, TextWriter stdout); }` — 返回退出码(0/1/2/3/4),把人类可读进度 + `--json` 时的报告 JSON 写进 `stdout`,把报告写进 `opt.ReportPath`。**不调 `Environment.Exit`。**
  - `Program.cs`:`try { return BenchRunner.Run(BenchOptions.Parse(args), Console.Out); } catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 3; }`
- `BenchRunner.Run` 逻辑:
  1. `--selftest` → 走 §"SelfTest" 分支(见下),return。
  2. `var golden = opt.UpdateGolden && !File.Exists(opt.GoldenPath) ? null : GoldenFile.Load(opt.GoldenPath);`(load 失败且非 update → return 3)。
  3. `int[] sampleTicks = golden?.SampleTicks ?? new[] { 1000, 5000, 10000, opt.Ticks };`
  4. **一趟** = `RunOnce(opt.Scale, opt.Ticks, sampleTicks, StopwatchStepProfiler? profiler) -> (long nsPerTick, string finalHash, Dictionary<int,string> sampleHashes, IReadOnlyDictionary<StepPhase,long>? phaseNs)`:
     - `var built = ScenarioBuilder.Build(opt.Scale);`(sentinel 在里面;抛 `ScenarioSentinelException` → 上层 catch → return 3)
     - 若传了 profiler:`built` 用的是不带 profiler 的 sim —— 需要 `ScenarioBuilder.Build` 支持传 `IStepProfiler?`。**给 `Build` 加可选参数 `IStepProfiler? profiler = null`,转发给 `new Simulation(...)`。**(Task 5 已建 `Build`;这里补一个重载或可选参。记进 Task 5 的 Produces —— 见下方 "Task 5 修订"。)
     - `var sw = Stopwatch.StartNew();` 循环 `opt.Ticks` 次 `built.Sim.Step()`,在 `k ∈ sampleTicks` 时(k = 已执行步数)记 `GoldenFile.FormatHash(built.Sim.ComputeStateHash())`;循环末尾记 finalHash(= 第 `opt.Ticks` 步后的哈希;若 `opt.Ticks ∈ sampleTicks` 则同值)。`sw.Stop();` `nsPerTick = sw.Elapsed.TotalNanoseconds / opt.Ticks`(long)。
     - profiler 非 null → `phaseNs = profiler.SnapshotNs()`。
  5. `warmup` 趟:`RunOnce(..., profiler: null)` 丢弃。
  6. `iterations` 趟净计时:`RunOnce(..., profiler: null)`,收集 `nsPerTick` 列表 + 每趟的 `(finalHash, sampleHashes)`。**趟间 `(finalHash, sampleHashes)` 必须全相等**,否则 `gate = "NONDETERMINISM"`,写报告,return 4。
  7. 1 趟分阶段:`RunOnce(..., profiler: new StopwatchStepProfiler())` → `phaseNs`。其哈希也必须与净计时趟一致(并进 step 6 的相等检查)。
  8. `minNs = min(list)`, `medianNs = 排序取中`。
  9. **门禁**(仅当 `golden != null && golden.Scale == opt.Scale && golden.Ticks == opt.Ticks`;否则 `gate = "SKIPPED"`):
     - `var (hok, mism) = GoldenFile.Compare(golden, finalHash, sampleHashesAsIntKeyed);` `!hok` → `gate = "HASH_FAIL"`,报告,return 1。
     - `golden.BaselineNsPerTick > 0 && minNs > golden.BaselineNsPerTick * golden.PerfFailMultiplier` → `gate = "PERF_FAIL"`,报告,return 2。
     - 否则 `gate = "PASS"`。
  10. `--update-golden`:用本趟结果构造新 `GoldenFile`(`BaselineNsPerTick = minNs`,`RecordedAtCommit = GitShortHead()`,`RecordedAtUtc = DateTime.UtcNow.ToString("O")`,`PerfFailMultiplier = golden?.PerfFailMultiplier ?? 3.0`,`SampleTicks` 不变或默认),`Save(opt.GoldenPath)`,`gate = "SKIPPED"`,写报告,return 0。
  11. 构造 `BenchReport`(`Phases` 从 `phaseNs`:`PctOfTick = ns / (double)minNs * 100`,按 ns 降序),`File.WriteAllText(opt.ReportPath, report.ToJson())`;`opt.Json` → `stdout.WriteLine(report.ToJson())`;总是 `stdout.WriteLine(report.ToMarkdownSummary())`。return 门禁结果码。
  - `GitShortHead()`:`Process.Start("git", "rev-parse --short HEAD")` 读 stdout;失败返回 `"unknown"`。
- **SelfTest 分支**:用一个内建的**迷你人造 golden**(`scale` 极小、几步、`FinalHash` 故意写错)跑,断言 `Run` 对 hash 不符 return 1;再造一个 `BaselineNsPerTick = 1`(1 ns,必然超)的 golden 断言 return 2;正常 golden 断言 return 0。把这些断言直接写在 `BenchRunner.SelfTest(TextWriter) -> int`(全过 return 0,否则 return 5),`Run` 在 `--selftest` 时调它。测试再从 xUnit 调 `BenchRunner.SelfTest`。

- [ ] **Step 0: Task 5 修订 —— `ScenarioBuilder.Build` 加 profiler 形参**

`public static BuiltScenario Build(int scale, long seed = WorldSeed, Faketorio.Sim.Profiling.IStepProfiler? profiler = null)`,把 `profiler` 透传给 `new Simulation(proto, seed, profiler)`。(这条本可以在 Task 5 就做;放这里因为需求在本 task 才明确。改完重跑 `ScenarioBuilderTests` 确认没破。)

- [ ] **Step 1: 失败测试**

`sim/Faketorio.Sim.Tests/BenchScenarioTests.cs`:
```csharp
using Faketorio.Sim.Bench;

namespace Faketorio.Sim.Tests;

public class BenchScenarioTests
{
    [Fact]
    public void Options_Parse_DefaultsAndOverrides()
    {
        var d = BenchOptions.Parse(Array.Empty<string>());
        Assert.Equal(ScenarioBuilder.DefaultScale, d.Scale);
        Assert.Equal(5, d.Iterations);
        var o = BenchOptions.Parse(new[] { "--scale", "3", "--ticks", "100", "--iterations", "2", "--warmup", "0" });
        Assert.Equal(3, o.Scale);
        Assert.Equal(100, o.Ticks);
        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(new[] { "--bogus" }));
    }

    [Fact]
    public void SmallScale_HashSequence_IsStableAcrossRuns()
    {
        // 小场景逐 tick 哈希序列的钉死值(实现时首次运行填入)
        var built1 = ScenarioBuilder.Build(1);
        var built2 = ScenarioBuilder.Build(1);
        var h1 = new List<ulong>(); var h2 = new List<ulong>();
        for (int t = 0; t < 800; t++) { built1.Sim.Step(); h1.Add(built1.Sim.ComputeStateHash()); built2.Sim.Step(); h2.Add(built2.Sim.ComputeStateHash()); }
        Assert.Equal(h1, h2);
        // 钉死锚点:确保场景行为没漂。GOLDEN_800 由实现者首跑填入。
        Assert.Equal(GOLDEN_TICK_800, h1[799]);
    }
    private const ulong GOLDEN_TICK_800 = 0; // ← 实现者首跑替换

    [Fact]
    public void Runner_SelfTest_Passes()
        => Assert.Equal(0, BenchRunner.SelfTest(TextWriter.Null));

    [Fact]
    public void Runner_SmallScale_UpdateThenVerify_RoundTrips()
    {
        string golden = Path.Combine(Path.GetTempPath(), $"g-{Guid.NewGuid():N}.json");
        string report = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}.json");
        var opt = new BenchOptions(Scale: 1, Ticks: 60, Warmup: 0, Iterations: 2,
            GoldenPath: golden, ReportPath: report, UpdateGolden: true, Json: false, SelfTest: false);
        Assert.Equal(0, BenchRunner.Run(opt, TextWriter.Null));           // 写 golden
        var verify = opt with { UpdateGolden = false };
        Assert.Equal(0, BenchRunner.Run(verify, TextWriter.Null));        // 哈希应匹配自己
        File.Delete(golden); File.Delete(report);
    }

    [Fact]
    public void Runner_HashMismatch_Returns1()
    {
        string golden = Path.Combine(Path.GetTempPath(), $"g-{Guid.NewGuid():N}.json");
        string report = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}.json");
        var mk = new BenchOptions(1, 60, 0, 2, golden, report, true, false, false);
        BenchRunner.Run(mk, TextWriter.Null);
        var g = GoldenFile.Load(golden); g.FinalHash = "0xFFFFFFFFFFFFFFFF"; g.Save(golden);
        Assert.Equal(1, BenchRunner.Run(mk with { UpdateGolden = false }, TextWriter.Null));
        File.Delete(golden); File.Delete(report);
    }
}
```

- [ ] **Step 2: 跑,失败** — 编译失败。

- [ ] **Step 3: 实现 `BenchOptions` / `BenchRunner` / `Program.cs`**（照 Interfaces）。

- [ ] **Step 4: 跑 `BenchScenarioTests`,填 `GOLDEN_TICK_800`**

首跑 `SmallScale_HashSequence_IsStableAcrossRuns` 会因 `GOLDEN_TICK_800 = 0` 失败,把断言报的 actual 值填进常量,重跑。其余测试应直接过。

- [ ] **Step 5: 冒烟小规模**

Run: `dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --scale 4 --ticks 500 --iterations 2 --warmup 0 --golden /tmp/g.json --report /tmp/r.json --update-golden --json`
Expected: 退出 0,`/tmp/g.json` + `/tmp/r.json` 生成,stdout 有 markdown 表 + JSON。再不带 `--update-golden` 重跑 → 退出 0(哈希自匹配)。

- [ ] **Step 6: 全套 + commit**
```bash
git add sim/Faketorio.Sim.Bench/Program.cs sim/Faketorio.Sim.Bench/BenchRunner.cs sim/Faketorio.Sim.Bench/Scenario/ScenarioBuilder.cs sim/Faketorio.Sim.Tests/BenchScenarioTests.cs
git commit -m "feat(bench): CLI + 计时协议 + 退出码 + selftest

<trailer>"
```

---

### Task 8: `bench/golden.json` + `bench/README.md` + CI workflow + README 补注

**Files:**
- Create: `bench/golden.json`
- Create: `bench/README.md`
- Create: `.github/workflows/ci.yml`
- Modify: `README.md` / `README_en.md`(构建/测试一节补一句)
- Modify: `sim/Faketorio.Sim.Bench/Faketorio.Sim.Bench.csproj`(若 CI 里 `dotnet run` 的工作目录导致 `data/` 找不到,补一个绝对/相对路径回退——见 Step 4)

**Interfaces:** 无新代码类型。产出物:一份能让 `dotnet run --project sim/Faketorio.Sim.Bench` 在默认参数下退出 0(或哈希不符退出 1)的 `golden.json`,和一份两 job 的 CI。

- [ ] **Step 1: 生成默认规模 golden(哈希实测,baseline 置 0)**

Run(本地,Release):
```bash
dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --update-golden --golden bench/golden.json --report bench/bench-report.json
```
然后**手工编辑 `bench/golden.json`**:把 `baselineNsPerTick` 改成 `0`(= 未校准,性能门禁 SKIP;本地机器的数不作 CI 基线)。确认文件形如:
```json
{
  "scale": 200,
  "ticks": 20000,
  "sampleTicks": [1000, 5000, 10000, 20000],
  "finalHash": "0x................",
  "sampleHashes": { "1000": "0x...", "5000": "0x...", "10000": "0x...", "20000": "0x..." },
  "baselineNsPerTick": 0,
  "perfFailMultiplier": 3.0,
  "recordedAtCommit": "................",
  "recordedAtUtc": "2026-09-05T..Z"
}
```

- [ ] **Step 2: 验证 golden 自洽**

Run: `dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json --report /tmp/r.json`
Expected: 退出 0,stdout `gate` 显示 `PASS`(性能因 baseline=0 被 SKIP,但哈希匹配 → 整体 PASS/SKIPPED,退出 0)。

- [ ] **Step 3: 写 `bench/README.md`**
```markdown
# Faketorio 模拟层 Benchmark

固定种子中型工厂,跑固定 tick,校验世界状态哈希 golden 值 + 记录 UPS/分阶段耗时。

## 跑

    dotnet run -c Release --project sim/Faketorio.Sim.Bench

默认 scale=200 ticks=20000。参数:`--scale N --ticks N --warmup N --iterations N`
`--json`(报告打到 stdout)`--report <path>` `--golden <path>` `--update-golden`(重写 golden)`--selftest`。

## 退出码

| 码 | 含义 |
|----|------|
| 0 | 通过(或非默认参数,门禁 SKIP) |
| 1 | 状态哈希与 golden 不符 |
| 2 | 最快趟 nsPerTick > baseline × perfFailMultiplier(baseline<=0 时此码不触发) |
| 3 | sentinel 断言失败 / 参数非法 / golden 缺失损坏 |
| 4 | 趟间哈希不一致(模拟有非确定性) |

## golden.json

`baselineNsPerTick = 0` 表示"未校准",性能门禁 SKIP,只有哈希门禁生效。
合并后从首次绿色 CI 的 `bench-report` artifact 读 `minNsPerTick`,填进 `baselineNsPerTick` 提交以激活性能门禁。
故意改了模拟行为导致哈希变:`--update-golden` 重写,并在 PR 里 review 这个 diff。
```

- [ ] **Step 4: 写 `.github/workflows/ci.yml`**
```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet test sim/Faketorio.Sim.Tests -c Release

  bench:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - name: Run benchmark
        run: dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --json --golden bench/golden.json --report bench/bench-report.json | tee bench-stdout.txt
      - name: Append report to job summary
        if: always()
        run: |
          echo '## Benchmark' >> "$GITHUB_STEP_SUMMARY"
          if [ -f bench/bench-report.json ]; then
            python3 - <<'PY' >> "$GITHUB_STEP_SUMMARY"
          import json
          r = json.load(open('bench/bench-report.json'))
          print(f"- scale={r['scale']} ticks={r['ticks']} gate=**{r['gate']}**")
          print(f"- minNsPerTick={r['minNsPerTick']} baseline={r['baselineNsPerTick']} ratio={r['ratio']:.3f}")
          print()
          print("| phase | ns | % |")
          print("|---|---:|---:|")
          for p in r['phases']:
              print(f"| {p['phase']} | {p['ns']} | {p['pctOfTick']:.1f} |")
          PY
          fi
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: bench-report
          path: |
            bench/bench-report.json
            bench-stdout.txt
```
（工作目录 = repo 根,`dotnet run` 里 `PrototypeLoader.LoadFromDirectory("data/base")` 的相对路径基准是**进程 CWD**,不是项目目录;`Content Include data/**` 只保证拷到 `bin/`。若 bench 进程实际以 `bin/.../` 为 CWD 就没问题,否则在 `BenchRunner`/`ScenarioBuilder` 里把 `"data/base"` 换成 `Path.Combine(AppContext.BaseDirectory, "data", "base")` —— **Step 5 验证并按需改**。）

- [ ] **Step 5: 本地模拟 CI 的路径条件**

Run(从 repo 根,不 `cd` 进项目):`dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --scale 4 --ticks 200 --warmup 0 --iterations 1 --golden /tmp/g.json --report /tmp/r.json --update-golden`
若报 `data/base` 找不到 → 把 `ScenarioBuilder` 里加载路径改为 `Path.Combine(AppContext.BaseDirectory, "data", "base")`(该目录因 csproj 的 `Content` 拷贝而存在),重跑确认。同步检查 `Faketorio.Sim.Tests` 里直接 `LoadFromDirectory("data/base")` 的用法 —— 测试的 CWD 是 test bin 目录,现有测试能过说明那里 OK;只有 bench 的 `dotnet run` CWD 可能不同。

- [ ] **Step 6: README 补一句**

`README.md` 和 `README_en.md` 的"构建 / 测试"段(现有 `dotnet test ...` / `dotnet build -c Release` 附近)各加一行:
```
# 基准 + 回归基线(状态哈希 golden + UPS/分阶段耗时)
dotnet run -c Release --project sim/Faketorio.Sim.Bench      # 详见 bench/README.md
```

- [ ] **Step 7: 全套测试 + 冒烟默认规模 + commit**

Run: `dotnet test sim/Faketorio.Sim.Tests -c Release` → 全绿。
Run: `dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json --report bench/bench-report.json` → 退出 0。
```bash
git add bench/ .github/ README.md README_en.md sim/Faketorio.Sim.Bench/
git commit -m "feat(bench): golden.json + bench/README + 仓库首个 CI(test + bench)

<trailer>"
```
（`bench/bench-report.json` 加进 `.gitignore` —— 它是每次跑的产物,不进版本库。若仓库还没 `.gitignore` 就建一个,写 `bench/bench-report.json` 和 `bench-stdout.txt`。）

---

## Self-Review

**1. Spec coverage:**

| spec 章节 | 对应 task |
|---|---|
| §2 组件(Bench 项目、Profiling 文件、测试文件、bench/、CI) | Task 2(项目)、Task 1(Profiling)、Task 8(bench/ + CI) |
| §3 `IStepProfiler` + `Simulation` 接线 + 8 阶段 | Task 1 |
| §3.3 `StopwatchStepProfiler` | Task 2 |
| §4.1 `IScenarioPart` / `ScenarioPartFacts` / `ScenarioBuilder` / `BuiltScenario` | Task 3(接口+facts)、Task 5(builder) |
| §4.2 布局(电力区一次性注入燃料、三链解耦、拐角) | Task 3(IronUnit + 拐角)、Task 4(PowerDistrict + SetFuelBufferJ 由 Task 5 drain 后执行) |
| §4.3 sentinel 四条 | Task 5(`ScenarioSentinel`) |
| §4.5 分层测试(part 级 ×3、Builder 级、小规模 golden 序列、profiler 不影响哈希) | Task 3/4(part)、Task 5(Builder)、Task 7(小规模序列)、Task 1(profiler 哈希) |
| §5 CLI + 两趟协议 + 退出码 0–4 + `--update-golden` + `--selftest` + 报告 | Task 7 |
| §5.1 `--json` / 参数表 | Task 7 |
| §5.6 `bench-report.json` schema | Task 6(`BenchReport`) |
| §6 `golden.json` schema + 红线 + baseline<=0 SKIP | Task 6(`GoldenFile`)、Task 7(门禁)、Task 8(生成) |
| §6.3 baseline 从 CI artifact 回填流程 | Task 8 Step 1 + bench/README(文档化;人工步骤) |
| §7 CI workflow(test + bench 两 job 并行、job summary、artifact) | Task 8 |
| §8 全局约束 | 复制进本plan Global Constraints;Task 1 的 `ProfilerAttached_DoesNotChangeHash` 强制确定性铁律 |

覆盖完整。§6.3 的"人从 artifact 回填 baseline"是刻意的人工步骤,plan 只负责让它可行 + 文档化。

**2. Placeholder scan:** 有意留的"实现者首跑填入实际值"共 3 处,均为 TDD 里正常的"钉死值首次落定":`RecordingProfiler` 的 Electric 期望值(Task 1 Step 5 已明说改 10)、`ScenarioBuilder` sentinel 常量 + 世界种子(Task 5 Step 3 有明确的选种子流程)、`GOLDEN_TICK_800`(Task 7 Step 4 有落定流程)。这些不是"待办占位",是"运行一次把观测值钉下来",每处都有具体操作步骤。几何微调(Task 3)明确授权按 P10/P11 先例实测调朝向、禁止放宽断言。无 "TBD/handle edge cases/similar to Task N" 类占位。

**3. Type consistency:**
- `IStepProfiler.Begin/End(StepPhase)` — Task 1 定义,Task 2 `StopwatchStepProfiler` 实现,Task 7 `BenchRunner` 用,一致。
- `ScenarioPartFacts` 7 字段(含 `FedDrillCount`)—— Task 3 定义时把 spec §4.1 的 6 字段 + `FedDrillCount` 一并定死;Task 4/5 消费一致。**注意**:spec §4.1 列的是 6 字段,plan 加了第 7 个 `FedDrillCount`(sentinel 条件 2 需要)——这是 plan 对 spec 的合理细化,已在 Task 3 Interfaces 写明。
- `ScenarioBuilder.Build(int, long, IStepProfiler?)` —— Task 5 建两参版,Task 7 Step 0 补第三参。最终签名一致。
- `BuiltScenario(Sim, PartFacts, Totals)` / `ScenarioTotals` —— Task 5 定义,Task 7 消费(`.Sim` / `.Totals`)一致。
- `GoldenFile` 字段名(`FinalHash`/`SampleHashes`/`BaselineNsPerTick`/`PerfFailMultiplier`)—— Task 6 定义,Task 7 门禁 + Task 8 生成一致;JSON 属性名用 camelCase(`PropertyNameCaseInsensitive` + `JsonNamingPolicy.CamelCase`)以匹配 spec §6.1 的 `finalHash` 等写法 —— **Task 6 实现时设 `JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase`**。
- `BenchReport` / `PhaseCost` —— Task 6 定义,Task 7 填充一致。
- 退出码 0/1/2/3/4 —— spec §5.4 = Task 7 `BenchRunner.Run` = Task 8 bench/README 三处一致。

一致性 OK。一处 plan 对 spec 的细化(`FedDrillCount`)已标注。
