using Faketorio.Sim.Entities;

namespace Faketorio.Sim.Tests;

public class OrderedEntityIdListTests
{
    [Fact]
    public void ToArray_ReturnsIndependentCopy_MutatingOriginalDoesNotAffectSnapshot()
    {
        var list = new OrderedEntityIdList();
        var a = new EntityId(1, 1);
        var b = new EntityId(2, 1);
        list.Add(a);
        list.Add(b);

        var snapshot = list.ToArray();
        list.Remove(a);
        list.Add(new EntityId(3, 1));

        Assert.Equal(new[] { a, b }, snapshot);   // 快照不受后续修改影响
        Assert.Equal(new[] { b, new EntityId(3, 1) }, list.Ids);
    }

    [Fact]
    public void ToArray_EmptyList_ReturnsEmptyArray()
    {
        var list = new OrderedEntityIdList();
        Assert.Empty(list.ToArray());
    }
}
