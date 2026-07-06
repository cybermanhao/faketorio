using Faketorio.Sim.Entities;

namespace Faketorio.Sim.Tests;

public class EntityPoolTests
{
    private struct Dummy { public int Value; }

    [Fact]
    public void CreateThenGet_ReturnsValue()
    {
        var pool = new EntityPool<Dummy>();
        var id = pool.Create(new Dummy { Value = 42 });
        Assert.True(pool.IsAlive(id));
        Assert.Equal(42, pool.Get(id).Value);
    }

    [Fact]
    public void Destroy_MakesIdStale()
    {
        var pool = new EntityPool<Dummy>();
        var id = pool.Create(new Dummy { Value = 1 });
        pool.Destroy(id);
        Assert.False(pool.IsAlive(id));
    }

    [Fact]
    public void ReusedSlot_BumpsGeneration_StaleIdStaysDead()
    {
        var pool = new EntityPool<Dummy>();
        var a = pool.Create(new Dummy { Value = 1 });
        pool.Destroy(a);
        var b = pool.Create(new Dummy { Value = 2 });
        Assert.Equal(a.Index, b.Index);          // 槽位复用
        Assert.NotEqual(a.Generation, b.Generation);
        Assert.False(pool.IsAlive(a));           // 旧 ID 永久失效
        Assert.Equal(2, pool.Get(b).Value);
    }

    [Fact]
    public void Get_MutatesInPlace()
    {
        var pool = new EntityPool<Dummy>();
        var id = pool.Create(new Dummy { Value = 1 });
        pool.Get(id).Value = 99;
        Assert.Equal(99, pool.Get(id).Value);
    }

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        var pool = new EntityPool<Dummy>(initialCapacity: 2);
        for (int i = 0; i < 100; i++)
            pool.Create(new Dummy { Value = i });
        Assert.True(pool.Capacity >= 100);
    }
}
