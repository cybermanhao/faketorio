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
