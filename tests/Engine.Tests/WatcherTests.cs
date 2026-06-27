using Coda.Agent;          // ReplHookContext, IPostSamplingHook
using Coda.Agent.Watchers; // the Phase 2 types under test
using LlmClient;

namespace Engine.Tests;

public sealed class WatcherTests
{
    /// <summary>Returns one scripted turn's events; records the last request seen.</summary>
    private sealed class ScriptedClient(params AssistantStreamEvent[] events) : ILlmClient
    {
        public ChatRequest? LastRequest { get; private set; }
        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.LastRequest = request;
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    [Fact]
    public async Task ForkedAgentRunner_returns_collected_text_with_no_tools()
    {
        var client = new ScriptedClient(
            AssistantStreamEvent.Delta("updated "),
            AssistantStreamEvent.Delta("notes"),
            AssistantStreamEvent.Finished("end_turn"));
        var fork = new ForkedAgentRunner(client, "m");

        var text = await fork.RunAsync("sys", [ChatMessage.UserText("go")], CancellationToken.None);

        Assert.Equal("updated notes", text);
        Assert.NotNull(client.LastRequest);
        Assert.Equal("sys", client.LastRequest!.System);
        Assert.Empty(client.LastRequest!.Tools); // a fork advertises no tools
    }

    [Fact]
    public async Task FileSessionMemoryStore_round_trips_and_creates_the_dot_coda_dir()
    {
        var root = Directory.CreateTempSubdirectory("coda_mem_").FullName;
        try
        {
            var store = new FileSessionMemoryStore(root);
            Assert.Null(await store.ReadAsync(CancellationToken.None)); // nothing yet

            await store.WriteAsync("# Session Title\nhello", CancellationToken.None);

            Assert.Equal("# Session Title\nhello", await store.ReadAsync(CancellationToken.None));
            Assert.True(File.Exists(Path.Combine(root, ".coda", "SESSION_MEMORY.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeForkedAgent : IForkedAgent
    {
        private readonly string reply;

        public FakeForkedAgent(string reply)
        {
            this.reply = reply;
        }

        public int Calls { get; private set; }
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public Task<string> RunAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            this.LastMessages = messages;
            return Task.FromResult(this.reply);
        }
    }

    private sealed class InMemoryStore : ISessionMemoryStore
    {
        public string? Content { get; set; }

        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(this.Content);

        public Task WriteAsync(string content, CancellationToken cancellationToken = default)
        {
            this.Content = content;
            return Task.CompletedTask;
        }
    }

    private static ReplHookContext ContextOf(params ChatMessage[] messages) => new()
    {
        Messages = messages,
        SystemPrompt = "sys",
        WorkingDirectory = ".",
    };

    private static ChatMessage AssistantWithTool() =>
        new(ChatRole.Assistant, [new TextBlock("working"), new ToolUseBlock("t1", "echo", "{}")]);

    [Fact]
    public async Task SessionMemoryWatcher_writes_updated_notes_after_a_tool_turn()
    {
        var fork = new FakeForkedAgent("# Session Title\nDoing the thing");
        var store = new InMemoryStore();
        var watcher = new SessionMemoryWatcher(fork, store);

        await watcher.RunAsync(ContextOf(ChatMessage.UserText("do x"), AssistantWithTool()), CancellationToken.None);

        Assert.Equal(1, fork.Calls);
        Assert.Equal("# Session Title\nDoing the thing", store.Content);
    }

    [Fact]
    public async Task SessionMemoryWatcher_skips_when_last_turn_had_no_tools()
    {
        var fork = new FakeForkedAgent("unused");
        var store = new InMemoryStore();
        var watcher = new SessionMemoryWatcher(fork, store);

        // Last assistant turn is plain text — no real work happened.
        var assistantTextOnly = new ChatMessage(ChatRole.Assistant, [new TextBlock("hi there")]);
        await watcher.RunAsync(ContextOf(ChatMessage.UserText("hello"), assistantTextOnly), CancellationToken.None);

        Assert.Equal(0, fork.Calls);
        Assert.Null(store.Content);
    }

    [Fact]
    public async Task SessionMemoryWatcher_seeds_the_default_template_when_no_notes_exist()
    {
        var fork = new FakeForkedAgent("# Session Title\nx");
        var store = new InMemoryStore(); // Content == null
        var watcher = new SessionMemoryWatcher(fork, store);

        await watcher.RunAsync(ContextOf(ChatMessage.UserText("do x"), AssistantWithTool()), CancellationToken.None);

        // The prompt the fork saw included the default template's sections.
        var promptText = ((TextBlock)fork.LastMessages![0].Content[0]).Text;
        Assert.Contains("# Current State", promptText);
        Assert.Contains("# Worklog", promptText);
    }

    [Fact]
    public async Task SessionMemoryWatcher_does_not_write_empty_fork_output()
    {
        var fork = new FakeForkedAgent("   ");
        var store = new InMemoryStore { Content = "old" };
        var watcher = new SessionMemoryWatcher(fork, store);

        await watcher.RunAsync(ContextOf(ChatMessage.UserText("do x"), AssistantWithTool()), CancellationToken.None);

        Assert.Equal("old", store.Content); // unchanged
    }

    [Fact]
    public async Task SessionMemoryWatcher_transcript_includes_tool_result_content()
    {
        // Arrange a history that has a user message, a tool call, a tool result, then
        // a final assistant turn that still has a tool call so the gate fires.
        var messages = new ChatMessage[]
        {
            ChatMessage.UserText("do x"),
            new(ChatRole.Assistant, [new TextBlock("thinking"), new ToolUseBlock("t1", "echo", "{}")]),
            new(ChatRole.User, [new ToolResultBlock("t1", "echoed output", false)]),
            new(ChatRole.Assistant, [new ToolUseBlock("t2", "echo", "{}")]),
        };

        var fork = new FakeForkedAgent("# updated");
        var store = new InMemoryStore();
        var watcher = new SessionMemoryWatcher(fork, store);

        await watcher.RunAsync(ContextOf(messages), CancellationToken.None);

        var promptText = ((TextBlock)fork.LastMessages![0].Content[0]).Text;
        Assert.Contains("User: do x", promptText);
        Assert.Contains("[tool call: echo]", promptText);
        Assert.Contains("echoed output", promptText);
    }

    [Fact]
    public async Task SessionMemoryWatcher_skips_when_only_earlier_turns_had_tools()
    {
        // History: the first assistant turn used a tool, but the LAST assistant turn
        // is text-only — the watcher must NOT fire.
        var messages = new ChatMessage[]
        {
            ChatMessage.UserText("hi"),
            new(ChatRole.Assistant, [new TextBlock("ok"), new ToolUseBlock("t1", "echo", "{}")]),
            new(ChatRole.User, [new ToolResultBlock("t1", "r", false)]),
            new(ChatRole.Assistant, [new TextBlock("all done")]),
        };

        var fork = new FakeForkedAgent("unused");
        var store = new InMemoryStore();
        var watcher = new SessionMemoryWatcher(fork, store);

        await watcher.RunAsync(ContextOf(messages), CancellationToken.None);

        Assert.Equal(0, fork.Calls);
    }
}
