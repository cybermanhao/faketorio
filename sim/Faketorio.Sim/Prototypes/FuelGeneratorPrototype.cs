namespace Faketorio.Sim.Prototypes;

public sealed class FuelGeneratorPrototype : EntityPrototype
{
    public long PowerOutputJPerTick { get; init; }
    public required string FuelItemName { get; init; }
    public int FuelItemProtoId { get; internal set; }   // 解析 pass 填,同配方名字解析模式
}
