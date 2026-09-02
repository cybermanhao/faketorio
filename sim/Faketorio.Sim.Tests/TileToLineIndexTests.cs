using Faketorio.Sim.Belts;

namespace Faketorio.Sim.Tests;

public class TileToLineIndexTests
{
    private static readonly BeltLineId A = new(3, 1);
    private static readonly BeltLineId B = new(4, 1);

    [Fact]
    public void Get_Unset_ReturnsInvalid()
        => Assert.Equal(BeltLineId.Invalid, new TileToLineIndex().Get(5, 5));

    [Fact]
    public void SetThenGet_RoundTrips()
    {
        var ix = new TileToLineIndex();
        ix.Set(10, 20, A);
        Assert.Equal(A, ix.Get(10, 20));
        Assert.Equal(BeltLineId.Invalid, ix.Get(11, 20)); // 邻格未设
    }

    [Fact]
    public void Set_Overwrites()
    {
        var ix = new TileToLineIndex();
        ix.Set(1, 1, A);
        ix.Set(1, 1, B);
        Assert.Equal(B, ix.Get(1, 1));
    }

    [Fact]
    public void NegativeAndCrossChunkCoordinates_Work()
    {
        var ix = new TileToLineIndex();
        ix.Set(-1, -1, A);
        ix.Set(-33, -33, B);   // 另一个 chunk,且为负
        Assert.Equal(A, ix.Get(-1, -1));
        Assert.Equal(B, ix.Get(-33, -33));
        Assert.Equal(BeltLineId.Invalid, ix.Get(-32, -32));
    }

    [Fact]
    public void Clear_RemovesEntry()
    {
        var ix = new TileToLineIndex();
        ix.Set(5, 5, new BeltLineId(2, 1));
        ix.Clear(5, 5);
        Assert.Equal(BeltLineId.Invalid, ix.Get(5, 5));
    }

    [Fact]
    public void Clear_UnsetTile_IsNoOp()
    {
        var ix = new TileToLineIndex();
        ix.Clear(9, 9); // 不抛
        Assert.Equal(BeltLineId.Invalid, ix.Get(9, 9));
    }
}
