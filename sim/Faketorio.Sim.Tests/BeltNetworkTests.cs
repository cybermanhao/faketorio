using Faketorio.Sim.Belts;
using Faketorio.Sim.State;
using System.Linq;

namespace Faketorio.Sim.Tests;

public class BeltNetworkTests
{
    private const byte N = 0, E = 1, S = 2, W = 3;
    private const int TestItem = 1;

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
        Assert.True(line.LaneA.TryInsertAtBack(TestItem));
        Assert.True(line.LaneB.TryInsertAtBack(TestItem));
        Assert.Equal(new[] { 192 }, line.LaneA.Gaps); // 256 - 64
    }

    [Fact]
    public void GetLineAt_EmptyTile_ReturnsInvalid()
        => Assert.False(new BeltNetwork().GetLineAt(9, 9).IsValid);

    [Fact]
    public void AddBelt_InvalidDirection_ThrowsBeforeMutating()
    {
        var net = new BeltNetwork();
        Assert.Throws<ArgumentOutOfRangeException>(() => net.AddBelt(0, 0, 4));
        Assert.Equal(0, net.Capacity);              // no orphan line left in the pool
        Assert.False(net.GetLineAt(0, 0).IsValid);  // no stale tile-index entry
    }

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
        net.GetLine(id).LaneA.TryInsertAtBack(TestItem);          // LaneA gaps = [192]
        net.AddBelt(4, 3, E);                              // ExtendBack:不动物品
        Assert.Equal(new[] { 192 }, net.GetLine(id).LaneA.Gaps);
        Assert.Equal(new[] { 192 }, net.GetLine(id).LaneA.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles));
        Assert.Equal(512, net.GetLine(id).LengthSubTiles);
    }

    [Fact]
    public void MergeOntoExitEnd_ShiftsItemAwayFromNewExit()
    {
        var net = new BeltNetwork();
        var id = net.AddBelt(5, 3, E);
        net.GetLine(id).LaneA.TryInsertAtBack(TestItem);          // gaps = [192]
        net.AddBelt(6, 3, E);                              // ExtendFront:gaps[0] += 256
        Assert.Equal(new[] { 448 }, net.GetLine(id).LaneA.Gaps);
        Assert.Equal(new[] { 448 }, net.GetLine(id).LaneA.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles));
    }

    [Fact]
    public void WrongDirectionNeighbour_DoesNotMerge()
    {
        var net = new BeltNetwork();
        net.AddBelt(5, 3, E);
        // (5,3) 是 E 线的出口格 Tiles[0],但新格朝 S —— 方向不符,不应合并
        net.AddBelt(5, 4, S);   // Delta(S)=(0,1) -> back=(5,3)
        Assert.Equal(2, LiveLines(net).Count);
    }

    [Fact]
    public void PerpendicularNeighbour_DoesNotMerge()
    {
        var (net, _) = BuildEastLine((5, 3), (4, 3), (3, 3));
        // (4,2) 的上/下游邻格 (3,2)/(5,2) 都是空的 —— 附近有线但不相邻,不应产生合并
        net.AddBelt(4, 2, E);
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
        var (_, downId) = EastLineOn(net, (6, 3), (5, 3));            // 2 格,长 512
        var (_, upId)   = EastLineOn(net, (3, 3), (2, 3), (1, 3));    // 3 格,长 768

        // L_down.LaneA: 一个物品 -> gaps [448], abs [448]
        net.GetLine(downId).LaneA.TryInsertAtBack(TestItem);
        // L_up.LaneA: 插入 -> [704],Advance(200) -> [504],再插入 -> gaps [504,136], abs [504,704]
        net.GetLine(upId).LaneA.TryInsertAtBack(TestItem);
        net.GetLine(upId).LaneA.Advance(200);
        net.GetLine(upId).LaneA.TryInsertAtBack(TestItem);
        // L_down.LaneB: 一个物品 -> abs [448]
        net.GetLine(downId).LaneB.TryInsertAtBack(TestItem);
        // L_up.LaneB: 一个物品 -> gaps [704], abs [704]
        net.GetLine(upId).LaneB.TryInsertAtBack(TestItem);

        var id = net.AddBelt(4, 3, E);                                // 三路合并
        var merged = net.GetLine(id);

        // combinedLen = 768 + 256 + 512 = 1536; shift = 256 + 512 = 768
        Assert.Equal(new[] { (6, 3), (5, 3), (4, 3), (3, 3), (2, 3), (1, 3) }, merged.Tiles);
        Assert.Equal(1536, merged.LengthSubTiles);

        // LaneA: down 448 原样;up 504->1272, 704->1472
        Assert.Equal(new[] { 448, 1272, 1472 }, merged.LaneA.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles));
        Assert.Equal(new[] { 448, 760, 136 }, merged.LaneA.Gaps);

        // LaneB: down 448 原样;up 704->1472
        Assert.Equal(new[] { 448, 1472 }, merged.LaneB.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles));
        Assert.Equal(new[] { 448, 960 }, merged.LaneB.Gaps);
    }

    [Fact]
    public void ThreeWayMerge_PreservesItemTypesFromBothOriginalLines()
    {
        var net = new BeltNetwork();
        // 先建两段东向线,分别插入不同类型的物品,再放中间那格触发三路合并
        var (_, downId) = EastLineOn(net, (6, 3), (5, 3));
        net.GetLine(downId).LaneA.TryInsertAtBack(10);

        var (_, upId) = EastLineOn(net, (3, 3), (2, 3));
        net.GetLine(upId).LaneA.TryInsertAtBack(20);

        var mergedId = net.AddBelt(4, 3, E); // 触发三路合并
        var positions = net.GetLine(mergedId).LaneA.ToAbsolutePositions();

        Assert.Equal(2, positions.Count);
        // 上游(离出口更远的一侧,down 是 6,3/5,3 段,up 是 3,3/2,3 段)保序:
        // down 的物品(10)排在前面(离出口更近),up 的物品(20)排在后面。
        Assert.Equal(10, positions[0].ItemProtoId);
        Assert.Equal(20, positions[1].ItemProtoId);
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

    [Fact]
    public void RemoveBelt_SingleTileLine_DestroysIt()
    {
        var net = new BeltNetwork();
        net.AddBelt(5, 5, E);
        int discarded = net.RemoveBelt(5, 5);
        Assert.Equal(0, discarded);
        Assert.Empty(LiveLines(net));
        Assert.False(net.GetLineAt(5, 5).IsValid);
    }

    [Fact]
    public void RemoveBelt_ExitEndpoint_ShrinksLineKeepsId()
    {
        var net = new BeltNetwork();
        var (_, id) = EastLineOn(net, (5, 3), (4, 3), (3, 3)); // len 768, exit (5,3)
        int discarded = net.RemoveBelt(5, 3);
        Assert.Equal(0, discarded);
        Assert.Single(LiveLines(net));
        var line = net.GetLine(id);                // 同一 id 仍存活
        Assert.Equal(new[] { (4, 3), (3, 3) }, line.Tiles);
        Assert.Equal(512, line.LengthSubTiles);
        Assert.False(net.GetLineAt(5, 3).IsValid);
        Assert.Equal(id, net.GetLineAt(4, 3));
    }

    [Fact]
    public void RemoveBelt_EntryEndpoint_ShrinksLineKeepsId()
    {
        var net = new BeltNetwork();
        var (_, id) = EastLineOn(net, (5, 3), (4, 3), (3, 3)); // entry (3,3), k = 2 = n-1
        int discarded = net.RemoveBelt(3, 3);
        Assert.Equal(0, discarded);
        var line = net.GetLine(id);
        Assert.Equal(new[] { (5, 3), (4, 3) }, line.Tiles);
        Assert.Equal(512, line.LengthSubTiles);
        Assert.False(net.GetLineAt(3, 3).IsValid);
    }

    [Fact]
    public void RemoveBelt_ExitEndpoint_DiscardsItemOnRemovedTile_AndCounts()
    {
        var net = new BeltNetwork();
        var (_, id) = EastLineOn(net, (5, 3), (4, 3), (3, 3)); // len 768
        var lane = net.GetLine(id).LaneA;
        lane.TryInsertAtBack(TestItem);   // gaps=[704]
        lane.Advance(704);         // gaps=[0],物品贴在出口(前沿 0,在出口格 [0,256))
        int discarded = net.RemoveBelt(5, 3);
        Assert.Equal(1, discarded);
        Assert.Equal(0, net.GetLine(id).LaneA.Count);
        Assert.Equal(512, net.GetLine(id).LengthSubTiles);
    }

    [Fact]
    public void RemoveBelt_EntryEndpoint_DiscardsBodyStraddler_AndCounts()
    {
        var net = new BeltNetwork();
        var (_, id) = EastLineOn(net, (5, 3), (4, 3), (3, 3)); // len 768, k=2 removes tile [512,768)
        var lane = net.GetLine(id).LaneA;
        lane.TryInsertAtBack(TestItem);   // gaps=[704],abs 704
        lane.Advance(222);         // gaps=[482],abs 482:身体 [482,546] 从 tile (4,3) 跨进 (3,3)
        int discarded = net.RemoveBelt(3, 3);
        Assert.Equal(1, discarded); // 前沿 482 ∈ [512-63, 768) = [449,768) → 被清
        Assert.Equal(0, net.GetLine(id).LaneA.Count);
        Assert.Equal(512, net.GetLine(id).LengthSubTiles);
    }

    [Fact]
    public void RemoveBelt_Endpoint_IsDeterministic()
    {
        BeltNetwork Build()
        {
            var n = new BeltNetwork();
            EastLineOn(n, (5, 3), (4, 3), (3, 3));
            n.RemoveBelt(5, 3);
            return n;
        }
        Assert.Equal(Hash(Build()), Hash(Build()));
    }

    [Fact]
    public void RemoveBelt_MiddleTile_SplitsIntoTwoLines()
    {
        var net = new BeltNetwork();
        var (_, id) = EastLineOn(net, (6, 3), (5, 3), (4, 3), (3, 3), (2, 3)); // 5 tiles, len 1280
        int discarded = net.RemoveBelt(4, 3); // k = 2, n = 5

        Assert.Equal(0, discarded);
        Assert.Equal(2, LiveLines(net).Count);

        var front = net.GetLine(id);                       // 原 id 仍存活
        Assert.Equal(new[] { (6, 3), (5, 3) }, front.Tiles);
        Assert.Equal(512, front.LengthSubTiles);

        var backId = net.GetLineAt(3, 3);
        Assert.NotEqual(id, backId);
        var back = net.GetLine(backId);
        Assert.Equal(new[] { (3, 3), (2, 3) }, back.Tiles);
        Assert.Equal(512, back.LengthSubTiles);

        Assert.False(net.GetLineAt(4, 3).IsValid);
        Assert.Equal(id, net.GetLineAt(6, 3));
        Assert.Equal(backId, net.GetLineAt(2, 3));
    }

    [Fact]
    public void RemoveBelt_MiddleTile_BackHalfItemGetsCorrectAbsolutePosition()
    {
        var net = new BeltNetwork();
        var (_, id) = EastLineOn(net, (6, 3), (5, 3), (4, 3), (3, 3), (2, 3)); // len 1280
        net.GetLine(id).LaneA.TryInsertAtBack(TestItem); // gaps=[1216],abs 1216(在 tile (2,3))

        net.RemoveBelt(4, 3); // k=2,cut = 3*256 = 768
        var backId = net.GetLineAt(2, 3);

        // 1216 >= 768 → 后半段,新前沿 1216 - 768 = 448
        Assert.Equal(new[] { 448 }, net.GetLine(backId).LaneA.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles));
        Assert.Equal(0, net.GetLine(id).LaneA.Count); // 前半段没物品
    }

    [Fact]
    public void MiddleSplit_PreservesItemTypeInBackHalf()
    {
        var net = new BeltNetwork();
        var (_, id) = EastLineOn(net, (6, 3), (5, 3), (4, 3), (3, 3), (2, 3)); // len 1280
        net.GetLine(id).LaneA.TryInsertAtBack(30); // gaps=[1216],abs 1216(在后半段 tile (2,3))

        net.RemoveBelt(4, 3); // 中间拆分,k=2,cut=768

        var backId = net.GetLineAt(2, 3);
        var positions = net.GetLine(backId).LaneA.ToAbsolutePositions();
        Assert.Single(positions);
        Assert.Equal(30, positions[0].ItemProtoId);
    }

    [Fact]
    public void RemoveBelt_MiddleTile_ItemOnRemovedTile_DiscardedAndCounted()
    {
        var net = new BeltNetwork();
        var (_, id) = EastLineOn(net, (6, 3), (5, 3), (4, 3), (3, 3), (2, 3)); // len 1280
        var lane = net.GetLine(id).LaneA;
        lane.TryInsertAtBack(TestItem); // gaps=[1216]
        lane.Advance(576);       // gaps=[640],abs 640:落在被移格 tile 2 = [512,768)

        int discarded = net.RemoveBelt(4, 3);
        Assert.Equal(1, discarded);
        var backId = net.GetLineAt(2, 3);
        Assert.Equal(0, net.GetLine(id).LaneA.Count);
        Assert.Equal(0, net.GetLine(backId).LaneA.Count);
    }

    [Fact]
    public void RemoveBelt_MiddleTile_SeamStraddler_DiscardedAndCounted()
    {
        var net = new BeltNetwork();
        var (_, id) = EastLineOn(net, (6, 3), (5, 3), (4, 3), (3, 3), (2, 3)); // len 1280
        var lane = net.GetLine(id).LaneA;
        lane.TryInsertAtBack(TestItem); // gaps=[1216]
        lane.Advance(726);       // gaps=[490],abs 490:前沿在 tile 1 [256,512),身体 [490,554] 跨进被移格

        int discarded = net.RemoveBelt(4, 3); // 若清除区间不向前拓宽 W-1,这个物品会留在前半段,ShrinkBack 会抛
        Assert.Equal(1, discarded);
        Assert.Equal(0, net.GetLine(id).LaneA.Count);
        Assert.Equal(512, net.GetLine(id).LengthSubTiles);
    }

    [Fact]
    public void RemoveBelt_MiddleTile_IsDeterministic()
    {
        BeltNetwork Build()
        {
            var n = new BeltNetwork();
            EastLineOn(n, (6, 3), (5, 3), (4, 3), (3, 3), (2, 3));
            n.RemoveBelt(4, 3);
            return n;
        }
        Assert.Equal(Hash(Build()), Hash(Build()));
    }

    [Fact]
    public void RemoveBelt_SingleTileLine_CountsDestroyedItems()
    {
        var net = new BeltNetwork();
        var id = net.AddBelt(5, 5, E);
        var line = net.GetLine(id);
        line.LaneA.TryInsertAtBack(TestItem); line.LaneA.Advance(128); line.LaneA.TryInsertAtBack(TestItem); // 2 on LaneA
        line.LaneB.TryInsertAtBack(TestItem);                                                        // 1 on LaneB
        Assert.Equal(3, net.RemoveBelt(5, 5));
        Assert.Empty(LiveLines(net));
    }

    [Fact]
    public void RemoveBelt_MiddleTile_BackHalfLanesAreIndependent()
    {
        var net = new BeltNetwork();
        var (_, id) = EastLineOn(net, (6, 3), (5, 3), (4, 3), (3, 3), (2, 3)); // len 1280
        net.GetLine(id).LaneA.TryInsertAtBack(TestItem);          // LaneA abs 1216
        net.GetLine(id).LaneB.TryInsertAtBack(TestItem);          // LaneB abs 1216
        net.GetLine(id).LaneB.Advance(320);               // LaneB abs 896

        net.RemoveBelt(4, 3);                             // k=2, cut = 768
        var back = net.GetLine(net.GetLineAt(2, 3));

        Assert.Equal(new[] { 448 }, back.LaneA.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles)); // 1216 - 768
        Assert.Equal(new[] { 128 }, back.LaneB.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles)); // 896 - 768
    }

    [Fact]
    public void RemoveBelt_EntryEndpoint_ItemJustClearOfRemovedTile_Survives()
    {
        var net = new BeltNetwork();
        var (_, id) = EastLineOn(net, (5, 3), (4, 3), (3, 3)); // len 768, remove entry (3,3): k=2=n-1
        var lane = net.GetLine(id).LaneA;
        lane.TryInsertAtBack(TestItem);  // gaps=[704]
        lane.Advance(256);        // gaps=[448]:身体 [448,512) 全在 tile (4,3),不碰被移格
        int discarded = net.RemoveBelt(3, 3);
        Assert.Equal(0, discarded);
        Assert.Equal(new[] { 448 }, net.GetLine(id).LaneA.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles));
        Assert.Equal(512, net.GetLine(id).LengthSubTiles);
    }

    [Fact]
    public void RemoveBelt_MiddleTile_FrontHalfItemJustClearOfSeam_Survives()
    {
        var net = new BeltNetwork();
        var (_, id) = EastLineOn(net, (6, 3), (5, 3), (4, 3), (3, 3), (2, 3)); // len 1280
        var lane = net.GetLine(id).LaneA;
        lane.TryInsertAtBack(TestItem);  // gaps=[1216]
        lane.Advance(768);        // gaps=[448]:身体 [448,512) 全在 tile (5,3),不碰被移格 (4,3)
        int discarded = net.RemoveBelt(4, 3); // k=2, front half len 512
        Assert.Equal(0, discarded);
        Assert.Equal(new[] { 448 }, net.GetLine(id).LaneA.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles)); // 仍在前半段
        Assert.Equal(512, net.GetLine(id).LengthSubTiles);
        Assert.Equal(0, net.GetLine(net.GetLineAt(2, 3)).LaneA.Count);            // 后半段空
    }

    [Fact]
    public void RemoveBelt_MiddleSplit_StillDiscardsAndCountsItemsAfterOutParamChange()
    {
        var net = new BeltNetwork();
        net.AddBelt(0, 0, 1); net.AddBelt(1, 0, 1); net.AddBelt(2, 0, 1); // one east line, 3 tiles

        var line = net.GetLine(net.GetLineAt(0, 0));
        // East line, exit at max x -> Tiles ordered [(2,0),(1,0),(0,0)]; middle tile (1,0) spans
        // leading-edge [256,512). Body [300,364) sits squarely on it.
        line.LaneA.TryInsertAt(300, 7);   // an item on the middle tile (1,0)
        int before = line.LaneA.Count;
        Assert.Equal(1, before);

        net.RemoveBelt(1, 0); // middle split — the straddling item is discarded

        // front half (tile 0) and back half (tile 2) each survive as their own line;
        // the item that was on the removed middle tile is gone.
        int frontCount = net.GetLine(net.GetLineAt(0, 0)).LaneA.Count;
        int backCount = net.GetLine(net.GetLineAt(2, 0)).LaneA.Count;
        Assert.Equal(0, frontCount + backCount);
    }
}
