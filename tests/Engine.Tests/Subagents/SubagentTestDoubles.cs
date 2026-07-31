using System.Runtime.CompilerServices;
using Coda.Agent;
using Coda.Agent.Hooks;
using LlmClient;

namespace Engine.Tests.Subagents;

/// <summary>Answers every hook invocation with a fixed stdout.</summary>
internal sealed class StubHookExecutor(string stdout) : IHookExecutor
{
    public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
        string command, string payload, CancellationToken ct) =>
        Task.FromResult((0, stdout, string.Empty));
}

/// <summary>Answers every model call with one finished turn, keeping what it was asked.</summary>
internal sealed class RecordingSubagentClient : ILlmClient
{
    /// <summary>The system prompt of the most recent request.</summary>
    public string? LastSystem { get; private set; }

    /// <summary>The tool names advertised on the most recent request.</summary>
    public IReadOnlyList<string> LastToolNames { get; private set; } = [];

    public string ProviderId => "fake";

    public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        this.LastSystem = request.System;
        this.LastToolNames = request.Tools?.Select(static t => t.Name).ToList() ?? [];
        await Task.Yield();
        yield return AssistantStreamEvent.Delta("done");
        yield return AssistantStreamEvent.Finished("end_turn");
    }
}

/// <summary>Discards everything a subagent emits.</summary>
internal sealed class DiscardingSink : IAgentSink
{
    public void OnAssistantText(string delta) { }
    public void OnAssistantTextComplete() { }
    public void OnToolCall(string toolName, string inputPreview) { }
    public void OnToolResult(string toolName, ToolResult result) { }
    public void OnError(string message) { }
    public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }
}

/// <summary>Records the request it was handed and returns at once, without running anything.</summary>
internal sealed class RequestCapturingHost : ISubagentHost
{
    /// <summary>The most recent request, or null when only the legacy overload was used.</summary>
    public SubagentRequest? LastRequest { get; private set; }

    /// <summary>Completes once the request overload has been called.</summary>
    public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string> RunSubagentAsync(
        string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
        string taskId, int depth, CancellationToken cancellationToken = default) =>
        Task.FromResult("report");

    public Task<string> RunSubagentAsync(
        SubagentRequest request, IAgentSink sink, SteeringInbox steering,
        CancellationToken cancellationToken = default)
    {
        this.LastRequest = request;
        this.Called.TrySetResult();
        return Task.FromResult("report");
    }
}
