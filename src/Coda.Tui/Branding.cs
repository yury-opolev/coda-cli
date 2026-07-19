using System.Reflection;

namespace Coda.Tui;

/// <summary>
/// Single source of truth for all product-facing names and look. This project is
/// its OWN product — never "Claude Code". Rename here and the whole UI follows.
/// (The wire-level <c>claude-cli</c> User-Agent is a provider fingerprint, not
/// user-facing branding, and lives in the auth library.)
/// </summary>
public static class Branding
{
    public const string ProductName = "Coda";
    public const string CliName = "coda";
    public const string Tagline = "an agentic coding assistant";

    /// <summary>Primary accent colour (Spectre colour name / hex).</summary>
    public const string AccentColor = "deepskyblue1";

    public const string DimColor = "grey50";

    /// <summary>The six-line Unicode wordmark spelling "Coda", rendered in the accent colour.</summary>
    public static IReadOnlyList<string> BannerLines { get; } =
    [
        " ┌───┐      ┌┐",
        " │┬─┐│┌──┐┌─┘│┌──┐",
        " ││ └┘│┬┐││┬┐││┬┐│",
        " ││ ┌┐││││││││││││",
        " │└─┴││└┴││└┴││└┴└┐",
        " └───┘└──┘└──┘└───┘",
    ];

    /// <summary>Assembly informational version (without the +sha suffix), e.g. "0.1.2".</summary>
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var assembly = typeof(Branding).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
