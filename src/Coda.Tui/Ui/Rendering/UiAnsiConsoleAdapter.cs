using System.Text;
using System.Text.RegularExpressions;
using Coda.Tui.Ui.Events;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// An offscreen <see cref="IAnsiConsole"/> that turns Spectre renderables into semantic
/// <see cref="UiEvent"/>s instead of writing terminal control sequences. Renders through a private
/// non-ANSI, no-color inner console, drains only the output produced by the current call, strips any
/// residual CSI/OSC escapes, normalizes newlines, and publishes exactly one
/// <see cref="CommandOutputEvent"/> when the render produced visible text. <see cref="Clear"/> is
/// translated into a <see cref="ConsoleClearRequestedEvent"/> rather than an escape sequence.
/// </summary>
public sealed partial class UiAnsiConsoleAdapter : IAnsiConsole
{
    private readonly IUiEventPublisher _publisher;
    private readonly StringBuilder _buffer;
    private readonly IAnsiConsole _inner;
    private readonly object _gate = new();

    /// <summary>Create an adapter that publishes rendered output through <paramref name="publisher"/>.</summary>
    /// <param name="publisher">Receives the typed events produced from Spectre output.</param>
    /// <param name="width">The fixed offscreen width; must be positive.</param>
    /// <param name="height">The fixed offscreen height; must be positive.</param>
    public UiAnsiConsoleAdapter(IUiEventPublisher publisher, int width, int height)
    {
        this._publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        this._buffer = new StringBuilder();
        var output = new OffscreenAnsiConsoleOutput(new StringWriter(this._buffer), width, height);
        this._inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = output,
        });
    }

    /// <inheritdoc />
    public Profile Profile => this._inner.Profile;

    /// <inheritdoc />
    public IAnsiConsoleCursor Cursor => this._inner.Cursor;

    /// <inheritdoc />
    public IAnsiConsoleInput Input => this._inner.Input;

    /// <inheritdoc />
    public IExclusivityMode ExclusivityMode => this._inner.ExclusivityMode;

    /// <inheritdoc />
    public RenderPipeline Pipeline => this._inner.Pipeline;

    /// <inheritdoc />
    public void Clear(bool home) => this._publisher.Publish(new ConsoleClearRequestedEvent());

    /// <inheritdoc />
    public void Write(IRenderable renderable)
    {
        ArgumentNullException.ThrowIfNull(renderable);
        lock (this._gate)
        {
            this._buffer.Clear();
            this._inner.Write(renderable);
            this.DrainAndPublish();
        }
    }

    /// <inheritdoc />
    public void WriteAnsi(Action<AnsiWriter> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (this._gate)
        {
            this._buffer.Clear();
            this._inner.WriteAnsi(action);
            this.DrainAndPublish();
        }
    }

    private void DrainAndPublish()
    {
        var raw = this._buffer.ToString();
        this._buffer.Clear();
        if (raw.Length == 0)
        {
            return;
        }

        var stripped = EscapeSequences().Replace(raw, string.Empty);
        var normalized = NormalizeNewlines(stripped);
        if (!HasVisibleContent(normalized))
        {
            return;
        }

        this._publisher.Publish(new CommandOutputEvent(normalized));
    }

    private static string NormalizeNewlines(string value)
    {
        var unified = value.Replace("\r\n", "\n").Replace('\r', '\n');
        return unified.Replace("\n", Environment.NewLine);
    }

    private static bool HasVisibleContent(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex("\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))")]
    private static partial Regex EscapeSequences();
}
