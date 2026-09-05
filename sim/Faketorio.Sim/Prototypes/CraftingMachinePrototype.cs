using Faketorio.Sim;

namespace Faketorio.Sim.Prototypes;

// 熔炉/装配机的共用字段。行为分支由具体类型决定(is FurnacePrototype /
// is AssemblingMachinePrototype),不用 bool 标志——同 P7 ElectricPolePrototype/
// FuelGeneratorPrototype 的先例。
public abstract class CraftingMachinePrototype : EntityPrototype
{
    public required string Category { get; init; }      // 必须匹配某些 RecipePrototype.Category
    public int InputSlots { get; init; }
    public int OutputSlots { get; init; }
    public Q16 CraftingSpeed { get; init; } = Q16.One;   // 进度倍率,M1 数据恒 1.0(JSON 不解析这个字段)
    public long EnergyUsageJPerTick { get; init; }        // 每 tick 向电网登记的 PrimaryInput 需求
}

public sealed class FurnacePrototype : CraftingMachinePrototype { }           // 自动匹配配方
public sealed class AssemblingMachinePrototype : CraftingMachinePrototype { } // 需要 SetRecipe
