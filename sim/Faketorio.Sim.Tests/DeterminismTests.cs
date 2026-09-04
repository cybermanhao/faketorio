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

    private static List<ulong> RunBeltScenario()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"));
        int belt = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id;
        var hashes = new List<ulong>();
        for (int t = 0; t < 60; t++)
        {
            if (t < 6) // 放一串 6 格东向带 (0,0)..(5,0)
                sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = belt, X = t, Y = 0, Rotation = 1 });
            if (t == 10) // 中间拆一格 -> 拆成两条线
                sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 3, Y = 0 });
            if (t == 20) // 补回 (3,0) -> 三路合并回一条
                sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = belt, X = 3, Y = 0, Rotation = 1 });
            if (t >= 6 && t % 12 == 6)
            {
                var line = sim.Belts.GetLine(sim.Belts.GetLineAt(0, 0));
                line.LaneA.TryInsertAtBack();
                line.LaneB.TryInsertAtBack();
            }
            sim.Step();
            hashes.Add(sim.ComputeStateHash());
        }
        return hashes;
    }

    [Fact]
    public void BeltScenario_SameCommands_SameHashEveryTick()
    {
        Assert.Equal(RunBeltScenario(), RunBeltScenario());
    }

    // 箱子 + 物品插入 + 拆除的代表性场景。物品直接经 Inventories 写入
    // (不走命令)——与 RunBeltScenario 里直接调 LaneA.TryInsertAtBack 同理。
    private static List<ulong> RunInventoryScenario()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"));
        int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        var hashes = new List<ulong>();

        for (int t = 0; t < 60; t++)
        {
            if (t == 0) sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 2, Y = 2 });
            if (t == 1) sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 5, Y = 2 });
            if (t == 40) sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 5, Y = 2 });

            if (t is >= 2 and < 40 && t % 4 == 2)
            {
                var a = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(2, 2)));
                a.Insert(plate, 7, plateStack);
                var b = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(5, 2)));
                b.Insert(coal, 3, coalStack);
            }
            if (t is >= 10 and < 40 && t % 9 == 1)
            {
                var a = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(2, 2)));
                a.Remove(plate, 5);
            }

            sim.Step();
            hashes.Add(sim.ComputeStateHash());
        }
        return hashes;
    }

    [Fact]
    public void InventoryScenario_SameCommands_SameHashEveryTick()
    {
        Assert.Equal(RunInventoryScenario(), RunInventoryScenario());
    }

    [Fact]
    public void HashChangesWhenItemInserted()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"));
        int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;

        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 1, Y = 1 });
        sim.Step();
        var before = sim.ComputeStateHash();

        var inv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(1, 1)));
        inv.Insert(plate, 5, plateStack);

        Assert.NotEqual(before, sim.ComputeStateHash());
    }

    private static List<ulong> RunResourceScenario(long seed)
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"), seed);
        var hashes = new List<ulong>();
        for (int t = 0; t < 30; t++)
        {
            if (t == 0)
                for (int x = 180; x < 260; x += 8)          // pure-noise sweep across chunks
                    for (int y = 180; y < 260; y += 8)
                        sim.Resources.GetResourceAt(x, y);
            if (t == 5)  sim.Resources.Extract(6, -8, 40);   // coal starter patch
            if (t == 12) sim.Resources.Extract(-9, -6, 40);  // iron starter patch
            if (t == 20) sim.Resources.Extract(6, -8, 25);
            sim.Step();
            hashes.Add(sim.ComputeStateHash());
        }
        return hashes;
    }

    [Fact]
    public void ResourceScenario_SameSeedSameCommands_SameHashEveryTick()
        => Assert.Equal(RunResourceScenario(777), RunResourceScenario(777));

    [Fact]
    public void ResourceScenario_DifferentSeed_DifferentHash()
        => Assert.NotEqual(RunResourceScenario(1), RunResourceScenario(2));

    private static List<ulong> RunPlayerScenario(long seed)
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"), seed);
        int gearRecipe = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;
        var hashes = new List<ulong>();
        for (int t = 0; t < 60; t++)
        {
            if (t < 8)  sim.Submit(new Command { Type = CommandType.MovePlayer, Rotation = 2 });  // walk east
            if (t == 8) sim.Submit(new Command { Type = CommandType.StopPlayer });
            if (t == 10) sim.Submit(new Command { Type = CommandType.MineStart, X = 1, Y = -1 });  // near coal patch
            if (t == 40) sim.Submit(new Command { Type = CommandType.MineStop });
            if (t == 12) sim.Submit(new Command { Type = CommandType.CraftEnqueue, ProtoId = gearRecipe, X = 1 });
            sim.Step();
            hashes.Add(sim.ComputeStateHash());
        }
        return hashes;
    }

    [Fact]
    public void PlayerScenario_SameSeedSameCommands_SameHashEveryTick()
        => Assert.Equal(RunPlayerScenario(4242), RunPlayerScenario(4242));
}
