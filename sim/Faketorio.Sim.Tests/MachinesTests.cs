using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class MachinesTests
{
    [Fact]
    public void RegisterMachine_StartsWithNoRecipeZeroProgressNotCompleted()
    {
        var m = new Machines();
        var id = new EntityId(1, 1);
        m.RegisterMachine(id);

        Assert.Equal(-1, m.GetCurrentRecipe(id));
        Assert.Equal(0, m.GetProgress(id));
        Assert.False(m.IsCompleted(id));
    }

    [Fact]
    public void UnregisteredEntity_FallsBackToDefaults()
    {
        var m = new Machines();
        var id = new EntityId(2, 1);

        Assert.Equal(-1, m.GetCurrentRecipe(id));
        Assert.Equal(0, m.GetProgress(id));
        Assert.False(m.IsCompleted(id));
    }

    [Fact]
    public void SetRecipe_ResetsProgressAndCompleted()
    {
        var m = new Machines();
        var id = new EntityId(3, 1);
        m.RegisterMachine(id);
        m.SetRecipe(id, 1);
        m.AddProgress(id, 100);
        m.MarkCompleted(id);

        m.SetRecipe(id, 42);

        Assert.Equal(42, m.GetCurrentRecipe(id));
        Assert.Equal(0, m.GetProgress(id));
        Assert.False(m.IsCompleted(id));
    }

    [Fact]
    public void AddProgress_Accumulates()
    {
        var m = new Machines();
        var id = new EntityId(4, 1);
        m.RegisterMachine(id);
        m.SetRecipe(id, 7);

        m.AddProgress(id, 30);
        m.AddProgress(id, 12);

        Assert.Equal(42, m.GetProgress(id));
    }

    [Fact]
    public void MarkCompleted_KeepsRecipeAndProgress()
    {
        var m = new Machines();
        var id = new EntityId(5, 1);
        m.RegisterMachine(id);
        m.SetRecipe(id, 9);
        m.AddProgress(id, 1000);

        m.MarkCompleted(id);

        Assert.True(m.IsCompleted(id));
        Assert.Equal(9, m.GetCurrentRecipe(id));
        Assert.Equal(1000, m.GetProgress(id));
    }

    [Fact]
    public void RestartCycle_ClearRecipeTrue_ClearsEverything()
    {
        var m = new Machines();
        var id = new EntityId(6, 1);
        m.RegisterMachine(id);
        m.SetRecipe(id, 3);
        m.AddProgress(id, 500);
        m.MarkCompleted(id);

        m.RestartCycle(id, clearRecipe: true);

        Assert.Equal(-1, m.GetCurrentRecipe(id));
        Assert.Equal(0, m.GetProgress(id));
        Assert.False(m.IsCompleted(id));
    }

    [Fact]
    public void RestartCycle_ClearRecipeFalse_KeepsRecipeResetsProgress()
    {
        var m = new Machines();
        var id = new EntityId(7, 1);
        m.RegisterMachine(id);
        m.SetRecipe(id, 5);
        m.AddProgress(id, 500);
        m.MarkCompleted(id);

        m.RestartCycle(id, clearRecipe: false);

        Assert.Equal(5, m.GetCurrentRecipe(id));
        Assert.Equal(0, m.GetProgress(id));
        Assert.False(m.IsCompleted(id));
    }

    [Fact]
    public void UnregisterMachine_RemovesState()
    {
        var m = new Machines();
        var id = new EntityId(8, 1);
        m.RegisterMachine(id);
        m.SetRecipe(id, 1);

        m.UnregisterMachine(id);

        Assert.Equal(-1, m.GetCurrentRecipe(id));
        Assert.Equal(0, m.GetProgress(id));
        Assert.False(m.IsCompleted(id));
    }

    [Fact]
    public void SetRecipe_UnregisteredEntity_Throws()
    {
        var m = new Machines();
        var id = new EntityId(10, 1);

        Assert.Throws<KeyNotFoundException>(() => m.SetRecipe(id, 1));
    }

    [Fact]
    public void RestartCycle_UnregisteredEntity_Throws()
    {
        var m = new Machines();
        var id = new EntityId(11, 1);

        Assert.Throws<KeyNotFoundException>(() => m.RestartCycle(id, clearRecipe: true));
    }

    [Fact]
    public void WriteState_SortsByEntityIndex_RegistrationOrderDoesNotMatter()
    {
        var idHigh = new EntityId(9, 1);
        var idLow = new EntityId(2, 1);

        var m1 = new Machines();
        m1.RegisterMachine(idHigh);
        m1.SetRecipe(idHigh, 11);
        m1.RegisterMachine(idLow);
        m1.SetRecipe(idLow, 22);
        var w1 = new Fnv1aHashWriter();
        m1.WriteState(w1);

        var m2 = new Machines();
        m2.RegisterMachine(idLow);
        m2.SetRecipe(idLow, 22);
        m2.RegisterMachine(idHigh);
        m2.SetRecipe(idHigh, 11);
        var w2 = new Fnv1aHashWriter();
        m2.WriteState(w2);

        Assert.Equal(w1.Hash, w2.Hash);
    }

    [Fact]
    public void RegisterMachine_StartsAwake()
    {
        var m = new Machines();
        var id = new EntityId(20, 1);
        m.RegisterMachine(id);

        Assert.True(m.IsAwake(id));
        Assert.Contains(id, m.AwakeSnapshot());
        Assert.DoesNotContain(id, m.AsleepSnapshot());
    }

    [Fact]
    public void MarkAsleep_MovesFromAwakeToAsleep()
    {
        var m = new Machines();
        var id = new EntityId(21, 1);
        m.RegisterMachine(id);

        m.MarkAsleep(id);

        Assert.False(m.IsAwake(id));
        Assert.Contains(id, m.AsleepSnapshot());
        Assert.DoesNotContain(id, m.AwakeSnapshot());
    }

    [Fact]
    public void MarkAwake_MovesFromAsleepBackToAwake()
    {
        var m = new Machines();
        var id = new EntityId(22, 1);
        m.RegisterMachine(id);
        m.MarkAsleep(id);

        m.MarkAwake(id);

        Assert.True(m.IsAwake(id));
        Assert.Contains(id, m.AwakeSnapshot());
    }

    [Fact]
    public void MarkAsleep_AlreadyAsleep_IsNoOp()
    {
        var m = new Machines();
        var id = new EntityId(23, 1);
        m.RegisterMachine(id);
        m.MarkAsleep(id);

        m.MarkAsleep(id);   // 第二次不应该抛异常或重复插入

        Assert.Single(m.AsleepSnapshot());
    }

    [Fact]
    public void MarkAwake_AlreadyAwake_IsNoOp()
    {
        var m = new Machines();
        var id = new EntityId(24, 1);
        m.RegisterMachine(id);

        m.MarkAwake(id);   // 已经醒着,第二次调用不应该重复插入

        Assert.Single(m.AwakeSnapshot());
    }

    [Fact]
    public void MarkAwake_UnregisteredEntity_IsNoOp()
    {
        var m = new Machines();
        var id = new EntityId(25, 1);

        m.MarkAwake(id);   // 不应该抛异常——见 Simulation 里唤醒调用点可能对着
                            // 非机器实体调用的场景(比如 dropEntity 不是机器时)

        Assert.Empty(m.AwakeSnapshot());
        Assert.Empty(m.AsleepSnapshot());
    }

    [Fact]
    public void UnregisterMachine_RemovesFromAwakeOrAsleep()
    {
        var m = new Machines();
        var idAwake = new EntityId(26, 1);
        var idAsleep = new EntityId(27, 1);
        m.RegisterMachine(idAwake);
        m.RegisterMachine(idAsleep);
        m.MarkAsleep(idAsleep);

        m.UnregisterMachine(idAwake);
        m.UnregisterMachine(idAsleep);

        Assert.Empty(m.AwakeSnapshot());
        Assert.Empty(m.AsleepSnapshot());
    }

    [Fact]
    public void SafetyNetIntervalTicks_Is60()
    {
        Assert.Equal(60, Machines.SafetyNetIntervalTicks);
    }
}
