using System.Runtime.CompilerServices;
using Coda.Agent;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// End-to-end proof that the cooperative <see cref="AgentExecutionGate"/> parks a REAL
/// <see cref="AgentLoop"/> at its first iteration boundary: with a pause requested up front, the
/// loop must not issue its first model call until the lease is released. Uses a signaling client
/// so the "no first model call" claim is asserted directly, not inferred.
/// </summary>
public sealed class AgentLoopGateTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NonCompletionWindow = TimeSpan.FromMilliseconds(150);

    private static async Task ShouldStayParked(Task task)
    {
        var delay = Task.Delay(NonCompletionWindow);
        var first = await Task.WhenAny(task, delay);
        Assert.Same(delay, first);
    }

    /// <summary>Records entry into the model call so a "not yet called" assertion is exact.</summary>
    private sealed class SignalingClient : ILlmClient
    {
        private int calls;

        public int Calls => Volatile.Read(ref this.calls);

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.calls);
            await Task.Yield();
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputJson) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    private static AgentOptions Options() => new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" };

    [Fact]
    public async Task Loop_paused_before_start_issues_no_model_call_until_released()
    {
        var gate = new AgentExecutionGate();
        // Pause requested while idle: the loop that starts next must park at its first boundary.
        var lease = gate.RequestPause();

        var client = new SignalingClient();
        var loop = new AgentLoop(
            client,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            gate: gate);

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        var run = loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // Parked at the first boundary: NO model call has been issued.
        await ShouldStayParked(run);
        Assert.Equal(0, client.Calls);

        // Release the lease → the loop crosses the boundary and issues its first model call.
        lease.Dispose();
        await run.WaitAsync(CompletionTimeout);
        Assert.Equal(1, client.Calls);
    }
}
