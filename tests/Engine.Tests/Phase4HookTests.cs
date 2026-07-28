using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Hooks;
using Coda.Agent.Permissions;
using Coda.Agent.Settings;
using Engine.Tests.TestSupport;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

/// <summary>
/// TDD tests for Phase 4 of the agent-hooks system:
/// <list type="bullet">
///   <item>Item 1 — <c>PreToolUse.modifiedInput</c> (total replacement of the tool arguments).</item>
///   <item>Item 2 — <c>PostToolUse</c> gains <c>modifiedResult</c>, <c>decision:block</c>, and failure payloads.</item>
///   <item>Item 3 — the <c>PermissionRequest</c> event (allow / deny / prompt, updatedPermissions, matchedRule).</item>
///   <item>Item 4 — surfacing via the new <see cref="IAgentSink"/> notifications.</item>
/// </list>
/// </summary>
public sealed class Phase4HookTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_hooks_p4_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { }
    }

    // =========================================================================
    // Shared test infrastructure
    // =========================================================================

    /// <summary>Minimal scripted LLM client: each StreamAsync call returns the next pre-baked turn.</summary>
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

    /// <summary>Records every Phase 4 sink notification plus the tool call/result stream.</summary>
    private sealed class RecordingAgentSink : IAgentSink
    {
        public List<(string ToolName, string InputJson)> ToolCalls { get; } = [];

        public List<(string ToolName, ToolResult Result)> ToolResults { get; } = [];

        public List<(string HookCommand, string ToolName, string Original, string Modified)> InputModified { get; } = [];

        public List<(string HookCommand, string ToolName, string Original, string Modified)> ResultModified { get; } = [];

        public List<(string HookCommand, string ToolName, string Decision)> PermissionDecided { get; } = [];

        public List<(string HookCommand, string? ModeApplied, IReadOnlyList<string> AddedAllow, IReadOnlyList<string> AddedDeny)> PermissionsUpdated { get; } = [];

        public void OnAssistantText(string delta) { }

        public void OnAssistantTextComplete() { }

        public void OnToolCall(string toolName, string inputJson) => this.ToolCalls.Add((toolName, inputJson));

        public void OnToolResult(string toolName, ToolResult result) => this.ToolResults.Add((toolName, result));

        public void OnError(string message) { }

        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }

        public void OnToolInputModified(string hookCommand, string toolName, string originalInput, string modifiedInput) =>
            this.InputModified.Add((hookCommand, toolName, originalInput, modifiedInput));

        public void OnToolResultModified(string hookCommand, string toolName, string originalResult, string modifiedResult) =>
            this.ResultModified.Add((hookCommand, toolName, originalResult, modifiedResult));

        public void OnPermissionDecided(string hookCommand, string toolName, string decision) =>
            this.PermissionDecided.Add((hookCommand, toolName, decision));

        public void OnPermissionsUpdated(
            string hookCommand,
            string? modeApplied,
            IReadOnlyList<string> addedAllow,
            IReadOnlyList<string> addedDeny) =>
            this.PermissionsUpdated.Add((hookCommand, modeApplied, addedAllow, addedDeny));
    }

    /// <summary>Hook executor driven by a delegate; records the command/event/payload of every call.</summary>
    private sealed class ScriptedExecutor(
        Func<string, string, CancellationToken, Task<(int ExitCode, string Stdout, string Stderr)>> fn)
        : IHookExecutor
    {
        public List<(string Command, string EventName, string Payload)> Calls { get; } = [];

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct)
        {
            var eventName = string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                eventName = doc.RootElement.GetProperty("event").GetString() ?? string.Empty;
            }
            catch (JsonException)
            {
            }

            this.Calls.Add((command, eventName, payload));
            return fn(command, payload, ct);
        }
    }

    /// <summary>A tool that records the exact JSON arguments it executed with.</summary>
    private sealed class RecordingTool(
        string name = "danger",
        bool readOnly = false,
        Func<JsonElement, ToolResult>? behaviour = null) : ITool
    {
        public List<string> ReceivedInputs { get; } = [];

        public bool Executed => this.ReceivedInputs.Count > 0;

        public string Name => name;

        public string Description => "test tool";

        public string InputSchemaJson => "{\"type\":\"object\"}";

        public bool IsReadOnly => readOnly;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            this.ReceivedInputs.Add(input.GetRawText());
            return Task.FromResult(behaviour?.Invoke(input) ?? new ToolResult("tool ran"));
        }
    }

    /// <summary>Permission prompt that counts interactive prompts and returns a fixed answer.</summary>
    private sealed class CountingPermissionPrompt(bool answer = true) : IPermissionPrompt
    {
        public int Calls { get; private set; }

        public string? LastInput { get; private set; }

        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            this.LastInput = inputPreview;
            return Task.FromResult(answer);
        }
    }

    private static AgentOptions Options(string? workingDirectory = null, PermissionModeState? modeState = null) => new()
    {
        SystemPrompt = "sys",
        WorkingDirectory = workingDirectory ?? ".",
        Model = "m",
        PermissionModeState = modeState,
    };

    private static UserHookRunner Runner(
        IReadOnlyList<UserHook> hooks,
        IHookExecutor executor,
        ILogger? logger = null) =>
        new(hooks, executor, context: null, logger: logger);

    private static IHookExecutor Exec(string stdout, int exitCode = 0) =>
        new ScriptedExecutor((_, _, _) => Task.FromResult((exitCode, stdout, string.Empty)));

    /// <summary>
    /// Runs a single turn where the model calls <paramref name="tool"/> once and then ends the turn.
    /// </summary>
    private static async Task<List<ChatMessage>> RunToolTurnAsync(
        ITool tool,
        UserHookRunner? hooks,
        IPermissionPrompt permissions,
        IAgentSink sink,
        string inputJson = "{}",
        PermissionRuleStore? ruleStore = null,
        AgentOptions? options = null,
        ILogger? agentLogger = null)
    {
        var turn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", tool.Name, inputJson)),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var turn2 = new[] { AssistantStreamEvent.Finished("end_turn") };

        var loop = new AgentLoop(
            new ScriptedClient(turn1, turn2),
            new ToolRegistry([tool]),
            permissions,
            options ?? Options(),
            userHooks: hooks,
            permissionRules: ruleStore,
            logger: agentLogger);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, sink, CancellationToken.None);
        return history;
    }

    private static string ToolResultText(List<ChatMessage> history)
    {
        var block = history
            .SelectMany(m => m.Content)
            .OfType<ToolResultBlock>()
            .Last();
        return block.Content;
    }

    // =========================================================================
    // Item 1 — PreToolUse.modifiedInput
    // =========================================================================

    [Fact]
    public async Task Test01_modifiedInput_replaces_arguments_and_is_what_tool_activity_reports()
    {
        var executor = new ScriptedExecutor((_, _, _) => Task.FromResult((
            0,
            """{"hookSpecificOutput":{"modifiedInput":{"path":"safe.txt"}}}""",
            string.Empty)));

        var tool = new RecordingTool();
        var sink = new RecordingAgentSink();

        await RunToolTurnAsync(
            tool,
            Runner([new UserHook("PreToolUse", "rewrite-input")], executor),
            new CountingPermissionPrompt(),
            sink,
            inputJson: """{"path":"secret.txt"}""");

        // The tool executed with the REPLACED arguments (total replacement, not a merge).
        var executedInput = Assert.Single(tool.ReceivedInputs);
        Assert.Contains("safe.txt", executedInput);
        Assert.DoesNotContain("secret.txt", executedInput);

        // Tool activity reports what actually ran.
        var call = Assert.Single(sink.ToolCalls);
        Assert.Contains("safe.txt", call.InputJson);

        // The modification is surfaced.
        var modified = Assert.Single(sink.InputModified);
        Assert.Equal("rewrite-input", modified.HookCommand);
        Assert.Equal("danger", modified.ToolName);
        Assert.Contains("secret.txt", modified.Original);
        Assert.Contains("safe.txt", modified.Modified);
    }

    [Fact]
    public async Task Test02_non_object_modifiedInput_is_ignored_with_a_warning()
    {
        var logger = new CapturingLogger();
        var executor = new ScriptedExecutor((_, _, _) => Task.FromResult((
            0,
            """{"hookSpecificOutput":{"modifiedInput":"not-an-object"}}""",
            string.Empty)));

        var tool = new RecordingTool();
        var sink = new RecordingAgentSink();

        await RunToolTurnAsync(
            tool,
            Runner([new UserHook("PreToolUse", "bad-hook")], executor, logger),
            new CountingPermissionPrompt(),
            sink,
            inputJson: """{"path":"original.txt"}""");

        // The turn proceeds with the ORIGINAL input.
        var executedInput = Assert.Single(tool.ReceivedInputs);
        Assert.Contains("original.txt", executedInput);
        Assert.Empty(sink.InputModified);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("modifiedInput", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test03_two_PreToolUse_hooks_returning_modifiedInput_last_writer_wins_and_is_logged()
    {
        var logger = new CapturingLogger();
        var executor = new ScriptedExecutor((command, _, _) => Task.FromResult((
            0,
            command == "hook1"
                ? """{"hookSpecificOutput":{"modifiedInput":{"path":"first.txt"}}}"""
                : """{"hookSpecificOutput":{"modifiedInput":{"path":"second.txt"}}}""",
            string.Empty)));

        var tool = new RecordingTool();
        var sink = new RecordingAgentSink();

        await RunToolTurnAsync(
            tool,
            Runner(
                [new UserHook("PreToolUse", "hook1"), new UserHook("PreToolUse", "hook2")],
                executor,
                logger),
            new CountingPermissionPrompt(),
            sink,
            inputJson: """{"path":"original.txt"}""");

        var executedInput = Assert.Single(tool.ReceivedInputs);
        Assert.Contains("second.txt", executedInput);
        Assert.DoesNotContain("first.txt", executedInput);

        var modified = Assert.Single(sink.InputModified);
        Assert.Equal("hook2", modified.HookCommand);

        Assert.Contains(logger.Entries, e => e.Message.Contains("modifiedInput", StringComparison.Ordinal));
    }

    // =========================================================================
    // Item 2 — PostToolUse gains real power
    // =========================================================================

    [Fact]
    public async Task Test04_PostToolUse_modifiedResult_changes_what_the_model_sees_but_the_tool_still_ran()
    {
        var executor = new ScriptedExecutor((_, _, _) => Task.FromResult((
            0,
            """{"hookSpecificOutput":{"modifiedResult":"[redacted]"}}""",
            string.Empty)));

        var tool = new RecordingTool(behaviour: _ => new ToolResult("secret output"));
        var sink = new RecordingAgentSink();

        var history = await RunToolTurnAsync(
            tool,
            Runner([new UserHook("PostToolUse", "redact")], executor),
            new CountingPermissionPrompt(),
            sink);

        Assert.True(tool.Executed);
        Assert.Equal("[redacted]", ToolResultText(history));

        var modified = Assert.Single(sink.ResultModified);
        Assert.Equal("redact", modified.HookCommand);
        Assert.Equal("secret output", modified.Original);
        Assert.Equal("[redacted]", modified.Modified);
    }

    [Fact]
    public async Task Test05_PostToolUse_block_replaces_the_result_with_the_reason()
    {
        var executor = new ScriptedExecutor((_, _, _) => Task.FromResult((
            0,
            """{"decision":"block","reason":"contains a secret"}""",
            string.Empty)));

        var tool = new RecordingTool(behaviour: _ => new ToolResult("secret output"));
        var sink = new RecordingAgentSink();

        var history = await RunToolTurnAsync(
            tool,
            Runner([new UserHook("PostToolUse", "block-result")], executor),
            new CountingPermissionPrompt(),
            sink);

        Assert.True(tool.Executed);
        Assert.Equal("contains a secret", ToolResultText(history));
        Assert.DoesNotContain("secret output", ToolResultText(history));
    }

    [Fact]
    public async Task Test06_PostToolUse_fires_on_a_failed_tool_call_with_the_error_field_populated()
    {
        var executor = new ScriptedExecutor((_, _, _) => Task.FromResult((0, string.Empty, string.Empty)));

        var tool = new RecordingTool(behaviour: _ => throw new InvalidOperationException("kaboom"));
        var sink = new RecordingAgentSink();

        await RunToolTurnAsync(
            tool,
            Runner([new UserHook("PostToolUse", "observe")], executor),
            new CountingPermissionPrompt(),
            sink);

        var post = Assert.Single(executor.Calls, c => c.EventName == "PostToolUse");
        using var doc = JsonDocument.Parse(post.Payload);
        Assert.True(doc.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("kaboom", error.GetString()!);
        Assert.True(doc.RootElement.TryGetProperty("result", out var result));
        Assert.Contains("kaboom", result.GetString()!);
    }

    [Fact]
    public async Task Test07_a_failing_PostToolUse_hook_does_not_fail_the_tool_call()
    {
        var executor = new ScriptedExecutor((_, _, _) =>
            throw new InvalidOperationException("hook process failed"));

        var tool = new RecordingTool(behaviour: _ => new ToolResult("tool output"));
        var sink = new RecordingAgentSink();

        var history = await RunToolTurnAsync(
            tool,
            Runner([new UserHook("PostToolUse", "broken")], executor),
            new CountingPermissionPrompt(),
            sink);

        Assert.True(tool.Executed);
        Assert.Equal("tool output", ToolResultText(history));
        var result = Assert.Single(sink.ToolResults);
        Assert.False(result.Result.IsError);
    }

    // =========================================================================
    // Item 3 — the PermissionRequest event
    // =========================================================================

    [Fact]
    public async Task Test08_PermissionRequest_fires_only_when_approval_is_needed()
    {
        // Read-only tool: never prompts, so PermissionRequest must not fire.
        var readOnlyExecutor = new ScriptedExecutor((_, _, _) => Task.FromResult((0, string.Empty, string.Empty)));
        await RunToolTurnAsync(
            new RecordingTool("reader", readOnly: true),
            Runner([new UserHook("PermissionRequest", "gate")], readOnlyExecutor),
            new CountingPermissionPrompt(),
            new RecordingAgentSink());

        Assert.DoesNotContain(readOnlyExecutor.Calls, c => c.EventName == "PermissionRequest");

        // Mutating tool: fires exactly once.
        var mutatingExecutor = new ScriptedExecutor((_, _, _) => Task.FromResult((0, string.Empty, string.Empty)));
        await RunToolTurnAsync(
            new RecordingTool(),
            Runner([new UserHook("PermissionRequest", "gate")], mutatingExecutor),
            new CountingPermissionPrompt(),
            new RecordingAgentSink());

        Assert.Single(mutatingExecutor.Calls, c => c.EventName == "PermissionRequest");
    }

    [Fact]
    public async Task Test09_PermissionRequest_allow_skips_the_prompt_deny_refuses_and_prompt_falls_through()
    {
        // allow → the interactive prompt is never consulted, the tool runs.
        var allowTool = new RecordingTool();
        var allowPrompt = new CountingPermissionPrompt();
        var allowSink = new RecordingAgentSink();
        await RunToolTurnAsync(
            allowTool,
            Runner([new UserHook("PermissionRequest", "gate")], Exec("""{"decision":"allow"}""")),
            allowPrompt,
            allowSink);

        Assert.True(allowTool.Executed);
        Assert.Equal(0, allowPrompt.Calls);
        Assert.Contains(allowSink.PermissionDecided, d => d.Decision == "allow");

        // deny → the tool never runs and the prompt is never consulted.
        var denyTool = new RecordingTool();
        var denyPrompt = new CountingPermissionPrompt();
        var denySink = new RecordingAgentSink();
        var denyHistory = await RunToolTurnAsync(
            denyTool,
            Runner([new UserHook("PermissionRequest", "gate")], Exec("""{"decision":"deny","reason":"policy says no"}""")),
            denyPrompt,
            denySink);

        Assert.False(denyTool.Executed);
        Assert.Equal(0, denyPrompt.Calls);
        Assert.Contains("policy says no", ToolResultText(denyHistory));
        Assert.Contains(denySink.PermissionDecided, d => d.Decision == "deny");

        // prompt → falls through to the interactive prompt.
        var promptTool = new RecordingTool();
        var promptPrompt = new CountingPermissionPrompt();
        var promptSink = new RecordingAgentSink();
        await RunToolTurnAsync(
            promptTool,
            Runner([new UserHook("PermissionRequest", "gate")], Exec("""{"decision":"prompt"}""")),
            promptPrompt,
            promptSink);

        Assert.True(promptTool.Executed);
        Assert.Equal(1, promptPrompt.Calls);
        Assert.Empty(promptSink.PermissionDecided);

        // no answer at all → also falls through.
        var silentTool = new RecordingTool();
        var silentPrompt = new CountingPermissionPrompt();
        var silentSink = new RecordingAgentSink();
        await RunToolTurnAsync(
            silentTool,
            Runner([new UserHook("PermissionRequest", "gate")], Exec("{}")),
            silentPrompt,
            silentSink);

        Assert.True(silentTool.Executed);
        Assert.Equal(1, silentPrompt.Calls);
        Assert.Empty(silentSink.PermissionDecided);
    }

    [Fact]
    public async Task Test10_a_timing_out_PermissionRequest_hook_denies()
    {
        var executor = new ScriptedExecutor(async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return (0, """{"decision":"allow"}""", string.Empty);
        });

        var tool = new RecordingTool();
        var prompt = new CountingPermissionPrompt();
        var sink = new RecordingAgentSink();

        var history = await RunToolTurnAsync(
            tool,
            Runner([new UserHook("PermissionRequest", "slow-gate", TimeoutSeconds: 1)], executor),
            prompt,
            sink);

        Assert.False(tool.Executed);
        Assert.Equal(0, prompt.Calls);
        Assert.Contains(sink.PermissionDecided, d => d.Decision == "deny");
        Assert.Contains("timed out", ToolResultText(history), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test11_updatedPermissions_session_scope_is_live_only_project_scope_writes_settings()
    {
        // --- session scope: live state only, no settings file written ---
        var sessionDir = Directory.CreateDirectory(Path.Combine(this.root, "session")).FullName;
        var store = new PermissionRuleStore();
        var modeState = new PermissionModeState(PermissionMode.Default);

        await RunToolTurnAsync(
            new RecordingTool(),
            Runner(
                [new UserHook("PermissionRequest", "grant")],
                Exec("""
                {"decision":"allow","hookSpecificOutput":{"updatedPermissions":{"addRules":{"allow":["danger"]},"setMode":"acceptEdits","scope":"session"}}}
                """)),
            new CountingPermissionPrompt(),
            new RecordingAgentSink(),
            ruleStore: store,
            options: Options(sessionDir, modeState));

        Assert.Equal("allow:danger", store.FindMatchedRule("danger", "{}"));
        Assert.Equal(PermissionMode.AcceptEdits, modeState.Mode);
        Assert.False(File.Exists(Path.Combine(sessionDir, ".coda", "settings.json")));

        // --- project scope: the settings file is written ---
        var projectDir = Directory.CreateDirectory(Path.Combine(this.root, "project")).FullName;

        await RunToolTurnAsync(
            new RecordingTool(),
            Runner(
                [new UserHook("PermissionRequest", "grant")],
                Exec("""
                {"decision":"allow","hookSpecificOutput":{"updatedPermissions":{"addRules":{"deny":["danger(rm:*)"]},"scope":"project"}}}
                """)),
            new CountingPermissionPrompt(),
            new RecordingAgentSink(),
            ruleStore: new PermissionRuleStore(),
            options: Options(projectDir));

        var settingsFile = Path.Combine(projectDir, ".coda", "settings.json");
        Assert.True(File.Exists(settingsFile));
        Assert.Contains("danger(rm:*)", File.ReadAllText(settingsFile));

        // --- a write failure is logged and does not fail the turn ---
        var brokenDir = Directory.CreateDirectory(Path.Combine(this.root, "broken")).FullName;
        File.WriteAllText(Path.Combine(brokenDir, ".coda"), "not a directory");

        var logger = new CapturingLogger();
        var tool = new RecordingTool();
        var history = await RunToolTurnAsync(
            tool,
            Runner(
                [new UserHook("PermissionRequest", "grant")],
                Exec("""
                {"decision":"allow","hookSpecificOutput":{"updatedPermissions":{"addRules":{"allow":["danger"]},"scope":"project"}}}
                """)),
            new CountingPermissionPrompt(),
            new RecordingAgentSink(),
            ruleStore: new PermissionRuleStore(),
            options: Options(brokenDir));

        Assert.True(tool.Executed);
        Assert.Equal("tool ran", ToolResultText(history));
    }

    [Fact]
    public async Task Test12_matchedRule_is_populated_when_a_rule_matched_and_null_otherwise()
    {
        // A deny rule matches → "deny:<rule>".
        var store = new PermissionRuleStore(
            allow: [PermissionRule.Parse("danger")],
            deny: [PermissionRule.Parse("danger(rm:*)")]);

        Assert.Equal("deny:danger(rm:*)", store.FindMatchedRule("danger", """{"command":"rm -rf /"}"""));
        Assert.Equal("allow:danger", store.FindMatchedRule("danger", """{"command":"ls"}"""));
        Assert.Null(store.FindMatchedRule("other_tool", "{}"));

        // The payload carries it.
        var matchedExecutor = new ScriptedExecutor((_, _, _) => Task.FromResult((0, string.Empty, string.Empty)));
        await RunToolTurnAsync(
            new RecordingTool(),
            Runner([new UserHook("PermissionRequest", "gate")], matchedExecutor),
            new CountingPermissionPrompt(),
            new RecordingAgentSink(),
            inputJson: """{"command":"rm -rf /"}""",
            ruleStore: store);

        var call = Assert.Single(matchedExecutor.Calls, c => c.EventName == "PermissionRequest");
        using (var doc = JsonDocument.Parse(call.Payload))
        {
            Assert.Equal("deny:danger(rm:*)", doc.RootElement.GetProperty("matchedRule").GetString());
            Assert.Equal("default", doc.RootElement.GetProperty("permissionMode").GetString());
        }

        // No rule store at all → matchedRule is null.
        var unmatchedExecutor = new ScriptedExecutor((_, _, _) => Task.FromResult((0, string.Empty, string.Empty)));
        await RunToolTurnAsync(
            new RecordingTool(),
            Runner([new UserHook("PermissionRequest", "gate")], unmatchedExecutor),
            new CountingPermissionPrompt(),
            new RecordingAgentSink());

        var unmatched = Assert.Single(unmatchedExecutor.Calls, c => c.EventName == "PermissionRequest");
        using (var doc = JsonDocument.Parse(unmatched.Payload))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("matchedRule").ValueKind);
        }
    }

    // =========================================================================
    // Item 4 — no hooks configured: unchanged behaviour
    // =========================================================================

    [Fact]
    public async Task Test13_no_hooks_configured_leaves_every_path_unchanged()
    {
        var tool = new RecordingTool(behaviour: _ => new ToolResult("plain output"));
        var prompt = new CountingPermissionPrompt();
        var sink = new RecordingAgentSink();

        var history = await RunToolTurnAsync(
            tool,
            hooks: null,
            prompt,
            sink,
            inputJson: """{"path":"a.txt"}""");

        Assert.True(tool.Executed);
        Assert.Equal("plain output", ToolResultText(history));
        Assert.Equal(1, prompt.Calls);

        var call = Assert.Single(sink.ToolCalls);
        Assert.Equal("""{"path":"a.txt"}""", call.InputJson);
        Assert.Empty(sink.InputModified);
        Assert.Empty(sink.ResultModified);
        Assert.Empty(sink.PermissionDecided);

        // The failure path is equally untouched.
        var failingTool = new RecordingTool(behaviour: _ => throw new InvalidOperationException("boom"));
        var failingHistory = await RunToolTurnAsync(
            failingTool,
            hooks: null,
            new CountingPermissionPrompt(),
            new RecordingAgentSink());

        Assert.Contains("boom", ToolResultText(failingHistory));
    }

    // =========================================================================
    // SettingsWriter — user/project permission-rule persistence
    // =========================================================================

    [Fact]
    public void AddPermissionRules_merges_into_the_permissions_section()
    {
        var dir = Directory.CreateDirectory(Path.Combine(this.root, "writer", ".coda")).FullName;
        var file = Path.Combine(dir, "settings.json");
        File.WriteAllText(file, """{"permissions":{"allow":["existing"]},"theme":"dark"}""");

        SettingsWriter.AddPermissionRules(["danger"], ["danger(rm:*)"], file);

        var text = File.ReadAllText(file);
        Assert.Contains("existing", text);
        Assert.Contains("danger", text);
        Assert.Contains("danger(rm:*)", text);
        // Unmodelled keys survive.
        Assert.Contains("dark", text);
        // defaultMode is not written (I2 fix: setMode is session-scoped only).
        Assert.DoesNotContain("defaultMode", text);
    }

    // =========================================================================
    // C1 — hook-added deny with no initial rules is enforced
    // =========================================================================

    [Fact]
    public async Task C1_hook_added_deny_with_no_initial_rules_is_enforced_on_next_call()
    {
        // Regression: when a session starts with no pre-configured rules, a deny rule installed by a
        // PermissionRequest hook must still take effect on the very next tool call. Without the fix, the
        // rule store is orphaned (never connected to RulesPermissionPrompt) and the deny is silently ignored.

        // Two "danger" tool calls in one LLM turn.
        var turn1Events = new IReadOnlyList<AssistantStreamEvent>[]
        {
            [
                AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", "danger", "{}")),
                AssistantStreamEvent.Tool(new ToolUseBlock("tu_2", "danger", "{}")),
                AssistantStreamEvent.Finished("tool_use"),
            ],
        };
        // The LLM gets a second chance after both results are returned.
        var turn2 = new[] { AssistantStreamEvent.Finished("end_turn") };

        var callIndex = 0;
        var executor = new ScriptedExecutor((_, _, _) =>
        {
            callIndex++;
            return Task.FromResult(callIndex == 1
                // First call: allow + add deny rule for "danger" session-scoped.
                ? (0,
                    """{"decision":"allow","hookSpecificOutput":{"updatedPermissions":{"addRules":{"deny":["danger"]},"scope":"session"}}}""",
                    string.Empty)
                // Second call: return empty (no opinion) — falls through to RulesPermissionPrompt.
                : (0, string.Empty, string.Empty));
        });

        var store = new PermissionRuleStore();
        var prompt = new CountingPermissionPrompt(answer: true);
        var sink = new RecordingAgentSink();

        var loop = new AgentLoop(
            new ScriptedClient(turn1Events[0], turn2),
            new ToolRegistry([new RecordingTool()]),
            new RulesPermissionPrompt(store, prompt),
            Options(),
            userHooks: Runner([new UserHook("PermissionRequest", "gate")], executor),
            permissionRules: store);

        await loop.RunAsync([ChatMessage.UserText("go")], sink, CancellationToken.None);

        // Hook ran twice (once per tool call).
        Assert.Equal(2, callIndex);

        // The second tool call must have been denied by the rule — the interactive prompt
        // must NOT have been called for it (the deny rule short-circuits the prompt).
        Assert.Equal(0, prompt.Calls);

        // The result of the second call is an error (denied).
        var decisions = sink.PermissionDecided;
        // First call: allow (by hook)
        Assert.Contains(decisions, d => d.ToolName == "danger" && d.Decision == "allow");
        // Second call: deny (by rule store via RulesPermissionPrompt)
        // Note: RulesPermissionPrompt.RequestAsync returns false → denied — but this is NOT via the hook,
        // so OnPermissionDecided is NOT fired for it. The tool result will be an error.
        Assert.Equal(1, decisions.Count(d => d.Decision == "allow"));
    }

    // =========================================================================
    // I1 — hook cannot escalate session to bypassPermissions
    // =========================================================================

    [Fact]
    public async Task I1_hook_cannot_escalate_session_to_bypassPermissions()
    {
        // A PermissionRequest hook trying to flip the session into bypassPermissions must be refused.
        // The mode must remain unchanged and a warning must be logged.
        var modeState = new PermissionModeState(PermissionMode.Default);
        var agentLogger = new CapturingLogger();

        await RunToolTurnAsync(
            new RecordingTool(),
            Runner(
                [new UserHook("PermissionRequest", "bad-hook")],
                Exec("""{"decision":"allow","hookSpecificOutput":{"updatedPermissions":{"setMode":"bypassPermissions","scope":"session"}}}""")),
            new CountingPermissionPrompt(),
            new RecordingAgentSink(),
            ruleStore: new PermissionRuleStore(),
            options: Options(modeState: modeState),
            agentLogger: agentLogger);

        // Mode must NOT have changed to bypass.
        Assert.Equal(PermissionMode.Default, modeState.Mode);

        // A warning must have been logged naming the refused hook.
        Assert.Contains(agentLogger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("bad-hook", StringComparison.Ordinal)
            && e.Message.Contains("bypass", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task I1_OnPermissionsUpdated_is_emitted_when_hook_applies_rules_or_mode()
    {
        // OnPermissionsUpdated must be emitted when a hook's updatedPermissions actually changes
        // something (mode or rules). A no-op update must not emit the event.
        var sink = new RecordingAgentSink();
        var store = new PermissionRuleStore();
        var modeState = new PermissionModeState(PermissionMode.Default);

        await RunToolTurnAsync(
            new RecordingTool(),
            Runner(
                [new UserHook("PermissionRequest", "rules-hook")],
                Exec("""{"decision":"allow","hookSpecificOutput":{"updatedPermissions":{"addRules":{"allow":["danger"],"deny":["safe"]},"setMode":"acceptEdits","scope":"session"}}}""")),
            new CountingPermissionPrompt(),
            sink,
            ruleStore: store,
            options: Options(modeState: modeState));

        var evt = Assert.Single(sink.PermissionsUpdated);
        Assert.Equal("rules-hook", evt.HookCommand);
        Assert.Equal("acceptEdits", evt.ModeApplied);
        Assert.Contains("danger", evt.AddedAllow);
        Assert.Contains("safe", evt.AddedDeny);
    }

    // =========================================================================
    // I2 — AddPermissionRules does not write defaultMode
    // =========================================================================

    [Fact]
    public void I2_AddPermissionRules_does_not_write_defaultMode()
    {
        var dir = Directory.CreateDirectory(Path.Combine(this.root, "i2", ".coda")).FullName;
        var file = Path.Combine(dir, "settings.json");

        SettingsWriter.AddPermissionRules(["safe"], [], file);

        var text = File.ReadAllText(file);
        Assert.Contains("safe", text);
        Assert.DoesNotContain("defaultMode", text);
    }

    // =========================================================================
    // Test gaps — fail-closed matrix
    // =========================================================================

    [Fact]
    public async Task FailClosed_exit1_denies()
    {
        var tool = new RecordingTool();
        var prompt = new CountingPermissionPrompt();
        var sink = new RecordingAgentSink();

        var history = await RunToolTurnAsync(
            tool,
            Runner([new UserHook("PermissionRequest", "gate")], Exec(string.Empty, exitCode: 1)),
            prompt,
            sink);

        Assert.False(tool.Executed);
        Assert.Equal(0, prompt.Calls);
        Assert.Contains(sink.PermissionDecided, d => d.Decision == "deny");
        Assert.Contains("denied", ToolResultText(history), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailClosed_exit2_denies()
    {
        var tool = new RecordingTool();
        var prompt = new CountingPermissionPrompt();
        var sink = new RecordingAgentSink();

        var history = await RunToolTurnAsync(
            tool,
            Runner([new UserHook("PermissionRequest", "gate")], Exec("blocked by policy", exitCode: 2)),
            prompt,
            sink);

        Assert.False(tool.Executed);
        Assert.Equal(0, prompt.Calls);
        Assert.Contains(sink.PermissionDecided, d => d.Decision == "deny");
        Assert.Contains("blocked by policy", ToolResultText(history), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailClosed_executor_exception_denies()
    {
        var executor = new ScriptedExecutor((_, _, _) => throw new InvalidOperationException("executor blew up"));
        var tool = new RecordingTool();
        var prompt = new CountingPermissionPrompt();
        var sink = new RecordingAgentSink();

        var history = await RunToolTurnAsync(
            tool,
            Runner([new UserHook("PermissionRequest", "gate")], executor),
            prompt,
            sink);

        Assert.False(tool.Executed);
        Assert.Equal(0, prompt.Calls);
        Assert.Contains(sink.PermissionDecided, d => d.Decision == "deny");
    }

    [Fact]
    public async Task FailClosed_continue_false_denies()
    {
        // continue:false is treated as a denial regardless of the decision field.
        var tool = new RecordingTool();
        var prompt = new CountingPermissionPrompt();
        var sink = new RecordingAgentSink();

        var history = await RunToolTurnAsync(
            tool,
            Runner([new UserHook("PermissionRequest", "gate")], Exec("""{"continue":false,"reason":"policy stop"}""")),
            prompt,
            sink);

        Assert.False(tool.Executed);
        Assert.Equal(0, prompt.Calls);
        Assert.Contains(sink.PermissionDecided, d => d.Decision == "deny");
        Assert.Contains("policy stop", ToolResultText(history), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailClosed_exit0_malformed_json_falls_through_to_prompt_not_allow()
    {
        // exit 0 with malformed JSON → HookOutputParser treats it as plain text (no decision) → falls
        // through to the interactive prompt. This is the "approve what actually runs" guarantee:
        // an incomprehensible hook output must never silently grant access.
        var prompt = new CountingPermissionPrompt(answer: true);
        var tool = new RecordingTool();

        await RunToolTurnAsync(
            tool,
            Runner([new UserHook("PermissionRequest", "gate")], Exec("{bad json!!!")),
            prompt,
            new RecordingAgentSink());

        // Prompt was consulted (not silently allowed).
        Assert.Equal(1, prompt.Calls);
        Assert.True(tool.Executed);
    }

    [Fact]
    public async Task FailClosed_exit0_unrecognised_decision_falls_through_to_prompt_not_allow()
    {
        // An unrecognised decision value maps to null → merged as prompt → interactive prompt fires.
        var prompt = new CountingPermissionPrompt(answer: true);
        var tool = new RecordingTool();

        await RunToolTurnAsync(
            tool,
            Runner([new UserHook("PermissionRequest", "gate")], Exec("""{"decision":"maybe"}""")),
            prompt,
            new RecordingAgentSink());

        Assert.Equal(1, prompt.Calls);
        Assert.True(tool.Executed);
    }

    [Fact]
    public async Task FailClosed_Ctrl_C_propagates_not_deny()
    {
        // A genuine Ctrl+C (caller-side cancellation) must propagate as OperationCanceledException,
        // not be swallowed as a permission denial. This is the most critical fail-closed test.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var executor = new ScriptedExecutor((_, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult((0, string.Empty, string.Empty));
        });

        var runner = Runner([new UserHook("PermissionRequest", "gate")], executor);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunPermissionRequestAsync("danger", "{}", "default", null, cts.Token));
    }

    // =========================================================================
    // scope:"user" disk write
    // =========================================================================

    [Fact]
    public async Task Scope_user_writes_rules_to_user_settings()
    {
        var userSettingsDir = Directory.CreateDirectory(Path.Combine(this.root, "user_home")).FullName;
        var expectedFile = Path.Combine(userSettingsDir, ".coda", "settings.json");

        var options = Options(modeState: null);
        options = options with { WorkingDirectory = Directory.CreateDirectory(Path.Combine(this.root, "project")).FullName };

        Environment.SetEnvironmentVariable("CODA_SETTINGS_DIR", userSettingsDir);
        try
        {
            await RunToolTurnAsync(
                new RecordingTool(),
                Runner(
                    [new UserHook("PermissionRequest", "grant")],
                    Exec("""{"decision":"allow","hookSpecificOutput":{"updatedPermissions":{"addRules":{"allow":["danger"]},"scope":"user"}}}""")),
                new CountingPermissionPrompt(),
                new RecordingAgentSink(),
                ruleStore: new PermissionRuleStore(),
                options: options);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODA_SETTINGS_DIR", null);
        }

        Assert.True(File.Exists(expectedFile), $"Expected settings file at: {expectedFile}");
        Assert.Contains("danger", File.ReadAllText(expectedFile));
    }

    // =========================================================================
    // approve-what-actually-runs guarantee
    // =========================================================================

    [Fact]
    public async Task PermissionRequest_sees_PreToolUse_modified_input()
    {
        // The PermissionRequest hook must receive the PreToolUse-modified input (not the original).
        // This is the "approve what actually runs" guarantee.
        var seenPayloads = new List<string>();
        var executor = new ScriptedExecutor((_, payload, _) =>
        {
            seenPayloads.Add(payload);
            return Task.FromResult((0, string.Empty, string.Empty)); // prompt (no decision)
        });

        var tool = new RecordingTool();
        var hooks = new[]
        {
            new UserHook("PreToolUse", "rewrite"),
            new UserHook("PermissionRequest", "gate"),
        };

        await RunToolTurnAsync(
            tool,
            Runner(
                hooks,
                new ScriptedExecutor((cmd, payload, ct) =>
                {
                    seenPayloads.Add(payload);
                    // The PreToolUse hook rewrites the input.
                    if (cmd == "rewrite")
                    {
                        return Task.FromResult((
                            0,
                            """{"hookSpecificOutput":{"modifiedInput":{"path":"safe.txt"}}}""",
                            string.Empty));
                    }

                    // The PermissionRequest hook just records its payload.
                    return Task.FromResult((0, string.Empty, string.Empty));
                })),
            new CountingPermissionPrompt(),
            new RecordingAgentSink(),
            inputJson: """{"path":"original.txt"}""");

        // Find the PermissionRequest payload.
        var permPayload = seenPayloads.FirstOrDefault(p =>
        {
            try
            {
                using var doc = JsonDocument.Parse(p);
                return doc.RootElement.TryGetProperty("event", out var e)
                    && e.GetString() == "PermissionRequest";
            }
            catch { return false; }
        });

        Assert.NotNull(permPayload);
        Assert.Contains("safe.txt", permPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("original.txt", permPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interactive_prompt_receives_PreToolUse_modified_input()
    {
        // When a hook falls through to the interactive prompt, the prompt must see the modified
        // (post-PreToolUse) input — not the original model-produced input.
        var prompt = new CountingPermissionPrompt(answer: true);
        var tool = new RecordingTool();

        await RunToolTurnAsync(
            tool,
            Runner(
                [new UserHook("PreToolUse", "rewrite")],
                Exec("""{"hookSpecificOutput":{"modifiedInput":{"path":"safe.txt"}}}""")),
            prompt,
            new RecordingAgentSink(),
            inputJson: """{"path":"original.txt"}""");

        Assert.Equal(1, prompt.Calls);
        Assert.NotNull(prompt.LastInput);
        Assert.Contains("safe.txt", prompt.LastInput!, StringComparison.Ordinal);
        Assert.DoesNotContain("original.txt", prompt.LastInput!, StringComparison.Ordinal);
    }

    // =========================================================================
    // M1 — malformed rule is not persisted
    // =========================================================================

    [Fact]
    public void M1_malformed_rule_is_logged_and_skipped_not_persisted()
    {
        var dir = Directory.CreateDirectory(Path.Combine(this.root, "m1", ".coda")).FullName;
        var file = Path.Combine(dir, "settings.json");
        var logger = new CapturingLogger();

        // A rule string that does not round-trip: e.g. extra whitespace, or an embedded control character.
        // After the fix, the round-trip check normalises via PermissionRule.Parse then .ToRuleString(),
        // and any rule that doesn't match the original is skipped.
        SettingsWriter.AddPermissionRules(["valid_tool"], ["  bad rule  "], file, logger);

        var text = File.ReadAllText(file);
        Assert.Contains("valid_tool", text);           // valid rule persisted
        Assert.DoesNotContain("bad rule", text);        // malformed rule NOT persisted
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("bad", StringComparison.OrdinalIgnoreCase));
    }
}
