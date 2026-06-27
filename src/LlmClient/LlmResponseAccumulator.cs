using System.Text;

namespace LlmClient;

/// <summary>
/// Accumulates a streaming assistant turn so the client can log a single response
/// summary (content preview, stop reason, token usage) once the stream completes.
/// Text deltas are concatenated; the stop reason and usage come from the terminal
/// <see cref="AssistantEventKind.Done"/> event.
/// </summary>
public sealed class LlmResponseAccumulator
{
    private readonly StringBuilder content = new();

    /// <summary>The accumulated assistant text so far.</summary>
    public string Content => this.content.ToString();

    /// <summary>The stop/finish reason reported on the terminal event, when present.</summary>
    public string? StopReason { get; private set; }

    /// <summary>Token usage reported on the terminal event, when present.</summary>
    public TokenUsage? Usage { get; private set; }

    /// <summary>Folds one stream event into the running accumulation.</summary>
    public void Observe(AssistantStreamEvent streamEvent)
    {
        switch (streamEvent.Kind)
        {
            case AssistantEventKind.TextDelta:
                if (!string.IsNullOrEmpty(streamEvent.Text))
                {
                    this.content.Append(streamEvent.Text);
                }

                break;

            case AssistantEventKind.Done:
                this.StopReason = streamEvent.StopReason;
                if (streamEvent.Usage is not null)
                {
                    this.Usage = streamEvent.Usage;
                }

                break;
        }
    }
}
