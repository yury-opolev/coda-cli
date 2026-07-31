using Coda.Agent;
using Coda.Agent.Settings;
using Coda.Agent.Tasks;
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Xunit;

namespace Engine.Tests.Subagents;

/// <summary>
/// The subagent limits are only worth having if a settings file actually reaches the running
/// session. These pin the whole path — settings file → <see cref="SessionOptions"/> →
/// <see cref="TaskManager"/> → <see cref="ToolContext"/> — because every hop is a place where the
/// value can be silently dropped and the symptom is simply that the setting does nothing.
/// </summary>
public sealed class SubagentLimitsWiringTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_subagent_wiring_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { }
    }

    private void WriteProjectSettings(string json)
    {
        var dir = Path.Combine(this.root, ".coda");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);
    }

    private static CredentialManager SignedInClaude()
    {
        var store = new InMemoryTokenStore();
        var creds = new CredentialManager(store, [new ClaudeAiProvider()]);
        creds.StoreAsync(ClaudeAiProvider.Id, new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "AT",
        }).GetAwaiter().GetResult();
        return creds;
    }

    private SessionOptions Options() => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = PermissionMode.BypassPermissions,
    };

    // -----------------------------------------------------------------------
    // settings.json → the session's TaskManager
    // -----------------------------------------------------------------------

    [Fact]
    public void A_settings_file_maxDepth_reaches_the_session_task_manager()
    {
        this.WriteProjectSettings("""{"subagents":{"maxDepth":4}}""");

        using var session = new CodaSession(SignedInClaude(), this.Options());

        Assert.Equal(4, session.Tasks.MaxSubagentDepth);
    }

    [Fact]
    public void A_settings_file_maxConcurrent_reaches_the_session_task_manager()
    {
        this.WriteProjectSettings("""{"subagents":{"maxConcurrent":3}}""");

        using var session = new CodaSession(SignedInClaude(), this.Options());

        Assert.Equal(3, session.Tasks.MaxConcurrentSubagents);
        Assert.Equal(3, session.Tasks.AvailableSubagentSlots);
    }

    [Fact]
    public void Without_a_subagents_block_the_session_keeps_todays_limits()
    {
        using var session = new CodaSession(SignedInClaude(), this.Options());

        Assert.Equal(SubagentSettings.Default.MaxDepth, session.Tasks.MaxSubagentDepth);
        Assert.Equal(SubagentSettings.Default.MaxConcurrent, session.Tasks.MaxConcurrentSubagents);
    }

    [Fact]
    public void An_absurd_settings_value_is_still_clamped_by_the_time_it_reaches_the_session()
    {
        this.WriteProjectSettings("""{"subagents":{"maxDepth":9999,"maxConcurrent":0}}""");

        using var session = new CodaSession(SignedInClaude(), this.Options());

        Assert.Equal(SubagentSettings.MaxAllowedDepth, session.Tasks.MaxSubagentDepth);
        Assert.Equal(1, session.Tasks.MaxConcurrentSubagents);
    }

    [Fact]
    public void An_explicit_SessionOptions_value_overrides_the_settings_file()
    {
        this.WriteProjectSettings("""{"subagents":{"maxDepth":4}}""");
        var options = this.Options() with { SubagentSettings = new SubagentSettings { MaxDepth = 6 } };

        using var session = new CodaSession(SignedInClaude(), options);

        Assert.Equal(6, session.Tasks.MaxSubagentDepth);
    }

    // -----------------------------------------------------------------------
    // TaskManager → ToolContext, so the tools see the session's real limit
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_loop_publishes_the_managers_depth_limit_on_the_tool_context()
    {
        using var tasks = new TaskManager(
            sessionId: "wiring-ctx",
            logRoot: null,
            subagentSettings: new SubagentSettings { MaxDepth = 5 });
        var probe = new ContextProbeTool();
        var loop = new AgentLoop(
            new ProbeScriptedClient(),
            new ToolRegistry([probe]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { Model = "m", WorkingDirectory = this.root, SystemPrompt = "sys" },
            tasks: tasks);

        await loop.RunAsync([ChatMessage.UserText("go")], new DiscardingSink(), CancellationToken.None);

        Assert.Equal(5, probe.SeenMaxDepth);
    }

    [Fact]
    public async Task Without_a_task_manager_the_tool_context_falls_back_to_the_default_depth()
    {
        var probe = new ContextProbeTool();
        var loop = new AgentLoop(
            new ProbeScriptedClient(),
            new ToolRegistry([probe]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { Model = "m", WorkingDirectory = this.root, SystemPrompt = "sys" });

        await loop.RunAsync([ChatMessage.UserText("go")], new DiscardingSink(), CancellationToken.None);

        Assert.Equal(TaskManager.DefaultMaxSubagentDepth, probe.SeenMaxDepth);
    }

    private sealed class ContextProbeTool : ITool
    {
        public int SeenMaxDepth { get; private set; } = -1;

        public string Name => "probe";

        public string Description => "records context";

        public string InputSchemaJson => "{}";

        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            this.SeenMaxDepth = context.MaxSubagentDepth;
            return Task.FromResult(new ToolResult("ok"));
        }
    }

    /// <summary>Calls <c>probe</c> once, then finishes.</summary>
    private sealed class ProbeScriptedClient : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (this.turn++ == 0)
            {
                yield return AssistantStreamEvent.Tool(new ToolUseBlock("p1", "probe", "{}"));
                yield return AssistantStreamEvent.Finished("tool_use");
            }
            else
            {
                yield return AssistantStreamEvent.Delta("done");
                yield return AssistantStreamEvent.Finished("end_turn");
            }
        }
    }
}
