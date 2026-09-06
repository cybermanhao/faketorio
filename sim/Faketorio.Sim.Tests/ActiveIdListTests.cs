using Faketorio.Sim;
using Faketorio.Sim.Entities;

namespace Faketorio.Sim.Tests;

public class ActiveIdListTests
{
    private static int[] Idx(IReadOnlyList<EntityId> ids) => ids.Select(x => x.Index).ToArray();

    [Fact]
    public void Machines_ActiveIds_TracksRegisterUnregister_InIndexOrder()
    {
        var m = new Machines();
        m.RegisterMachine(new EntityId(5, 1));
        m.RegisterMachine(new EntityId(2, 1));
        m.RegisterMachine(new EntityId(8, 1));
        Assert.Equal(new[] { 2, 5, 8 }, Idx(m.ActiveIds));

        m.RegisterMachine(new EntityId(4, 1));
        Assert.Equal(new[] { 2, 4, 5, 8 }, Idx(m.ActiveIds));

        m.UnregisterMachine(new EntityId(5, 1));
        Assert.Equal(new[] { 2, 4, 8 }, Idx(m.ActiveIds));

        m.UnregisterMachine(new EntityId(999, 1));   // 没注册过
        Assert.Equal(new[] { 2, 4, 8 }, Idx(m.ActiveIds));

        // 复用槽位
        m.UnregisterMachine(new EntityId(4, 1));
        m.RegisterMachine(new EntityId(4, 3));
        Assert.Equal(new[] { 2, 4, 8 }, Idx(m.ActiveIds));
        Assert.Equal(3, m.ActiveIds.Single(x => x.Index == 4).Generation);
    }

    [Fact]
    public void MiningDrills_ActiveIds_TracksRegisterUnregister_InIndexOrder()
    {
        var d = new MiningDrills();
        d.RegisterDrill(new EntityId(7, 1));
        d.RegisterDrill(new EntityId(3, 1));
        d.RegisterDrill(new EntityId(7, 1));   // 重复,忽略
        Assert.Equal(new[] { 3, 7 }, Idx(d.ActiveIds));
        d.UnregisterDrill(new EntityId(3, 1));
        Assert.Equal(new[] { 7 }, Idx(d.ActiveIds));
        d.UnregisterDrill(new EntityId(42, 1));   // 没注册
        Assert.Equal(new[] { 7 }, Idx(d.ActiveIds));
    }

    [Fact]
    public void Inserters_ActiveIds_TracksRegisterUnregister_InIndexOrder()
    {
        var ins = new Inserters();
        ins.RegisterInserter(new EntityId(6, 1));
        ins.RegisterInserter(new EntityId(1, 1));
        ins.RegisterInserter(new EntityId(4, 1));
        Assert.Equal(new[] { 1, 4, 6 }, Idx(ins.ActiveIds));
        ins.UnregisterInserter(new EntityId(4, 1));
        Assert.Equal(new[] { 1, 6 }, Idx(ins.ActiveIds));
        ins.UnregisterInserter(new EntityId(4, 1));   // 再删一次,no-op
        Assert.Equal(new[] { 1, 6 }, Idx(ins.ActiveIds));
    }

    [Fact]
    public void ElectricGrid_GeneratorIds_TracksRegisterUnregister_InIndexOrder()
    {
        var g = new Faketorio.Sim.Electric.ElectricGrid();
        g.RegisterGenerator(new EntityId(9, 1));
        g.RegisterGenerator(new EntityId(3, 1));
        g.RegisterGenerator(new EntityId(6, 1));
        Assert.Equal(new[] { 3, 6, 9 }, Idx(g.GeneratorIds));
        g.UnregisterGenerator(new EntityId(6, 1));
        Assert.Equal(new[] { 3, 9 }, Idx(g.GeneratorIds));
        g.UnregisterGenerator(new EntityId(100, 1));   // 没注册
        Assert.Equal(new[] { 3, 9 }, Idx(g.GeneratorIds));
    }

    // Spec §6.1 set-equivalence invariant: 任意(真实 sim 可达的)操作序列后
    // HashSet(ActiveIds) == _states.Keys,且 Count 一致。
    // 每个容器跑同一条混合序列:乱序 register / 重复 register(no-op)/
    // 拆一个真的 / 拆一个从没注册的(no-op)/ 槽位复用(先拆再以新代数重注册)。
    [Fact]
    public void Containers_ActiveIds_StayInLockstepWithState()
    {
        // 期望最终仍注册的 id(Index 升序):1@1, 2@2(复用后), 5@1
        var expected = new HashSet<EntityId>
        {
            new(1, 1), new(2, 2), new(5, 1),
        };

        var m = new Machines();
        foreach (var id in new[] { new EntityId(5, 1), new EntityId(2, 1), new EntityId(8, 1), new EntityId(1, 1) })
            m.RegisterMachine(id);
        m.RegisterMachine(new EntityId(2, 1));          // 重复 -> no-op
        m.UnregisterMachine(new EntityId(8, 1));        // 拆真的
        m.UnregisterMachine(new EntityId(99, 1));       // 从没注册 -> no-op
        m.UnregisterMachine(new EntityId(2, 1));        // 槽位复用:先拆
        m.RegisterMachine(new EntityId(2, 2));          //           再以新代数重注册
        Assert.Equal(m.StateCount, m.ActiveIds.Count);
        Assert.True(new HashSet<EntityId>(m.ActiveIds).SetEquals(expected));

        var d = new MiningDrills();
        foreach (var id in new[] { new EntityId(5, 1), new EntityId(2, 1), new EntityId(8, 1), new EntityId(1, 1) })
            d.RegisterDrill(id);
        d.RegisterDrill(new EntityId(2, 1));
        d.UnregisterDrill(new EntityId(8, 1));
        d.UnregisterDrill(new EntityId(99, 1));
        d.UnregisterDrill(new EntityId(2, 1));
        d.RegisterDrill(new EntityId(2, 2));
        Assert.Equal(d.StateCount, d.ActiveIds.Count);
        Assert.True(new HashSet<EntityId>(d.ActiveIds).SetEquals(expected));

        var ins = new Inserters();
        foreach (var id in new[] { new EntityId(5, 1), new EntityId(2, 1), new EntityId(8, 1), new EntityId(1, 1) })
            ins.RegisterInserter(id);
        ins.RegisterInserter(new EntityId(2, 1));
        ins.UnregisterInserter(new EntityId(8, 1));
        ins.UnregisterInserter(new EntityId(99, 1));
        ins.UnregisterInserter(new EntityId(2, 1));
        ins.RegisterInserter(new EntityId(2, 2));
        Assert.Equal(ins.StateCount, ins.ActiveIds.Count);
        Assert.True(new HashSet<EntityId>(ins.ActiveIds).SetEquals(expected));

        var g = new Faketorio.Sim.Electric.ElectricGrid();
        foreach (var id in new[] { new EntityId(5, 1), new EntityId(2, 1), new EntityId(8, 1), new EntityId(1, 1) })
            g.RegisterGenerator(id);
        g.RegisterGenerator(new EntityId(2, 1));
        g.UnregisterGenerator(new EntityId(8, 1));
        g.UnregisterGenerator(new EntityId(99, 1));
        g.UnregisterGenerator(new EntityId(2, 1));
        g.RegisterGenerator(new EntityId(2, 2));
        Assert.Equal(g.GeneratorStateCount, g.GeneratorIds.Count);
        Assert.True(new HashSet<EntityId>(g.GeneratorIds).SetEquals(expected));
    }
}
