using Faketorio.Sim.Belts;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class BeltLinePoolTests
{
    private static BeltLine MakeLine(byte dir = 1)
        => new BeltLine(dir, new List<(int X, int Y)> { (0, 0) },
                        new BeltLane(BeltLine.TileSubTiles), new BeltLane(BeltLine.TileSubTiles));

    [Fact]
    public void CreateThenGet_ReturnsSameInstance()
    {
        var pool = new BeltLinePool();
        var line = MakeLine();
        var id = pool.Create(line);
        Assert.True(pool.IsAlive(id));
        Assert.Same(line, pool.Get(id));
    }

    [Fact]
    public void Destroy_MakesIdStale()
    {
        var pool = new BeltLinePool();
        var id = pool.Create(MakeLine());
        pool.Destroy(id);
        Assert.False(pool.IsAlive(id));
        Assert.Throws<InvalidOperationException>(() => pool.Get(id));
    }

    [Fact]
    public void ReusedSlot_BumpsGeneration_StaleIdStaysDead()
    {
        var pool = new BeltLinePool();
        var a = pool.Create(MakeLine());
        pool.Destroy(a);
        var b = pool.Create(MakeLine());
        Assert.Equal(a.Index, b.Index);          // 槽位复用
        Assert.NotEqual(a.Generation, b.Generation);
        Assert.False(pool.IsAlive(a));
        Assert.True(pool.IsAlive(b));
    }

    [Fact]
    public void IndexOrderIteration_SeesLiveSlotsOnly()
    {
        var pool = new BeltLinePool();
        var a = pool.Create(MakeLine());
        var b = pool.Create(MakeLine());
        var c = pool.Create(MakeLine());
        pool.Destroy(b);

        var seen = new List<int>();
        for (int i = 0; i < pool.Capacity; i++)
            if (pool.IsAliveAtIndex(i)) seen.Add(i);

        Assert.Equal(new[] { a.Index, c.Index }, seen);
    }

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        var pool = new BeltLinePool(initialCapacity: 2);
        for (int i = 0; i < 50; i++) pool.Create(MakeLine());
        Assert.True(pool.Capacity >= 50);
    }

    private static ulong Hash(BeltLinePool pool)
    {
        var w = new Fnv1aHashWriter();
        pool.WriteState(w);
        return w.Hash;
    }

    [Fact]
    public void WriteState_StableThenChangesWithAllocatorState()
    {
        var pool = new BeltLinePool();
        var a = pool.Create(MakeLine());
        var b = pool.Create(MakeLine());
        pool.Destroy(a);
        pool.Destroy(b);

        var h1 = Hash(pool);
        Assert.Equal(h1, Hash(pool));       // 无改动:稳定

        pool.Create(MakeLine());             // 弹空闲栈:_freeCount 与槽位代数都变
        Assert.NotEqual(h1, Hash(pool));
    }
}
