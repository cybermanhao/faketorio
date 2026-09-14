# MCP 截图能力 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 `Faketorio.Sim.McpServer` 加第 10 个工具 `get_screenshot`——记录 MCP server 这边的操作历史，截图时拉起一个新的 Godot 子进程，用同一份历史重放出字节对齐的模拟状态，摆好相机，截一张 PNG 传回来。

**Architecture:** MCP server 端新增 `OperationLog`（记 Command + step 序列）+ `GetScreenshot` 工具（序列化历史到临时文件、拉子进程、等退出、读 PNG、清理临时文件）。`game/` 项目的既有 `SimHost.cs` 加一个"回放模式"分支（检测到命令行参数就跳过写死的演示场景，改成从文件按顺序重放，同步做完，不走实时 `_Process` 推进）；新增 `ReplayCapture.cs`（等渲染追上、摆相机、截图、退出）；`CameraController.cs` 加一个程序化定位相机的公开方法。两边通过同一个 `Faketorio.Sim` 项目引用共享 `Command`/`CommandType` 类型，JSON 序列化用 `System.Text.Json` 直接对着这些真实类型读写，不用手写字符串协议。

**Tech Stack:** C# / .NET 8，`ModelContextProtocol` NuGet 包，Godot 4.5.1 Mono，`System.Text.Json`，xUnit（限 MCP server 端能测的部分——Godot 端代码没有自动化测试基础设施，靠手动验证）。

**Spec:** `docs/superpowers/specs/2026-09-13-mcp-screenshot-design.md`

## Global Constraints

- 两个同名但完全不同的 `SimHost` 类:`sim/Faketorio.Sim.McpServer/SimHost.cs`（本计划扩展它，加 `Seed`/`DataDir` 静态字段）和 `game/SimHost.cs`（Godot autoload，本计划给它加回放分支）——每个任务的文件路径已经精确到具体项目，不要混淆（spec §2）。
- `game/SimHost.cs` 现有的正常启动路径（演示场景 + 实时 `_Process` 推进）**不能变**——普通双击/`dotnet run` 启动游戏的行为必须跟现在完全一样，回放模式只在检测到特定命令行参数时才激活（spec §5）。
- 不硬编码这台机器的 Godot 可执行文件路径——用环境变量 `FAKETORIO_GODOT_EXE`，没设置就报清楚的错误，不猜测/不搜索 `PATH`（spec §6）。
- 相机自动定位、debug 覆盖层叠加、常驻 Godot 进程、真人游戏会话截图——都明确不做（spec §7）。
- Godot 端代码（`game/*.cs`）没有 xUnit 一类的自动化测试覆盖——这次也不新建测试基础设施，正确性靠手动跑一次完整链路验证（spec §8）。
- **本计划做出的技术选型决定**（spec 留给实施阶段的开放点，这里钉死，避免任务之间对不上）：
  - 回放/截图逻辑写在 C#（`game/*.cs`），不用 GDScript——`game/` 项目本来就是 Mono/C#，用 C# 能直接 `using Faketorio.Sim.Commands;` 复用真实的 `Command`/`CommandType` 类型做 JSON 反序列化，不用在两边手写一套字符串协议再对齐。
  - 藏窗口用"挪到屏幕外"（`--position -3000,-3000` 传给 Godot 启动参数），不用"最小化"——两种 spike 都验证过能截图，但没有专门验证过"先按 `--width`/`--height` 设置窗口大小,再最小化"这个组合,选屏幕外定位避免这个没验证过的组合风险。

---

## Task 1: `OperationLog` —— MCP server 端记录操作历史

**Files:**
- Create: `sim/Faketorio.Sim.McpServer/OperationLog.cs`
- Modify: `sim/Faketorio.Sim.McpServer/SimHost.cs`
- Modify: `sim/Faketorio.Sim.McpServer/SimTools.cs:12-46`（`ResetSimulation`/`Step`/`SubmitCommand`，当前精确行号——**动手前先重新读一遍这三个方法的当前代码确认没变**）
- Test: `sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct LogEntry(bool IsStep, Faketorio.Sim.Commands.Command Command, int StepTicks);`
  - `internal static class OperationLog { public static void RecordCommand(Command); public static void RecordStep(int); public static void Clear(); public static IReadOnlyList<LogEntry> Entries { get; } }`
  - `SimHost.Seed`/`SimHost.DataDir`（新增两个 `public static` 字段，`ResetSimulation` 里一起赋值）。

- [ ] **Step 1: 写失败测试**

追加到 `sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs`:

```csharp
    [Fact]
    public void ResetSimulation_ClearsOperationLog()
    {
        SimTools.ResetSimulation(seed: 1);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0, protoName: "small-electric-pole");

        SimTools.ResetSimulation(seed: 2);   // 第二次 reset 应该清空第一次留下的历史

        Assert.Empty(OperationLog.Entries);
    }

    [Fact]
    public void ResetSimulation_RecordsSeedAndDataDir()
    {
        SimTools.ResetSimulation(seed: 42, dataDir: "data/base");

        Assert.Equal(42, SimHost.Seed);
        Assert.Equal("data/base", SimHost.DataDir);
    }

    [Fact]
    public void SubmitCommand_Success_AppendsCommandToLog()
    {
        SimTools.ResetSimulation(seed: 1);

        SimTools.SubmitCommand(type: "PlaceEntity", x: 3, y: 4, protoName: "small-electric-pole", rotation: 2);

        var entry = Assert.Single(OperationLog.Entries);
        Assert.False(entry.IsStep);
        Assert.Equal(Faketorio.Sim.Commands.CommandType.PlaceEntity, entry.Command.Type);
        Assert.Equal(3, entry.Command.X);
        Assert.Equal(4, entry.Command.Y);
        Assert.Equal((byte)2, entry.Command.Rotation);
    }

    [Fact]
    public void Step_AppendsStepEntryToLog()
    {
        SimTools.ResetSimulation(seed: 1);

        SimTools.Step(ticks: 7);

        var entry = Assert.Single(OperationLog.Entries);
        Assert.True(entry.IsStep);
        Assert.Equal(7, entry.StepTicks);
    }

    [Fact]
    public void SubmitCommand_And_Step_AppendInOrder()
    {
        SimTools.ResetSimulation(seed: 1);

        SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0, protoName: "small-electric-pole");
        SimTools.Step(ticks: 3);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 1, y: 0, protoName: "small-electric-pole");

        Assert.Equal(3, OperationLog.Entries.Count);
        Assert.False(OperationLog.Entries[0].IsStep);
        Assert.True(OperationLog.Entries[1].IsStep);
        Assert.False(OperationLog.Entries[2].IsStep);
    }
```

（这几条测试需要在文件顶部能看到 `internal` 的 `OperationLog`/`SimHost`——已有的
`InternalsVisibleTo` 配置覆盖了这个，不用再改 `.csproj`。）

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj --filter "FullyQualifiedName~ResetSimulation_ClearsOperationLog|FullyQualifiedName~ResetSimulation_RecordsSeedAndDataDir|FullyQualifiedName~SubmitCommand_Success_AppendsCommandToLog|FullyQualifiedName~Step_AppendsStepEntryToLog|FullyQualifiedName~SubmitCommand_And_Step_AppendInOrder"`
Expected: 编译失败（`OperationLog`/`SimHost.Seed`/`SimHost.DataDir` 不存在）。

- [ ] **Step 3: 实现**

新建 `sim/Faketorio.Sim.McpServer/OperationLog.cs`:

```csharp
namespace Faketorio.Sim.McpServer;

// 一条历史记录——要么是"提交了这条命令"，要么是"推进了 N 个 tick"。用来在
// Godot 那边重放出跟 MCP server 这边字节对齐的状态（sim 层本来就是确定性
// 的，这里只是把同一份操作序列原样喂给另一个 Simulation 实例）。
public readonly record struct LogEntry(bool IsStep, Faketorio.Sim.Commands.Command Command, int StepTicks);

internal static class OperationLog
{
    private static readonly List<LogEntry> _entries = new();

    public static void RecordCommand(Faketorio.Sim.Commands.Command command)
        => _entries.Add(new LogEntry(false, command, 0));

    public static void RecordStep(int ticks)
        => _entries.Add(new LogEntry(true, default, ticks));

    public static void Clear() => _entries.Clear();

    public static IReadOnlyList<LogEntry> Entries => _entries;
}
```

`sim/Faketorio.Sim.McpServer/SimHost.cs` 加两个字段:

```csharp
internal static class SimHost
{
    public static Simulation? Sim;
    public static long Seed;
    public static string DataDir = "";
}
```

`sim/Faketorio.Sim.McpServer/SimTools.cs` 三处改动:

```csharp
    public static ResetResult ResetSimulation(
        [Description("确定性种子，默认 0")] long seed = 0,
        [Description("原型数据目录，相对于 server 可执行文件所在目录，默认 data/base")] string dataDir = "data/base")
    {
        string resolvedPath = Path.Combine(AppContext.BaseDirectory, dataDir);
        var prototypes = PrototypeLoader.LoadFromDirectory(resolvedPath);
        SimHost.Sim = new Simulation(prototypes, seed);
        SimHost.Seed = seed;
        SimHost.DataDir = dataDir;
        OperationLog.Clear();
        return new ResetResult(SimHost.Sim.Tick, prototypes.Count);
    }
```

```csharp
    public static StepResult Step([Description("要跑的 tick 数，默认 1")] int ticks = 1)
    {
        if (SimHost.Sim is null) throw new ModelContextProtocol.McpException(NotReadyError.Message);
        if (ticks < 1) throw new ModelContextProtocol.McpException($"ticks 必须 >= 1（收到 {ticks}）");

        int before = SimHost.Sim.RejectedCommandCount;
        for (int i = 0; i < ticks; i++) SimHost.Sim.Step();
        int after = SimHost.Sim.RejectedCommandCount;
        OperationLog.RecordStep(ticks);

        return new StepResult(SimHost.Sim.Tick, after, after - before);
    }
```

`SubmitCommand` 里,在现有的 `SimHost.Sim.Submit(new Faketorio.Sim.Commands.Command { ... });` 那句之后、`return new SubmitResult(Queued: true);` 之前,加一行:

```csharp
        var submittedCommand = new Faketorio.Sim.Commands.Command
        {
            Type = commandType,
            ProtoId = resolvedProtoId,
            X = x,
            Y = y,
            Rotation = rotation,
            Count = count,
        };
        SimHost.Sim.Submit(submittedCommand);
        OperationLog.RecordCommand(submittedCommand);

        return new SubmitResult(Queued: true);
```

（原来的代码是直接 `new Command {...}` 作为 `Submit` 的参数内联写的，这里拆成一个
局部变量 `submittedCommand`，是为了同一个值既能传给 `Submit` 又能传给
`RecordCommand`，不是别的原因。）

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj --filter "FullyQualifiedName~ResetSimulation_ClearsOperationLog|FullyQualifiedName~ResetSimulation_RecordsSeedAndDataDir|FullyQualifiedName~SubmitCommand_Success_AppendsCommandToLog|FullyQualifiedName~Step_AppendsStepEntryToLog|FullyQualifiedName~SubmitCommand_And_Step_AppendInOrder"`
Expected: 全部通过。

- [ ] **Step 5: 跑全量测试确认没有破坏别的东西**

Run: `dotnet test -c Release Faketorio.sln`

- [ ] **Step 6: 提交**

```bash
git add sim/Faketorio.Sim.McpServer/OperationLog.cs sim/Faketorio.Sim.McpServer/SimHost.cs sim/Faketorio.Sim.McpServer/SimTools.cs sim/Faketorio.Sim.McpServer.Tests/SimToolsTests.cs
git commit -m "feat(mcp): OperationLog —— 记录操作历史,为截图重放做准备"
```

---

## Task 2: 操作历史的 JSON 序列化

**Files:**
- Create: `sim/Faketorio.Sim.McpServer/ReplayLogFormat.cs`
- Test: `sim/Faketorio.Sim.McpServer.Tests/ReplayLogFormatTests.cs`（新建——先确认这个文件名当前不存在再动手,同类的坑已经在这个计划系列里踩过两次）

**Interfaces:**
- Consumes: `OperationLog.Entries`、`SimHost.Seed`/`SimHost.DataDir`（Task 1）。
- Produces: `internal static class ReplayLogFormat { public static string Serialize(IReadOnlyList<LogEntry> entries, long seed, string dataDir); }`——**这个方法只在 MCP server 端用**,Godot 端（Task 4）按同一份 JSON 结构写反序列化代码,两边不共享程序集,靠这份 spec/plan 里写清楚的 schema 对齐,不是靠共享类型（`game/` 项目目前不引用 `Faketorio.Sim.McpServer`,这次也不建立这个引用——两个可执行文件之间只通过文件系统上的 JSON 文本交换数据）。

- [ ] **Step 1: 写失败测试**

新建 `sim/Faketorio.Sim.McpServer.Tests/ReplayLogFormatTests.cs`:

```csharp
using System.Text.Json;
using Faketorio.Sim.Commands;
using Faketorio.Sim.McpServer;

namespace Faketorio.Sim.McpServer.Tests;

public class ReplayLogFormatTests
{
    [Fact]
    public void Serialize_EmptyLog_ProducesValidJsonWithSeedAndDataDir()
    {
        string json = ReplayLogFormat.Serialize(Array.Empty<LogEntry>(), seed: 99, dataDir: "data/base");

        using var doc = JsonDocument.Parse(json);   // 不抛异常就是合法 JSON
        Assert.Equal(99, doc.RootElement.GetProperty("seed").GetInt64());
        Assert.Equal("data/base", doc.RootElement.GetProperty("dataDir").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public void Serialize_CommandEntry_RoundTripsAllFields()
    {
        var command = new Command { Type = CommandType.PlaceEntity, ProtoId = 7, X = 3, Y = -4, Rotation = 2, Count = 0 };
        var entries = new[] { new LogEntry(IsStep: false, Command: command, StepTicks: 0) };

        string json = ReplayLogFormat.Serialize(entries, seed: 1, dataDir: "data/base");
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("entries")[0];

        Assert.False(entry.GetProperty("isStep").GetBoolean());
        var cmd = entry.GetProperty("command");
        Assert.Equal((int)CommandType.PlaceEntity, cmd.GetProperty("type").GetInt32());
        Assert.Equal(7, cmd.GetProperty("protoId").GetInt32());
        Assert.Equal(3, cmd.GetProperty("x").GetInt32());
        Assert.Equal(-4, cmd.GetProperty("y").GetInt32());
        Assert.Equal(2, cmd.GetProperty("rotation").GetInt32());
        Assert.Equal(0, cmd.GetProperty("count").GetInt32());
    }

    [Fact]
    public void Serialize_StepEntry_HasIsStepTrueAndStepTicks()
    {
        var entries = new[] { new LogEntry(IsStep: true, Command: default, StepTicks: 42) };

        string json = ReplayLogFormat.Serialize(entries, seed: 1, dataDir: "data/base");
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("entries")[0];

        Assert.True(entry.GetProperty("isStep").GetBoolean());
        Assert.Equal(42, entry.GetProperty("stepTicks").GetInt32());
    }

    [Fact]
    public void Serialize_MultipleEntries_PreservesOrder()
    {
        var entries = new[]
        {
            new LogEntry(false, new Command { Type = CommandType.PlaceEntity, ProtoId = 1, X = 0, Y = 0 }, 0),
            new LogEntry(true, default, 5),
            new LogEntry(false, new Command { Type = CommandType.RotateEntity, ProtoId = 0, X = 0, Y = 0, Rotation = 1 }, 0),
        };

        string json = ReplayLogFormat.Serialize(entries, seed: 1, dataDir: "data/base");
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("entries");

        Assert.Equal(3, arr.GetArrayLength());
        Assert.False(arr[0].GetProperty("isStep").GetBoolean());
        Assert.True(arr[1].GetProperty("isStep").GetBoolean());
        Assert.False(arr[2].GetProperty("isStep").GetBoolean());
        Assert.Equal((int)CommandType.RotateEntity, arr[2].GetProperty("command").GetProperty("type").GetInt32());
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj --filter "FullyQualifiedName~ReplayLogFormatTests"`
Expected: 编译失败（`ReplayLogFormat` 不存在）。

- [ ] **Step 3: 实现**

```csharp
// sim/Faketorio.Sim.McpServer/ReplayLogFormat.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using Faketorio.Sim.Commands;

namespace Faketorio.Sim.McpServer;

// 操作历史的 JSON 序列化格式——供 Godot 端(game/SimHost.cs 的回放分支,
// Task 4)反序列化。schema:
//   { "seed": <long>, "dataDir": <string>,
//     "entries": [
//       { "isStep": false, "command": { "type": <int>, "protoId": <int>, "x": <int>, "y": <int>, "rotation": <int>, "count": <int> } },
//       { "isStep": true, "stepTicks": <int> },
//       ...
//     ] }
// command.type 是 CommandType 枚举的底层数值(byte 转成的 int),不是字符串——
// Godot 端拿到这个数值直接 (CommandType)value 转回来即可,两边共享同一份
// Faketorio.Sim 程序集里的枚举定义,数值本身就是唯一真源,不需要额外的
// 字符串<->枚举映射表。
internal static class ReplayLogFormat
{
    public static string Serialize(IReadOnlyList<LogEntry> entries, long seed, string dataDir)
    {
        var root = new JsonObject
        {
            ["seed"] = seed,
            ["dataDir"] = dataDir,
        };

        var entriesArray = new JsonArray();
        foreach (var entry in entries)
        {
            var entryObj = new JsonObject { ["isStep"] = entry.IsStep };
            if (entry.IsStep)
            {
                entryObj["stepTicks"] = entry.StepTicks;
            }
            else
            {
                entryObj["command"] = new JsonObject
                {
                    ["type"] = (int)entry.Command.Type,
                    ["protoId"] = entry.Command.ProtoId,
                    ["x"] = entry.Command.X,
                    ["y"] = entry.Command.Y,
                    ["rotation"] = entry.Command.Rotation,
                    ["count"] = entry.Command.Count,
                };
            }
            entriesArray.Add(entryObj);
        }
        root["entries"] = entriesArray;

        return root.ToJsonString();
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test sim/Faketorio.Sim.McpServer.Tests/Faketorio.Sim.McpServer.Tests.csproj --filter "FullyQualifiedName~ReplayLogFormatTests"`
Expected: 全部通过。

- [ ] **Step 5: 跑全量测试**

Run: `dotnet test -c Release Faketorio.sln`

- [ ] **Step 6: 提交**

```bash
git add sim/Faketorio.Sim.McpServer/ReplayLogFormat.cs sim/Faketorio.Sim.McpServer.Tests/ReplayLogFormatTests.cs
git commit -m "feat(mcp): 操作历史 JSON 序列化格式(ReplayLogFormat),供 Godot 端重放读取"
```

---

## Task 3: `CameraController` 加程序化定位

**Files:**
- Modify: `game/CameraController.cs`（当前 141 行,`SetCameraForScreenshot` 加在 `Zoomstep` 方法之后、`TargetTile` 方法之前，或任何合理位置——这个文件没有配套的自动化测试，见 Global Constraints）

**Interfaces:**
- Produces: `public void SetCameraForScreenshot(double worldX, double worldY, double ppt)`——Task 5 的 `ReplayCapture.cs` 会调用它。

- [ ] **Step 1: 实现**

`game/CameraController.cs` 里加:

```csharp
    // 供截图回放流程调用:直接把相机摆到指定世界坐标 + 缩放,不走鼠标/键盘
    // 输入。进 Free 模式(不受 Follow 的近景窄缩放区间限制),ppt 仍然按 Free
    // 的 [FreeMinPpt, FreeMaxPpt] 夹一下,避免传入离谱的值渲染出全黑/全是
    // 网格线的图。
    public void SetCameraForScreenshot(double worldX, double worldY, double ppt)
    {
        Mode = CameraMode.Free;
        _panning = false;
        _snapping = false;
        _centerTile = new Vector2((float)worldX, (float)worldY);
        _ppt = System.Math.Clamp(ppt, FreeMinPpt, FreeMaxPpt);
    }
```

- [ ] **Step 2: 编译确认没有语法错误**

Run: `dotnet build game/Game.csproj`
Expected: 编译成功——这一步没有自动化测试可跑,只能确认能编译;真正的行为验证要等 Task 5/6 端到端跑起来才能看到效果。

- [ ] **Step 3: 提交**

```bash
git add game/CameraController.cs
git commit -m "feat(game): CameraController.SetCameraForScreenshot —— 程序化定位相机,供截图回放用"
```

---

## Task 4: `game/SimHost.cs` 的回放模式

**Files:**
- Modify: `game/SimHost.cs`（当前 104 行,改 `_Ready()`，新增私有方法）

**Interfaces:**
- Consumes: `ReplayLogFormat`(Task 2)约定的 JSON schema——**这个任务不引用 `Faketorio.Sim.McpServer` 程序集**，自己用 `System.Text.Json` 按 schema 手写反序列化，两边用同一份 `Faketorio.Sim.Commands.Command`/`CommandType` 类型（都引用了 `Faketorio.Sim` 项目），只是通过 JSON 文本对齐，不是通过共享代码对齐。
- Produces: 回放完成后 `SimHost.Sim` 处于跟 MCP server 端重放前状态字节对齐的 tick；`public bool IsReplayMode { get; private set; }`（新增只读属性，Task 5 用来判断是不是回放场景，从而跳过等真人输入之类的逻辑——目前 Task 5 暂时用不到判断分支，但作为一个明确的"我在回放模式"信号,比翻查命令行参数更清楚，供后续排查问题时用）。

- [ ] **Step 1: 实现**

`game/SimHost.cs` 改成:

```csharp
using System.IO;
using System.Text.Json;
using Godot;
using Faketorio.Presentation.Core;
using Faketorio.Sim;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Game;

public partial class SimHost : Node
{
    [Export] public long Seed = 20260906L;

    public Simulation Sim { get; private set; } = null!;
    public bool IsReplayMode { get; private set; }

    public double Alpha => _acc.Alpha;

    public void Submit(in Command c) => Sim.Submit(c);

    private readonly TickAccumulator _acc = new();

    public override void _Ready()
    {
        string? replayLogPath = FindArgValue("--replay-log");
        if (replayLogPath is not null)
        {
            IsReplayMode = true;
            RunReplay(replayLogPath);
        }
        else
        {
            Sim = new Simulation(PrototypeLoader.LoadFromDirectory(ResolveDataDir()), Seed);
            SubmitStartupScene();
        }
    }

    public override void _Process(double delta)
    {
        // 回放模式下重放是同步一次性做完的(见 RunReplay),不需要实时推进——
        // 实时推进反而会在截图之后继续跑,把已经对齐好的 tick 又往前推,
        // 截出来的图就不是 MCP server 那边看到的那个精确状态了。
        if (IsReplayMode) return;
        for (int n = _acc.Advance(delta); n-- > 0;) Sim.Step();
    }

    // 从 `-- --key value --key2 value2 ...` 形式的用户自定义参数里取一个 key
    // 对应的 value。找不到返回 null。
    private static string? FindArgValue(string key)
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == key) return args[i + 1];
        return null;
    }

    private void RunReplay(string logPath)
    {
        string json = File.ReadAllText(logPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        long seed = root.GetProperty("seed").GetInt64();
        string dataDir = root.GetProperty("dataDir").GetString()!;

        Sim = new Simulation(PrototypeLoader.LoadFromDirectory(ResolveDataDirFor(dataDir)), seed);

        foreach (var entry in root.GetProperty("entries").EnumerateArray())
        {
            bool isStep = entry.GetProperty("isStep").GetBoolean();
            if (isStep)
            {
                int ticks = entry.GetProperty("stepTicks").GetInt32();
                for (int i = 0; i < ticks; i++) Sim.Step();
            }
            else
            {
                var cmd = entry.GetProperty("command");
                Sim.Submit(new Command
                {
                    Type = (CommandType)cmd.GetProperty("type").GetInt32(),
                    ProtoId = cmd.GetProperty("protoId").GetInt32(),
                    X = cmd.GetProperty("x").GetInt32(),
                    Y = cmd.GetProperty("y").GetInt32(),
                    Rotation = (byte)cmd.GetProperty("rotation").GetInt32(),
                    Count = cmd.GetProperty("count").GetInt32(),
                });
            }
        }
    }

    // 回放模式下的 dataDir 是 MCP server 进程那边、相对于**它自己**可执行
    // 文件目录解析出来的路径写进 log 文件的，在这个 Godot 进程里没有意义
    // (两个进程的 AppContext.BaseDirectory 完全不同)——回放场景固定用
    // ResolveDataDir() 这个 Godot 项目自己的既有解析逻辑，log 里的 dataDir
    // 字段目前只是留作调试信息，不直接使用。
    private static string ResolveDataDirFor(string _) => ResolveDataDir();

    private static string ResolveDataDir()
    {
        string sibling = ProjectSettings.GlobalizePath("res://../data/base");
        if (Directory.Exists(sibling)) return sibling;

        string inside = ProjectSettings.GlobalizePath("res://data/base");
        if (Directory.Exists(inside)) return inside;

        GD.PushError($"Prototype data directory not found (tried '{sibling}' and '{inside}')");
        return sibling;
    }

    private void SubmitStartupScene()
    {
        int drill = Sim.Prototypes.Get<MiningDrillPrototype>("electric-mining-drill").Id;
        int furnace = Sim.Prototypes.Get<FurnacePrototype>("stone-furnace").Id;
        int assembler = Sim.Prototypes.Get<AssemblingMachinePrototype>("assembling-machine-1").Id;
        int belt = Sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id;
        int chest = Sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        int pole = Sim.Prototypes.Get<ElectricPolePrototype>("small-electric-pole").Id;
        int gen = Sim.Prototypes.Get<FuelGeneratorPrototype>("burner-generator").Id;
        int inserter = Sim.Prototypes.Get<InserterPrototype>("inserter-basic").Id;

        void Place(int protoId, int x, int y, byte rot = 0)
            => Sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = protoId, X = x, Y = y, Rotation = rot });

        for (int x = 0; x < 5; x++) Place(belt, x, 0, rot: 1);
        Place(drill, 0, -2, rot: 2);
        Place(inserter, 5, 0, rot: 1);
        Place(chest, 6, 0);
        Place(gen, 1, -4);
        Place(gen, 3, -4);
        Place(pole, 2, -2);
        Place(pole, 5, -1);
        Place(furnace, 8, -2);
        Place(assembler, -5, 2);
        Place(drill, -4, -3);

        Sim.Step();

        long coalFuelJ = Sim.Prototypes.Get<ItemPrototype>("coal").FuelValueJ;
        FuelGenerator(1, -4, 50L * coalFuelJ);
        FuelGenerator(3, -4, 50L * coalFuelJ);
    }

    private void FuelGenerator(int x, int y, long joules)
    {
        var id = Sim.World.GetEntityAt(x, y);
        if (id.IsValid) Sim.ElectricGrid.SetFuelBufferJ(id, joules);
    }
}
```

（`ResolveDataDir()` 方法体跟改动前完全一样，只是因为新增的 `ResolveDataDirFor`
需要调用它，顺带确认一下没有手滑改错内容——这一段是**照抄**现有代码，不是
新写的逻辑。`SubmitStartupScene()`/`FuelGenerator()` 同样是原样保留，未改动。）

- [ ] **Step 2: 编译确认**

Run: `dotnet build game/Game.csproj`
Expected: 编译成功。

- [ ] **Step 3: 手动验证"正常路径不受影响"**

这一步没有自动化测试，需要手动跑一次确认回归——正常方式启动游戏（不带
`--replay-log` 参数）:

```bash
"/c/Program Files/Godot_v4.5.1-stable_mono_win64/Godot_v4.5.1-stable_mono_win64_console.exe" --path game --position -3000,-3000
```

（用屏幕外定位是为了不用真的盯着屏幕看，跑几秒后用任务管理器或
`taskkill //IM Godot_v4.5.1-stable_mono_win64_console.exe //F` 手动结束进程——
这一步只是确认它正常跑起来、没有立刻崩溃/报错，不需要长时间盯着。）
Expected: 正常启动，控制台没有异常报错(现有的一些既有提示/警告如果之前就有
不算新问题)。

- [ ] **Step 4: 提交**

```bash
git add game/SimHost.cs
git commit -m "feat(game): SimHost 回放模式 —— 从命令行传入的操作历史重放出对齐的模拟状态"
```

---

## Task 5: `ReplayCapture.cs` —— 等待渲染、摆相机、截图、退出

**Files:**
- Create: `game/ReplayCapture.cs`
- Modify: `game/Main.tscn`（给根节点加一个新的子节点挂这个脚本——只在检测到回放模式时才真正做事，正常游玩场景下这个节点存在但什么都不做，不影响正常玩法）

**Interfaces:**
- Consumes: `SimHost.IsReplayMode`（Task 4）、`CameraController.SetCameraForScreenshot`（Task 3）。

- [ ] **Step 1: 实现**

```csharp
// game/ReplayCapture.cs
using Godot;

namespace Faketorio.Game;

// 只在回放模式(SimHost.IsReplayMode)下做事:等渲染追上重放好的模拟状态、
// 摆相机到指定坐标/缩放、按指定宽高截一张 PNG、退出进程。正常游玩场景下
// (IsReplayMode == false)这个节点的 _Process 第一行就直接返回,不影响任何
// 正常玩法逻辑。
public partial class ReplayCapture : Node
{
    private SimHost _host = null!;
    private CameraController _camera = null!;
    private int _frameCount;
    private bool _cameraPositioned;

    // 相机定位之后再等这么多帧,让 WorldView._Draw() 按新的相机参数重新画一遍。
    // spike 验证过 30 帧的余量足够,这里保留同样的余量。
    private const int FramesAfterCameraMove = 30;

    public override void _Ready()
    {
        _host = GetNode<SimHost>("/root/SimHost");
        _camera = GetParent().GetNode<CameraController>("CameraController");
    }

    public override void _Process(double _delta)
    {
        if (!_host.IsReplayMode) return;

        if (!_cameraPositioned)
        {
            double x = FindArgDouble("--camera-x", 0);
            double y = FindArgDouble("--camera-y", 0);
            double zoom = FindArgDouble("--zoom", 32);
            int width = (int)FindArgDouble("--width", 1280);
            int height = (int)FindArgDouble("--height", 720);

            DisplayServer.WindowSetSize(new Vector2I(width, height));
            _camera.SetCameraForScreenshot(x, y, zoom);
            _cameraPositioned = true;
            _frameCount = 0;
            return;
        }

        _frameCount++;
        if (_frameCount < FramesAfterCameraMove) return;

        string? outPath = FindArgValue("--out");
        if (outPath is null)
        {
            GD.PushError("回放模式缺少 --out 参数,无法保存截图。");
            GetTree().Quit(1);
            return;
        }

        var img = GetViewport().GetTexture().GetImage();
        var err = img.SavePng(outPath);
        if (err != Error.Ok) GD.PushError($"截图保存失败: {err}");
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }

    private static string? FindArgValue(string key)
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == key) return args[i + 1];
        return null;
    }

    private static double FindArgDouble(string key, double fallback)
    {
        string? raw = FindArgValue(key);
        return raw is not null && double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : fallback;
    }
}
```

`game/Main.tscn` 加一个子节点(在现有的四个 `[node ... parent="."]` 后面追加):

```
[node name="ReplayCapture" type="Node" parent="."]
script = ExtResource("5")
```

同时在文件头部的 `[gd_scene load_steps=5 format=3]` 那一行,`load_steps` 改成 `6`,
`ext_resource` 列表追加一行:

```
[ext_resource type="Script" path="res://ReplayCapture.cs" id="5"]
```

（`.tscn` 是纯文本格式，直接用编辑器打开、拖一个新的 `Node` 子节点进去挂脚本，
或者手动编辑文本都行——手动编辑时要注意 `load_steps` 数字和 `id` 编号，
上面给的是照着现有四个 `ext_resource` 的编号顺序网往后接的第五个。）

- [ ] **Step 2: 编译确认**

Run: `dotnet build game/Game.csproj`
Expected: 编译成功。

- [ ] **Step 3: 提交**

```bash
git add game/ReplayCapture.cs game/Main.tscn
git commit -m "feat(game): ReplayCapture —— 回放模式下等渲染追上、摆相机、截图、退出"
```

---

## Task 6: MCP server 的 `get_screenshot` 工具

**Files:**
- Modify: `sim/Faketorio.Sim.McpServer/Dtos.cs`（如果需要新的 DTO——`GetScreenshot` 直接返回 `ImageContentBlock`，目前看不需要新 DTO，除非实施时发现 SDK 用法要求别的形状）
- Modify: `sim/Faketorio.Sim.McpServer/SimTools.cs`

**Interfaces:**
- Consumes: `OperationLog`（Task 1）、`ReplayLogFormat.Serialize`（Task 2）。
- Produces: `public static ImageContentBlock GetScreenshot(double cameraX, double cameraY, double zoomPpt = 32, int width = 1280, int height = 720)`。

- [ ] **Step 1: 实现**

`SimTools.cs` 追加(需要在文件顶部加 `using System.Diagnostics;`):

```csharp
    [McpServerTool, Description("把当前模拟状态渲染成一张 PNG 截图返回。会另起一个 Godot 子进程重放操作历史来渲染,有秒级延迟；需要设置 FAKETORIO_GODOT_EXE 环境变量指向 Godot 可执行文件")]
    public static ModelContextProtocol.Protocol.ImageContentBlock GetScreenshot(
        [Description("相机中心的世界 tile X 坐标")] double cameraX,
        [Description("相机中心的世界 tile Y 坐标")] double cameraY,
        [Description("缩放,每 tile 像素数,建议范围 6~64,默认 32")] double zoomPpt = 32,
        [Description("输出图片宽度(像素),默认 1280")] int width = 1280,
        [Description("输出图片高度(像素),默认 720")] int height = 720)
    {
        if (SimHost.Sim is null) throw new ModelContextProtocol.McpException(NotReadyError.Message);

        string godotExe = Environment.GetEnvironmentVariable("FAKETORIO_GODOT_EXE")
            ?? throw new ModelContextProtocol.McpException(
                "没有设置 FAKETORIO_GODOT_EXE 环境变量——它必须指向 Godot 可执行文件的完整路径。");
        if (!File.Exists(godotExe))
            throw new ModelContextProtocol.McpException($"FAKETORIO_GODOT_EXE 指向的路径不存在: {godotExe}");

        // game/ 和 sim/Faketorio.Sim.McpServer/ 都在仓库根目录下的兄弟目录,
        // 相对关系固定,同 ResetSimulation 解析 dataDir 用的 AppContext.BaseDirectory
        // 手法一致。
        string gameProjectDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "game");
        gameProjectDir = Path.GetFullPath(gameProjectDir);
        if (!Directory.Exists(gameProjectDir))
            throw new ModelContextProtocol.McpException($"找不到 game/ 项目目录(推算出的路径: {gameProjectDir})。");

        string logPath = Path.Combine(Path.GetTempPath(), $"faketorio-mcp-replay-{Guid.NewGuid():N}.json");
        string outPath = Path.Combine(Path.GetTempPath(), $"faketorio-mcp-shot-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllText(logPath, ReplayLogFormat.Serialize(OperationLog.Entries, SimHost.Seed, SimHost.DataDir));

            var psi = new ProcessStartInfo
            {
                FileName = godotExe,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--path");
            psi.ArgumentList.Add(gameProjectDir);
            psi.ArgumentList.Add("--position");
            psi.ArgumentList.Add("-3000,-3000");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("--replay-log");
            psi.ArgumentList.Add(logPath);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(outPath);
            psi.ArgumentList.Add("--camera-x");
            psi.ArgumentList.Add(cameraX.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--camera-y");
            psi.ArgumentList.Add(cameraY.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--zoom");
            psi.ArgumentList.Add(zoomPpt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--width");
            psi.ArgumentList.Add(width.ToString());
            psi.ArgumentList.Add("--height");
            psi.ArgumentList.Add(height.ToString());

            using var proc = Process.Start(psi)
                ?? throw new ModelContextProtocol.McpException("无法启动 Godot 子进程。");
            // 重放+截图理论上应该在几秒内结束(spike 里 60 帧不到 1 秒),30 秒是
            // 留了大量余量的上限,不是精确校准过的值。
            if (!proc.WaitForExit(30_000))
            {
                proc.Kill(entireProcessTree: true);
                throw new ModelContextProtocol.McpException("Godot 截图子进程超时(30 秒),已强制终止。");
            }
            if (proc.ExitCode != 0 || !File.Exists(outPath))
                throw new ModelContextProtocol.McpException($"Godot 截图子进程退出码 {proc.ExitCode},没有产出图片。");

            byte[] png = File.ReadAllBytes(outPath);
            return ModelContextProtocol.Protocol.ImageContentBlock.FromBytes(png, "image/png");
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
```

**`gameProjectDir` 的相对路径层数(五个 `".."`)需要在实施时用实际的
`AppContext.BaseDirectory` 值核对**——这是 `bin/Debug/net8.0/`（或 `Release`）
这一层相对仓库根目录的深度,`sim/Faketorio.Sim.McpServer/bin/Debug/net8.0/` 到
仓库根是四层 `..`(`net8.0` -> `Debug` -> `bin` -> `Faketorio.Sim.McpServer`),
仓库根到 `game/` 再进一层,写代码时按实际编译产物目录结构验证这个数字对不对,
不要照抄这里写的层数就当作没有风险——**如果编译输出目录结构因为
`RuntimeIdentifier`/自包含发布之类的设置多套了一层,这个路径就会算错**,
实施阶段第一次跑 `get_screenshot` 时用 `Console.WriteLine`/日志确认这个路径
真的落在 `game/` 目录上,再删掉调试输出。

如果 `ModelContextProtocol.Protocol.ImageContentBlock`/`ImageContentBlock.FromBytes`
这个具体命名空间跟实际装的 SDK 版本对不上(参照子项目一 Task1 的经验,SDK
API 有可能已经变化),按实际编译报错调整,不要死磕这份 plan 写的确切路径。

- [ ] **Step 2: 手动验证(不是自动化测试,需要本机装好 Godot 并设置环境变量)**

```bash
export FAKETORIO_GODOT_EXE="/c/Program Files/Godot_v4.5.1-stable_mono_win64/Godot_v4.5.1-stable_mono_win64_console.exe"
```

写一个临时的手动验证脚本或者直接在 `dotnet run --project sim/Faketorio.Sim.McpServer`
起的进程上用 MCP inspector/一个简单的 stdio 客户端调用序列:
`reset_simulation` → `submit_command`(放几个实体)→ `step` → `get_screenshot`。
确认拿到的 `ImageContentBlock.Data` 解码成 PNG 之后,画面上能看到刚放置的
实体(不是空场景、不是报错)。这一步没有 xUnit 断言,是人工确认——同子项目一
Task 1 独立复核 stdio 流量用的方法(临时脚本发 JSON-RPC,读 stdout)。

- [ ] **Step 3: 跑全量既有测试确认没有破坏别的东西**

Run: `dotnet test -c Release Faketorio.sln`
Expected: 全部通过(这个任务本身没有新增 xUnit 测试,只是确认没有编译错误/
没有破坏既有工具)。

- [ ] **Step 4: 提交**

```bash
git add sim/Faketorio.Sim.McpServer/SimTools.cs
git commit -m "feat(mcp): get_screenshot 工具 —— 拉起 Godot 子进程重放+截图"
```

---

## Task 7: README 更新 + roadmap 收尾

**Files:**
- Modify: `sim/Faketorio.Sim.McpServer/README.md`
- Modify: `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`

- [ ] **Step 1: 更新 README**

在工具列表里加一条:

```markdown
- `get_screenshot(cameraX, cameraY, zoomPpt?, width?, height?)` —— 把当前模拟状态渲染成一张 PNG 截图返回。需要设置 `FAKETORIO_GODOT_EXE` 环境变量指向 Godot 可执行文件；每次调用会另起一个 Godot 子进程，有秒级延迟。
```

在文件末尾加一节说明环境变量要求:

```markdown
## 截图能力的额外要求

`get_screenshot` 工具需要:
1. 本机装有 Godot 4.5.1(Mono 版,支持 C#)。
2. 环境变量 `FAKETORIO_GODOT_EXE` 指向 Godot 可执行文件的完整路径。
3. `game/` 项目(截图用的渲染场景)跟这个 MCP server 项目在同一个仓库里,
   相对路径关系固定——不要把这个 MCP server 单独拷贝部署到别的地方运行。
```

- [ ] **Step 2: 更新 roadmap**

在 `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md` 里记录这个
子项目二完成、commit 引用、以及"这次是纯技术验证走通,没有走完整的 SDD
review 流程(Godot 端代码没有自动化测试基础设施,靠手动验证)"这个跟前面
几轮不一样的地方。

- [ ] **Step 3: 提交**

```bash
git add sim/Faketorio.Sim.McpServer/README.md docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md
git commit -m "docs(mcp): 截图能力 README + roadmap 收尾"
```

---

## Self-Review Notes

- **Spec 覆盖**:§1(两个世界对齐)→ Task 1/2/4;§2(命名)→ 每个任务的文件
  路径都精确标注是哪个 `SimHost`;§3(MCP 端操作历史+工具)→ Task 1/2/6;
  §4(CameraController)→ Task 3;§5(SimHost 回放分支)→ Task 4/5;§6(配置)
  → Task 6 的 `FAKETORIO_GODOT_EXE` 处理;§7(范围外)→ Global Constraints
  没有加任何相机自动取景/debug 覆盖层/常驻进程的代码;§8(测试计划)→
  Task 1/2 有 xUnit,Task 3/4/5 是手动验证(Godot 端代码没有自动化测试
  基础设施,如实反映,不假装有)。
- **占位符扫描**:无 "TBD"/"后续实现" 这类模式——`gameProjectDir` 的路径层数
  和 SDK 命名空间两处明确标注"实施时要核对/可能要调整",不是回避写实现,
  是诚实标注两个本计划写作时没有在这台机器上跑通整条链路验证过的具体数字/
  API 名字(跟子项目一 Task1 处理 SDK 版本不确定性的方式一致)。
- **类型一致性**:`LogEntry`/`OperationLog`/`ReplayLogFormat.Serialize`/
  `SimHost.Seed`/`SimHost.DataDir`(MCP server 端)在 Task 1/2 定义后,
  Task 6 按同样签名引用;`CameraController.SetCameraForScreenshot`/
  `SimHost.IsReplayMode`(Godot 端)在 Task 3/4 定义后,Task 5 按同样签名
  引用,两组类型分属两个不同的程序集,没有互相引用,只通过 Task 6/Task 4
  的 JSON 文本(Task 2 定义的 schema)对齐——这条边界在写这些任务时反复
  确认过没有不小心把两边的类型弄混。
