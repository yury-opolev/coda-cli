using System.Text.RegularExpressions;

namespace Coda.Tui.Plugins;

/// <summary>
/// Parses a marketplace source input string into a <see cref="MarketplaceSource"/> discriminated union.
/// Handles the MVP subset of supported input forms.
/// </summary>
public static partial class MarketplaceInputParser
{
    [GeneratedRegex(@"^([a-zA-Z0-9._-]+@[^:]+:.+?(?:\.git)?)(#(.+))?$")]
    private static partial Regex SshUrlRegex();

    [GeneratedRegex(@"^([^#]+)(#(.+))?$")]
    private static partial Regex FragmentSplitRegex();

    [GeneratedRegex(@"^/([^/]+/[^/]+?)(/|\.git|$)")]
    private static partial Regex GithubPathRegex();

    [GeneratedRegex(@"^[a-zA-Z]:[/\\]")]
    private static partial Regex WindowsDriveRegex();

    [GeneratedRegex(@"^([^#@]+)(?:[#@](.+))?$")]
    private static partial Regex ShorthandRefRegex();

    /// <summary>
    /// Parses <paramref name="input"/> and returns either a <see cref="MarketplaceSource"/>
    /// or an error message. Returns <c>(null, null)</c> when the format is unrecognized.
    /// </summary>
    public static (MarketplaceSource? Source, string? Error) Parse(string input)
    {
        var trimmed = input.Trim();

        // 1. SSH git URLs: user@host:path[.git][#ref]
        var sshMatch = SshUrlRegex().Match(trimmed);
        if (sshMatch.Success)
        {
            var url = sshMatch.Groups[1].Value;
            var refValue = sshMatch.Groups[3].Success ? sshMatch.Groups[3].Value : null;
            return (new GitSource(url, refValue), null);
        }

        // 2. HTTP/HTTPS URLs
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseHttpUrl(trimmed);
        }

        // 3. Local path detection
        var isWindowsPath = trimmed.StartsWith(@".\", StringComparison.Ordinal) ||
                            trimmed.StartsWith(@"..\", StringComparison.Ordinal) ||
                            WindowsDriveRegex().IsMatch(trimmed);

        if (trimmed.StartsWith("./", StringComparison.Ordinal) ||
            trimmed.StartsWith("../", StringComparison.Ordinal) ||
            trimmed.StartsWith("/", StringComparison.Ordinal) ||
            trimmed.StartsWith("~", StringComparison.Ordinal) ||
            isWindowsPath)
        {
            return ParseLocalPath(trimmed);
        }

        // 4. GitHub shorthand: contains '/' and does NOT start with '@'
        if (trimmed.Contains('/') && !trimmed.StartsWith("@", StringComparison.Ordinal))
        {
            if (trimmed.Contains(':'))
            {
                return (null, null);
            }

            var match = ShorthandRefRegex().Match(trimmed);
            var repo = match.Success ? match.Groups[1].Value : trimmed;
            var refValue = match.Success && match.Groups[2].Success ? match.Groups[2].Value : null;
            return (new GithubSource(repo, refValue), null);
        }

        // 5. Unrecognized
        return (null, null);
    }

    private static (MarketplaceSource? Source, string? Error) ParseHttpUrl(string trimmed)
    {
        var fragmentMatch = FragmentSplitRegex().Match(trimmed);
        var urlWithoutFragment = fragmentMatch.Success ? fragmentMatch.Groups[1].Value : trimmed;
        var refValue = fragmentMatch.Success && fragmentMatch.Groups[3].Success
            ? fragmentMatch.Groups[3].Value
            : null;

        // Explicit .git suffix or Azure DevOps /_git/ path → git clone
        if (urlWithoutFragment.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ||
            urlWithoutFragment.Contains("/_git/", StringComparison.OrdinalIgnoreCase))
        {
            return (new GitSource(urlWithoutFragment, refValue), null);
        }

        // Parse host to check for github.com
        Uri uri;
        try
        {
            uri = new Uri(urlWithoutFragment);
        }
        catch (UriFormatException)
        {
            return (null, $"Unsupported marketplace URL (MVP supports git repositories): {urlWithoutFragment}");
        }

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var pathMatch = GithubPathRegex().Match(uri.AbsolutePath);
            if (pathMatch.Success)
            {
                var gitUrl = urlWithoutFragment.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                    ? urlWithoutFragment
                    : urlWithoutFragment + ".git";
                return (new GitSource(gitUrl, refValue), null);
            }
        }

        return (null, $"Unsupported marketplace URL (MVP supports git repositories): {urlWithoutFragment}");
    }

    private static (MarketplaceSource? Source, string? Error) ParseLocalPath(string trimmed)
    {
        var expanded = trimmed.StartsWith("~", StringComparison.Ordinal)
            ? trimmed.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal)
            : trimmed;

        var resolvedPath = Path.GetFullPath(expanded);

        if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
        {
            return (null, $"Path does not exist: {resolvedPath}");
        }

        if (File.Exists(resolvedPath))
        {
            if (resolvedPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return (new LocalFileSource(resolvedPath), null);
            }

            return (null, $"File path must point to a .json file (marketplace.json), but got: {resolvedPath}");
        }

        // Must be a directory at this point
        return (new LocalDirectorySource(resolvedPath), null);
    }
}
