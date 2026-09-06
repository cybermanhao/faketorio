using Faketorio.Sim.Bench.Scenario;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Sim.Tests;

public class ScenarioPartTests
{
    // 用 Task 5 选定的世界种子(基准场景同一个种子),这样这里的实测注释和
    // ScenarioBuilder 里的钉死常量描述的是同一个世界。
    private static Simulation NewSim()
        => new(PrototypeLoader.LoadFromDirectory("data/base"), ScenarioBuilder.WorldSeed);

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

    [Fact]
    public void PowerDistrict_ReachesFullSatisfaction()
    {
        var sim = NewSim();
        var ids = new ScenarioProtoIds(sim.Prototypes);

        // 3 台发电机(4500 J/tick)盖不住一个单元的 9498 J/tick 满载需求,
        // 拿 7 台(10500 J/tick)才能把 satisfaction 顶到 1。
        var pd = new PowerDistrictPart(ids, generatorCount: 7);
        var facts = pd.Place(sim, 0, 0);

        // 一个假消费者(一个铁生产单元)靠这片电供电。发电区锚点 (0,0),
        // 杆链最右一根在 rel (20,2);单元放在 (24,0),西侧接入杆 (25,2) 落在
        // 连线距离 5 之内 -> 并入同一张网。
        var unit = new IronUnitPart(ids);
        unit.Place(sim, 24, 0);
        sim.Step();  // drain

        foreach (var (gx, gy) in pd.GeneratorTiles)
            sim.ElectricGrid.SetFuelBufferJ(sim.World.GetEntityAt(gx, gy), 200L * 1_000_000L);

        for (int t = 0; t < 300; t++) sim.Step();

        // 单元里一个耗电实体(rel (23,2) = 上料机械臂 insB)的 satisfaction 应接近 1。
        var powered = sim.World.GetEntityAt(24 + 23, 0 + 2);
        Assert.True(sim.ElectricGrid.GetSatisfaction(powered).Raw >= 60000,
            $"satisfaction={sim.ElectricGrid.GetSatisfaction(powered).Raw}");
        Assert.True(facts.RatedPowerSupplyJPerTick > 0);
        Assert.Equal(0, facts.RatedPowerDemandJPerTick);
        Assert.Equal(0, facts.SeededIronOre);
    }

    [Fact]
    public void PoleBackbone_ConnectsTwoUnitsToPower()
    {
        var sim = NewSim();
        var ids = new ScenarioProtoIds(sim.Prototypes);

        var pd = new PowerDistrictPart(ids, generatorCount: 3);
        pd.Place(sim, 0, 0);

        var u0 = new IronUnitPart(ids); u0.Place(sim, 10, 0);
        var u1 = new IronUnitPart(ids); u1.Place(sim, 10 + IronUnitPart.CellWidth + 4, 0);

        // 骨干走 y=-2 的净空走廊(单元/发电区实体全在 y>=0),经每个单元西侧
        // 接入杆的 x 上方穿过:district 杆 (2,2) <- (2,-2) 桥进电,(2,-2)..(47,-2)
        // 每 6 格一根,u0 西杆 (11,2) / u1 西杆 (51,2) 都落在连线距离内。
        var backbone = new PoleBackbonePart(ids, new[]
        {
            (2, -2),
            (10 + 1, -2),
            (10 + IronUnitPart.CellWidth + 4 + 1, -2),
        });
        backbone.Place(sim, 0, 0);
        sim.Step();

        foreach (var (gx, gy) in pd.GeneratorTiles)
            sim.ElectricGrid.SetFuelBufferJ(sim.World.GetEntityAt(gx, gy), 200L * 1_000_000L);
        for (int t = 0; t < 200; t++) sim.Step();

        var f0 = sim.World.GetEntityAt(10 + 23, 0 + 2);
        var f1 = sim.World.GetEntityAt(10 + IronUnitPart.CellWidth + 4 + 23, 0 + 2);
        Assert.True(sim.ElectricGrid.GetSatisfaction(f0).Raw > 0,
            $"u0 satisfaction={sim.ElectricGrid.GetSatisfaction(f0).Raw}");
        Assert.True(sim.ElectricGrid.GetSatisfaction(f1).Raw > 0,
            $"u1 satisfaction={sim.ElectricGrid.GetSatisfaction(f1).Raw}");
    }
}
