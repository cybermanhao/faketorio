using Faketorio.Sim.Bench.Scenario;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Sim.Tests;

public class ScenarioPartTests
{
    private static Simulation NewSim()
        => new(PrototypeLoader.LoadFromDirectory("data/base"), ScenarioBuilderSeedForTests());

    // Task 5 会把真正选定的种子写进 ScenarioBuilder.WorldSeed;在那之前测试用同一个候选值。
    private static long ScenarioBuilderSeedForTests() => 20260905L;

    private static Command Place(int protoId, int x, int y, byte rot = 0) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = protoId,
        X = x, Y = y, Rotation = rot,
    };

    [Fact]
    public void IronUnit_ProducesPlates()
    {
        var sim = NewSim();
        var ids = new ScenarioProtoIds(sim.Prototypes);

        // 供电:发电机灌满燃料缓冲 + 一根电线杆,把单元的西侧电线杆接进网络。
        // 单元放在 (0,0);电力设施放在负坐标一侧避免撞单元。
        // 电线杆 (-2,4) 的供电区(Chebyshev 2)必须覆盖发电机的锚点格 (-4,4),
        // 否则 RegisterSupply 找不到网络、整个电网 0 供给(见 ElectricGrid.FindNetworkAt)。
        sim.Submit(Place(ids.Generator, -4, 4));
        sim.Submit(Place(ids.Pole, -2, 4));

        var unit = new IronUnitPart(ids);
        var facts = unit.Place(sim, 0, 0);
        sim.Step();   // drain placement

        // 建造期注入:发电机燃料缓冲 + 单元铁料源(测试自己做 Builder 在 Task 5 才做的事)
        var genId = sim.World.GetEntityAt(-4, 4);
        sim.ElectricGrid.SetFuelBufferJ(genId, 200L * 1_000_000L);   // 200 coal 焦耳,够用
        var (srcX, srcY) = IronUnitPart.IronSourceChestTile(0, 0);
        var srcId = sim.World.GetEntityAt(srcX, srcY);
        var srcInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(srcId));
        srcInv.Insert(ids.IronOre, (int)facts.SeededIronOre, ids.IronOreStackSize);

        var (outX, outY) = IronUnitPart.OutputChestTile(0, 0);

        // 实测(seed 20260905,单元 @ (0,0),1 台 90kW 发电机 -> 满载需求
        // 9498 J/tick,satisfaction ~= 0.158):第一块铁板落箱在 tick 2263,
        // 4000 tick 时约 2 块、10000 tick 时 7 块。4000 是留了 ~1.8x 余量的预算。
        for (int t = 0; t < 4000; t++) sim.Step();

        var outId = sim.World.GetEntityAt(outX, outY);
        var outInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(outId));
        Assert.True(outInv.CountOf(ids.IronPlate) > 0,
            $"tick 预算跑完后输出箱应有铁板,实际 {outInv.CountOf(ids.IronPlate)}");
        Assert.True(facts.RatedPowerDemandJPerTick > 0);
        Assert.Equal(IronUnitPart.EntitiesPerUnit, facts.EntitiesPlaced);
        Assert.Equal(0, sim.RejectedCommandCount);
    }
}
