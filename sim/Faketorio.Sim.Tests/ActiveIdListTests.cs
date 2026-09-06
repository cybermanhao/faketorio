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
}
