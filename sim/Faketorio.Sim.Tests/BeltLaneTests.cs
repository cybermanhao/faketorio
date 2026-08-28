using Faketorio.Sim.Belts;

namespace Faketorio.Sim.Tests;

public class BeltLaneTests
{
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
        Assert.True(lane.TryInsertAtBack());
        Assert.Equal(1, lane.Count);
        Assert.Equal(new[] { 192 }, lane.Gaps); // 256 - 64
    }

    [Fact]
    public void TryInsertAtBack_WhenNoRoom_ReturnsFalseAndDoesNotChangeState()
    {
        var lane = new BeltLane(256);
        Assert.True(lane.TryInsertAtBack());  // 单个物品刚好占满整条 256 长的 line
        Assert.False(lane.TryInsertAtBack()); // 队尾无空间
        Assert.Equal(1, lane.Count);
        Assert.Equal(new[] { 192 }, lane.Gaps);
    }

    [Fact]
    public void Advance_UnblockedSingleItem_MovesFrontGapForward()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(); // gaps=[192]
        lane.Advance(50);
        Assert.Equal(new[] { 142 }, lane.Gaps);
    }

    [Fact]
    public void Advance_NeverDrivesGapNegative_ClampsAtZero()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack(); // gaps=[192]
        lane.Advance(1000);      // 远超剩余 gap
        Assert.Equal(new[] { 0 }, lane.Gaps);
    }

    [Fact]
    public void Advance_CascadesIntoNextGapWhenFrontFullyCloses()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack();  // gaps=[192]
        lane.Advance(100);        // gaps=[92]
        lane.TryInsertAtBack();  // free=256-(92+64+64)=36 -> gaps=[92,36]
        lane.Advance(110);        // 92 耗尽 gaps[0],剩余 18 接着消耗 gaps[1]
        Assert.Equal(new[] { 0, 18 }, lane.Gaps);
    }

    [Fact]
    public void Advance_ContinuesFromCachedOpenIndexAfterFrontCloses()
    {
        var lane = new BeltLane(256);
        lane.TryInsertAtBack();
        lane.Advance(100);
        lane.TryInsertAtBack();
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
        lane.TryInsertAtBack();
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
            Assert.True(lane.TryInsertAtBack());
            lane.Advance(1000);
        }
        Assert.Equal(4, lane.Count);
        Assert.False(lane.TryInsertAtBack()); // 4*64=256,队尾无空间

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
            lane.TryInsertAtBack();
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
        lane.TryInsertAtBack();
        Assert.Throws<ArgumentOutOfRangeException>(() => lane.Advance(-1));
    }
}
