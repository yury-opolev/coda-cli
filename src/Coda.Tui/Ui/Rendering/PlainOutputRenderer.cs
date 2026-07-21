using System.Globalization;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;
using Coda.Tui.Ui.Tasks;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// Renders <see cref="UiEvent"/>s as stable, plain text with no cursor movement, alternate-screen
/// switching, color, or interactive prompts. Suitable for redirected output, CI, unsupported
/// terminals, and the explicit <c>--plain</c> mode. Externally supplied command/diff/diagnostic text
/// is sanitized of control escapes while ordinary tabs and newlines are preserved.
/// </summary>
public sealed class PlainOutputRenderer : IUiEventObserver
{
    private readonly TextWriter _writer;

    /// <summary>Create a renderer that writes plain output to <paramref name="writer"/>.</summary>
    /// <param name="writer">The destination writer (e.g. redirected stdout).</param>
    public PlainOutputRenderer(TextWriter writer)
    {
        this._writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    /// <inheritdoc />
    public ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (uiEvent)
        {
            case AssistantTextDeltaEvent e:
                this._writer.Write(e.Delta);
                break;

            case AssistantTextCompletedEvent:
                this._writer.Write(Environment.NewLine);
                break;

            case ToolStartedEvent e:
                this.WriteLine($"[tool] {e.ToolName} {e.InputJson}");
                break;

            case ToolProgressEvent e:
                var seconds = (e.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
                this.WriteLine($"[tool-progress] {e.ToolName} {seconds}s");
                break;

            case ToolCompletedEvent e:
                this.WriteLine($"[tool-result] {e.ToolName}: {e.Result.Content}");
                break;

            case WarningEvent e:
                this.WriteLine($"[warning] {e.Message}");
                break;

            case AgentErrorEvent e:
                this.WriteLine($"[error] {e.Message}");
                break;

            case LimitReachedEvent e:
                this.WriteLine($"[limit:{e.Kind}] {e.Message}");
                break;

            case StopReasonEvent e when !string.IsNullOrWhiteSpace(e.StopReason):
                this.WriteLine($"[stop] {e.StopReason}");
                break;

            case DiagnosticEvent e:
                this.WriteLine($"[diagnostic:{e.Source}] {Sanitize(e.Message)}");
                break;

            case NotificationEvent e:
                this.WriteLine($"[{PrefixFor(e.Level)}] {e.Message}");
                break;

            case DiffOutputEvent e:
                this.WriteBlock(Sanitize(e.Patch));
                break;

            case CommandOutputEvent e:
                this.WriteBlock(Sanitize(e.Text));
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void WriteLine(string text)
    {
        this._writer.Write(text);
        this._writer.Write(Environment.NewLine);
    }

    private void WriteBlock(string text)
    {
        this._writer.Write(TrimTrailingNewlines(text));
        this._writer.Write(Environment.NewLine);
    }

    private static string PrefixFor(UiNotificationLevel level) => level switch
    {
        UiNotificationLevel.Error => "error",
        UiNotificationLevel.Warning => "warning",
        _ => "info",
    };

    private static string TrimTrailingNewlines(string value) => value.TrimEnd('\r', '\n');

    private static string Sanitize(string value) => TaskTextSanitizer.StripAnsiEscapes(value);
}
