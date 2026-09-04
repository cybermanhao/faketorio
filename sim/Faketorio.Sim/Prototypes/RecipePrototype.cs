namespace Faketorio.Sim.Prototypes;

public readonly struct ItemAmount
{
    public required string Name { get; init; }
    public required int Amount { get; init; }
}

public readonly record struct ResolvedAmount(int ItemProtoId, int Amount);

public sealed class RecipePrototype : PrototypeBase
{
    public string Category { get; init; } = "crafting";
    public int EnergyRequiredTicks { get; init; } = 30;
    public required List<ItemAmount> Ingredients { get; init; }
    public required List<ItemAmount> Results { get; init; }
    public bool Enabled { get; init; } = true;

    // PrototypeLoader 的解析 pass 后填好(Name -> ItemPrototype.Id)。构造后为空。
    public IReadOnlyList<ResolvedAmount> ResolvedIngredients { get; internal set; } = Array.Empty<ResolvedAmount>();
    public IReadOnlyList<ResolvedAmount> ResolvedResults     { get; internal set; } = Array.Empty<ResolvedAmount>();
}
