namespace Faketorio.Sim.Profiling;

/// Step() 内的计时阶段。顺序即 (int) 索引,勿重排。
public enum StepPhase
{
    Commands = 0,
    Player = 1,
    Electric = 2,
    Machines = 3,
    MiningDrills = 4,
    Inserters = 5,
    BeltAdvance = 6,
    BeltHandoff = 7,
}
