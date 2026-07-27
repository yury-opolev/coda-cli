using Terminal.Gui.Drivers;

namespace Coda.Tui.Ui.Host;

/// <summary>
/// A <see cref="AnsiComponentFactory"/> that replaces the stock ANSI output with
/// <see cref="DiffingAnsiOutput"/>, which reduces terminal bandwidth by transmitting only cells
/// that changed since the last frame. Everything else — input, size monitoring, and Kitty keyboard
/// support — is inherited unchanged from the base factory, preserving the Shift+Enter behavior that
/// Windows Terminal requires.
/// </summary>
internal sealed class DiffingAnsiComponentFactory : AnsiComponentFactory
{
    /// <summary>The driver name reported by this factory and its output layer.</summary>
    internal const string DriverName = "coda-diff";

    /// <inheritdoc />
    public override IOutput CreateOutput() => new DiffingAnsiOutput(AppModel);

    /// <inheritdoc />
    public override string GetDriverName() => DriverName;
}
