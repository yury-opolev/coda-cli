using Coda.Agent;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Sdk;

/// <summary>Creates the right <see cref="ILlmClient"/> for a provider id.</summary>
public static class LlmClientFactory
{
    /// <summary>
    /// Anthropic Messages client for Claude.ai (OAuth) + the Anthropic API key;
    /// the OpenAI-shaped Copilot chat client for GitHub Copilot; null otherwise.
    /// </summary>
    public static ILlmClient? Create(
        string providerId,
        CredentialManager credentials,
        ClientFingerprint fingerprint,
        HttpClient httpClient,
        ILoggerFactory? loggerFactory = null,
        LlmHttpTimeoutConfig? timeoutConfig = null,
        IStreamProgressSink? progressSink = null)
    {
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        // Bounds a hung HTTP call at the client (header + per-chunk idle guards). When not
        // supplied, resolved from the environment (CODA_LLM_RESPONSE_HEADERS_TIMEOUT /
        // CODA_LLM_STREAM_IDLE_TIMEOUT) so deployments can tune it without code changes.
        var timeouts = timeoutConfig ?? LlmHttpTimeoutConfig.FromEnvironment();
        // Stream-progress telemetry so a streaming turn is never radio-silent in the log
        // (first token, throttled running totals, completion). A stall then shows as
        // "first token, no completion" instead of dead silence. When the serve layer
        // supplies a sink (the Bridge liveness pulse), fan out to both.
        var loggingProgress = new LoggingStreamProgressSink(factory.CreateLogger("LlmClient.Stream"));
        IStreamProgressSink streamProgress = progressSink is null
            ? loggingProgress
            : new CompositeStreamProgressSink(loggingProgress, progressSink);
        if (providerId is ClaudeAiProvider.Id or ApiKeyProvider.Id)
        {
            return new AnthropicMessagesClient(credentials, providerId, fingerprint, httpClient, logger: factory.CreateLogger("LlmClient.Anthropic"), timeoutConfig: timeouts, progressSink: streamProgress);
        }

        if (providerId == GitHubCopilotProvider.Id)
        {
            var copilotConfig = GitHubCopilotConfig.FromEnvironment();
            return new CopilotChatClient(credentials, providerId, httpClient, baseUrl: copilotConfig.ApiBaseUrl, logger: factory.CreateLogger("LlmClient.Copilot"), timeoutConfig: timeouts, progressSink: streamProgress);
        }

        return null;
    }

    /// <summary>True if there is a chat client for this provider.</summary>
    public static bool Supports(string providerId) =>
        providerId is ClaudeAiProvider.Id or ApiKeyProvider.Id or GitHubCopilotProvider.Id;
}
