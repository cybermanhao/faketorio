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
}
