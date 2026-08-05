using Coda.Mcp;
using Coda.Tui.Mcp;

namespace Coda.Tui.Ui.Mcp;

/// <summary>
/// Resolves the ordered field set for a given draft, driven purely by the draft's transport and
/// auth mode. There is no mutable state here: every call to <see cref="For"/> is a pure function.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="McpEditorField.Environment"/> is stdio-only.
/// <c>NormalizeHttpDraft</c> in <c>McpManagementService</c> zeroes it on every save, so exposing
/// it in the http form would silently discard anything the user typed.
/// </para>
/// <para>
/// The conditional auth fields (<c>ClientId</c>, <c>Scopes</c>, <c>BearerToken</c>) appear only
/// when <c>AuthMode != None</c>: there is no server to authenticate to when auth is disabled, so
/// showing those fields would create dead input.
/// </para>
/// </remarks>
internal static class McpEditorFieldSet
{
    private static readonly IReadOnlyList<McpEditorField> StdioFields =
    [
        McpEditorField.Scope,
        McpEditorField.Name,
        McpEditorField.Transport,
        McpEditorField.Command,
        McpEditorField.Arguments,
        McpEditorField.Environment,
        McpEditorField.Save,
        McpEditorField.Cancel,
    ];

    private static readonly IReadOnlyList<McpEditorField> HttpNoAuthFields =
    [
        McpEditorField.Scope,
        McpEditorField.Name,
        McpEditorField.Transport,
        McpEditorField.Url,
        McpEditorField.Headers,
        McpEditorField.AuthMode,
        McpEditorField.Save,
        McpEditorField.Cancel,
    ];

    private static readonly IReadOnlyList<McpEditorField> HttpWithAuthFields =
    [
        McpEditorField.Scope,
        McpEditorField.Name,
        McpEditorField.Transport,
        McpEditorField.Url,
        McpEditorField.Headers,
        McpEditorField.AuthMode,
        McpEditorField.ClientId,
        McpEditorField.Scopes,
        McpEditorField.BearerToken,
        McpEditorField.Save,
        McpEditorField.Cancel,
    ];

    /// <summary>
    /// Returns the ordered fields for <paramref name="draft"/>.
    /// The returned list is always the same cached array for a given (transport, authMode) pair.
    /// </summary>
    internal static IReadOnlyList<McpEditorField> For(McpServerDraft draft)
    {
        if (draft.Transport == McpTransportKind.Stdio)
        {
            return StdioFields;
        }

        return draft.AuthMode == McpAuthMode.None ? HttpNoAuthFields : HttpWithAuthFields;
    }
}
