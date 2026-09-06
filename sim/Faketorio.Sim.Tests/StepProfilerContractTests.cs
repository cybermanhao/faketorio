using System.Diagnostics;
using Faketorio.Sim.Bench;
using Faketorio.Sim.Profiling;

namespace Faketorio.Sim.Tests;

public class StepProfilerContractTests
{
    [Fact]
    public void SnapshotNs_HasAllEightPhases()
    {
        var p = new StopwatchStepProfiler();
        var snap = p.SnapshotNs();
        Assert.Equal(8, snap.Count);
        foreach (StepPhase phase in Enum.GetValues<StepPhase>())
            Assert.True(snap.ContainsKey(phase));
    }

    [Fact]
    public void Accumulates_AcrossMultipleIntervalsOfSamePhase()
    {
        var p = new StopwatchStepProfiler();
        for (int i = 0; i < 3; i++)
        {
            p.Begin(StepPhase.Machines);
            SpinWaitMicros(200);
            p.End(StepPhase.Machines);
        }
        Assert.True(p.SnapshotNs()[StepPhase.Machines] > 0);
        Assert.Equal(0, p.SnapshotNs()[StepPhase.Player]);
    }

    [Fact]
    public void Reset_ZeroesEverything()
    {
        var p = new StopwatchStepProfiler();
        p.Begin(StepPhase.Electric); SpinWaitMicros(100); p.End(StepPhase.Electric);
        p.Reset();
        Assert.Equal(0, p.SnapshotNs()[StepPhase.Electric]);
    }

    private static void SpinWaitMicros(long micros)
    {
        long ticks = micros * Stopwatch.Frequency / 1_000_000;
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetTimestamp() - start < ticks) { }
    }
}
