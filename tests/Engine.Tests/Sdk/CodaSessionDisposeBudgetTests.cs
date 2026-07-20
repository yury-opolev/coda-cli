using Coda.Agent.Tasks;
using Coda.Sdk;
using Xunit;

namespace Engine.Tests.Sdk;

/// <summary>
/// Task 9.2: the synchronous <see cref="CodaSession.Dispose"/> must budget for the FULL async
/// teardown it drives — the TaskManager shutdown budget (running work + shell tree-kills) PLUS the
/// LSP shutdown timeout — so HTTP/logger/LSP disposal completes before the sync call returns, yet
/// stays bounded (never unbounded).
/// </summary>
public class CodaSessionDisposeBudgetTests
{
    [Fact]
    public void SyncDisposeBudget_CoversTaskShutdownPlusLspTimeout()
    {
        Assert.True(
            CodaSession.SyncDisposeBudget >= TaskManager.DefaultShutdownBudget + CodaSession.LspDisposeTimeout,
            $"sync dispose budget {CodaSession.SyncDisposeBudget} must cover task shutdown " +
            $"{TaskManager.DefaultShutdownBudget} + LSP timeout {CodaSession.LspDisposeTimeout}");
    }

    [Fact]
    public void SyncDisposeBudget_IsBounded()
    {
        // Bounded, not unbounded: the summed cap stays within a reasonable ceiling (~10s + margin).
        Assert.True(CodaSession.SyncDisposeBudget > TimeSpan.Zero);
        Assert.True(CodaSession.SyncDisposeBudget <= TimeSpan.FromSeconds(20));
    }
}
