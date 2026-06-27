namespace LlmClient;

/// <summary>Default + known GitHub Copilot chat model ids.</summary>
public static class CopilotModels
{
    public const string DefaultModel = "gpt-4o";

    public static readonly IReadOnlyList<string> Known =
    [
        "gpt-4o",
        "gpt-4.1",
        "o4-mini",
        "claude-sonnet-4",
    ];
}
