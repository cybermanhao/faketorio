namespace Faketorio.Sim.Prototypes;

public readonly struct ItemAmount
{
    public required string Name { get; init; }
    public required int Amount { get; init; }
}

public sealed class RecipePrototype : PrototypeBase
{
    public string Category { get; init; } = "crafting";
    public int EnergyRequiredTicks { get; init; } = 30;
    public required List<ItemAmount> Ingredients { get; init; }
    public required List<ItemAmount> Results { get; init; }
    public bool Enabled { get; init; } = true;
}
