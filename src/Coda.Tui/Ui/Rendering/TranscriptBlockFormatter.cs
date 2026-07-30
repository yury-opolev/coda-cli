using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Coda.Agent;
using Coda.Tui.Ui.State;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// A contiguous column range on a single rendered line that is a hyperlink. Part A carries the span
/// metadata so the draw path can style it; Part B will use it for hit-testing and opening.
/// </summary>
/// <param name="StartColumn">Inclusive start cell column of the link (or sub-span) on this render line.</param>
/// <param name="EndColumn">Exclusive end cell column of the link (or sub-span) on this render line.</param>
/// <param name="Url">The destination URL (always http/https from markdown or autolinks).</param>
/// <param name="TextMatchesUrl">
/// <see langword="true"/> when the display text unambiguously identifies the destination (bare autolink,
/// or display text equals the URL or its host/authority, case-insensitively). <see langword="false"/>
/// (deceptive) when the visible text hides a different destination.
/// </param>
public readonly record struct LinkSpan(int StartColumn, int EndColumn, string Url, bool TextMatchesUrl);

/// <summary>Visual role of a rendered transcript line, used to pick a color/attribute at draw time.</summary>
public enum TranscriptRole
{
    User,
    Assistant,
    Heading,
    Code,
    Tool,
    Diff,
    Permission,
    Question,
    Warning,
    Notification,
    Error,

    // Six semantic context-usage roles, one per /context category, so the breakdown stays distinguishable
    // by color (each pairs with a distinct glyph in the formatter for low-color legibility).
    ContextSystemPrompt,
    ContextSystemTools,
    ContextMcpTools,
    ContextMessages,
    ContextAutocompactBuffer,
    ContextFreeSpace,

    // Five GitHub-style admonition callout roles, one per type. Title rows use the callout role so they
    // render in the type's hue; body text rows stay Assistant for readable neutral color.
    CalloutNote,
    CalloutTip,
    CalloutImportant,
    CalloutWarning,
    CalloutCaution,

    /// <summary>A queued user message that has not yet been delivered: rendered with a dim user foreground
    /// and a <c>[pending]</c> prefix on the first line so it reads as muted until sent.</summary>
    PendingUser,

    /// <summary>A batch of tool calls where every call succeeded.</summary>
    ToolSuccess,

    /// <summary>A batch of tool calls where some, but not all, calls failed.</summary>
    ToolPartialFailure,

    /// <summary>A tool call whose permission was approved and executed.</summary>
    PermissionApproved,
}

/// <summary>A single rendered transcript line: display text plus the role that colors it.</summary>
/// <remarks>
/// The implicit conversion from <see cref="string"/> lets callers (and the layout index) treat a plain
/// wrapped line as an assistant-role render line without ceremony; typed callers still supply an
/// explicit role.
/// </remarks>
public readonly record struct TranscriptRenderLine(string Text, TranscriptRole Role)
{
    /// <summary>
    /// Whether the row paints its background across the full viewport width (a block treatment used for
    /// user messages), rather than only under the drawn text.
    /// </summary>
    public bool FillWidth { get; init; }

    /// <summary>
    /// An optional right-aligned annotation (e.g. a sent-time <c>HH:mm</c>) drawn near the row's trailing edge
    /// in a dim attribute. It is never part of <see cref="Text"/>, so it does not wrap and is excluded from
    /// selection/copy; the row's text and trailing cells are reserved so the two never overlap.
    /// </summary>
    public string? RightText { get; init; }

    /// <summary>Cells intentionally left blank after <see cref="RightText"/>.</summary>
    public int RightTextTrailingCells { get; init; }

    /// <summary>
    /// When greater than zero, the first <c>PrefixCells</c> display cells of <see cref="Text"/> are painted
    /// in <see cref="PrefixRole"/> rather than <see cref="Role"/>. The bar text (e.g. <c>│ </c>) stays in
    /// <see cref="Text"/> so copy/selection still includes it; only its COLOR comes from the prefix role.
    /// Selection highlight still wins over the prefix color within its range.
    /// </summary>
    public int PrefixCells { get; init; }

    /// <summary>The role (and thus color) applied to the first <see cref="PrefixCells"/> cells of the row.</summary>
    public TranscriptRole PrefixRole { get; init; }

    /// <summary>
    /// Zero or more hyperlink spans on this render line. Each span records the inclusive column range,
    /// destination URL, and whether the display text honestly identifies the destination. Null when the
    /// row has no links, keeping the common (non-link) path allocation-free.
    /// A single logical link whose text wraps across multiple render lines contributes one
    /// <see cref="LinkSpan"/> per line, all sharing the same <see cref="LinkSpan.Url"/>.
    /// </summary>
    public IReadOnlyList<LinkSpan>? Links { get; init; }

    /// <summary>Where this row sits in the transcript's gutter/tree shape. Set while the block is being
    /// projected and consumed by the final shaping pass, which turns it into the row's literal prefix.</summary>
    public TranscriptGutterKind Gutter { get; init; }

    /// <summary>Wraps a plain string as an assistant-role line.</summary>
    public static implicit operator TranscriptRenderLine(string text) => new(text, TranscriptRole.Assistant);

    // The auto-generated record equality compares IReadOnlyList<LinkSpan>? by reference, which would
    // break IncrementalMarkdownFormatterTests (two lists with identical content from separate renders
    // would not be equal). Override to compare Links by content.
    public bool Equals(TranscriptRenderLine other) =>
        this.Text == other.Text &&
        this.Role == other.Role &&
        this.FillWidth == other.FillWidth &&
        this.RightText == other.RightText &&
        this.RightTextTrailingCells == other.RightTextTrailingCells &&
        this.PrefixCells == other.PrefixCells &&
        this.PrefixRole == other.PrefixRole &&
        this.Gutter == other.Gutter &&
        LinksContentEqual(this.Links, other.Links);

    private static bool LinksContentEqual(IReadOnlyList<LinkSpan>? a, IReadOnlyList<LinkSpan>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = HashCode.Combine(
            this.Text, (int)this.Role, this.FillWidth, this.RightText,
            this.RightTextTrailingCells, this.PrefixCells, (int)this.PrefixRole, (int)this.Gutter);
        if (this.Links is not null)
        {
            foreach (var link in this.Links)
            {
                hash = HashCode.Combine(hash, link);
            }
        }

        return hash;
    }
}

/// <summary>
/// Shared, host-neutral projection of a <see cref="TranscriptBlock"/> onto attributed, width-wrapped
/// lines. Assistant markdown is parsed through Markdig's block/inline AST (headings, paragraphs, fenced
/// code, bold/emphasis) and flattened to plain text with a role per line; typed blocks (user, tool,
/// diff, permission, question, notice, ...) map to sensible roles. The formatter never emits ANSI or
/// other control sequences — color is applied later from <see cref="TranscriptRole"/> — and it wraps by
/// display cell width without splitting grapheme clusters, so both the inline and full-screen shells can
/// render identical content.
/// </summary>
public static class TranscriptBlockFormatter
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAutoLinks().Build();

    /// <summary>Projects <paramref name="block"/> onto wrapped, attributed lines for the given cell width.</summary>
    public static IReadOnlyList<TranscriptRenderLine> Format(TranscriptBlock block, int width) =>
        Format(block, width, ToolDisplayMode.Full);

    /// <summary>Projects <paramref name="block"/> using the requested tool display mode.</summary>
    public static IReadOnlyList<TranscriptRenderLine> Format(
        TranscriptBlock block,
        int width,
        ToolDisplayMode toolDisplayMode,
        TimeProvider? timeProvider = null,
        TranscriptGlyphs? glyphs = null)
    {
        ArgumentNullException.ThrowIfNull(block);

        var effectiveGlyphs = glyphs ?? TranscriptGlyphs.Unicode;
        var safeWidth = width > 0 ? width : 1;
        var reserved = GutterReservedCells(block, toolDisplayMode);
        var contentWidth = Math.Max(1, safeWidth - reserved);
        var lines = new List<TranscriptRenderLine>();

        switch (block)
        {
            case AssistantTranscriptBlock assistant:
                AppendMarkdown(lines, assistant.Text, contentWidth);
                TagMessageRows(lines, assistant.Complete ? TranscriptGutterKind.AgentComplete : TranscriptGutterKind.AgentActive);
                break;

            case UserTranscriptBlock user:
                AppendUser(lines, user, contentWidth);
                TagMessageRows(lines, TranscriptGutterKind.UserMarker);
                break;

            case PendingUserTranscriptBlock pending:
                AppendPendingUser(lines, pending, contentWidth);
                TagMessageRows(lines, TranscriptGutterKind.UserMarker);
                break;

            case ToolTranscriptBlock tool:
                if (toolDisplayMode != ToolDisplayMode.Hidden)
                {
                    AppendTool(lines, tool, contentWidth, toolDisplayMode);
                }
                break;

            case ToolActivityTranscriptBlock activity:
                AppendToolActivity(lines, activity, contentWidth, toolDisplayMode);
                break;

            case CommandOutputTranscriptBlock command:
                AppendPreformatted(lines, command.Text, safeWidth, TranscriptRole.Code);
                break;

            case ContextUsageTranscriptBlock usage:
                AppendContextUsage(lines, usage.Usage, safeWidth);
                break;

            case DiffTranscriptBlock diff:
                AppendDiff(lines, diff.Patch, safeWidth);
                break;

            case PermissionTranscriptBlock permission:
                AppendWrapped(lines, FormatPermission(permission), safeWidth, PermissionRole(permission.Allowed));
                break;

            case UserQuestionTranscriptBlock question:
                AppendWrapped(lines, FormatQuestion(question), safeWidth, TranscriptRole.Question);
                break;

            case NoticeTranscriptBlock notice:
                AppendWrapped(lines, notice.Text, safeWidth, RoleFor(notice.Level));
                break;

            case SessionBoundaryTranscriptBlock boundary:
                AppendWrapped(lines, $"── session {boundary.SessionId} ──", safeWidth, TranscriptRole.Notification);
                break;

            case ThinkingTranscriptBlock thinking:
                if (toolDisplayMode != ToolDisplayMode.Hidden)
                {
                    AppendThinking(lines, thinking, contentWidth, toolDisplayMode, timeProvider);
                    TagMessageRows(lines, thinking.Complete ? TranscriptGutterKind.AgentComplete : TranscriptGutterKind.AgentActive);
                }

                break;
        }

        ApplyGutters(lines, effectiveGlyphs);
        return lines;
    }

    /// <summary>Joins the formatted lines with newlines, for a plain-text projection of a block.</summary>
    public static string FormatPlainText(
        TranscriptBlock block,
        int width,
        ToolDisplayMode toolDisplayMode = ToolDisplayMode.Full,
        TranscriptGlyphs? glyphs = null) =>
        string.Join('\n', Format(block, width, toolDisplayMode, null, glyphs).Select(line => line.Text));

    /// <summary>Cells the gutter reserves for <paramref name="block"/>, so content is wrapped narrow enough
    /// that the prefix always fits inside the viewport width.</summary>
    internal static int GutterReservedCells(TranscriptBlock block, ToolDisplayMode toolDisplayMode) => block switch
    {
        UserTranscriptBlock => TranscriptGlyphs.MarkerCells,
        PendingUserTranscriptBlock => TranscriptGlyphs.MarkerCells,
        AssistantTranscriptBlock => TranscriptGlyphs.MarkerCells,
        ThinkingTranscriptBlock => TranscriptGlyphs.MarkerCells,
        ToolTranscriptBlock => TranscriptGlyphs.ChildCells,
        ToolActivityTranscriptBlock => TranscriptGlyphs.ChildCells,
        _ => 0,
    };

    /// <summary>
    /// Applies gutter prefixes to all tagged rows in <paramref name="lines"/>, mutating text and adjusting
    /// <see cref="TranscriptRenderLine.PrefixCells"/> and <see cref="TranscriptRenderLine.Links"/> offsets.
    /// </summary>
    internal static void ApplyGutters(List<TranscriptRenderLine> lines, TranscriptGlyphs glyphs)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var kind = line.Gutter;
            if (kind == TranscriptGutterKind.None)
            {
                continue;
            }

            // An empty row never takes an indent or a connector: a whitespace-only row would add trailing
            // whitespace to copied text and hang a connector off a blank line. Marker rows are the exception
            // — the marker is what identifies the entry, so it is drawn even with no text beside it.
            if (line.Text.Length == 0 && !IsMarkerKind(kind))
            {
                continue;
            }

            var prefix = glyphs.Prefix(kind);
            var shift = TerminalCellText.Width(prefix);
            var newPrefixCells = line.PrefixCells > 0 ? line.PrefixCells + shift : 0;
            lines[i] = line with
            {
                Text = prefix + line.Text,
                PrefixCells = newPrefixCells,
                Links = ShiftLinkSpans(line.Links, shift),
            };
        }
    }

    /// <summary>Whether <paramref name="kind"/> opens an entry (and so always draws its marker).</summary>
    private static bool IsMarkerKind(TranscriptGutterKind kind) =>
        kind is TranscriptGutterKind.UserMarker
            or TranscriptGutterKind.AgentActive
            or TranscriptGutterKind.AgentComplete;

    /// <summary>
    /// Tags rows from <paramref name="start"/> to the end of <paramref name="lines"/> as dependent child
    /// rows, terminating the tree on the last row that actually carries content. Text ending in a newline
    /// yields a trailing empty row (command output almost always does), so choosing the terminator by
    /// physical index alone would hang the closing connector off a blank line while the last visible row
    /// kept a continuing one.
    /// </summary>
    private static void TagChildRows(List<TranscriptRenderLine> lines, int start)
    {
        var last = -1;
        for (var i = start; i < lines.Count; i++)
        {
            if (lines[i].Text.Length > 0)
            {
                last = i;
            }
        }

        for (var i = start; i < lines.Count; i++)
        {
            lines[i] = lines[i] with
            {
                Gutter = i == last ? TranscriptGutterKind.LastChild : TranscriptGutterKind.Child,
            };
        }
    }

    /// <summary>
    /// Tags row 0 of <paramref name="lines"/> with <paramref name="firstRowKind"/> and all subsequent rows
    /// with <see cref="TranscriptGutterKind.Continuation"/>. Used for message-level blocks (user, assistant,
    /// thinking) where every wrapped row is a continuation of the same entry.
    /// </summary>
    internal static void TagMessageRows(List<TranscriptRenderLine> lines, TranscriptGutterKind firstRowKind)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            lines[i] = lines[i] with
            {
                Gutter = i == 0 ? firstRowKind : TranscriptGutterKind.Continuation,
            };
        }
    }

    /// <summary>
    /// Formats the text of an assistant block at <paramref name="contentWidth"/> without tagging or applying
    /// gutters. Used by <see cref="IncrementalMarkdownFormatter"/> to format individual segments before the
    /// final gutter pass over the assembled result.
    /// </summary>
    internal static IReadOnlyList<TranscriptRenderLine> FormatAssistantContent(string text, int contentWidth)
    {
        var lines = new List<TranscriptRenderLine>();
        AppendMarkdown(lines, text, contentWidth);
        return lines;
    }

    /// <summary>Returns true when <paramref name="status"/> is a terminal (non-running) tool call status.</summary>
    private static bool IsTerminalStatus(ToolCallStatus status) =>
        status is ToolCallStatus.Succeeded or ToolCallStatus.Failed or ToolCallStatus.Cancelled or ToolCallStatus.Skipped;

    /// <summary>
    /// The canonical semantic style for each <c>/context</c> category: a distinct, shape-legible glyph and
    /// the semantic role that colors its line. Exposed so tests can assert every category maps to a distinct
    /// marker/role even when the driver drops to 16 colors. Unknown names fall back to a neutral diamond.
    /// </summary>
    internal static (char Glyph, TranscriptRole Role) ContextCategoryStyle(string categoryName) => categoryName switch
    {
        "System prompt" => ('\u25c6', TranscriptRole.ContextSystemPrompt),       // ◆
        "System tools" => ('\u25b2', TranscriptRole.ContextSystemTools),         // ▲
        "MCP tools" => ('\u25cf', TranscriptRole.ContextMcpTools),               // ●
        "Messages" => ('\u25a0', TranscriptRole.ContextMessages),                // ■
        "Autocompact buffer" => ('\u2592', TranscriptRole.ContextAutocompactBuffer), // ▒
        "Free space" => ('\u2591', TranscriptRole.ContextFreeSpace),             // ░
        _ => ('\u25c6', TranscriptRole.ContextSystemPrompt),
    };

    /// <summary>
    /// Projects context-window usage onto a compact, no-blank-line block: a heading, a one-line summary, an
    /// optional "estimated" note, then exactly one line per category. Each category line carries its distinct
    /// glyph, a proportional mini-bar (bounded to <see cref="ContextBarWidth"/> repetitions of the glyph),
    /// the category label, and the token/percentage text — all under the category's semantic role. No empty
    /// separator rows are emitted, so the breakdown never renders a blank line between events.
    /// </summary>
    private static void AppendContextUsage(List<TranscriptRenderLine> lines, ContextUsageData usage, int width)
    {
        var approx = usage.IsExact ? string.Empty : "~";

        lines.Add(new TranscriptRenderLine($"Context Usage \u00b7 {usage.Model}", TranscriptRole.Heading));

        var messages = usage.MessageCount == 1 ? "message" : "messages";
        var summary =
            $"{approx}{usage.UsedTokens.ToString("N0", CultureInfo.InvariantCulture)} / " +
            $"{usage.MaxTokens.ToString("N0", CultureInfo.InvariantCulture)} tokens " +
            $"({usage.Percentage}%) across {usage.MessageCount} {messages}";
        lines.Add(new TranscriptRenderLine(summary, TranscriptRole.Notification));

        if (!usage.IsExact)
        {
            lines.Add(new TranscriptRenderLine(
                "(estimated \u2014 provider has no token-counting API or it was unavailable)",
                TranscriptRole.Notification));
        }

        foreach (var category in usage.Categories)
        {
            lines.Add(FormatContextCategory(category, usage.MaxTokens, approx));
        }
    }

    /// <summary>The maximum number of glyph repetitions in a category's proportional mini-bar.</summary>
    private const int ContextBarWidth = 10;

    private static TranscriptRenderLine FormatContextCategory(ContextUsageCategory category, int maxTokens, string approx)
    {
        var (glyph, role) = ContextCategoryStyle(category.Name);
        var pct = maxTokens <= 0 ? 0 : (int)Math.Round(category.Tokens * 100.0 / maxTokens);

        var filled = 0;
        if (maxTokens > 0 && category.Tokens > 0)
        {
            filled = Math.Clamp((int)Math.Round(category.Tokens * (double)ContextBarWidth / maxTokens), 1, ContextBarWidth);
        }

        var builder = new StringBuilder();
        builder.Append(glyph);
        if (filled > 0)
        {
            builder.Append(' ').Append(new string(glyph, filled));
        }

        builder.Append(' ').Append(category.Name);
        builder.Append("  ").Append(approx)
            .Append(category.Tokens.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" tokens (").Append(pct).Append("%)");

        return new TranscriptRenderLine(builder.ToString(), role);
    }

    private static void AppendMarkdown(List<TranscriptRenderLine> lines, string text, int width)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var document = Markdig.Markdown.Parse(NormalizeNewlines(text), Pipeline);
        var first = true;
        foreach (var node in document)
        {
            if (!first)
            {
                lines.Add(new TranscriptRenderLine(string.Empty, TranscriptRole.Assistant));
            }

            first = false;
            AppendBlock(lines, node, width);
        }
    }

    private static void AppendBlock(List<TranscriptRenderLine> lines, Block node, int width, string indent = "")
    {
        switch (node)
        {
            case HeadingBlock heading:
                AppendWrappedInline(lines, heading.Inline, width, TranscriptRole.Heading, indent);
                break;

            case ParagraphBlock paragraph:
                AppendWrappedInline(lines, paragraph.Inline, width, TranscriptRole.Assistant, indent);
                break;

            case Markdig.Syntax.CodeBlock code:
                AppendCode(lines, code.Lines.ToString(), width, indent);
                break;

            case QuoteBlock quote:
                var callout = DetectCallout(quote);
                if (callout is { } type)
                {
                    AppendCallout(lines, quote, type, width, indent);
                }
                else
                {
                    var innerFirst = true;
                    foreach (var child in quote)
                    {
                        if (!innerFirst)
                        {
                            lines.Add(new TranscriptRenderLine(indent, TranscriptRole.Assistant));
                        }

                        innerFirst = false;
                        AppendBlock(lines, child, width, indent);
                    }
                }

                break;

            case ListBlock list:
                AppendList(lines, list, width, indent);
                break;

            case LeafBlock leaf when leaf.Inline is not null:
                AppendWrappedInline(lines, leaf.Inline, width, TranscriptRole.Assistant, indent);
                break;
        }
    }

    // ---------------------------------------------------------------------------
    // Callout detection and rendering (GitHub-style > [!TYPE] blockquote syntax)
    // ---------------------------------------------------------------------------

    private enum CalloutType { Note, Tip, Important, Warning, Caution }

    /// <summary>
    /// Returns the unicode or ASCII glyph for a callout <see cref="TranscriptRole"/>.
    /// Unicode is used in production; the ASCII form is the fallback for environments that cannot
    /// display the unicode symbol (exposed <c>internal</c> so tests can verify both forms).
    /// </summary>
    internal static string CalloutGlyph(TranscriptRole role, bool ascii = false) => role switch
    {
        TranscriptRole.CalloutNote => ascii ? "i" : "ℹ",
        TranscriptRole.CalloutTip => ascii ? "*" : "✦",
        TranscriptRole.CalloutImportant => ascii ? "!!" : "‼",
        TranscriptRole.CalloutWarning => ascii ? "!" : "⚠",
        TranscriptRole.CalloutCaution => ascii ? "x" : "⊗",
        _ => ascii ? "?" : "ℹ",
    };

    /// <summary>
    /// Checks whether <paramref name="quote"/>'s first block is a paragraph whose leading inline text
    /// is exactly <c>[!TYPE]</c> (case-insensitive, optional surrounding whitespace). Returns the
    /// recognized <see cref="CalloutType"/>, or <c>null</c> when the blockquote is a plain quote.
    /// GitHub semantics: the marker must be alone on the first line; trailing text or an unknown type
    /// make this a plain blockquote (no false positives).
    /// </summary>
    private static CalloutType? DetectCallout(QuoteBlock quote)
    {
        if (quote.Count == 0 || quote[0] is not ParagraphBlock firstParagraph)
        {
            return null;
        }

        var container = firstParagraph.Inline;
        if (container is null)
        {
            return null;
        }

        // Collect consecutive LiteralInline nodes from the start of the paragraph until the first
        // non-literal. Per GitHub semantics, the marker must be ALONE on the first line: if anything
        // other than a LineBreakInline (soft/hard break) immediately follows the leading literals, the
        // marker has inline-formatted content on the same line (emphasis, code, link, …) and is NOT a
        // callout. A LineBreakInline means the marker ends cleanly at a line boundary.
        var firstLineText = new System.Text.StringBuilder();
        foreach (var inline in container)
        {
            if (inline is LiteralInline literal)
            {
                firstLineText.Append(literal.Content.ToString());
            }
            else if (inline is LineBreakInline)
            {
                // Clean line break — the marker occupies the first line alone.
                break;
            }
            else
            {
                // Inline-formatted content (emphasis, code span, link, image, …) on the same line
                // as the marker → not a valid callout per GitHub spec.
                return null;
            }
        }

        return ParseCalloutType(firstLineText.ToString());
    }

    /// <summary>
    /// Parses a trimmed first-line string as a callout marker (<c>[!TYPE]</c>). Returns the
    /// <see cref="CalloutType"/> when the string is exactly <c>[!TYPE]</c> with no extra content;
    /// returns <c>null</c> for unknown types, trailing text, or malformed markers.
    /// </summary>
    private static CalloutType? ParseCalloutType(string markerText)
    {
        var trimmed = markerText.Trim();
        if (trimmed.Length < 4 || trimmed[0] != '[' || trimmed[1] != '!' || trimmed[^1] != ']')
        {
            return null;
        }

        // Inner text must match exactly (no interior whitespace); ToUpperInvariant gives case-insensitivity.
        var type = trimmed[2..^1];
        return type.ToUpperInvariant() switch
        {
            "NOTE" => CalloutType.Note,
            "TIP" => CalloutType.Tip,
            "IMPORTANT" => CalloutType.Important,
            "WARNING" => CalloutType.Warning,
            "CAUTION" => CalloutType.Caution,
            _ => null,
        };
    }

    /// <summary>
    /// Returns the <see cref="TranscriptRole"/> and display label for a callout type.
    /// </summary>
    private static (TranscriptRole Role, string Label) CalloutRoleAndLabel(CalloutType type) => type switch
    {
        CalloutType.Note => (TranscriptRole.CalloutNote, "NOTE"),
        CalloutType.Tip => (TranscriptRole.CalloutTip, "TIP"),
        CalloutType.Important => (TranscriptRole.CalloutImportant, "IMPORTANT"),
        CalloutType.Warning => (TranscriptRole.CalloutWarning, "WARNING"),
        CalloutType.Caution => (TranscriptRole.CalloutCaution, "CAUTION"),
        _ => (TranscriptRole.CalloutNote, "NOTE"),
    };

    /// <summary>
    /// Renders a detected callout: a glyph+label title row in the callout's role, followed by body rows
    /// each prefixed with <c>│ </c>. The bar glyph stays in the row text (so copy still includes it);
    /// its color comes from <c>PrefixRole</c> set to the callout role while the body text uses the normal
    /// <c>Role</c> (Assistant, Code, …).
    /// </summary>
    private static void AppendCallout(
        List<TranscriptRenderLine> lines,
        QuoteBlock quote,
        CalloutType type,
        int width,
        string indent)
    {
        var (role, label) = CalloutRoleAndLabel(type);
        var glyph = CalloutGlyph(role);

        // Title row: "<glyph> LABEL" in the callout role.
        AppendWrapped(lines, $"{glyph} {label}", width, role, indent);

        // Body bar prefix: each body row is indented with "│ " so the bar is visible as the
        // left boundary of the callout body, mirroring the list-indent discipline.
        var barPrefix = indent + "│ ";
        // The bar glyph and trailing space together occupy this many display cells; these cells
        // are drawn in the callout role color via PrefixRole/PrefixCells on each body row.
        var prefixCells = TerminalCellText.Width(barPrefix);
        var firstParagraph = (ParagraphBlock)quote[0];

        var bodyStart = lines.Count;
        AppendCalloutFirstParagraphBody(lines, firstParagraph, width, barPrefix);

        // Subsequent child blocks of the blockquote (second paragraph onward, code blocks, etc.).
        var hadBody = lines.Count > bodyStart;
        for (var i = 1; i < quote.Count; i++)
        {
            if (hadBody)
            {
                lines.Add(new TranscriptRenderLine(barPrefix, TranscriptRole.Assistant));
            }

            hadBody = true;
            AppendBlock(lines, quote[i], width, barPrefix);
        }

        // Apply prefix coloring to all body rows: the first PrefixCells cells (the "│ " bar) draw in
        // the callout role color; the remainder draws in the row's own Role (Assistant, Code, …).
        // The `with` expression preserves all other properties including Links.
        for (var j = bodyStart; j < lines.Count; j++)
        {
            lines[j] = lines[j] with { PrefixCells = prefixCells, PrefixRole = role };
        }
    }

    /// <summary>
    /// Renders the body portion of a callout's first paragraph (everything after the <c>[!TYPE]</c>
    /// marker line) through the link-aware path so that URLs in the body produce
    /// <see cref="LinkSpan"/>s exactly like subsequent paragraphs do.
    /// </summary>
    /// <remarks>
    /// Markdig folds the marker line and the body line into a single <see cref="ParagraphBlock"/>
    /// separated by a <see cref="LineBreakInline"/>. This method skips every inline up to and including
    /// that break, then renders the remaining siblings with <see cref="AppendWrappedText"/>.
    /// Does nothing when the paragraph contains only the marker (no body content).
    /// </remarks>
    private static void AppendCalloutFirstParagraphBody(
        List<TranscriptRenderLine> lines,
        ParagraphBlock firstParagraph,
        int width,
        string barPrefix)
    {
        // Walk the inline tree to find the LineBreakInline that ends the [!TYPE] marker.
        Inline? bodyStart = null;
        if (firstParagraph.Inline is not null)
        {
            foreach (var node in firstParagraph.Inline)
            {
                if (node is LineBreakInline)
                {
                    bodyStart = node.NextSibling;
                    break;
                }
            }
        }

        if (bodyStart is null)
        {
            return; // marker-only paragraph — no body to render
        }

        // Render body inlines using the same per-node helper that RenderInlineWithLinks uses,
        // but walking NextSibling rather than iterating a ContainerInline.
        var linkRecords = new List<InlineLinkRecord>();
        var builder = new StringBuilder();
        for (var node = bodyStart; node is not null; node = node.NextSibling)
        {
            RenderInlineNodeWithLinks(node, builder, linkRecords);
        }

        AppendWrappedText(lines, builder.ToString(), linkRecords, width, TranscriptRole.Assistant, barPrefix);
    }

    private static void AppendList(List<TranscriptRenderLine> lines, ListBlock list, int width, string indent)
    {
        var order = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem)
            {
                continue;
            }

            var marker = list.IsOrdered ? $"{order++}. " : "• ";
            // Continuation lines (and nested blocks) align under the item text, not the marker.
            var contentIndent = indent + new string(' ', marker.Length);
            var itemStart = lines.Count;

            foreach (var child in listItem)
            {
                AppendBlock(lines, child, width, contentIndent);
            }

            // Replace the padding at the front of the item's first line with the actual marker.
            if (lines.Count > itemStart)
            {
                var firstLine = lines[itemStart];
                var prefixLength = indent.Length + marker.Length;
                var text = firstLine.Text;
                var suffix = text.Length >= prefixLength ? text[prefixLength..] : string.Empty;
                lines[itemStart] = firstLine with { Text = indent + marker + suffix };
            }
        }
    }

    private static void AppendCode(List<TranscriptRenderLine> lines, string code, int width, string indent = "")
    {
        var contentWidth = EffectiveWidth(width, indent);
        foreach (var line in SplitLines(code))
        {
            // Code is preformatted: preserve whitespace, only hard-breaking lines wider than the viewport.
            foreach (var wrapped in WrapPreformatted(line, contentWidth))
            {
                lines.Add(new TranscriptRenderLine(indent + wrapped, TranscriptRole.Code));
            }
        }
    }

    private static void AppendDiff(List<TranscriptRenderLine> lines, string patch, int width)
    {
        foreach (var line in SplitLines(patch))
        {
            foreach (var wrapped in WrapPreformatted(line, width))
            {
                lines.Add(new TranscriptRenderLine(wrapped, TranscriptRole.Diff));
            }
        }
    }

    private static void AppendTool(
        List<TranscriptRenderLine> lines,
        ToolTranscriptBlock tool,
        int width,
        ToolDisplayMode toolDisplayMode)
    {
        var role = tool.IsError ? TranscriptRole.Error : TranscriptRole.Tool;
        var header = new StringBuilder(tool.ToolName);
        var input = toolDisplayMode == ToolDisplayMode.Compact
            ? ToolDisplayModeText.ArgumentPreview(tool.InputJson)
            : tool.InputJson.Trim();
        if (!string.IsNullOrWhiteSpace(input))
        {
            header.Append(' ').Append(input);
        }

        if (toolDisplayMode == ToolDisplayMode.Compact)
        {
            header.Append(tool.Complete
                ? tool.IsError ? " [error]" : " [success]"
                : " [running]");
        }
        else if (tool.ElapsedMs is { } ms)
        {
            header.Append(" (").Append(ms.ToString(CultureInfo.InvariantCulture)).Append("ms)");
        }
        else if (!tool.Complete)
        {
            header.Append(" (running)");
        }

        if (toolDisplayMode != ToolDisplayMode.Compact && tool.IsError)
        {
            header.Append(" [error]");
        }

        AppendPreformatted(lines, header.ToString(), width, role);

        // Tag the header rows: first = AgentComplete/AgentActive, the rest = Continuation.
        var headerKind = tool.Complete ? TranscriptGutterKind.AgentComplete : TranscriptGutterKind.AgentActive;
        for (var i = 0; i < lines.Count; i++)
        {
            lines[i] = lines[i] with { Gutter = i == 0 ? headerKind : TranscriptGutterKind.Continuation };
        }

        if (toolDisplayMode == ToolDisplayMode.Full && tool.Result is { Length: > 0 } result)
        {
            var resultStart = lines.Count;
            foreach (var line in SplitLines(result))
            {
                foreach (var wrapped in WrapPreformatted(line, width))
                {
                    lines.Add(new TranscriptRenderLine(wrapped, role));
                }
            }

            TagChildRows(lines, resultStart);
        }
    }

    private static void AppendToolActivity(
        List<TranscriptRenderLine> lines,
        ToolActivityTranscriptBlock activity,
        int width,
        ToolDisplayMode toolDisplayMode)
    {
        switch (toolDisplayMode)
        {
            case ToolDisplayMode.Hidden:
                return;
            case ToolDisplayMode.Summary:
                AppendToolActivitySummary(lines, activity, width);
                return;
            case ToolDisplayMode.Compact:
                AppendToolActivityCompact(lines, activity, width);
                return;
            default:
                AppendToolActivityFull(lines, activity, width);
                return;
        }
    }

    /// <summary>
    /// Projects a <see cref="ThinkingTranscriptBlock"/> per the requested display mode.
    /// <list type="bullet">
    /// <item><term>Full</term><description>Status line + full reasoning text (streamed tail while active).</description></item>
    /// <item><term>Compact</term><description>Status line + last ~5 lines of reasoning (streamed tail).</description></item>
    /// <item><term>Summary</term><description>Status one-liner only.</description></item>
    /// </list>
    /// Hidden is already guarded by the caller. Elapsed time is computed live from <see
    /// cref="ThinkingTranscriptBlock.StartedAt"/> when the block is active and frozen from
    /// <see cref="ThinkingTranscriptBlock.ElapsedMs"/> when complete.
    /// </summary>
    private static void AppendThinking(
        List<TranscriptRenderLine> lines,
        ThinkingTranscriptBlock thinking,
        int width,
        ToolDisplayMode displayMode,
        TimeProvider? timeProvider = null)
    {
        var status = FormatThinkingStatus(thinking, timeProvider);
        AppendWrapped(lines, status, width, TranscriptRole.Notification);

        if (displayMode == ToolDisplayMode.Summary)
        {
            return;
        }

        if (string.IsNullOrEmpty(thinking.Text))
        {
            return;
        }

        if (displayMode == ToolDisplayMode.Full)
        {
            // Full: stream the entire reasoning; completed turns show all text.
            AppendMarkdown(lines, thinking.Text, width);
            return;
        }

        // Compact: show the last ~5 lines (streaming tail) without re-parsing the full markdown.
        // We split the raw text, take the tail, and append as preformatted lines.
        const int CompactTailLines = 5;
        var allLines = SplitLines(thinking.Text).ToList();
        // Remove trailing empty lines to avoid wasted blank rows at the tail.
        while (allLines.Count > 0 && string.IsNullOrEmpty(allLines[^1]))
        {
            allLines.RemoveAt(allLines.Count - 1);
        }

        var tailStart = Math.Max(0, allLines.Count - CompactTailLines);
        for (var i = tailStart; i < allLines.Count; i++)
        {
            foreach (var wrapped in WrapPreformatted(allLines[i], width))
            {
                lines.Add(new TranscriptRenderLine(wrapped, TranscriptRole.Notification));
            }
        }
    }

    /// <summary>
    /// Returns the one-line thinking status: "💭 Thinking… Xs · N tok" while active, or
    /// "💭 Thought for Xs" when complete. Elapsed is computed live when the burst is active,
    /// using the injected <paramref name="timeProvider"/> (defaults to <see cref="TimeProvider.System"/>).
    /// </summary>
    private static string FormatThinkingStatus(ThinkingTranscriptBlock thinking, TimeProvider? timeProvider = null)
    {
        long elapsedMs;
        if (thinking.ElapsedMs is { } frozen)
        {
            elapsedMs = frozen;
        }
        else
        {
            // Live: compute from StartedAt via the injectable clock so tests are deterministic.
            // The render loop drives periodic refresh so the elapsed ticks are decoupled from delta arrival.
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            elapsedMs = (long)Math.Max(0, (now - thinking.StartedAt.ToUniversalTime()).TotalMilliseconds);
        }

        var seconds = elapsedMs / 1000;
        var sb = new StringBuilder();
        if (thinking.Complete)
        {
            sb.Append("\U0001f4ad Thought for ").Append(seconds).Append('s');
        }
        else
        {
            sb.Append("\U0001f4ad Thinking\u2026 ").Append(seconds).Append('s');
            if (thinking.ThinkingTokens is { } tokens)
            {
                sb.Append(" \u00b7 ").Append(tokens.ToString(CultureInfo.InvariantCulture)).Append(" tok");
            }
        }

        return sb.ToString();
    }

    private static void AppendToolActivitySummary(
        List<TranscriptRenderLine> lines,
        ToolActivityTranscriptBlock activity,
        int width)
    {
        var summary = ActivitySummary(activity);
        if (activity.CompletionState != ToolActivityCompletionState.Active)
        {
            var role = SummaryRole(summary);
            var headerStart = lines.Count;
            AppendActivityLine(lines, ToolActivityPreview.CompletedText(summary), width, role);
            // Tag the header row as complete.
            for (var i = headerStart; i < lines.Count; i++)
            {
                lines[i] = lines[i] with { Gutter = TranscriptGutterKind.AgentComplete };
            }

            return;
        }

        var shellCommands = activity.Calls.Length > 0 &&
            activity.Calls.All(call => call.ToolName == "run_command");
        var noun = shellCommands
            ? activity.Calls.Length == 1 ? "shell command" : "shell commands"
            : activity.Calls.Length == 1 ? "tool" : "tools";
        var headerLineStart = lines.Count;
        AppendActivityLine(lines, $"Running {activity.Calls.Length} {noun}...", width, TranscriptRole.Tool);
        for (var i = headerLineStart; i < lines.Count; i++)
        {
            lines[i] = lines[i] with { Gutter = TranscriptGutterKind.AgentActive };
        }

        var running = activity.Calls.Where(call => call.Status == ToolCallStatus.Running).ToArray();
        if (running.Length <= 5)
        {
            for (var index = 0; index < running.Length; index++)
            {
                var childStart = lines.Count;
                AppendActivityLine(
                    lines,
                    ToolActivityPreview.Create(running[index].ToolName, running[index].InputJson),
                    width,
                    TranscriptRole.Tool);
                var isLast = index == running.Length - 1;
                for (var i = childStart; i < lines.Count; i++)
                {
                    lines[i] = lines[i] with { Gutter = isLast ? TranscriptGutterKind.LastChild : TranscriptGutterKind.Child };
                }
            }

            return;
        }

        for (var index = 0; index < 4; index++)
        {
            var childStart = lines.Count;
            AppendActivityLine(
                lines,
                ToolActivityPreview.Create(running[index].ToolName, running[index].InputJson),
                width,
                TranscriptRole.Tool);
            for (var i = childStart; i < lines.Count; i++)
            {
                lines[i] = lines[i] with { Gutter = TranscriptGutterKind.Child };
            }
        }

        var overflowStart = lines.Count;
        AppendActivityLine(lines, $"...and {running.Length - 4} more", width, TranscriptRole.Tool);
        for (var i = overflowStart; i < lines.Count; i++)
        {
            lines[i] = lines[i] with { Gutter = TranscriptGutterKind.LastChild };
        }
    }

    private static void AppendToolActivityCompact(
        List<TranscriptRenderLine> lines,
        ToolActivityTranscriptBlock activity,
        int width)
    {
        foreach (var call in activity.Calls)
        {
            var elapsed = call.ElapsedMs is { } milliseconds
                ? $" ({milliseconds.ToString(CultureInfo.InvariantCulture)}ms)"
                : string.Empty;
            var callStart = lines.Count;
            AppendActivityLine(
                lines,
                $"{ToolActivityPreview.Create(call.ToolName, call.InputJson)} [{ActivityStatusText(call.Status)}]{elapsed}",
                width,
                RoleFor(call));
            var gutter = IsTerminalStatus(call.Status) ? TranscriptGutterKind.AgentComplete : TranscriptGutterKind.AgentActive;
            for (var i = callStart; i < lines.Count; i++)
            {
                lines[i] = lines[i] with { Gutter = gutter };
            }
        }
    }

    private static void AppendToolActivityFull(
        List<TranscriptRenderLine> lines,
        ToolActivityTranscriptBlock activity,
        int width)
    {
        foreach (var call in activity.Calls)
        {
            var header = new StringBuilder(ActivityToolName(call.ToolName));
            var input = TerminalTextSanitizer.Sanitize(call.InputJson).Trim();
            if (input.Length > 0)
            {
                header.Append(' ').Append(input);
            }

            header.Append(" [").Append(ActivityStatusText(call.Status)).Append(']');
            if (call.ElapsedMs is { } milliseconds)
            {
                header.Append(" (").Append(milliseconds.ToString(CultureInfo.InvariantCulture)).Append("ms)");
            }

            var role = RoleFor(call);
            var headerStart = lines.Count;
            AppendPreformatted(lines, header.ToString(), width, role);
            var headerKind = IsTerminalStatus(call.Status) ? TranscriptGutterKind.AgentComplete : TranscriptGutterKind.AgentActive;
            for (var i = headerStart; i < lines.Count; i++)
            {
                lines[i] = lines[i] with { Gutter = i == headerStart ? headerKind : TranscriptGutterKind.Continuation };
            }

            var childStart = lines.Count;
            if (!string.IsNullOrEmpty(call.Result))
            {
                AppendPreformatted(lines, TerminalTextSanitizer.Sanitize(call.Result), width, role);
            }

            if (!string.IsNullOrEmpty(call.Error))
            {
                AppendPreformatted(lines, $"Error: {TerminalTextSanitizer.Sanitize(call.Error)}", width, role);
            }

            TagChildRows(lines, childStart);
        }
    }

    private static void AppendActivityLine(
        List<TranscriptRenderLine> lines,
        string text,
        int width,
        TranscriptRole role) =>
        lines.Add(new TranscriptRenderLine(ToolActivityPreview.TruncateToCells(text, width), role));

    private static ToolActivitySummary ActivitySummary(ToolActivityTranscriptBlock activity)
    {
        var homogeneousToolName = activity.Calls.Length > 0 &&
            activity.Calls.All(call => string.Equals(
                call.ToolName,
                activity.Calls[0].ToolName,
                StringComparison.Ordinal))
            ? activity.Calls[0].ToolName
            : null;
        var cancelledCalls = activity.Calls.Count(call => call.Status == ToolCallStatus.Cancelled);
        if (activity.CompletionState == ToolActivityCompletionState.Cancelled)
        {
            cancelledCalls = Math.Max(1, cancelledCalls);
        }

        return new ToolActivitySummary(
            activity.RootTurnId,
            activity.ActivityId,
            activity.Calls.Length,
            activity.Calls.Count(call => call.Status == ToolCallStatus.Failed),
            cancelledCalls,
            activity.Calls.Count(call => call.Status == ToolCallStatus.Skipped),
            homogeneousToolName);
    }

    private static string ActivityToolName(string toolName)
    {
        var sanitized = TerminalTextSanitizer.SanitizeSingleLine(toolName);
        return sanitized.Length == 0 ? "tool" : sanitized;
    }

    private static string ActivityStatusText(ToolCallStatus status) => status switch
    {
        ToolCallStatus.Pending => "pending",
        ToolCallStatus.AwaitingApproval => "awaiting approval",
        ToolCallStatus.Running => "running",
        ToolCallStatus.Succeeded => "success",
        ToolCallStatus.Failed => "error",
        ToolCallStatus.Cancelled => "cancelled",
        ToolCallStatus.Skipped => "skipped",
        _ => "unknown",
    };

    private static TranscriptRole RoleFor(ToolActivityCall call) => call.Status switch
    {
        ToolCallStatus.Failed => TranscriptRole.Error,
        ToolCallStatus.Cancelled => TranscriptRole.Warning,
        _ => TranscriptRole.Tool,
    };

    /// <summary>The semantic role colouring a finished tool-activity summary line. Green only when every
    /// call succeeded, red only when every call failed, orange for a mixed outcome; a cancelled batch with
    /// no failures stays a warning.</summary>
    internal static TranscriptRole SummaryRole(ToolActivitySummary summary)
    {
        if (summary.FailedCalls <= 0 && summary.Cancelled)
        {
            return TranscriptRole.Warning;
        }

        if (summary.FailedCalls <= 0)
        {
            return TranscriptRole.ToolSuccess;
        }

        if (summary.TotalCalls > 0 && summary.FailedCalls >= summary.TotalCalls)
        {
            return TranscriptRole.Error;
        }

        return TranscriptRole.ToolPartialFailure;
    }

    /// <summary>The semantic role colouring a permission transcript row based on the decision: approved
    /// tools are orange/yellow (noteworthy, not a failure), rejected tools are red (a rejection is
    /// exactly what red means), and pending decisions are a neutral question.</summary>
    internal static TranscriptRole PermissionRole(bool? allowed) => allowed switch
    {
        true => TranscriptRole.PermissionApproved,
        false => TranscriptRole.Permission,
        null => TranscriptRole.Question,
    };

    private static void AppendPreformatted(List<TranscriptRenderLine> lines, string text, int width, TranscriptRole role)
    {
        foreach (var line in SplitLines(text))
        {
            foreach (var wrapped in WrapPreformatted(line, width))
            {
                lines.Add(new TranscriptRenderLine(wrapped, role));
            }
        }
    }

    private static void AppendWrapped(List<TranscriptRenderLine> lines, string text, int width, TranscriptRole role, string indent = "")
    {
        var contentWidth = EffectiveWidth(width, indent);
        foreach (var line in SplitLines(text))
        {
            foreach (var wrapped in WrapLine(line, contentWidth))
            {
                lines.Add(new TranscriptRenderLine(indent + wrapped, role));
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Link-aware inline rendering (Part A — extraction + data model)
    // ---------------------------------------------------------------------------

    /// <summary>A link span expressed as character offsets in the pre-wrap rendered string,
    /// plus the destination URL and whether the display text honestly identifies it.</summary>
    private readonly record struct InlineLinkRecord(int CharStart, int CharEnd, string Url, bool TextMatchesUrl);

    /// <summary>
    /// The deceptive-link warning glyph appended immediately after a deceptive link's display text.
    /// ⚠ (U+26A0) is already used by the WARNING callout and verified to occupy exactly one terminal cell.
    /// </summary>
    private const char DeceptiveMarker = '\u26a0'; // ⚠

    /// <summary>
    /// Link-aware variant of <see cref="AppendWrapped"/> for blocks whose content comes from Markdig
    /// inline trees (paragraphs, headings, generic leaf blocks). Records link spans in terms of
    /// character positions in the rendered string, then threads them through <see cref="WrapLineWithLinks"/>
    /// so each produced <see cref="TranscriptRenderLine"/> carries the <see cref="LinkSpan"/>s that
    /// intersect its column range.
    /// </summary>
    private static void AppendWrappedInline(
        List<TranscriptRenderLine> lines,
        ContainerInline? container,
        int width,
        TranscriptRole role,
        string indent = "")
    {
        var linkRecords = new List<InlineLinkRecord>();
        var text = RenderInlineWithLinks(container, linkRecords);
        AppendWrappedText(lines, text, linkRecords, width, role, indent);
    }

    /// <summary>
    /// Wraps <paramref name="text"/> (with associated <paramref name="linkRecords"/>) into one or more
    /// <see cref="TranscriptRenderLine"/> entries, applying <paramref name="indent"/> and shifting link
    /// column spans by the indent's display-cell width. Shared between
    /// <see cref="AppendWrappedInline"/> and <see cref="AppendCalloutFirstParagraphBody"/>.
    /// </summary>
    private static void AppendWrappedText(
        List<TranscriptRenderLine> lines,
        string text,
        List<InlineLinkRecord> linkRecords,
        int width,
        TranscriptRole role,
        string indent)
    {
        var contentWidth = EffectiveWidth(width, indent);

        // WrapLineWithLinks produces LinkSpan columns relative to the wrapped text (column 0 = start of
        // the wrapped text). When the line is stored as (indent + wrappedText) the indent cells sit before
        // the text, so every link column must be shifted right by the indent's display-cell width.
        var indentWidth = TerminalCellText.Width(indent);

        // SplitLines may yield multiple source lines (e.g. from a hard line break inside an inline).
        // Link records are relative to the full rendered text; adjust them per source line.
        var lineCharOffset = 0;
        foreach (var sourceLine in SplitLines(text))
        {
            var lineEnd = lineCharOffset + sourceLine.Length;

            // Clip and shift link records to be relative to this source line.
            List<InlineLinkRecord>? lineLinks = null;
            foreach (var rec in linkRecords)
            {
                if (rec.CharEnd <= lineCharOffset || rec.CharStart >= lineEnd)
                {
                    continue;
                }

                var clipped = new InlineLinkRecord(
                    Math.Max(0, rec.CharStart - lineCharOffset),
                    Math.Min(sourceLine.Length, rec.CharEnd - lineCharOffset),
                    rec.Url,
                    rec.TextMatchesUrl);
                (lineLinks ??= new List<InlineLinkRecord>()).Add(clipped);
            }

            foreach (var (wrappedText, links) in WrapLineWithLinks(sourceLine, contentWidth, lineLinks))
            {
                lines.Add(new TranscriptRenderLine(indent + wrappedText, role)
                {
                    Links = ShiftLinkSpans(links, indentWidth),
                });
            }

            lineCharOffset += sourceLine.Length + 1; // +1 for the '\n' separator consumed by SplitLines
        }
    }

    /// <summary>
    /// Returns a new list with every span's <see cref="LinkSpan.StartColumn"/> and
    /// <see cref="LinkSpan.EndColumn"/> increased by <paramref name="shift"/> cells. Returns the
    /// original reference unchanged when <paramref name="shift"/> is zero or the list is empty.
    /// </summary>
    private static IReadOnlyList<LinkSpan>? ShiftLinkSpans(IReadOnlyList<LinkSpan>? links, int shift)
    {
        if (links is null || links.Count == 0 || shift == 0)
        {
            return links;
        }

        var shifted = new List<LinkSpan>(links.Count);
        foreach (var span in links)
        {
            shifted.Add(span with { StartColumn = span.StartColumn + shift, EndColumn = span.EndColumn + shift });
        }

        return shifted;
    }

    /// <summary>
    /// Renders an inline container to a string, collecting hyperlink spans as character-offset records.
    /// Deceptive links (display text does not identify the destination) have ⚠ appended so the span
    /// covers the marker. Non-link and image nodes are rendered identically to <see cref="RenderInline"/>.
    /// </summary>
    private static string RenderInlineWithLinks(ContainerInline? container, List<InlineLinkRecord> links)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        RenderInlineWithLinks(container, builder, links);
        return builder.ToString();
    }

    private static void RenderInlineWithLinks(ContainerInline container, StringBuilder builder, List<InlineLinkRecord> links)
    {
        foreach (var inline in container)
        {
            RenderInlineNodeWithLinks(inline, builder, links);
        }
    }

    /// <summary>
    /// Renders a single <see cref="Inline"/> node into <paramref name="builder"/>, recording any
    /// hyperlink span into <paramref name="links"/>. Shared by
    /// <see cref="RenderInlineWithLinks(ContainerInline,StringBuilder,List{InlineLinkRecord})"/> (which
    /// iterates a container) and the callout body walker (which iterates <c>NextSibling</c> links).
    /// </summary>
    private static void RenderInlineNodeWithLinks(Inline inline, StringBuilder builder, List<InlineLinkRecord> links)
    {
        switch (inline)
        {
            case LiteralInline literal:
                builder.Append(literal.Content.ToString());
                break;

            case CodeInline code:
                builder.Append(code.Content);
                break;

            case LineBreakInline lineBreak:
                builder.Append(lineBreak.IsHard ? '\n' : ' ');
                break;

            case LinkInline link:
                var linkStart = builder.Length;
                // Render the display text (recurse into children; for images this is the alt text).
                RenderInlineWithLinks(link, builder, links);
                var linkUrl = link.Url ?? string.Empty;
                // If no children produced text and we have a URL, use the URL as fallback display text
                // (handles links without explicit text in non-image links).
                if (builder.Length == linkStart && linkUrl.Length > 0 && !link.IsImage)
                {
                    builder.Append(linkUrl);
                }

                // Record link spans only for actual hyperlinks (not images) with non-empty URLs.
                if (!link.IsImage && linkUrl.Length > 0)
                {
                    var displayText = builder.ToString()[linkStart..];
                    var textMatchesUrl = ComputeTextMatchesUrl(displayText, linkUrl);
                    if (!textMatchesUrl)
                    {
                        builder.Append(DeceptiveMarker);
                    }

                    links.Add(new InlineLinkRecord(linkStart, builder.Length, linkUrl, textMatchesUrl));
                }

                break;

            case ContainerInline nested:
                RenderInlineWithLinks(nested, builder, links);
                break;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="displayText"/> (trimmed, case-insensitive)
    /// equals the full URL or equals the URL's host/authority (e.g. "example.com" for an https URL).
    /// </summary>
    private static bool ComputeTextMatchesUrl(string displayText, string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return true;
        }

        var trimmed = displayText.Trim();
        if (string.Equals(trimmed, url, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            string.Equals(trimmed, uri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------------------
    // Link-aware word wrapping
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Wraps a single pre-rendered logical line by display cells, threading link span records through so
    /// each yielded wrapped line gets the <see cref="LinkSpan"/>s (with line-local column positions) that
    /// fall on it. Functionally identical to <see cref="WrapLine"/> when <paramref name="linkRecords"/> is
    /// null or empty.
    /// </summary>
    private static IEnumerable<(string Text, IReadOnlyList<LinkSpan>? Links)> WrapLineWithLinks(
        string line,
        int width,
        IReadOnlyList<InlineLinkRecord>? linkRecords)
    {
        var cellWidth = width > 0 ? width : 1;
        if (line.Length == 0)
        {
            yield return (string.Empty, null);
            yield break;
        }

        var current = new StringBuilder();
        var currentWidth = 0;
        var currentPlacements = new List<(string Word, int WordCharStart, int LineCol)>();

        foreach (var (word, wordCharStart) in SplitWordsWithCharPositions(line))
        {
            if (word.Length == 0)
            {
                continue;
            }

            var wordWidth = TerminalCellText.Width(word);

            if (currentWidth == 0)
            {
                // Line is empty: place word or hard-break it.
                if (wordWidth <= cellWidth)
                {
                    currentPlacements.Add((word, wordCharStart, 0));
                    current.Append(word);
                    currentWidth = wordWidth;
                }
                else
                {
                    foreach (var (chunk, chunkCharStart, chunkWidth, isLast) in BreakWordWithCharPositions(word, wordCharStart, cellWidth))
                    {
                        if (isLast)
                        {
                            currentPlacements.Add((chunk, chunkCharStart, 0));
                            current.Append(chunk);
                            currentWidth = chunkWidth;
                        }
                        else
                        {
                            var chunkPlacements = new List<(string, int, int)> { (chunk, chunkCharStart, 0) };
                            yield return (chunk, ComputeLinkSpans(chunkPlacements, linkRecords));
                        }
                    }
                }

                continue;
            }

            if (currentWidth + 1 + wordWidth <= cellWidth)
            {
                // Word fits on the current line.
                currentPlacements.Add((word, wordCharStart, currentWidth + 1));
                current.Append(' ').Append(word);
                currentWidth += 1 + wordWidth;
                continue;
            }

            // Word does not fit: flush the current line and start a new one.
            yield return (current.ToString(), ComputeLinkSpans(currentPlacements, linkRecords));
            current.Clear();
            currentPlacements.Clear();
            currentWidth = 0;

            if (wordWidth <= cellWidth)
            {
                currentPlacements.Add((word, wordCharStart, 0));
                current.Append(word);
                currentWidth = wordWidth;
            }
            else
            {
                foreach (var (chunk, chunkCharStart, chunkWidth, isLast) in BreakWordWithCharPositions(word, wordCharStart, cellWidth))
                {
                    if (isLast)
                    {
                        currentPlacements.Add((chunk, chunkCharStart, 0));
                        current.Append(chunk);
                        currentWidth = chunkWidth;
                    }
                    else
                    {
                        var chunkPlacements = new List<(string, int, int)> { (chunk, chunkCharStart, 0) };
                        yield return (chunk, ComputeLinkSpans(chunkPlacements, linkRecords));
                    }
                }
            }
        }

        yield return (current.ToString(), ComputeLinkSpans(currentPlacements, linkRecords));
    }

    /// <summary>
    /// Splits <paramref name="line"/> at space boundaries, yielding each word and its inclusive
    /// character start position within <paramref name="line"/>. Empty words (from consecutive spaces)
    /// are included so the caller can skip them.
    /// </summary>
    private static IEnumerable<(string Word, int CharStart)> SplitWordsWithCharPositions(string line)
    {
        var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == ' ')
            {
                yield return (line[start..i], start);
                start = i + 1;
            }
        }

        yield return (line[start..], start);
    }

    /// <summary>
    /// Breaks an over-long word into display-cell-bounded chunks, also tracking each chunk's absolute
    /// character start position within the original source line (not just within the word).
    /// </summary>
    private static IEnumerable<(string Chunk, int ChunkAbsCharStart, int Width, bool IsLast)> BreakWordWithCharPositions(
        string word,
        int wordAbsCharStart,
        int width)
    {
        var chunks = new List<(string Chunk, int ChunkAbsCharStart, int Width)>();
        var builder = new StringBuilder();
        var builderWidth = 0;
        var chunkAbsStart = wordAbsCharStart;

        foreach (var element in TerminalCellText.Enumerate(word))
        {
            var clusterWidth = element.CellWidth;

            if (builderWidth > 0 && builderWidth + clusterWidth > width)
            {
                chunks.Add((builder.ToString(), chunkAbsStart, builderWidth));
                builder.Clear();
                builderWidth = 0;
                chunkAbsStart = wordAbsCharStart + element.Utf16Start;
            }

            builder.Append(element.Text);
            builderWidth += clusterWidth;
        }

        if (builder.Length > 0)
        {
            chunks.Add((builder.ToString(), chunkAbsStart, builderWidth));
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            yield return (chunks[i].Chunk, chunks[i].ChunkAbsCharStart, chunks[i].Width, i == chunks.Count - 1);
        }
    }

    /// <summary>
    /// Given the word placements on one wrapped line and the full set of link records (already adjusted
    /// to be relative to the source line), returns the <see cref="LinkSpan"/>s that fall on this wrapped
    /// line with columns measured from the line's left edge (column 0).
    /// </summary>
    private static IReadOnlyList<LinkSpan>? ComputeLinkSpans(
        IReadOnlyList<(string Word, int WordCharStart, int LineCol)> wordPlacements,
        IReadOnlyList<InlineLinkRecord>? linkRecords)
    {
        if (linkRecords is null || linkRecords.Count == 0 || wordPlacements.Count == 0)
        {
            return null;
        }

        List<LinkSpan>? result = null;

        foreach (var rec in linkRecords)
        {
            int? linkColStart = null;
            int? linkColEnd = null;

            foreach (var (word, wordCharStart, lineCol) in wordPlacements)
            {
                var wordCharEnd = wordCharStart + word.Length;
                // Skip words that do not overlap with the link's char range.
                if (wordCharEnd <= rec.CharStart || wordCharStart >= rec.CharEnd)
                {
                    continue;
                }

                // Compute the overlap within the word and convert to column offsets.
                var overlapStart = Math.Max(rec.CharStart, wordCharStart) - wordCharStart;
                var overlapEnd = Math.Min(rec.CharEnd, wordCharEnd) - wordCharStart;
                var colStart = lineCol + TerminalCellText.Width(word[..overlapStart]);
                var colEnd = lineCol + TerminalCellText.Width(word[..overlapEnd]);

                if (!linkColStart.HasValue || colStart < linkColStart.Value)
                {
                    linkColStart = colStart;
                }

                if (!linkColEnd.HasValue || colEnd > linkColEnd.Value)
                {
                    linkColEnd = colEnd;
                }
            }

            if (linkColStart.HasValue && linkColEnd.HasValue && linkColStart.Value < linkColEnd.Value)
            {
                (result ??= new List<LinkSpan>()).Add(
                    new LinkSpan(linkColStart.Value, linkColEnd.Value, rec.Url, rec.TextMatchesUrl));
            }
        }

        return result;
    }

    /// <summary>
    /// Projects a user message onto full-width background-block rows. When the block carries a send time it is
    /// formatted as a local <c>HH:mm</c> annotation on the first row; the first source line is wrapped into a
    /// narrower zone so the reserved time and trailing cells can never overlap the message text. The
    /// time is carried as <see cref="TranscriptRenderLine.RightText"/> (never mixed into the copyable text) and
    /// every row is marked <see cref="TranscriptRenderLine.FillWidth"/> so the whole block paints its distinct
    /// background across the visible width.
    /// </summary>
    private static void AppendUser(List<TranscriptRenderLine> lines, UserTranscriptBlock user, int width)
    {
        var time = user.SentAt is { } sentAt
            ? sentAt.ToString("HH:mm", CultureInfo.InvariantCulture)
            : null;

        const int TextToTimestampGap = 1;
        const int TimestampTrailingGap = 1;

        // Reserve the timestamp's cells, its separation from the text, and a trailing blank cell so the
        // annotation stays clear of both the message and a possible scrollbar. Only narrow the first row when
        // the reservation still leaves a usable text zone.
        var firstRowWidth = width;
        if (time is not null)
        {
            var reserved = TerminalCellText.Width(time)
                + TextToTimestampGap
                + TimestampTrailingGap;
            if (width - reserved >= 1)
            {
                firstRowWidth = width - reserved;
            }

            else
            {
                time = null; // too narrow to show a time without crowding the text
            }
        }

        var annotationPending = time is not null;
        var sourceLines = SplitLines(user.Text).ToList();
        for (var i = 0; i < sourceLines.Count; i++)
        {
            var lineWidth = i == 0 && time is not null ? firstRowWidth : width;
            foreach (var wrapped in WrapLine(sourceLines[i], lineWidth))
            {
                var right = annotationPending ? time : null;
                annotationPending = false;
                lines.Add(new TranscriptRenderLine(wrapped, TranscriptRole.User)
                {
                    FillWidth = true,
                    RightText = right,
                    RightTextTrailingCells = right is null ? 0 : TimestampTrailingGap,
                });
            }
        }
    }

    private static void AppendPendingUser(List<TranscriptRenderLine> lines, PendingUserTranscriptBlock pending, int width)
    {
        var firstLine = true;
        foreach (var sourceLine in SplitLines(pending.Text))
        {
            foreach (var wrapped in WrapLine(sourceLine, width))
            {
                var text = firstLine ? "[pending] " + wrapped : wrapped;
                firstLine = false;
                lines.Add(new TranscriptRenderLine(text, TranscriptRole.PendingUser)
                {
                    FillWidth = true,
                });
            }
        }
    }

    /// <summary>Width available for content once an indentation prefix is reserved (indent counts toward width).</summary>
    private static int EffectiveWidth(int width, string indent)
    {
        var remaining = width - indent.Length;
        return remaining > 0 ? remaining : 1;
    }

    private static string RenderInline(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        RenderInline(container, builder);
        return builder.ToString();
    }

    private static void RenderInline(ContainerInline container, StringBuilder builder)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;

                case CodeInline code:
                    builder.Append(code.Content);
                    break;

                case LineBreakInline lineBreak:
                    builder.Append(lineBreak.IsHard ? '\n' : ' ');
                    break;

                case LinkInline link:
                    var start = builder.Length;
                    RenderInline(link, builder);
                    if (builder.Length == start && link.Url is { Length: > 0 } url)
                    {
                        builder.Append(url);
                    }

                    break;

                case ContainerInline nested:
                    RenderInline(nested, builder);
                    break;
            }
        }
    }

    private static string FormatPermission(PermissionTranscriptBlock permission)
    {
        var decision = permission.Allowed switch
        {
            true => " → allowed",
            false => " → denied",
            null => string.Empty,
        };

        return $"{permission.ToolName} {permission.InputPreview}{decision}";
    }

    private static string FormatQuestion(UserQuestionTranscriptBlock question) =>
        question.Answer is { } answer ? $"{question.Question} → {answer}" : question.Question;

    private static TranscriptRole RoleFor(UiNotificationLevel level) => level switch
    {
        UiNotificationLevel.Error => TranscriptRole.Error,
        UiNotificationLevel.Warning => TranscriptRole.Warning,
        _ => TranscriptRole.Notification,
    };

    private static IEnumerable<string> SplitLines(string text)
    {
        var normalized = NormalizeNewlines(text ?? string.Empty);
        var start = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] == '\n')
            {
                yield return normalized[start..i];
                start = i + 1;
            }
        }

        yield return normalized[start..];
    }

    /// <summary>Hard-wraps preformatted text (code, diff, tool/command output) preserving all whitespace.</summary>
    private static IEnumerable<string> WrapPreformatted(string line, int width)
    {
        var cellWidth = width > 0 ? width : 1;
        if (line.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        if (TerminalCellText.Width(line) <= cellWidth)
        {
            yield return line;
            yield break;
        }

        foreach (var (chunk, _, _) in BreakWord(line, cellWidth))
        {
            yield return chunk;
        }
    }

    /// <summary>Word-wraps a single logical line by display cells, never splitting a grapheme cluster.</summary>
    private static IEnumerable<string> WrapLine(string line, int width)
    {
        var cellWidth = width > 0 ? width : 1;
        if (line.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        var current = new StringBuilder();
        var currentWidth = 0;

        foreach (var word in SplitWords(line))
        {
            if (word.Length == 0)
            {
                continue;
            }

            var wordWidth = TerminalCellText.Width(word);

            if (currentWidth == 0)
            {
                // Nothing buffered: place the word, breaking it if it is wider than the line.
                if (wordWidth <= cellWidth)
                {
                    current.Append(word);
                    currentWidth = wordWidth;
                }
                else
                {
                    foreach (var (chunk, chunkWidth, isLast) in BreakWord(word, cellWidth))
                    {
                        if (isLast)
                        {
                            current.Append(chunk);
                            currentWidth = chunkWidth;
                        }
                        else
                        {
                            yield return chunk;
                        }
                    }
                }

                continue;
            }

            if (currentWidth + 1 + wordWidth <= cellWidth)
            {
                current.Append(' ').Append(word);
                currentWidth += 1 + wordWidth;
                continue;
            }

            // Word does not fit on the current line: flush and start a new one.
            yield return current.ToString();
            current.Clear();
            currentWidth = 0;

            if (wordWidth <= cellWidth)
            {
                current.Append(word);
                currentWidth = wordWidth;
            }
            else
            {
                foreach (var (chunk, chunkWidth, isLast) in BreakWord(word, cellWidth))
                {
                    if (isLast)
                    {
                        current.Append(chunk);
                        currentWidth = chunkWidth;
                    }
                    else
                    {
                        yield return chunk;
                    }
                }
            }
        }

        yield return current.ToString();
    }

    private static IEnumerable<string> SplitWords(string line)
    {
        var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == ' ')
            {
                yield return line[start..i];
                start = i + 1;
            }
        }

        yield return line[start..];
    }

    /// <summary>Breaks an over-long word into chunks at grapheme boundaries; the final tuple item is the tail.</summary>
    private static IEnumerable<(string Chunk, int Width, bool IsLast)> BreakWord(string word, int width)
    {
        var chunks = new List<(string Chunk, int Width)>();
        var builder = new StringBuilder();
        var builderWidth = 0;

        foreach (var element in TerminalCellText.Enumerate(word))
        {
            var clusterWidth = element.CellWidth;

            if (builderWidth > 0 && builderWidth + clusterWidth > width)
            {
                chunks.Add((builder.ToString(), builderWidth));
                builder.Clear();
                builderWidth = 0;
            }

            builder.Append(element.Text);
            builderWidth += clusterWidth;
        }

        if (builder.Length > 0)
        {
            chunks.Add((builder.ToString(), builderWidth));
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            yield return (chunks[i].Chunk, chunks[i].Width, i == chunks.Count - 1);
        }
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n');
}
