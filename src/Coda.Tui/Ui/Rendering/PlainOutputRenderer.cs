using System.Globalization;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;

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
    private readonly ToolDisplayMode _toolDisplayMode;

    /// <summary>Create a renderer that writes plain output to <paramref name="writer"/>.</summary>
    /// <param name="writer">The destination writer (e.g. redirected stdout).</param>
    public PlainOutputRenderer(TextWriter writer, ToolDisplayMode toolDisplayMode = ToolDisplayMode.Verbose)
    {
        this._writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this._toolDisplayMode = toolDisplayMode;
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
                if (this._toolDisplayMode is not (ToolDisplayMode.Tiny or ToolDisplayMode.Summary))
                {
                    var input = this._toolDisplayMode == ToolDisplayMode.Compact
                        ? ToolDisplayModeText.ArgumentPreview(e.InputJson)
                        : e.InputJson;
                    var suffix = this._toolDisplayMode == ToolDisplayMode.Compact ? " [running]" : string.Empty;
                    this.WriteLine($"[tool] {e.ToolName} {input}{suffix}".TrimEnd());
                }
                break;

            case ToolProgressEvent e:
                if (this._toolDisplayMode is not (ToolDisplayMode.Tiny or ToolDisplayMode.Summary))
                {
                    var seconds = (e.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
                    this.WriteLine($"[tool-progress] {e.ToolName} {seconds}s");
                }
                break;

            case ToolCompletedEvent e:
                if (this._toolDisplayMode is not (ToolDisplayMode.Tiny or ToolDisplayMode.Summary))
                {
                    if (this._toolDisplayMode == ToolDisplayMode.Compact)
                    {
                        var status = e.Result.IsError ? "error" : "success";
                        this.WriteLine($"[tool-result] {e.ToolName} [{status}]");
                    }
                    else
                    {
                        this.WriteLine($"[tool-result] {e.ToolName}: {e.Result.Content}");
                    }
                }
                break;

            case ToolActivityCompletedEvent e when this._toolDisplayMode == ToolDisplayMode.Summary:
                this.WriteLine(ToolActivityPreview.CompletedText(e.Summary));
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

    private static string Sanitize(string value) => TerminalTextSanitizer.StripAnsiEscapes(value);
}
