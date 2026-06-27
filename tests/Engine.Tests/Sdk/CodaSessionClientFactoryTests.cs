using Coda.Agent;
using Coda.Sdk;
using Engine.Tests.TestSupport;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Microsoft.Extensions.Logging;
using static Engine.Tests.TestSupport.CredentialFixtures;
using static Engine.Tests.TestSupport.SseTestHandler;

namespace Engine.Tests.Sdk;

/// <summary>
/// Verifies the <see cref="ILlmClientFactory"/> seam: when a session is given a factory,
/// <see cref="CodaSession.RunAsync(string, IAgentSink?, System.Threading.CancellationToken)"/>
/// obtains its provider client from THAT factory (not the static default).
/// </summary>
public sealed class CodaSessionClientFactoryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_session_factory_").FullName;

    private SessionOptions Options() => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = PermissionMode.BypassPermissions,
    };

    /// <summary>
    /// Recording fake: flags that it was invoked and delegates to the real default factory so the
    /// turn still completes against the canned handler. Proves RunAsync goes through the seam.
    /// </summary>
    private sealed class RecordingClientFactory : ILlmClientFactory
    {
        public int CreateCalls { get; private set; }

        public string? LastProviderId { get; private set; }

        public ILlmClient? Create(
            string providerId,
            CredentialManager credentials,
            ClientFingerprint fingerprint,
            HttpClient httpClient,
            ILoggerFactory? loggerFactory = null,
            LlmHttpTimeoutConfig? timeoutConfig = null,
            IStreamProgressSink? progressSink = null)
        {
            this.CreateCalls++;
            this.LastProviderId = providerId;
            return LlmClientFactory.Create(providerId, credentials, fingerprint, httpClient, loggerFactory, timeoutConfig, progressSink);
        }
    }

    [Fact]
    public async Task RunAsync_obtains_its_client_from_the_injected_factory()
    {
        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var factory = new RecordingClientFactory();
        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            llmClientFactory: factory);

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        Assert.Equal("hello world", result.FinalText);
        Assert.True(factory.CreateCalls >= 1, "RunAsync should obtain its client from the injected factory.");
        Assert.Equal(ClaudeAiProvider.Id, factory.LastProviderId);
    }

    [Fact]
    public async Task CompactAsync_obtains_its_client_from_the_injected_factory()
    {
        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var factory = new RecordingClientFactory();
        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            llmClientFactory: factory);

        await session.CompactAsync();

        Assert.True(factory.CreateCalls >= 1, "CompactAsync should obtain its client from the injected factory.");
        Assert.Equal(ClaudeAiProvider.Id, factory.LastProviderId);
    }

    [Fact]
    public async Task AnalyzeContextAsync_obtains_its_client_from_the_injected_factory()
    {
        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var factory = new RecordingClientFactory();
        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            llmClientFactory: factory);

        await session.AnalyzeContextAsync();

        Assert.True(factory.CreateCalls >= 1, "AnalyzeContextAsync should obtain its client from the injected factory.");
        Assert.Equal(ClaudeAiProvider.Id, factory.LastProviderId);
    }

    [Fact]
    public async Task ListModelsAsync_obtains_its_client_from_the_injected_factory()
    {
        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var factory = new RecordingClientFactory();
        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            llmClientFactory: factory);

        await session.ListModelsAsync();

        Assert.True(factory.CreateCalls >= 1, "ListModelsAsync should obtain its client from the injected factory.");
        Assert.Equal(ClaudeAiProvider.Id, factory.LastProviderId);
    }

    [Fact]
    public async Task RunAsync_uses_the_default_factory_when_none_is_injected()
    {
        // The default-path branch (?? new DefaultLlmClientFactory()): existing positional callers
        // pass no factory, yet the turn still completes against the real Anthropic client.
        using var http = new HttpClient(new SseTestHandler(TextTurn));
        using var session = new CodaSession(SignedInClaude(), this.Options(), httpClient: http);

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        Assert.Equal("hello world", result.FinalText);
    }

    [Fact]
    public void DefaultLlmClientFactory_delegates_to_the_static_factory()
    {
        using var http = new HttpClient(new SseTestHandler(TextTurn));
        ILlmClientFactory factory = new DefaultLlmClientFactory();

        var client = factory.Create(ClaudeAiProvider.Id, SignedInClaude(), new ClientFingerprint(), http);

        Assert.NotNull(client);
        Assert.Equal(ClaudeAiProvider.Id, client!.ProviderId);

        // Unknown provider yields null, matching LlmClientFactory.Create.
        var none = factory.Create("nope", SignedInClaude(), new ClientFingerprint(), http);
        Assert.Null(none);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
