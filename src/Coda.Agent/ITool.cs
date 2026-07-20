using System.Text.Json;
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Lsp;
using Coda.Agent.Scheduling;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Coda.Agent;

/// <summary>Context passed to a tool execution (working dir + optional services).</summary>
public sealed record ToolContext(string WorkingDirectory)
{
    /// <summary>
    /// When true, filesystem tools may operate on paths outside <see cref="WorkingDirectory"/>.
    /// Set only in bypass-permissions ("yolo") mode; the default keeps the cwd sandbox.
    /// </summary>
    public bool AllowOutsideWorkingDirectory { get; init; }

    /// <summary>Telemetry logger for tool execution; null when telemetry is not wired (e.g. some tests).</summary>
    public ILogger? Logger { get; init; }

    /// <summary>The live agent sink (so tools like <c>task</c> can stream subagent output).</summary>
    public IAgentSink? Sink { get; init; }

    /// <summary>Host that runs nested subagents (provided when the <c>task</c> tool is available).</summary>
    public ISubagentHost? Subagents { get; init; }

    /// <summary>Session todo list, when available (null for subagents).</summary>
    public TodoStore? Todos { get; init; }

    /// <summary>Scheduled-task store, when available (null for subagents).</summary>
    public ScheduledTaskStore? Schedules { get; init; }

    /// <summary>Interactive question prompt, when an interactive user is available (null for headless/subagents).</summary>
    public IUserQuestionPrompt? UserQuestion { get; init; }

    /// <summary>Plan-approval callback, when an interactive user is available (null for headless/subagents).</summary>
    public IPlanApprover? PlanApprover { get; init; }

    /// <summary>Background-task runner, when available (null for subagents).</summary>
    public BackgroundTaskRunner? BackgroundTasks { get; init; }

    /// <summary>LSP server manager for code-intelligence operations; null when no LSP servers are configured.</summary>
    public LspServerManager? Lsp { get; init; }

    /// <summary>The full registered tool set (deferred + non-deferred); null when not wired by the coordinator.</summary>
    public IReadOnlyList<ITool>? AllTools { get; init; }

    /// <summary>Callback invoked with matched tool names after a tool_search execution; null when not wired.</summary>
    public Action<IReadOnlyList<string>>? OnToolsDiscovered { get; init; }
}

/// <summary>The outcome of running a tool, fed back to the model.</summary>
public sealed record ToolResult(string Content, bool IsError = false);

/// <summary>An executable tool the model can call.</summary>
public interface ITool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema (as text) for the tool's input.</summary>
    string InputSchemaJson { get; }

    /// <summary>Read-only tools run without a permission prompt.</summary>
    bool IsReadOnly { get; }

    Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default);

    /// <summary>When true, this tool is hidden from the inline tool list and discovered on demand via tool_search.</summary>
    bool ShouldDefer => false;

    /// <summary>A curated capability phrase for search; null means rely on name and description alone.</summary>
    string? SearchHint => null;

    /// <summary>The wire definition advertised to the model.</summary>
    ToolDefinition ToDefinition() => new(this.Name, this.Description, this.InputSchemaJson);
}
