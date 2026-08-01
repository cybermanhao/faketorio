using Faketorio.Sim.Commands;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Sim.Tests;

public class DeterminismTests
{
    // 有代表性的场景:多 tick、放置(含被拒)、拆除、跨 chunk 与负坐标
    private static List<ulong> RunScenario()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"));
        int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        var hashes = new List<ulong>();

        for (int t = 0; t < 50; t++)
        {
            if (t % 3 == 0)
                sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = t - 25, Y = -t });
            if (t % 7 == 0)
                sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 0, Y = 0 }); // t>0 时被拒
            if (t == 30) // 拆除 t=6 时放在 (-19,-6) 的箱子,覆盖成功拆除路径
                sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 6 - 25, Y = -6 });
            sim.Step();
            hashes.Add(sim.ComputeStateHash());
        }
        return hashes;
    }

    [Fact]
    public void SameCommands_SameHashEveryTick()
    {
        var first = RunScenario();
        var second = RunScenario();
        Assert.Equal(first, second);
    }

    [Fact]
    public void HashChangesWhenWorldChanges()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"));
        int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        sim.Step();
        var before = sim.ComputeStateHash();
        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 1, Y = 1 });
        sim.Step();
        Assert.NotEqual(before, sim.ComputeStateHash());
    }

    [Fact]
    public void HashCoversEntityRotation()
    {
        ulong Run(byte rotation)
        {
            var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"));
            int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
            sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 0, Y = 0, Rotation = rotation });
            sim.Step();
            return sim.ComputeStateHash();
        }
        Assert.NotEqual(Run(0), Run(1));
    }
}
