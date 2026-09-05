using Faketorio.Sim;

namespace Faketorio.Sim.Prototypes;

public sealed class InserterPrototype : EntityPrototype
{
    // 基础转速:每 tick 转过的"半程比例"(Q16.16)。Q16.One = 1 tick 摆完半程;
    // Q16.FromRatio(1, 30) = 30 tick 摆完半程。运行时再乘 satisfaction(将来乘科技加成)。
    public Q16 RotationSpeed { get; init; } = Q16.One;
    public long EnergyUsageJPerTick { get; init; }
}
