using Coda.Agent;
using Coda.Agent.Compaction;
using Coda.Agent.Watchers;
using LlmClient;

namespace Engine.Tests;

public sealed class CompactionTests
{
    private sealed class FakeForkedAgent : IForkedAgent
    {
        private readonly string reply;
        public FakeForkedAgent(string reply) { this.reply = reply; }
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }
        public Task<string> RunAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            this.LastMessages = messages;
            return Task.FromResult(this.reply);
        }
    }

    [Fact]
    public void Estimate_grows_with_text()
    {
        var small = TokenEstimator.Estimate([ChatMessage.UserText("hi")]);
        var big = TokenEstimator.Estimate([ChatMessage.UserText(new string('x', 4000))]);
        Assert.True(big > small);
        Assert.True(big >= 900 && big <= 1100); // ~4000 chars / 4
    }

    [Fact]
    public async Task Compact_replaces_history_with_summary_then_ack()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.UserText("build a parser"),
            new(ChatRole.Assistant, [new TextBlock("I wrote parser.cs")]),
            ChatMessage.UserText("now add tests"),
            new(ChatRole.Assistant, [new TextBlock("added tests")]),
        };
        var service = new CompactionService(new FakeForkedAgent("SUMMARY: built a parser and tests."));

        var compacted = await service.CompactAsync(history, CancellationToken.None);

        Assert.Equal(2, compacted.Count);
        Assert.Equal(ChatRole.User, compacted[0].Role);
        Assert.Contains("SUMMARY: built a parser and tests.", ((TextBlock)compacted[0].Content[0]).Text);
        Assert.Equal(ChatRole.Assistant, compacted[1].Role); // ack keeps alternation valid
    }

    [Fact]
    public async Task Compact_on_empty_history_returns_it_unchanged()
    {
        var service = new CompactionService(new FakeForkedAgent("unused"));
        var compacted = await service.CompactAsync([], CancellationToken.None);
        Assert.Empty(compacted);
    }

    private sealed class SeqHandler(params string[] sseBodies) : System.Net.Http.HttpMessageHandler
    {
        private int index;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = sseBodies[Math.Min(this.index, sseBodies.Length - 1)];
            this.index++;
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private const string SummaryTurn = """
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"COMPACTED SUMMARY"}}

        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

        data: {"type":"message_stop"}

        """;

    private const string FinalTurn = """
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"answer"}}

        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

        data: {"type":"message_stop"}

        """;

    [Fact]
    public async Task CodaSession_does_not_auto_compact_when_threshold_is_zero()
    {
        var store = new LlmAuth.InMemoryTokenStore();
        var creds = new LlmAuth.CredentialManager(store, [new LlmAuth.Providers.ClaudeAi.ClaudeAiProvider(), new LlmAuth.Providers.ClaudeAi.ApiKeyProvider(), new LlmAuth.Providers.GitHubCopilot.GitHubCopilotProvider()]);
        await creds.StoreAsync(LlmAuth.Providers.ClaudeAi.ClaudeAiProvider.Id, new LlmAuth.Credential { ProviderId = LlmAuth.Providers.ClaudeAi.ClaudeAiProvider.Id, Kind = LlmAuth.CredentialKind.OAuth, AccessToken = "AT" });

        // Pre-load a history that would trigger compaction if threshold > 0.
        var largeBlock = new string('a', 8000);
        var history = new List<ChatMessage>
        {
            ChatMessage.UserText(largeBlock),
            new(ChatRole.Assistant, [new TextBlock(new string('b', 8000))]),
        };
        var options = new Coda.Sdk.SessionOptions
        {
            ProviderId = LlmAuth.Providers.ClaudeAi.ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = ".",
            PermissionMode = PermissionMode.BypassPermissions,
            AutoCompactTokenThreshold = 0, // disabled
        };
        // Only the main turn fires; no compaction HTTP call is made first.
        using var http = new System.Net.Http.HttpClient(new SeqHandler(FinalTurn));
        using var session = new Coda.Sdk.CodaSession(creds, options, httpClient: http, history: history);

        var result = await session.RunAsync("x");

        Assert.True(result.Success);
        // The giant 'a' block must still be present — compaction did NOT run.
        Assert.Contains(session.History, m => m.Content.Any(b => b is TextBlock t && t.Text == largeBlock));
        // No summary was injected.
        Assert.DoesNotContain(session.History, m => m.Content.Any(b => b is TextBlock t && t.Text.Contains("COMPACTED SUMMARY")));
    }

    [Fact]
    public async Task CompactAsync_directly_replaces_history_with_summary()
    {
        var store = new LlmAuth.InMemoryTokenStore();
        var creds = new LlmAuth.CredentialManager(store, [new LlmAuth.Providers.ClaudeAi.ClaudeAiProvider(), new LlmAuth.Providers.ClaudeAi.ApiKeyProvider(), new LlmAuth.Providers.GitHubCopilot.GitHubCopilotProvider()]);
        await creds.StoreAsync(LlmAuth.Providers.ClaudeAi.ClaudeAiProvider.Id, new LlmAuth.Credential { ProviderId = LlmAuth.Providers.ClaudeAi.ClaudeAiProvider.Id, Kind = LlmAuth.CredentialKind.OAuth, AccessToken = "AT" });

        var largeBlock = new string('z', 8000);
        var history = new List<ChatMessage>
        {
            ChatMessage.UserText(largeBlock),
            new(ChatRole.Assistant, [new TextBlock("assistant reply")]),
            ChatMessage.UserText("follow-up question"),
            new(ChatRole.Assistant, [new TextBlock("follow-up answer")]),
        };
        var options = new Coda.Sdk.SessionOptions
        {
            ProviderId = LlmAuth.Providers.ClaudeAi.ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = ".",
            PermissionMode = PermissionMode.BypassPermissions,
            AutoCompactTokenThreshold = 0, // keep disabled so only CompactAsync fires
        };
        // The single HTTP call returns the summary turn.
        using var http = new System.Net.Http.HttpClient(new SeqHandler(SummaryTurn));
        using var session = new Coda.Sdk.CodaSession(creds, options, httpClient: http, history: history);

        await session.CompactAsync();

        // History is now the user(summary)+assistant(ack) pair.
        Assert.Equal(2, session.History.Count);
        Assert.Equal(ChatRole.User, session.History[0].Role);
        Assert.Contains("COMPACTED SUMMARY", ((TextBlock)session.History[0].Content[0]).Text);
        Assert.Equal(ChatRole.Assistant, session.History[1].Role);
        // The original large block is gone.
        Assert.DoesNotContain(session.History, m => m.Content.Any(b => b is TextBlock t && t.Text == largeBlock));
    }

    [Fact]
    public async Task CodaSession_auto_compacts_when_over_threshold()
    {
        var store = new LlmAuth.InMemoryTokenStore();
        var creds = new LlmAuth.CredentialManager(store, [new LlmAuth.Providers.ClaudeAi.ClaudeAiProvider(), new LlmAuth.Providers.ClaudeAi.ApiKeyProvider(), new LlmAuth.Providers.GitHubCopilot.GitHubCopilotProvider()]);
        await creds.StoreAsync(LlmAuth.Providers.ClaudeAi.ClaudeAiProvider.Id, new LlmAuth.Credential { ProviderId = LlmAuth.Providers.ClaudeAi.ClaudeAiProvider.Id, Kind = LlmAuth.CredentialKind.OAuth, AccessToken = "AT" });

        // Pre-load a large shared history (> threshold) so the first turn triggers compaction.
        var history = new List<ChatMessage>
        {
            ChatMessage.UserText(new string('a', 8000)),
            new(ChatRole.Assistant, [new TextBlock(new string('b', 8000))]),
        };
        var options = new Coda.Sdk.SessionOptions
        {
            ProviderId = LlmAuth.Providers.ClaudeAi.ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = ".",
            PermissionMode = PermissionMode.BypassPermissions,
            AutoCompactTokenThreshold = 1000, // far below the ~4000-token history
        };
        // Request order is deterministic: compaction fork runs (awaited) BEFORE the main turn.
        using var http = new System.Net.Http.HttpClient(new SeqHandler(SummaryTurn, FinalTurn));
        using var session = new Coda.Sdk.CodaSession(creds, options, httpClient: http, history: history);

        var result = await session.RunAsync("continue");

        Assert.True(result.Success);
        // History was compacted: it no longer contains the giant 'a' block; it has the summary.
        Assert.Contains(session.History, m => m.Content.Any(b => b is TextBlock t && t.Text.Contains("COMPACTED SUMMARY")));
        Assert.DoesNotContain(session.History, m => m.Content.Any(b => b is TextBlock t && t.Text.Length >= 8000));
    }
}
