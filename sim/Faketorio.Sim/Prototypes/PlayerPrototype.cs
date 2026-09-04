namespace Faketorio.Sim.Prototypes;

// 单例,约定 name = "player"。PrototypeLoader 校验字段;Simulation 构造期
// 要求 registry 恰好有一条(无 -> InvalidOperationException)。
public sealed class PlayerPrototype : PrototypeBase
{
    public int InventorySize            { get; init; } = 60;
    public int ReachSubTiles            { get; init; } = 1536;  // 6 tile × 256
    public int WalkSpeedSubTilesPerTick { get; init; } = 38;    // ≈0.15 tile/tick
    public int CraftQueueCap            { get; init; } = 32;
    public IReadOnlyList<ItemAmount> StartingInventory { get; init; } = Array.Empty<ItemAmount>();
}
