using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
}

/// <summary>A single rendered transcript line: display text plus the role that colors it.</summary>
/// <remarks>
/// The implicit conversion from <see cref="string"/> lets callers (and the layout index) treat a plain
/// wrapped line as an assistant-role render line without ceremony; typed callers still supply an
/// explicit role.
/// </remarks>
public readonly record struct TranscriptRenderLine(string Text, TranscriptRole Role)
{
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
    public static IReadOnlyList<TranscriptRenderLine> Format(TranscriptBlock block, int width)
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
                AppendWrapped(lines, user.Text, safeWidth, TranscriptRole.User);
                break;

            case ToolTranscriptBlock tool:
                AppendTool(lines, tool, safeWidth);
                break;

            case CommandOutputTranscriptBlock command:
                AppendPreformatted(lines, command.Text, safeWidth, TranscriptRole.Code);
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
        }

        return lines;
    }

    /// <summary>Joins the formatted lines with newlines, for a plain-text projection of a block.</summary>
    public static string FormatPlainText(TranscriptBlock block, int width) =>
        string.Join('\n', Format(block, width).Select(line => line.Text));

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

                break;

            case ListBlock list:
                AppendList(lines, list, width, indent);
                break;

            case LeafBlock leaf when leaf.Inline is not null:
                AppendWrapped(lines, RenderInline(leaf.Inline), width, TranscriptRole.Assistant, indent);
                break;
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

    private static void AppendTool(List<TranscriptRenderLine> lines, ToolTranscriptBlock tool, int width)
    {
        var role = tool.IsError ? TranscriptRole.Error : TranscriptRole.Tool;
        var header = new StringBuilder(tool.ToolName);
        if (!string.IsNullOrWhiteSpace(tool.InputJson))
        {
            header.Append(' ').Append(tool.InputJson.Trim());
        }

        if (tool.ElapsedMs is { } ms)
        {
            header.Append(" (").Append(ms.ToString(CultureInfo.InvariantCulture)).Append("ms)");
        }
        else if (!tool.Complete)
        {
            header.Append(" (running)");
        }

        if (tool.IsError)
        {
            header.Append(" [error]");
        }

        AppendPreformatted(lines, header.ToString(), width, role);

        if (tool.Result is { Length: > 0 } result)
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
            var clusterWidth = element.Width;

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
