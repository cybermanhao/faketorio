# MCP 原子操作模拟驱动器 · 截图能力设计(子项目二)

## 0. 背景

子项目一(`docs/superpowers/specs/2026-09-13-mcp-sim-driver-design.md`,已合并)
给了 9 个操作/读取状态的 MCP 工具。这一轮加第 10 个:`get_screenshot`,把
"MCP 工具驱动出来的那个模拟状态"渲染成一张 PNG 传回来。

**spike 结论(已验证,不是假设)**:在这台 Windows 机器上,Godot 4.5.1(Mono)
不需要 `--headless`(那个会禁用渲染管线)、不需要真实显示器、窗口**最小化**
时渲染循环仍正常跑(不会暂停),`viewport.get_texture().get_image().save_png()`
截出来的是真实渲染内容,不是空白/纯色。两轮独立验证都一次成功,细节见
brainstorm 记录(commit 历史里没有专门的 spike 报告文件,过程是纯手动验证,
结论直接体现在这份 spec 里)。

## 1. 核心问题:两个世界要对齐

Godot 项目(`game/`)现在有自己的 `SimHost`(autoload 单例,`game/SimHost.cs`),
`_Ready()` 里硬编码调用 `SubmitStartupScene()` 摆一套固定的演示场景,然后靠
`_Process(delta)` 实时推进——这是**给人玩的**那个世界,跟 MCP server 进程里
`Faketorio.Sim.McpServer` 的 `SimHost`(命名相同,类型和用途完全不同,见下方
"命名"一节)持有的模拟是两个完全独立的 `Simulation` 实例,互不知道对方存在。

`get_screenshot` 要截的是 MCP 那边驱动出来的状态,不是 Godot 项目现在写死的
demo 场景。做法:MCP server 记录一份**操作历史**(它自己发出过的全部
`Command` + 每次 `step` 推进了多少 tick),截图时把这份历史连同相机参数一起
交给一个新拉起的 Godot 进程,那个进程用同一份历史重放出**字节对齐**的模拟
状态(sim 层本来就是确定性的,这是复用现有保证,不是新造轮子),摆好相机,
截图,退出。

## 2. 命名:两个 `SimHost` 不要互相污染

- `sim/Faketorio.Sim.McpServer/SimHost.cs` 里的 `internal static class SimHost`
  ——**不改**,只是加一个新的静态字段/类来记操作历史(见 §3),不往这个类里
  塞跟 Godot 有关的东西。
- `game/SimHost.cs` 里的 `public partial class SimHost : Node`(Godot autoload)
  ——加一个"回放模式"分支(见 §5),依然叫 `SimHost`,这是 Godot 项目自己的
  既有类,不重命名(重命名会动到 `CameraController.cs:43` 等好几处引用,
  超出这次改动的范围)。
写 plan 时每处提到"MCP server 的 SimHost"还是"Godot 项目的 SimHost"要点明
是哪一个,避免实施时的人搞混。

## 3. MCP server 端:操作历史 + `get_screenshot` 工具

新增 `sim/Faketorio.Sim.McpServer/OperationLog.cs`:

```csharp
namespace Faketorio.Sim.McpServer;

// 一条历史记录——要么是"提交了这条命令",要么是"推进了 N 个 tick"。
// 用来在 Godot 那边重放出跟 MCP server 这边字节对齐的状态。
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

`SimTools.ResetSimulation` 里加一行 `OperationLog.Clear();`(新场景开始,历史
清空)。`SimTools.SubmitCommand` 成功 `Submit` 之后加 `OperationLog.RecordCommand(...)`
(提交的那个 `Command` 结构体本身)。`SimTools.Step` 里加 `OperationLog.RecordStep(ticks)`。

`get_screenshot` 工具:

```csharp
[McpServerTool, Description("把当前模拟状态渲染成一张 PNG 截图返回。会另起一个 Godot 子进程重放操作历史来渲染,有秒级延迟")]
public static ImageContentBlock GetScreenshot(
    [Description("相机中心的世界 tile X 坐标")] double cameraX,
    [Description("相机中心的世界 tile Y 坐标")] double cameraY,
    [Description("缩放,每 tile 像素数,建议范围 6~64,默认 32")] double zoomPpt = 32,
    [Description("输出图片宽度(像素),默认 1280")] int width = 1280,
    [Description("输出图片高度(像素),默认 720")] int height = 720)
{
    if (SimHost.Sim is null) throw new ModelContextProtocol.McpException(NotReadyError.Message);

    string logPath = Path.Combine(Path.GetTempPath(), $"faketorio-mcp-replay-{Guid.NewGuid():N}.json");
    string outPath = Path.Combine(Path.GetTempPath(), $"faketorio-mcp-shot-{Guid.NewGuid():N}.png");
    try
    {
        File.WriteAllText(logPath, SerializeLog(OperationLog.Entries, SimHost.Sim));

        string godotExe = ResolveGodotExecutable();   // 见 §6,配置来源
        string gameProjectDir = ResolveGameProjectDir();

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = godotExe,
            ArgumentList =
            {
                "--path", gameProjectDir,
                "-s", "ReplayCapture.gd",
                "--", "--replay-log", logPath, "--out", outPath,
                "--camera-x", cameraX.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--camera-y", cameraY.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--zoom", zoomPpt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--width", width.ToString(), "--height", height.ToString(),
            },
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new ModelContextProtocol.McpException("无法启动 Godot 子进程。");
        // 超时防止孤儿进程——重放理论上应该在几秒内结束(spike 里 60 帧不到 1 秒),
        // 30 秒是留了大量余量的上限,不是精确校准过的值。
        if (!proc.WaitForExit(30_000))
        {
            proc.Kill(entireProcessTree: true);
            throw new ModelContextProtocol.McpException("Godot 截图子进程超时(30 秒),已强制终止。");
        }
        if (proc.ExitCode != 0 || !File.Exists(outPath))
            throw new ModelContextProtocol.McpException($"Godot 截图子进程退出码 {proc.ExitCode},没有产出图片。");

        byte[] png = File.ReadAllBytes(outPath);
        return ImageContentBlock.FromBytes(png, "image/png");
    }
    finally
    {
        // 临时文件用完即删,不留垃圾——即使中途抛异常也要清。
        if (File.Exists(logPath)) File.Delete(logPath);
        if (File.Exists(outPath)) File.Delete(outPath);
    }
}
```

`SerializeLog` 用 `System.Text.Json` 把 `OperationLog.Entries` 序列化成一个
JSON 数组,每条要么是 `{"step": N}` 要么是
`{"command": {"type": "PlaceEntity", "protoId": 3, "x": 0, "y": 2, "rotation": 0, "count": 0}}`
——精确 JSON schema 留给实施阶段定(用 `System.Text.Json` 默认的
`JsonSerializer.Serialize`/自定义 `JsonConverter` 都行,不钉死具体写法,
只钉死"Godot 那边的 GDScript/C# 反过来能读懂同一份 JSON"这个约束)。另外
`SerializeLog` 还需要知道 `SimHost.Sim` 的**种子**(`ResetSimulation` 时传的
`seed`)和 `dataDir`,一起写进 log 文件开头(重放端要用同样的种子/数据目录
重新 `new Simulation(...)`)——`SimHost.Sim` 现在没有暴露 `Seed`,需要在 MCP
server 的 `SimHost.cs` 里加一个 `public static long Seed;`,在 `ResetSimulation`
里一起存,同 `Sim` 字段本身的生命周期。

`ModelContextProtocol.McpException` 的具体命名空间/构造函数签名以子项目一
落地时实际确认过的为准(`sim/Faketorio.Sim.McpServer/SimTools.cs` 现在已经
在用了,照抄那边的写法)。

## 4. Godot 端:`CameraController` 加程序化定位

`game/CameraController.cs` 加一个公开方法(供回放脚本调用):

```csharp
// 供截图回放脚本调用:直接把相机摆到指定世界坐标 + 缩放,不走鼠标/键盘输入。
// 进 Free 模式(不受 Follow 的近景窄缩放区间限制),ppt 仍然按 Free 的
// [FreeMinPpt, FreeMaxPpt] 夹一下,避免传入离谱的值渲染出全黑/全是网格线的图。
public void SetCameraForScreenshot(double worldX, double worldY, double ppt)
{
    Mode = CameraMode.Free;
    _panning = false;
    _snapping = false;
    _centerTile = new Vector2((float)worldX, (float)worldY);
    _ppt = System.Math.Clamp(ppt, FreeMinPpt, FreeMaxPpt);
}
```

## 5. Godot 端:`SimHost.cs` 的回放分支

`game/SimHost.cs` 的 `_Ready()` 改成:检测 `OS.GetCmdlineUserArgs()`(`--` 之后
的参数)里有没有 `--replay-log`。有:进回放模式(读 log 文件、按同样的 seed/
dataDir 建 `Simulation`、按顺序重放 `Submit`/`Step`、不跑 `SubmitStartupScene()`、
不靠 `_Process(delta)` 实时推进——重放是"一次性瞬间做完",不是"实时模拟几秒
钟");没有:走现在的老路径(演示场景 + 实时推进),**正常双击/`dotnet run`
启动这个游戏时行为完全不变**。

重放完成后的截图/退出逻辑,这份 spec 倾向于放在一个独立的新文件
`game/ReplayCapture.cs`(附着在 Main 场景上,或者作为一个单独的
`-s ReplayCapture.gd` SceneTree 脚本,两种都能工作,**实施阶段按哪种更顺手
定**,不是这份 spec 要钉死的技术选型分歧点)——核心逻辑:

1. 解析命令行参数(`--replay-log`/`--out`/`--camera-x`/`--camera-y`/`--zoom`/
   `--width`/`--height`)。
2. 等待 `SimHost` 完成重放(可以是一个简单的"重放完成"信号/事件,或者直接
   数帧——spike 验证过 30 帧的余量绰绰有余,给重放留够帧数即可,不需要精确
   到"刚好重放完那一帧就截")。
3. 调 `CameraController.SetCameraForScreenshot(x, y, ppt)`。
4. 按 `--width`/`--height` 设置窗口大小(`DisplayServer.WindowSetSize` 或
   `project.godot` 里 `--resolution` 参数,实施阶段确认哪个真的能在这个场景
   下生效)。
5. 再等几帧(相机挪动后 `WorldView._Draw()` 要重新画一遍)。
6. `get_viewport().get_texture().get_image().save_png(<--out 参数给的路径>)`。
7. `GetTree().Quit()`。
8. 窗口用 spike 验证过的**最小化**方式藏起来(`root.mode = Window.MODE_MINIMIZED`,
   或者启动参数直接给一个屏幕外 `--position`,两种都验证过能行,选一种,
   spec 不强制)。

## 6. 配置:Godot 可执行文件路径 / 游戏项目目录

`get_screenshot` 需要知道去哪找 `Godot_v4.5.1-stable_mono_win64.exe`。**不要
硬编码这台机器的路径**(`C:\Program Files\Godot_v4.5.1-stable_mono_win64\...`
只在这台开发机上成立)。用环境变量 `FAKETORIO_GODOT_EXE`,没设置就报一个
清楚的错误("设置 FAKETORIO_GODOT_EXE 环境变量指向 Godot 可执行文件"),不要
偷偷猜测/搜索 `PATH`(猜错了会是一个很难查的静默失败)。`game/` 项目目录
相对于 MCP server 可执行文件的位置解析,同子项目一 `dataDir` 参数用的
`AppContext.BaseDirectory` 手法(`game/` 和 `data/` 都在仓库根,两者的相对
路径关系是固定的)。

## 7. 明确不做(这轮范围外)

- 常驻 Godot 进程/多次截图复用同一个进程——每次都新起,brainstorm 已确认。
- 相机自动取景(比如"围着最近的实体自动构图")——`cameraX`/`cameraY`/`zoomPpt`
  全部由调用方(Claude Code)自己算,工具只负责"给定坐标就截图"。
- 截图里叠加 debug 覆盖层(`WorldView` 现有的 `_debugOn` 那套传送带/lane
  可视化)——这次只截正常渲染画面,要不要加 debug 层开关留给后续单独决定。
- 真人在玩游戏时的实时截图(比如"截一下我现在正在玩的这局")——这个工具截
  的永远是 MCP server 自己那份独立操作历史重放出来的状态,不是某个正在跑的
  人类游戏会话;如果以后要支持"截当前正在玩的这局",是一个不同的设计
  (需要跟一个真实运行中的 Godot 实例通信,而不是每次重放一个新进程),
  不在这次范围内。

## 8. 测试计划

- `sim/Faketorio.Sim.McpServer.Tests` 里加 `OperationLog` 的单元测试:
  `RecordCommand`/`RecordStep` 正确追加、`Clear` 正确清空、`ResetSimulation`
  调用后历史确实被清空。
- `get_screenshot` 工具本身**不**在这批单元测试里跑真实的 Godot 子进程(太慢、
  依赖这台机器装了 Godot 且设了 `FAKETORIO_GODOT_EXE`,不适合放进
  `dotnet test -c Release Faketorio.sln` 这个每次都跑的套件)——用一个显式
  跳过的集成测试(`[Fact(Skip = "需要本机装 Godot 且设置 FAKETORIO_GODOT_EXE,手动跑")]`
  之类的标记,或者放进一个独立的、CI 不默认跑的测试类别),内容是:
  `reset_simulation` → 放几个实体 → `step` → `get_screenshot` → 断言拿到的
  `ImageContentBlock.Data` 解码后是一张非空、尺寸符合 `width`/`height` 的 PNG。
  这条测试写出来是给开发者手动验证用的,不是 CI 门禁的一部分。
- `CameraController.SetCameraForScreenshot` 因为是 Godot 端代码(依赖 Godot
  引擎运行时),不在 `Faketorio.Sim.McpServer.Tests`/`Faketorio.Sim.Tests`
  这类纯 .NET xUnit 项目的覆盖范围内——这个项目现在也没有 Godot 场景级别的
  自动化测试基础设施(`Faketorio.Presentation.Core.Tests` 测的是presentation
  层的纯逻辑类,不含实际 Godot 场景),这次也不新建——正确性靠手动跑一次
  `get_screenshot` 看截图对不对来验证。
