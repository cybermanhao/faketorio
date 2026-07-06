using Faketorio.Sim.Entities;
using Faketorio.Sim.World;

namespace Faketorio.Sim.Tests;

public class WorldGridTests
{
    private static readonly EntityId SomeId = new(7, 1);

    [Fact]
    public void EmptyTile_ReturnsInvalid()
        => Assert.Equal(EntityId.Invalid, new WorldGrid().GetEntityAt(5, 5));

    [Fact]
    public void OccupyArea_AllTilesReturnId()
    {
        var grid = new WorldGrid();
        grid.OccupyArea(10, 20, 2, 3, SomeId);
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                Assert.Equal(SomeId, grid.GetEntityAt(10 + dx, 20 + dy));
        Assert.Equal(EntityId.Invalid, grid.GetEntityAt(12, 20)); // footprint 外
    }

    [Fact]
    public void IsAreaFree_DetectsPartialOverlap()
    {
        var grid = new WorldGrid();
        grid.OccupyArea(0, 0, 2, 2, SomeId);
        Assert.False(grid.IsAreaFree(1, 1, 2, 2)); // 有重叠
        Assert.True(grid.IsAreaFree(2, 0, 2, 2));  // 相邻不重叠
    }

    [Fact]
    public void ClearArea_FreesTiles()
    {
        var grid = new WorldGrid();
        grid.OccupyArea(0, 0, 2, 2, SomeId);
        grid.ClearArea(0, 0, 2, 2);
        Assert.True(grid.IsAreaFree(0, 0, 2, 2));
    }

    [Fact]
    public void NegativeCoordinates_Work()
    {
        var grid = new WorldGrid();
        grid.OccupyArea(-33, -1, 2, 2, SomeId); // 跨 chunk 边界且为负
        Assert.Equal(SomeId, grid.GetEntityAt(-33, -1));
        Assert.Equal(SomeId, grid.GetEntityAt(-32, 0));
    }

    [Fact]
    public void SortedChunkKeys_IsSortedAndComplete()
    {
        var grid = new WorldGrid();
        grid.OccupyArea(100, 100, 1, 1, SomeId);
        grid.OccupyArea(-100, -100, 1, 1, SomeId);
        var keys = grid.SortedChunkKeys();
        Assert.Equal(2, keys.Count);
        Assert.True(keys[0] < keys[1]);
    }
}
