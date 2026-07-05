namespace Faketorio.Sim.Prototypes;

public sealed class ItemPrototype : PrototypeBase
{
    public int StackSize { get; init; } = 50;
    public string? PlaceResult { get; init; }
    public long FuelValueJ { get; init; }
    public string? FuelCategory { get; init; }
}
