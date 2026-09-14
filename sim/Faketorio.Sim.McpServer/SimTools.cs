using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
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
        SimHost.Seed = seed;
        SimHost.DataDir = dataDir;
        OperationLog.Clear();
        return new ResetResult(SimHost.Sim.Tick, prototypes.Count);
    }

    [McpServerTool, Description("查询当前 tick 和已拒绝命令总数，不推进模拟")]
    public static TickInfo GetTick()
    {
        if (SimHost.Sim is null) throw new ModelContextProtocol.McpException(NotReadyError.Message);
        return new TickInfo(SimHost.Sim.Tick, SimHost.Sim.RejectedCommandCount);
    }

    // 供测试直接验证提交命令后的模拟状态，不是 MCP 工具。
    internal static Simulation? Sim => SimHost.Sim;

    [McpServerTool, Description("推进模拟若干 tick")]
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
        if (SimHost.Sim is null) throw new ModelContextProtocol.McpException(NotReadyError.Message);

        if (!Enum.TryParse<Faketorio.Sim.Commands.CommandType>(type, ignoreCase: true, out var commandType))
        {
            string validValues = string.Join("/", Enum.GetNames<Faketorio.Sim.Commands.CommandType>());
            throw new ModelContextProtocol.McpException($"未知的命令类型 '{type}'，合法值：{validValues}");
        }

        if (protoId.HasValue == (protoName is not null))
            throw new ModelContextProtocol.McpException("protoId 和 protoName 必须二选一（不能都给，也不能都不给）。");

        int resolvedProtoId;
        if (protoId.HasValue)
        {
            resolvedProtoId = protoId.Value;
        }
        else
        {
            // "wooden-chest" 这样的名字可能同时被 ItemPrototype/ContainerPrototype/
            // RecipePrototype 等好几个不同类型的原型注册（同名不冲突，因为它们的 id
            // 空间不是分类型隔开的）。这里如果只是"选第一个匹配的 id"，命令实际用的
            // ProtoId 会取决于原型加载顺序这种调用方完全看不到的细节，静默地对/错——
            // 宁可直接报错让调用方改用 resolve_prototype 消歧义，也不要猜。
            var matches = ResolvePrototype(protoName!);
            if (matches.Count > 1)
            {
                string typeList = string.Join("、", matches.Select(m => m.TypeName));
                throw new ModelContextProtocol.McpException(
                    $"名字 '{protoName}' 在 {matches.Count} 个不同类型的原型间有歧义（{typeList}）。" +
                    "改用 resolve_prototype 工具查出具体想要哪个的 protoId，再用 protoId 参数调用。");
            }
            if (matches.Count == 0)
                throw new ModelContextProtocol.McpException($"找不到名字是 '{protoName}' 的原型。");
            resolvedProtoId = matches[0].Id;
        }

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
    }

    [McpServerTool, Description("查询指定坐标的实体，没有实体返回 null")]
    public static EntityInfo? GetEntityAt(
        [Description("查询坐标 X")] int x,
        [Description("查询坐标 Y")] int y)
    {
        if (SimHost.Sim is null) throw new ModelContextProtocol.McpException(NotReadyError.Message);

        var id = SimHost.Sim.World.GetEntityAt(x, y);
        if (!id.IsValid) return null;

        ref var data = ref SimHost.Sim.Entities.Get(id);
        string protoName = SimHost.Sim.Prototypes.GetById(data.ProtoId).Name;
        return new EntityInfo(id.Index, id.Generation, protoName, data.ProtoId, data.X, data.Y, data.Rotation);
    }

    [McpServerTool, Description("查询指定坐标实体的库存内容（role 默认 0，机器/发电机等多库存实体可能需要传 1 或 2）")]
    public static InventoryInfo? GetInventory(
        [Description("查询坐标 X")] int x,
        [Description("查询坐标 Y")] int y,
        [Description("库存角色编号，默认 0（主库存）；机器/发电机等多库存实体的输入/输出库存可能是 1 或 2")] int role = 0)
    {
        if (SimHost.Sim is null) throw new ModelContextProtocol.McpException(NotReadyError.Message);

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

    [McpServerTool, Description("按名字查原型 id；同名可能匹配多个不同类型的原型（item 和 entity 允许同名），此时都返回，自己按 TypeName 挑")]
    public static List<PrototypeInfo> ResolvePrototype(
        [Description("要查询的原型名字")] string name,
        [Description("可选，按类型过滤；取值是具体的 C# 原型类名（例如 \"ItemPrototype\"、\"FurnacePrototype\"），不是原型数据里的某个字段，不传则返回同名的所有类型")] string? typeName = null)
    {
        if (SimHost.Sim is null) throw new ModelContextProtocol.McpException(NotReadyError.Message);
        return CollectPrototypes(p => p.Name == name && (typeName is null || p.GetType().Name == typeName));
    }

    [McpServerTool, Description("列出全部已加载的原型（名字+类型），用于发现能用什么")]
    public static List<PrototypeInfo> ListPrototypes(
        [Description("可选，按类型过滤；取值是具体的 C# 原型类名（例如 \"ItemPrototype\"、\"FurnacePrototype\"），不传则列出全部类型")] string? typeNameFilter = null)
    {
        if (SimHost.Sim is null) throw new ModelContextProtocol.McpException(NotReadyError.Message);
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
        if (SimHost.Sim is null) throw new ModelContextProtocol.McpException(NotReadyError.Message);
        // 十六进制字符串格式，跟仓库里已有的约定一致（bench/golden.json 等），
        // 避免原始 ulong 数字在 JSON-RPC 上被 JS 端 JSON.parse 当成 double 精度损失
        // （> 2^53 就不能保证按位相等），而这个工具存在的意义就是精确比较。
        return new HashResult($"0x{SimHost.Sim.ComputeStateHash():X16}");
    }

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
                // 必须重定向子进程的标准输出/错误——不重定向的话,.NET 在 Windows 上
                // 默认让子进程继承父进程(这个 MCP server 自己)的控制台句柄,Godot
                // 自己的 stdout 输出会直接混进这个进程用来传 JSON-RPC 协议帧的 stdio
                // 通道,破坏协议帧(跟 Program.cs 里关控制台日志 provider 是同一类
                // 问题)。这里全部重定向到内存缓冲区丢弃/仅用于报错诊断,不再流向
                // 父进程自己的 stdout。
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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
            // 必须在 WaitForExit 之前开始异步读取,否则子进程输出把重定向管道的
            // 缓冲区写满后会阻塞在那里,while 这边又在同步等它退出——经典死锁。
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            // 重放+截图理论上应该在几秒内结束(spike 里 60 帧不到 1 秒),30 秒是
            // 留了大量余量的上限,不是精确校准过的值。
            if (!proc.WaitForExit(30_000))
            {
                proc.Kill(entireProcessTree: true);
                throw new ModelContextProtocol.McpException("Godot 截图子进程超时(30 秒),已强制终止。");
            }
            if (proc.ExitCode != 0 || !File.Exists(outPath))
            {
                string stderrText = stderrTask.GetAwaiter().GetResult();
                string stdoutText = stdoutTask.GetAwaiter().GetResult();
                throw new ModelContextProtocol.McpException(
                    $"Godot 截图子进程退出码 {proc.ExitCode},没有产出图片。stderr: {Truncate(stderrText)} stdout: {Truncate(stdoutText)}");
            }

            byte[] png = File.ReadAllBytes(outPath);
            return ModelContextProtocol.Protocol.ImageContentBlock.FromBytes(png, "image/png");
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    private static string Truncate(string s, int max = 2000) => s.Length <= max ? s : s[..max] + "...(truncated)";
}
