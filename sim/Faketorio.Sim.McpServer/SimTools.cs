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

    // 供测试直接验证提交命令后的模拟状态，不是 MCP 工具。
    internal static Simulation? Sim => SimHost.Sim;

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
}
