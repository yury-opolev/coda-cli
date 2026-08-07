namespace Coda.Tui.Ui.Mcp;

/// <summary>
/// Produces the context-sensitive footer hint for the MCP editor. Pure and Terminal.Gui-free so it
/// can be unit-tested directly.
/// </summary>
/// <remarks>
/// A footer that lies is worse than no footer, so every token here is checked against what the key
/// actually does. <c>Enter</c> in particular is NOT a global "save": it is dispatched per focused
/// field by <see cref="McpBrowserController"/>'s <c>ApplyEditorAsync</c>, where it variously
/// cycles an option, moves to the value part, opens the encrypt-secret modal, saves, cancels, or —
/// on plain text and list fields — does nothing at all. The Save button is deliberately not
/// <c>IsDefault</c>, so a field that ignores Enter simply advertises no Enter token.
/// </remarks>
internal static class McpEditorHints
{
    /// <summary>Hint shown when the terminal is too narrow for the full string.</summary>
    internal const string Compact = "↑/↓ field · Esc cancel";

    /// <summary>
    /// The full-width footer for <paramref name="field"/>, and — for the key/value fields — the
    /// focused <paramref name="part"/> of the current row.
    /// </summary>
    internal static string ForField(McpEditorField field, McpEditorItemPart part)
    {
        var parts = new List<string> { "↑/↓ field" };

        switch (field)
        {
            // Enter cycles these too, but ←/→ is the discoverable way and saying both is noise.
            case McpEditorField.Scope:
            case McpEditorField.Transport:
            case McpEditorField.AuthMode:
                parts.Add("←/→ option");
                break;

            case McpEditorField.Arguments:
            case McpEditorField.Scopes:
                AddCollectionKeys(parts);
                break;

            // Focus moves between the two parts with Tab/Shift+Tab — the arrows belong to the text
            // caret inside the focused field, so advertising them here would be actively wrong.
            case McpEditorField.Environment:
            case McpEditorField.Headers:
                parts.Add("Tab name/value");
                AddCollectionKeys(parts);
                parts.Add(part == McpEditorItemPart.Value ? "Enter encrypt" : "Enter → value");
                break;

            // Enter opens the same encrypt-secret modal as a key/value row's value part.
            case McpEditorField.BearerToken:
                parts.Add("Enter encrypt");
                break;

            case McpEditorField.Save:
                parts.Add("Enter save");
                break;

            case McpEditorField.Cancel:
                parts.Add("Enter cancel");
                break;

            default:
                // Name, Command, Url, ClientId: plain text fields where Enter is a no-op.
                break;
        }

        parts.Add("Esc cancel");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// True when <paramref name="field"/> ignores Enter entirely, so the footer must not promise
    /// anything for it. Exposed so a test can assert the footer and the dispatcher agree.
    /// </summary>
    internal static bool EnterIsNoOp(McpEditorField field) => field is
        McpEditorField.Name or
        McpEditorField.Command or
        McpEditorField.Url or
        McpEditorField.ClientId or
        McpEditorField.Arguments or
        McpEditorField.Scopes;

    private static void AddCollectionKeys(List<string> parts)
    {
        parts.Add("Ctrl+N add");
        parts.Add("Ctrl+R remove");
        parts.Add("Alt+↑/↓ reorder");
    }
}
