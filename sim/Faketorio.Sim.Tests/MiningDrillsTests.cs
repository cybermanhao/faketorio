using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class MiningDrillsTests
{
    [Fact]
    public void RegisterDrill_StartsWithNoTargetZeroProgressNotCompleted()
    {
        var d = new MiningDrills();
        var id = new EntityId(1, 1);
        d.RegisterDrill(id);

        Assert.Equal(-1, d.GetTargetX(id));
        Assert.Equal(-1, d.GetTargetY(id));
        Assert.Equal(0, d.GetProgress(id));
        Assert.False(d.IsCompleted(id));
        Assert.Equal(0, d.GetPendingItemProtoId(id));
    }

    [Fact]
    public void UnregisteredEntity_FallsBackToDefaults()
    {
        var d = new MiningDrills();
        var id = new EntityId(2, 1);

        Assert.Equal(-1, d.GetTargetX(id));
        Assert.Equal(-1, d.GetTargetY(id));
        Assert.Equal(0, d.GetProgress(id));
        Assert.False(d.IsCompleted(id));
    }

    [Fact]
    public void SetTarget_UnregisteredEntity_Throws()
    {
        var d = new MiningDrills();
        var id = new EntityId(3, 1);
        Assert.Throws<KeyNotFoundException>(() => d.SetTarget(id, 5, 6));
    }

    [Fact]
    public void AddProgress_UnregisteredEntity_Throws()
    {
        var d = new MiningDrills();
        var id = new EntityId(4, 1);
        Assert.Throws<KeyNotFoundException>(() => d.AddProgress(id, 100));
    }

    [Fact]
    public void MarkCompleted_UnregisteredEntity_Throws()
    {
        var d = new MiningDrills();
        var id = new EntityId(5, 1);
        Assert.Throws<KeyNotFoundException>(() => d.MarkCompleted(id, 42));
    }

    [Fact]
    public void SetTarget_ResetsProgress()
    {
        var d = new MiningDrills();
        var id = new EntityId(6, 1);
        d.RegisterDrill(id);
        d.SetTarget(id, 1, 1);
        d.AddProgress(id, 500);

        d.SetTarget(id, 2, 2);

        Assert.Equal(2, d.GetTargetX(id));
        Assert.Equal(2, d.GetTargetY(id));
        Assert.Equal(0, d.GetProgress(id));
    }

    [Fact]
    public void AddProgress_Accumulates()
    {
        var d = new MiningDrills();
        var id = new EntityId(7, 1);
        d.RegisterDrill(id);
        d.SetTarget(id, 1, 1);

        d.AddProgress(id, 30);
        d.AddProgress(id, 12);

        Assert.Equal(42, d.GetProgress(id));
    }

    [Fact]
    public void MarkCompleted_SetsCompletedAndPendingItem_KeepsTargetAndProgress()
    {
        var d = new MiningDrills();
        var id = new EntityId(8, 1);
        d.RegisterDrill(id);
        d.SetTarget(id, 3, 4);
        d.AddProgress(id, 1000);

        d.MarkCompleted(id, 99);

        Assert.True(d.IsCompleted(id));
        Assert.Equal(99, d.GetPendingItemProtoId(id));
        Assert.Equal(3, d.GetTargetX(id));
        Assert.Equal(4, d.GetTargetY(id));
        Assert.Equal(1000, d.GetProgress(id));
    }

    [Fact]
    public void ResetAfterFlush_ClearsCompletedAndProgress_SetsGivenTarget()
    {
        var d = new MiningDrills();
        var id = new EntityId(9, 1);
        d.RegisterDrill(id);
        d.SetTarget(id, 3, 4);
        d.AddProgress(id, 1000);
        d.MarkCompleted(id, 99);

        d.ResetAfterFlush(id, 3, 4); // 矿格没挖空,保留同一目标

        Assert.False(d.IsCompleted(id));
        Assert.Equal(0, d.GetProgress(id));
        Assert.Equal(0, d.GetPendingItemProtoId(id));
        Assert.Equal(3, d.GetTargetX(id));
        Assert.Equal(4, d.GetTargetY(id));
    }

    [Fact]
    public void ResetAfterFlush_WithInvalidTarget_ClearsTargetToo()
    {
        var d = new MiningDrills();
        var id = new EntityId(10, 1);
        d.RegisterDrill(id);
        d.SetTarget(id, 3, 4);
        d.MarkCompleted(id, 99);

        d.ResetAfterFlush(id, -1, -1); // 矿格挖空了

        Assert.Equal(-1, d.GetTargetX(id));
        Assert.Equal(-1, d.GetTargetY(id));
    }

    [Fact]
    public void UnregisterDrill_RemovesState()
    {
        var d = new MiningDrills();
        var id = new EntityId(11, 1);
        d.RegisterDrill(id);
        d.SetTarget(id, 1, 1);

        d.UnregisterDrill(id);

        Assert.Equal(-1, d.GetTargetX(id));
        Assert.Equal(0, d.GetProgress(id));
    }

    [Fact]
    public void WriteState_SortsByEntityIndex_RegistrationOrderDoesNotMatter()
    {
        var idHigh = new EntityId(9, 1);
        var idLow = new EntityId(2, 1);

        var d1 = new MiningDrills();
        d1.RegisterDrill(idHigh);
        d1.SetTarget(idHigh, 1, 1);
        d1.RegisterDrill(idLow);
        d1.SetTarget(idLow, 2, 2);
        var w1 = new Fnv1aHashWriter();
        d1.WriteState(w1);

        var d2 = new MiningDrills();
        d2.RegisterDrill(idLow);
        d2.SetTarget(idLow, 2, 2);
        d2.RegisterDrill(idHigh);
        d2.SetTarget(idHigh, 1, 1);
        var w2 = new Fnv1aHashWriter();
        d2.WriteState(w2);

        Assert.Equal(w1.Hash, w2.Hash);
    }
}
