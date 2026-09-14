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
