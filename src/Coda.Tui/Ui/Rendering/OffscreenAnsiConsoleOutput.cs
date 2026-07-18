using System.Text;
using Spectre.Console;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// A fixed-size, non-terminal <see cref="IAnsiConsoleOutput"/> that writes into a caller-owned
/// <see cref="TextWriter"/> (backed by a <see cref="StringBuilder"/> buffer). It reports a stable
/// width/height so Spectre lays content out deterministically and never probes a real terminal.
/// </summary>
public sealed class OffscreenAnsiConsoleOutput : IAnsiConsoleOutput
{
    /// <summary>Create an offscreen output over <paramref name="writer"/> with a fixed size.</summary>
    /// <param name="writer">The buffer writer that receives rendered output.</param>
    /// <param name="width">The reported console width; must be positive.</param>
    /// <param name="height">The reported console height; must be positive.</param>
    public OffscreenAnsiConsoleOutput(TextWriter writer, int width, int height)
    {
        this.Writer = writer ?? throw new ArgumentNullException(nameof(writer));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        this.Width = width;
        this.Height = height;
    }

    /// <inheritdoc />
    public TextWriter Writer { get; }

    /// <inheritdoc />
    public bool IsTerminal => false;

    /// <inheritdoc />
    public int Width { get; }

    /// <inheritdoc />
    public int Height { get; }

    /// <inheritdoc />
    public void SetEncoding(Encoding encoding)
    {
        // The buffer writer owns its encoding; there is no real device to reconfigure.
    }
}
