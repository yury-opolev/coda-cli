using Coda.Tui.Ui.Mcp;

namespace Coda.Tui.Tests;

/// <summary>
/// The editor footer is the only place the MCP editor's keys are advertised, so a token that does
/// not match what the key actually does is worse than no footer at all. These tests pin the footer
/// to the real dispatch in <c>McpBrowserController.ApplyEditorAsync</c> and the real bindings in
/// <c>McpBrowserKeyMap</c>.
/// </summary>
/// <remarks>
/// <c>McpEditorField</c> is internal, so these are loop-driven facts rather than theories with an
/// enum parameter (a public theory parameter cannot be less accessible than its method).
/// </remarks>
public sealed class McpEditorHintsTests
{
    private static readonly McpEditorField[] AllFields = Enum.GetValues<McpEditorField>();

    private static readonly McpEditorField[] RadioSelectors =
        [McpEditorField.Scope, McpEditorField.Transport, McpEditorField.AuthMode];

    private static readonly McpEditorField[] MapFields =
        [McpEditorField.Environment, McpEditorField.Headers];

    private static readonly McpEditorField[] CollectionFields =
        [McpEditorField.Arguments, McpEditorField.Scopes, McpEditorField.Environment, McpEditorField.Headers];

    private static readonly McpEditorField[] EnterIsNoOpFields =
    [
        McpEditorField.Name, McpEditorField.Command, McpEditorField.Url,
        McpEditorField.ClientId, McpEditorField.Arguments, McpEditorField.Scopes,
    ];

    private static string Hint(McpEditorField field, McpEditorItemPart part = McpEditorItemPart.Name) =>
        McpEditorHints.ForField(field, part);

    // ── invariants that hold for every field ────────────────────────────────

    [Fact]
    public void Every_field_opens_with_field_navigation_and_closes_with_escape()
    {
        foreach (var field in AllFields)
        {
            var hint = Hint(field);
            Assert.StartsWith("↑/↓ field", hint, StringComparison.Ordinal);
            Assert.EndsWith("Esc cancel", hint, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The footer must never invent a binding. Left/Right belong to the text caret inside a focused
    /// TextField; they only mean "option" on the three radio selectors, and they are NOT how the
    /// user moves between a key and its value (that is Tab).
    /// </summary>
    [Fact]
    public void Arrow_keys_are_only_advertised_where_they_change_an_option()
    {
        foreach (var field in AllFields.Except(RadioSelectors))
        {
            foreach (var part in Enum.GetValues<McpEditorItemPart>())
            {
                Assert.DoesNotContain("←/→", Hint(field, part), StringComparison.Ordinal);
            }
        }

        foreach (var field in RadioSelectors)
        {
            Assert.Contains("←/→ option", Hint(field), StringComparison.Ordinal);
        }
    }

    // ── Enter must match ApplyEditorAsync ───────────────────────────────────

    /// <summary>
    /// <c>ApplyEditorAsync</c> falls through to <c>default: return</c> for plain text and list
    /// fields, and the Save button is not <c>IsDefault</c>, so Enter genuinely does nothing there.
    /// Promising "Enter save" would send the user down a dead end.
    /// </summary>
    [Fact]
    public void A_field_that_ignores_enter_advertises_no_enter_action()
    {
        foreach (var field in EnterIsNoOpFields)
        {
            Assert.True(McpEditorHints.EnterIsNoOp(field));
            Assert.DoesNotContain("Enter", Hint(field), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_field_that_acts_on_enter_advertises_exactly_one_enter_action()
    {
        foreach (var field in AllFields.Where(f => !McpEditorHints.EnterIsNoOp(f)))
        {
            foreach (var part in Enum.GetValues<McpEditorItemPart>())
            {
                var hint = Hint(field, part);

                // The radio selectors are the one deliberate omission: Enter cycles them, but ←/→
                // is the discoverable way and advertising both is noise.
                if (RadioSelectors.Contains(field))
                {
                    Assert.DoesNotContain("Enter", hint, StringComparison.Ordinal);
                    continue;
                }

                Assert.Equal(1, hint.Split(" · ").Count(t => t.StartsWith("Enter", StringComparison.Ordinal)));
            }
        }
    }

    /// <summary>
    /// Enter on a value part opens the encrypt-secret modal (<c>PromptNamedReplacementAsync</c>);
    /// on a name part it moves to the value (<c>MoveEditorItemPart</c>). Neither is a save.
    /// </summary>
    [Fact]
    public void Enter_on_a_map_row_encrypts_the_value_and_advances_from_the_name()
    {
        foreach (var field in MapFields)
        {
            Assert.Contains("Enter encrypt", Hint(field, McpEditorItemPart.Value), StringComparison.Ordinal);

            var nameHint = Hint(field, McpEditorItemPart.Name);
            Assert.Contains("Enter → value", nameHint, StringComparison.Ordinal);
            Assert.DoesNotContain("Enter encrypt", nameHint, StringComparison.Ordinal);
            Assert.DoesNotContain("Enter save", nameHint, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The bearer token runs the SAME encrypt modal as a value part
    /// (<c>PromptBearerReplacementAsync</c>), so it must not be mislabelled as a save.
    /// </summary>
    [Fact]
    public void The_bearer_token_advertises_encryption_not_save()
    {
        var hint = Hint(McpEditorField.BearerToken);

        Assert.Contains("Enter encrypt", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("Enter save", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_buttons_advertise_what_they_do()
    {
        Assert.Contains("Enter save", Hint(McpEditorField.Save), StringComparison.Ordinal);
        Assert.Contains("Enter cancel", Hint(McpEditorField.Cancel), StringComparison.Ordinal);
    }

    // ── collection keys ─────────────────────────────────────────────────────

    /// <summary>
    /// <c>EditorAddItem</c> / <c>EditorRemoveItem</c> / <c>ReorderEditorItem</c> all handle
    /// Arguments, Scopes, Environment AND Headers — the map fields must not omit reorder.
    /// </summary>
    [Fact]
    public void Every_collection_field_advertises_add_remove_and_reorder()
    {
        foreach (var field in CollectionFields)
        {
            var hint = Hint(field);

            Assert.Contains("Ctrl+N add", hint, StringComparison.Ordinal);
            Assert.Contains("Ctrl+R remove", hint, StringComparison.Ordinal);
            Assert.Contains("Alt+↑/↓ reorder", hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_non_collection_field_does_not_advertise_collection_keys()
    {
        foreach (var field in AllFields.Except(CollectionFields))
        {
            Assert.DoesNotContain("Ctrl+N", Hint(field), StringComparison.Ordinal);
        }
    }

    /// <summary>Tab is the real key/value move; the map fields must say so.</summary>
    [Fact]
    public void The_map_fields_advertise_tab_for_name_value()
    {
        foreach (var field in MapFields)
        {
            Assert.Contains("Tab name/value", Hint(field), StringComparison.Ordinal);
        }

        foreach (var field in AllFields.Except(MapFields))
        {
            Assert.DoesNotContain("Tab name/value", Hint(field), StringComparison.Ordinal);
        }
    }
}
