using Faketorio.Sim.Belts;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Prototypes;
using Faketorio.Sim.State;

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

    private static ulong BeltHash(Simulation sim)
    {
        var w = new Fnv1aHashWriter();
        sim.Belts.WriteState(w);
        return w.Hash;
    }

    private static int LiveLineCount(Simulation sim)
    {
        int c = 0;
        for (int i = 0; i < sim.Belts.Capacity; i++) if (sim.Belts.IsAliveAtIndex(i)) c++;
        return c;
    }

    // 把一条 256 长的 lane 塞满 4 个物品:交替 insert / advance-to-compress。
    private static void Pack(BeltLane lane)
    {
        while (lane.TryInsertAtBack())
            lane.Advance(256); // 把刚插入的物品推到出口,为下一个腾出入口空间
    }

    [Fact]
    public void LShapeCorner_ItemFlowsFromEastLineToSouthLine()
    {
        var sim = NewSim();
        // 东向线 A:(0,0)(1,0)(2,0),出口 (2,0),长 768
        PlaceBelt(sim, 0, 0, E); PlaceBelt(sim, 1, 0, E); PlaceBelt(sim, 2, 0, E);
        // 南向线 B:(3,0)(3,1)(3,2),入口 Tiles[^1]=(3,0),出口 (3,2),长 768
        PlaceBelt(sim, 3, 0, S); PlaceBelt(sim, 3, 1, S); PlaceBelt(sim, 3, 2, S);
        Assert.Equal(2, LiveLineCount(sim)); // 方向不同,两条独立线

        LineAt(sim, 0, 0).LaneA.TryInsertAtBack(); // 放在 A 入口

        // A 上走 704 (=3*256-64) 亚格,过拐角,再在 B 上走 704;每 tick 8;留足余量
        for (int t = 0; t < 704 / 8 + 704 / 8 + 20; t++) sim.Step();

        Assert.Equal(0, LineAt(sim, 0, 0).LaneA.Count);  // A 已空
        Assert.Equal(1, LineAt(sim, 3, 2).LaneA.Count);  // 物品到了 B
    }

    [Fact]
    public void FourTileLoop_ItemKeepsCirculating()
    {
        var sim = NewSim();
        PlaceBelt(sim, 0, 0, E);
        PlaceBelt(sim, 1, 0, S);
        PlaceBelt(sim, 1, 1, W);
        PlaceBelt(sim, 0, 1, N);
        Assert.Equal(4, LiveLineCount(sim)); // 四格四向,互不合并

        LineAt(sim, 0, 0).LaneA.TryInsertAtBack();
        for (int t = 0; t < 300; t++) sim.Step(); // 一圈 ≈ 4*24 tick,跑多圈

        int total = LineAt(sim, 0, 0).LaneA.Count + LineAt(sim, 1, 0).LaneA.Count
                  + LineAt(sim, 1, 1).LaneA.Count + LineAt(sim, 0, 1).LaneA.Count;
        Assert.Equal(1, total); // 物品一直在环里,不多不少
    }

    [Fact]
    public void FourTileLoop_PackedFull_Freezes()
    {
        var sim = NewSim();
        PlaceBelt(sim, 0, 0, E);
        PlaceBelt(sim, 1, 0, S);
        PlaceBelt(sim, 1, 1, W);
        PlaceBelt(sim, 0, 1, N);

        foreach (var (x, y) in new[] { (0, 0), (1, 0), (1, 1), (0, 1) })
            Pack(LineAt(sim, x, y).LaneA);

        for (int t = 0; t < 40; t++) sim.Step(); // 压到出口
        var frozen = BeltHash(sim);
        for (int t = 0; t < 40; t++) sim.Step();
        Assert.Equal(frozen, BeltHash(sim)); // 塞满 ⇒ 传送带状态不再变
    }
}
