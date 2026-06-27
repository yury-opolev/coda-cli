namespace Coda.Agent.Teams;

/// <summary>
/// System-prompt addendum appended to a teammate's base system prompt.
/// </summary>
public static class TeammateSystemPrompt
{
    /// <summary>
    /// Template for the teammate addendum. Use <see cref="Format"/> to fill in
    /// the teammate's name and team.
    /// </summary>
    public const string Template =
        "You are '{name}', a teammate on team '{team}'. " +
        "Coordinate with the team lead and teammates using the send_message tool " +
        "and the shared task board (task_create/task_list/task_get/task_update). " +
        "When you finish your assigned work, stop and you will be marked idle. " +
        "If you receive a shutdown_request, decide whether to approve " +
        "(send_message with a shutdown_response approve=true) or reject it.";

    /// <summary>
    /// Builds the teammate addendum with the given name and team filled in.
    /// </summary>
    public static string Format(string name, string team) =>
        Template
            .Replace("{name}", name, StringComparison.Ordinal)
            .Replace("{team}", team, StringComparison.Ordinal);
}
