using System.Globalization;
using System.Text.Json;

namespace Faketorio.Sim.Bench;

/// Thrown when the pinned golden file is missing or cannot be parsed.
public sealed class GoldenFileException : Exception
{
    public GoldenFileException(string message) : base(message) { }
    public GoldenFileException(string message, Exception inner) : base(message, inner) { }
}

/// Load/save/compare for the pinned <c>bench/golden.json</c>.
/// Pure data — no scenario logic.
public sealed class GoldenFile
{
    public int Scale;
    public int Ticks;
    public int[] SampleTicks = Array.Empty<int>();
    public string FinalHash = "";
    public Dictionary<string, string> SampleHashes = new();
    public long BaselineNsPerTick;
    public double PerfFailMultiplier;
    public string RecordedAtCommit = "";
    public string RecordedAtUtc = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        IncludeFields = true,
    };

    public static GoldenFile Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new GoldenFileException($"golden file not found or unreadable: {path}", ex);
        }

        try
        {
            var g = JsonSerializer.Deserialize<GoldenFile>(json, JsonOptions);
            if (g is null)
                throw new GoldenFileException($"golden file parsed to null: {path}");
            return g;
        }
        catch (JsonException ex)
        {
            throw new GoldenFileException($"golden file unparseable: {path}", ex);
        }
    }

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    public static string FormatHash(ulong h) => "0x" + h.ToString("X16");

    public static ulong ParseHash(string s)
        => ulong.Parse(s.AsSpan(2), NumberStyles.HexNumber);

    /// Compare golden vs. an actual run. Sample ticks are checked in ascending
    /// numeric order, then the final hash. Returns <c>(false, "<which>")</c> on the
    /// first mismatch — the string names the offending tick number or "final" —
    /// or <c>(true, null)</c> if everything matches.
    public static (bool ok, string? firstMismatch) Compare(
        GoldenFile golden,
        string actualFinalHash,
        IReadOnlyDictionary<int, string> actualSampleHashes)
    {
        foreach (int tick in golden.SampleTicks.OrderBy(t => t))
        {
            golden.SampleHashes.TryGetValue(tick.ToString(CultureInfo.InvariantCulture), out var expected);
            actualSampleHashes.TryGetValue(tick, out var actual);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                return (false, $"tick {tick}: expected {expected ?? "<missing>"} got {actual ?? "<missing>"}");
        }

        if (!string.Equals(golden.FinalHash, actualFinalHash, StringComparison.Ordinal))
            return (false, $"final: expected {golden.FinalHash} got {actualFinalHash}");

        return (true, null);
    }
}
