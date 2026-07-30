using System.Collections.Immutable;
using Coda.Agent;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// A row wider than the viewport makes the terminal auto-wrap it, and the transcript then draws its own
/// next row over part of that wrapped overflow — leaving the sparse, stranded characters users see after
/// a command or tool run. Every projection must therefore fit the width it was asked for.
/// </summary>
public sealed class TranscriptRowWidthTests
{
    /// <summary>Realistic assistant output: long paths, a bare URL, em dashes and curly quotes.</summary>
    private const string WideAssistantText =
        "The setup article is in BC-Wiki, at:\n\n" +
        "/Engineering/AgentTools/BC-Memory-MCP.md \u2014 \u201cbc-memory \u2014 the Business Central shared " +
        "memory MCP\u201d (prerequisites, https://bccp-memorymcp-prod.azurewebsites.net/mcp/v1, step-by-step " +
        "VS Code / Copilot CLI / Claude Code configuration)\n\n" +
        "It\u2019s linked from the parent page /Engineering/AgentTools (\u201cAgentTools\u201d), which lists " +
        "all agent tooling and is the page most people land on first.";

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    public void Assistant_rows_never_exceed_the_requested_width(int width)
    {
        var block = new AssistantTranscriptBlock(Guid.NewGuid(), WideAssistantText, Complete: true);

        AssertFits(TranscriptBlockFormatter.Format(block, width), width);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    public void Command_output_rows_never_exceed_the_requested_width(int width)
    {
        var block = new CommandOutputTranscriptBlock(
            Guid.NewGuid(),
            "https://bccp-memorymcp-prod.azurewebsites.net/mcp/v1?query=one&other=two#fragment-that-keeps-going");

        AssertFits(TranscriptBlockFormatter.Format(block, width), width);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    public void Tool_activity_rows_never_exceed_the_requested_width(int width)
    {
        var activity = new ToolActivityTranscriptBlock(
            Guid.NewGuid(),
            "root",
            "act",
            ImmutableArray.Create(
                new ToolActivityCall(
                    "c0",
                    "root",
                    "mcp__azuredevops__search_wiki",
                    """{"query":"how to setup memory mcp","project":"Engineering/AgentTools"}""",
                    "preview",
                    ToolCallStatus.Succeeded,
                    12,
                    "/Engineering/AgentTools/BC-Memory-MCP.md \u2014 the Business Central shared memory MCP\n",
                    null)),
            ToolActivityCompletionState.Completed);

        foreach (var mode in new[] { ToolDisplayMode.Summary, ToolDisplayMode.Compact, ToolDisplayMode.Full })
        {
            AssertFits(TranscriptBlockFormatter.Format(activity, width, mode), width);
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    public void User_rows_never_exceed_the_requested_width(int width)
    {
        var block = new UserTranscriptBlock(
            Guid.NewGuid(),
            "great! now, can you search where do we have wiki acrticle how to setup memory mcp?",
            DateTimeOffset.Now);

        AssertFits(TranscriptBlockFormatter.Format(block, width), width);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    public void Diff_rows_never_exceed_the_requested_width(int width)
    {
        // A realistic multi-hunk patch with long content lines to exercise wrapping at narrow widths.
        var patch =
            "diff --git a/src/some/very/long/path/to/component.tsx b/src/some/very/long/path/to/component.tsx\n" +
            "--- a/src/some/very/long/path/to/component.tsx\n" +
            "+++ b/src/some/very/long/path/to/component.tsx\n" +
            "@@ -1,3 +1,3 @@\n" +
            " export const Component = () => { return <div className=\"some-very-long-classname\">hello</div>; };\n" +
            "-const old = 'value that is removed from this line';\n" +
            "+const new_ = 'value that is added to replace the removed one on this line';";

        var block = new DiffTranscriptBlock(Guid.NewGuid(), patch);

        AssertFits(TranscriptBlockFormatter.Format(block, width), width);
    }

    private static void AssertFits(IReadOnlyList<TranscriptRenderLine> lines, int width)
    {
        foreach (var line in lines)
        {
            // The right-hand annotation is drawn into reserved trailing cells, so it counts too.
            var annotation = line.RightText is { Length: > 0 } right
                ? TerminalCellText.Width(right) + line.RightTextTrailingCells
                : 0;
            var cells = TerminalCellText.Width(line.Text) + annotation;

            Assert.True(
                cells <= width,
                $"Row \"{line.Text}\" measures {cells} cells, which exceeds the {width}-cell viewport.");
        }
    }
}
