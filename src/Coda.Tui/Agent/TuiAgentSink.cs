using Coda.Agent;
using Coda.Tui.Rendering;
using Spectre.Console;

namespace Coda.Tui.Agent;

/// <summary>Renders live agent events to the console (streaming text, tool calls, results).</summary>
public sealed class TuiAgentSink : IAgentSink
{
    private readonly IAnsiConsole console;
    private bool wroteText;

    public TuiAgentSink(IAnsiConsole console)
    {
        this.console = console;
    }

    public void OnAssistantText(string delta)
    {
        // Write raw model text (no markup parsing of bracket characters).
        this.console.Write(new Text(delta));
        this.wroteText = true;
    }

    public void OnAssistantTextComplete()
    {
        if (this.wroteText)
        {
            this.console.WriteLine();
            this.wroteText = false;
        }
    }

    public void OnToolCall(string toolName, string inputJson)
    {
        this.console.MarkupLine(Theme.DimMarkup($"⚙ {toolName}({ToolPreview.Compact(inputJson)})"));
    }

    public void OnToolResult(string toolName, ToolResult result)
    {
        var firstLine = result.Content.Split('\n', 2)[0];
        if (firstLine.Length > 160)
        {
            firstLine = firstLine[..160] + "…";
        }

        var markup = result.IsError ? Theme.ErrorMarkup(firstLine) : Theme.DimMarkup(firstLine);
        this.console.MarkupLine($"  {markup}");
    }

    public void OnError(string message)
    {
        this.console.MarkupLine(Theme.ErrorMarkup(message));
    }

    public void OnLimitReached(string kind, string message)
    {
        // A soft, recoverable stop (e.g. max_tokens / iteration cap) — a notice, not an error.
        this.console.MarkupLine(Theme.DimMarkup($"⏸ {message}"));
    }
}
