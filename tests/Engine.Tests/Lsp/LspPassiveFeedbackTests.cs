using System.Text.Json.Nodes;
using Coda.Agent.Lsp;

namespace Engine.Tests.Lsp;

/// <summary>
/// Tests for LspPassiveFeedback: mapping publishDiagnostics params to DiagnosticFile[]
/// and routing notifications into the registry via a fake server (DuplexStreamPair).
/// </summary>
public sealed class LspPassiveFeedbackTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static async Task WaitForPendingAsync(LspDiagnosticRegistry registry, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (registry.PendingCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, ct);
        }
    }

    private static JsonObject MakeDiagnostic(string message, int severity, int startLine = 0, int startChar = 0)
    {
        return new JsonObject
        {
            ["message"] = message,
            ["severity"] = severity,
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = startLine, ["character"] = startChar },
                ["end"] = new JsonObject { ["line"] = startLine, ["character"] = startChar + 1 },
            },
        };
    }

    [Fact]
    public void Maps_severity_numbers_to_enum()
    {
        var parameters = new JsonObject
        {
            ["uri"] = "file:///x.ts",
            ["diagnostics"] = new JsonArray
            {
                MakeDiagnostic("e", 1),
                MakeDiagnostic("w", 2),
                MakeDiagnostic("i", 3),
                MakeDiagnostic("h", 4),
                new JsonObject
                {
                    ["message"] = "m",
                    ["range"] = new JsonObject
                    {
                        ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                        ["end"] = new JsonObject { ["line"] = 0, ["character"] = 1 },
                    },
                },
            },
        };

        var files = LspPassiveFeedback.FormatDiagnosticsForAttachment(parameters);

        var diagnostics = Assert.Single(files).Diagnostics;
        Assert.Equal(LspDiagnosticSeverity.Error, diagnostics[0].Severity);
        Assert.Equal(LspDiagnosticSeverity.Warning, diagnostics[1].Severity);
        Assert.Equal(LspDiagnosticSeverity.Info, diagnostics[2].Severity);
        Assert.Equal(LspDiagnosticSeverity.Hint, diagnostics[3].Severity);
        Assert.Equal(LspDiagnosticSeverity.Error, diagnostics[4].Severity);
    }

    [Fact]
    public void Converts_file_uri_to_path()
    {
        var parameters = new JsonObject
        {
            ["uri"] = "file:///tmp/project/main.ts",
            ["diagnostics"] = new JsonArray { MakeDiagnostic("e", 1) },
        };

        var files = LspPassiveFeedback.FormatDiagnosticsForAttachment(parameters);

        var uri = Assert.Single(files).Uri;
        Assert.DoesNotContain("file://", uri);
        Assert.Contains("main.ts", uri);
    }

    [Fact]
    public void Invalid_params_missing_uri_returns_empty()
    {
        var parameters = new JsonObject
        {
            ["diagnostics"] = new JsonArray { MakeDiagnostic("e", 1) },
        };

        Assert.Empty(LspPassiveFeedback.FormatDiagnosticsForAttachment(parameters));
    }

    [Fact]
    public void Invalid_params_missing_diagnostics_returns_empty()
    {
        var parameters = new JsonObject { ["uri"] = "file:///x.ts" };

        Assert.Empty(LspPassiveFeedback.FormatDiagnosticsForAttachment(parameters));
    }

    [Fact]
    public void Empty_diagnostics_produces_a_file_with_no_diagnostics()
    {
        var parameters = new JsonObject
        {
            ["uri"] = "file:///x.ts",
            ["diagnostics"] = new JsonArray(),
        };

        var files = LspPassiveFeedback.FormatDiagnosticsForAttachment(parameters);
        Assert.Empty(Assert.Single(files).Diagnostics);
    }

    [Fact]
    public void Code_number_is_stringified()
    {
        var diagnostic = MakeDiagnostic("e", 1);
        diagnostic["code"] = 2304;
        var parameters = new JsonObject
        {
            ["uri"] = "file:///x.ts",
            ["diagnostics"] = new JsonArray { diagnostic },
        };

        var files = LspPassiveFeedback.FormatDiagnosticsForAttachment(parameters);
        var code = Assert.Single(Assert.Single(files).Diagnostics).Code;
        Assert.Equal("2304", code);
    }

    [Fact]
    public void Code_string_is_preserved()
    {
        var diagnostic = MakeDiagnostic("e", 1);
        diagnostic["code"] = "TS2304";
        diagnostic["source"] = "ts";
        var parameters = new JsonObject
        {
            ["uri"] = "file:///x.ts",
            ["diagnostics"] = new JsonArray { diagnostic },
        };

        var parsed = Assert.Single(Assert.Single(LspPassiveFeedback.FormatDiagnosticsForAttachment(parameters)).Diagnostics);
        Assert.Equal("TS2304", parsed.Code);
        Assert.Equal("ts", parsed.Source);
    }

    [Fact]
    public async Task Handler_registered_on_each_server_routes_to_registry()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (manager, loop) = LspFakeServerHarness.BuildManager();
        await using var _ = manager;
        await using var __ = loop;

        // Passive feedback is the sole publishDiagnostics handler — only one handler is
        // kept per method, so we must not register a competing observer here.
        var registry = new LspDiagnosticRegistry();
        LspPassiveFeedback.RegisterNotificationHandlers(manager, registry);

        await manager
            .OpenFileAsync("/workspace/test.ts", "const x = 1;", cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        await manager
            .EnsureServerStartedAsync("/workspace/test.ts", cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        await manager
            .SaveFileAsync("/workspace/test.ts", cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        // The fake emits publishDiagnostics asynchronously after didSave; poll until routed.
        await WaitForPendingAsync(registry, cts.Token);
        Assert.True(registry.PendingCount >= 1);
    }

    [Fact]
    public async Task Invalid_notification_does_not_register_or_throw()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var (manager, loop) = LspFakeServerHarness.BuildManager();
        await using var _ = manager;
        await using var __ = loop;

        var registry = new LspDiagnosticRegistry();
        LspPassiveFeedback.RegisterNotificationHandlers(manager, registry);

        await manager
            .EnsureServerStartedAsync("/workspace/test.ts", cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        // Send a malformed publishDiagnostics (no uri/diagnostics) from the fake server.
        await loop
            .SendNotificationAsync(
                new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "textDocument/publishDiagnostics",
                    ["params"] = new JsonObject { ["garbage"] = true },
                },
                cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        await Task.Delay(150, cts.Token);
        Assert.Equal(0, registry.PendingCount);
    }
}
