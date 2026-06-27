using System.Text.Json;
using Coda.Agent;
using LlmClient;

namespace Engine.Tests;

public sealed class HookTests
{
    /// <summary>A scripted client: each call returns the next pre-baked turn's events.</summary>
    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;
        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = turns[Math.Min(this.turn, turns.Length - 1)];
            this.turn++;
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    private sealed class RecordingPostSamplingHook : IPostSamplingHook
    {
        public int Calls { get; private set; }
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public Task RunAsync(ReplHookContext context, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            this.LastMessages = context.Messages;
            return Task.CompletedTask;
        }
    }

    private static AgentOptions Options() => new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" };

    [Fact]
    public async Task PostSampling_hook_receives_the_conversation_after_the_turn()
    {
        var turn = new[] { AssistantStreamEvent.Delta("hello"), AssistantStreamEvent.Finished("end_turn") };
        var hook = new RecordingPostSamplingHook();
        var loop = new AgentLoop(
            new ScriptedClient(turn),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            hooks: new AgentHooks(postSampling: [hook]));

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.Equal(1, hook.Calls);
        Assert.NotNull(hook.LastMessages);
        // Snapshot includes the user message and the assistant reply.
        Assert.Contains(hook.LastMessages!, m => m.Role == ChatRole.Assistant);
    }

    /// <summary>Blocks the first stop (injects a nudge), proceeds once stopHookActive.</summary>
    private sealed class ContinueOnceStopHook : IStopHook
    {
        public Task<StopHookDecision> EvaluateAsync(ReplHookContext context, bool stopHookActive, CancellationToken cancellationToken = default)
            => Task.FromResult(stopHookActive ? StopHookDecision.Proceed : StopHookDecision.BlockWith("keep going"));
    }

    /// <summary>Always blocks — used to prove the MaxStopContinuations bound.</summary>
    private sealed class AlwaysBlockStopHook : IStopHook
    {
        public Task<StopHookDecision> EvaluateAsync(ReplHookContext context, bool stopHookActive, CancellationToken cancellationToken = default)
            => Task.FromResult(StopHookDecision.BlockWith("again"));
    }

    private static int CountUserTextMessages(IEnumerable<ChatMessage> history, string text) =>
        history.Count(m => m.Role == ChatRole.User
            && m.Content.Count == 1
            && m.Content[0] is TextBlock t
            && t.Text == text);

    [Fact]
    public async Task StopHook_block_injects_message_and_continues()
    {
        var turn1 = new[] { AssistantStreamEvent.Delta("first"), AssistantStreamEvent.Finished("end_turn") };
        var turn2 = new[] { AssistantStreamEvent.Delta("second"), AssistantStreamEvent.Finished("end_turn") };

        var loop = new AgentLoop(
            new ScriptedClient(turn1, turn2),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            hooks: new AgentHooks(stop: [new ContinueOnceStopHook()]));

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // The nudge was injected exactly once and the agent produced a second turn.
        Assert.Equal(1, CountUserTextMessages(history, "keep going"));
        Assert.Contains(history, m => m.Role == ChatRole.Assistant
            && m.Content.Any(c => c is TextBlock t && t.Text == "second"));
    }

    private sealed class ProceedStopHook : IStopHook
    {
        public Task<StopHookDecision> EvaluateAsync(ReplHookContext context, bool stopHookActive, CancellationToken cancellationToken = default)
            => Task.FromResult(StopHookDecision.Proceed);
    }

    [Fact]
    public async Task StopHook_proceed_stops_normally()
    {
        var turn = new[] { AssistantStreamEvent.Delta("done"), AssistantStreamEvent.Finished("end_turn") };

        var loop = new AgentLoop(
            new ScriptedClient(turn),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            hooks: new AgentHooks(stop: [new ProceedStopHook()]));

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // user + assistant only — no continuation.
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task StopHook_runaway_is_bounded_by_MaxStopContinuations()
    {
        // Every turn ends without tools; the hook always blocks. The bound must stop it.
        var endTurn = new[] { AssistantStreamEvent.Delta("x"), AssistantStreamEvent.Finished("end_turn") };
        var options = Options() with { MaxStopContinuations = 2 };

        var loop = new AgentLoop(
            new ScriptedClient(endTurn), // ScriptedClient repeats the last turn forever
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            options,
            hooks: new AgentHooks(stop: [new AlwaysBlockStopHook()]));

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // Exactly MaxStopContinuations injected nudges, then it stops.
        Assert.Equal(2, CountUserTextMessages(history, "again"));
    }

    /// <summary>
    /// A post-sampling hook backed by a TaskCompletionSource so we can prove the
    /// finally-drain actually awaited the background task.
    /// </summary>
    private sealed class AsyncSignalHook : IPostSamplingHook
    {
        private readonly TaskCompletionSource<bool> tcs = new();

        public bool Completed { get; private set; }

        public async Task RunAsync(ReplHookContext context, CancellationToken cancellationToken = default)
        {
            // Complete the TCS from a background thread after a tiny delay, then await it
            // so the hook body suspends until the external signal arrives.
            _ = Task.Run(async () =>
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                this.tcs.TrySetResult(true);
            }, cancellationToken);

            await this.tcs.Task.ConfigureAwait(false);
            this.Completed = true;
        }
    }

    [Fact]
    public async Task PostSampling_background_drain_is_awaited_before_RunAsync_returns()
    {
        // The hook only finishes after an async delay; if the finally-drain didn't await
        // the background task, Completed would still be false when we check.
        var hook = new AsyncSignalHook();
        var turn = new[] { AssistantStreamEvent.Delta("hi"), AssistantStreamEvent.Finished("end_turn") };

        var loop = new AgentLoop(
            new ScriptedClient(turn),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            hooks: new AgentHooks(postSampling: [hook]));

        var history = new List<ChatMessage> { ChatMessage.UserText("hello") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // If the drain was properly awaited, Completed must be true here.
        Assert.True(hook.Completed, "RunAsync returned before the background hook task completed — drain not awaited.");
    }

    /// <summary>A minimal read-only echo tool for use in scripted multi-turn tests.</summary>
    private sealed class EchoTool : ITool
    {
        public string Name => "echo";
        public string Description => "Echoes its input.";
        public string InputSchemaJson => """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""";
        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            var text = input.TryGetProperty("text", out var prop) ? prop.GetString() ?? "" : "";
            return Task.FromResult(new ToolResult(text));
        }
    }

    [Fact]
    public async Task PostSampling_fires_once_per_assistant_turn_across_tool_iteration()
    {
        // Turn 1: assistant requests the echo tool, then signals tool_use stop.
        var turn1 = new AssistantStreamEvent[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("t1", "echo", """{"text":"ping"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };

        // Turn 2: assistant produces plain text and ends.
        var turn2 = new AssistantStreamEvent[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var hook = new RecordingPostSamplingHook();
        var registry = new ToolRegistry([new EchoTool()]);

        var loop = new AgentLoop(
            new ScriptedClient(turn1, turn2),
            registry,
            new AllowAllPermissionPrompt(),
            Options(),
            hooks: new AgentHooks(postSampling: [hook]));

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // One fire after turn 1 (tool turn), one after turn 2 (final turn).
        Assert.Equal(2, hook.Calls);
    }
}
