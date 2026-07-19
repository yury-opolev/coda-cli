namespace Coda.Tui.Ui.Shells;

/// <summary>
/// The outcome of a single clipboard read behind the shell's <c>clipboardReader</c> seam. A failed read
/// (no clipboard backend, or the driver refused the request) is represented by <see cref="Available"/>
/// being false rather than by throwing, so a pointer-driven paste can surface a deterministic
/// "Clipboard unavailable" warning without swallowing exceptions. A successful read reports
/// <see cref="Available"/> true and the retrieved <see cref="Text"/> (which may be empty).
/// </summary>
internal readonly record struct ClipboardReadResult(bool Available, string Text);
