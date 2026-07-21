using System.Runtime.CompilerServices;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Sdk;
using Coda.Tui.Agent;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Tests;

/// <summary>
/// Shared test scaffolding that builds an <see cref="AgentRunner"/> over a fully faked
/// <see cref="CodaSession"/> (fake LLM client + agent loop, no network), plus a matching
/// <see cref="CommandContext"/>. Lets tests exercise eager <c>InitializeSessionAsync</c> and the live
/// task registry without contacting a provider.
/// </summary>
internal static class FakeSessionRunner
{
    internal static CommandContext CreateContext(string workingDirectory, IUiEventPublisher events)
    {
        var store = new InMemoryTokenStore();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { new ClaudeAiProvider() });
        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };
        var session = new SessionState("claude-ai", workingDirectory);
        var registry = new SlashCommandRegistry(Array.Empty<ISlashCommand>());

        return new CommandContext(
            new Spectre.Console.Testing.TestConsole(),
            credentials,
            session,
            providers,
            registry,
            events: events);
    }

    internal static AgentRunner Create(HttpClient http) => new(
        extraToolsProvider: null,
        sessionFactory: (context, options, currentOptions) => new CodaSession(
            context.Credentials,
            options,
            httpClient: http,
            history: context.Session.History,
            sessionId: context.Session.SessionId,
            llmClientFactory: new StubClientFactory(new StubClient()),
            agentLoopFactory: new SingleLoopFactory(new ScriptedLoop()),
            currentOptionsProvider: currentOptions));

    internal sealed class BlockingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network call expected in these tests.");
    }

    private sealed class ScriptedLoop : IAgentLoop
    {
        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default)
        {
            sink.OnAssistantText("ok");
            sink.OnAssistantTextComplete();
            return Task.CompletedTask;
        }
    }

    private sealed class SingleLoopFactory(IAgentLoop loop) : IAgentLoopFactory
    {
        public IAgentLoop Create(AgentLoopSpec spec) => loop;
    }

    private sealed class StubClientFactory(ILlmClient client) : ILlmClientFactory
    {
        public ILlmClient? Create(
            string providerId,
            CredentialManager credentials,
            ClientFingerprint fingerprint,
            HttpClient httpClient,
            ILoggerFactory? loggerFactory = null,
            LlmHttpTimeoutConfig? timeoutConfig = null,
            IStreamProgressSink? progressSink = null) => client;
    }

    private sealed class StubClient : ILlmClient
    {
        public string ProviderId => ClaudeAiProvider.Id;

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }
}
