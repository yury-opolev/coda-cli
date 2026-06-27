using System.Text.RegularExpressions;

namespace Coda.Agent.Teams;

public static partial class AgentId
{
    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NonAlphanumericRegex();

    public static string Format(string name, string team) => $"{name}@{team}";

    public static (string Name, string Team)? Parse(string id)
    {
        var lastAt = id.LastIndexOf('@');
        if (lastAt == -1)
        {
            return null;
        }

        var name = id[..lastAt];
        var team = id[(lastAt + 1)..];

        if (name.Length == 0 || team.Length == 0)
        {
            return null;
        }

        return (name, team);
    }

    public static string SanitizeAgentName(string name) => name.Replace("@", "-");

    public static string SanitizeName(string name) =>
        NonAlphanumericRegex().Replace(name, "-").ToLowerInvariant();
}
