using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Faketorio.Sim.Bench;

/// One phase's slice of a tick's cost.
public readonly record struct PhaseCost(string Phase, long Ns, double PctOfTick);

/// Per-run benchmark report POCO. Serialises to camelCase JSON and renders a
/// compact markdown block for the CI job summary. Pure data.
public sealed class BenchReport
{
    public int Scale;
    public int Ticks;
    public long MinNsPerTick;
    public long MedianNsPerTick;
    public long BaselineNsPerTick;
    public double Ratio;
    public double PerfFailMultiplier;

    /// One of "PASS" | "HASH_FAIL" | "PERF_FAIL" | "NONDETERMINISM" | "SKIPPED".
    public string Gate = "SKIPPED";
    public PhaseCost[] Phases = Array.Empty<PhaseCost>();
    public Dictionary<string, string> Hashes = new();
    public string Commit = "";
    public string Utc = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        IncludeFields = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public string ToMarkdownSummary()
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("**scale** ").Append(Scale.ToString(ic))
          .Append(" · **ticks** ").Append(Ticks.ToString(ic))
          .Append(" · **gate** ").Append(Gate).Append('\n');
        sb.Append("**minNsPerTick** ").Append(MinNsPerTick.ToString(ic))
          .Append(" · **baseline** ").Append(BaselineNsPerTick.ToString(ic))
          .Append(" · **ratio** ").Append(Ratio.ToString("0.###", ic)).Append('\n');
        sb.Append('\n');
        sb.Append("| phase | ns | % |\n");
        sb.Append("| --- | ---: | ---: |\n");
        foreach (var p in Phases)
        {
            sb.Append("| ").Append(p.Phase)
              .Append(" | ").Append(p.Ns.ToString(ic))
              .Append(" | ").Append(p.PctOfTick.ToString("0.#", ic))
              .Append(" |\n");
        }
        return sb.ToString();
    }
}
