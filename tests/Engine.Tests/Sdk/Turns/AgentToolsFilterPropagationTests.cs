using Coda.Agent;
using Coda.Agent.Permissions;
using Coda.Agent.Scheduling;
using Coda.Agent.Settings;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Coda.Sdk;
using Coda.Sdk.Turns;
using Engine.Tests.TestSupport;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Microsoft.Extensions.Logging.Abstractions;
using static Engine.Tests.TestSupport.CredentialFixtures;
using static Engine.Tests.TestSupport.SseTestHandler;

namespace Engine.Tests.Sdk.Turns;

/// <summary>
/// Non-propagation regression test: when <c>agent.tools</c> is configured, the filter is
/// applied ONLY to the parent (main-agent) tool registry produced by
/// <see cref="TurnPipelineBuilder.BuildSpec"/>. The registry handed to
/// <see cref="SubagentHost"/> must still contain the FULL built-in toolset so subagents
/// are not silently starved of tools.
///
/// This test is the concrete regression guard for the "do not implement as TurnShape.AllowedTools"
/// design constraint: if anyone reimplements the feature that way,
/// <c>TurnShapeResolver.ToToolRestrictionShape</c> would intersect the allowlist with the
/// subagent registry and leave every subagent with zero or near-zero tools.
/// </summary>
public sealed class AgentToolsFilterPropagationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_filter_prop_").FullName;
    private readonly HttpClient http = new(new SseTestHandler(MessageStopOnly));

    public void Dispose()
    {
        this.http.Dispose();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { }
    }

    private ILlmClient Client() =>
        LlmClientFactory.Create(ClaudeAiProvider.Id, SignedInClaude(), new ClientFingerprint(), this.http)!;

    private TurnPipelineBuilder NewBuilder() =>
        new TurnPipelineBuilder(
            new TodoStore(),
            new ScheduledTaskStore(),
            new TaskManager(sessionId: "filter-test", logRoot: null),
            lspManager: null,
            lspDiagnostics: null,
            toolSearchCoordinator: null,
            NullLoggerFactory.Instance,
            (_, _, _, _, _) => Task.FromResult(true),
            () => null);

    private SessionOptions Options(ToolNameFilter? agentToolFilter = null) => new SessionOptions
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        AgentToolFilter = agentToolFilter,
    };

    /// <summary>
    /// Returns the tool registry stored inside the <see cref="SubagentHost"/> via its
    /// internal test accessor.
    /// </summary>
    private static ToolRegistry GetSubagentHostTools(ISubagentHost host)
    {
        var subagentHost = Assert.IsType<SubagentHost>(host);
        return subagentHost.SubagentTools;
    }

    // ------------------------------------------------------------------
    // THE CRITICAL NON-PROPAGATION TEST
    // ------------------------------------------------------------------

    /// <summary>
    /// When <c>agent.tools.allow</c> is ["task","task_start"], the parent registry must
    /// contain only those two tools. The SubagentHost registry must still contain
    /// <c>read_file</c>, <c>run_command</c>, and the other built-ins — confirming the
    /// filter was NOT forwarded to the subagent registry.
    /// </summary>
    [Fact]
    public void Filter_is_applied_to_parent_registry_only_not_to_subagent_host()
    {
        var filter = new ToolNameFilter(allow: ["task", "task_start"], deny: []);
        var builder = this.NewBuilder();
        var settings = CodaSettings.Empty;
        var spec = builder.BuildSpec(this.Options(filter), this.Client(), settings);

        // Parent registry: only the allowed tools survive.
        var parentToolNames = spec.Tools.All.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("task", parentToolNames);
        Assert.Contains("task_start", parentToolNames);
        Assert.DoesNotContain("read_file", parentToolNames);
        Assert.DoesNotContain("run_command", parentToolNames);

        // Subagent host registry: must contain the full built-in toolset.
        Assert.NotNull(spec.Subagents);
        var subagentTools = GetSubagentHostTools(spec.Subagents!);
        var subagentToolNames = subagentTools.All.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("read_file", subagentToolNames);
        Assert.Contains("run_command", subagentToolNames);
        Assert.Contains("glob", subagentToolNames);
        Assert.Contains("grep", subagentToolNames);
        Assert.Contains("write_file", subagentToolNames);
    }

    // ------------------------------------------------------------------
    // No filter → no restriction
    // ------------------------------------------------------------------

    [Fact]
    public void Without_filter_parent_registry_contains_all_built_in_tools()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(agentToolFilter: null), this.Client(), CodaSettings.Empty);

        var names = spec.Tools.All.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("task", names);
        Assert.Contains("task_start", names);
        Assert.Contains("read_file", names);
        Assert.Contains("run_command", names);
    }

    // ------------------------------------------------------------------
    // Deny filter — also non-propagating
    // ------------------------------------------------------------------

    [Fact]
    public void Deny_filter_removes_tools_from_parent_but_not_subagent()
    {
        var filter = new ToolNameFilter(allow: null, deny: ["run_command"]);
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(filter), this.Client(), CodaSettings.Empty);

        // Parent: run_command removed.
        var parentNames = spec.Tools.All.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("run_command", parentNames);
        Assert.Contains("task", parentNames);

        // Subagent host: run_command still present.
        Assert.NotNull(spec.Subagents);
        var subagentTools = GetSubagentHostTools(spec.Subagents!);
        var subagentNames = subagentTools.All.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("run_command", subagentNames);
    }
}
