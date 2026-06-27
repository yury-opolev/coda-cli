using LlmAuth;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Coda.Sdk;

/// <summary>
/// Builds the provider <see cref="ILlmClient"/> for a session turn. Injected into
/// <see cref="CodaSession"/> so the provider client can be faked in tests without a real
/// provider. The default implementation (<see cref="DefaultLlmClientFactory"/>) delegates
/// verbatim to <see cref="LlmClientFactory.Create"/>.
/// </summary>
public interface ILlmClientFactory
{
    /// <summary>
    /// Create the <see cref="ILlmClient"/> for <paramref name="providerId"/>, or <c>null</c>
    /// when the provider has no chat client. Mirrors <see cref="LlmClientFactory.Create"/>.
    /// </summary>
    /// <param name="providerId">The provider whose client to build.</param>
    /// <param name="credentials">Credential store used to authenticate the client.</param>
    /// <param name="fingerprint">Stable client fingerprint sent with provider requests.</param>
    /// <param name="httpClient">The shared HTTP client; the returned client does not own it.</param>
    /// <param name="loggerFactory">Optional logger factory; defaults to a null logger when omitted.</param>
    /// <param name="timeoutConfig">Optional HTTP timeout config; resolved from the environment when omitted.</param>
    /// <param name="progressSink">Optional LLM-stream liveness sink (e.g. the Bridge pulse); null disables.</param>
    ILlmClient? Create(
        string providerId,
        CredentialManager credentials,
        ClientFingerprint fingerprint,
        HttpClient httpClient,
        ILoggerFactory? loggerFactory = null,
        LlmHttpTimeoutConfig? timeoutConfig = null,
        IStreamProgressSink? progressSink = null);
}
