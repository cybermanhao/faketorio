using Faketorio.Sim.McpServer;

namespace Faketorio.Sim.McpServer.Tests;

public class SimToolsTests
{
    // 每条测试跑之前，SimHost.Sim 可能残留上一条测试的状态——静态字段在同一个
    // 测试进程内跨测试共享。每条测试自己先调 ResetSimulation 建立已知状态，
    // 不依赖测试执行顺序或初始的 null 状态。

    [Fact]
    public void ResetSimulation_ReturnsTickZeroAndPositivePrototypeCount()
    {
        var result = SimTools.ResetSimulation(seed: 42);

        Assert.Equal(0, result.Tick);
        Assert.True(result.PrototypeCount > 0);
    }

    [Fact]
    public void GetTick_AfterReset_ReturnsZeroTickAndZeroRejected()
    {
        SimTools.ResetSimulation(seed: 1);

        var tick = SimTools.GetTick();

        Assert.Equal(0, tick.Tick);
        Assert.Equal(0, tick.RejectedCommandCount);
    }

    [Fact]
    public void GetTick_BeforeAnyReset_Throws()
    {
        // 这条测试依赖一个全新的、从未 reset 过的 SimHost.Sim 状态，但静态字段
        // 在测试进程内跨测试共享——用一个反射把 SimHost.Sim 显式设回 null，
        // 模拟"进程刚启动、还没人调过 reset_simulation"这个真实场景，不依赖
        // 测试执行顺序。
        typeof(SimHost).GetField(nameof(SimHost.Sim))!.SetValue(null, null);

        var ex = Assert.Throws<InvalidOperationException>(() => SimTools.GetTick());
        Assert.Equal(NotReadyError.Message, ex.Message);
    }
}
