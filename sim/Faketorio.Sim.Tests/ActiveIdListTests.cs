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
}
