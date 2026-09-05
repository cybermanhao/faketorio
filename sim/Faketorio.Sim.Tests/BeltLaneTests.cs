using Faketorio.Sim.Belts;
using Faketorio.Sim.State;
using System.Linq;

namespace Faketorio.Sim.Tests;

public class BeltLaneTests
{
    private const int TestItem = 1;   // 位置/间距测试不关心具体是什么物品,固定用一个 id

    private static IReadOnlyList<BeltLane.PositionedItem> Positions(params int[] leadingEdges)
        => Array.ConvertAll(leadingEdges, p => new BeltLane.PositionedItem(p, TestItem));

    [Fact]
    public void NewLane_IsEmpty()
    {
        var lane = new BeltLane(256);
        Assert.Equal(0, lane.Count);
    }

    [Fact]
    public void Constructor_RejectsLengthShorterThanOneItem()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BeltLane(63));
    }

    [Fact]
    public void TryInsertAtBack_FirstItem_EntersAtBackWithFullGapToExit()
    {
        var lane = new BeltLane(256);
        Assert.True(lane.TryInsertAtBack(TestItem));
        Assert.Equal(1, lane.Count);
        Assert.Equal(new[] { 192 }, lane.Gaps); // 256 - 64
    }

    [Fact]
    public void TryInsertAtBack_WhenNoRoom_ReturnsFalseAndDoesNotChangeState()
    {
        var lane = new BeltLane(256);
        Assert.True(lane.TryInsertAtBack(TestItem));  // 单个物品刚好占满整条 256 长的 line
        Assert.False(lane.TryInsertAtBack(TestItem)); // 队尾无空间
        Assert.Equal(1, lane.Count);
        Assert.Equal(new[] { 192 }, lane.Gaps);
    }

    [Fact]
    public void Advance_UnblockedSingleItem_MovesFrontGapForward()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem); // gaps=[192]
        lane.Advance(50);
        Assert.Equal(new[] { 142 }, lane.Gaps);
    }

    [Fact]
    public void Advance_NeverDrivesGapNegative_ClampsAtZero()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem); // gaps=[192]
        lane.Advance(1000);      // 远超剩余 gap
        Assert.Equal(new[] { 0 }, lane.Gaps);
    }

    [Fact]
    public void Advance_CascadesIntoNextGapWhenFrontFullyCloses()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem);  // gaps=[192]
        lane.Advance(100);        // gaps=[92]
        lane.TryInsertAtBack(TestItem);  // free=256-(92+64+64)=36 -> gaps=[92,36]
        lane.Advance(110);        // 92 耗尽 gaps[0],剩余 18 接着消耗 gaps[1]
        Assert.Equal(new[] { 0, 18 }, lane.Gaps);
    }

    [Fact]
    public void Advance_ContinuesFromCachedOpenIndexAfterFrontCloses()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem);
        lane.Advance(100);
        lane.TryInsertAtBack(TestItem);
        lane.Advance(110); // gaps=[0,18]
        lane.Advance(5);    // 从缓存的下标(不是 0)继续推进
        Assert.Equal(new[] { 0, 13 }, lane.Gaps);
    }

    [Fact]
    public void Advance_OnEmptyLane_DoesNothing()
    {
        var lane = new BeltLane(256);
        lane.Advance(50); // 不能抛异常
        Assert.Equal(0, lane.Count);
    }

    [Fact]
    public void Advance_WithZeroSpeed_DoesNotChangeGaps()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem);
        lane.Advance(0);
        Assert.Equal(new[] { 192 }, lane.Gaps);
    }

    [Fact]
    public void Advance_ManyTicksWhileBlocked_ConvergesToFullyCompressedWithoutError()
    {
        var lane = new BeltLane(256);
        // 256 / 64 = 4 个物品刚好装满一条 line;每次插入后都把物品尽量往前推,
        // 为下一次插入腾出队尾空间。
        for (int i = 0; i < 4; i++)
        {
            Assert.True(lane.TryInsertAtBack(TestItem));
            lane.Advance(1000);
        }
        Assert.Equal(4, lane.Count);
        Assert.False(lane.TryInsertAtBack(TestItem)); // 4*64=256,队尾无空间

        // 此后一直堵塞(没有任何 RemoveFront):重复 Advance 必须保持稳定,
        // 不抛异常、不出现负值、不改变物品数。
        for (int tick = 0; tick < 1000; tick++)
            lane.Advance(7);

        Assert.Equal(4, lane.Count);
        Assert.All(lane.Gaps, g => Assert.True(g >= 0));
        Assert.Equal(0, lane.Gaps[0]); // 完全压缩到出口
    }

    [Fact]
    public void Advance_CursorStaysO1InBlockedSteadyState_NotFrontRescan()
    {
        var lane = new BeltLane(256);
        // 装满 4 个物品(256/64),让它们尽量往前推,形成完全堵塞状态。
        for (int i = 0; i < 4; i++)
        {
            lane.TryInsertAtBack(TestItem);
            lane.Advance(1000);
        }
        Assert.Equal(new[] { 0, 0, 0, 0 }, lane.Gaps);

        // 稳定堵塞状态下,重复调用 Advance 每次只应做 1 次循环迭代——
        // 如果实现退化成"每次都从下标 0 重新扫描",这里会是 4(或更多),
        // 而不是 1。
        lane.Advance(7);
        Assert.Equal(1, lane.TouchesInLastAdvance);
        lane.Advance(7);
        Assert.Equal(1, lane.TouchesInLastAdvance);
    }

    [Fact]
    public void Advance_WithNegativeSpeed_Throws()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem);
        Assert.Throws<ArgumentOutOfRangeException>(() => lane.Advance(-1));
    }

    [Fact]
    public void Advance_UnblockedMultiItem_OnlyMovesFrontGap()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem);  // gaps=[192]
        lane.Advance(100);        // gaps=[92]
        lane.TryInsertAtBack(TestItem);  // gaps=[92,36]
        lane.Advance(10);         // 前方 gap 未压缩到 0,不应级联touch第二个 gap
        Assert.Equal(new[] { 82, 36 }, lane.Gaps);
    }

    [Fact]
    public void IsFrontReady_FalseWhenEmpty()
    {
        var lane = new BeltLane(256);
        Assert.False(lane.IsFrontReady);
    }

    [Fact]
    public void IsFrontReady_FalseWhenFrontGapNotYetZero()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem); // gaps=[192]
        Assert.False(lane.IsFrontReady);
    }

    [Fact]
    public void IsFrontReady_TrueWhenFrontGapReachesZero()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem);
        lane.Advance(1000);
        Assert.True(lane.IsFrontReady);
    }

    [Fact]
    public void RemoveFront_WhenNotReady_Throws()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem); // gaps=[192],队首还没到出口
        Assert.Throws<InvalidOperationException>(() => lane.RemoveFront());
    }

    [Fact]
    public void RemoveFront_FreesSpaceIntoNewFrontGap()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem);  // gaps=[192]
        lane.Advance(100);        // gaps=[92]
        lane.TryInsertAtBack(TestItem);  // gaps=[92,36]
        lane.Advance(110);        // gaps=[0,18]
        lane.RemoveFront();
        Assert.Equal(1, lane.Count);
        Assert.Equal(new[] { 82 }, lane.Gaps); // 18 + 64(被移除物品腾出的槽宽)
    }

    [Fact]
    public void RemoveFront_WhenLastItem_LeavesLaneEmpty()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem);
        lane.Advance(1000);
        lane.RemoveFront();
        Assert.Equal(0, lane.Count);
        Assert.False(lane.IsFrontReady);
    }

    [Fact]
    public void RemoveFront_ResetsCursorSoTrailingItemsCanAdvanceOnNextTick()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem);  // gaps=[192]
        lane.Advance(100);        // gaps=[92]
        lane.TryInsertAtBack(TestItem);  // gaps=[92,36]
        lane.Advance(110);        // gaps=[0,18]
        lane.RemoveFront();       // gaps=[82]
        lane.Advance(30);         // 原来的第二个物品现在向出口前进
        Assert.Equal(new[] { 52 }, lane.Gaps); // 82-30
    }

    [Fact]
    public void ExtendBack_GrowsBackCapacityWithoutMovingItems()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem);               // gaps=[192],队尾已满
        Assert.False(lane.TryInsertAtBack(TestItem)); // 延长前没有空间

        lane.ExtendBack(256);                 // line 现在长 512

        Assert.Equal(new[] { 192 }, lane.Gaps);       // 最前物品没有移动
        Assert.True(lane.TryInsertAtBack(TestItem));           // 队尾腾出了空间
        Assert.Equal(new[] { 192, 192 }, lane.Gaps);  // 512 - (192+64) - 64 = 192
    }

    [Fact]
    public void ExtendBack_NegativeSubtiles_Throws()
    {
        var lane = new BeltLane(256);
        Assert.Throws<ArgumentOutOfRangeException>(() => lane.ExtendBack(-1));
    }

    [Fact]
    public void ExtendFront_PushesExitOutwardAndEnlargesFrontGap()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem);   // gaps=[192]
        lane.ExtendFront(256);    // 出口离物品又远了 256
        Assert.Equal(new[] { 448 }, lane.Gaps);
    }

    [Fact]
    public void ExtendFront_OnEmptyLane_JustGrowsLength()
    {
        var lane = new BeltLane(256);
        lane.ExtendFront(256);
        Assert.Equal(0, lane.Count);
        Assert.True(lane.TryInsertAtBack(TestItem));       // 现在长 512
        Assert.Equal(new[] { 448 }, lane.Gaps);   // 512 - 64
    }

    [Fact]
    public void ExtendFront_ResetsCursorSoBlockedFrontItemAdvancesIntoNewSpace()
    {
        var lane = new BeltLane(256);
        // 装满并完全压缩:gaps=[0,0,0,0],内部游标停在末尾
        for (int i = 0; i < 4; i++) { lane.TryInsertAtBack(TestItem); lane.Advance(1000); }
        Assert.Equal(new[] { 0, 0, 0, 0 }, lane.Gaps);

        lane.ExtendFront(64);   // gaps=[64,0,0,0]
        lane.Advance(64);       // 队首必须能走进新腾出的 64

        Assert.Equal(new[] { 0, 0, 0, 0 }, lane.Gaps);
    }

    [Fact]
    public void ExtendFront_NegativeSubtiles_Throws()
    {
        var lane = new BeltLane(256);
        Assert.Throws<ArgumentOutOfRangeException>(() => lane.ExtendFront(-1));
    }

    [Fact]
    public void ToAbsolutePositions_EmptyLane_ReturnsEmpty()
    {
        var lane = new BeltLane(256);
        Assert.Empty(lane.ToAbsolutePositions());
    }

    [Fact]
    public void ToAbsolutePositions_ReturnsLeadingEdgeDistancesFromExit()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem); // gaps=[192]
        lane.Advance(100);       // gaps=[92]
        lane.TryInsertAtBack(TestItem); // gaps=[92,36]
        Assert.Equal(new[] { 92, 192 }, lane.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles)); // 92, 92+64+36
    }

    [Fact]
    public void ToAbsolutePositions_TouchingItems_AreOneWidthApart()
    {
        var lane = new BeltLane(256);
        for (int i = 0; i < 2; i++) { lane.TryInsertAtBack(TestItem); lane.Advance(1000); }
        Assert.Equal(new[] { 0, 64 }, lane.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles));
    }

    [Fact]
    public void FromAbsolutePositions_RoundTripsWithToAbsolutePositions()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(TestItem); lane.Advance(100);
        lane.TryInsertAtBack(TestItem);                 // gaps=[92,36]
        var rebuilt = BeltLane.FromAbsolutePositions(256, lane.ToAbsolutePositions());
        Assert.Equal(new[] { 92, 36 }, rebuilt.Gaps);
        Assert.Equal(2, rebuilt.Count);
    }

    [Fact]
    public void FromAbsolutePositions_EmptyList_GivesEmptyLaneOfGivenLength()
    {
        var lane = BeltLane.FromAbsolutePositions(512, Positions());
        Assert.Equal(0, lane.Count);
        Assert.True(lane.TryInsertAtBack(TestItem));
        Assert.Equal(new[] { 448 }, lane.Gaps); // 512 - 64
    }

    [Fact]
    public void FromAbsolutePositions_OverlappingItems_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => BeltLane.FromAbsolutePositions(256, Positions(10, 50))); // 50-10 < 64
    }

    [Fact]
    public void FromAbsolutePositions_ItemPastExit_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => BeltLane.FromAbsolutePositions(256, Positions(-1)));
    }

    [Fact]
    public void FromAbsolutePositions_ItemOverrunsLineEnd_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => BeltLane.FromAbsolutePositions(256, Positions(200))); // 200+64 > 256
    }

    [Fact]
    public void FromAbsolutePositions_CursorStartsAtZero_BlockedLaneStillConverges()
    {
        var lane = BeltLane.FromAbsolutePositions(256, Positions(0, 64, 128, 192));
        Assert.Equal(new[] { 0, 0, 0, 0 }, lane.Gaps);
        lane.Advance(7); // 不抛、不产生负值
        Assert.Equal(new[] { 0, 0, 0, 0 }, lane.Gaps);
    }

    [Fact]
    public void TryRemoveItemInRange_NoItemInRange_ReturnsFalseAndKeepsState()
    {
        var lane = MakeThreeItemLane();                 // gaps=[20,20,24],前沿 20/104/192
        Assert.False(lane.TryRemoveItemInRange(200, 220));
        Assert.Equal(new[] { 20, 20, 24 }, lane.Gaps);
    }

    [Fact]
    public void TryRemoveItemInRange_MiddleItem_StitchesGapsKeepingSurvivorsInPlace()
    {
        var lane = MakeThreeItemLane();
        Assert.True(lane.TryRemoveItemInRange(100, 110)); // 命中中间物品(前沿 104)
        Assert.Equal(new[] { 20, 108 }, lane.Gaps);        // 24 + 20 + 64
        Assert.Equal(new[] { 20, 192 }, lane.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles)); // 幸存物品没有移动
    }

    [Fact]
    public void TryRemoveItemInRange_LastItem_JustDropsIt()
    {
        var lane = MakeThreeItemLane();
        Assert.True(lane.TryRemoveItemInRange(190, 260));
        Assert.Equal(new[] { 20, 20 }, lane.Gaps);
    }

    [Fact]
    public void TryRemoveItemInRange_FrontItemWithNonZeroFrontGap_FoldsIntoNextGap()
    {
        var lane = MakeThreeItemLane();
        Assert.True(lane.TryRemoveItemInRange(0, 50));   // 命中最前物品(前沿 20)
        Assert.Equal(new[] { 104, 24 }, lane.Gaps);       // 20 + 20 + 64
    }

    [Fact]
    public void TryRemoveItemInRange_MatchesRemoveFront_InTheFrontZeroGapCase()
    {
        var a = new BeltLane(256);
        a.TryInsertAtBack(TestItem); a.Advance(100); a.TryInsertAtBack(TestItem); a.Advance(110); // gaps=[0,18]
        var b = new BeltLane(256);
        b.TryInsertAtBack(TestItem); b.Advance(100); b.TryInsertAtBack(TestItem); b.Advance(110); // gaps=[0,18]

        a.RemoveFront();
        Assert.True(b.TryRemoveItemInRange(0, 1));

        Assert.Equal(a.Gaps, b.Gaps); // 两者都是 [82]
    }

    [Fact]
    public void TryRemoveItemInRange_PullsCursorBackSoTrailingItemsAdvance()
    {
        var lane = BeltLane.FromAbsolutePositions(256, Positions(0, 64, 128, 192)); // gaps=[0,0,0,0]
        lane.Advance(1000); // 游标推到末尾,gaps 仍是 [0,0,0,0]

        Assert.True(lane.TryRemoveItemInRange(64, 65)); // 摘掉下标 1(前沿 64)
        // 缝合:_gaps[2] += _gaps[1] + 64 => [0,0,64,0],RemoveAt(1) => [0,64,0]

        lane.Advance(64); // 若游标没被收回,这个 64 gap 不会被消耗
        Assert.Equal(new[] { 0, 0, 0 }, lane.Gaps);
    }

    [Fact]
    public void TryRemoveItemInRange_TwoItemsInRange_RemovesFrontmost()
    {
        var lane = MakeThreeItemLane();                 // gaps=[20,20,24],前沿 20/104/192
        Assert.True(lane.TryRemoveItemInRange(0, 150)); // 范围包含前沿 20 和 104 的两个物品
        Assert.Equal(new[] { 104, 24 }, lane.Gaps);    // 20 + 20 + 64 = 104
        Assert.Equal(2, lane.Count);
        Assert.Equal(new[] { 104, 192 }, lane.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles)); // 幸存物品位置不变
    }

    [Fact]
    public void WriteState_SameGaps_SameHash_RegardlessOfInternalCursor()
    {
        var blocked = new BeltLane(256);
        for (int i = 0; i < 4; i++) { blocked.TryInsertAtBack(TestItem); blocked.Advance(1000); }
        // blocked.Gaps == [0,0,0,0],内部游标停在末尾

        var rebuilt = BeltLane.FromAbsolutePositions(256, Positions(0, 64, 128, 192));
        // rebuilt.Gaps == [0,0,0,0],游标在 0

        Assert.Equal(Hash(blocked), Hash(rebuilt));
    }

    [Fact]
    public void WriteState_DifferentGaps_DifferentHash()
    {
        var a = new BeltLane(256);
        a.TryInsertAtBack(TestItem);                 // gaps=[192]
        var b = new BeltLane(256);
        b.TryInsertAtBack(TestItem); b.Advance(50);  // gaps=[142]
        Assert.NotEqual(Hash(a), Hash(b));
    }

    [Fact]
    public void WriteState_EmptyLane_IsStable()
    {
        var lane = new BeltLane(256);
        Assert.Equal(Hash(lane), Hash(lane));
    }

    private static BeltLane MakeThreeItemLane()
    {
        // len 256,三个 64 宽物品,gap 20/20/24(和 64 + 物品体 192 = 256)
        return BeltLane.FromAbsolutePositions(256, Positions(20, 104, 192));
    }

    private static ulong Hash(BeltLane lane)
    {
        var w = new Fnv1aHashWriter();
        lane.WriteState(w);
        return w.Hash;
    }

    [Fact]
    public void ShrinkBack_TightensBackCapacity_WithoutMovingItems()
    {
        var lane = new BeltLane(512);
        lane.TryInsertAtBack(TestItem);   // gaps=[448]
        lane.Advance(300);         // gaps=[148],物品前沿 148、尾沿 212,队尾空 300
        lane.ShrinkBack(256);      // 512 -> 256:物品仍在 [148,212],放得下
        Assert.Equal(new[] { 148 }, lane.Gaps);
        Assert.False(lane.TryInsertAtBack(TestItem)); // 现在队尾只剩 256-(148+64)=44,插不下
    }

    [Fact]
    public void ShrinkBack_WouldStrandEntryItem_Throws()
    {
        var lane = new BeltLane(512);
        lane.TryInsertAtBack(TestItem);   // gaps=[448],物品尾沿贴在 512 入口,队尾空 0
        Assert.Throws<InvalidOperationException>(() => lane.ShrinkBack(256));
    }

    [Fact]
    public void ShrinkBack_BelowOneItemWidth_Throws()
    {
        var lane = new BeltLane(256);
        Assert.Throws<InvalidOperationException>(() => lane.ShrinkBack(200)); // 256-200=56 < 64
    }

    [Fact]
    public void ShrinkBack_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BeltLane(256).ShrinkBack(-1));
    }

    [Fact]
    public void ShrinkFront_MovesExitInward_TowardItems()
    {
        var lane = new BeltLane(512);
        lane.TryInsertAtBack(TestItem);   // gaps=[448]
        lane.ShrinkFront(256);     // 出口内移 256:gaps[0] 448-256=192,长度 256
        Assert.Equal(new[] { 192 }, lane.Gaps);
        Assert.Equal(new[] { 192 }, lane.ToAbsolutePositions().Select(p => p.LeadingEdgeSubTiles));
    }

    [Fact]
    public void ShrinkFront_WouldStrandFrontItem_Throws()
    {
        var lane = new BeltLane(512);
        lane.TryInsertAtBack(TestItem);
        lane.Advance(400);         // gaps=[48]
        Assert.Throws<InvalidOperationException>(() => lane.ShrinkFront(256)); // gaps[0]=48 < 256
    }

    [Fact]
    public void ShrinkFront_EmptyLane_JustShortens()
    {
        var lane = new BeltLane(512);
        lane.ShrinkFront(256);
        Assert.Equal(0, lane.Count);
        Assert.True(lane.TryInsertAtBack(TestItem));
        Assert.Equal(new[] { 192 }, lane.Gaps); // 256 - 64
    }

    [Fact]
    public void ShrinkFront_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BeltLane(256).ShrinkFront(-1));
    }

    [Fact]
    public void TryInsertAtBack_DifferentItems_FrontItemProtoIdTracksEachInTurn()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(10);
        lane.Advance(1000); // 推到出口
        Assert.True(lane.IsFrontReady);
        Assert.Equal(10, lane.FrontItemProtoId);

        lane.TryInsertAtBack(20); // 排在后面
        Assert.Equal(10, lane.FrontItemProtoId); // 队首还是第一个

        lane.RemoveFront();
        lane.Advance(1000);
        Assert.Equal(20, lane.FrontItemProtoId); // 现在轮到第二个
    }

    [Fact]
    public void ToAbsolutePositions_PreservesItemTypesInOrder()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(10);
        lane.Advance(100);
        lane.TryInsertAtBack(20);

        var positions = lane.ToAbsolutePositions();
        Assert.Equal(2, positions.Count);
        Assert.Equal(10, positions[0].ItemProtoId);
        Assert.Equal(20, positions[1].ItemProtoId);
    }

    [Fact]
    public void FromAbsolutePositions_RoundTripsItemTypes()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(10);
        lane.Advance(100);
        lane.TryInsertAtBack(20);

        var rebuilt = BeltLane.FromAbsolutePositions(256, lane.ToAbsolutePositions());
        var positions = rebuilt.ToAbsolutePositions();
        Assert.Equal(10, positions[0].ItemProtoId);
        Assert.Equal(20, positions[1].ItemProtoId);
    }
}
