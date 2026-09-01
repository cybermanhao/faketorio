using Faketorio.Sim.Belts;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class BeltNetworkTests
{
    private const byte N = 0, E = 1, S = 2, W = 3;

    private static ulong Hash(BeltNetwork net)
    {
        var w = new Fnv1aHashWriter();
        net.WriteState(w);
        return w.Hash;
    }

    private static List<BeltLine> LiveLines(BeltNetwork net)
    {
        var result = new List<BeltLine>();
        for (int i = 0; i < net.Capacity; i++)
            if (net.IsAliveAtIndex(i)) result.Add(net.GetAtIndex(i));
        return result;
    }

    [Fact]
    public void AddBelt_SingleTile_CreatesOneLineCoveringThatTile()
    {
        var net = new BeltNetwork();
        var id = net.AddBelt(5, 3, E);

        var lines = LiveLines(net);
        Assert.Single(lines);
        Assert.Equal(E, lines[0].Direction);
        Assert.Equal(new[] { (5, 3) }, lines[0].Tiles);
        Assert.Equal(256, lines[0].LengthSubTiles);
        Assert.Equal(id, net.GetLineAt(5, 3));
        Assert.Same(lines[0], net.GetLine(id));
    }

    [Fact]
    public void AddBelt_SingleTile_LanesAreUsableLength256()
    {
        var net = new BeltNetwork();
        var line = net.GetLine(net.AddBelt(0, 0, E));
        Assert.True(line.LaneA.TryInsertAtBack());
        Assert.True(line.LaneB.TryInsertAtBack());
        Assert.Equal(new[] { 192 }, line.LaneA.Gaps); // 256 - 64
    }

    [Fact]
    public void GetLineAt_EmptyTile_ReturnsInvalid()
        => Assert.False(new BeltNetwork().GetLineAt(9, 9).IsValid);

    [Fact]
    public void TwoNonAdjacentBelts_StayTwoLines()
    {
        var net = new BeltNetwork();
        net.AddBelt(0, 0, E);
        net.AddBelt(10, 10, E);
        Assert.Equal(2, LiveLines(net).Count);
    }

    [Fact]
    public void WriteState_SameBuildSequence_SameHash()
    {
        BeltNetwork Build()
        {
            var n = new BeltNetwork();
            n.AddBelt(0, 0, E);
            n.AddBelt(4, 7, N);
            return n;
        }
        Assert.Equal(Hash(Build()), Hash(Build()));
    }

    [Fact]
    public void WriteState_DifferentContent_DifferentHash()
    {
        var a = new BeltNetwork(); a.AddBelt(0, 0, E);
        var b = new BeltNetwork(); b.AddBelt(0, 0, E); b.AddBelt(1, 9, E);
        Assert.NotEqual(Hash(a), Hash(b));
    }

    // 顺着 E 方向从出口往入口一格格建:每次新格的下游邻格正好是已建线的入口端。
    private static (BeltNetwork net, BeltLineId id) BuildEastLine(params (int x, int y)[] tilesFrontToBack)
    {
        var net = new BeltNetwork();
        var id = net.AddBelt(tilesFrontToBack[0].x, tilesFrontToBack[0].y, E);
        for (int i = 1; i < tilesFrontToBack.Length; i++)
            id = net.AddBelt(tilesFrontToBack[i].x, tilesFrontToBack[i].y, E);
        return (net, id);
    }

    [Fact]
    public void AddBelt_BehindEntry_MergesOntoEntryEnd()
    {
        var net = new BeltNetwork();
        var id = net.AddBelt(5, 3, E);      // 线 = [(5,3)]
        var id2 = net.AddBelt(4, 3, E);     // (4,3) 的下游邻格 (5,3) 是线的入口端

        Assert.Equal(id, id2);
        Assert.Single(LiveLines(net));
        var line = net.GetLine(id);
        Assert.Equal(new[] { (5, 3), (4, 3) }, line.Tiles);
        Assert.Equal(512, line.LengthSubTiles);
        Assert.Equal(id, net.GetLineAt(4, 3));
        Assert.Equal(id, net.GetLineAt(5, 3));
    }

    [Fact]
    public void AddBelt_AheadOfExit_MergesOntoExitEnd()
    {
        var net = new BeltNetwork();
        var id = net.AddBelt(5, 3, E);      // 线 = [(5,3)]
        var id2 = net.AddBelt(6, 3, E);     // (6,3) 的上游邻格 (5,3) 是线的出口端

        Assert.Equal(id, id2);
        Assert.Single(LiveLines(net));
        var line = net.GetLine(id);
        Assert.Equal(new[] { (6, 3), (5, 3) }, line.Tiles); // 新格成为新的出口
        Assert.Equal(512, line.LengthSubTiles);
        Assert.Equal(id, net.GetLineAt(6, 3));
    }

    [Fact]
    public void Build3TileLine_ByRepeatedEntryEndMerge()
    {
        var (net, id) = BuildEastLine((5, 3), (4, 3), (3, 3));
        Assert.Single(LiveLines(net));
        var line = net.GetLine(id);
        Assert.Equal(new[] { (5, 3), (4, 3), (3, 3) }, line.Tiles);
        Assert.Equal(768, line.LengthSubTiles);
        foreach (var (tx, ty) in line.Tiles)
            Assert.Equal(id, net.GetLineAt(tx, ty));
    }

    [Fact]
    public void MergeOntoEntryEnd_PreservesItemPositions()
    {
        var net = new BeltNetwork();
        var id = net.AddBelt(5, 3, E);
        net.GetLine(id).LaneA.TryInsertAtBack();          // LaneA gaps = [192]
        net.AddBelt(4, 3, E);                              // ExtendBack:不动物品
        Assert.Equal(new[] { 192 }, net.GetLine(id).LaneA.Gaps);
        Assert.Equal(new[] { 192 }, net.GetLine(id).LaneA.ToAbsolutePositions());
        Assert.Equal(512, net.GetLine(id).LengthSubTiles);
    }

    [Fact]
    public void MergeOntoExitEnd_ShiftsItemAwayFromNewExit()
    {
        var net = new BeltNetwork();
        var id = net.AddBelt(5, 3, E);
        net.GetLine(id).LaneA.TryInsertAtBack();          // gaps = [192]
        net.AddBelt(6, 3, E);                              // ExtendFront:gaps[0] += 256
        Assert.Equal(new[] { 448 }, net.GetLine(id).LaneA.Gaps);
        Assert.Equal(new[] { 448 }, net.GetLine(id).LaneA.ToAbsolutePositions());
    }

    [Fact]
    public void WrongDirectionNeighbour_DoesNotMerge()
    {
        var net = new BeltNetwork();
        net.AddBelt(5, 3, E);
        net.AddBelt(6, 3, S);   // 上游邻格是 (5,3),但方向不同
        Assert.Equal(2, LiveLines(net).Count);
    }

    [Fact]
    public void PerpendicularNeighbour_DoesNotMerge()
    {
        var (net, _) = BuildEastLine((5, 3), (4, 3), (3, 3));
        net.AddBelt(4, 2, E);   // 上/下游邻格 (5,2)/(3,2) 都不是那条线的端点
        Assert.Equal(2, LiveLines(net).Count);
    }

    // 建一条 E 向线,tiles 前到后给出。第一格是出口。
    private static (BeltNetwork net, BeltLineId id) EastLineOn(BeltNetwork net, params (int x, int y)[] frontToBack)
    {
        var id = net.AddBelt(frontToBack[0].x, frontToBack[0].y, E);
        for (int i = 1; i < frontToBack.Length; i++)
            id = net.AddBelt(frontToBack[i].x, frontToBack[i].y, E);
        return (net, id);
    }

    [Fact]
    public void AddBelt_FillsOneTileGapBetweenTwoLines_MergesAllThree()
    {
        var net = new BeltNetwork();
        EastLineOn(net, (6, 3), (5, 3));           // L_down:出口 (6,3),入口 (5,3)
        EastLineOn(net, (3, 3), (2, 3));           // L_up:  出口 (3,3),入口 (2,3)

        var id = net.AddBelt(4, 3, E);             // 补空隙:上游邻格 (3,3)=L_up 出口,下游邻格 (5,3)=L_down 入口

        Assert.Single(LiveLines(net));
        var line = net.GetLine(id);
        Assert.Equal(new[] { (6, 3), (5, 3), (4, 3), (3, 3), (2, 3) }, line.Tiles);
        Assert.Equal(1280, line.LengthSubTiles);
        foreach (var (tx, ty) in line.Tiles)
            Assert.Equal(id, net.GetLineAt(tx, ty));
    }

    [Fact]
    public void ThreeWayMerge_ConcatenatesItemsAtCorrectAbsolutePositions()
    {
        var net = new BeltNetwork();
        var (_, downId) = EastLineOn(net, (6, 3), (5, 3));   // 长 512
        var (_, upId)   = EastLineOn(net, (3, 3), (2, 3));   // 长 512

        net.GetLine(downId).LaneA.TryInsertAtBack();          // L_down.LaneA gaps=[448] -> abs [448]
        net.GetLine(upId).LaneA.TryInsertAtBack();            // L_up.LaneA   gaps=[448] -> abs [448]

        var id = net.AddBelt(4, 3, E);
        var lane = net.GetLine(id).LaneA;

        // down 原样 [448];up 后移 256 + 512 = 768 -> [1216]
        Assert.Equal(new[] { 448, 1216 }, lane.ToAbsolutePositions());
        Assert.Equal(new[] { 448, 704 }, lane.Gaps);          // 448, 1216-448-64
        Assert.Equal(1280, net.GetLine(id).LengthSubTiles);
    }

    [Fact]
    public void ThreeWayMerge_ReleasesAllThreeOldSlots()
    {
        var net = new BeltNetwork();
        var (_, downId) = EastLineOn(net, (6, 3), (5, 3));
        var (_, upId)   = EastLineOn(net, (3, 3), (2, 3));
        var id = net.AddBelt(4, 3, E);

        Assert.False(net.GetLineAt(4, 3).Equals(BeltLineId.Invalid)); // 新线有效
        Assert.Single(LiveLines(net));
        Assert.Equal(id, net.GetLineAt(2, 3));
        Assert.Equal(id, net.GetLineAt(6, 3));
        Assert.NotEqual(id, downId);
        Assert.NotEqual(id, upId);
    }

    [Fact]
    public void ThreeWayMerge_Deterministic()
    {
        BeltNetwork Build()
        {
            var n = new BeltNetwork();
            EastLineOn(n, (6, 3), (5, 3));
            EastLineOn(n, (3, 3), (2, 3));
            n.AddBelt(4, 3, E);
            return n;
        }
        Assert.Equal(Hash(Build()), Hash(Build()));
    }
}
