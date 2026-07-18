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

    public Task<UiPromptResponse> RequestAsync(UiPromptRequest request, CancellationToken cancellationToken = default)
    {
        this.Requests.Add(request);
        return Task.FromResult(this.responses.Dequeue());
    }
}
