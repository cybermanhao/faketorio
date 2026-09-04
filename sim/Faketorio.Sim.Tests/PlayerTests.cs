using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class PlayerTests
{
    private static ulong Hash(Player p)
    {
        var w = new Fnv1aHashWriter();
        p.WriteState(w);
        return w.Hash;
    }

    [Fact]
    public void NewPlayer_AtOrigin_IdleWithEmptyInventory()
    {
        var p = new Player(10);
        Assert.Equal(0, p.X);
        Assert.Equal(0, p.Y);
        Assert.False(p.Walking);
        Assert.False(p.Mining);
        Assert.Empty(p.CraftQueue);
        Assert.Equal(10, p.Inventory.SlotCount);
        Assert.Equal(0, p.Inventory.TotalItems());
    }

    [Theory]
    [InlineData(0, 0, -5)]
    [InlineData(1, 5, -5)]
    [InlineData(2, 5, 0)]
    [InlineData(3, 5, 5)]
    [InlineData(4, 0, 5)]
    [InlineData(5, -5, 5)]
    [InlineData(6, -5, 0)]
    [InlineData(7, -5, -5)]
    public void WalkDelta_EightDirections(byte dir, int expectedDx, int expectedDy)
    {
        var (dx, dy) = Player.WalkDelta(dir, 5);
        Assert.Equal(expectedDx, dx);
        Assert.Equal(expectedDy, dy);
    }

    [Fact]
    public void MoveTo_UpdatesPosition_ChangesHash()
    {
        var p = new Player(10);
        var h = Hash(p);
        p.MoveTo(38, -76);
        Assert.Equal(38, p.X);
        Assert.Equal(-76, p.Y);
        Assert.NotEqual(h, Hash(p));
    }

    [Fact]
    public void WriteState_SensitiveToWalkAndInventory()
    {
        var a = new Player(10);
        var b = new Player(10);
        Assert.Equal(Hash(a), Hash(b));
        a.SetWalk(3);
        Assert.NotEqual(Hash(a), Hash(b));
        b.SetWalk(3);
        Assert.Equal(Hash(a), Hash(b));
        a.Inventory.Insert(100, 1, 50);
        Assert.NotEqual(Hash(a), Hash(b));
    }
}
