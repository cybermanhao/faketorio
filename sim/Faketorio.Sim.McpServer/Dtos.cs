namespace Faketorio.Sim.McpServer;

public record ResetResult(long Tick, int PrototypeCount);

public record TickInfo(long Tick, int RejectedCommandCount);

public record StepResult(long Tick, int RejectedCommandCount, int RejectedThisCall);

public record SubmitResult(bool Queued);
