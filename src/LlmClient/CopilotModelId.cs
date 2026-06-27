using System.Text.RegularExpressions;

namespace LlmClient;

/// <summary>
/// Normalizes a Claude model id to the form GitHub Copilot's live API expects:
/// a dotted major-minor version (claude-opus-4.8), not the dashed catalog form
/// (claude-opus-4-8) that Copilot rejects with HTTP 400 model_not_supported. Only the
/// final <c>-N-N</c> version tail of a <c>claude-&lt;family&gt;-…</c> id is rewritten;
/// everything else (other providers, already-dotted ids, non-version dashes) is left
/// unchanged.
/// </summary>
public static partial class CopilotModelId
{
    [GeneratedRegex(@"^(claude-[a-z]+-\d+)-(\d+)$")]
    private static partial Regex ClaudeVersionTail();

    public static string Normalize(string modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return modelId;
        }

        return ClaudeVersionTail().Replace(modelId, "$1.$2");
    }
}
