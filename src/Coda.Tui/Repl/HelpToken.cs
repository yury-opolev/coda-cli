namespace Coda.Tui;

/// <summary>Recognizes the argument tokens that request a command's help.</summary>
public static class HelpToken
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="token"/> is one of the
    /// recognized help flags: <c>--help</c>, <c>-h</c>, or <c>help</c> (case-insensitive).
    /// </summary>
    public static bool IsHelpToken(string token) =>
        token.Equals("--help", StringComparison.OrdinalIgnoreCase)
        || token.Equals("-h", StringComparison.OrdinalIgnoreCase)
        || token.Equals("help", StringComparison.OrdinalIgnoreCase);
}
