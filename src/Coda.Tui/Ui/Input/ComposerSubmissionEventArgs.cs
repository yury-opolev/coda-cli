namespace Coda.Tui.Ui.Input;

/// <summary>
/// Carries a submitted draft together with the draft that existed before a completion was accepted.
/// </summary>
internal sealed record ComposerSubmissionEventArgs(string Text, string OriginalDraft);
