namespace Faketorio.Sim.McpServer;

public record ResetResult(long Tick, int PrototypeCount);

public record TickInfo(long Tick, int RejectedCommandCount);
