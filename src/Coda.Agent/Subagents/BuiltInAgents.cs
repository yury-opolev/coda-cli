namespace Coda.Agent.Subagents;

/// <summary>
/// The built-in named subagent type definitions and their lookup logic.
/// </summary>
public static class BuiltInAgents
{
    private const string GeneralPurposeType = "general-purpose";
    private const string ExploreType = "explore";

    private static readonly SubagentDefinition GeneralPurpose = new(
        Type: GeneralPurposeType,
        Description: "A general-purpose autonomous subagent with full tool access.",
        SystemPromptBody:
            """
            You are a subagent launched to complete a single, self-contained task
            autonomously. Use the available tools (read_file, list_dir, glob, grep,
            edit_file, write_file, run_command) to do the work, then finish with a
            concise report of what you found or changed — that report is your only
            return value to the caller, so make it self-sufficient.
            """,
        ReadOnlyToolsOnly: false);

    private static readonly SubagentDefinition Explore = new(
        Type: ExploreType,
        Description: "A read-only research subagent that investigates and reports findings.",
        SystemPromptBody:
            """
            You are an Explore subagent. Investigate the codebase to answer the request.
            Use only read-only tools; do NOT modify anything.
            Report your findings concisely as your final message — that report is your only output.
            """,
        ReadOnlyToolsOnly: true);

    private static readonly IReadOnlyList<SubagentDefinition> All =
    [
        GeneralPurpose,
        Explore,
    ];

    /// <summary>
    /// Resolves a subagent type name (case-insensitive) to its definition.
    /// Unknown or null values fall back to general-purpose.
    /// </summary>
    public static SubagentDefinition Resolve(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return GeneralPurpose;
        }

        foreach (var definition in All)
        {
            if (string.Equals(definition.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                return definition;
            }
        }

        return GeneralPurpose;
    }
}
