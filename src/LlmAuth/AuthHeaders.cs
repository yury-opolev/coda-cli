using System.Net.Http.Headers;

namespace LlmAuth;

/// <summary>
/// The set of HTTP headers a credential contributes to a provider request
/// (e.g. <c>Authorization: Bearer …</c> + <c>anthropic-beta</c>, or
/// <c>x-api-key</c>). Identity/fingerprint headers are supplied separately by
/// <see cref="AnthropicClientIdentity"/>.
/// </summary>
public sealed class AuthHeaders
{
    private readonly Dictionary<string, string> headers;

    public AuthHeaders(IDictionary<string, string> headers)
    {
        this.headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, string> Headers => this.headers;

    /// <summary>Apply these headers to an outgoing request (replacing existing values).</summary>
    public void ApplyTo(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var (name, value) in this.headers)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>Apply these headers to a shared <see cref="HttpClient"/>'s default headers.</summary>
    public void ApplyTo(HttpRequestHeaders target)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (var (name, value) in this.headers)
        {
            target.Remove(name);
            target.TryAddWithoutValidation(name, value);
        }
    }
}
