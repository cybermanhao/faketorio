namespace Faketorio.Sim.Profiling;

/// Step() 每阶段前后回调。实现只做计时,绝不碰任何进 WriteState 的状态。
/// 同一 phase 一个 tick 内可被 Begin/End 多次(如 Machines 的 pre + post),累加。
/// 不嵌套:一对 Begin/End 之间不会再来同 phase 的 Begin。
public interface IStepProfiler
{
    void Begin(StepPhase phase);
    void End(StepPhase phase);
}
