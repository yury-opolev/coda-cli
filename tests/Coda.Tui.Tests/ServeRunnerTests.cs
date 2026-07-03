using Coda.Agent;
using Coda.Agent.Settings;
using Coda.Mcp;
using Coda.Sdk;
using Coda.Sdk.Serve;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace Coda.Tui.Tests;

/// <summary>
/// Light tests for the <c>coda serve</c> subcommand: option parsing and
/// the BuildHost seam (verifies the wiring builds a <see cref="ServeHost"/>
/// without touching real stdin/stdout or real auth).
/// </summary>
public sealed class ServeRunnerTests
{
    // ── ServeOptions parsing ──────────────────────────────────────────────

    [Fact]
    public void Parse_empty_args_defaults_working_directory_but_not_provider_or_model()
    {
        // Hermetic: no settings file. Working directory always has a sensible default,
        // but provider/model are NOT invented when unconfigured (no built-in fallback).
        using var home = new TempSettingsHome(settingsJson: null);

        var options = ServeRunner.Parse(["--cwd", home.Root], home.Root);

        Assert.Equal(home.Root, options.WorkingDirectory);
        Assert.Null(options.ProviderId);
        Assert.Null(options.Model);
    }

    [Fact]
    public void Parse_model_flag_sets_model()
    {
        var options = ServeRunner.Parse(["--model", "claude-x"]);

        Assert.Equal("claude-x", options.Model);
    }

    [Fact]
    public void Parse_provider_flag_sets_provider()
    {
        var options = ServeRunner.Parse(["--provider", "anthropic-api-key"]);

        Assert.Equal("anthropic-api-key", options.ProviderId);
    }

    [Fact]
    public void Parse_cwd_flag_sets_working_directory()
    {
        var options = ServeRunner.Parse(["--cwd", "C:\\Temp"]);

        Assert.Equal("C:\\Temp", options.WorkingDirectory);
    }

    [Fact]
    public void Parse_permission_mode_bypass_sets_bypass()
    {
        var options = ServeRunner.Parse(["--permission-mode", "bypass"]);

        Assert.Equal(PermissionMode.BypassPermissions, options.PermissionMode);
    }

    [Fact]
    public void Parse_yolo_flag_sets_bypass_mode()
    {
        var options = ServeRunner.Parse(["--yolo"]);

        Assert.Equal(PermissionMode.BypassPermissions, options.PermissionMode);
    }

    [Fact]
    public void Parse_yolo_safe_flag_sets_bypass_and_classifier()
    {
        var options = ServeRunner.Parse(["--yolo-safe"]);

        Assert.Equal(PermissionMode.BypassPermissions, options.PermissionMode);
        Assert.True(options.EnableClassifier);
    }

    [Fact]
    public void Parse_permission_mode_yolo_safe_sets_bypass_and_classifier()
    {
        var options = ServeRunner.Parse(["--permission-mode", "yolo-safe"]);

        Assert.Equal(PermissionMode.BypassPermissions, options.PermissionMode);
        Assert.True(options.EnableClassifier);
    }

    [Fact]
    public void Parse_plain_yolo_does_not_enable_classifier()
    {
        var options = ServeRunner.Parse(["--yolo"]);

        Assert.Equal(PermissionMode.BypassPermissions, options.PermissionMode);
        Assert.False(options.EnableClassifier);
    }

    [Fact]
    public void Parse_multiple_flags_are_all_applied()
    {
        var options = ServeRunner.Parse(["--model", "my-model", "--provider", "claude", "--cwd", "/tmp"]);

        Assert.Equal("my-model", options.Model);
        Assert.Equal("/tmp", options.WorkingDirectory);
    }

    // ── MCP enable/disable parsing ────────────────────────────────────────

    [Fact]
    public void Parse_enable_mcp_defaults_to_true()
    {
        var options = ServeOptions.Parse([]);
        Assert.True(options.EnableMcp);
    }

    [Fact]
    public void Parse_no_mcp_flag_disables_mcp()
    {
        var options = ServeOptions.Parse(["--no-mcp"]);
        Assert.False(options.EnableMcp);
    }

    [Fact]
    public void Parse_mcp_flag_keeps_mcp_enabled()
    {
        var options = ServeOptions.Parse(["--mcp"]);
        Assert.True(options.EnableMcp);
    }

    [Fact]
    public void Parse_no_mcp_wins_regardless_of_position()
    {
        var options = ServeOptions.Parse(["--mcp", "--no-mcp"]);
        Assert.False(options.EnableMcp);
    }

    // ── BuildHost seam ───────────────────────────────────────────────────

    [Fact]
    public async Task BuildHost_over_memory_streams_constructs_without_throwing()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();

        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var copilot = new GitHubCopilotProvider();
        var apiKey = new ApiKeyProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude, copilot, apiKey });

        var sessionOptions = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };

        await using var host = ServeRunner.BuildHost(input, output, credentials, sessionOptions);

        Assert.NotNull(host);
    }

    [Fact]
    public async Task BuildHost_session_factory_injects_wire_callbacks_when_host_runs()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();

        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var copilot = new GitHubCopilotProvider();
        var apiKey = new ApiKeyProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude, copilot, apiKey });

        var sessionOptions = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };

        IPermissionPrompt? capturedPerm = null;
        IUserQuestionPrompt? capturedQuestion = null;
        IPlanApprover? capturedPlan = null;

        // ServeHost calls the factory when RunAsync starts; use a pre-cancelled token
        // so it shuts down immediately after building the session.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await using var host = ServeRunner.BuildHost(
            input,
            output,
            credentials,
            sessionOptions,
            factoryOverride: (perm, question, plan) =>
            {
                capturedPerm = perm;
                capturedQuestion = question;
                capturedPlan = plan;
                return new CodaSession(credentials, sessionOptions with
                {
                    InteractivePrompt = perm,
                    UserQuestionPrompt = question,
                    PlanApprover = plan,
                });
            });

        // Run with a pre-cancelled token — the host starts up, invokes the factory,
        // then exits immediately because the CT is already cancelled.
        await host.RunAsync(cts.Token);

        Assert.NotNull(capturedPerm);
        Assert.NotNull(capturedQuestion);
        Assert.NotNull(capturedPlan);
    }

    // ── Help text ─────────────────────────────────────────────────────────

    [Fact]
    public void Help_output_documents_serve_subcommand()
    {
        var writer = new StringWriter();
        ImmediateCli.TryHandle(["--help"], writer);
        var output = writer.ToString();

        Assert.Contains("serve", output);
    }

    [Fact]
    public void Help_output_documents_no_mcp_flag()
    {
        var writer = new StringWriter();
        ImmediateCli.TryHandle(["--help"], writer);
        var output = writer.ToString();

        Assert.Contains("--no-mcp", output);
    }

    // ── API key / endpoint flag parsing ───────────────────────────────────

    [Fact]
    public void Parses_api_key_and_endpoint()
    {
        var opts = ServeOptions.Parse(["--api-key", "secret", "--endpoint", "my-pipe"]);
        Assert.Equal("secret", opts.ApiKey);
        Assert.Equal("my-pipe", opts.Endpoint);
    }

    [Fact]
    public void Endpoint_without_key_is_rejected()
    {
        var opts = ServeOptions.Parse(["--endpoint", "my-pipe"]);
        var (ok, error) = ServeRunner.ValidateApiMode(opts);
        Assert.False(ok);
        Assert.Contains("api key", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Weak_key_is_rejected_with_reason()
    {
        var opts = ServeOptions.Parse(["--api-key", "short"]);
        var (ok, error) = ServeRunner.ValidateApiMode(opts);
        Assert.False(ok);
        Assert.Contains("64", error!);
    }

    [Fact]
    public void Strong_key_passes_validation()
    {
        var key = string.Concat(Enumerable.Range(0, 8).Select(_ => "0123456789abcdef")); // 128 chars, 16 distinct
        var opts = ServeOptions.Parse(["--api-key", key]);
        var (ok, error) = ServeRunner.ValidateApiMode(opts);
        Assert.True(ok, error);
    }

    [Fact]
    public void No_api_key_is_stdio_mode_and_valid()
    {
        var opts = ServeOptions.Parse([]);
        var (ok, _) = ServeRunner.ValidateApiMode(opts);
        Assert.True(ok);
        Assert.Null(opts.ApiKey);
    }

    // ── ResolveMcpEnabled (CODA_SERVE_DISABLE_MCP override) ────────────────

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "", true)]
    [InlineData(true, "0", true)]
    [InlineData(true, "1", false)]
    [InlineData(true, "true", false)]
    [InlineData(true, "True", false)]   // case-insensitive, matching CODA_DISABLE_MODELS_FETCH
    [InlineData(true, "TRUE", false)]
    [InlineData(false, null, false)]   // --no-mcp already off; env absent keeps it off
    [InlineData(false, "1", false)]
    public void ResolveMcpEnabled_applies_env_override(bool parsed, string? env, bool expected)
    {
        Assert.Equal(expected, ServeRunner.ResolveMcpEnabled(parsed, env));
    }

    // ── BuildMcpExtraTools composition ────────────────────────────────────

    [Fact]
    public void BuildMcpExtraTools_appends_the_four_resource_prompt_helpers()
    {
        // An empty manager (no connected servers) → manager.Tools is empty, so the result is
        // exactly the four helper tools the TUI also adds.
        var manager = new McpClientManager();

        var tools = ServeRunner.BuildMcpExtraTools(manager);

        Assert.Equal(4, tools.Count);
        Assert.Single(tools.OfType<ListMcpResourcesTool>());
        Assert.Single(tools.OfType<ReadMcpResourceTool>());
        Assert.Single(tools.OfType<ListMcpPromptsTool>());
        Assert.Single(tools.OfType<GetMcpPromptTool>());
    }

    // ── LoadMcpToolsAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task LoadMcpToolsAsync_disabled_returns_empty_and_no_manager()
    {
        var (tools, manager) = await ServeRunner.LoadMcpToolsAsync(
            enableMcp: false,
            workingDirectory: Directory.GetCurrentDirectory(),
            httpFactory: new ThrowingHttpFactory(),
            log: _ => { },
            cancellationToken: default);

        Assert.Empty(tools);
        Assert.Null(manager);
    }

    [Fact]
    public async Task LoadMcpToolsAsync_enabled_but_no_servers_returns_empty_and_no_manager()
    {
        // Hermetic: both the working dir and the user MCP dir are empty temp dirs, so
        // McpConfig.Load finds zero servers regardless of the machine's real ~/.coda.
        using var work = new TempDir();
        using var user = new TempDir();

        var (tools, manager) = await ServeRunner.LoadMcpToolsAsync(
            enableMcp: true,
            workingDirectory: work.Path,
            httpFactory: new ThrowingHttpFactory(),
            log: _ => { },
            cancellationToken: default,
            userMcpDir: user.Path);

        Assert.Empty(tools);
        Assert.Null(manager);
    }

    [Fact]
    public async Task LoadMcpToolsAsync_with_configured_server_returns_server_tools_plus_helpers()
    {
        // A project .mcp.json with one HTTP server; the stub factory returns a fake client
        // exposing a single tool — no process or network is touched. Covers the connect branch
        // (ConnectAllAsync → BuildMcpExtraTools) and the caller's manager-disposal contract.
        using var work = new TempDir();
        using var user = new TempDir();
        File.WriteAllText(
            Path.Combine(work.Path, ".mcp.json"),
            """{ "mcpServers": { "fake": { "type": "http", "url": "https://example.test/mcp" } } }""");

        var (tools, manager) = await ServeRunner.LoadMcpToolsAsync(
            enableMcp: true,
            workingDirectory: work.Path,
            httpFactory: new StubHttpFactory(),
            log: _ => { },
            cancellationToken: default,
            userMcpDir: user.Path);

        Assert.NotNull(manager);
        await using (manager)
        {
            // The one server tool (mcp__fake__echo) followed by the four resource/prompt helpers.
            Assert.Equal(5, tools.Count);
            var serverTool = Assert.Single(tools.OfType<McpTool>());
            Assert.Equal("mcp__fake__echo", serverTool.Name);
            Assert.Single(tools.OfType<ListMcpResourcesTool>());
            Assert.Single(tools.OfType<ReadMcpResourceTool>());
            Assert.Single(tools.OfType<ListMcpPromptsTool>());
            Assert.Single(tools.OfType<GetMcpPromptTool>());
        }
    }

    // ── BuildSessionOptions ───────────────────────────────────────────────

    [Fact]
    public void BuildSessionOptions_defaults_extra_tools_to_empty()
    {
        var options = ServeRunner.Parse(["--cwd", "C:\\x"]);

        var so = ServeRunner.BuildSessionOptions(options);

        Assert.Empty(so.ExtraTools);
    }

    [Fact]
    public void BuildSessionOptions_threads_extra_tools_through()
    {
        var options = ServeRunner.Parse(["--cwd", "C:\\x"]);
        // A real MCP helper tool instance is a convenient ITool sample (no hand-written double).
        ITool sample = new ListMcpPromptsTool(new McpClientManager());

        var so = ServeRunner.BuildSessionOptions(options, baseTelemetry: null, extraTools: [sample]);

        Assert.Single(so.ExtraTools);
        Assert.Same(sample, so.ExtraTools[0]);
    }

    [Fact]
    public void BuildSessionOptions_maps_yolo_safe_to_bypass_with_classifier()
    {
        var options = ServeRunner.Parse(["--yolo-safe", "--cwd", "C:\\Temp"]);

        var sessionOptions = ServeRunner.BuildSessionOptions(options);

        Assert.Equal(PermissionMode.BypassPermissions, sessionOptions.PermissionMode);
        Assert.True(sessionOptions.EnableBypassClassifier);
        Assert.Equal("C:\\Temp", sessionOptions.WorkingDirectory);
    }

    [Fact]
    public void BuildSessionOptions_plain_yolo_has_no_classifier()
    {
        var options = ServeRunner.Parse(["--yolo", "--cwd", "C:\\Temp"]);

        var sessionOptions = ServeRunner.BuildSessionOptions(options);

        Assert.Equal(PermissionMode.BypassPermissions, sessionOptions.PermissionMode);
        Assert.False(sessionOptions.EnableBypassClassifier);
    }

    [Fact]
    public void Parse_goal_flag_sets_goal()
    {
        var options = ServeRunner.Parse(["--goal", "all tests pass"]);
        Assert.Equal("all tests pass", options.Goal);
    }

    [Fact]
    public void Parse_session_memory_flag_enables_it()
    {
        var options = ServeRunner.Parse(["--session-memory"]);
        Assert.True(options.EnableSessionMemory);
    }

    [Fact]
    public void BuildSessionOptions_maps_goal_and_session_memory()
    {
        var options = ServeRunner.Parse(["--goal", "ship it", "--session-memory", "--max-continuations", "20", "--cwd", "C:\\x"]);
        var so = ServeRunner.BuildSessionOptions(options);
        Assert.Equal("ship it", so.Goal);
        Assert.True(so.EnableSessionMemory);
        Assert.Equal(20, so.MaxStopContinuations);
    }

    [Fact]
    public void Parse_goal_max_duration_flag_sets_goal_max_duration()
    {
        var options = ServeRunner.Parse(["--goal", "ship it", "--goal-max-duration", "2h"]);
        Assert.Equal(TimeSpan.FromHours(2), options.GoalMaxDuration);
    }

    [Fact]
    public void Parse_goal_max_continuations_flag_sets_goal_max_continuations()
    {
        var options = ServeRunner.Parse(["--goal", "ship it", "--goal-max-continuations", "500"]);
        Assert.Equal(500, options.GoalMaxContinuations);
    }

    [Fact]
    public void Parse_invalid_goal_max_duration_is_ignored()
    {
        // Invalid durations are silently ignored (forward-compatible).
        var options = ServeRunner.Parse(["--goal", "ship it", "--goal-max-duration", "notaduration"]);
        Assert.Null(options.GoalMaxDuration);
    }

    [Fact]
    public void BuildSessionOptions_maps_goal_max_duration_and_continuations()
    {
        var options = ServeRunner.Parse([
            "--goal", "fix all bugs",
            "--goal-max-duration", "30m",
            "--goal-max-continuations", "100",
            "--cwd", "C:\\x"]);
        var so = ServeRunner.BuildSessionOptions(options);
        Assert.Equal("fix all bugs", so.Goal);
        Assert.Equal(TimeSpan.FromMinutes(30), so.GoalMaxDuration);
        Assert.Equal(100, so.GoalMaxContinuations);
    }

    // ── --telemetry / --telemetry-level parsing ────────────────────────────

    [Fact]
    public void Parse_telemetry_flag_sets_force_telemetry()
    {
        var options = ServeOptions.Parse(["--telemetry"]);
        Assert.True(options.ForceTelemetry);
    }

    [Fact]
    public void Parse_telemetry_level_flag_sets_level()
    {
        var options = ServeOptions.Parse(["--telemetry-level", "debug"]);
        Assert.Equal("debug", options.TelemetryLevel);
    }

    [Fact]
    public void Parse_telemetry_absent_is_off_and_null_level()
    {
        var options = ServeOptions.Parse([]);
        Assert.False(options.ForceTelemetry);
        Assert.Null(options.TelemetryLevel);
    }

    [Fact]
    public void Parse_invalid_telemetry_level_is_ignored()
    {
        // Invalid levels are silently ignored (forward-compatible), matching the
        // file's handling of other invalid values (e.g. --goal-max-duration).
        var options = ServeOptions.Parse(["--telemetry-level", "notalevel"]);
        Assert.Null(options.TelemetryLevel);
    }

    [Fact]
    public void Parse_telemetry_level_off_is_honored()
    {
        var options = ServeOptions.Parse(["--telemetry-level", "off"]);
        Assert.Equal("off", options.TelemetryLevel);
    }

    // ── BuildSessionOptions telemetry override ─────────────────────────────

    [Fact]
    public void BuildSessionOptions_force_telemetry_overrides_disabled_settings()
    {
        var options = ServeRunner.Parse(["--telemetry", "--cwd", "C:\\x"]);

        // Loaded settings have telemetry OFF; --telemetry must force it ON for the run.
        var so = ServeRunner.BuildSessionOptions(options, baseTelemetry: TelemetrySettings.Disabled);

        Assert.NotNull(so.TelemetryOverride);
        Assert.True(so.TelemetryOverride!.Enabled);
    }

    [Fact]
    public void BuildSessionOptions_force_telemetry_honors_level()
    {
        var options = ServeRunner.Parse(["--telemetry", "--telemetry-level", "debug", "--cwd", "C:\\x"]);

        var so = ServeRunner.BuildSessionOptions(options, baseTelemetry: TelemetrySettings.Disabled);

        Assert.NotNull(so.TelemetryOverride);
        Assert.True(so.TelemetryOverride!.Enabled);
        Assert.Equal(LogLevel.Debug, so.TelemetryOverride.MinLevel);
    }

    [Fact]
    public void BuildSessionOptions_force_telemetry_keeps_existing_level_when_unspecified()
    {
        var options = ServeRunner.Parse(["--telemetry", "--cwd", "C:\\x"]);

        var baseTelemetry = TelemetrySettings.Disabled with { MinLevel = LogLevel.Warning };
        var so = ServeRunner.BuildSessionOptions(options, baseTelemetry: baseTelemetry);

        Assert.NotNull(so.TelemetryOverride);
        Assert.True(so.TelemetryOverride!.Enabled);
        Assert.Equal(LogLevel.Warning, so.TelemetryOverride.MinLevel);
    }

    [Fact]
    public void BuildSessionOptions_no_force_telemetry_leaves_override_null()
    {
        var options = ServeRunner.Parse(["--cwd", "C:\\x"]);

        var so = ServeRunner.BuildSessionOptions(options, baseTelemetry: TelemetrySettings.Disabled);

        Assert.Null(so.TelemetryOverride);
    }

    [Fact]
    public void BuildSessionOptions_telemetry_level_without_force_leaves_override_null()
    {
        // A lone --telemetry-level (no --telemetry) must NOT enable telemetry: the level is a
        // verbosity knob, not an on-switch. Locks that contract against future drift.
        var options = ServeRunner.Parse(["--telemetry-level", "debug", "--cwd", "C:\\x"]);

        var so = ServeRunner.BuildSessionOptions(options, baseTelemetry: TelemetrySettings.Disabled);

        Assert.Null(so.TelemetryOverride);
    }

    [Fact]
    public void BuildSessionOptions_force_telemetry_with_level_off_stays_enabled()
    {
        // --telemetry --telemetry-level off: the force-on switch wins; "off" is ignored at the
        // build layer rather than silently disabling the telemetry the caller explicitly forced.
        var options = ServeRunner.Parse(["--telemetry", "--telemetry-level", "off", "--cwd", "C:\\x"]);

        var so = ServeRunner.BuildSessionOptions(options, baseTelemetry: TelemetrySettings.Disabled);

        Assert.NotNull(so.TelemetryOverride);
        Assert.True(so.TelemetryOverride!.Enabled);
    }

    // ── settings.json defaultProvider/defaultModel fallback ────────────────
    // When --provider/--model are absent, Parse falls back to the user's persisted
    // ~/.coda/settings.json defaults BEFORE the built-in provider default. CLI flags win.

    [Fact]
    public void Parse_no_provider_or_model_flag_falls_back_to_settings_defaults()
    {
        using var home = new TempSettingsHome("""
        {
            "defaultProvider": "github-copilot",
            "defaultModel": "some-model"
        }
        """);

        var options = ServeRunner.Parse(["--cwd", "X:/wf"], home.Root);

        Assert.Equal(GitHubCopilotProvider.Id, options.ProviderId);
        Assert.Equal("some-model", options.Model);
    }

    [Fact]
    public void Parse_explicit_provider_and_model_flags_win_over_settings()
    {
        using var home = new TempSettingsHome("""
        {
            "defaultProvider": "github-copilot",
            "defaultModel": "some-model"
        }
        """);

        var options = ServeRunner.Parse(["--provider", "claude", "--model", "m2"], home.Root);

        Assert.Equal(ClaudeAiProvider.Id, options.ProviderId);
        Assert.NotEqual(GitHubCopilotProvider.Id, options.ProviderId);
        Assert.Equal("m2", options.Model);
    }

    [Fact]
    public void Parse_explicit_model_flag_wins_while_provider_falls_back_to_settings()
    {
        // Mixed: --model is explicit (wins), --provider is absent (settings default applies).
        using var home = new TempSettingsHome("""
        {
            "defaultProvider": "github-copilot",
            "defaultModel": "settings-model"
        }
        """);

        var options = ServeRunner.Parse(["--model", "flag-model"], home.Root);

        Assert.Equal(GitHubCopilotProvider.Id, options.ProviderId);
        Assert.Equal("flag-model", options.Model);
    }

    [Fact]
    public void Parse_no_flag_and_no_settings_resolves_to_null_no_builtin_default()
    {
        using var home = new TempSettingsHome(settingsJson: null);

        var options = ServeRunner.Parse(["--cwd", "X:/wf"], home.Root);

        // Nothing configured by flag or settings → no invented provider/model.
        Assert.Null(options.ProviderId);
        Assert.Null(options.Model);
    }

    /// <summary>
    /// An <see cref="IMcpHttpClientFactory"/> that fails if used. The no-server tests configure
    /// no HTTP MCP server, so <see cref="Create"/> must never be called — this proves it.
    /// </summary>
    private sealed class ThrowingHttpFactory : IMcpHttpClientFactory
    {
        public IMcpClient Create(string serverName, McpHttpServerConfig config)
            => throw new InvalidOperationException("HTTP factory must not be used when no HTTP server is configured.");
    }

    /// <summary>Returns a <see cref="StubMcpClient"/> for any HTTP server — no real transport.</summary>
    private sealed class StubHttpFactory : IMcpHttpClientFactory
    {
        public IMcpClient Create(string serverName, McpHttpServerConfig config) => new StubMcpClient(serverName);
    }

    /// <summary>A fake MCP client exposing one tool (<c>echo</c>); everything else is inert.</summary>
    private sealed class StubMcpClient : IMcpClient
    {
        public StubMcpClient(string serverName) => this.ServerName = serverName;

        public string ServerName { get; }

        public Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>([new McpToolInfo("echo", "Echoes input.", """{"type":"object"}""", true)]);

        public Task<(string Text, bool IsError)> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(("ok", false));

        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);

        public Task<string> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);

        public Task<string> GetPromptAsync(string name, JsonNode? arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A throwaway directory, deleted on dispose — for hermetic MCP-config tests.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coda-mcp-test-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(this.Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(this.Path))
                {
                    Directory.Delete(this.Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    /// <summary>
    /// Creates a throwaway home directory with an optional <c>.coda/settings.json</c> so
    /// serve-default resolution tests are hermetic (never read the machine's real settings).
    /// </summary>
    private sealed class TempSettingsHome : IDisposable
    {
        public string Root { get; }

        public TempSettingsHome(string? settingsJson)
        {
            this.Root = Path.Combine(Path.GetTempPath(), "coda-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(this.Root, ".coda"));
            if (settingsJson is not null)
            {
                File.WriteAllText(Path.Combine(this.Root, ".coda", "settings.json"), settingsJson);
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(this.Root))
                {
                    Directory.Delete(this.Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
