using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>
/// Reads the caller's requested influence over a subagent's system prompt from a launch tool's
/// input. Shared by <see cref="TaskTool"/> and <see cref="BackgroundTaskStartTool"/> so the two
/// tools cannot drift on what counts as "asked for nothing".
/// </summary>
internal static class SubagentPromptInput
{
    /// <summary>
    /// Returns the caller's influence, or null when nothing usable was supplied. Blank strings are
    /// treated as absent rather than appended, so a model padding the call cannot pad the prompt.
    /// </summary>
    public static SubagentSystemPrompt? Read(JsonElement input)
    {
        var append = ToolInput.GetString(input, "system_prompt_append");
        var requested = new SubagentSystemPrompt(
            Append: string.IsNullOrWhiteSpace(append) ? null : append);

        return requested.IsEmpty ? null : requested;
    }
}
