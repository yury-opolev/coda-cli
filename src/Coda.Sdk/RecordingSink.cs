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
    private readonly StringBuilder current = new();
    private readonly List<ToolCallRecord> toolCalls = [];

    public RecordingSink(IAgentSink? inner)
    {
        this.inner = inner;
    }

    public string FinalText { get; private set; } = string.Empty;

    public string? StopReason { get; private set; }

    public TokenUsage Usage { get; private set; } = TokenUsage.Zero;

    public IReadOnlyList<ToolCallRecord> ToolCalls => this.toolCalls;

    public void OnAssistantText(string delta)
    {
        this.current.Append(delta);
        this.inner?.OnAssistantText(delta);
    }

    public void OnAssistantTextComplete()
    {
        if (this.current.Length > 0)
        {
            // The last completed text span is the final answer.
            this.FinalText = this.current.ToString().Trim();
            this.current.Clear();
        }

        this.inner?.OnAssistantTextComplete();
    }

    public void OnToolCall(string toolName, string inputJson)
    {
        this.toolCalls.Add(new ToolCallRecord(toolName, Truncate(inputJson), null, false));
        this.inner?.OnToolCall(toolName, inputJson);
    }

    public void OnToolResult(string toolName, ToolResult result)
    {
        for (var i = this.toolCalls.Count - 1; i >= 0; i--)
        {
            if (this.toolCalls[i].Name == toolName && this.toolCalls[i].Result is null)
            {
                this.toolCalls[i] = this.toolCalls[i] with { Result = Truncate(result.Content), IsError = result.IsError };
                break;
            }
        }

        this.inner?.OnToolResult(toolName, result);
    }

    public void OnError(string message) => this.inner?.OnError(message);

    public void OnLimitReached(string kind, string message) => this.inner?.OnLimitReached(kind, message);

    public void OnUsage(TokenUsage usage)
    {
        this.Usage = this.Usage.Add(usage);
        this.inner?.OnUsage(usage);
    }

    public void OnStopReason(string? stopReason)
    {
        this.StopReason = stopReason;
        this.inner?.OnStopReason(stopReason);
    }

    private static string Truncate(string value) => value.Length > 500 ? value[..500] + "…" : value;
}
