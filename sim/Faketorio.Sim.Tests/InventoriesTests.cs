using Faketorio.Sim.Entities;
using Faketorio.Sim.Items;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class InventoriesTests
{
    private const int Iron = 100;
    private const int Stack = 50;

    [Fact]
    public void AddContainer_BindsEntity_GetReturnsRequestedSize()
    {
        var inv = new Inventories();
        var e = new EntityId(7, 1);
        var id = inv.AddContainer(e, 16);

        Assert.True(id.IsValid);
        Assert.Equal(id, inv.GetInventoryId(e));
        Assert.Equal(16, inv.Get(id).SlotCount);
    }

    [Fact]
    public void GetInventoryId_UnknownEntity_ReturnsInvalid()
    {
        var inv = new Inventories();
        Assert.Equal(InventoryId.Invalid, inv.GetInventoryId(new EntityId(3, 1)));
        Assert.False(inv.GetInventoryId(new EntityId(3, 1)).IsValid);
    }

    [Fact]
    public void RemoveContainer_DestroysInventory_ReturnsItemTotalAtRemoval()
    {
        var inv = new Inventories();
        var e = new EntityId(2, 1);
        var id = inv.AddContainer(e, 16);
        inv.Get(id).Insert(Iron, 70, Stack);   // 50 + 20 across two slots

        int returned = inv.RemoveContainer(e);

        Assert.Equal(70, returned);
        Assert.Equal(InventoryId.Invalid, inv.GetInventoryId(e));
        Assert.Throws<InvalidOperationException>(() => inv.Get(id));  // pool slot destroyed
    }

    [Fact]
    public void RemoveThenAddContainer_ReusesPoolSlot_BumpsGeneration()
    {
        var inv = new Inventories();
        var e1 = new EntityId(1, 1);
        var first = inv.AddContainer(e1, 16);
        inv.Get(first).Insert(Iron, 10, Stack);
        inv.RemoveContainer(e1);

        var e2 = new EntityId(4, 1);
        var second = inv.AddContainer(e2, 16);

        Assert.Equal(first.Index, second.Index);          // slot reused
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.Equal(0, inv.Get(second).TotalItems());    // fresh inventory, not the old contents
    }

    [Fact]
    public void ByEntity_GrowsToHighEntityIndex_EarlierUnboundEntitiesStayInvalid()
    {
        var inv = new Inventories(initialCapacity: 4);   // force a resize
        var high = new EntityId(500, 1);
        inv.AddContainer(high, 16);

        // an earlier index that was never bound must NOT look like it points at pool slot 0
        Assert.Equal(InventoryId.Invalid, inv.GetInventoryId(new EntityId(1, 1)));
        Assert.False(inv.GetInventoryId(new EntityId(300, 1)).IsValid);
        Assert.True(inv.GetInventoryId(high).IsValid);
    }

    private static ulong Hash(Inventories inv)
    {
        var w = new Fnv1aHashWriter();
        inv.WriteState(w);
        return w.Hash;
    }

    [Fact]
    public void WriteState_ChangesWhenBoundOwnerDiffers()
    {
        var a = new Inventories();
        a.AddContainer(new EntityId(1, 1), 4);
        var b = new Inventories();
        b.AddContainer(new EntityId(2, 1), 4);   // same inventory shape, different owner
        Assert.NotEqual(Hash(a), Hash(b));
    }

    [Fact]
    public void AddContainer_WithFilter_RejectsWrongItem_AcceptsRightItem()
    {
        var inv = new Inventories();
        var id = inv.AddContainer(new EntityId(1, 1), 1, filterItemProtoId: 42);
        var container = inv.Get(id);
        Assert.Equal(0, container.Insert(7, 5, 50));    // wrong item -> rejected
        Assert.Equal(5, container.Insert(42, 5, 50));   // right item -> accepted
    }

    [Fact]
    public void AddContainer_ReadOnly_RejectsAllInserts()
    {
        var inv = new Inventories();
        var id = inv.AddContainer(new EntityId(1, 1), 1, readOnly: true);
        Assert.Equal(0, inv.Get(id).Insert(42, 5, 50));
    }

    [Fact]
    public void AddContainer_DefaultArgs_Unfiltered_Writable()
    {
        var inv = new Inventories();
        var id = inv.AddContainer(new EntityId(1, 1), 1);   // 现有单参数调用形态不变
        Assert.Equal(5, inv.Get(id).Insert(42, 5, 50));
    }

    [Fact]
    public void AddContainer_DifferentRoles_AreIndependent()
    {
        var inv = new Inventories();
        var e = new EntityId(5, 1);
        var inputId = inv.AddContainer(e, 1, role: 1);
        var outputId = inv.AddContainer(e, 1, role: 2);

        inv.Get(inputId).Insert(Iron, Stack, Stack);   // fill role 1's one slot fully

        Assert.NotEqual(inputId, outputId);
        Assert.Equal(Stack, inv.Get(inputId).CountOf(Iron));
        Assert.Equal(0, inv.Get(outputId).CountOf(Iron));
        Assert.True(inv.Get(outputId).CanInsert(Iron, Stack, Stack));   // role 2 untouched by role 1 being full
    }

    [Fact]
    public void GetInventoryId_UnknownRole_ReturnsInvalid()
    {
        var inv = new Inventories();
        var e = new EntityId(6, 1);
        inv.AddContainer(e, 1, role: 1);

        Assert.False(inv.GetInventoryId(e, role: 2).IsValid);
        Assert.True(inv.GetInventoryId(e, role: 1).IsValid);
    }

    [Fact]
    public void RemoveContainer_WithRole_OnlyRemovesThatRole()
    {
        var inv = new Inventories();
        var e = new EntityId(7, 1);
        inv.AddContainer(e, 1, role: 1);
        var outputId = inv.AddContainer(e, 1, role: 2);

        inv.RemoveContainer(e, role: 1);

        Assert.False(inv.GetInventoryId(e, role: 1).IsValid);
        Assert.True(inv.GetInventoryId(e, role: 2).IsValid);
        Assert.Equal(outputId, inv.GetInventoryId(e, role: 2));
    }

    [Fact]
    public void DefaultRole_UnaffectedByOtherRolesOnSameEntity()
    {
        var inv = new Inventories();
        var e = new EntityId(8, 1);
        var defaultId = inv.AddContainer(e, 16);          // role 0, same as every pre-P9 call site
        inv.AddContainer(e, 1, role: 1);

        Assert.Equal(defaultId, inv.GetInventoryId(e));    // GetInventoryId(e) still means role 0
        Assert.Equal(defaultId, inv.GetInventoryId(e, role: 0));
    }
}
