using System.Net.Http;
using Coda.Agent;
using Coda.Agent.Classifier;
using Coda.Agent.Watchers;
using LlmClient;

namespace Engine.Tests;

public sealed class ClassifierTests
{
    [Fact]
    public void Parse_allow_yields_allow()
    {
        var verdict = ToolActionClassifierPrompt.Parse("ALLOW");
        Assert.Equal(PermissionDecision.Allow, verdict.Decision);
    }

    [Fact]
    public void Parse_ask_yields_ask_with_reason()
    {
        var verdict = ToolActionClassifierPrompt.Parse("ASK: deletes the database");
        Assert.Equal(PermissionDecision.Ask, verdict.Decision);
        Assert.Equal("deletes the database", verdict.Reason);
    }

    [Fact]
    public void Parse_unparseable_fails_closed_to_ask()
    {
        var verdict = ToolActionClassifierPrompt.Parse("um, maybe? I'm not sure");
        Assert.Equal(PermissionDecision.Ask, verdict.Decision);
        Assert.NotNull(verdict.Reason); // carries a "blocking for safety" reason
    }

    [Fact]
    public void Parse_empty_fails_closed_to_ask()
    {
        var verdict = ToolActionClassifierPrompt.Parse("   ");
        Assert.Equal(PermissionDecision.Ask, verdict.Decision);
    }

    [Fact]
    public void Parse_treats_block_or_deny_as_ask()
    {
        Assert.Equal(PermissionDecision.Ask, ToolActionClassifierPrompt.Parse("BLOCK: rm -rf /").Decision);
        Assert.Equal(PermissionDecision.Ask, ToolActionClassifierPrompt.Parse("DENY").Decision);
    }

    private sealed class ThrowingForkedAgent : IForkedAgent
    {
        private readonly Exception error;

        public ThrowingForkedAgent(Exception error)
        {
            this.error = error;
        }

        public Task<string> RunAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw this.error;
    }

    private sealed class FakeForkedAgent : IForkedAgent
    {
        private readonly string reply;

        public FakeForkedAgent(string reply)
        {
            this.reply = reply;
        }

        public int Calls { get; private set; }

        public Task<string> RunAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return Task.FromResult(this.reply);
        }
    }

    private sealed class FixedPrompt : IPermissionPrompt
    {
        private readonly bool answer;

        public FixedPrompt(bool answer)
        {
            this.answer = answer;
        }

        public int Calls { get; private set; }

        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return Task.FromResult(this.answer);
        }
    }

    private sealed class FakeTool : ITool
    {
        public string Name => "run_command";
        public string Description => "runs a shell command";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => false;

        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult("ran"));
    }

    [Fact]
    public async Task LlmClassifier_allows_a_safe_action()
    {
        var classifier = new LlmToolActionClassifier(new FakeForkedAgent("ALLOW"));
        var verdict = await classifier.ClassifyAsync("read_file", "{\"path\":\"a\"}", CancellationToken.None);
        Assert.Equal(PermissionDecision.Allow, verdict.Decision);
    }

    [Fact]
    public async Task LlmClassifier_escalates_a_risky_action_with_reason()
    {
        var classifier = new LlmToolActionClassifier(new FakeForkedAgent("ASK: rm -rf is destructive"));
        var verdict = await classifier.ClassifyAsync("run_command", "{\"cmd\":\"rm -rf /\"}", CancellationToken.None);
        Assert.Equal(PermissionDecision.Ask, verdict.Decision);
        Assert.Equal("rm -rf is destructive", verdict.Reason);
    }

    [Fact]
    public async Task ClassifierPrompt_allows_when_classifier_allows_without_asking_inner()
    {
        var inner = new FixedPrompt(answer: false);
        var prompt = new ClassifierPermissionPrompt(new LlmToolActionClassifier(new FakeForkedAgent("ALLOW")), inner);

        var allowed = await prompt.RequestAsync(new FakeTool(), "{}", CancellationToken.None);

        Assert.True(allowed);
        Assert.Equal(0, inner.Calls); // safe action never bothered the user
    }

    [Fact]
    public async Task ClassifierPrompt_escalates_risky_action_to_the_inner_prompt()
    {
        var inner = new FixedPrompt(answer: true);
        var prompt = new ClassifierPermissionPrompt(new LlmToolActionClassifier(new FakeForkedAgent("ASK: destructive")), inner);

        var allowed = await prompt.RequestAsync(new FakeTool(), "{}", CancellationToken.None);

        Assert.True(allowed);           // user approved
        Assert.Equal(1, inner.Calls);   // and was asked
    }

    [Fact]
    public async Task ClassifierPrompt_denies_risky_action_when_no_inner_prompt()
    {
        var prompt = new ClassifierPermissionPrompt(new LlmToolActionClassifier(new FakeForkedAgent("ASK: destructive")), inner: null);

        var allowed = await prompt.RequestAsync(new FakeTool(), "{}", CancellationToken.None);

        Assert.False(allowed); // headless: risky → denied
    }

    [Fact]
    public async Task LlmClassifier_converts_exception_to_ask_for_safety()
    {
        var classifier = new LlmToolActionClassifier(new ThrowingForkedAgent(new HttpRequestException("timeout")));
        var verdict = await classifier.ClassifyAsync("run_command", "{}", CancellationToken.None);
        Assert.Equal(PermissionDecision.Ask, verdict.Decision);
        Assert.Contains("blocking for safety", verdict.Reason);
    }

    [Fact]
    public async Task LlmClassifier_propagates_cancellation()
    {
        var classifier = new LlmToolActionClassifier(new ThrowingForkedAgent(new OperationCanceledException()));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => classifier.ClassifyAsync("run_command", "{}", CancellationToken.None));
    }
}
