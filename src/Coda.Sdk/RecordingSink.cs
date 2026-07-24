using System.Text;
using Coda.Agent;
using LlmClient;

namespace Coda.Sdk;

/// <summary>
/// Forwards agent events to an optional inner sink while recording the final
/// assistant text and the tool calls, so <see cref="CodaSession.RunAsync"/> can
/// return a <see cref="RunResult"/>.
/// </summary>
internal sealed class RecordingSink : IAgentSink
{
    private readonly IAgentSink? inner;
    private readonly object gate = new();
    private readonly StringBuilder current = new();
    private readonly List<ToolCallRecord> toolCalls = [];
    private readonly Dictionary<ToolCallKey, int> correlatedCallIndexes = [];
    private ToolActivitySummary? completedSummary;
    private string finalText = string.Empty;
    private string? stopReason;
    private TokenUsage usage = TokenUsage.Zero;

    public RecordingSink(IAgentSink? inner)
    {
        this.inner = inner;
    }

    public string FinalText
    {
        get
        {
            lock (this.gate)
            {
                return this.finalText;
            }
        }
    }

    public string? StopReason
    {
        get
        {
            lock (this.gate)
            {
                return this.stopReason;
            }
        }
    }

    public TokenUsage Usage
    {
        get
        {
            lock (this.gate)
            {
                return this.usage;
            }
        }
    }

    public IReadOnlyList<ToolCallRecord> ToolCalls
    {
        get
        {
            lock (this.gate)
            {
                return this.toolCalls.ToArray();
            }
        }
    }

    public void OnAssistantText(string delta)
    {
        lock (this.gate)
        {
            this.current.Append(delta);
        }

        this.inner?.OnAssistantText(delta);
    }

    public void OnAssistantTextComplete()
    {
        lock (this.gate)
        {
            if (this.current.Length > 0)
            {
                // The last completed text span is the final answer.
                this.finalText = this.current.ToString().Trim();
                this.current.Clear();
            }
        }

        this.inner?.OnAssistantTextComplete();
    }

    public void OnToolCall(string toolName, string inputJson)
    {
        lock (this.gate)
        {
            this.toolCalls.Add(new ToolCallRecord(toolName, Truncate(inputJson), null, false));
        }

        this.inner?.OnToolCall(toolName, inputJson);
    }

    public void OnToolResult(string toolName, ToolResult result)
    {
        lock (this.gate)
        {
            for (var i = this.toolCalls.Count - 1; i >= 0; i--)
            {
                var call = this.toolCalls[i];
                if (call.CallId is null
                    && call.SourceId is null
                    && call.Name == toolName
                    && call.Result is null)
                {
                    this.toolCalls[i] = call with { Result = Truncate(result.Content), IsError = result.IsError };
                    break;
                }
            }
        }

        this.inner?.OnToolResult(toolName, result);
    }

    public void OnToolQueued(ToolCallIdentity identity, string toolName, string inputJson)
    {
        lock (this.gate)
        {
            var key = ToolCallKey.From(identity);
            if (!this.correlatedCallIndexes.ContainsKey(key))
            {
                this.correlatedCallIndexes[key] = this.toolCalls.Count;
                this.toolCalls.Add(new ToolCallRecord(toolName, Truncate(inputJson), null, false)
                {
                    RootTurnId = identity.RootTurnId,
                    ActivityId = identity.ActivityId,
                    CallId = identity.CallId,
                    SourceId = identity.SourceId,
                    Status = ToolCallStatus.Pending,
                });
            }
        }

        this.inner?.OnToolQueued(identity, toolName, inputJson);
    }

    public void OnToolCall(ToolCallIdentity identity, string toolName, string inputJson)
    {
        lock (this.gate)
        {
            if (this.TryGetCorrelatedIndex(identity, out var index))
            {
                this.toolCalls[index] = this.toolCalls[index] with
                {
                    Name = toolName,
                    Input = Truncate(inputJson),
                };
            }
        }

        this.inner?.OnToolCall(identity, toolName, inputJson);
    }

    public void OnToolStatus(ToolCallIdentity identity, string toolName, ToolCallStatus status)
    {
        lock (this.gate)
        {
            if (this.TryGetCorrelatedIndex(identity, out var index))
            {
                this.toolCalls[index] = this.toolCalls[index] with { Status = status };
            }
        }

        this.inner?.OnToolStatus(identity, toolName, status);
    }

    // Must forward — this is the production decorator wrapping the serve WireAgentSink. If the
    // tool-execution heartbeat is not forwarded here it never reaches the wire and the Bridge
    // watchdog stays blind during tool execution (the whole bug this pulse exists to fix).
    public void OnToolProgress(string toolName, long elapsedMs) => this.inner?.OnToolProgress(toolName, elapsedMs);

    public void OnToolProgress(ToolCallIdentity identity, string toolName, long elapsedMs) =>
        this.inner?.OnToolProgress(identity, toolName, elapsedMs);

    public void OnToolResult(ToolCallIdentity identity, string toolName, ToolResult result, ToolCallStatus status)
    {
        lock (this.gate)
        {
            if (this.TryGetCorrelatedIndex(identity, out var index))
            {
                this.toolCalls[index] = this.toolCalls[index] with
                {
                    Result = Truncate(result.Content),
                    IsError = result.IsError,
                    Status = status,
                };
            }
        }

        this.inner?.OnToolResult(identity, toolName, result, status);
    }

    public void OnToolActivityCompleted(ToolActivitySummary summary) =>
        this.inner?.OnToolActivityCompleted(summary);

    public void OnError(string message) => this.inner?.OnError(message);

    public void OnLimitReached(string kind, string message) => this.inner?.OnLimitReached(kind, message);

    public void OnSteeringDelivered(IReadOnlyList<string> ids) => this.inner?.OnSteeringDelivered(ids);

    public void OnUsage(TokenUsage usage)
    {
        lock (this.gate)
        {
            this.usage = this.usage.Add(usage);
        }

        this.inner?.OnUsage(usage);
    }

    public void OnStopReason(string? stopReason)
    {
        lock (this.gate)
        {
            this.stopReason = stopReason;
        }

        this.inner?.OnStopReason(stopReason);
    }

    public ToolActivitySummary? CompleteActivity(bool interrupted)
    {
        ToolActivitySummary? summary;
        lock (this.gate)
        {
            if (this.completedSummary is not null)
            {
                return this.completedSummary;
            }

            var representative = this.toolCalls.FirstOrDefault(IsFullyCorrelated);
            if (representative is null)
            {
                return null;
            }

            var rootTurnId = representative.RootTurnId!;
            var activityId = representative.ActivityId!;
            var correlatedIndexes = new List<int>();
            for (var index = 0; index < this.toolCalls.Count; index++)
            {
                var call = this.toolCalls[index];
                if (!IsFullyCorrelated(call)
                    || !string.Equals(call.RootTurnId, rootTurnId, StringComparison.Ordinal)
                    || !string.Equals(call.ActivityId, activityId, StringComparison.Ordinal))
                {
                    continue;
                }

                var finalizedStatus = call.Status switch
                {
                    ToolCallStatus.Pending => ToolCallStatus.Skipped,
                    ToolCallStatus.AwaitingApproval or ToolCallStatus.Running => ToolCallStatus.Cancelled,
                    _ => call.Status,
                };
                if (finalizedStatus != call.Status)
                {
                    this.toolCalls[index] = call with { Status = finalizedStatus };
                }

                correlatedIndexes.Add(index);
            }

            if (correlatedIndexes.Count == 0)
            {
                return null;
            }

            var correlated = correlatedIndexes.Select(index => this.toolCalls[index]).ToArray();
            string? homogeneousToolName = correlated[0].Name;
            if (!correlated.All(call => string.Equals(call.Name, homogeneousToolName, StringComparison.Ordinal)))
            {
                homogeneousToolName = null;
            }

            summary = new ToolActivitySummary(
                rootTurnId,
                activityId,
                correlated.Length,
                correlated.Count(call => call.Status == ToolCallStatus.Failed),
                correlated.Count(call => call.Status == ToolCallStatus.Cancelled),
                correlated.Count(call => call.Status == ToolCallStatus.Skipped),
                homogeneousToolName);
            this.completedSummary = summary;
        }

        this.inner?.OnToolActivityCompleted(summary);
        return summary;
    }

    private bool TryGetCorrelatedIndex(ToolCallIdentity identity, out int index) =>
        this.correlatedCallIndexes.TryGetValue(ToolCallKey.From(identity), out index);

    private static bool IsFullyCorrelated(ToolCallRecord call) =>
        call.RootTurnId is not null
        && call.ActivityId is not null
        && call.CallId is not null
        && call.SourceId is not null;

    private static string Truncate(string value) => value.Length > 500 ? value[..500] + "…" : value;

    private readonly record struct ToolCallKey(
        string? RootTurnId,
        string? ActivityId,
        string? SourceId,
        string? CallId)
    {
        public static ToolCallKey From(ToolCallIdentity identity) =>
            new(identity.RootTurnId, identity.ActivityId, identity.SourceId, identity.CallId);
    }
}
