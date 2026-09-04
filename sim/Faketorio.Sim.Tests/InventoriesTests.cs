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
}
