using System.Diagnostics;
using Faketorio.Sim.Profiling;

namespace Faketorio.Sim.Bench;

/// IStepProfiler 的计时实现。每 phase 一个累加槽(Stopwatch tick 单位)。
/// 不嵌套 → 每 phase 一个 begin 时间戳槽即可。
public sealed class StopwatchStepProfiler : IStepProfiler
{
    private readonly long[] _accum = new long[8];
    private readonly long[] _begin = new long[8];

    public void Begin(StepPhase phase) => _begin[(int)phase] = Stopwatch.GetTimestamp();

    public void End(StepPhase phase) => _accum[(int)phase] += Stopwatch.GetTimestamp() - _begin[(int)phase];

    public void Reset() => Array.Clear(_accum);

    public IReadOnlyDictionary<StepPhase, long> SnapshotNs()
    {
        double nsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
        var d = new Dictionary<StepPhase, long>(8);
        foreach (StepPhase p in Enum.GetValues<StepPhase>())
            d[p] = (long)(_accum[(int)p] * nsPerTick);
        return d;
    }
}
