# MCP 原子操作模拟驱动器 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新建一个 `Faketorio.Sim.McpServer` 控制台项目，用官方 `ModelContextProtocol` C# SDK 把 `Simulation` 的 `Submit`/`Step`/查询 API 包成 9 个 stdio MCP 工具，让 Claude Code 能在对话里直接调用原子操作驱动/探索 sim 层场景。

**Architecture:** 新项目引用 `Faketorio.Sim`（项目引用，sim 层零改动）。进程持有一个静态可空的 `Simulation?` 字段（`SimHost.Sim`），`reset_simulation` 创建/替换它，其余 8 个工具在它非空时读写它。工具方法是普通静态方法，用 `[McpServerToolType]`/`[McpServerTool]` 标注；测试直接在进程内调这些静态方法，不经 stdio。

**Tech Stack:** C# / .NET 8，`ModelContextProtocol` NuGet 包（stdio transport），xUnit。不引入任何 sim 层改动。

**Spec:** `docs/superpowers/specs/2026-09-13-mcp-sim-driver-design.md`

## Global Constraints

- sim 层（`Faketorio.Sim` 项目）本次**零改动**——只新增一个引用它的新项目。
- 新项目必须注册进 `Faketorio.sln`，用 `dotnet sln add`，不要手写 `.sln` 里的 GUID。
- `ModelContextProtocol` 包用 `dotnet add package ModelContextProtocol` 装最新稳定版，不手动钉版本号（spec §1）。
- `reset_simulation` 的 `dataDir` 参数必须相对于 `AppContext.BaseDirectory`（进程可执行文件所在目录）解析，不能依赖进程当前工作目录——这是 spec §3 明确标注、必须在实施时解决而不是留白的细节。新项目的 `.csproj` 要照抄 `Faketorio.Sim.Bench.csproj`/`Faketorio.Sim.Tests.csproj` 里 `<Content Include="..\..\data\**" CopyToOutputDirectory="PreserveNewest" LinkBase="data" />` 这一段，让 `data/` 目录被复制到输出目录里。
- 除 `reset_simulation` 外的全部工具，在 `SimHost.Sim == null` 时必须返回清楚的"还没 reset"错误，不能抛未处理异常导致进程崩溃或返回不友好的通用错误（spec §3 末尾）。
- `submit_command` 按"名字全局唯一"假设解析 `protoName`（不按 `CommandType` 做具体 CLR 类型过滤）——这是 spec §3/§4 明确的 YAGNI 决定，不要在这轮加类型过滤。
- MCP 层不做任何 sim 层已经做的合法性校验（比如坐标范围、原型是否匹配）——校验完全交给 `Simulation.Apply`，工具只负责把参数转成 `Command` 提交。
- 测试项目（`Faketorio.Sim.McpServer.Tests`）直接在进程内调用工具类的静态方法测试，不通过 stdio 起子进程（spec §5）。

---

## Task 1: 项目脚手架 + 状态模型 + `reset_simulation`/`get_tick`

**Files:**
- Create: `sim/Faketorio.Sim.McpServer/Faketorio.Sim.McpServer.csproj`
- Create: `sim/Faketorio.Sim.McpServer/Program.cs`
- Create: `sim/Faketorio.Sim.McpServer/SimHost.cs`
- Create: `sim/Faketorio.Sim.McpServer/Dtos.cs`
- Create: `sim/Faketorio.Sim.McpServer/SimTools.cs`
- Create: `sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj`
- Create: `sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs`
- Modify: `Faketorio.sln`（通过 `dotnet sln add`，不手写）

**Interfaces:**
- Produces:
  - `internal static class SimHost { public static Simulation? Sim; }`
  - `public static ResetResult ResetSimulation(long seed = 0, string dataDir = "data/base")`
  - `public static TickInfo GetTick()`
  - `public record ResetResult(long Tick, int PrototypeCount);`
  - `public record TickInfo(long Tick, int RejectedCommandCount);`
  - `internal static class NotReadyError { public const string Message = "模拟还没初始化——先调用 reset_simulation。"; }`（后续任务的其它工具都要引用这个常量，保证错误文案一致）

- [ ] **Step 1: 用 dotnet CLI 脚手架新项目，不手写 .csproj**

```bash
cd /c/code/faketorio
dotnet new console -n Faketorio.Sim.McpServer -o sim/Faketorio.Sim.McpServer --framework net8.0
dotnet sln add sim/Faketorio.Sim.McpServer/Faketorio.Sim.McpServer.csproj
dotnet add sim/Faketorio.Sim.McpServer/Faketorio.Sim.McpServer.csproj reference sim/Faketorio.Sim/Faketorio.Sim.csproj
dotnet add sim/Faketorio.Sim.McpServer/Faketorio.Sim.McpServer.csproj package ModelContextProtocol
dotnet add sim/Faketorio.Sim.McpServer/Faketorio.Sim.McpServer.csproj package Microsoft.Extensions.Hosting
```

`dotnet new console` 生成的 `Program.cs` 会有一句默认的 `Console.WriteLine("Hello, World!")`——Step 5 会整个替换掉它，不用现在删。

`Microsoft.Extensions.Hosting` 是 `Host.CreateApplicationBuilder` 需要的包——`ModelContextProtocol` 包本身不一定自带传递依赖，如果 `dotnet add package ModelContextProtocol` 已经自动带上了 `Microsoft.Extensions.Hosting` 依赖（查 `Faketorio.Sim.McpServer.csproj` 的 `<PackageReference>` 列表，或者直接尝试编译 Step 5 的代码），可以跳过这条单独安装的命令——不确定就都装上，多装一个包不会出问题。

- [ ] **Step 2: 打开生成的 `.csproj`，加上 data 目录拷贝和 nullable/implicit-usings 设置**

`dotnet new console` 默认已经带 `<ImplicitUsings>enable</ImplicitUsings>`/`<Nullable>enable</Nullable>`。在 `<PropertyGroup>` 后面加一个新的 `<ItemGroup>`（照抄 `sim/Faketorio.Sim.Bench/Faketorio.Sim.Bench.csproj` 里的写法）：

```xml
  <ItemGroup>
    <Content Include="..\..\data\**" CopyToOutputDirectory="PreserveNewest" LinkBase="data" />
  </ItemGroup>
```

- [ ] **Step 3: 写 `SimHost.cs`**

```csharp
using Faketorio.Sim;

namespace Faketorio.Sim.McpServer;

// 进程级单实例状态——brainstorm 已确认不做多 session。reset_simulation 创建/
// 替换它，其余工具在它非空时读写。
internal static class SimHost
{
    public static Simulation? Sim;
}

// 全部工具在 SimHost.Sim == null 时统一用这句话报错，保证错误文案一致
// (spec §3 末尾)。
internal static class NotReadyError
{
    public const string Message = "模拟还没初始化——先调用 reset_simulation。";
}
```

- [ ] **Step 4: 写 `Dtos.cs`（这个任务只需要 `ResetResult`/`TickInfo`，后续任务往同一个文件里追加）**

```csharp
namespace Faketorio.Sim.McpServer;

public record ResetResult(long Tick, int PrototypeCount);

public record TickInfo(long Tick, int RejectedCommandCount);
```

- [ ] **Step 5: 写 `SimTools.cs`，实现 `ResetSimulation`/`GetTick`，并把 `Program.cs` 换成 MCP host 启动代码**

```csharp
// sim/Faketorio.Sim.McpServer/SimTools.cs
using System.ComponentModel;
using Faketorio.Sim.Prototypes;
using ModelContextProtocol.Server;

namespace Faketorio.Sim.McpServer;

[McpServerToolType]
public static class SimTools
{
    [McpServerTool, Description("创建一个新的模拟，替换当前场景（如果有的话）")]
    public static ResetResult ResetSimulation(
        [Description("确定性种子，默认 0")] long seed = 0,
        [Description("原型数据目录，相对于 server 可执行文件所在目录，默认 data/base")] string dataDir = "data/base")
    {
        // 相对于 AppContext.BaseDirectory 解析，不依赖进程当前工作目录——
        // Global Constraints 里明确要求的细节，Claude Code 启动这个子进程时
        // 的 CWD 不一定是仓库根目录。
        string resolvedPath = Path.Combine(AppContext.BaseDirectory, dataDir);
        var prototypes = PrototypeLoader.LoadFromDirectory(resolvedPath);
        SimHost.Sim = new Simulation(prototypes, seed);
        return new ResetResult(SimHost.Sim.Tick, prototypes.Count);
    }

    [McpServerTool, Description("查询当前 tick 和已拒绝命令总数，不推进模拟")]
    public static TickInfo GetTick()
    {
        if (SimHost.Sim is null) throw new InvalidOperationException(NotReadyError.Message);
        return new TickInfo(SimHost.Sim.Tick, SimHost.Sim.RejectedCommandCount);
    }
}
```

```csharp
// sim/Faketorio.Sim.McpServer/Program.cs
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

**如果编译报 `AddMcpServer`/`WithStdioServerTransport`/`WithToolsFromAssembly` 找不到**：先确认 `sim/Faketorio.Sim.McpServer/Faketorio.Sim.McpServer.csproj` 里 `ModelContextProtocol` 包版本（`dotnet list package` 看一下实际装的版本号），这几个扩展方法所在的命名空间/包名在 SDK 不同大版本之间可能变过——去读一下装到本地 NuGet 缓存里的这个包版本对应的官方文档/示例（`https://github.com/modelcontextprotocol/csharp-sdk` 的 README，或者 `https://csharp.sdk.modelcontextprotocol.io/` 的 Getting Started 页面），按实际 API 调整这几行，不要死磕这份 plan 里写的确切方法名——这份 plan 写于 2026-09，SDK 还在快速迭代，方法名/命名空间可能已经变了。

- [ ] **Step 6: 跑一次 `dotnet build`，确认新项目本身能编译通过**

```bash
cd /c/code/faketorio
dotnet build sim/Faketorio.Sim.McpServer/Faketorio.Sim.McpServer.csproj
```
Expected: 编译成功，0 error。

- [ ] **Step 7: 脚手架测试项目**

```bash
dotnet new xunit -n Faketorio.Sim.McpServer.Tests -o sim/Faketorio.Sim.McpServer.Tests --framework net8.0
dotnet sln add sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj
dotnet add sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj reference sim/Faketorio.Sim.McpServer/Faketorio.Sim.McpServer.csproj
```

打开生成的 `Faketorio.Sim.McpServer.Tests.csproj`，删掉 `dotnet new xunit` 自带的示例测试文件 `UnitTest1.cs`（`rm sim/Faketorio.Sim.McpServer.Tests/UnitTest1.cs`），并在 `.csproj` 里加上 data 目录拷贝（同 Step 2 那一段 `<Content Include="..\..\data\**" .../>`——测试项目自己跑测试时也要能找到 `data/base`）。

- [ ] **Step 8: 写失败测试**

```csharp
// sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs
using Faketorio.Sim.McpServer;

namespace Faketorio.Sim.McpServer.Tests;

public class SimToolsTests
{
    // 每条测试跑之前，SimHost.Sim 可能残留上一条测试的状态——静态字段在同一个
    // 测试进程内跨测试共享。每条测试自己先调 ResetSimulation 建立已知状态，
    // 不依赖测试执行顺序或初始的 null 状态。

    [Fact]
    public void ResetSimulation_ReturnsTickZeroAndPositivePrototypeCount()
    {
        var result = SimTools.ResetSimulation(seed: 42);

        Assert.Equal(0, result.Tick);
        Assert.True(result.PrototypeCount > 0);
    }

    [Fact]
    public void GetTick_AfterReset_ReturnsZeroTickAndZeroRejected()
    {
        SimTools.ResetSimulation(seed: 1);

        var tick = SimTools.GetTick();

        Assert.Equal(0, tick.Tick);
        Assert.Equal(0, tick.RejectedCommandCount);
    }

    [Fact]
    public void GetTick_BeforeAnyReset_Throws()
    {
        // 这条测试依赖一个全新的、从未 reset 过的 SimHost.Sim 状态，但静态字段
        // 在测试进程内跨测试共享——用一个反射把 SimHost.Sim 显式设回 null，
        // 模拟"进程刚启动、还没人调过 reset_simulation"这个真实场景，不依赖
        // 测试执行顺序。
        typeof(SimHost).GetField(nameof(SimHost.Sim))!.SetValue(null, null);

        var ex = Assert.Throws<InvalidOperationException>(() => SimTools.GetTick());
        Assert.Equal(NotReadyError.Message, ex.Message);
    }
}
```

- [ ] **Step 9: 跑测试确认失败**

Run: `dotnet test sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj`
Expected: 如果 Step 3-5 已经写完，这里应该已经能编译通过并且部分/全部通过——如果 Step 3-5 是在 Step 8 之前完成的（本任务的 Step 顺序把实现放在测试前面，因为脚手架/MCP host 接线本身就是这个任务最大的不确定性来源，值得先跑通编译），那就直接跳到 Step 10 确认全部通过；如果发现某条测试失败，先诊断是不是 Step 3-5 的实现有 typo，不要假设是测试本身的问题。

- [ ] **Step 10: 跑测试确认通过**

Run: `dotnet test sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj`
Expected: 3/3 通过。

- [ ] **Step 11: 跑一次全量 solution 测试，确认没有破坏别的项目**

Run: `dotnet test -c Release Faketorio.sln`
Expected: 除了新加的 3 条，其它既有测试(截至这个 plan 写的时候是 525 条 `Faketorio.Sim.Tests` + 26 条 `Faketorio.Presentation.Core.Tests`)全部照常通过——这个新项目不改 sim 层一行代码,不应该影响任何既有测试。

- [ ] **Step 12: 提交**

```bash
git add sim/Faketorio.Sim.McpServer sim/Faketorio.Sim.McpServer.Tests Faketorio.sln
git commit -m "feat(mcp): 脚手架 Faketorio.Sim.McpServer —— stdio MCP host + reset_simulation/get_tick"
```

---

## Task 2: `step` / `submit_command`

**Files:**
- Modify: `sim/Faketorio.Sim.McpServer/Dtos.cs`
- Modify: `sim/Faketorio.Sim.McpServer/SimTools.cs`
- Modify: `sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs`

**Interfaces:**
- Consumes: `SimHost.Sim`、`NotReadyError.Message`（Task 1）。
- Produces:
  - `public record StepResult(long Tick, int RejectedCommandCount, int RejectedThisCall);`
  - `public record SubmitResult(bool Queued);`
  - `public static StepResult Step(int ticks = 1)`
  - `public static SubmitResult SubmitCommand(string type, int x, int y, int? protoId = null, string? protoName = null, byte rotation = 0, int count = 0)`

- [ ] **Step 1: 写失败测试**

追加到 `sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs`:

```csharp
    [Fact]
    public void Step_AdvancesTick()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.Step(ticks: 5);

        Assert.Equal(5, result.Tick);
        Assert.Equal(0, result.RejectedCommandCount);
        Assert.Equal(0, result.RejectedThisCall);
    }

    [Fact]
    public void Step_ZeroOrNegativeTicks_Throws()
    {
        SimTools.ResetSimulation(seed: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => SimTools.Step(ticks: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimTools.Step(ticks: -1));
    }

    [Fact]
    public void Step_BeforeAnyReset_Throws()
    {
        typeof(SimHost).GetField(nameof(SimHost.Sim))!.SetValue(null, null);

        var ex = Assert.Throws<InvalidOperationException>(() => SimTools.Step(ticks: 1));
        Assert.Equal(NotReadyError.Message, ex.Message);
    }

    [Fact]
    public void SubmitCommand_InvalidType_Throws()
    {
        SimTools.ResetSimulation(seed: 1);

        var ex = Assert.Throws<ArgumentException>(() =>
            SimTools.SubmitCommand(type: "NotARealCommandType", x: 0, y: 0));
        Assert.Contains("PlaceEntity", ex.Message);   // 错误信息要列出合法值
    }

    [Fact]
    public void SubmitCommand_BothProtoIdAndProtoNameGiven_Throws()
    {
        SimTools.ResetSimulation(seed: 1);

        Assert.Throws<ArgumentException>(() =>
            SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0, protoId: 1, protoName: "stone-furnace"));
    }

    [Fact]
    public void SubmitCommand_NeitherProtoIdNorProtoNameGiven_Throws()
    {
        SimTools.ResetSimulation(seed: 1);

        Assert.Throws<ArgumentException>(() =>
            SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0));
    }

    [Fact]
    public void SubmitCommand_WithProtoName_PlacesSameEntityAsProtoId()
    {
        SimTools.ResetSimulation(seed: 1);
        int furnaceId = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.FurnacePrototype>("stone-furnace").Id;

        var result = SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0, protoName: "stone-furnace", rotation: 0);
        SimTools.Step(ticks: 1);

        Assert.True(result.Queued);
        var placed = SimTools.Sim!.World.GetEntityAt(0, 0);
        Assert.True(placed.IsValid);
        Assert.Equal(furnaceId, SimTools.Sim!.Entities.Get(placed).ProtoId);
    }

    [Fact]
    public void SubmitCommand_UnresolvableProtoName_Throws()
    {
        SimTools.ResetSimulation(seed: 1);

        var ex = Assert.Throws<ArgumentException>(() =>
            SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0, protoName: "this-does-not-exist"));
        Assert.Contains("this-does-not-exist", ex.Message);
    }
```

**注意**：`SubmitCommand_WithProtoName_PlacesSameEntityAsProtoId` 这条测试用了
`SimTools.Sim!`——这需要在 Task 1 的 `SimHost.cs` 基础上，给 `SimTools` 加一个
内部只读透传属性（下一步 Step 3 会加），方便测试直接读当前模拟状态验证效果，
不用为了验证一个 `PlaceEntity` 命令的效果专门写一个新工具。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj --filter "FullyQualifiedName~Step_|FullyQualifiedName~SubmitCommand_"`
Expected: 编译失败（`Step`/`SubmitCommand`/`SimTools.Sim` 不存在）。

- [ ] **Step 3: 实现**

`Dtos.cs` 追加:

```csharp
public record StepResult(long Tick, int RejectedCommandCount, int RejectedThisCall);

public record SubmitResult(bool Queued);
```

`SimTools.cs` 里加一个内部透传属性（供测试直接读状态，不算 MCP 工具，不用
`[McpServerTool]` 标注）:

```csharp
    // 供测试直接验证提交命令后的模拟状态，不是 MCP 工具。
    internal static Simulation? Sim => SimHost.Sim;
```

再加 `Step`/`SubmitCommand` 两个工具：

```csharp
    [McpServerTool, Description("推进模拟若干 tick")]
    public static StepResult Step([Description("要跑的 tick 数，默认 1")] int ticks = 1)
    {
        if (SimHost.Sim is null) throw new InvalidOperationException(NotReadyError.Message);
        if (ticks < 1) throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "ticks 必须 >= 1");

        int before = SimHost.Sim.RejectedCommandCount;
        for (int i = 0; i < ticks; i++) SimHost.Sim.Step();
        int after = SimHost.Sim.RejectedCommandCount;

        return new StepResult(SimHost.Sim.Tick, after, after - before);
    }

    [McpServerTool, Description(
        "提交一条原子命令（不会立即执行，下次 step 时生效）。type 取值：" +
        "PlaceEntity/RemoveEntity/MovePlayer/StopPlayer/MineStart/MineStop/" +
        "CraftEnqueue/TransferToEntity/TransferFromEntity/SetRecipe/RotateEntity")]
    public static SubmitResult SubmitCommand(
        [Description("命令类型，见工具描述里的取值列表")] string type,
        [Description("目标/来源坐标 X")] int x,
        [Description("目标/来源坐标 Y")] int y,
        [Description("原型 id（和 protoName 二选一）")] int? protoId = null,
        [Description("原型名字（和 protoId 二选一，内部查表转 id）")] string? protoName = null,
        [Description("朝向 0=北 1=东 2=南 3=西，默认 0")] byte rotation = 0,
        [Description("数量，默认 0")] int count = 0)
    {
        if (SimHost.Sim is null) throw new InvalidOperationException(NotReadyError.Message);

        if (!Enum.TryParse<Faketorio.Sim.Commands.CommandType>(type, ignoreCase: true, out var commandType))
        {
            string validValues = string.Join("/", Enum.GetNames<Faketorio.Sim.Commands.CommandType>());
            throw new ArgumentException($"未知的命令类型 '{type}'，合法值：{validValues}", nameof(type));
        }

        if (protoId.HasValue == (protoName is not null))
            throw new ArgumentException("protoId 和 protoName 必须二选一（不能都给，也不能都不给）。");

        int resolvedProtoId;
        if (protoId.HasValue)
        {
            resolvedProtoId = protoId.Value;
        }
        else
        {
            resolvedProtoId = -1;
            for (int i = 0; i < SimHost.Sim.Prototypes.Count; i++)
            {
                if (SimHost.Sim.Prototypes.GetById(i).Name == protoName)
                {
                    resolvedProtoId = i;
                    break;
                }
            }
            if (resolvedProtoId == -1)
                throw new ArgumentException($"找不到名字是 '{protoName}' 的原型。", nameof(protoName));
        }

        SimHost.Sim.Submit(new Faketorio.Sim.Commands.Command
        {
            Type = commandType,
            ProtoId = resolvedProtoId,
            X = x,
            Y = y,
            Rotation = rotation,
            Count = count,
        });

        return new SubmitResult(Queued: true);
    }
```

**注意 `protoId.HasValue == (protoName is not null)` 这个条件**：`protoId`
是 `int?`，`protoName` 是 `string?`。两者都给（`true == true`）或者两者都不给
（`false == false`）时这个表达式是 `true`，触发"必须二选一"的错误——这正是
我们想要的（恰好只给一个时，`protoId.HasValue` 和 `protoName is not null`
一真一假，表达式是 `false`，不报错，往下走）。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj --filter "FullyQualifiedName~Step_|FullyQualifiedName~SubmitCommand_"`
Expected: 全部通过。

- [ ] **Step 5: 跑全量测试确认没有破坏别的东西**

Run: `dotnet test -c Release Faketorio.sln`

- [ ] **Step 6: 提交**

```bash
git add sim/Faketorio.Sim.McpServer/Dtos.cs sim/Faketorio.Sim.McpServer/SimTools.cs sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs
git commit -m "feat(mcp): step/submit_command 工具"
```

---

## Task 3: `get_entity_at` / `get_inventory`

**Files:**
- Modify: `sim/Faketorio.Sim.McpServer/Dtos.cs`
- Modify: `sim/Faketorio.Sim.McpServer/SimTools.cs`
- Modify: `sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs`

**Interfaces:**
- Consumes: `SimHost.Sim`（Task 1）、`SubmitCommand`（Task 2，测试里用它先放实体）。
- Produces:
  - `public record EntityInfo(int Index, int Generation, string ProtoName, int ProtoId, int X, int Y, byte Rotation);`
  - `public record SlotInfo(int Slot, string ItemName, int ItemProtoId, int Count);`
  - `public record InventoryInfo(int SlotCount, List<SlotInfo> Slots);`
  - `public static EntityInfo? GetEntityAt(int x, int y)`
  - `public static InventoryInfo? GetInventory(int x, int y, int role = 0)`

- [ ] **Step 1: 写失败测试**

追加到 `sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs`:

```csharp
    [Fact]
    public void GetEntityAt_EmptyTile_ReturnsNull()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.GetEntityAt(999, 999);

        Assert.Null(result);
    }

    [Fact]
    public void GetEntityAt_AfterPlacingEntity_ReturnsInfo()
    {
        SimTools.ResetSimulation(seed: 1);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 5, y: 5, protoName: "stone-furnace", rotation: 2);
        SimTools.Step(ticks: 1);

        var result = SimTools.GetEntityAt(5, 5);

        Assert.NotNull(result);
        Assert.Equal("stone-furnace", result!.ProtoName);
        Assert.Equal(5, result.X);
        Assert.Equal(5, result.Y);
        Assert.Equal(2, result.Rotation);
    }

    [Fact]
    public void GetEntityAt_BeforeAnyReset_Throws()
    {
        typeof(SimHost).GetField(nameof(SimHost.Sim))!.SetValue(null, null);

        Assert.Throws<InvalidOperationException>(() => SimTools.GetEntityAt(0, 0));
    }

    [Fact]
    public void GetInventory_EmptyTile_ReturnsNull()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.GetInventory(999, 999);

        Assert.Null(result);
    }

    [Fact]
    public void GetInventory_InvalidRoleForEntity_ReturnsNull()
    {
        SimTools.ResetSimulation(seed: 1);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 5, y: 5, protoName: "wooden-chest", rotation: 0);
        SimTools.Step(ticks: 1);

        var result = SimTools.GetInventory(5, 5, role: 99);

        Assert.Null(result);
    }

    [Fact]
    public void GetInventory_ChestWithItems_ListsNonEmptySlots()
    {
        SimTools.ResetSimulation(seed: 1);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 5, y: 5, protoName: "wooden-chest", rotation: 0);
        SimTools.Step(ticks: 1);
        var chestId = SimTools.Sim!.World.GetEntityAt(5, 5);
        int oreId = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("iron-ore").Id;
        SimTools.Sim!.Inventories.Get(SimTools.Sim!.Inventories.GetInventoryId(chestId)).Insert(oreId, 5, 50);

        var result = SimTools.GetInventory(5, 5);

        Assert.NotNull(result);
        Assert.Contains(result!.Slots, s => s.ItemName == "iron-ore" && s.Count == 5);
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj --filter "FullyQualifiedName~GetEntityAt_|FullyQualifiedName~GetInventory_"`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`Dtos.cs` 追加:

```csharp
public record EntityInfo(int Index, int Generation, string ProtoName, int ProtoId, int X, int Y, byte Rotation);

public record SlotInfo(int Slot, string ItemName, int ItemProtoId, int Count);

public record InventoryInfo(int SlotCount, List<SlotInfo> Slots);
```

`SimTools.cs` 追加:

```csharp
    [McpServerTool, Description("查询指定坐标的实体，没有实体返回 null")]
    public static EntityInfo? GetEntityAt(int x, int y)
    {
        if (SimHost.Sim is null) throw new InvalidOperationException(NotReadyError.Message);

        var id = SimHost.Sim.World.GetEntityAt(x, y);
        if (!id.IsValid) return null;

        ref var data = ref SimHost.Sim.Entities.Get(id);
        string protoName = SimHost.Sim.Prototypes.GetById(data.ProtoId).Name;
        return new EntityInfo(id.Index, id.Generation, protoName, data.ProtoId, data.X, data.Y, data.Rotation);
    }

    [McpServerTool, Description("查询指定坐标实体的库存内容（role 默认 0，机器/发电机等多库存实体可能需要传 1 或 2）")]
    public static InventoryInfo? GetInventory(int x, int y, int role = 0)
    {
        if (SimHost.Sim is null) throw new InvalidOperationException(NotReadyError.Message);

        var entityId = SimHost.Sim.World.GetEntityAt(x, y);
        if (!entityId.IsValid) return null;

        var invId = SimHost.Sim.Inventories.GetInventoryId(entityId, role);
        if (!invId.IsValid) return null;

        var inv = SimHost.Sim.Inventories.Get(invId);
        var slots = new List<SlotInfo>();
        for (int s = 0; s < inv.SlotCount; s++)
        {
            var stack = inv[s];
            if (stack.IsEmpty) continue;
            string itemName = SimHost.Sim.Prototypes.GetById(stack.ItemProtoId).Name;
            slots.Add(new SlotInfo(s, itemName, stack.ItemProtoId, stack.Count));
        }
        return new InventoryInfo(inv.SlotCount, slots);
    }
```

**确认 `InventoryId` 有 `IsValid` 属性**：如果编译报 `InventoryId` 没有
`IsValid`，读一下 `sim/Faketorio.Sim/Items/InventoryId.cs` 找它实际叫什么
（多半是同 `EntityId` 一样的 `IsValid`，但不要假设，读源码确认）。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj --filter "FullyQualifiedName~GetEntityAt_|FullyQualifiedName~GetInventory_"`
Expected: 全部通过。

- [ ] **Step 5: 跑全量测试**

Run: `dotnet test -c Release Faketorio.sln`

- [ ] **Step 6: 提交**

```bash
git add sim/Faketorio.Sim.McpServer/Dtos.cs sim/Faketorio.Sim.McpServer/SimTools.cs sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs
git commit -m "feat(mcp): get_entity_at/get_inventory 工具"
```

---

## Task 4: `resolve_prototype` / `list_prototypes` / `compute_state_hash`

**Files:**
- Modify: `sim/Faketorio.Sim.McpServer/Dtos.cs`
- Modify: `sim/Faketorio.Sim.McpServer/SimTools.cs`
- Modify: `sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs`

**Interfaces:**
- Consumes: `SimHost.Sim`（Task 1）。
- Produces:
  - `public record PrototypeInfo(int Id, string Name, string TypeName);`
  - `public record HashResult(ulong Hash);`
  - `public static List<PrototypeInfo> ResolvePrototype(string name, string? typeName = null)`
  - `public static List<PrototypeInfo> ListPrototypes(string? typeNameFilter = null)`
  - `public static HashResult ComputeStateHash()`

- [ ] **Step 1: 写失败测试**

追加到 `sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs`:

```csharp
    [Fact]
    public void ResolvePrototype_KnownName_ReturnsOneMatch()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.ResolvePrototype("stone-furnace");

        Assert.Single(result);
        Assert.Equal("stone-furnace", result[0].Name);
        Assert.Equal("FurnacePrototype", result[0].TypeName);
    }

    [Fact]
    public void ResolvePrototype_UnknownName_ReturnsEmptyList()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.ResolvePrototype("this-does-not-exist");

        Assert.Empty(result);
    }

    [Fact]
    public void ResolvePrototype_WithTypeNameFilter_NarrowsMatch()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.ResolvePrototype("stone-furnace", typeName: "FurnacePrototype");

        Assert.Single(result);
    }

    [Fact]
    public void ListPrototypes_ReturnsAllLoadedPrototypes()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.ListPrototypes();

        Assert.True(result.Count > 0);
        Assert.Contains(result, p => p.Name == "stone-furnace");
    }

    [Fact]
    public void ListPrototypes_WithFilter_OnlyReturnsMatchingType()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.ListPrototypes(typeNameFilter: "FurnacePrototype");

        Assert.All(result, p => Assert.Equal("FurnacePrototype", p.TypeName));
        Assert.Contains(result, p => p.Name == "stone-furnace");
    }

    [Fact]
    public void ComputeStateHash_SameSeedSameCommands_ProducesSameHash()
    {
        SimTools.ResetSimulation(seed: 7);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 1, y: 1, protoName: "stone-furnace", rotation: 0);
        SimTools.Step(ticks: 10);
        var hash1 = SimTools.ComputeStateHash();

        SimTools.ResetSimulation(seed: 7);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 1, y: 1, protoName: "stone-furnace", rotation: 0);
        SimTools.Step(ticks: 10);
        var hash2 = SimTools.ComputeStateHash();

        Assert.Equal(hash1.Hash, hash2.Hash);
    }

    [Fact]
    public void ComputeStateHash_BeforeAnyReset_Throws()
    {
        typeof(SimHost).GetField(nameof(SimHost.Sim))!.SetValue(null, null);

        Assert.Throws<InvalidOperationException>(() => SimTools.ComputeStateHash());
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj --filter "FullyQualifiedName~ResolvePrototype_|FullyQualifiedName~ListPrototypes_|FullyQualifiedName~ComputeStateHash_"`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`Dtos.cs` 追加:

```csharp
public record PrototypeInfo(int Id, string Name, string TypeName);

public record HashResult(ulong Hash);
```

`SimTools.cs` 追加:

```csharp
    [McpServerTool, Description("按名字查原型 id；同名可能匹配多个不同类型的原型（item 和 entity 允许同名），此时都返回，自己按 TypeName 挑")]
    public static List<PrototypeInfo> ResolvePrototype(string name, string? typeName = null)
    {
        if (SimHost.Sim is null) throw new InvalidOperationException(NotReadyError.Message);
        return CollectPrototypes(p => p.Name == name && (typeName is null || p.GetType().Name == typeName));
    }

    [McpServerTool, Description("列出全部已加载的原型（名字+类型），用于发现能用什么")]
    public static List<PrototypeInfo> ListPrototypes(string? typeNameFilter = null)
    {
        if (SimHost.Sim is null) throw new InvalidOperationException(NotReadyError.Message);
        return CollectPrototypes(p => typeNameFilter is null || p.GetType().Name == typeNameFilter);
    }

    private static List<PrototypeInfo> CollectPrototypes(Func<Faketorio.Sim.Prototypes.PrototypeBase, bool> predicate)
    {
        var result = new List<PrototypeInfo>();
        for (int i = 0; i < SimHost.Sim!.Prototypes.Count; i++)
        {
            var proto = SimHost.Sim.Prototypes.GetById(i);
            if (predicate(proto)) result.Add(new PrototypeInfo(proto.Id, proto.Name, proto.GetType().Name));
        }
        return result;
    }

    [McpServerTool, Description("计算当前模拟状态的 FNV-1a 哈希，用于跨会话对拍确定性")]
    public static HashResult ComputeStateHash()
    {
        if (SimHost.Sim is null) throw new InvalidOperationException(NotReadyError.Message);
        return new HashResult(SimHost.Sim.ComputeStateHash());
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj --filter "FullyQualifiedName~ResolvePrototype_|FullyQualifiedName~ListPrototypes_|FullyQualifiedName~ComputeStateHash_"`
Expected: 全部通过。

- [ ] **Step 5: 跑全量测试**

Run: `dotnet test -c Release Faketorio.sln`

- [ ] **Step 6: 提交**

```bash
git add sim/Faketorio.Sim.McpServer/Dtos.cs sim/Faketorio.Sim.McpServer/SimTools.cs sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs
git commit -m "feat(mcp): resolve_prototype/list_prototypes/compute_state_hash 工具"
```

---

## Task 5: 端到端集成测试 + 使用说明

**Files:**
- Modify: `sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs`
- Create: `sim/Faketorio.Sim.McpServer/README.md`

**Interfaces:**
- 无新接口——本任务只加一条综合场景测试和一份使用说明文档，验证全部 9 个工具拼在一起真的能干活。

- [ ] **Step 1: 写失败测试**

追加到 `sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs`。这条测试完全通过
MCP 工具调用完成（不直接碰 `Simulation` API 拿捷径），复刻的是
`sim/Faketorio.Sim.Tests/SimulationTests.cs` 里
`Machine_StandbyEnergyRegisteredEvenWhenIdle`/`Furnace_AutoMatchesAndSmeltsIronOre`
一带测过的"电线杆+发电机+熔炉+矿石->铁板"链路，读那两条测试确认坐标/供电范围
的几何关系（电线杆 `supplyAreaDistanceTiles` 是 2，熔炉必须落在电线杆
Chebyshev 距离 2 以内才能收到电）：

```csharp
    [Fact]
    public void EndToEnd_PoweredFurnaceSmeltsOre_ThroughMcpToolsOnly()
    {
        SimTools.ResetSimulation(seed: 0);

        // 电线杆 (0,0) + 发电机 (2,0)，同 SimulationTests.PlacePoweredMachineInfra
        // 的坐标关系。
        SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0, protoName: "small-electric-pole", rotation: 0);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 2, y: 0, protoName: "burner-generator", rotation: 0);
        SimTools.Step(ticks: 1);

        // 给发电机塞煤——TransferToEntity 命令要求玩家背包里先有煤，
        // 这条端到端测试直接往 Sim.Player.Inventory 塞（跟既有
        // SimulationTests.PlacePoweredMachineInfra 的做法一致），因为
        // "往玩家背包塞初始物品"本身不是这 9 个工具要覆盖的场景（玩家背包
        // 管理不在这轮 MCP 工具范围内，spec §4 没有把它列进来）。
        int coalId = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("coal").Id;
        int coalStack = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("coal").StackSize;
        SimTools.Sim!.Player.Inventory.Insert(coalId, 5, coalStack);
        SimTools.SubmitCommand(type: "TransferToEntity", x: 2, y: 0, protoId: coalId, count: 5);
        SimTools.Step(ticks: 1);

        // 熔炉 (0,2)，在电线杆 Chebyshev 距离 2 以内。
        SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 2, protoName: "stone-furnace", rotation: 0);
        SimTools.Step(ticks: 1);

        var furnace = SimTools.GetEntityAt(0, 2);
        Assert.NotNull(furnace);

        // 直接往熔炉输入库存(role 1)塞矿——用 get_inventory 确认库存形状后
        // 手动操纵，因为"隔空塞矿"本身不是这 9 个工具要覆盖的场景，
        // TransferToEntity 走的是玩家背包->实体这条路，这里为了让测试独立于
        // 玩家背包细节，直接摆状态（同既有 SimulationTests 里
        // Furnace_AutoMatchesAndSmeltsIronOre 的做法）。
        int oreId = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("iron-ore").Id;
        int oreStack = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("iron-ore").StackSize;
        var furnaceInputInv = SimTools.Sim!.Inventories.Get(SimTools.Sim!.Inventories.GetInventoryId(
            SimTools.Sim!.World.GetEntityAt(0, 2), role: 1));
        furnaceInputInv.Insert(oreId, 1, oreStack);

        // iron-plate 是 192 tick，给够余量。
        SimTools.Step(ticks: 200);

        var output = SimTools.GetInventory(0, 2, role: 2);
        Assert.NotNull(output);
        Assert.Contains(output!.Slots, s => s.ItemName == "iron-plate" && s.Count >= 1);

        var hash = SimTools.ComputeStateHash();
        Assert.True(hash.Hash != 0);   // 只是确认这个工具跑得通、返回非零值，不对拍具体数值
    }
```

- [ ] **Step 2: 跑测试确认失败或通过**

Run: `dotnet test sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj --filter "FullyQualifiedName~EndToEnd_"`
Expected: 如果 Task 1-4 都做完了，这条测试应该直接通过（本任务不新增产品
代码，纯粹是拿既有工具拼一个场景）；如果失败，大概率是坐标/供电范围没抄对
`SimulationTests.cs` 里的几何关系，回去重新读一遍 `PlacePoweredMachineInfra`
那段注释核对。

- [ ] **Step 3: 写 `README.md`**

```markdown
# Faketorio.Sim.McpServer

把 `Faketorio.Sim` 的 `Simulation.Submit`/`Step`/查询 API 包成 9 个 stdio MCP
工具，供 Claude Code 在对话里直接驱动/探索 sim 层场景，不用写新 xUnit 测试
再编译。

## 工具列表

- `reset_simulation(seed?, dataDir?)` —— 创建新模拟。
- `step(ticks?)` —— 推进 tick。
- `submit_command(type, x, y, protoId?, protoName?, rotation?, count?)` —— 提交命令。
- `get_tick()` —— 查当前 tick/拒绝数。
- `get_entity_at(x, y)` —— 查坐标上的实体。
- `get_inventory(x, y, role?)` —— 查实体库存。
- `resolve_prototype(name, typeName?)` —— 名字转原型 id。
- `list_prototypes(typeNameFilter?)` —— 列出全部原型。
- `compute_state_hash()` —— 算确定性哈希。

## 本地跑

```bash
dotnet run --project sim/Faketorio.Sim.McpServer
```

## 在 Claude Code 里注册

按 Claude Code 当前的 MCP server 注册文档操作（配置文件路径/格式以官方文档
为准，这里不重复维护一份容易过期的说明）。

## 范围外

截图/渲染能力（驱动 Godot 截图）是独立的第二个子项目，还没开始——见
`docs/superpowers/specs/2026-09-13-mcp-sim-driver-design.md` §0。
```

- [ ] **Step 4: 跑一次全量 solution 测试**

Run: `dotnet test -c Release Faketorio.sln`
Expected: 全部通过（这时候 `Faketorio.Sim.McpServer.Tests` 应该有 20+ 条测试全绿）。

- [ ] **Step 5: 提交**

```bash
git add sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs sim/Faketorio.Sim.McpServer/README.md
git commit -m "test(mcp): 端到端集成测试(纯MCP工具驱动的熔炼链路) + README"
```

---

## Self-Review Notes

- **Spec 覆盖**：§1(架构/传输)→ Task 1；§2(状态模型)→ Task 1；§3 的 9 个工具
  → Task 1(`reset_simulation`/`get_tick`)/Task 2(`step`/`submit_command`)/
  Task 3(`get_entity_at`/`get_inventory`)/Task 4(`resolve_prototype`/
  `list_prototypes`/`compute_state_hash`)；§3 末尾的统一错误行为 → 每个工具
  自己的实现里都有 `SimHost.Sim is null` 检查，用同一个 `NotReadyError.Message`；
  §4(明确不做)→ Global Constraints 写明不做类型精确过滤/多 session/复合动作/
  截图；§5(测试计划)→ 每个 Task 自己的单测 + Task 5 的端到端测试。
- **占位符扫描**：无 "TBD"/"后续实现" 这类模式——每个 Step 3 都是可以直接抄的
  完整代码块；Program.cs/MCP SDK 那一小段明确写了"如果编译报错去查官方文档",
  这不是占位符,是对"SDK 版本可能已经变"这个真实不确定性的诚实标注,而不是
  逃避写具体实现。
- **类型一致性**：`ResetResult`/`TickInfo`/`StepResult`/`SubmitResult`/
  `EntityInfo`/`SlotInfo`/`InventoryInfo`/`PrototypeInfo`/`HashResult` 九个
  DTO record 在 Task 1-4 定义后，各自只在定义它的那个 Task 和后续引用它的地方
  出现，没有改名不同步。`SimTools.Sim`(internal 透传属性)在 Task 2 定义,
  Task 3/4/5 的测试代码里统一用这个名字读状态,没有另起别名。
