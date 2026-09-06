using System.Diagnostics;
using System.Globalization;
using Faketorio.Sim.Bench.Scenario;
using Faketorio.Sim.Profiling;

namespace Faketorio.Sim.Bench;

/// Parsed benchmark CLI arguments. A record so tests can clone with `with`.
public sealed record BenchOptions(
    int Scale,
    int Ticks,
    int Warmup,
    int Iterations,
    string GoldenPath,
    string ReportPath,
    bool UpdateGolden,
    bool Json,
    bool SelfTest)
{
    public static BenchOptions Parse(string[] args)
    {
        int scale = ScenarioBuilder.DefaultScale;
        int ticks = ScenarioBuilder.DefaultTicks;
        int warmup = 1;
        int iterations = 5;
        string goldenPath = "bench/golden.json";
        string reportPath = "bench/bench-report.json";
        bool updateGolden = false;
        bool json = false;
        bool selfTest = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scale": scale = NextInt(args, ref i); break;
                case "--ticks": ticks = NextInt(args, ref i); break;
                case "--warmup": warmup = NextInt(args, ref i); break;
                case "--iterations": iterations = NextInt(args, ref i); break;
                case "--golden": goldenPath = NextArg(args, ref i); break;
                case "--report": reportPath = NextArg(args, ref i); break;
                case "--update-golden": updateGolden = true; break;
                case "--json": json = true; break;
                case "--selftest": selfTest = true; break;
                default: throw new ArgumentException($"unknown argument: {args[i]}");
            }
        }

        return new BenchOptions(
            scale, ticks, warmup, iterations, goldenPath, reportPath, updateGolden, json, selfTest);
    }

    private static string NextArg(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"missing value for {args[i]}");
        return args[++i];
    }

    private static int NextInt(string[] args, ref int i)
    {
        string raw = NextArg(args, ref i);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            throw new ArgumentException($"expected an integer for {args[i - 1]}, got '{raw}'");
        return v;
    }
}

/// The benchmark timing protocol, gate logic and exit codes. Fully testable —
/// never calls <c>Environment.Exit</c>; every path returns an exit code.
///
/// Exit codes: 0 PASS/SKIPPED · 1 HASH_FAIL · 2 PERF_FAIL · 3 setup failure
/// (bad golden / scenario sentinel) · 4 NONDETERMINISM · 5 selftest failure.
public static class BenchRunner
{
    public static int Run(BenchOptions opt, TextWriter stdout)
    {
        if (opt.SelfTest) return SelfTest(stdout);

        try
        {
            return RunCore(opt, stdout);
        }
        catch (GoldenFileException ex)
        {
            stdout.WriteLine($"bench: {ex.Message}");
            return 3;
        }
        catch (ScenarioSentinelException ex)
        {
            stdout.WriteLine($"bench: scenario sentinel tripped — {ex.Message}");
            return 3;
        }
    }

    private static int RunCore(BenchOptions opt, TextWriter stdout)
    {
        GoldenFile? golden =
            opt.UpdateGolden && !File.Exists(opt.GoldenPath)
                ? null
                : GoldenFile.Load(opt.GoldenPath);

        int[] sampleTicks = NormaliseSampleTicks(
            golden?.SampleTicks ?? new[] { 1000, 5000, 10000, opt.Ticks }, opt.Ticks);

        // 5. warmup — discarded.
        for (int w = 0; w < opt.Warmup; w++)
            RunOnce(opt.Scale, opt.Ticks, sampleTicks, null);

        // 6. timed iterations.
        var nsList = new List<long>(Math.Max(1, opt.Iterations));
        string? finalHash = null;
        Dictionary<int, string>? sampleHashes = null;
        bool nondeterministic = false;

        for (int it = 0; it < opt.Iterations; it++)
        {
            var run = RunOnce(opt.Scale, opt.Ticks, sampleTicks, null);
            nsList.Add(run.NsPerTick);
            if (!FoldHashes(ref finalHash, ref sampleHashes, run)) nondeterministic = true;
        }

        // 7. one profiled iteration — its hashes fold into the same equality check.
        var profiled = RunOnce(opt.Scale, opt.Ticks, sampleTicks, new StopwatchStepProfiler());
        if (!FoldHashes(ref finalHash, ref sampleHashes, profiled)) nondeterministic = true;
        nsList.Add(profiled.NsPerTick);

        var phaseNs = profiled.PhaseNs ?? new Dictionary<StepPhase, long>();
        finalHash ??= profiled.FinalHash;
        sampleHashes ??= profiled.SampleHashes;

        long minNs = nsList.Min();
        long medianNs = Median(nsList);

        bool gatesApply = golden != null && golden.Scale == opt.Scale && golden.Ticks == opt.Ticks;

        if (nondeterministic)
        {
            var ndReport = BuildReport(opt, minNs, medianNs, golden, "NONDETERMINISM",
                phaseNs, finalHash!, sampleHashes!);
            WriteReport(opt, ndReport, stdout);
            return 4;
        }

        // 10. --update-golden regenerates the baseline and always succeeds.
        if (opt.UpdateGolden)
        {
            var fresh = new GoldenFile
            {
                Scale = opt.Scale,
                Ticks = opt.Ticks,
                SampleTicks = sampleTicks,
                FinalHash = finalHash!,
                SampleHashes = ToStringKeyed(sampleHashes!),
                BaselineNsPerTick = minNs,
                PerfFailMultiplier = golden?.PerfFailMultiplier ?? 3.0,
                RecordedAtCommit = GitShortHead(),
                RecordedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };
            fresh.Save(opt.GoldenPath);
            var upReport = BuildReport(opt, minNs, medianNs, fresh, "SKIPPED",
                phaseNs, finalHash!, sampleHashes!);
            WriteReport(opt, upReport, stdout);
            return 0;
        }

        // 9. gates.
        string gate;
        int exit;
        if (!gatesApply)
        {
            gate = "SKIPPED";
            exit = 0;
        }
        else
        {
            var (hok, mism) = GoldenFile.Compare(golden!, finalHash!, sampleHashes!);
            if (!hok)
            {
                stdout.WriteLine($"bench: HASH_FAIL — {mism}");
                gate = "HASH_FAIL";
                exit = 1;
            }
            else if (golden!.BaselineNsPerTick > 0
                     && minNs > golden.BaselineNsPerTick * golden.PerfFailMultiplier)
            {
                stdout.WriteLine(
                    $"bench: PERF_FAIL — minNs {minNs} > baseline {golden.BaselineNsPerTick} * {golden.PerfFailMultiplier}");
                gate = "PERF_FAIL";
                exit = 2;
            }
            else
            {
                gate = "PASS";
                exit = 0;
            }
        }

        var report = BuildReport(opt, minNs, medianNs, golden, gate, phaseNs, finalHash!, sampleHashes!);
        WriteReport(opt, report, stdout);
        return exit;
    }

    // --- one measured pass -------------------------------------------------

    private readonly record struct OnceResult(
        long NsPerTick,
        string FinalHash,
        Dictionary<int, string> SampleHashes,
        IReadOnlyDictionary<StepPhase, long>? PhaseNs);

    private static OnceResult RunOnce(
        int scale, int ticks, int[] sampleTicks, StopwatchStepProfiler? profiler)
    {
        var built = ScenarioBuilder.Build(scale, ScenarioBuilder.WorldSeed, profiler);
        var wanted = new HashSet<int>(sampleTicks);
        var hashes = new Dictionary<int, string>();

        var sw = Stopwatch.StartNew();
        for (int k = 1; k <= ticks; k++)
        {
            built.Sim.Step();
            if (wanted.Contains(k))
                hashes[k] = GoldenFile.FormatHash(built.Sim.ComputeStateHash());
        }
        sw.Stop();

        string finalHash = GoldenFile.FormatHash(built.Sim.ComputeStateHash());
        long nsPerTick = ticks > 0 ? (long)(sw.Elapsed.TotalNanoseconds / ticks) : 0L;
        var phaseNs = profiler?.SnapshotNs();

        return new OnceResult(nsPerTick, finalHash, hashes, phaseNs);
    }

    private static bool FoldHashes(
        ref string? finalHash, ref Dictionary<int, string>? sampleHashes, in OnceResult run)
    {
        if (finalHash is null)
        {
            finalHash = run.FinalHash;
            sampleHashes = run.SampleHashes;
            return true;
        }

        if (!string.Equals(finalHash, run.FinalHash, StringComparison.Ordinal)) return false;
        if (sampleHashes!.Count != run.SampleHashes.Count) return false;
        foreach (var kv in run.SampleHashes)
            if (!sampleHashes.TryGetValue(kv.Key, out var v) || !string.Equals(v, kv.Value, StringComparison.Ordinal))
                return false;
        return true;
    }

    // --- helpers ---------------------------------------------------------

    private static int[] NormaliseSampleTicks(int[] raw, int ticks)
        => raw.Where(t => t > 0 && t <= ticks).Distinct().OrderBy(t => t).ToArray();

    private static long Median(List<long> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    private static Dictionary<string, string> ToStringKeyed(Dictionary<int, string> src)
    {
        var d = new Dictionary<string, string>(src.Count);
        foreach (var kv in src) d[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
        return d;
    }

    private static BenchReport BuildReport(
        BenchOptions opt,
        long minNs,
        long medianNs,
        GoldenFile? golden,
        string gate,
        IReadOnlyDictionary<StepPhase, long> phaseNs,
        string finalHash,
        Dictionary<int, string> sampleHashes)
    {
        long baseline = golden?.BaselineNsPerTick ?? 0L;
        double multiplier = golden?.PerfFailMultiplier ?? 0.0;

        // phaseNs is cumulative over the whole profiled iteration; report it per-tick
        // so the "ns" column shares units with minNsPerTick and the "%" column lands
        // in a sane 0–100-ish band (may sum slightly over 100: the profiled iteration
        // carries Stopwatch overhead and isn't the fastest of the set).
        int ticks = opt.Ticks > 0 ? opt.Ticks : 1;
        var phases = phaseNs
            .Select(kv =>
            {
                long perTickNs = kv.Value / ticks;
                return new PhaseCost(
                    kv.Key.ToString(),
                    perTickNs,
                    minNs > 0 ? perTickNs * 100.0 / minNs : 0.0);
            })
            .OrderByDescending(p => p.Ns)
            .ToArray();

        var hashes = ToStringKeyed(sampleHashes);
        hashes["final"] = finalHash;

        return new BenchReport
        {
            Scale = opt.Scale,
            Ticks = opt.Ticks,
            MinNsPerTick = minNs,
            MedianNsPerTick = medianNs,
            BaselineNsPerTick = baseline,
            Ratio = baseline > 0 ? minNs / (double)baseline : 0.0,
            PerfFailMultiplier = multiplier,
            Gate = gate,
            Phases = phases,
            Hashes = hashes,
            Commit = GitShortHead(),
            Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static void WriteReport(BenchOptions opt, BenchReport report, TextWriter stdout)
    {
        string json = report.ToJson();
        var dir = Path.GetDirectoryName(opt.ReportPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(opt.ReportPath, json);
        if (opt.Json) stdout.WriteLine(json);
        stdout.WriteLine(report.ToMarkdownSummary());
    }

    private static string GitShortHead()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return "unknown";
            string outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return string.IsNullOrEmpty(outp) ? "unknown" : outp;
        }
        catch
        {
            return "unknown";
        }
    }

    // --- selftest ------------------------------------------------------

    /// Exercises the exit-code semantics with a tiny scenario and synthetic
    /// goldens. Returns 0 if every assertion holds, 5 otherwise.
    public static int SelfTest(TextWriter stdout)
    {
        string golden = Path.Combine(Path.GetTempPath(), $"bench-selftest-g-{Guid.NewGuid():N}.json");
        string report = Path.Combine(Path.GetTempPath(), $"bench-selftest-r-{Guid.NewGuid():N}.json");

        var mk = new BenchOptions(
            Scale: 1, Ticks: 60, Warmup: 0, Iterations: 2,
            GoldenPath: golden, ReportPath: report,
            UpdateGolden: true, Json: false, SelfTest: false);

        try
        {
            // Baseline: write a correct golden.
            if (Run(mk, TextWriter.Null) != 0) return Fail(stdout, "update-golden run did not return 0");

            // (c) a correct golden verifies clean.
            if (Run(mk with { UpdateGolden = false }, TextWriter.Null) != 0)
                return Fail(stdout, "verify against correct golden did not return 0");

            // (a) a wrong FinalHash → HASH_FAIL → 1.
            var gWrong = GoldenFile.Load(golden);
            gWrong.FinalHash = "0xFFFFFFFFFFFFFFFF";
            gWrong.Save(golden);
            if (Run(mk with { UpdateGolden = false }, TextWriter.Null) != 1)
                return Fail(stdout, "corrupt-hash golden did not return 1");

            // (b) an impossibly low baseline → PERF_FAIL → 2.
            Run(mk, TextWriter.Null); // rewrite a correct golden
            var gPerf = GoldenFile.Load(golden);
            gPerf.BaselineNsPerTick = 1;
            gPerf.PerfFailMultiplier = 3.0;
            gPerf.Save(golden);
            if (Run(mk with { UpdateGolden = false }, TextWriter.Null) != 2)
                return Fail(stdout, "baseline=1 golden did not return 2");

            return 0;
        }
        finally
        {
            TryDelete(golden);
            TryDelete(report);
        }
    }

    private static int Fail(TextWriter stdout, string why)
    {
        stdout.WriteLine($"selftest FAILED: {why}");
        return 5;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
