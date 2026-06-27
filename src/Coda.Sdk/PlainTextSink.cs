using Coda.Agent;

namespace Coda.Sdk;

/// <summary>
/// Human-readable headless output: assistant text to the output writer, tool
/// activity and errors to the error writer.
/// </summary>
public sealed class PlainTextSink : IAgentSink
{
    private readonly TextWriter output;
    private readonly TextWriter error;
    private bool wroteText;

    public PlainTextSink(TextWriter output, TextWriter error)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public void OnAssistantText(string delta)
    {
        this.output.Write(delta);
        this.wroteText = true;
    }

    public void OnAssistantTextComplete()
    {
        if (this.wroteText)
        {
            this.output.WriteLine();
            this.wroteText = false;
        }
    }

    public void OnToolCall(string toolName, string inputJson) =>
        this.error.WriteLine($"⚙ {toolName}({ToolPreview.Compact(inputJson)})");

    public void OnToolResult(string toolName, ToolResult result)
    {
        var firstLine = result.Content.Split('\n', 2)[0];
        var marker = result.IsError ? "✗ " : "  ";
        this.error.WriteLine($"{marker}{firstLine}");
    }

    public void OnError(string message) => this.error.WriteLine(message);
}
