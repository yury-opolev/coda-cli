using Coda.Client;
using Coda.Client.Process;

namespace Coda.Client.Tests;

/// <summary>
/// The owned-process path has to actually spawn a child, keep its stdout for the
/// protocol, drain its stderr separately, and stop it within a bounded grace.
/// These tests use a trivial system command rather than a real engine — the
/// point is the process lifecycle and stream ownership, which are engine-agnostic
/// — so they stay deterministic and need no coda binary, provider or network.
/// </summary>
public sealed class CodaEngineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>A short-lived command that writes one line to stderr and exits,
    /// chosen per-OS so the test runs everywhere rather than being a silent no-op.</summary>
    private static EngineCommand DiagnosticCommand() =>
        OperatingSystem.IsWindows()
            ? EngineCommand.For("cmd.exe").WithArguments("/c", "echo diag 1>&2")
            : EngineCommand.For("/bin/sh").WithArguments("-c", "echo diag 1>&2");

    [Fact]
    public async Task An_owned_engine_drains_stderr_separately_and_reports_its_exit_code()
    {
        var engine = CodaEngine.Start(DiagnosticCommand());

        int exitCode = await engine.ShutdownAsync(Timeout).WaitAsync(Timeout);

        Assert.Equal(0, exitCode);
        // The diagnostic line reached the bounded stderr ring, not the protocol
        // stream.
        Assert.Contains(engine.RecentStderr(), line => line.Contains("diag"));

        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_a_launched_client_whose_engine_has_stopped_is_bounded_and_does_not_hang()
    {
        CodaClient client = CodaClient.Launch(
            DiagnosticCommand(),
            new CodaClientOptions { ReverseRequestHandler = ReverseRequestHandlers.Decline });

        // The child exits on its own; disposal must still complete promptly rather
        // than waiting forever on a process that is already gone.
        await client.DisposeAsync().AsTask().WaitAsync(Timeout);
    }
}
