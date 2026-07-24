using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace Coda.Tui.Benchmarks;

/// <summary>
/// Entry point for the Coda TUI performance harness (Workstream 0).
///
/// Run everything (full statistical run):    dotnet run -c Release --project tests/Coda.Tui.Benchmarks
/// Run one class:                            dotnet run -c Release --project tests/Coda.Tui.Benchmarks -- --filter *Composer*
/// Fast CI smoke subset (few iterations):    dotnet run -c Release --project tests/Coda.Tui.Benchmarks -- --smoke
///
/// The smoke mode trades statistical rigor for speed so CI can catch gross regressions cheaply; the
/// full mode is authoritative for architectural decisions.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var smoke = args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);
        var passthrough = args.Where(a => !a.Equals("--smoke", StringComparison.OrdinalIgnoreCase)).ToArray();

        var config = smoke ? SmokeConfig() : DefaultConfig.Instance;

        var summaries = BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(passthrough, config);

        // Non-zero exit if any benchmark validation failed, so CI can gate on it.
        return summaries.Any(s => s.HasCriticalValidationErrors) ? 1 : 0;
    }

    /// <summary>A fast job for CI smoke runs: <see cref="Job.Dry"/> executes every benchmark exactly once,
    /// so CI catches build/measurement breakage (and grossly broken code paths) in seconds without paying
    /// for a full statistical run. Timings from smoke mode are indicative only — the full run is
    /// authoritative for regression gating.</summary>
    private static IConfig SmokeConfig() =>
        ManualConfig.CreateMinimumViable().AddJob(Job.Dry.WithId("Smoke"));
}
