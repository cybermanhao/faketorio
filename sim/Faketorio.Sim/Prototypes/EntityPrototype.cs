namespace Faketorio.Sim.Prototypes;

public abstract class EntityPrototype : PrototypeBase
{
    public int TileWidth { get; init; } = 1;
    public int TileHeight { get; init; } = 1;
    public string? MinableResult { get; init; }
    public int MiningTimeTicks { get; init; }
}

public sealed class ContainerPrototype : EntityPrototype
{
    public int InventorySize { get; init; }
}
