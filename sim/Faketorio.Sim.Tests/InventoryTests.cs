using Faketorio.Sim.Items;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class InventoryTests
{
    private const int Iron = 100;   // 任意 item proto id — Inventory 是 prototype 无关的
    private const int Copper = 200;
    private const int Stack = 50;

    private static ulong Hash(Inventory inv)
    {
        var w = new Fnv1aHashWriter();
        inv.WriteState(w);
        return w.Hash;
    }

    [Fact]
    public void NewInventory_HasRequestedSlotCount_AllEmpty()
    {
        var inv = new Inventory(4);
        Assert.Equal(4, inv.SlotCount);
        for (int i = 0; i < inv.SlotCount; i++)
            Assert.True(inv[i].IsEmpty);
        Assert.Equal(ItemStack.Empty, inv[0]);
    }

    [Fact]
    public void Insert_IntoEmpty_FillsFirstSlot_ReturnsCount()
    {
        var inv = new Inventory(4);
        int put = inv.Insert(Iron, 10, Stack);
        Assert.Equal(10, put);
        Assert.Equal(new ItemStack(Iron, 10), inv[0]);
        Assert.True(inv[1].IsEmpty);
    }

    [Fact]
    public void Insert_TopsUpPartialSameTypeSlotBeforeEmptySlot()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 45, Stack);        // slot 0 -> 45
        int put = inv.Insert(Iron, 10, Stack);
        Assert.Equal(10, put);
        Assert.Equal(new ItemStack(Iron, 50), inv[0]);   // topped up to stackSize
        Assert.Equal(new ItemStack(Iron, 5), inv[1]);     // overflow to next slot
    }

    [Fact]
    public void Insert_CapsEachSlotAtStackSize()
    {
        var inv = new Inventory(4);
        int put = inv.Insert(Iron, 120, Stack);
        Assert.Equal(120, put);
        Assert.Equal(new ItemStack(Iron, 50), inv[0]);
        Assert.Equal(new ItemStack(Iron, 50), inv[1]);
        Assert.Equal(new ItemStack(Iron, 20), inv[2]);
    }

    [Fact]
    public void Insert_IntoFull_ReturnsOnlyWhatFit()
    {
        var inv = new Inventory(2);
        int put = inv.Insert(Iron, 250, Stack);   // capacity is 100
        Assert.Equal(100, put);
        Assert.Equal(100, inv.TotalItems());
    }

    [Fact]
    public void Insert_ReadOnly_ReturnsZero_NoStateChange()
    {
        var inv = new Inventory(4, readOnly: true);
        var h = Hash(inv);
        Assert.Equal(0, inv.Insert(Iron, 10, Stack));
        Assert.Equal(h, Hash(inv));
    }

    [Fact]
    public void Insert_FilterMismatch_ReturnsZero()
    {
        var inv = new Inventory(4, filterItemProtoId: Iron);
        Assert.Equal(0, inv.Insert(Copper, 10, Stack));
        Assert.Equal(10, inv.Insert(Iron, 10, Stack));
    }

    [Fact]
    public void Insert_NonPositiveCountOrStackSize_ReturnsZero_NoStateChange()
    {
        var inv = new Inventory(4);
        var h = Hash(inv);
        Assert.Equal(0, inv.Insert(Iron, -1, Stack));
        Assert.Equal(0, inv.Insert(Iron, 0, Stack));
        Assert.Equal(0, inv.Insert(Iron, 10, 0));      // stackSize 0: nothing fits, no mutation
        Assert.Equal(h, Hash(inv));
    }

    [Fact]
    public void Remove_PartialFromSingleSlot()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 30, Stack);
        int got = inv.Remove(Iron, 10);
        Assert.Equal(10, got);
        Assert.Equal(new ItemStack(Iron, 20), inv[0]);
    }

    [Fact]
    public void Remove_DrainsSlotToEmpty()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 10, Stack);
        int got = inv.Remove(Iron, 10);
        Assert.Equal(10, got);
        Assert.Equal(ItemStack.Empty, inv[0]);
        Assert.True(inv[0].IsEmpty);
    }

    [Fact]
    public void Remove_SpansMultipleSlots_FrontToBack()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 120, Stack);            // slots: 50 / 50 / 20
        int got = inv.Remove(Iron, 75);
        Assert.Equal(75, got);
        Assert.Equal(ItemStack.Empty, inv[0]);
        Assert.Equal(new ItemStack(Iron, 25), inv[1]);
        Assert.Equal(new ItemStack(Iron, 20), inv[2]);
    }

    [Fact]
    public void Remove_MoreThanPresent_ReturnsOnlyWhatWasThere()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 15, Stack);
        Assert.Equal(15, inv.Remove(Iron, 999));
        Assert.Equal(0, inv.TotalItems());
    }

    [Fact]
    public void Remove_WrongType_ReturnsZero()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 15, Stack);
        Assert.Equal(0, inv.Remove(Copper, 5));
        Assert.Equal(15, inv.CountOf(Iron));
    }

    [Fact]
    public void Remove_NonPositiveCount_ReturnsZero_NoStateChange()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 3, Stack);
        var h = Hash(inv);
        Assert.Equal(0, inv.Remove(Iron, -5));
        Assert.Equal(0, inv.Remove(Iron, 0));
        Assert.Equal(h, Hash(inv));
        Assert.Equal(3, inv.CountOf(Iron));   // 没有变成 3 - (-5)
    }

    [Fact]
    public void CountOf_SumsAcrossSlots()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 120, Stack);         // 50 / 50 / 20
        Assert.Equal(120, inv.CountOf(Iron));
        Assert.Equal(0, inv.CountOf(Copper));
    }

    [Fact]
    public void TotalItems_SumsEveryNonEmptySlot()
    {
        var inv = new Inventory(4);
        inv.Insert(Iron, 60, Stack);
        inv.Insert(Copper, 10, Stack);
        Assert.Equal(70, inv.TotalItems());
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var inv = new Inventory(2);
        Assert.Throws<IndexOutOfRangeException>(() => inv[5]);
    }

    [Fact]
    public void WriteState_SameSlotContents_SameHash()
    {
        var a = new Inventory(4);
        var b = new Inventory(4);
        a.Insert(Iron, 30, Stack);
        b.Insert(Iron, 30, Stack);
        Assert.Equal(Hash(a), Hash(b));
    }

    [Fact]
    public void WriteState_DifferentSlotContents_DifferentHash()
    {
        var a = new Inventory(4);
        var b = new Inventory(4);
        a.Insert(Iron, 30, Stack);
        b.Insert(Iron, 31, Stack);
        Assert.NotEqual(Hash(a), Hash(b));
    }

    [Fact]
    public void WriteState_DifferentSlotCount_DifferentHash()
    {
        Assert.NotEqual(Hash(new Inventory(4)), Hash(new Inventory(5)));
    }
}
