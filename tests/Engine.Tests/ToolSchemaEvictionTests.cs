using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.ToolSearch;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// The regression for the incident: a single tool definition the provider refuses used to fail
/// every request for the rest of the session, because the rejected tool was re-sent every time
/// and discovery had no eviction path. The turn must now recover by dropping that one tool.
/// </summary>
public sealed class ToolSchemaEvictionTests
{
    private const string AnthropicRejection =
        """{"type":"error","error":{"type":"invalid_request_error","message":"tools.1.custom.input_schema.type: Field required"}}""";

    /// <summary>Rejects with a 400 naming a tool index until that tool stops being sent.</summary>
    private sealed class SchemaRejectingClient(string rejectedTool, string bodyTemplate) : ILlmClient
    {
        public int Calls { get; private set; }

        public List<IReadOnlyList<string>> SentToolNames { get; } = [];

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.Calls++;
            var names = request.Tools.Select(t => t.Name).ToList();
            this.SentToolNames.Add(names);

            var index = names.IndexOf(rejectedTool);
            if (index >= 0)
            {
                await Task.Yield();
                throw new LlmClientException(
                    400,
                    bodyTemplate.Replace("{idx}", index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            await Task.Yield();
            yield return AssistantStreamEvent.Delta("recovered");
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }

    private sealed class CollectingSink : IAgentSink
    {
        public List<string> Errors { get; } = [];

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) => this.Errors.Add(message);
        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }
    }

    private sealed class StubTool(string name, bool shouldDefer = false) : ITool
    {
        public string Name => name;
        public string Description => "stub";
        public string InputSchemaJson => """{"type":"object"}""";
        public bool IsReadOnly => true;
        public bool ShouldDefer => shouldDefer;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult("stub"));
    }

    private static ToolRegistry Registry(params ITool[] tools) => new(tools);

    private static AgentLoop Loop(
        ILlmClient client,
        ToolRegistry registry,
        ToolQuarantine? quarantine = null,
        ToolSearchCoordinator? toolSearch = null) =>
        new(
            client,
            registry,
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" },
            toolSearch: toolSearch,
            transportRetryDelay: TimeSpan.Zero,
            quarantine: quarantine);

    private const string BodyWithIndex =
        """{"type":"error","error":{"type":"invalid_request_error","message":"tools.{idx}.custom.input_schema.type: Field required"}}""";

    [Fact]
    public async Task A_rejected_tool_is_evicted_and_the_turn_succeeds()
    {
        var client = new SchemaRejectingClient("mcp__playwright__browser_navigate", BodyWithIndex);
        var registry = Registry(
            new StubTool("read_file"),
            new StubTool("mcp__playwright__browser_navigate"));
        var sink = new CollectingSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };

        await Loop(client, registry).RunAsync(history, sink, CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.Contains("mcp__playwright__browser_navigate", client.SentToolNames[0]);
        Assert.DoesNotContain("mcp__playwright__browser_navigate", client.SentToolNames[1]);
        Assert.Contains("read_file", client.SentToolNames[1]);

        var text = Assert.IsType<TextBlock>(history[^1].Content[0]);
        Assert.Equal("recovered", text.Text);
    }

    [Fact]
    public async Task The_user_is_told_which_tool_went_away_and_why()
    {
        var client = new SchemaRejectingClient("bad_tool", BodyWithIndex);
        var sink = new CollectingSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };

        await Loop(client, Registry(new StubTool("good_tool"), new StubTool("bad_tool")))
            .RunAsync(history, sink, CancellationToken.None);

        var error = Assert.Single(sink.Errors);
        Assert.Contains("bad_tool", error, StringComparison.Ordinal);
        Assert.Contains("disabled", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_eviction_persists_for_the_whole_session_via_a_shared_quarantine()
    {
        var quarantine = new ToolQuarantine();
        var registry = Registry(new StubTool("good_tool"), new StubTool("bad_tool"));

        var first = new SchemaRejectingClient("bad_tool", BodyWithIndex);
        await Loop(first, registry, quarantine)
            .RunAsync([ChatMessage.UserText("hi")], new CollectingSink(), CancellationToken.None);
        Assert.Equal(2, first.Calls);

        // A later turn (a NEW loop, as the session builds one per turn) must not re-send it.
        var second = new SchemaRejectingClient("bad_tool", BodyWithIndex);
        await Loop(second, registry, quarantine)
            .RunAsync([ChatMessage.UserText("again")], new CollectingSink(), CancellationToken.None);

        Assert.Equal(1, second.Calls); // no rejection at all the second time
        Assert.DoesNotContain("bad_tool", second.SentToolNames[0]);
        Assert.Contains("bad_tool", quarantine.Names);
    }

    [Fact]
    public async Task Eviction_also_returns_a_discovered_tool_to_deferred()
    {
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);
        coordinator.AddDiscovered(["mcp__p__bad"]);
        var registry = Registry(
            new StubTool("tool_search"),
            new StubTool("mcp__p__bad", shouldDefer: true));
        var client = new SchemaRejectingClient("mcp__p__bad", BodyWithIndex);

        await Loop(client, registry, toolSearch: coordinator)
            .RunAsync([ChatMessage.UserText("hi")], new CollectingSink(), CancellationToken.None);

        Assert.DoesNotContain("mcp__p__bad", coordinator.Discovered);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task Standard_mode_is_protected_too()
    {
        // Standard mode never consults the coordinator, so the quarantine must be applied on that
        // branch as well — otherwise this bug is fatal from the very first turn.
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Standard);
        var registry = Registry(new StubTool("ok"), new StubTool("bad"));
        var client = new SchemaRejectingClient("bad", BodyWithIndex);

        await Loop(client, registry, toolSearch: coordinator)
            .RunAsync([ChatMessage.UserText("hi")], new CollectingSink(), CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.DoesNotContain("bad", client.SentToolNames[1]);
    }

    [Fact]
    public async Task Several_bad_tools_are_evicted_one_at_a_time_up_to_the_cap()
    {
        // Every attempt blames whichever bad tool is still present; the loop must converge.
        var registry = Registry(
            new StubTool("good"), new StubTool("bad1"), new StubTool("bad2"), new StubTool("bad3"));
        var client = new MultiRejectingClient(["bad1", "bad2", "bad3"]);

        await Loop(client, registry).RunAsync(
            [ChatMessage.UserText("hi")], new CollectingSink(), CancellationToken.None);

        Assert.Equal(4, client.Calls);
        Assert.Equal(["good"], client.SentToolNames[^1]);
    }

    [Fact]
    public async Task An_unattributable_400_surfaces_instead_of_evicting_a_random_tool()
    {
        var client = new AlwaysRejectingClient(
            """{"type":"error","error":{"message":"credit balance is too low"}}""");

        await Assert.ThrowsAsync<LlmClientException>(() =>
            Loop(client, Registry(new StubTool("a"), new StubTool("b")))
                .RunAsync([ChatMessage.UserText("hi")], new CollectingSink(), CancellationToken.None));

        Assert.Equal(1, client.Calls); // never retried
    }

    [Fact]
    public async Task A_provider_that_keeps_blaming_tools_stops_at_the_eviction_cap()
    {
        // Pathological: the index always points at whatever is first, so every retry evicts one
        // more. Without a cap this would strip every tool and spin; with it, the error surfaces.
        var registry = Registry(
            new StubTool("a"), new StubTool("b"), new StubTool("c"),
            new StubTool("d"), new StubTool("e"), new StubTool("f"), new StubTool("g"));
        var client = new AlwaysRejectingClient(
            """{"type":"error","error":{"message":"tools.0.custom.input_schema.type: Field required"}}""");

        await Assert.ThrowsAsync<LlmClientException>(() =>
            Loop(client, registry).RunAsync(
                [ChatMessage.UserText("hi")], new CollectingSink(), CancellationToken.None));

        // initial attempt + 5 evictions = 6 calls.
        Assert.Equal(6, client.Calls);
    }

    [Fact]
    public async Task Overflow_compaction_and_schema_eviction_compose_within_one_turn()
    {
        // The two retry paths mutate different halves of the request: overflow rewrites Messages,
        // eviction rewrites Tools. If either clobbered the other's work the turn would either
        // re-overflow forever or re-send the rejected tool.
        var client = new OverflowThenSchemaClient("bad");
        var registry = Registry(new StubTool("good"), new StubTool("bad"));
        var history = new List<ChatMessage>
        {
            ChatMessage.UserText("a very long conversation"),
            ChatMessage.UserText("second message"),
        };

        var compactions = 0;
        var loop = new AgentLoop(
            client,
            registry,
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" },
            transportRetryDelay: TimeSpan.Zero,
            compactAsync: (h, _, _) =>
            {
                compactions++;
                h.Clear();
                h.Add(ChatMessage.UserText("summary of the conversation"));
                return Task.FromResult(true);
            });

        await loop.RunAsync(history, new CollectingSink(), CancellationToken.None);

        Assert.Equal(1, compactions);
        Assert.Equal(3, client.Calls);

        // Compaction survived the eviction retry…
        Assert.Equal(1, client.SentMessageCounts[^1]);
        // …and the eviction survived the compacted request.
        Assert.DoesNotContain("bad", client.SentToolNames[^1]);
        Assert.Contains("good", client.SentToolNames[^1]);
    }

    /// <summary>Call 1 overflows, call 2 rejects the named tool's schema, call 3 succeeds.</summary>
    private sealed class OverflowThenSchemaClient(string rejectedTool) : ILlmClient
    {
        public int Calls { get; private set; }

        public List<IReadOnlyList<string>> SentToolNames { get; } = [];

        public List<int> SentMessageCounts { get; } = [];

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.Calls++;
            var names = request.Tools.Select(t => t.Name).ToList();
            this.SentToolNames.Add(names);
            this.SentMessageCounts.Add(request.Messages.Count);

            if (this.Calls == 1)
            {
                await Task.Yield();
                throw new LlmClientException(
                    400, """{"type":"error","error":{"message":"prompt is too long: 250000 tokens"}}""");
            }

            var index = names.IndexOf(rejectedTool);
            if (index >= 0)
            {
                await Task.Yield();
                throw new LlmClientException(
                    400,
                    """{"type":"error","error":{"message":"tools.IDX.custom.input_schema.type: Field required"}}"""
                        .Replace("IDX", index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            await Task.Yield();
            yield return AssistantStreamEvent.Delta("done");
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }

    /// <summary>Rejects while ANY of the named tools is still on the wire, blaming the first present.</summary>
    private sealed class MultiRejectingClient(IReadOnlyList<string> bad) : ILlmClient
    {
        public int Calls { get; private set; }

        public List<IReadOnlyList<string>> SentToolNames { get; } = [];

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.Calls++;
            var names = request.Tools.Select(t => t.Name).ToList();
            this.SentToolNames.Add(names);

            var offender = names.FindIndex(bad.Contains);
            if (offender >= 0)
            {
                await Task.Yield();
                throw new LlmClientException(
                    400,
                    """{"type":"error","error":{"message":"tools.IDX.custom.input_schema.type: Field required"}}"""
                        .Replace("IDX", offender.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            await Task.Yield();
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }

    private sealed class AlwaysRejectingClient(string body) : ILlmClient
    {
        public int Calls { get; private set; }

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.Calls++;
            await Task.Yield();
            throw new LlmClientException(400, body);
#pragma warning disable CS0162 // required to make this an iterator
            yield break;
#pragma warning restore CS0162
        }
    }
}
