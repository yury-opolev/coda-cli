using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Coda.Agent;
using Coda.Tui.Ui.State;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Coda.Tui.Ui.Rendering;

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

    /// <summary>Wraps a plain string as an assistant-role line.</summary>
    public static implicit operator TranscriptRenderLine(string text) => new(text, TranscriptRole.Assistant);
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
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    /// <summary>Projects <paramref name="block"/> onto wrapped, attributed lines for the given cell width.</summary>
    public static IReadOnlyList<TranscriptRenderLine> Format(TranscriptBlock block, int width) =>
        Format(block, width, ToolDisplayMode.Full);

    /// <summary>Projects <paramref name="block"/> using the requested tool display mode.</summary>
    public static IReadOnlyList<TranscriptRenderLine> Format(
        TranscriptBlock block,
        int width,
        ToolDisplayMode toolDisplayMode,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(block);

        var safeWidth = width > 0 ? width : 1;
        var lines = new List<TranscriptRenderLine>();

        switch (block)
        {
            case AssistantTranscriptBlock assistant:
                AppendMarkdown(lines, assistant.Text, safeWidth);
                break;

            case UserTranscriptBlock user:
                AppendUser(lines, user, safeWidth);
                break;

            case PendingUserTranscriptBlock pending:
                AppendPendingUser(lines, pending, safeWidth);
                break;

            case ToolTranscriptBlock tool:
                if (toolDisplayMode != ToolDisplayMode.Hidden)
                {
                    AppendTool(lines, tool, safeWidth, toolDisplayMode);
                }
                break;

            case ToolActivityTranscriptBlock activity:
                AppendToolActivity(lines, activity, safeWidth, toolDisplayMode);
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
                AppendWrapped(lines, FormatPermission(permission), safeWidth, TranscriptRole.Permission);
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
                    AppendThinking(lines, thinking, safeWidth, toolDisplayMode, timeProvider);
                }

                break;
        }

        return lines;
    }

    /// <summary>Joins the formatted lines with newlines, for a plain-text projection of a block.</summary>
    public static string FormatPlainText(
        TranscriptBlock block,
        int width,
        ToolDisplayMode toolDisplayMode = ToolDisplayMode.Full) =>
        string.Join('\n', Format(block, width, toolDisplayMode).Select(line => line.Text));

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
                AppendWrapped(lines, RenderInline(heading.Inline), width, TranscriptRole.Heading, indent);
                break;

            case ParagraphBlock paragraph:
                AppendWrapped(lines, RenderInline(paragraph.Inline), width, TranscriptRole.Assistant, indent);
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
                AppendWrapped(lines, RenderInline(leaf.Inline), width, TranscriptRole.Assistant, indent);
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
        // non-literal (a soft/hard line break, emphasis, image, …). This gives the first "logical line"
        // of the blockquote even when Markdig splits the marker into multiple adjacent literal nodes.
        var firstLineText = new System.Text.StringBuilder();
        foreach (var inline in container)
        {
            if (inline is LiteralInline literal)
            {
                firstLineText.Append(literal.Content.ToString());
            }
            else
            {
                // Non-literal encountered — stop; we now have the first logical line text.
                break;
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
    /// Extracts the body text from a callout's first paragraph: everything after the <c>[!TYPE]</c>
    /// marker on the first inline line. Returns an empty string when the marker is the only content.
    /// </summary>
    private static string ExtractBodyFromFirstParagraph(ParagraphBlock paragraph)
    {
        var fullText = RenderInline(paragraph.Inline);
        var closingBracket = fullText.IndexOf(']');
        if (closingBracket < 0 || closingBracket + 1 >= fullText.Length)
        {
            return string.Empty;
        }

        return fullText[(closingBracket + 1)..].TrimStart();
    }

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
        var bodyText = ExtractBodyFromFirstParagraph(firstParagraph);

        var bodyStart = lines.Count;
        if (!string.IsNullOrEmpty(bodyText))
        {
            AppendWrapped(lines, bodyText, width, TranscriptRole.Assistant, barPrefix);
        }

        // Subsequent child blocks of the blockquote (second paragraph onward, code blocks, etc.).
        var hadBody = !string.IsNullOrEmpty(bodyText);
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
        for (var j = bodyStart; j < lines.Count; j++)
        {
            lines[j] = lines[j] with { PrefixCells = prefixCells, PrefixRole = role };
        }
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

        if (toolDisplayMode == ToolDisplayMode.Full && tool.Result is { Length: > 0 } result)
        {
            foreach (var line in SplitLines(result))
            {
                foreach (var wrapped in WrapPreformatted(line, width))
                {
                    lines.Add(new TranscriptRenderLine(wrapped, role));
                }
            }
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
            var role = summary.FailedCalls > 0
                ? TranscriptRole.Error
                : summary.Cancelled ? TranscriptRole.Warning : TranscriptRole.Tool;
            AppendActivityLine(lines, ToolActivityPreview.CompletedText(summary), width, role);
            return;
        }

        var shellCommands = activity.Calls.Length > 0 &&
            activity.Calls.All(call => call.ToolName == "run_command");
        var noun = shellCommands
            ? activity.Calls.Length == 1 ? "shell command" : "shell commands"
            : activity.Calls.Length == 1 ? "tool" : "tools";
        AppendActivityLine(lines, $"Running {activity.Calls.Length} {noun}...", width, TranscriptRole.Tool);

        var running = activity.Calls.Where(call => call.Status == ToolCallStatus.Running).ToArray();
        if (running.Length <= 5)
        {
            for (var index = 0; index < running.Length; index++)
            {
                var prefix = index == running.Length - 1 ? "`-" : "|-";
                AppendActivityLine(
                    lines,
                    $"{prefix} {ToolActivityPreview.Create(running[index].ToolName, running[index].InputJson)}",
                    width,
                    TranscriptRole.Tool);
            }

            return;
        }

        for (var index = 0; index < 4; index++)
        {
            AppendActivityLine(
                lines,
                $"|- {ToolActivityPreview.Create(running[index].ToolName, running[index].InputJson)}",
                width,
                TranscriptRole.Tool);
        }

        AppendActivityLine(lines, $"`- ...and {running.Length - 4} more", width, TranscriptRole.Tool);
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
            AppendActivityLine(
                lines,
                $"{ToolActivityPreview.Create(call.ToolName, call.InputJson)} [{ActivityStatusText(call.Status)}]{elapsed}",
                width,
                RoleFor(call));
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
            AppendPreformatted(lines, header.ToString(), width, role);
            if (!string.IsNullOrEmpty(call.Result))
            {
                AppendPreformatted(lines, TerminalTextSanitizer.Sanitize(call.Result), width, role);
            }

            if (!string.IsNullOrEmpty(call.Error))
            {
                AppendPreformatted(lines, $"Error: {TerminalTextSanitizer.Sanitize(call.Error)}", width, role);
            }
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
        var annotationPending = true;
        foreach (var sourceLine in SplitLines(pending.Text))
        {
            foreach (var wrapped in WrapLine(sourceLine, width))
            {
                lines.Add(new TranscriptRenderLine(wrapped, TranscriptRole.User)
                {
                    FillWidth = true,
                    RightText = annotationPending ? "pending" : null,
                });
                annotationPending = false;
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
