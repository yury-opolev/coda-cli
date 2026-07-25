using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Tests;

internal sealed class RecordingPromptService : IUiPromptService
{
    private readonly Queue<UiPromptResponse> responses;

    public RecordingPromptService(params UiPromptResponse[] responses)
    {
        this.responses = new(responses);
    }

    public bool IsInteractive => true;

    public List<UiPromptRequest> Requests { get; } = [];

    /// <summary>
    /// Enqueue a sequence of highlight ids to fire (via <see cref="UiPromptRequest.OnHighlight"/>)
    /// before the corresponding <see cref="RequestAsync"/> call returns its canned response. Each entry
    /// in the queue corresponds to the next request in order; an empty array means "no highlights for
    /// this request". Entries that have no queued highlights (or whose request has no callback) are
    /// silently skipped.
    /// </summary>
    public Queue<IReadOnlyList<string>> PendingHighlights { get; } = new();

    public Task<UiPromptResponse> RequestAsync(UiPromptRequest request, CancellationToken cancellationToken = default)
    {
        this.Requests.Add(request);

        if (this.PendingHighlights.TryDequeue(out var highlightSequence) && request.OnHighlight is { } cb)
        {
            foreach (var id in highlightSequence)
            {
                cb(id);
            }
        }

        return Task.FromResult(this.responses.Dequeue());
    }
}
