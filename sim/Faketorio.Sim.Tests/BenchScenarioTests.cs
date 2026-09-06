using Faketorio.Sim.Bench;
using Faketorio.Sim.Bench.Scenario;

namespace Faketorio.Sim.Tests;

public class BenchScenarioTests
{
    [Fact]
    public void Options_Parse_DefaultsAndOverrides()
    {
        var d = BenchOptions.Parse(Array.Empty<string>());
        Assert.Equal(ScenarioBuilder.DefaultScale, d.Scale);
        Assert.Equal(ScenarioBuilder.DefaultTicks, d.Ticks);
        Assert.Equal(1, d.Warmup);
        Assert.Equal(3, d.Iterations);
        Assert.Equal("bench/golden.json", d.GoldenPath);
        Assert.Equal("bench/bench-report.json", d.ReportPath);
        Assert.False(d.UpdateGolden);
        Assert.False(d.Json);
        Assert.False(d.SelfTest);

        var o = BenchOptions.Parse(new[]
        {
            "--scale", "3", "--ticks", "100", "--iterations", "2", "--warmup", "0",
            "--golden", "g.json", "--report", "r.json", "--update-golden", "--json", "--selftest",
        });
        Assert.Equal(3, o.Scale);
        Assert.Equal(100, o.Ticks);
        Assert.Equal(2, o.Iterations);
        Assert.Equal(0, o.Warmup);
        Assert.Equal("g.json", o.GoldenPath);
        Assert.Equal("r.json", o.ReportPath);
        Assert.True(o.UpdateGolden);
        Assert.True(o.Json);
        Assert.True(o.SelfTest);

        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(new[] { "--bogus" }));
    }

    [Fact]
    public void SmallScale_HashSequence_IsStableAcrossRuns()
    {
        var built1 = ScenarioBuilder.Build(1);
        var built2 = ScenarioBuilder.Build(1);
        var h1 = new List<ulong>();
        var h2 = new List<ulong>();
        for (int t = 0; t < 800; t++)
        {
            built1.Sim.Step();
            h1.Add(built1.Sim.ComputeStateHash());
            built2.Sim.Step();
            h2.Add(built2.Sim.ComputeStateHash());
        }
        Assert.Equal(h1, h2);
        Assert.Equal(GOLDEN_TICK_800, h1[799]);
    }

    private const ulong GOLDEN_TICK_800 = 1494511041091785676UL; // pinned on first run

    [Fact]
    public void Runner_SelfTest_Passes()
        => Assert.Equal(0, BenchRunner.SelfTest(TextWriter.Null));

    [Fact]
    public void Runner_SmallScale_UpdateThenVerify_RoundTrips()
    {
        string golden = Path.Combine(Path.GetTempPath(), $"g-{Guid.NewGuid():N}.json");
        string report = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}.json");
        var opt = new BenchOptions(Scale: 1, Ticks: 60, Warmup: 0, Iterations: 2,
            GoldenPath: golden, ReportPath: report, UpdateGolden: true, Json: false, SelfTest: false);
        Assert.Equal(0, BenchRunner.Run(opt, TextWriter.Null));
        var verify = opt with { UpdateGolden = false };
        Assert.Equal(0, BenchRunner.Run(verify, TextWriter.Null));
        File.Delete(golden);
        File.Delete(report);
    }

    [Fact]
    public void Runner_HashMismatch_Returns1()
    {
        string golden = Path.Combine(Path.GetTempPath(), $"g-{Guid.NewGuid():N}.json");
        string report = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}.json");
        var mk = new BenchOptions(1, 60, 0, 2, golden, report, true, false, false);
        BenchRunner.Run(mk, TextWriter.Null);
        var g = GoldenFile.Load(golden);
        g.FinalHash = "0xFFFFFFFFFFFFFFFF";
        g.Save(golden);
        Assert.Equal(1, BenchRunner.Run(mk with { UpdateGolden = false }, TextWriter.Null));
        File.Delete(golden);
        File.Delete(report);
    }
}
