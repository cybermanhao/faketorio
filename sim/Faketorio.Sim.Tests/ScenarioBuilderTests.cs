using Faketorio.Sim.Bench.Scenario;

namespace Faketorio.Sim.Tests;

public class ScenarioBuilderTests
{
    [Fact]
    public void Build_Scale1_Deterministic_AndSentinelPasses()
    {
        var a = ScenarioBuilder.Build(1);
        var b = ScenarioBuilder.Build(1);

        Assert.Equal(a.Sim.ComputeStateHash(), b.Sim.ComputeStateHash());
        for (int t = 0; t < 200; t++)
        {
            a.Sim.Step();
            b.Sim.Step();
            Assert.Equal(a.Sim.ComputeStateHash(), b.Sim.ComputeStateHash());
        }
    }

    [Fact]
    public void Build_Scale1_EntityMix_MatchesConstants()
    {
        var s = ScenarioBuilder.Build(1);

        Assert.Equal(ScenarioBuilder.ExpectedEntities(1), s.Totals.Entities);
        Assert.Equal(4, s.Totals.CountByType["electric-mining-drill"]);
        Assert.Equal(2, s.Totals.CountByType["stone-furnace"]);
    }

    [Fact]
    public void Build_DefaultScale_SentinelPasses_AndRunsAShortWhile()
    {
        var s = ScenarioBuilder.Build(ScenarioBuilder.DefaultScale);

        Assert.Equal(0, s.Sim.RejectedCommandCount);
        Assert.True(s.Totals.RatedSupply >= s.Totals.RatedDemand);
        Assert.True(s.Totals.FedDrillCount > 0);

        for (int t = 0; t < 50; t++) s.Sim.Step();   // 冒烟:不炸

        // 骨干真的把电送到了最远那个单元(阵列最后一行的第 0 列)——不然基准
        // 跑的是一片没通电的空转机器,数字好看但毫无意义。
        int cols = (int)Math.Ceiling(Math.Sqrt(ScenarioBuilder.DefaultScale));
        int lastRow = (ScenarioBuilder.DefaultScale - 1) / cols;
        int ax = ScenarioBuilder.UnitOriginX;
        int ay = ScenarioBuilder.UnitOriginY + lastRow * (IronUnitPart.CellHeight + ScenarioBuilder.UnitGapY);
        var farInserter = s.Sim.World.GetEntityAt(ax + 23, ay + 2);   // 单元里的上料机械臂 insB
        Assert.True(s.Sim.ElectricGrid.GetSatisfaction(farInserter).Raw > 0,
            $"最远单元没通电,satisfaction={s.Sim.ElectricGrid.GetSatisfaction(farInserter).Raw}");
    }

    [Fact]
    public void Build_NonDefaultScale_StillBuilds()
    {
        var s = ScenarioBuilder.Build(9);
        Assert.Equal(0, s.Sim.RejectedCommandCount);
    }
}
