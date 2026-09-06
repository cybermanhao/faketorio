using Faketorio.Sim.Commands;
using Faketorio.Sim.Entities;
using Faketorio.Sim.Prototypes;
using Faketorio.Sim.Profiling;

namespace Faketorio.Sim.Tests;

public class StepProfilerWiringTests
{
    private sealed class RecordingProfiler : IStepProfiler
    {
        public readonly int[] Begins = new int[8];
        public readonly int[] Ends = new int[8];
        private readonly int[] _open = new int[8];
        public int MaxConcurrent;

        public void Begin(StepPhase p) { Begins[(int)p]++; _open[(int)p]++; MaxConcurrent = Math.Max(MaxConcurrent, _open[(int)p]); }
        public void End(StepPhase p) { Ends[(int)p]++; _open[(int)p]--; }
        public void ResetCounts() { Array.Clear(Begins); Array.Clear(Ends); Array.Clear(_open); MaxConcurrent = 0; }
    }

    private static Simulation NewSim(IStepProfiler? prof = null)
        => new(PrototypeLoader.LoadFromDirectory("data/base"), 0, prof);

    [Fact]
    public void RecordingProfiler_EveryPhaseBalancedEachTick()
    {
        var prof = new RecordingProfiler();
        var sim = NewSim(prof);
        int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        int belt = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id;
        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 0, Y = 0 });
        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = belt, X = 2, Y = 0, Rotation = 1 });
        sim.Step();                 // drain placement
        prof.ResetCounts();

        for (int t = 0; t < 5; t++) sim.Step();

        // 每个 phase 的 Begin/End 必须成对
        for (int p = 0; p < 8; p++)
            Assert.Equal(prof.Begins[p], prof.Ends[p]);
        Assert.True(prof.MaxConcurrent <= 1, "phase 不应嵌套/重入");

        // 每 tick 一次的 phase
        Assert.Equal(5, prof.Begins[(int)StepPhase.Commands]);
        Assert.Equal(5, prof.Begins[(int)StepPhase.Player]);
        Assert.Equal(5, prof.Begins[(int)StepPhase.BeltAdvance]);
        Assert.Equal(5, prof.Begins[(int)StepPhase.BeltHandoff]);

        // Electric 每 tick 两段(供给登记 / Settle+烧油),Machines/MiningDrills/Inserters
        // 每 tick 两段(pre-settle / post-settle)。5 ticks -> 10。
        Assert.Equal(10, prof.Begins[(int)StepPhase.Electric]);
        Assert.Equal(10, prof.Begins[(int)StepPhase.Machines]);
        Assert.Equal(10, prof.Begins[(int)StepPhase.MiningDrills]);
        Assert.Equal(10, prof.Begins[(int)StepPhase.Inserters]);
    }

    [Fact]
    public void ProfilerAttached_DoesNotChangeHash()
    {
        List<ulong> Run(bool withProfiler)
        {
            var sim = NewSim(withProfiler ? new RecordingProfiler() : null);
            int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
            var hashes = new List<ulong>();
            for (int t = 0; t < 30; t++)
            {
                if (t % 4 == 0)
                    sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = t, Y = 1 });
                sim.Step();
                hashes.Add(sim.ComputeStateHash());
            }
            return hashes;
        }
        Assert.Equal(Run(false), Run(true));
    }
}
