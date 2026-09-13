namespace Faketorio.Sim.McpServer;

public record ResetResult(long Tick, int PrototypeCount);

public record TickInfo(long Tick, int RejectedCommandCount);

public record StepResult(long Tick, int RejectedCommandCount, int RejectedThisCall);

public record SubmitResult(bool Queued);

public record EntityInfo(int Index, int Generation, string ProtoName, int ProtoId, int X, int Y, byte Rotation);

public record SlotInfo(int Slot, string ItemName, int ItemProtoId, int Count);

public record InventoryInfo(int SlotCount, List<SlotInfo> Slots);

public record PrototypeInfo(int Id, string Name, string TypeName);

public record HashResult(string Hash);
