using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class InsertersTests
{
    [Fact]
    public void RegisterInserter_StartsEmptyHandZeroProgress()
    {
        var ins = new Inserters();
        var id = new EntityId(1, 1);
        ins.RegisterInserter(id);

        Assert.Equal(0, ins.GetHeldItemProtoId(id));
        Assert.Equal(0, ins.GetSwingProgress(id));
    }

    [Fact]
    public void UnregisteredEntity_FallsBackToDefaults()
    {
        var ins = new Inserters();
        var id = new EntityId(2, 1);

        Assert.Equal(0, ins.GetHeldItemProtoId(id));
        Assert.Equal(0, ins.GetSwingProgress(id));
    }

    [Fact]
    public void Grab_UnregisteredEntity_Throws()
    {
        var ins = new Inserters();
        Assert.Throws<KeyNotFoundException>(() => ins.Grab(new EntityId(3, 1), 42));
    }

    [Fact]
    public void AddSwing_UnregisteredEntity_Throws()
    {
        var ins = new Inserters();
        Assert.Throws<KeyNotFoundException>(() => ins.AddSwing(new EntityId(4, 1), 100));
    }

    [Fact]
    public void Release_UnregisteredEntity_Throws()
    {
        var ins = new Inserters();
        Assert.Throws<KeyNotFoundException>(() => ins.Release(new EntityId(5, 1)));
    }

    [Fact]
    public void ArriveAtPickup_UnregisteredEntity_Throws()
    {
        var ins = new Inserters();
        Assert.Throws<KeyNotFoundException>(() => ins.ArriveAtPickup(new EntityId(6, 1)));
    }

    [Fact]
    public void Grab_SetsHeldItem_ProgressZero()
    {
        var ins = new Inserters();
        var id = new EntityId(7, 1);
        ins.RegisterInserter(id);
        ins.AddSwing(id, 999);   // pretend it was mid-swing-back

        ins.Grab(id, 42);

        Assert.Equal(42, ins.GetHeldItemProtoId(id));
        Assert.Equal(0, ins.GetSwingProgress(id));
    }

    [Fact]
    public void AddSwing_Accumulates()
    {
        var ins = new Inserters();
        var id = new EntityId(8, 1);
        ins.RegisterInserter(id);
        ins.Grab(id, 42);

        ins.AddSwing(id, 30000);
        ins.AddSwing(id, 20000);

        Assert.Equal(50000, ins.GetSwingProgress(id));
    }

    [Fact]
    public void Release_ClearsHeld_PinsProgressAtHalfSwing()
    {
        var ins = new Inserters();
        var id = new EntityId(9, 1);
        ins.RegisterInserter(id);
        ins.Grab(id, 42);
        ins.AddSwing(id, Inserters.HalfSwing + 500);   // overshot the half-swing threshold

        ins.Release(id);

        Assert.Equal(0, ins.GetHeldItemProtoId(id));
        Assert.Equal(Inserters.HalfSwing, ins.GetSwingProgress(id));   // overshoot discarded
    }

    [Fact]
    public void ArriveAtPickup_ResetsProgressToZero_KeepsHandEmpty()
    {
        var ins = new Inserters();
        var id = new EntityId(10, 1);
        ins.RegisterInserter(id);
        ins.Grab(id, 42);
        ins.AddSwing(id, Inserters.HalfSwing);
        ins.Release(id);
        ins.AddSwing(id, Inserters.HalfSwing + 700);   // swung back past FullSwing

        ins.ArriveAtPickup(id);

        Assert.Equal(0, ins.GetSwingProgress(id));
        Assert.Equal(0, ins.GetHeldItemProtoId(id));
    }

    [Fact]
    public void UnregisterInserter_RemovesState()
    {
        var ins = new Inserters();
        var id = new EntityId(11, 1);
        ins.RegisterInserter(id);
        ins.Grab(id, 42);

        ins.UnregisterInserter(id);

        Assert.Equal(0, ins.GetHeldItemProtoId(id));
        Assert.Equal(0, ins.GetSwingProgress(id));
    }

    [Fact]
    public void WriteState_SortsByEntityIndex_RegistrationOrderDoesNotMatter()
    {
        var idHigh = new EntityId(9, 1);
        var idLow = new EntityId(2, 1);

        var a = new Inserters();
        a.RegisterInserter(idHigh);
        a.Grab(idHigh, 11);
        a.RegisterInserter(idLow);
        a.Grab(idLow, 22);
        var wa = new Fnv1aHashWriter();
        a.WriteState(wa);

        var b = new Inserters();
        b.RegisterInserter(idLow);
        b.Grab(idLow, 22);
        b.RegisterInserter(idHigh);
        b.Grab(idHigh, 11);
        var wb = new Fnv1aHashWriter();
        b.WriteState(wb);

        Assert.Equal(wa.Hash, wb.Hash);
    }
}
