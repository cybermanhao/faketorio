using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

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

    private static ulong Hash(EntityPool<Dummy> pool)
    {
        var writer = new Fnv1aHashWriter();
        pool.WriteState(writer);
        return writer.Hash;
    }

    [Fact]
    public void WriteState_IsStableThenChangesWhenAllocatorStateChanges()
    {
        var pool = new EntityPool<Dummy>();
        var a = pool.Create(new Dummy { Value = 1 });
        var b = pool.Create(new Dummy { Value = 2 });
        pool.Destroy(a); // 填充空闲栈,推进死槽代数
        pool.Destroy(b);

        var hash1 = Hash(pool);
        var hash2 = Hash(pool); // 同一实例,无改动:必须稳定
        Assert.Equal(hash1, hash2);

        pool.Create(new Dummy { Value = 3 }); // 弹出空闲栈:_freeCount 与该槽代数都会变
        var hash3 = Hash(pool);
        Assert.NotEqual(hash1, hash3);
    }
}
