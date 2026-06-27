using System.Runtime.InteropServices;
using LlmAuth;

namespace LlmClient;

/// <summary>
/// Builds the exact identifying header set the real Claude Code client sends to
/// the Anthropic API, so this .NET client presents identically: the
/// <see cref="AnthropicClientIdentity"/> headers plus the <c>X-Stainless-*</c>
/// headers the @anthropic-ai/sdk injects. The Stainless headers describe a JS/node
/// runtime; we deliberately spoof node values (matching the reference client)
/// rather than reporting .NET.
/// </summary>
public sealed class ClientFingerprint
{
    /// <summary>A node-style runtime version reported as X-Stainless-Runtime-Version.</summary>
    public const string SpoofedNodeVersion = "v22.14.0";

    private readonly AnthropicClientIdentity identity;

    public ClientFingerprint(AnthropicClientIdentity? identity = null)
    {
        this.identity = identity ?? new AnthropicClientIdentity();
    }

    public AnthropicClientIdentity Identity => this.identity;

    public IReadOnlyDictionary<string, string> BuildHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in this.identity.GetDefaultHeaders())
        {
            headers[name] = value;
        }

        headers[StainlessHeaders.LangHeader] = StainlessHeaders.Lang;
        headers[StainlessHeaders.RuntimeHeader] = StainlessHeaders.Runtime;
        headers[StainlessHeaders.RuntimeVersionHeader] = SpoofedNodeVersion;
        headers[StainlessHeaders.OsHeader] = OsName();
        headers[StainlessHeaders.ArchHeader] = ArchName();
        headers[StainlessHeaders.PackageVersionHeader] = StainlessHeaders.MinSdkVersion;
        headers[StainlessHeaders.RetryCountHeader] = "0";
        return headers;
    }

    private static string OsName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "Windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "MacOS";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "Linux";
        }

        return "Unknown";
    }

    private static string ArchName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x32",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => "unknown",
    };
}
