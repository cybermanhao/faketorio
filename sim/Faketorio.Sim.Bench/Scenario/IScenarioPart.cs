namespace Faketorio.Sim.Bench.Scenario;

// 一个场景片段自报的数据。全部是"意图/静态额定值",不是运行时观测——
// Place 在实体真正存在之前就返回它(实体要到排掉 PlaceEntity 命令的那次
// Step 之后才存在),所以这里不可能有任何"跑起来之后"的量。
public readonly record struct ScenarioPartFacts(
    int EntitiesPlaced,
    IReadOnlyDictionary<string, int> EntityCountByType,
    long RatedPowerSupplyJPerTick,
    long RatedPowerDemandJPerTick,
    long MinableOreUnderDrills,
    long SeededIronOre,
    int FedDrillCount);

public interface IScenarioPart
{
    /// 用 sim.Submit 放实体;不 Step、不写库存。返回自报数据。
    ScenarioPartFacts Place(Simulation sim, int anchorX, int anchorY);
}
