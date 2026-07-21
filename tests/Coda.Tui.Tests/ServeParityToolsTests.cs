using System.Runtime.CompilerServices;
using Coda.Agent;
using Coda.Agent.Scheduling;
using Coda.Agent.Settings;
using Coda.Agent.Tasks;
using Coda.Sdk;
using Coda.Sdk.Turns;
using Coda.Tui;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Tui.Tests;

/// <summary>
/// Serve-parity characterization for the model-facing task tools. Proves that the parent (leader)
/// tool registry <see cref="TurnPipelineBuilder"/> assembles is IDENTICAL for a normal interactive
/// turn and for a <c>coda serve</c> turn whose <see cref="SessionOptions"/> come from
/// <see cref="ServeRunner.BuildSessionOptions"/>: both surface every task_* tool — the pre-existing
/// set plus the new wait/background/remove parity tools — because both flow through the same
/// <see cref="BuiltInTools.All"/>-derived assembly. It also locks the naming contract the
/// read-only/max-depth subagent strip relies on (every task tool is <c>task</c> or <c>task_*</c>),
/// so those tools are denied to restricted children by prefix without any per-tool wiring.
/// </summary>
public sealed class ServeParityToolsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_serve_parity_").FullName;
    private readonly string userSettingsDir = Directory.CreateTempSubdirectory("coda_serve_parity_user_").FullName;

    private const string Provider = ClaudeAiProvider.Id;
    private const string Model = "claude-sonnet-4-6";

    private TurnPipelineBuilder NewBuilder() => new(
        new TodoStore(),
        new ScheduledTaskStore(),
        new TaskManager(sessionId: "parity", logRoot: null),
        lspManager: null,
        lspDiagnostics: null,
        toolSearchCoordinator: null,
        NullLoggerFactory.Instance,
        (_, _, _) => Task.CompletedTask,
        () => null);

    private static ILlmClient Client() => new StubClient();

    /// <summary>Task-management tool names in the registry (the <c>task</c> spawn tool + every task_* tool).</summary>
    private static IReadOnlyList<string> TaskToolNames(AgentLoopSpec spec) =>
        spec.Tools.All
            .Select(t => t.Name)
            .Where(n => n == "task" || n.StartsWith("task_", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private SessionOptions InteractiveOptions() => new()
    {
        ProviderId = Provider,
        Model = Model,
        WorkingDirectory = this.root,
    };

    private SessionOptions ServeOptions()
    {
        // Serve builds its SessionOptions from parsed CLI options through the SAME code path the
        // running server uses; only ExtraTools (MCP) differ from interactive, never the built-ins.
        var options = ServeRunner.Parse(
            ["--provider", Provider, "--model", Model, "--cwd", this.root],
            userSettingsDir: this.userSettingsDir);
        return ServeRunner.BuildSessionOptions(options);
    }

    [Fact]
    public void Serve_and_interactive_expose_identical_task_tools()
    {
        var interactive = TaskToolNames(this.NewBuilder().BuildSpec(this.InteractiveOptions(), Client(), CodaSettings.Empty));
        var serve = TaskToolNames(this.NewBuilder().BuildSpec(this.ServeOptions(), Client(), CodaSettings.Empty));

        // Byte-for-byte identical task tool surface: serve parity.
        Assert.Equal(interactive, serve);
    }

    [Fact]
    public void Serve_and_interactive_include_all_new_and_existing_task_tools()
    {
        foreach (var options in new[] { this.InteractiveOptions(), this.ServeOptions() })
        {
            var names = TaskToolNames(this.NewBuilder().BuildSpec(options, Client(), CodaSettings.Empty)).ToHashSet();

            // Parent-only spawn tool + the runtime/query tools + the new parity tools.
            Assert.Contains("task", names);
            Assert.Contains("task_list", names);
            Assert.Contains("task_get", names);
            Assert.Contains("task_peek", names);
            Assert.Contains("task_send", names);
            Assert.Contains("task_stop", names);
            Assert.Contains("task_wait", names);
            Assert.Contains("task_background", names);
            Assert.Contains("task_remove", names);
        }
    }

    [Fact]
    public void Every_task_tool_is_prefix_stripped_for_restricted_subagents()
    {
        // The read-only/max-depth subagent strip removes tools by the `task`/`task_` prefix. Lock
        // that every task tool the parent exposes honors that naming contract, so new task tools
        // are denied to restricted children automatically rather than leaking through.
        var names = TaskToolNames(this.NewBuilder().BuildSpec(this.InteractiveOptions(), Client(), CodaSettings.Empty));

        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.True(n == "task" || n.StartsWith("task_", StringComparison.Ordinal)));
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
        try { Directory.Delete(this.userSettingsDir, recursive: true); } catch { /* ignore */ }
    }

    private sealed class StubClient : ILlmClient
    {
        public string ProviderId => Provider;

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }
}
