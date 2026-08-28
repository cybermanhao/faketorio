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
}
