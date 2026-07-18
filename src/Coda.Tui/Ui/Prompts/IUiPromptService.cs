namespace Coda.Tui.Ui.Prompts;

/// <summary>
/// A host-neutral prompt surface. The agent and slash commands depend on this abstraction instead of
/// Spectre directly, so the same code runs against the actor mailbox, an offscreen Spectre console, or
/// a non-interactive plain fallback.
/// </summary>
public interface IUiPromptService
{
    /// <summary>Whether a real user can answer prompts; false means every request is denied/cancelled.</summary>
    bool IsInteractive { get; }

    /// <summary>Present <paramref name="request"/> and asynchronously return the user's answer.</summary>
    Task<UiPromptResponse> RequestAsync(UiPromptRequest request, CancellationToken cancellationToken = default);
}
