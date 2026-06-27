using System.Text;

namespace Coda.Agent.Teams;

public static class TeamColors
{
    public static readonly string[] Palette =
    [
        "red",
        "green",
        "yellow",
        "blue",
        "magenta",
        "cyan",
        "bright_red",
        "bright_green",
        "bright_yellow",
        "bright_blue",
        "bright_magenta",
        "bright_cyan",
    ];

    public static string Assign(string agentId)
    {
        var hash = ComputeFnv1aHash(agentId);
        var index = (int)(hash % (uint)Palette.Length);
        return Palette[index];
    }

    private static uint ComputeFnv1aHash(string value)
    {
        const uint fnvOffsetBasis = 2166136261u;
        const uint fnvPrime = 16777619u;

        var hash = fnvOffsetBasis;
        var bytes = Encoding.UTF8.GetBytes(value);

        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= fnvPrime;
        }

        return hash;
    }
}
