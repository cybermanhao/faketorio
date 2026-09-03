using Faketorio.Sim.Belts;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Sim.Tests;

public class BeltIntegrationTests
{
    private const byte N = 0, E = 1, S = 2, W = 3;

    private static Simulation NewSim() => new(PrototypeLoader.LoadFromDirectory("data/base"));

    // 放一格传送带并 Step 一次(命令在下一 tick 应用)。
    private static void PlaceBelt(Simulation sim, int x, int y, byte rot)
    {
        sim.Submit(new Command
        {
            Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id,
            X = x, Y = y, Rotation = rot,
        });
        sim.Step();
    }

    private static BeltLine LineAt(Simulation sim, int x, int y)
        => sim.Belts.GetLine(sim.Belts.GetLineAt(x, y));

    [Fact]
    public void SingleBelt_ItemAdvancesAtBeltSpeed()
    {
        var sim = NewSim();
        PlaceBelt(sim, 0, 0, E);
        LineAt(sim, 0, 0).LaneA.TryInsertAtBack(); // gaps=[192]
        sim.Step();
        Assert.Equal(new[] { 184 }, LineAt(sim, 0, 0).LaneA.Gaps); // speed 8: 192-8
        sim.Step();
        Assert.Equal(new[] { 176 }, LineAt(sim, 0, 0).LaneA.Gaps);
    }

    [Fact]
    public void Belt_ItemReachesExitAndStops_NoDownstream()
    {
        var sim = NewSim();
        PlaceBelt(sim, 0, 0, E);
        LineAt(sim, 0, 0).LaneA.TryInsertAtBack(); // gaps=[192]
        for (int t = 0; t < 24; t++) sim.Step(); // 192 / 8 = 24
        Assert.True(LineAt(sim, 0, 0).LaneA.IsFrontReady);
        Assert.Equal(new[] { 0 }, LineAt(sim, 0, 0).LaneA.Gaps);
        sim.Step(); // 无下游线,原地不动
        Assert.Equal(new[] { 0 }, LineAt(sim, 0, 0).LaneA.Gaps);
    }
}
