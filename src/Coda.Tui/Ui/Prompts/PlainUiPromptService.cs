using System.Collections.Immutable;

namespace Coda.Tui.Ui.Prompts;

/// <summary>
/// A non-interactive prompt surface for headless/plain runs. It never reads stdin: confirmations
/// resolve to "no" and every other prompt is cancelled. Safe defaults so an automated run cannot
/// silently allow a mutating action.
/// </summary>
public sealed class PlainUiPromptService : IUiPromptService
{
    /// <summary>The shared instance.</summary>
    public static PlainUiPromptService Instance { get; } = new();

    private PlainUiPromptService()
    {
    }

    /// <inheritdoc />
    public bool IsInteractive => false;

    /// <inheritdoc />
    public Task<UiPromptResponse> RequestAsync(UiPromptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = request.Kind == UiPromptKind.Confirm
            ? new UiPromptResponse(false, ["no"], null)
            : new UiPromptResponse(true, [], null);

        return Task.FromResult(response);
    }
}
