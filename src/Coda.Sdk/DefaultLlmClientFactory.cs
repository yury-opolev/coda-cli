using LlmAuth;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Coda.Sdk;

/// <summary>
/// Default <see cref="ILlmClientFactory"/>: delegates to the static
/// <see cref="LlmClientFactory.Create"/> so the client built is identical to the
/// pre-seam behavior. Used whenever a <see cref="CodaSession"/> is constructed without
/// an explicit factory.
/// </summary>
public sealed class DefaultLlmClientFactory : ILlmClientFactory
{
    /// <inheritdoc />
    public ILlmClient? Create(
        string providerId,
        CredentialManager credentials,
        ClientFingerprint fingerprint,
        HttpClient httpClient,
        ILoggerFactory? loggerFactory = null,
        LlmHttpTimeoutConfig? timeoutConfig = null,
        IStreamProgressSink? progressSink = null)
    {
        return LlmClientFactory.Create(providerId, credentials, fingerprint, httpClient, loggerFactory, timeoutConfig, progressSink);
    }
}
