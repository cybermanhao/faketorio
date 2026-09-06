using Faketorio.Sim;
using Faketorio.Sim.Entities;

namespace Faketorio.Sim.Tests;

public class OrderedEntityIdListTests
{
    private static int[] Indices(OrderedEntityIdList l)
        => l.Ids.Select(x => x.Index).ToArray();

    [Fact]
    public void Add_KeepsIndexAscending_RegardlessOfInsertOrder()
    {
        var l = new OrderedEntityIdList();
        l.Add(new EntityId(5, 1));
        l.Add(new EntityId(2, 1));
        l.Add(new EntityId(8, 1));
        Assert.Equal(new[] { 2, 5, 8 }, Indices(l));
        Assert.Equal(3, l.Count);

        l.Add(new EntityId(4, 1));   // 落在中间
        Assert.Equal(new[] { 2, 4, 5, 8 }, Indices(l));

        l.Add(new EntityId(1, 1));   // 落在最前
        Assert.Equal(new[] { 1, 2, 4, 5, 8 }, Indices(l));

        l.Add(new EntityId(9, 1));   // 落在最后
        Assert.Equal(new[] { 1, 2, 4, 5, 8, 9 }, Indices(l));
    }

    [Fact]
    public void Add_DuplicateIndex_IsIgnored()
    {
        var l = new OrderedEntityIdList();
        l.Add(new EntityId(3, 1));
        l.Add(new EntityId(3, 1));       // 完全相同
        l.Add(new EntityId(3, 7));       // 同 Index 不同 Generation —— 也当作已存在,忽略
        Assert.Equal(new[] { 3 }, Indices(l));
        Assert.Equal(1, l.Count);
        Assert.Equal(1, l.Ids[0].Generation);   // 保留最先加入的
    }

    [Fact]
    public void Remove_ByValue_LeavesOrderIntact()
    {
        var l = new OrderedEntityIdList();
        foreach (var i in new[] { 2, 5, 8 }) l.Add(new EntityId(i, 1));
        l.Remove(new EntityId(5, 1));
        Assert.Equal(new[] { 2, 8 }, Indices(l));
    }

    [Fact]
    public void Remove_Missing_IsNoOp()
    {
        var l = new OrderedEntityIdList();
        l.Add(new EntityId(2, 1));
        l.Remove(new EntityId(99, 1));           // 从没加过
        l.Remove(new EntityId(2, 4));            // Index 在,Generation 不符
        Assert.Equal(new[] { 2 }, Indices(l));
        Assert.Equal(1, l.Count);
    }

    [Fact]
    public void SlotReuse_NewGenerationReplacesOld()
    {
        var l = new OrderedEntityIdList();
        foreach (var i in new[] { 2, 4, 8 }) l.Add(new EntityId(i, 1));
        l.Remove(new EntityId(4, 1));            // 拆除
        l.Add(new EntityId(4, 3));              // 同槽位、新代数重建
        Assert.Equal(new[] { 2, 4, 8 }, Indices(l));
        Assert.Equal(3, l.Ids.Single(x => x.Index == 4).Generation);
    }
}
