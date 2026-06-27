namespace Coda.Agent;

/// <summary>
/// Host callback that lets the agent ask the user a structured multiple-choice question.
/// Implementations are UI-specific (TUI, tests). Null means headless — no user available.
/// </summary>
public interface IUserQuestionPrompt
{
    /// <summary>
    /// Presents a question with a list of options to the user and returns the chosen text.
    /// For <paramref name="multiSelect"/>, returns the selected options joined by ", " (comma-space).
    /// </summary>
    Task<string> AskAsync(
        string question,
        IReadOnlyList<string> options,
        bool multiSelect,
        CancellationToken cancellationToken = default);
}
