using System.Collections.Immutable;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Settings;
using Coda.Common;
using Coda.Mcp;
using Coda.Tui;
using Coda.Tui.Commands;
using Coda.Tui.Mcp;
using Coda.Tui.Ui;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Mcp;
using Coda.Tui.Ui.Mode;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class CliTuiMcpSpecComplianceTests
{
    [Theory]
    [InlineData("verbose", ToolDisplayMode.Verbose)]
    [InlineData("compact", ToolDisplayMode.Compact)]
    [InlineData("tiny", ToolDisplayMode.Tiny)]
    public void Explicit_tool_modes_remain_unchanged(string raw, ToolDisplayMode expected)
    {
        var resolution = ToolDisplayModeResolver.Resolve(raw);

        Assert.Equal(expected, resolution.Mode);
        Assert.True(resolution.IsValid);
        Assert.Equal(raw, resolution.RawValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Missing_or_blank_tool_mode_defaults_to_summary(string? raw)
    {
        var resolution = ToolDisplayModeResolver.Resolve(raw);

        Assert.Equal(ToolDisplayMode.Summary, resolution.Mode);
        Assert.True(resolution.IsValid);
        Assert.Equal(raw, resolution.RawValue);
    }

    [Fact]
    public void Invalid_tool_mode_warns_falls_back_and_does_not_rewrite_settings()
    {
        using var directory = TestDirectory.Create();
        var settingsPath = Path.Combine(directory.Path, ".coda", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        const string original = """{"toolDisplayMode":"invalid","other":"preserve"}""";
        File.WriteAllText(settingsPath, original);

        var settings = SettingsLoader.Load(directory.Path, directory.Path);
        var resolution = ToolDisplayModeResolver.Resolve(settings.ToolDisplayMode);

        Assert.Equal(ToolDisplayMode.Summary, resolution.Mode);
        Assert.False(resolution.IsValid);
        Assert.Equal("Invalid toolDisplayMode 'invalid'; using summary.", ToolDisplayModeResolver.InvalidValueWarning(resolution.RawValue));
        Assert.Equal(original, File.ReadAllText(settingsPath));
    }

    [Theory]
    [InlineData("/mcp", true)]
    [InlineData(" /mcp ", true)]
    [InlineData("/MCP", false)]
    [InlineData("/mcp list", false)]
    public void Mcp_interception_is_exact_and_other_forms_remain_textual(string text, bool opens)
    {
        Assert.Equal(opens, McpBrowserController.IsOpenRequest(text));

        using var fixture = RetainedShellFixture.CreateWithMcpBrowser(activeWork: true);
        string? submitted = null;
        fixture.Shell.PromptSubmitted += (_, value) => submitted = value;
        fixture.Shell.Composer.SetDraft(text, text.Length);
        fixture.Shell.Composer.NewKeyDownEvent(Key.Enter);

        if (opens)
        {
            Assert.True(fixture.Shell.McpOverlay!.Visible);
            Assert.Null(submitted);
        }
        else
        {
            Assert.NotNull(submitted);
            Assert.StartsWith(text[..4], submitted, StringComparison.OrdinalIgnoreCase);
            Assert.False(fixture.Shell.McpOverlay!.Visible);
        }
    }

    [Fact]
    public async Task Physical_scopes_preserve_disabled_precedence_and_overridden_runtime_state()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteUser("""{"mcpServers":{"shared":{"type":"http","url":"https://user.test/mcp"}}}""");
        harness.WriteProject("""{"mcpServers":{"shared":{"type":"http","url":"https://project.test/mcp"}}}""");
        await harness.ConnectEffectiveAsync("shared");

        var result = await harness.Service.SetEnabledAsync(
            new McpServerKey(McpConfigScope.Project, "shared"),
            enabled: false,
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Snapshot.Servers.Length);
        Assert.False(result.Snapshot.Servers.Single(server => server.Key.Scope == McpConfigScope.Project).Enabled);
        Assert.Equal(
            McpConnectionState.Overridden,
            result.Snapshot.Servers.Single(server => server.Key.Scope == McpConfigScope.User).Connection);
        Assert.False(harness.Runtime.IsServerConnected("shared"));
    }

    [Fact]
    public async Task Adding_a_server_persists_without_connecting_it()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        var draft = new McpServerDraft(
            "new-server",
            McpConfigScope.Project,
            Enabled: true,
            McpTransportKind.Http,
            Command: null,
            Args: [],
            Url: "https://example.test/mcp",
            Environment: [],
            Headers: [],
            McpAuthMode.None,
            ClientId: null,
            Scopes: [],
            new McpSecretChange("auth/token", McpSecretChangeKind.Unchanged));

        var result = await harness.Service.CommitAddAsync(
            await harness.Service.PrepareAddAsync(draft, CancellationToken.None),
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Equal(0, harness.RuntimeFactory.ConnectCalls);
        Assert.Contains(
            McpConfig.LoadPhysicalEntries(harness.Project, harness.User),
            entry => entry.Key == new McpServerKey(McpConfigScope.Project, "new-server"));
    }

    [Fact]
    public async Task Only_destructive_mcp_actions_request_confirmation()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject("""{"mcpServers":{"server":{"type":"http","url":"https://example.test/mcp"}}}""");
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["no"], null));
        var controller = new McpBrowserController(() => new McpBrowserProvider(
            harness.Service,
            prompts,
            new PassiveIdleGate()));

        try
        {
            controller.Open();
            await controller.RefreshAsync(CancellationToken.None);
            await controller.ExecuteAsync(McpBrowserCommand.ToggleEnabled, null, CancellationToken.None);
            Assert.Empty(prompts.Requests);

            await controller.ExecuteAsync(McpBrowserCommand.DeleteServer, null, CancellationToken.None);
            Assert.Single(prompts.Requests);
            Assert.Equal(UiPromptKind.Confirm, prompts.Requests[0].Kind);
            Assert.Equal(0, harness.RuntimeFactory.ConnectCalls);
        }
        finally
        {
            controller.Close();
        }
    }

    [Theory]
    [InlineData(null, "persisted", "persisted")]
    [InlineData("exact", "persisted", "exact")]
    [InlineData("", "persisted", "")]
    public void Exact_prompt_precedence_preserves_empty_and_nonempty_overrides(
        string? startup,
        string? persisted,
        string? expected)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            using var session = NewSession(directory.FullName, startup);
            session.Resume(
                "session",
                [new ChatMessage(ChatRole.User, [new TextBlock("hello")])],
                new SessionMetadata { SystemPromptOverride = persisted });

            Assert.Equal(expected, session.Options.SystemPromptOverride);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Startup_prompt_sources_are_mutually_exclusive_with_concise_errors()
    {
        var parsed = TuiLaunchOptions.Parse(["--system-prompt", "one", "--system-prompt-file", "two"]);

        Assert.Equal("Specify only one of --system-prompt or --system-prompt-file, once.", parsed.Error);
        Assert.Null(parsed.SystemPromptSource);
    }

    [Fact]
    public void Summary_wording_keeps_failure_cancel_and_skipped_contracts_and_caps_children()
    {
        var calls = Enumerable.Range(0, 6)
            .Select(index => new ToolActivityCall(
                $"call-{index}",
                "root",
                "grep",
                """{"pattern":"safe"}""",
                "grep",
                ToolCallStatus.Running,
                null,
                null,
                null))
            .ToImmutableArray();
        var active = new ToolActivityTranscriptBlock(
            Guid.NewGuid(), "root", "activity", calls, ToolActivityCompletionState.Active);

        var activeLines = TranscriptBlockFormatter.Format(active, 120, ToolDisplayMode.Summary);
        Assert.Equal(6, activeLines.Count);
        Assert.Equal("`- ...and 2 more", activeLines[^1].Text);
        Assert.Equal(
            "Ran 4 tools - 1 failed, cancelled",
            ToolActivityPreview.CompletedText(new ToolActivitySummary("root", "activity", 4, 1, 1, 1, null)));
    }

    [Fact]
    public void Management_and_activity_outputs_redact_secrets_and_terminal_controls()
    {
        const string secret = "abc123456789012345678901234567890";
        const string escape = "\u001b[31m";
        var summary = new McpServerSummary(
            new McpServerKey(McpConfigScope.Project, "server"),
            @"C:\project\.mcp.json",
            Enabled: true,
            IsEffective: true,
            McpTransportKind.Http,
            McpConnectionState.Error,
            string.Concat("Authorization: Bearer ", secret, escape, "\r\n"));

        var management = McpView.FormatList(new McpManagementSnapshot(true, [summary]));
        const string apiKey = "sk-abcdefghijklmnopqrstuvwxyz012345";
        var ansiObfuscatedApiKey = string.Concat(apiKey[..6], escape, apiKey[6..]);
        var activity = ToolActivityPreview.Create(
            "run_command",
            JsonSerializer.Serialize(new
            {
                command = $"echo safe-context {ansiObfuscatedApiKey}\nnext",
                token = apiKey,
            }));

        Assert.DoesNotContain(secret, management, StringComparison.Ordinal);
        Assert.DoesNotContain(escape, management, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0007', management);
        Assert.StartsWith("$ echo safe-context ", activity, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, activity, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', activity);
        Assert.DoesNotContain('\u001b', activity);
        Assert.DoesNotContain(apiKey, activity, StringComparison.Ordinal);
    }

    private static CodaSession NewSession(string workingDirectory, string? systemPromptOverride)
    {
        var credentials = new CredentialManager(
            new InMemoryTokenStore(),
            [new ApiKeyProvider()]);
        return new CodaSession(credentials, new SessionOptions
        {
            ProviderId = ApiKeyProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = workingDirectory,
            SystemPromptOverride = systemPromptOverride,
        });
    }

    private sealed class PassiveIdleGate : IExclusiveIdleGate
    {
        public bool IsBusy => false;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public IDisposable TryAcquire() => new Lease();

        private sealed class Lease : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        private TestDirectory(string path) => this.Path = path;

        public string Path { get; }

        public static TestDirectory Create()
        {
            var path = System.IO.Path.Combine(
                Directory.GetCurrentDirectory(),
                "artifacts",
                "spec-compliance-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(this.Path))
            {
                Directory.Delete(this.Path, recursive: true);
            }
        }
    }
}
