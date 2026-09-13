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
