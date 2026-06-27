namespace Coda.Agent.BackgroundTasks;

/// <summary>
/// An <see cref="IAgentSink"/> that captures subagent events into a
/// <see cref="BackgroundTask"/> output buffer. Does not forward to any parent sink.
/// Thread-safe via <see cref="BackgroundTask.Append"/>.
/// </summary>
public sealed class CapturingSink : IAgentSink
{
    private readonly BackgroundTask task;

    public CapturingSink(BackgroundTask task)
    {
        this.task = task ?? throw new ArgumentNullException(nameof(task));
    }

    /// <inheritdoc/>
    public void OnAssistantText(string delta)
    {
        this.task.Append(delta);
    }

    /// <inheritdoc/>
    public void OnAssistantTextComplete()
    {
        // No-op: completion of a text span doesn't add visible content.
    }

    /// <inheritdoc/>
    public void OnToolCall(string toolName, string inputJson)
    {
        this.task.Append($"\n[tool: {toolName}]\n");
    }

    /// <inheritdoc/>
    public void OnToolResult(string toolName, ToolResult result)
    {
        this.task.Append($"[/{toolName}]\n");
    }

    /// <inheritdoc/>
    public void OnError(string message)
    {
        this.task.Append($"[error: {message}]\n");
    }
}
