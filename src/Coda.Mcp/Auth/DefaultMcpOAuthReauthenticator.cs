using LlmAuth;

namespace Coda.Mcp.Auth;

/// <summary>
/// Default user-initiated OAuth reauthenticator. It constructs the same provider used by HTTP MCP
/// clients, but always enables the interactive browser flow.
/// </summary>
public sealed class DefaultMcpOAuthReauthenticator : IMcpOAuthReauthenticator
{
    private readonly HttpClient http;
    private readonly ITokenStore tokenStore;
    private readonly Func<Uri, CancellationToken, Task>? openBrowser;
    private readonly Func<LoopbackRedirectListener>? listenerFactory;
    private readonly Action<string>? log;

    public DefaultMcpOAuthReauthenticator(
        HttpClient http,
        ITokenStore tokenStore,
        Func<Uri, CancellationToken, Task>? openBrowser = null,
        Func<LoopbackRedirectListener>? listenerFactory = null,
        Action<string>? log = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        this.openBrowser = openBrowser;
        this.listenerFactory = listenerFactory;
        this.log = log;
    }

    public Task<McpAuthResult> ReauthenticateAsync(
        McpHttpServerConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Auth.Mode != McpAuthMode.OAuth)
        {
            return Task.FromResult(new McpAuthResult(
                false,
                "MCP OAuth reauthentication requires OAuth authentication mode."));
        }

        if (!config.Url.IsAbsoluteUri)
        {
            return Task.FromResult(new McpAuthResult(
                false,
                "MCP OAuth reauthentication requires an absolute server URL."));
        }

        var provider = new McpOAuthProvider(
            this.http,
            this.tokenStore,
            CanonicalResourceUri.From(config.Url),
            config.Auth,
            interactive: true,
            openBrowser: this.openBrowser,
            listenerFactory: this.listenerFactory,
            log: this.log);
        return provider.ForceReauthorizeAsync(cancellationToken);
    }
}
