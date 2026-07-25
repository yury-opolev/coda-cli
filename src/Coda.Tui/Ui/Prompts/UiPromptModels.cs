using System.Collections.Immutable;

namespace Coda.Tui.Ui.Prompts;

/// <summary>The kind of interaction a <see cref="UiPromptRequest"/> models.</summary>
public enum UiPromptKind
{
    /// <summary>A yes/no confirmation.</summary>
    Confirm,

    /// <summary>Pick exactly one option.</summary>
    SelectOne,

    /// <summary>Pick zero or more options.</summary>
    SelectMany,

    /// <summary>Free-form text entry.</summary>
    Text,

    /// <summary>Free-form secret entry (masked input).</summary>
    Secret,
}

/// <summary>A single selectable option, carrying a stable <paramref name="Id"/> distinct from its display label.</summary>
public sealed record UiPromptOption(
    string Id,
    string Label,
    string? Detail = null,
    bool IsCurrent = false);

/// <summary>
/// A host-neutral description of a prompt the UI must present. Frontends render it however they like
/// (actor round-trip, Spectre widget, or a plain no-op) and answer with a <see cref="UiPromptResponse"/>.
/// </summary>
public sealed record UiPromptRequest(
    Guid Id,
    UiPromptKind Kind,
    string Title,
    string? Message,
    ImmutableArray<UiPromptOption> Options,
    string? DefaultValue,
    bool Required)
{
    /// <summary>A yes/no confirmation defaulting to <paramref name="defaultValue"/>.</summary>
    public static UiPromptRequest Confirm(string title, bool defaultValue) =>
        new(Guid.NewGuid(), UiPromptKind.Confirm, title, null, [new("yes", "Yes"), new("no", "No")], defaultValue ? "yes" : "no", true);

    /// <summary>A single-choice selection over <paramref name="options"/>, optionally pre-selecting <paramref name="defaultValue"/>.</summary>
    public static UiPromptRequest Select(string title, IEnumerable<UiPromptOption> options, string? defaultValue = null, bool allowFreeText = false) =>
        new(Guid.NewGuid(), UiPromptKind.SelectOne, title, null, [.. options], defaultValue, true) { AllowFreeText = allowFreeText };

    /// <summary>A multi-choice selection over <paramref name="options"/>.</summary>
    public static UiPromptRequest SelectMany(string title, IEnumerable<UiPromptOption> options, bool allowFreeText = false) =>
        new(Guid.NewGuid(), UiPromptKind.SelectMany, title, null, [.. options], null, false) { AllowFreeText = allowFreeText };

    /// <summary>A free-form text (or secret) entry.</summary>
    public static UiPromptRequest Text(string title, string? defaultValue = null, bool required = false, bool secret = false) =>
        new(Guid.NewGuid(), secret ? UiPromptKind.Secret : UiPromptKind.Text, title, null, [], defaultValue, required);

    /// <summary>
    /// Optional callback invoked on the UI thread whenever the highlighted row changes in a selection
    /// prompt (including the initial selection when the prompt opens). Has no effect on Text/Secret
    /// prompts or any caller that passes <see langword="null"/>. The serve path and all existing pickers
    /// other than the theme picker always leave this null, preserving their existing behavior.
    /// </summary>
    public Action<string>? OnHighlight { get; init; }

    /// <summary>
    /// When <see langword="true"/> on a <see cref="UiPromptKind.SelectOne"/> or
    /// <see cref="UiPromptKind.SelectMany"/> prompt, the overlay appends a trailing
    /// <c>✎ Type your own answer…</c> row that lets the user submit an arbitrary string instead of
    /// picking from the fixed options. Always <see langword="false"/> for closed-set internal pickers.
    /// </summary>
    public bool AllowFreeText { get; init; }
}

/// <summary>
/// The answer to a <see cref="UiPromptRequest"/>. <see cref="SelectedIds"/> holds option ids (never
/// labels); <see cref="Text"/> holds free-form input. <see cref="Cancelled"/> means the user dismissed
/// the prompt without answering.
/// </summary>
public sealed record UiPromptResponse(
    bool Cancelled,
    ImmutableArray<string> SelectedIds,
    string? Text);
