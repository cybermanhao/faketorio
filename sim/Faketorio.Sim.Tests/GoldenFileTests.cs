using Faketorio.Sim.Bench;

namespace Faketorio.Sim.Tests;

public class GoldenFileTests
{
    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var g = new GoldenFile {
            Scale = 200, Ticks = 20000, SampleTicks = new[] { 1000, 20000 },
            FinalHash = "0x00000000DEADBEEF",
            SampleHashes = new() { ["1000"] = "0x0000000000000001", ["20000"] = "0x00000000DEADBEEF" },
            BaselineNsPerTick = 0, PerfFailMultiplier = 3.0,
            RecordedAtCommit = "abc1234", RecordedAtUtc = "2026-09-05T00:00:00Z",
        };
        string path = Path.Combine(Path.GetTempPath(), $"golden-{Guid.NewGuid():N}.json");
        g.Save(path);
        var back = GoldenFile.Load(path);
        File.Delete(path);
        Assert.Equal(200, back.Scale);
        Assert.Equal("0x00000000DEADBEEF", back.FinalHash);
        Assert.Equal(3.0, back.PerfFailMultiplier);
    }

    [Fact]
    public void FormatParseHash_RoundTrips()
    {
        ulong h = 0x1234_5678_9ABC_DEF0UL;
        Assert.Equal(h, GoldenFile.ParseHash(GoldenFile.FormatHash(h)));
    }

    [Fact]
    public void Compare_DetectsSampleMismatch()
    {
        var g = new GoldenFile {
            Scale = 1, Ticks = 10, SampleTicks = new[] { 5, 10 },
            FinalHash = "0x000000000000000A",
            SampleHashes = new() { ["5"] = "0x0000000000000005", ["10"] = "0x000000000000000A" },
            BaselineNsPerTick = 0, PerfFailMultiplier = 3.0, RecordedAtCommit = "x", RecordedAtUtc = "x",
        };
        var (ok, mismatch) = GoldenFile.Compare(g, "0x000000000000000A",
            new Dictionary<int,string> { [5] = "0x0000000000000099", [10] = "0x000000000000000A" });
        Assert.False(ok);
        Assert.Contains("5", mismatch);
    }

    [Fact]
    public void Load_MissingFile_Throws()
        => Assert.Throws<GoldenFileException>(() => GoldenFile.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".json")));
}
