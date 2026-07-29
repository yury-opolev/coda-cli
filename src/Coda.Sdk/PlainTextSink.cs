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

    public void OnToolInputModified(string hookCommand, string toolName, string originalInput, string modifiedInput) =>
        this.error.WriteLine($"  ↺ {toolName} input rewritten by hook {hookCommand}");

    public void OnToolResultModified(string hookCommand, string toolName, string originalResult, string modifiedResult) =>
        this.error.WriteLine($"  ↺ {toolName} result rewritten by hook {hookCommand}");

    public void OnPermissionDecided(string hookCommand, string toolName, string decision) =>
        this.error.WriteLine($"  {(decision == "allow" ? "✓" : "✗")} {toolName}: {decision} (by hook {hookCommand})");

    public void OnPermissionsUpdated(
        string hookCommand,
        string? modeApplied,
        IReadOnlyList<string> addedAllow,
        IReadOnlyList<string> addedDeny)
    {
        if (modeApplied is not null)
        {
            this.error.WriteLine($"  ↻ permission mode → {modeApplied} (by hook {hookCommand})");
        }

        foreach (var rule in addedAllow)
        {
            this.error.WriteLine($"  + allow:{rule} (by hook {hookCommand})");
        }

        foreach (var rule in addedDeny)
        {
            this.error.WriteLine($"  + deny:{rule} (by hook {hookCommand})");
        }
    }

    public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }

    public void OnSubagentBlocked(string hookCommand, string taskId, string reason) =>
        this.error.WriteLine($"  ✗ subagent {taskId} blocked by hook {hookCommand}: {reason}");

    public void OnWarning(string message) => this.error.WriteLine(message);

    public void OnSubagentResultModified(string hookCommand, string taskId, string originalResult, string modifiedResult) =>
        this.error.WriteLine($"  ↺ subagent {taskId} result rewritten by hook {hookCommand}");

    public void OnCompactionCancelled(string hookCommand, string trigger) =>
        this.error.WriteLine($"  ✗ compaction ({trigger}) cancelled by hook {hookCommand}");

    public void OnPostCompactContextInjected(string additionalContext) =>
        this.error.WriteLine($"  ↻ PostCompact: injected {additionalContext.Length} chars of additional context");
}
