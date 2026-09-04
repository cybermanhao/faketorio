using Faketorio.Sim.Items;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class InventoryPoolTests
{
    private static Inventory MakeInv() => new(4);

    [Fact]
    public void CreateThenGet_ReturnsSameInstance()
    {
        var pool = new InventoryPool();
        var inv = MakeInv();
        var id = pool.Create(inv);
        Assert.True(pool.IsAlive(id));
        Assert.Same(inv, pool.Get(id));
    }

    [Fact]
    public void Destroy_MakesIdStale()
    {
        var pool = new InventoryPool();
        var id = pool.Create(MakeInv());
        pool.Destroy(id);
        Assert.False(pool.IsAlive(id));
        Assert.Throws<InvalidOperationException>(() => pool.Get(id));
    }

    [Fact]
    public void ReusedSlot_BumpsGeneration_StaleIdStaysDead()
    {
        var pool = new InventoryPool();
        var a = pool.Create(MakeInv());
        pool.Destroy(a);
        var b = pool.Create(MakeInv());
        Assert.Equal(a.Index, b.Index);
        Assert.NotEqual(a.Generation, b.Generation);
        Assert.False(pool.IsAlive(a));
        Assert.True(pool.IsAlive(b));
    }

    [Fact]
    public void IndexOrderIteration_SeesLiveSlotsOnly()
    {
        var pool = new InventoryPool();
        var a = pool.Create(MakeInv());
        var b = pool.Create(MakeInv());
        var c = pool.Create(MakeInv());
        pool.Destroy(b);

        var seen = new List<int>();
        for (int i = 0; i < pool.Capacity; i++)
            if (pool.IsAliveAtIndex(i)) seen.Add(i);

        Assert.Equal(new[] { a.Index, c.Index }, seen);
    }

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        var pool = new InventoryPool(initialCapacity: 2);
        for (int i = 0; i < 50; i++) pool.Create(MakeInv());
        Assert.True(pool.Capacity >= 50);
    }

    private static ulong Hash(InventoryPool pool)
    {
        var w = new Fnv1aHashWriter();
        pool.WriteState(w);
        return w.Hash;
    }

    [Fact]
    public void WriteState_StableThenChangesWithAllocatorState()
    {
        var pool = new InventoryPool();
        var a = pool.Create(MakeInv());
        var b = pool.Create(MakeInv());
        pool.Destroy(a);
        pool.Destroy(b);

        var h1 = Hash(pool);
        Assert.Equal(h1, Hash(pool));

        pool.Create(MakeInv());
        Assert.NotEqual(h1, Hash(pool));
    }
}
