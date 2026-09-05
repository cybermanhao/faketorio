# M1 Plan 12 — CI 基准场景 + 回归基线设计

日期: 2026-09-05
状态: 已与用户确认的设计基线
前置依赖: P1–P11 全部已合并 `main`(`fae170b`)——核心模拟闭环(采矿/自动采矿 → 手搓/熔炼/装配 → 带类型物流 → 机械臂搬运 → 存储,全程电力驱动,确定性状态哈希基建齐全)
配套参考: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](2026-07-03-faketorio-design.md)(§5.3 实体休眠、§5.5 基准测试进 CI)、[`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md)(横切"基准场景 + UPS 报告进 CI"条目)

## 1. 目标

给仓库补上两样一直缺的东西:

1. **一个可重复跑的大型确定性场景** + **一个独立 benchmark 项目**。固定世界种子、程序化生成一个中型工厂(大量采矿机 / 传送带 / 熔炉 / 机械臂 / 电网),跑固定 tick 数。
2. **回归基线接进 CI**:
   - **状态哈希 golden 值**——同代码同输入必然算出同一个 64 位世界指纹;任何让模拟行为发生变化的改动都必须显式地、被人 review 地更新这个值,不能悄悄溜过。这是**硬红线**。
   - **性能红线**——`nsPerTick` 相对一个 checked-in 基线,超过 `3×` 判失败。自校准(基线是 CI 环境实测值,人工更新),只抓数量级退化(某个两趟扫描写成 O(n²)、每 tick 多分配一坨垃圾),不追 60 UPS。这也是**硬红线**。
   - **分阶段耗时画像**——每个 `Step()` 阶段(电网 / 机器 / 采矿机 / 机械臂 / 传送带推进 / 拐角交接)的 ns 和占比,进 CI job summary。**只记录不设线**,累积腐蚀靠人眼看趋势。

**动机**:这是后续"实体休眠 / 活跃列表"优化的前置——那次优化会动 P9/P10/P11 三个两趟全扫描,必须先有"输出字节级不变"的安全网 + "哪个模块变快/变慢了"的度量台架。参考 Dyson Sphere Program 2025 年底的多线程重写 Dev Log:它们靠自研 profiler 按 task/stage 拆帧成本来定位瓶颈("分拣器逻辑 ~3.6ms / 整帧 ~22ms"),主指标就是固定存档上的逻辑帧耗时;但它们没有确定性/CI 这一层,而**确定性状态哈希正是让"敢把 `Step()` 拆成并行 task 并证明输出不变"成为可能的东西**——本子项就是搭这层网。

**不做**(明确排除):

- 不做 UPS 硬 SLA、不追 60 UPS。性能红线只抓数量级退化(3× 基线)。
- 不做 BenchmarkDotNet(统计严谨度对"防退化告警"是过度投资,单次跑几十秒到几分钟对每个 PR 偏重)。
- 不做多线程,不做 sim 层性能优化本身(那是"实体休眠"子项)。
- 不做 CI 自动 commit `golden.json`、不做 gh-pages 趋势图、不做 `history.ndjson` 持久化。累积腐蚀靠 job summary 人眼看。
- 不做 Windows/macOS CI matrix(sim 层是纯 .NET 无平台依赖)。
- 不做"录制真人操作回放"的命令序列文件格式(当前无 `Command` 序列化格式,不引入)。
- 不改现有 `DeterminismTests` / `DeterministicHashTests`。
- 场景构造**只用 public `Command`**,不做任何"直接往库存塞物品 / 直接改世界状态"的特权注入。

## 2. 组件与文件结构

**新项目**

```
sim/Faketorio.Sim.Bench/                    console (net8.0, 无额外 NuGet 依赖)
  Faketorio.Sim.Bench.csproj
  Program.cs                                CLI 解析 + 计时协议 + 退出码
  Scenario/IScenarioPart.cs                 可组合场景片段接口
  Scenario/PowerDistrictPart.cs             电力区(发电机 + 煤矿机 + 机械臂上料 + 电线杆)
  Scenario/IronUnitPart.cs                  1 个铁生产单元(4 采矿机 → 带 → 2 熔炉 → 机械臂 → 箱)
  Scenario/PoleBackbonePart.cs              电线杆骨干,连成一张网
  Scenario/ScenarioBuilder.cs              按固定顺序组合 part;返回构造好的 Simulation + 自报数据
  Scenario/ScenarioSentinel.cs             生成后三条全局断言
  StopwatchStepProfiler.cs                  IStepProfiler 实现(Stopwatch.GetTimestamp 累加)
  BenchReport.cs                            报告 POCO + JSON 序列化(System.Text.Json)
  GoldenFile.cs                             golden.json 读写 + 比对
```

**新文件(sim 层)**

| 文件 | 职责 |
|---|---|
| `sim/Faketorio.Sim/Profiling/StepPhase.cs` | `enum StepPhase { Commands, Player, Electric, Machines, MiningDrills, Inserters, BeltAdvance, BeltHandoff }` |
| `sim/Faketorio.Sim/Profiling/IStepProfiler.cs` | `interface IStepProfiler { void Begin(StepPhase phase); void End(StepPhase phase); }` |

**新文件(测试)**

| 文件 | 职责 |
|---|---|
| `sim/Faketorio.Sim.Tests/BenchScenarioTests.cs` | 小规模逐 tick 哈希序列 vs 内联 golden 数组;挂/不挂 profiler 哈希一致;sentinel 三条通过;bench `--selftest`。 |
| `sim/Faketorio.Sim.Tests/ScenarioPartTests.cs` | 每个 `IScenarioPart` 的 part 级单测(放进空 sim 跑几百 tick,断言在干活)。 |
| `sim/Faketorio.Sim.Tests/StepProfilerContractTests.cs` | `StopwatchStepProfiler` 的 `Begin/End` 配对、`Snapshot` 累加、`Reset` 归零。 |

**新文件(仓库根)**

| 文件 | 职责 |
|---|---|
| `bench/golden.json` | checked-in:场景参数 + 黄金哈希(final + 采样)+ 基线 `nsPerTick` + 记录时 commit/时间戳。 |
| `bench/README.md` | 本地怎么跑、怎么更新基线、退出码含义。 |
| `.github/workflows/ci.yml` | 仓库第一个 CI workflow:`test` job + `bench` job。 |

**改动**

| 文件 | 改动 |
|---|---|
| `sim/Faketorio.Sim/Simulation.cs` | 构造增加可选参数 `IStepProfiler? profiler = null`,存 `_profiler` 字段;`Step()` 里每个阶段包一对 `_profiler?.Begin(phase)` / `_profiler?.End(phase)`。`_profiler` **不出现在 `WriteState`**,不参与哈希。 |
| `sim/Faketorio.Sim.Tests/Faketorio.Sim.Tests.csproj` | 加 `<ProjectReference Include="..\Faketorio.Sim.Bench\Faketorio.Sim.Bench.csproj" />`(复用 `ScenarioBuilder`,避免场景逻辑两处维护)。 |
| `Faketorio.sln` | 加入 `Faketorio.Sim.Bench` 项目。 |
| `README.md` / `README_en.md` | "如何构建 / 测试"一节补一句 benchmark 的跑法 + 指向 `bench/README.md`。 |

## 3. sim 层计时接线(`IStepProfiler`)

### 3.1 接口与枚举

```csharp
namespace Faketorio.Sim.Profiling;

public enum StepPhase
{
    Commands, Player, Electric, Machines, MiningDrills,
    Inserters, BeltAdvance, BeltHandoff
}

public interface IStepProfiler
{
    void Begin(StepPhase phase);
    void End(StepPhase phase);
}
```

### 3.2 `Simulation` 改动

- 构造:现有签名是 `public Simulation(PrototypeRegistry prototypes, long worldSeed = 0)`(`Simulation.cs:38`)。改为 `public Simulation(PrototypeRegistry prototypes, long worldSeed = 0, IStepProfiler? profiler = null)`——尾部追加可选参数,现有调用点不受影响。新增字段 `private readonly IStepProfiler? _profiler;`。
- `Step()` 每阶段包裹。阶段与现有代码块的对应(见 `Simulation.cs:56`–`104`):

  | phase | 包裹的现有代码 |
  |---|---|
  | `Commands` | `_commands.BeginTick()` + `for … Apply(in commands[i])` |
  | `Player` | `PlayerWalk(); PlayerMine(); PlayerCraft();` |
  | `Electric` | `ElectricGeneratorsRegisterSupply(); ElectricGrid.Settle(); ElectricGeneratorsBurnFuel();` |
  | `Machines` | `MachinesTickPreSettle();` **和** `MachinesTickPostSettle();`(两段分处 `Electric` 两侧,各自 `Begin/End` 同一 phase key) |
  | `MiningDrills` | `MiningDrillsTickPreSettle();` **和** `MiningDrillsTickPostSettle();` |
  | `Inserters` | `InsertersTickPreSettle();` **和** `InsertersTickPostSettle();` |
  | `BeltAdvance` | 传送带推进 for 循环(`Simulation.cs:80`–`87`) |
  | `BeltHandoff` | 线间交接 for 循环(`Simulation.cs:89`–`102`) |

  实际执行顺序不变;只是把现有 `ElectricGeneratorsRegisterSupply(); MachinesTickPreSettle(); MiningDrillsTickPreSettle(); InsertersTickPreSettle(); ElectricGrid.Settle(); ElectricGeneratorsBurnFuel(); MachinesTickPostSettle(); MiningDrillsTickPostSettle(); InsertersTickPostSettle();` 这一串按 phase 边界插入 `Begin/End` 调用。`Machines`/`MiningDrills`/`Inserters` 各自的 Pre 段先 `Begin(phase)` … `End(phase)`,Post 段再 `Begin(phase)` … `End(phase)`,profiler 内部累加同 key。

- `_profiler` 为 `null` 时:每 tick 多约 16 个 `?.` 空判断,可忽略;不进 `WriteState`,不影响哈希。

### 3.3 `StopwatchStepProfiler`(Bench 项目)

```csharp
public sealed class StopwatchStepProfiler : IStepProfiler
{
    private readonly long[] _accumTicks = new long[8];   // Stopwatch tick 单位,按 (int)phase 索引
    private readonly long[] _beginStamp = new long[8];

    public void Begin(StepPhase phase) => _beginStamp[(int)phase] = Stopwatch.GetTimestamp();
    public void End(StepPhase phase)   => _accumTicks[(int)phase] += Stopwatch.GetTimestamp() - _beginStamp[(int)phase];

    public void Reset() => Array.Clear(_accumTicks);
    public IReadOnlyDictionary<StepPhase, long> SnapshotNs()  // 换算成 ns:accumTicks * 1e9 / Stopwatch.Frequency
        => …;
}
```

`Begin/End` 不可重入(同一 phase 一 tick 内的多次调用是顺序的:Pre 段结束后才轮到 Post 段),因此单个 `_beginStamp[phase]` 槽足够。

## 4. 场景(可组合 part + 分层测试)

### 4.1 结构

```csharp
public interface IScenarioPart
{
    // 在给定锚点放自己这一坨(只用 sim.Submit);返回自报数据。
    ScenarioPartFacts Place(Simulation sim, int anchorX, int anchorY);
}

public readonly record struct ScenarioPartFacts(
    int EntitiesPlaced,
    IReadOnlyDictionary<string, int> EntityCountByType,  // prototype name -> count
    long RatedPowerSupplyJPerTick,   // 该 part 贡献的额定供给(PoleBackbone/IronUnit 为 0)
    long RatedPowerDemandJPerTick,   // 该 part 满载需求(PowerDistrict 为 0)
    long MinableOreUnderDrills);     // 该 part 采矿机脚下可采总量(非 IronUnit 为 0)
```

`ScenarioBuilder`:

```csharp
public static class ScenarioBuilder
{
    public const long WorldSeed = <选定常量>;   // 实现计划里挑一个,并让 sentinel 兜住

    public const int DefaultScale = 200;
    public const int DefaultTicks = 20000;

    // 组合:[PowerDistrictPart] + [IronUnitPart × scale] + [PoleBackbonePart]
    // IronUnitPart 按 ceil(sqrt(scale)) 列平铺,锚点按 (列, 行) 固定序遍历。
    public static BuiltScenario Build(int scale, long seed = WorldSeed);
}

public sealed record BuiltScenario(Simulation Sim, IReadOnlyList<ScenarioPartFacts> PartFacts);
```

`Build` 内部:`new Simulation(protos, seed)` → 依次 `part.Place(sim, ...)` 收集 facts → `ScenarioSentinel.Assert(scale, partFacts)` → 返回。**不 step**,step 由调用方(bench / 测试)控制。

### 4.2 场景布局(形态钉死,几何细节留给实现计划)

- **固定世界种子**,一个选定常量。
- **一个电力区**(`PowerDistrictPart`,放在原点附近一块锚定区域):一片煤矿田上铺 `P` 台燃料发电机,每台配 1 台煤矿机 + 1 台机械臂把煤从矿机搬进发电机燃料槽,配电线杆。`P` 由"满载总需求 / 单机发电"向上取整 + 裕度,公式在实现计划定。**发电机的燃料全程机械供给,无特权注入。**
- **铁生产单元阵列**(`IronUnitPart × scale`):每单元 = 铁矿田上 4 台采矿机 → 传送带汇流(经过至少一个拐角,测 typed belt items + 线间交接)→ 2 台熔炉(recipe 铁矿石→铁板,测加工状态机 + satisfaction 降速)→ 机械臂上料(role 1)/ 下料(role 2)→ 输出箱。
- **电线杆骨干**(`PoleBackbonePart`):把电力区和所有单元连成一张连通电网(测 `ElectricGrid.Settle` 在大图上的规模表现)。
- **默认 `scale = 200`** → 约 5000 实体。

### 4.3 供给保证(`ScenarioSentinel`)

生成后三条断言,任一失败 → `Build` 抛异常(bench exit 3 / 测试红),异常信息指明是哪一条:

1. **实体计数**:`Σ EntitiesPlaced` 和各类型计数 == 按 `scale` 算出的预期常量/公式值。防生成器被无意改小。
2. **矿量充足**:`Σ MinableOreUnderDrills` > `(采矿机总数) × (采矿速率, 物品/tick) × DefaultTicks`。防某个种子/世界生成改动让矿在 benchmark 窗口内被挖空、场景退化成空转。仅对 `scale == DefaultScale && ticks == DefaultTicks` 的等价规模严格要求;其它规模按比例。
3. **供需匹配**:`Σ RatedPowerSupplyJPerTick` ≥ `Σ RatedPowerDemandJPerTick`。保证 satisfaction ≈ 1,让加工/搬运每 tick 都在干活,负载稳定。

采矿机落位用**固定螺旋搜索**从单元锚点找最近的对应矿脉;搜索序固定 → 确定性。

### 4.4 确定性

所有 part 遍历按 `(列, 行)` 固定序;所有 `sim.Submit` 顺序固定;不依赖任何 `Dictionary` 迭代序或哈希序。`ScenarioBuilder.Build` 对同 `(scale, seed)` 必然产生逐 tick 字节级相同的模拟。

### 4.5 分层测试

| 测试 | 内容 |
|---|---|
| `ScenarioPartTests.PowerDistrict_ReachesFullSatisfaction` | `PowerDistrictPart.Place` 进空 sim,跑 ~1000 tick,断言电网 satisfaction 达到 ~1(发电机点着了、煤在流)。 |
| `ScenarioPartTests.IronUnit_ProducesPlates` | `IronUnitPart` + 一个最小电力区,跑 ~1500 tick,断言输出箱里出现铁板(采矿→带→熔炉→机械臂→箱 全链路通)。 |
| `ScenarioPartTests.PoleBackbone_ConnectsGrid` | 电力区 + 2 个单元 + 骨干,断言两个单元都拿到电(satisfaction > 0)。 |
| `BenchScenarioTests.Builder_Scale1_CommandCountAndEntityMix` | `scale = 1`:下发 `Command` 数 & 各类型实体总数 == 钉死的小常量(小到能人眼核对)。 |
| `BenchScenarioTests.Builder_Scale200_EntityMixMatchesFormula` | `scale = 200`:各类型计数 == 公式值。 |
| `BenchScenarioTests.SmallScale_MatchesGoldenHashSequence` | 小 `scale` + ~800 tick:逐 tick 哈希序列 == 钉在测试文件里的内联小 golden 数组(独立于 `bench/golden.json`)。`dotnet test` 就能挡住场景/sim 行为漂移。 |
| `BenchScenarioTests.ProfilerAttached_SameHashes` | 同场景挂 `StopwatchStepProfiler` / 不挂,逐 tick 哈希完全一致(守确定性铁律)。 |
| `BenchScenarioTests.Sentinel_DefaultScale_Passes` | `ScenarioSentinel` 三条断言在默认参数下通过。 |

**扩展/重写场景的安全网** = 上表第 6/7 行 + `bench/golden.json` 的 `--update-golden` reviewed diff:随便重写 part,只要组合出的逐 tick 哈希变了,就必须有人在 PR 里为新 golden 值背书;实体 mix / 供给漂移则 sentinel 失败。加一个新子系统(M2 的机器人/火车)= 新写一个 `IScenarioPart` + 加它的 part 级测试 + `--update-golden`,不动老 part。

## 5. Bench runner(CLI + 计时协议 + 退出码)

### 5.1 命令行

`dotnet run -c Release --project sim/Faketorio.Sim.Bench -- <args>`

| 参数 | 默认 | 说明 |
|---|---|---|
| `--scale <int>` | `200` | 铁生产单元数 |
| `--ticks <int>` | `20000` | 每趟 step 数 |
| `--warmup <int>` | `1` | 丢弃的预热趟数 |
| `--iterations <int>` | `5` | 净计时趟数,取最快一趟 |
| `--golden <path>` | `bench/golden.json` | 黄金文件路径 |
| `--report <path>` | `bench/bench-report.json` | 输出报告路径 |
| `--update-golden` | off | 用本次结果重写 golden(哈希 + 基线 + commit + 时间戳),exit 0 |
| `--json` | off | 把报告同时打到 stdout(CI job summary 用) |
| `--selftest` | off | 跑迷你场景 + 人造 golden,验证退出码语义(见 §5.4),不碰真 golden |

### 5.2 一趟的流程

全新 `Simulation` ← `ScenarioBuilder.Build(scale, seed)`(内含 sentinel 断言)→ `for t in [0, ticks): sim.Step()`,在 golden 的 `sampleTicks` 处记 `sim.ComputeStateHash()`,循环末尾再记一次 final hash。

### 5.3 协议

1. `warmup` 趟:只跑,丢弃计时和结果。
2. `iterations` 趟**净计时**:`Simulation` **不挂** profiler;外层 `Stopwatch` 量 `总壁钟ns / ticks`;记录每趟的哈希序列。取 `min(nsPerTick)` 为顶线。
3. 1 趟**分阶段**:`Simulation` 挂 `StopwatchStepProfiler`;跑完取 `SnapshotNs()` → 每阶段 ns 和 `pctOfTick`。**只进报告,不设线。**
4. 所有趟(warmup 除外)的哈希序列必须彼此一致——不一致 → **exit 4**(sim 有非确定性)。
5. `ComputeStateHash()` 的耗时**算进净计时顶线**(和 Factorio 把 checksum 当帧成本一部分一致)。

### 5.4 判定与退出码

门禁**仅当** `scale` 和 `ticks` 同时等于 golden 里记录的值时启用;否则打印 `GATES SKIPPED: non-default params`,只出报告,exit 0。

| 条件 | 退出码 |
|---|---|
| 全过 | `0` |
| `finalHash` 或任一 `sampleHashes[t]` 与 golden 不符 | `1` |
| `min(nsPerTick) > baselineNsPerTick × perfFailMultiplier`(默认 `3.0`) | `2` |
| `ScenarioSentinel` 断言失败 / 参数非法 / golden 文件缺失或损坏(非 `--update-golden` 时) | `3` |
| 趟间哈希序列不一致 | `4` |

`--selftest`:用迷你 `scale` + 一份**人造错误的** golden 跑,断言"哈希不符真的 exit 1、性能倍数超标真的 exit 2、sentinel 失败真的 exit 3",全部符合预期则 exit 0。由 `BenchScenarioTests` 以子进程或直接调用 `Program` 入口的方式驱动。

### 5.5 `--update-golden`

跑完把以下写回 `--golden` 指定文件,exit 0:

- `scale`, `ticks`, `sampleTicks`(不变则原样回写)
- `finalHash`, `sampleHashes`(本次实测,十六进制字符串 `0x…`)
- `baselineNsPerTick` = 本次 `min(nsPerTick)`
- `perfFailMultiplier`(不变则原样回写,默认 `3.0`)
- `recordedAtCommit` = `git rev-parse --short HEAD`(取不到则 `"unknown"`)
- `recordedAtUtc` = ISO-8601 UTC

开发者自己 `git add bench/golden.json` 并在 PR 描述里解释这次哈希/基线变化为何是预期的。**CI 永不自动运行 `--update-golden`。**

### 5.6 报告(`bench-report.json`)

```json
{
  "scale": 200,
  "ticks": 20000,
  "minNsPerTick": 41800,
  "medianNsPerTick": 44100,
  "baselineNsPerTick": 42000,
  "ratio": 0.995,
  "perfFailMultiplier": 3.0,
  "gate": "PASS",
  "phases": [
    { "phase": "Inserters", "ns": 12800, "pctOfTick": 30.6 },
    { "phase": "BeltAdvance", "ns": 9100, "pctOfTick": 21.8 }
  ],
  "hashes": { "final": "0x9A3F...", "1000": "0x...", "5000": "0x..." },
  "commit": "abc1234",
  "utc": "2026-09-05T12:00:00Z"
}
```

`phases` 按 `ns` 降序。`gate` ∈ `PASS` / `HASH_FAIL` / `PERF_FAIL` / `NONDETERMINISM` / `SKIPPED`。

## 6. golden.json 与红线

### 6.1 `bench/golden.json`

```json
{
  "scale": 200,
  "ticks": 20000,
  "sampleTicks": [1000, 5000, 10000, 20000],
  "finalHash": "0x9A3F...",
  "sampleHashes": { "1000": "0x...", "5000": "0x...", "10000": "0x...", "20000": "0x..." },
  "baselineNsPerTick": 42000,
  "perfFailMultiplier": 3.0,
  "recordedAtCommit": "abc1234",
  "recordedAtUtc": "2026-09-05T12:00:00Z"
}
```

### 6.2 红线语义

- **哈希(硬,不自校准)**:`finalHash` + 每个 `sampleHashes` 逐一比对。多个采样点让"从哪个 tick 开始分叉"能一眼定位。
- **性能(硬,自校准)**:`min(nsPerTick) > baselineNsPerTick × perfFailMultiplier` → exit 2。`3.0` 吸收 CI 共享机器抖动(同代码来回 ±30–50% 常见),只抓数量级退化。
- **累积腐蚀(不设线)**:每阶段占比进 `bench-report.json` → CI job summary 表格。每个 PR 都能看到"机械臂占比从 28% 涨到 34%",靠人看趋势。

### 6.3 基线怎么来 / 怎么更新

- **首个实现任务**在 CI(`ubuntu-latest`)上跑一次 `--update-golden`,把测出的 `baselineNsPerTick` 连同哈希一起提交。**基线必须是 CI 环境的数**——3× 裕度要算在跑门禁的那台硬件上。
- 之后任何 PR 若合理地改变性能(优化变快 / 接受 <3× 的回归),开发者本地 `--update-golden` 后手动复核新 `baselineNsPerTick`,连同 diff 一起提交;`golden.json` 的 diff 就是 code review 里性能变化的签字点。
- CI **不自动**改 `golden.json`(不给 `main` 加提交噪声,不给 workflow 授写权限)。
- **非默认规模**(`--scale 1000` 手动压测):golden 无对应条目 → 门禁 SKIP,只出报告。

## 7. CI workflow

`.github/workflows/ci.yml`——仓库第一个 CI:

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
      - run: dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --json --report bench/bench-report.json
      - name: Render report to job summary
        if: always()
        run: |
          # 把 bench-report.json 渲染成 markdown 表 append 到 $GITHUB_STEP_SUMMARY
          # (minNsPerTick / ratio / gate / 每阶段占比)
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: bench-report
          path: bench/bench-report.json
```

- 两个 job 并行。`bench` 非零退出(哈希不符 / 性能 3×)→ CI 红。
- `test` 和 `bench` 都 `-c Release`。
- 固定 `actions/*@v4` 大版本;`8.0.x` 与 `TargetFramework net8.0` 对齐(本地装的 9.0.312 不影响)。
- 单平台 `ubuntu-latest`。

## 8. 全局约束(实现计划照抄进 Global Constraints)

- **确定性铁律**:`IStepProfiler` 及其实现只持有 `long[]` 计时累加,**绝不**触碰任何进 `WriteState` 的状态;`_profiler` 不进 `WriteState`。sim 层不得因 `_profiler` 是否为 `null` 而走不同的**状态相关**分支(只允许"是否调用计时钩子"这一个差异)。测试 `ProfilerAttached_SameHashes` 强制这一点。
- sim 层不引入 `float`/`double` 参与任何状态计算(计时换算成 ns 只在 Bench 项目里做,不回流)。
- 场景构造只用 public `Command`,无特权状态注入。
- 所有场景遍历/提交按固定序,不依赖 `Dictionary` 迭代序。
- `Faketorio.Sim` 保持零 Godot 依赖;`Faketorio.Sim.Bench` 只依赖 `Faketorio.Sim` + BCL,无额外 NuGet。
- 新 CI 用 `actions/checkout@v4`、`actions/setup-dotnet@v4`(`8.0.x`)、`actions/upload-artifact@v4`,单平台 `ubuntu-latest`。
- 提交遵循仓库既有 message 规范 + 结尾 `Co-Authored-By:` / `Claude-Session:` trailer。

## 9. 开放项(交给实现计划定,不阻塞设计)

- 世界种子常量的具体取值(挑一个让 sentinel 条件 2/3 在默认规模下成立的)。
- 单元几何:单元 footprint 尺寸、螺旋搜索半径、骨干电线杆间距、发电机台数 `P` 的公式、单元阵列列数 `ceil(sqrt(scale))` 的边缘处理。
- `sampleTicks` 的最终取值(默认 `[1000, 5000, 10000, 20000]`,可按实现时观察调整)。
- job summary 的 markdown 渲染:内联 shell + `jq`,还是 bench 直接吐一段 markdown 到单独文件由 workflow `cat` 进 `$GITHUB_STEP_SUMMARY`。
- `--selftest` 的驱动方式:`BenchScenarioTests` 直接 `Program.Main(args)` 拿退出码,还是 `Process.Start` 子进程。
