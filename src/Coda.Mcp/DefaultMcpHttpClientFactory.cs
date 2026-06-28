using Coda.Mcp.Auth;
using LlmAuth;

namespace Coda.Mcp;

/// <summary>
/// Default <see cref="IMcpHttpClientFactory"/>: builds an <see cref="McpHttpClient"/> and the
/// matching <see cref="IMcpAuthProvider"/> for the server's configured auth mode. OAuth
/// servers get an <see cref="McpOAuthProvider"/> keyed by the canonical resource URI; the
/// <paramref name="interactive"/> flag is false for headless runs so they never open a browser.
/// </summary>
public sealed class DefaultMcpHttpClientFactory : IMcpHttpClientFactory
{
    private readonly HttpClient http;
    private readonly ITokenStore tokenStore;
    private readonly bool interactive;
    private readonly Action<string>? log;

    public DefaultMcpHttpClientFactory(HttpClient http, ITokenStore tokenStore, bool interactive, Action<string>? log = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        this.interactive = interactive;
        this.log = log;
    }

    public IMcpClient Create(string serverName, McpHttpServerConfig config)
    {
        var auth = this.CreateAuthProvider(config);
        return new McpHttpClient(serverName, config, this.http, auth);
    }

    private IMcpAuthProvider? CreateAuthProvider(McpHttpServerConfig config)
    {
        switch (config.Auth.Mode)
        {
            case McpAuthMode.None:
                return null;

            case McpAuthMode.Bearer:
                return string.IsNullOrEmpty(config.Auth.BearerToken)
                    ? null
                    : new StaticBearerAuthProvider(config.Auth.BearerToken);

            case McpAuthMode.OAuth:
            default:
                return new McpOAuthProvider(
                    this.http,
                    this.tokenStore,
                    CanonicalResourceUri.From(config.Url),
                    config.Auth,
                    this.interactive,
                    log: this.log);
        }
    }
}
