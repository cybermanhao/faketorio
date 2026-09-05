using Faketorio.Sim;

namespace Faketorio.Sim.Prototypes;

public sealed class MiningDrillPrototype : EntityPrototype
{
    public Q16 MiningSpeed { get; init; } = Q16.One;   // 进度倍率,M1 数据恒 1.0,JSON 不解析
    public long EnergyUsageJPerTick { get; init; }      // 每 tick 向电网登记的 PrimaryInput 需求
}
