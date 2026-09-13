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

    [Fact]
    public void RegisterDrill_StartsAwake()
    {
        var m = new MiningDrills();
        var id = new EntityId(1, 1);
        m.RegisterDrill(id);

        Assert.True(m.IsAwake(id));
        Assert.Contains(id, m.AwakeSnapshot());
        Assert.DoesNotContain(id, m.AsleepSnapshot());
    }

    [Fact]
    public void MarkAsleep_MovesFromAwakeToAsleep()
    {
        var m = new MiningDrills();
        var id = new EntityId(2, 1);
        m.RegisterDrill(id);

        m.MarkAsleep(id);

        Assert.False(m.IsAwake(id));
        Assert.Contains(id, m.AsleepSnapshot());
        Assert.DoesNotContain(id, m.AwakeSnapshot());
    }

    [Fact]
    public void MarkAwake_MovesFromAsleepBackToAwake()
    {
        var m = new MiningDrills();
        var id = new EntityId(3, 1);
        m.RegisterDrill(id);
        m.MarkAsleep(id);

        m.MarkAwake(id);

        Assert.True(m.IsAwake(id));
        Assert.Contains(id, m.AwakeSnapshot());
    }

    [Fact]
    public void MarkAsleep_AlreadyAsleep_IsNoOp()
    {
        var m = new MiningDrills();
        var id = new EntityId(4, 1);
        m.RegisterDrill(id);
        m.MarkAsleep(id);

        m.MarkAsleep(id);

        Assert.Single(m.AsleepSnapshot());
    }

    [Fact]
    public void MarkAwake_AlreadyAwake_IsNoOp()
    {
        var m = new MiningDrills();
        var id = new EntityId(5, 1);
        m.RegisterDrill(id);

        m.MarkAwake(id);

        Assert.Single(m.AwakeSnapshot());
    }

    [Fact]
    public void MarkAwake_UnregisteredEntity_IsNoOp()
    {
        var m = new MiningDrills();
        var id = new EntityId(6, 1);

        m.MarkAwake(id);   // 不应该抛异常

        Assert.Empty(m.AwakeSnapshot());
        Assert.Empty(m.AsleepSnapshot());
    }

    [Fact]
    public void UnregisterDrill_RemovesFromAwakeOrAsleep()
    {
        var m = new MiningDrills();
        var idAwake = new EntityId(7, 1);
        var idAsleep = new EntityId(8, 1);
        m.RegisterDrill(idAwake);
        m.RegisterDrill(idAsleep);
        m.MarkAsleep(idAsleep);

        m.UnregisterDrill(idAwake);
        m.UnregisterDrill(idAsleep);

        Assert.Empty(m.AwakeSnapshot());
        Assert.Empty(m.AsleepSnapshot());
    }

    [Fact]
    public void SafetyNetIntervalTicks_Is60()
        => Assert.Equal(60, MiningDrills.SafetyNetIntervalTicks);

    [Fact]
    public void RegisterBlockedOutputWaiter_ThenWakeWaitersAt_WakesUpAndClears()
    {
        var m = new MiningDrills();
        var id = new EntityId(10, 1);
        m.RegisterDrill(id);
        m.MarkAsleep(id);
        m.RegisterBlockedOutputWaiter(id, 5, 7);

        m.WakeWaitersAt(5, 7);

        Assert.True(m.IsAwake(id));
    }

    [Fact]
    public void WakeWaitersAt_MultipleWaitersSameCoordinate_WakesAll()
    {
        var m = new MiningDrills();
        var idA = new EntityId(11, 1);
        var idB = new EntityId(12, 1);
        m.RegisterDrill(idA);
        m.RegisterDrill(idB);
        m.MarkAsleep(idA);
        m.MarkAsleep(idB);
        m.RegisterBlockedOutputWaiter(idA, 3, 3);
        m.RegisterBlockedOutputWaiter(idB, 3, 3);

        m.WakeWaitersAt(3, 3);

        Assert.True(m.IsAwake(idA));
        Assert.True(m.IsAwake(idB));
    }

    [Fact]
    public void WakeWaitersAt_DifferentCoordinate_DoesNotWake()
    {
        var m = new MiningDrills();
        var id = new EntityId(13, 1);
        m.RegisterDrill(id);
        m.MarkAsleep(id);
        m.RegisterBlockedOutputWaiter(id, 1, 1);

        m.WakeWaitersAt(2, 2);   // 不同坐标

        Assert.False(m.IsAwake(id));
    }

    [Fact]
    public void WakeWaitersAt_NoWaiters_IsNoOp()
    {
        var m = new MiningDrills();
        m.WakeWaitersAt(99, 99);   // 不应该抛异常
    }

    [Fact]
    public void WakeWaitersAt_SameCoordinateCalledTwice_SecondCallIsNoOp()
    {
        // WakeWaitersAt 唤醒后要把 key 从表里移除——第二次调用同一坐标不应该
        // 再"唤醒"任何东西(此时列表已空,验证的是"不留残留状态"而不是具体行为)。
        var m = new MiningDrills();
        var id = new EntityId(14, 1);
        m.RegisterDrill(id);
        m.MarkAsleep(id);
        m.RegisterBlockedOutputWaiter(id, 4, 4);

        m.WakeWaitersAt(4, 4);
        m.MarkAsleep(id);   // 手动睡回去,模拟"唤醒后又堵住了"
        m.WakeWaitersAt(4, 4);   // 表已经空了,这次调用不应该再把它唤醒

        Assert.False(m.IsAwake(id));
    }

    [Fact]
    public void UnregisterDrill_RemovesFromBlockedOutputWaiters()
    {
        // 采矿机被卸载后,如果还挂在某个坐标的等待列表里,必须被清理掉——
        // 否则 WakeWaitersAt 会对着一个已经不存在状态的 EntityId 调 MarkAwake
        // (MarkAwake 本身对未注册 id 是空操作,不会崩溃,但列表会无限增长)。
        var m = new MiningDrills();
        var idStays = new EntityId(15, 1);
        var idRemoved = new EntityId(16, 1);
        m.RegisterDrill(idStays);
        m.RegisterDrill(idRemoved);
        m.MarkAsleep(idStays);
        m.MarkAsleep(idRemoved);
        m.RegisterBlockedOutputWaiter(idStays, 9, 9);
        m.RegisterBlockedOutputWaiter(idRemoved, 9, 9);

        m.UnregisterDrill(idRemoved);
        m.WakeWaitersAt(9, 9);

        Assert.True(m.IsAwake(idStays));
        // idRemoved 已卸载,IsAwake 对未注册 id 恒为 false,这里只验证 idStays 没受影响
        // ——UnregisterDrill 没有把整个 (9,9) 列表清空,只精确移除了 idRemoved 那一条。
    }
}
