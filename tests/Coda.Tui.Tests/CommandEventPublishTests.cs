using Coda.Sdk;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

/// <summary>
/// Verifies that state-mutating commands publish the right semantic events only after an accepted
/// mutation, that cost/diff/clear publish their events, and that the /context and /status commands
/// prefer the semantic snapshot sources — without duplicating console output.
/// </summary>
public sealed class CommandEventPublishTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_cmdevents_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ }
    }

    // ── metadata-mutating commands ───────────────────────────────────────────

    [Fact]
    public async Task Effort_set_publishes_metadata_with_effective_effort()
    {
        var (context, events, _) = this.Build();
        context.Session.Model = "claude-sonnet-4-6";

        await new EffortCommand().ExecuteAsync(context, ["high"], CancellationToken.None);

        var metadata = Single<SessionMetadataChangedEvent>(events);
        Assert.Equal("high", metadata.RequestedEffort);
        Assert.Equal("high", metadata.EffectiveEffort);
    }

    [Fact]
    public async Task Effort_invalid_argument_does_not_publish()
    {
        var (context, events, _) = this.Build();

        await new EffortCommand().ExecuteAsync(context, ["bogus"], CancellationToken.None);

        Assert.DoesNotContain(events.Events, e => e is SessionMetadataChangedEvent);
    }

    [Fact]
    public async Task Permissions_set_publishes_metadata()
    {
        var (context, events, _) = this.Build();

        await new PermissionsCommand().ExecuteAsync(context, ["acceptEdits"], CancellationToken.None);

        var metadata = Single<SessionMetadataChangedEvent>(events);
        Assert.Equal(Coda.Agent.PermissionMode.AcceptEdits, metadata.PermissionMode);
    }

    [Fact]
    public async Task Permissions_invalid_does_not_publish()
    {
        var (context, events, _) = this.Build();

        await new PermissionsCommand().ExecuteAsync(context, ["nope"], CancellationToken.None);

        Assert.DoesNotContain(events.Events, e => e is SessionMetadataChangedEvent);
    }

    [Fact]
    public async Task Yolo_publishes_metadata_in_bypass_mode()
    {
        var (context, events, _) = this.Build();

        await new YoloCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        var metadata = Single<SessionMetadataChangedEvent>(events);
        Assert.Equal(Coda.Agent.PermissionMode.BypassPermissions, metadata.PermissionMode);
    }

    [Fact]
    public async Task Model_set_publishes_metadata()
    {
        var (context, events, _) = this.Build();

        await new ModelCommand().ExecuteAsync(context, ["claude-sonnet-4-6"], CancellationToken.None);

        var metadata = Single<SessionMetadataChangedEvent>(events);
        Assert.Equal("claude-sonnet-4-6", metadata.Model);
    }

    [Fact]
    public async Task Fork_publishes_metadata_with_new_session_id()
    {
        var (context, events, _) = this.Build();
        context.Session.SessionId = "source-id";

        await new ForkCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        var metadata = Single<SessionMetadataChangedEvent>(events);
        Assert.Equal(context.Session.SessionId, metadata.SessionId);
        Assert.NotEqual("source-id", metadata.SessionId);
    }

    [Fact]
    public async Task Provider_connect_publishes_metadata_for_api_key_provider()
    {
        var (context, events, _) = this.Build();

        await new ProviderCommand().ExecuteAsync(context, ["anthropic-api-key"], CancellationToken.None);

        var metadata = Single<SessionMetadataChangedEvent>(events);
        Assert.Equal("anthropic-api-key", metadata.Provider);
    }

    [Fact]
    public async Task Provider_unknown_does_not_publish()
    {
        var (context, events, _) = this.Build();

        await new ProviderCommand().ExecuteAsync(context, ["does-not-exist"], CancellationToken.None);

        Assert.DoesNotContain(events.Events, e => e is SessionMetadataChangedEvent);
    }

    [Fact]
    public async Task Resume_existing_session_publishes_metadata()
    {
        var (context, events, _) = this.Build(workingDirectory: this.tempDir);
        await new SessionTranscriptStore(this.tempDir).SaveAsync(
            "saved-session",
            [new ChatMessage(ChatRole.User, [new TextBlock("hi")])]);

        await new ResumeCommand().ExecuteAsync(context, ["saved-session"], CancellationToken.None);

        var metadata = Single<SessionMetadataChangedEvent>(events);
        Assert.Equal("saved-session", metadata.SessionId);
    }

    [Fact]
    public async Task Resume_missing_session_does_not_publish()
    {
        var (context, events, _) = this.Build(workingDirectory: this.tempDir);

        await new ResumeCommand().ExecuteAsync(context, ["nope-missing"], CancellationToken.None);

        Assert.DoesNotContain(events.Events, e => e is SessionMetadataChangedEvent);
    }

    // ── /clear ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_publishes_transcript_cleared_and_metadata()
    {
        var (context, events, _) = this.Build();
        context.Session.SessionId = "old-id";

        await new ClearCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        var cleared = Single<TranscriptClearedEvent>(events);
        Assert.Equal(context.Session.SessionId, cleared.NewSessionId);
        Assert.Contains(events.Events, e => e is SessionMetadataChangedEvent);
    }

    [Fact]
    public async Task Clear_semantic_mode_does_not_render_banner()
    {
        var (context, _, console) = this.Build(semanticUi: true);

        await new ClearCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain("Welcome", console.Output);
    }

    [Fact]
    public async Task Clear_legacy_mode_renders_banner()
    {
        var (context, _, console) = this.Build(semanticUi: false);

        await new ClearCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("Welcome", console.Output);
    }

    // ── /cost ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cost_with_usage_publishes_estimate()
    {
        var (context, events, console) = this.Build();
        context.Session.Model = "claude-sonnet-4-6";
        context.Session.SessionUsage = new TokenUsage(1_000_000, 500_000);

        await new CostCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        var cost = Single<CostEstimateChangedEvent>(events);
        var expected = Pricing.EstimateUsd(
            context.Session.Model,
            context.Session.SessionUsage,
            ModelCatalog.Default.Get(context.Session.ActiveProviderId, context.Session.Model));
        Assert.Equal(expected, cost.EstimatedCost);
    }

    [Fact]
    public async Task Cost_without_usage_does_not_publish()
    {
        var (context, events, _) = this.Build();

        await new CostCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain(events.Events, e => e is CostEstimateChangedEvent);
    }

    // ── /diff ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Diff_semantic_publishes_event_and_skips_console()
    {
        this.InitGitRepoWithChange();
        var (context, events, console) = this.Build(semanticUi: true, workingDirectory: this.tempDir);

        await new DiffCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        var diff = Single<DiffOutputEvent>(events);
        Assert.Contains("changed line", diff.Patch);
        Assert.DoesNotContain("changed line", console.Output);
    }

    [Fact]
    public async Task Diff_legacy_writes_console_and_publishes_nothing()
    {
        this.InitGitRepoWithChange();
        var (context, events, console) = this.Build(semanticUi: false, workingDirectory: this.tempDir);

        await new DiffCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("changed line", console.Output);
        Assert.DoesNotContain(events.Events, e => e is DiffOutputEvent);
    }

    // ── /context ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Context_uses_snapshot_cache_when_present()
    {
        var report = new ContextReport
        {
            Model = "cache-model-zzz",
            MaxTokens = 200000,
            Categories = [new ContextCategory("Free space", 200000)],
            UsedTokens = 4242,
            IsExact = true,
            MessageCount = 3,
        };
        var cache = new ContextSnapshotCache(_ => Task.FromResult(report));
        var (context, _, console) = this.Build();
        context.ContextSnapshots = cache;

        await new ContextCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("cache-model-zzz", console.Output);
    }

    [Fact]
    public async Task Context_semantic_publishes_one_typed_usage_event_and_no_command_output()
    {
        var report = new ContextReport
        {
            Model = "semantic-context-model",
            MaxTokens = 200_000,
            Categories =
            [
                new ContextCategory("System prompt", 10_000),
                new ContextCategory("Messages", 30_000),
                new ContextCategory("Free space", 160_000),
            ],
            UsedTokens = 40_000,
            IsExact = true,
            MessageCount = 2,
        };
        var (context, events, console) = this.Build(semanticUi: true);
        context.ContextSnapshots = new ContextSnapshotCache(_ => Task.FromResult(report));

        await new ContextCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        var usage = Single<ContextUsageEvent>(events);
        Assert.Equal("semantic-context-model", usage.Usage.Model);
        Assert.Equal(40_000, usage.Usage.UsedTokens);
        Assert.DoesNotContain(events.Events, e => e is CommandOutputEvent);

        // The semantic path publishes the typed block and writes nothing generic to the console.
        Assert.Empty(console.Output);
    }

    [Fact]
    public async Task Context_legacy_writes_grid_to_console_and_publishes_no_usage_event()
    {
        var report = new ContextReport
        {
            Model = "legacy-context-model",
            MaxTokens = 200_000,
            Categories = [new ContextCategory("Free space", 200_000)],
            UsedTokens = 1_234,
            IsExact = true,
            MessageCount = 1,
        };
        var (context, events, console) = this.Build(semanticUi: false);
        context.ContextSnapshots = new ContextSnapshotCache(_ => Task.FromResult(report));

        await new ContextCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("legacy-context-model", console.Output);
        Assert.DoesNotContain(events.Events, e => e is ContextUsageEvent);
    }

    // ── /status ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_semantic_renders_snapshot_fields()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            Model = "semantic-model-xyz",
            EffectiveEffort = "high",
            Provider = "claude-ai",
        };
        var (context, _, console) = this.Build(snapshotProvider: () => snapshot);

        await new StatusCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("semantic-model-xyz", console.Output);
        Assert.Contains("high", console.Output);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static T Single<T>(RecordingUiEvents events)
        where T : UiEvent => Assert.Single(events.Events.OfType<T>());

    private (CommandContext Context, RecordingUiEvents Events, TestConsole Console) Build(
        bool semanticUi = false,
        Func<UiSessionSnapshot>? snapshotProvider = null,
        string? workingDirectory = null)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var credentials = new CredentialManager(
            store,
            new ICredentialProvider[] { new ClaudeAiProvider(), new GitHubCopilotProvider(), new ApiKeyProvider() });
        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
            new("github-copilot", "GitHub Copilot", LoginKind.DeviceCode, "gpt-4o"),
            new("anthropic-api-key", "Anthropic API key", LoginKind.ApiKey, "claude-sonnet-4-6"),
        };
        var events = new RecordingUiEvents();
        var session = new SessionState("claude-ai", workingDirectory ?? this.tempDir);
        var registry = new SlashCommandRegistry(Array.Empty<ISlashCommand>());
        var context = new CommandContext(
            console,
            credentials,
            session,
            providers,
            registry,
            events: events,
            uiSnapshotProvider: snapshotProvider,
            semanticUiEnabled: semanticUi);
        return (context, events, console);
    }

    private void InitGitRepoWithChange()
    {
        Git("init");
        Git("config user.email test@example.com");
        Git("config user.name Test");
        File.WriteAllText(Path.Combine(this.tempDir, "file.txt"), "original line\n");
        Git("add .");
        Git("commit -m init");
        File.WriteAllText(Path.Combine(this.tempDir, "file.txt"), "changed line\n");
    }

    private void Git(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = this.tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = args,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit(10_000);
    }
}
