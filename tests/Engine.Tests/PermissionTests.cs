using System.Text.Json;
using Coda.Agent;

namespace Engine.Tests;

public sealed class PermissionPolicyTests
{
    private sealed class FakeTool(string name, bool readOnly) : ITool
    {
        public string Name => name;
        public string Description => name;
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => readOnly;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult("ok"));
    }

    private static readonly ITool ReadOnly = new FakeTool("read_file", true);
    private static readonly ITool Edit = new FakeTool("edit_file", false);
    private static readonly ITool Write = new FakeTool("write_file", false);
    private static readonly ITool Run = new FakeTool("run_command", false);

    [Theory]
    [InlineData(PermissionMode.Default)]
    [InlineData(PermissionMode.AcceptEdits)]
    [InlineData(PermissionMode.Plan)]
    [InlineData(PermissionMode.BypassPermissions)]
    public void Read_only_tools_are_always_allowed(PermissionMode mode)
    {
        Assert.Equal(PermissionDecision.Allow, PermissionPolicy.Decide(mode, ReadOnly));
    }

    [Fact]
    public void Bypass_allows_all_mutating_tools()
    {
        Assert.Equal(PermissionDecision.Allow, PermissionPolicy.Decide(PermissionMode.BypassPermissions, Edit));
        Assert.Equal(PermissionDecision.Allow, PermissionPolicy.Decide(PermissionMode.BypassPermissions, Run));
    }

    [Fact]
    public void Plan_denies_all_mutating_tools()
    {
        Assert.Equal(PermissionDecision.Deny, PermissionPolicy.Decide(PermissionMode.Plan, Edit));
        Assert.Equal(PermissionDecision.Deny, PermissionPolicy.Decide(PermissionMode.Plan, Write));
        Assert.Equal(PermissionDecision.Deny, PermissionPolicy.Decide(PermissionMode.Plan, Run));
    }

    [Fact]
    public void AcceptEdits_allows_edits_but_asks_for_commands()
    {
        Assert.Equal(PermissionDecision.Allow, PermissionPolicy.Decide(PermissionMode.AcceptEdits, Edit));
        Assert.Equal(PermissionDecision.Allow, PermissionPolicy.Decide(PermissionMode.AcceptEdits, Write));
        Assert.Equal(PermissionDecision.Ask, PermissionPolicy.Decide(PermissionMode.AcceptEdits, Run));
    }

    [Fact]
    public void Default_asks_for_all_mutating_tools()
    {
        Assert.Equal(PermissionDecision.Ask, PermissionPolicy.Decide(PermissionMode.Default, Edit));
        Assert.Equal(PermissionDecision.Ask, PermissionPolicy.Decide(PermissionMode.Default, Run));
    }
}

public sealed class ModePermissionPromptTests
{
    private sealed class CountingPrompt(bool answer) : IPermissionPrompt
    {
        public int Calls { get; private set; }
        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return Task.FromResult(answer);
        }
    }

    private sealed class FakeTool(string name, bool readOnly) : ITool
    {
        public string Name => name;
        public string Description => name;
        public string InputSchemaJson => "{}";
        public bool IsReadOnly => readOnly;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult("ok"));
    }

    private static readonly ITool Run = new FakeTool("run_command", false);

    [Fact]
    public async Task Bypass_allows_without_calling_inner()
    {
        var inner = new CountingPrompt(false);
        var gate = new ModePermissionPrompt(PermissionMode.BypassPermissions, inner);
        Assert.True(await gate.RequestAsync(Run, "x", CancellationToken.None));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Plan_denies_without_calling_inner()
    {
        var inner = new CountingPrompt(true);
        var gate = new ModePermissionPrompt(PermissionMode.Plan, inner);
        Assert.False(await gate.RequestAsync(Run, "x", CancellationToken.None));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Default_ask_delegates_to_inner()
    {
        var inner = new CountingPrompt(true);
        var gate = new ModePermissionPrompt(PermissionMode.Default, inner);
        Assert.True(await gate.RequestAsync(Run, "x", CancellationToken.None));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Ask_with_no_inner_denies()
    {
        var gate = new ModePermissionPrompt(PermissionMode.Default, inner: null);
        Assert.False(await gate.RequestAsync(Run, "x", CancellationToken.None));
    }
}
