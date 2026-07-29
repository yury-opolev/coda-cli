using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Tui.Skills;
using LlmClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Tui.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Shared helpers
// ─────────────────────────────────────────────────────────────────────────────

file static class Helpers
{
    public static SkillDefinition SkillDef(
        string name = "my-skill",
        string body = "Do something.",
        string? model = null,
        string? effort = null,
        IReadOnlyList<string>? allowedTools = null,
        IReadOnlyList<string>? disallowedTools = null,
        SkillContextMode contextMode = SkillContextMode.Inline,
        string? agentType = null,
        IReadOnlyList<string>? paths = null,
        string? sourcePath = null) =>
        new(name, "A skill.", body)
        {
            Model = model,
            Effort = effort,
            AllowedTools = allowedTools ?? [],
            DisallowedTools = disallowedTools ?? [],
            ContextMode = contextMode,
            AgentType = agentType,
            Paths = paths ?? [],
            SourcePath = sourcePath,
        };

    public static Task<ToolResult> InvokeAsync(
        SkillTool tool,
        string name,
        ToolContext? context = null)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = name });
        var element = JsonDocument.Parse(json).RootElement;
        return tool.ExecuteAsync(element, context ?? new ToolContext(Directory.GetCurrentDirectory()));
    }

    public static SkillTool MakeTool(
        SkillDefinition skill,
        SkillSessionState? state = null) =>
        new([skill], state ?? new SkillSessionState());

    public static ToolRegistry Registry(params string[] names) =>
        new(names.Select(n => new FakeReadOnlyTool(n)).ToList<ITool>());
}

file sealed class FakeReadOnlyTool(string name) : ITool
{
    public string Name => name;
    public string Description => name;
    public string InputSchemaJson => "{}";
    public bool IsReadOnly => true;
    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ToolResult("ok"));
}

file sealed class FakeWriteTool(string name) : ITool
{
    public string Name => name;
    public string Description => name;
    public string InputSchemaJson => "{}";
    public bool IsReadOnly => false;
    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ToolResult("done"));
}

file sealed class RecordingPermissionPrompt : IPermissionPrompt
{
    public List<string> AskedTools { get; } = [];
    public bool AllowByDefault { get; set; } = false;

    public Task<bool> RequestAsync(ITool tool, string inputJson, CancellationToken cancellationToken = default)
    {
        this.AskedTools.Add(tool.Name);
        return Task.FromResult(this.AllowByDefault);
    }
}

file sealed class RecordingUserQuestion : IUserQuestionPrompt
{
    public record Asked(string Question, IReadOnlyList<string> Options);

    public List<Asked> Questions { get; } = [];
    public string Answer { get; set; } = "No";

    public Task<string> AskAsync(
        string question,
        IReadOnlyList<string> options,
        bool multiSelect,
        CancellationToken cancellationToken = default)
    {
        this.Questions.Add(new Asked(question, options));
        return Task.FromResult(this.Answer);
    }
}

file sealed class RecordingSubagentHost : ISubagentHost
{
    public record Call(string Type, string Prompt, TurnShape? Restriction);

    public List<Call> Calls { get; } = [];
    public string Report { get; set; } = "subagent-report";

    public Task<string> RunSubagentAsync(
        string subagentType,
        string prompt,
        IAgentSink parentSink,
        SteeringInbox steering,
        string taskId,
        int depth,
        CancellationToken cancellationToken = default)
    {
        this.Calls.Add(new Call(subagentType, prompt, null));
        return Task.FromResult(this.Report);
    }

    public Task<string> RunSubagentAsync(
        string subagentType,
        string prompt,
        IAgentSink parentSink,
        SteeringInbox steering,
        string taskId,
        int depth,
        ToolActivityContext? parentActivity,
        TurnShape? parentToolRestriction,
        CancellationToken cancellationToken = default)
    {
        this.Calls.Add(new Call(subagentType, prompt, parentToolRestriction));
        return Task.FromResult(this.Report);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test 1 — allowed-tools pre-approval
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// allowed-tools pre-approves for the invoking turn only — shape delta carries the allowed list,
/// and IsPreApprovedTool returns true for listed tools when the delta is resolved.
/// The next turn (new shape=null) has no restriction.
/// </summary>
public sealed class SkillAllowedToolsTests
{
    [Fact]
    public async Task ExecuteAsync_returns_ShapeDelta_with_PreApprovedTools()
    {
        var skill = Helpers.SkillDef(allowedTools: ["bash", "read_file"]);
        var tool = Helpers.MakeTool(skill);

        var result = await Helpers.InvokeAsync(tool, "my-skill");

        Assert.NotNull(result.ShapeDelta);
        // Skill's allowed-tools must populate PreApprovedTools, not AllowedTools.
        Assert.NotNull(result.ShapeDelta!.PreApprovedTools);
        Assert.Contains("bash", result.ShapeDelta.PreApprovedTools!);
        Assert.Contains("read_file", result.ShapeDelta.PreApprovedTools!);
        // AllowedTools must NOT be set by the skill — it is reserved for hook-imposed allowlists.
        Assert.Null(result.ShapeDelta.AllowedTools);
    }

    [Fact]
    public async Task Resolved_shape_marks_listed_tools_as_PreApproved()
    {
        var skill = Helpers.SkillDef(allowedTools: ["bash"]);
        var tool = Helpers.MakeTool(skill);
        var result = await Helpers.InvokeAsync(tool, "my-skill");

        var merged = TurnShape.Layer(null, result.ShapeDelta);
        var registry = Helpers.Registry("bash", "read_file");
        var resolution = TurnShapeResolver.Resolve("sys", "model", null, registry, merged);

        Assert.True(resolution.IsPreApprovedTool("bash"));
        Assert.False(resolution.IsPreApprovedTool("read_file")); // not in allowed list
    }

    [Fact]
    public async Task Next_turn_with_null_shape_has_no_restriction()
    {
        // The delta only applies for the current RunAsync call (the invoking turn).
        // A subsequent RunAsync with shape=null must be unrestricted.
        var skill = Helpers.SkillDef(allowedTools: ["bash"]);
        var tool = Helpers.MakeTool(skill);
        await Helpers.InvokeAsync(tool, "my-skill"); // fire the skill once

        // Start a fresh resolution with no shape (next turn).
        var registry = Helpers.Registry("bash", "read_file");
        var resolution = TurnShapeResolver.Resolve("sys", "model", null, registry, shape: null);

        Assert.False(resolution.IsPreApprovedTool("bash"));
        Assert.False(resolution.IsPreApprovedTool("read_file"));
    }

    [Fact]
    public async Task No_AllowedTools_means_no_delta_AllowedTools()
    {
        var skill = Helpers.SkillDef(); // no allowed-tools
        var tool = Helpers.MakeTool(skill);
        var result = await Helpers.InvokeAsync(tool, "my-skill");

        // Either no delta or delta with null AllowedTools.
        var allowed = result.ShapeDelta?.AllowedTools;
        Assert.Null(allowed);
    }

    /// <summary>
    /// C1 regression: skill's allowed-tools must pre-approve only, not restrict other tools.
    /// Before the fix this test fails because allowed-tools maps to AllowedTools which strips
    /// every unlisted tool from the turn — exactly the C1 bug.
    /// </summary>
    [Fact]
    public void SkillAllowedTools_preapproves_without_restricting_other_tools()
    {
        var skill = Helpers.SkillDef(allowedTools: ["read_file"]);
        var delta = SkillTurnShapeComposer.BuildSkillDelta(skill);
        var merged = TurnShape.Layer(null, delta);
        var registry = Helpers.Registry("read_file", "edit_file", "bash");
        var resolution = TurnShapeResolver.Resolve("sys", "m", null, registry, merged);

        // C1: edit_file and bash must remain allowed — allowed-tools is pre-approval, not restriction.
        Assert.True(resolution.IsToolAllowed("edit_file"),
            "edit_file must remain allowed; skill allowed-tools only pre-approves");
        Assert.True(resolution.IsToolAllowed("bash"),
            "bash must remain allowed; skill allowed-tools only pre-approves");
        // read_file must be pre-approved (skip permission prompt).
        Assert.True(resolution.IsPreApprovedTool("read_file"),
            "read_file must be pre-approved");
        // edit_file must NOT be pre-approved (not in the skill's allowed list).
        Assert.False(resolution.IsPreApprovedTool("edit_file"),
            "edit_file must not be pre-approved");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test 2 — disallowed-tools removes tools for that turn
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// disallowed-tools are added to DeniedTools in the skill's shape delta. The merged delta
/// causes those tools to be absent from the resolution's tool definitions.
/// </summary>
public sealed class SkillDisallowedToolsTests
{
    [Fact]
    public async Task ExecuteAsync_returns_ShapeDelta_with_DeniedTools()
    {
        var skill = Helpers.SkillDef(disallowedTools: ["run_command"]);
        var tool = Helpers.MakeTool(skill);
        var result = await Helpers.InvokeAsync(tool, "my-skill");

        Assert.NotNull(result.ShapeDelta?.DeniedTools);
        Assert.Contains("run_command", result.ShapeDelta!.DeniedTools!);
    }

    [Fact]
    public async Task Disallowed_tool_absent_from_resolved_definitions()
    {
        var skill = Helpers.SkillDef(disallowedTools: ["run_command"]);
        var tool = Helpers.MakeTool(skill);
        var result = await Helpers.InvokeAsync(tool, "my-skill");

        var merged = TurnShape.Layer(null, result.ShapeDelta);
        var registry = Helpers.Registry("bash", "run_command", "read_file");
        var resolution = TurnShapeResolver.Resolve("sys", "model", null, registry, merged);

        Assert.DoesNotContain(resolution.ToolDefinitions, d => d.Name == "run_command");
        // Other tools are still allowed.
        Assert.Contains(resolution.ToolDefinitions, d => d.Name == "bash");
        Assert.Contains(resolution.ToolDefinitions, d => d.Name == "read_file");
    }

    [Fact]
    public async Task Disallowed_is_not_pre_approved()
    {
        var skill = Helpers.SkillDef(disallowedTools: ["run_command"]);
        var tool = Helpers.MakeTool(skill);
        var result = await Helpers.InvokeAsync(tool, "my-skill");

        var merged = TurnShape.Layer(null, result.ShapeDelta);
        var registry = Helpers.Registry("run_command");
        var resolution = TurnShapeResolver.Resolve("sys", "model", null, registry, merged);

        Assert.False(resolution.IsToolAllowed("run_command"));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test 3 — skill cannot widen a hook's restriction
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A UserPromptSubmit hook-denied tool stays denied even when a skill claims to allow it.
/// Denial beats allowance at every composition step.
/// </summary>
public sealed class SkillCannotWidenHookRestrictionTests
{
    [Fact]
    public void Hook_denied_tool_stays_denied_after_skill_allows_it()
    {
        // Hook shape: DeniedTools = [run_command]
        var hookShape = new TurnShape { DeniedTools = ["run_command"] };

        // Skill delta: AllowedTools = [run_command]
        var skillDelta = new TurnShape { AllowedTools = ["run_command"] };

        // Layer: merges hook shape with skill delta
        var merged = TurnShape.Layer(hookShape, skillDelta);

        var registry = Helpers.Registry("bash", "run_command");
        var resolution = TurnShapeResolver.Resolve("sys", "model", null, registry, merged);

        // run_command must remain denied (DeniedTools beats AllowedTools).
        Assert.False(resolution.IsToolAllowed("run_command"),
            "Hook-denied run_command must stay denied when skill claims to allow it.");
        Assert.DoesNotContain(resolution.ToolDefinitions, d => d.Name == "run_command");
    }

    [Fact]
    public void Hook_allowlist_intersects_skill_allowlist_no_widening()
    {
        // Hook has a strict allowlist (no run_command).
        var hookShape = new TurnShape { AllowedTools = ["bash", "read_file"] };

        // Skill uses PreApprovedTools (not AllowedTools) — it cannot widen the hook's allowlist.
        var skillDelta = new TurnShape { PreApprovedTools = ["bash", "run_command"] };

        // Layer: AllowedTools from hook is preserved; PreApprovedTools is unioned.
        var merged = TurnShape.Layer(hookShape, skillDelta);

        var registry = Helpers.Registry("bash", "read_file", "run_command");
        var resolution = TurnShapeResolver.Resolve("sys", "model", null, registry, merged);

        Assert.True(resolution.IsToolAllowed("bash"));
        Assert.False(resolution.IsToolAllowed("run_command"),
            "Skill cannot widen hook AllowedTools to include run_command.");
        // read_file is in the hook's AllowedTools and NOT excluded by the skill's PreApprovedTools,
        // so it remains allowed.
        Assert.True(resolution.IsToolAllowed("read_file"),
            "read_file is in hook's AllowedTools and must remain allowed.");
    }

    [Fact]
    public void DeniedTools_are_always_unioned_denial_is_monotonic()
    {
        var hookShape = new TurnShape { DeniedTools = ["run_command"] };
        var skillDelta = new TurnShape { DeniedTools = ["bash"] };

        var merged = TurnShape.Layer(hookShape, skillDelta);

        Assert.NotNull(merged?.DeniedTools);
        Assert.Contains("run_command", merged!.DeniedTools!);
        Assert.Contains("bash", merged.DeniedTools!);
    }

    [Fact]
    public void Skill_delta_built_from_BuildSkillDelta_uses_PreApprovedTools()
    {
        var skill = Helpers.SkillDef(allowedTools: ["bash"], disallowedTools: ["run_command"]);
        var delta = SkillTurnShapeComposer.BuildSkillDelta(skill);

        Assert.NotNull(delta);
        // allowed-tools maps to PreApprovedTools, not AllowedTools.
        Assert.Contains("bash", delta!.PreApprovedTools!);
        Assert.Null(delta.AllowedTools);
        Assert.Contains("run_command", delta.DeniedTools!);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test 4 — model/effort override; inherit is a no-op; skill wins over hook
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A skill's model/effort override applies for the rest of the turn. "inherit" is a no-op.
/// When a hook set a model and then a skill sets a different one, the skill wins.
/// </summary>
public sealed class SkillModelEffortOverrideTests
{
    [Fact]
    public async Task Skill_model_and_effort_appear_in_ShapeDelta()
    {
        var skill = Helpers.SkillDef(model: "claude-opus-4.8", effort: "high");
        var tool = Helpers.MakeTool(skill);
        var result = await Helpers.InvokeAsync(tool, "my-skill");

        Assert.Equal("claude-opus-4.8", result.ShapeDelta?.Model);
        Assert.Equal("high", result.ShapeDelta?.Effort);
    }

    [Fact]
    public void Inherit_value_is_normalised_to_null_by_parser()
    {
        var content = """
            ---
            name: test
            model: inherit
            effort: inherit
            ---
            Body.
            """;
        var fm = SkillFrontmatterParser.Parse(content);
        Assert.Null(fm.Model);
        Assert.Null(fm.Effort);
    }

    [Fact]
    public void Skill_with_null_model_produces_no_delta_model_field()
    {
        var skill = Helpers.SkillDef(); // no model/effort
        var delta = SkillTurnShapeComposer.BuildSkillDelta(skill);
        Assert.Null(delta?.Model);
        Assert.Null(delta?.Effort);
    }

    [Fact]
    public void Skill_model_wins_over_hook_model_last_write_wins()
    {
        // Hook set model to hook-model.
        var hookShape = new TurnShape { Model = "hook-model" };
        // Skill wants skill-model.
        var skillDelta = new TurnShape { Model = "skill-model" };

        var merged = TurnShape.Layer(hookShape, skillDelta);

        // Skill wins (more specific, later decision).
        Assert.Equal("skill-model", merged?.Model);
    }

    [Fact]
    public void Skill_effort_wins_over_hook_effort()
    {
        var hookShape = new TurnShape { Effort = "low" };
        var skillDelta = new TurnShape { Effort = "high" };

        var merged = TurnShape.Layer(hookShape, skillDelta);

        Assert.Equal("high", merged?.Effort);
    }

    [Fact]
    public void Resolved_model_reflects_skill_override()
    {
        var hookShape = new TurnShape { Model = "hook-model" };
        var skillDelta = new TurnShape { Model = "skill-model" };
        var merged = TurnShape.Layer(hookShape, skillDelta);

        var resolution = TurnShapeResolver.Resolve("sys", "session-model", null, Helpers.Registry("t"), merged);

        Assert.Equal("skill-model", resolution.Model);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test 5 — context: fork runs in a subagent; max-depth degrades to inline
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// When context: fork is declared, the skill body runs in a subagent and only its report returns.
/// At MaxSubagentDepth the skill degrades to inline with a logged note.
/// </summary>
public sealed class SkillContextForkTests
{
    [Fact]
    public async Task Fork_calls_subagent_and_returns_its_report()
    {
        var skill = Helpers.SkillDef(contextMode: SkillContextMode.Fork, body: "Do fork task.");
        var tool = Helpers.MakeTool(skill);

        var subagent = new RecordingSubagentHost { Report = "fork-report" };
        var mgr = new TaskManager(sessionId: "s", logRoot: null);
        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            Subagents = subagent,
            Tasks = mgr,
            Sink = new NullAgentSink(),
            CurrentDepth = 0,
        };

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"name":"my-skill"}""").RootElement,
            context);

        Assert.False(result.IsError);
        Assert.Equal("fork-report", result.Content);
        Assert.Null(result.ShapeDelta); // fork does not affect parent turn's shape
        Assert.Single(subagent.Calls);
    }

    [Fact]
    public async Task Fork_uses_skill_agent_type()
    {
        var skill = Helpers.SkillDef(contextMode: SkillContextMode.Fork, agentType: "custom-agent");
        var tool = Helpers.MakeTool(skill);

        var subagent = new RecordingSubagentHost();
        var mgr = new TaskManager(sessionId: "s", logRoot: null);
        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            Subagents = subagent,
            Tasks = mgr,
            Sink = new NullAgentSink(),
            CurrentDepth = 0,
        };

        await tool.ExecuteAsync(JsonDocument.Parse("""{"name":"my-skill"}""").RootElement, context);

        Assert.Equal("custom-agent", subagent.Calls[0].Type);
    }

    [Fact]
    public async Task Fork_defaults_to_general_purpose_agent()
    {
        var skill = Helpers.SkillDef(contextMode: SkillContextMode.Fork); // no agentType
        var tool = Helpers.MakeTool(skill);

        var subagent = new RecordingSubagentHost();
        var mgr = new TaskManager(sessionId: "s", logRoot: null);
        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            Subagents = subagent,
            Tasks = mgr,
            Sink = new NullAgentSink(),
            CurrentDepth = 0,
        };

        await tool.ExecuteAsync(JsonDocument.Parse("""{"name":"my-skill"}""").RootElement, context);

        Assert.Equal("general-purpose", subagent.Calls[0].Type);
    }

    [Fact]
    public async Task Fork_degrades_to_inline_at_max_depth()
    {
        var skill = Helpers.SkillDef(contextMode: SkillContextMode.Fork, body: "Forked body.");
        var tool = Helpers.MakeTool(skill);

        var subagent = new RecordingSubagentHost();
        var mgr = new TaskManager(sessionId: "s", logRoot: null);
        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            Subagents = subagent,
            Tasks = mgr,
            Sink = new NullAgentSink(),
            CurrentDepth = TaskManager.MaxSubagentDepth, // at the limit
        };

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"name":"my-skill"}""").RootElement,
            context);

        // No subagent was launched.
        Assert.Empty(subagent.Calls);
        // Result contains the inline body and the degradation note.
        Assert.Contains("Forked body.", result.Content);
        Assert.Contains("inline", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fork_degrades_to_inline_when_tasks_null()
    {
        var skill = Helpers.SkillDef(contextMode: SkillContextMode.Fork, body: "body");
        var tool = Helpers.MakeTool(skill);

        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            CurrentDepth = 0,
            Tasks = null, // no TaskManager
            Sink = new NullAgentSink(),
        };

        // Must not throw — degrade to inline silently.
        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"name":"my-skill"}""").RootElement,
            context);

        Assert.Contains("body", result.Content);
    }

    /// <summary>
    /// L1 regression: the Tasks=null degrade path must emit the same explanatory note as the
    /// max-depth path so consumers know the skill inlined. Currently the degrade is silent.
    /// </summary>
    [Fact]
    public async Task Fork_degrades_to_inline_when_tasks_null_emits_degradation_note()
    {
        var skill = Helpers.SkillDef(contextMode: SkillContextMode.Fork, body: "fork-body");
        var tool = Helpers.MakeTool(skill);
        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            CurrentDepth = 0,
            Tasks = null,
            Sink = new NullAgentSink(),
        };

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"name":"my-skill"}""").RootElement, context);

        Assert.Contains("fork-body", result.Content);
        // L1: a note must explain the degrade (same as max-depth path).
        Assert.Contains("inline", result.Content, StringComparison.OrdinalIgnoreCase);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test 6 — forked skill inherits turn's tool restriction
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A forked skill cannot escape its parent turn's tool restriction.
/// The subagent receives the monotonically-stricter parentRestriction.
/// </summary>
public sealed class SkillForkInheritsRestrictionTests
{
    [Fact]
    public async Task Fork_passes_parent_restriction_to_subagent()
    {
        var parentRestriction = new TurnShape { DeniedTools = ["run_command"] };
        var skill = Helpers.SkillDef(contextMode: SkillContextMode.Fork);
        var tool = Helpers.MakeTool(skill);

        var subagent = new RecordingSubagentHost();
        var mgr = new TaskManager(sessionId: "s", logRoot: null);
        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            Subagents = subagent,
            Tasks = mgr,
            Sink = new NullAgentSink(),
            CurrentDepth = 0,
            ParentToolRestriction = parentRestriction,
        };

        await tool.ExecuteAsync(JsonDocument.Parse("""{"name":"my-skill"}""").RootElement, context);

        var restriction = subagent.Calls[0].Restriction;
        Assert.NotNull(restriction);
        Assert.NotNull(restriction!.DeniedTools);
        Assert.Contains("run_command", restriction.DeniedTools!);
    }

    [Fact]
    public async Task Fork_layers_skill_disallowed_onto_parent_restriction()
    {
        var parentRestriction = new TurnShape { DeniedTools = ["run_command"] };
        var skill = Helpers.SkillDef(
            contextMode: SkillContextMode.Fork,
            disallowedTools: ["bash"]); // skill also denies bash
        var tool = Helpers.MakeTool(skill);

        var subagent = new RecordingSubagentHost();
        var mgr = new TaskManager(sessionId: "s", logRoot: null);
        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            Subagents = subagent,
            Tasks = mgr,
            Sink = new NullAgentSink(),
            CurrentDepth = 0,
            ParentToolRestriction = parentRestriction,
        };

        await tool.ExecuteAsync(JsonDocument.Parse("""{"name":"my-skill"}""").RootElement, context);

        var restriction = subagent.Calls[0].Restriction;
        Assert.NotNull(restriction?.DeniedTools);
        // Both the parent's denial and the skill's denial are present.
        Assert.Contains("run_command", restriction!.DeniedTools!);
        Assert.Contains("bash", restriction.DeniedTools!);
    }

    [Fact]
    public void TurnShape_Layer_preserves_parent_denial_when_skill_allows_denied_tool()
    {
        var parentRestriction = new TurnShape { DeniedTools = ["run_command"] };
        var skillDelta = new TurnShape { AllowedTools = ["run_command"] };

        var merged = TurnShape.Layer(parentRestriction, skillDelta);

        var registry = Helpers.Registry("run_command", "bash");
        var resolution = TurnShapeResolver.Resolve("sys", "m", null, registry, merged);

        // The skill's attempt to allow run_command is defeated by the parent's DeniedTools.
        Assert.False(resolution.IsToolAllowed("run_command"),
            "Forked skill cannot escape parent-denied run_command.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test 7 — directory consent
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Directory consent prompts name the directory, approving once avoids re-prompt,
/// denying still runs the skill without access, and unattended contexts deny automatically.
/// </summary>
public sealed class SkillDirectoryConsentTests
{
    private static string SourcePath(string dir = "myskill") =>
        Path.Combine(Path.GetTempPath(), dir, "SKILL.md");

    [Fact]
    public async Task Consent_prompt_names_the_directory()
    {
        var sourcePath = SourcePath();
        var skill = Helpers.SkillDef(sourcePath: sourcePath, body: "Skill body.");
        var tool = Helpers.MakeTool(skill);
        var question = new RecordingUserQuestion { Answer = "Yes" };
        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            UserQuestion = question,
        };

        await tool.ExecuteAsync(JsonDocument.Parse("""{"name":"my-skill"}""").RootElement, context);

        Assert.Single(question.Questions);
        // The question must name the directory (not just the file).
        var asked = question.Questions[0].Question;
        var expectedDir = Path.GetFullPath(Path.GetDirectoryName(sourcePath)!);
        Assert.Contains(expectedDir, asked);
    }

    [Fact]
    public async Task Approving_once_does_not_re_prompt()
    {
        var sourcePath = SourcePath();
        var state = new SkillSessionState();
        var skill = Helpers.SkillDef(sourcePath: sourcePath, body: "Body.");
        var tool = new SkillTool([skill], state);
        var question = new RecordingUserQuestion { Answer = "Yes" };
        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            UserQuestion = question,
        };

        var input = JsonDocument.Parse("""{"name":"my-skill"}""").RootElement;
        await tool.ExecuteAsync(input, context);
        await tool.ExecuteAsync(input, context); // second invocation

        // Prompt shown only once.
        Assert.Single(question.Questions);
        Assert.True(state.HasDirectoryConsent("my-skill"));
    }

    [Fact]
    public async Task Denying_still_runs_skill_with_denial_note_in_body()
    {
        var sourcePath = SourcePath();
        var skill = Helpers.SkillDef(sourcePath: sourcePath, body: "Skill body.");
        var tool = Helpers.MakeTool(skill);
        var question = new RecordingUserQuestion { Answer = "No" };
        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            UserQuestion = question,
        };

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"name":"my-skill"}""").RootElement, context);

        Assert.False(result.IsError);
        // Body is still returned.
        Assert.Contains("Skill body.", result.Content);
        // Denial is explained.
        Assert.Contains("denied", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unattended_context_denies_without_prompt()
    {
        var sourcePath = SourcePath();
        var skill = Helpers.SkillDef(sourcePath: sourcePath, body: "Body.");
        var tool = Helpers.MakeTool(skill);
        // No UserQuestion → unattended.
        var context = new ToolContext(Directory.GetCurrentDirectory());

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"name":"my-skill"}""").RootElement, context);

        Assert.False(result.IsError);
        // Body is returned but denial note is appended.
        Assert.Contains("Body.", result.Content);
        // The note explains access was not granted (unattended).
        Assert.Contains("not granted", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Skill_without_SourcePath_runs_without_consent_prompt()
    {
        var skill = Helpers.SkillDef(sourcePath: null, body: "Body."); // no SourcePath
        var tool = Helpers.MakeTool(skill);
        var question = new RecordingUserQuestion();
        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            UserQuestion = question,
        };

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"name":"my-skill"}""").RootElement, context);

        Assert.Empty(question.Questions);
        Assert.Contains("Body.", result.Content);
    }

    /// <summary>
    /// I2 regression: granting consent must record the canonical directory so filesystem tools
    /// can check it. Before the fix GrantDirectoryConsent only recorded the skill name; the
    /// canonical dir was computed but never persisted.
    /// </summary>
    [Fact]
    public void SkillSessionState_GrantDirectoryConsent_persists_canonical_directory()
    {
        var state = new SkillSessionState();
        var canonicalDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "some-skill-dir"));

        // I2 fix adds a (skillName, canonicalDir) overload.
        state.GrantDirectoryConsent("my-skill", canonicalDir);

        Assert.True(state.HasDirectoryConsent("my-skill"));
        var dirs = state.GetGrantedDirectories();
        Assert.Contains(canonicalDir, dirs);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test 8 — traversal in SourcePath is blocked
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A skill whose SourcePath contains ".." cannot widen the granted path.
/// The consent is denied unconditionally when traversal is detected.
/// </summary>
public sealed class SkillSourcePathTraversalTests
{
    [Fact]
    public async Task Traversal_in_SourcePath_is_denied_without_prompt()
    {
        var traversalPath = Path.Combine("legit", "..", "etc", "SKILL.md");
        var skill = Helpers.SkillDef(sourcePath: traversalPath, body: "Body.");
        var tool = Helpers.MakeTool(skill);
        var question = new RecordingUserQuestion { Answer = "Yes" };
        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            UserQuestion = question,
        };

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"name":"my-skill"}""").RootElement, context);

        // No prompt shown — traversal detected early.
        Assert.Empty(question.Questions);
        // Skill still runs (denial note in body).
        Assert.False(result.IsError);
        Assert.Contains("Body.", result.Content);
        // The traversal denial note is present.
        Assert.Contains("traversal", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Normal_path_is_not_mistaken_for_traversal()
    {
        var normalPath = Path.Combine(Path.GetTempPath(), "skills", "myskill", "SKILL.md");
        var skill = Helpers.SkillDef(sourcePath: normalPath, body: "Normal.");
        var tool = Helpers.MakeTool(skill);
        var question = new RecordingUserQuestion { Answer = "No" };
        var context = new ToolContext(Directory.GetCurrentDirectory())
        {
            UserQuestion = question,
        };

        await tool.ExecuteAsync(JsonDocument.Parse("""{"name":"my-skill"}""").RootElement, context);

        // Normal path triggers a real consent prompt.
        Assert.Single(question.Questions);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test 9 — paths glob filters model invocation; /skill still runs explicitly
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Skills with non-matching `paths` globs are hidden from the model-facing skill tool but
/// remain runnable when invoked explicitly (user /skill command).
/// </summary>
public sealed class SkillPathsGlobFilterTests
{
    [Fact]
    public void SkillPathMatcher_empty_patterns_always_matches()
    {
        Assert.True(SkillPathMatcher.IsMatch([], "/any/path"));
        Assert.True(SkillPathMatcher.IsMatch([], "C:/project"));
    }

    [Fact]
    public void SkillPathMatcher_literal_pattern_matches_path()
    {
        Assert.True(SkillPathMatcher.IsMatch(["/home/user/rust-project"], "/home/user/rust-project"));
    }

    [Fact]
    public void SkillPathMatcher_star_glob_matches_any_segment()
    {
        Assert.True(SkillPathMatcher.IsMatch(["/home/*/project"], "/home/user/project"));
        Assert.False(SkillPathMatcher.IsMatch(["/home/*/project"], "/home/user/other/project"));
    }

    [Fact]
    public void SkillPathMatcher_doublestar_glob_matches_across_segments()
    {
        Assert.True(SkillPathMatcher.IsMatch(["**/rust-project"], "/deep/nested/rust-project"));
        Assert.True(SkillPathMatcher.IsMatch(["**/rust-project"], "rust-project"));
    }

    [Fact]
    public void SkillPathMatcher_non_matching_pattern_returns_false()
    {
        Assert.False(SkillPathMatcher.IsMatch(["**/rust-project"], "/home/node-project"));
    }

    [Fact]
    public void SkillPathMatcher_normalises_backslash_paths()
    {
        // Windows paths should still match forward-slash patterns.
        Assert.True(SkillPathMatcher.IsMatch(["**/myproject"], @"C:\repos\myproject"));
    }

    /// <summary>
    /// M1 regression: GlobToRegex must not produce a pattern that backtracks catastrophically on
    /// adversarial input. The reviewer's pattern (**a × 20 + **Z) against a 60-char 'a' string
    /// hung for >8 s before the fix (ReDoS). After the fix it must complete well within 500 ms.
    /// </summary>
    [Fact]
    public void SkillPathMatcher_adversarial_pattern_does_not_hang()
    {
        // ("**a" × 20) + "**Z" is the reviewer's demonstrated ReDoS pattern.
        var pattern = string.Concat(Enumerable.Repeat("**a", 20)) + "**Z";
        var adversarialPath = new string('a', 60);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = SkillPathMatcher.IsMatch([pattern], adversarialPath);
        sw.Stop();

        Assert.False(result, "adversarial path should not match the pattern");
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"matching took {sw.ElapsedMilliseconds} ms; expected < 500 ms (ReDoS)");
    }

    [Fact]
    public void CreateOrNull_hides_non_matching_paths_from_model()
    {
        var rustSkill = Helpers.SkillDef("rust-skill", paths: ["**/rust-project"]);
        var anySkill = Helpers.SkillDef("any-skill", paths: []); // always visible
        var state = new SkillSessionState();

        var toolInNodeProject = SkillTool.CreateOrNull(
            [rustSkill, anySkill],
            state,
            workingDirectory: "/home/user/node-project");

        // Only any-skill should be visible in a Node project.
        Assert.NotNull(toolInNodeProject);
        var names = toolInNodeProject!.InputSchemaJson;
        Assert.DoesNotContain("rust-skill", names);
        Assert.Contains("any-skill", names);
    }

    [Fact]
    public void CreateOrNull_shows_matching_skill_in_matching_workspace()
    {
        var rustSkill = Helpers.SkillDef("rust-skill", paths: ["**/rust-project"]);
        var state = new SkillSessionState();

        var tool = SkillTool.CreateOrNull(
            [rustSkill],
            state,
            workingDirectory: "/home/user/rust-project");

        Assert.NotNull(tool);
        Assert.Contains("rust-skill", tool!.InputSchemaJson);
    }

    [Fact]
    public async Task Skill_with_paths_still_runs_when_invoked_directly_regardless_of_workspace()
    {
        // When a user types /skill rust-skill in a Node project, the skill still runs.
        // Simulate: create the tool directly with the skill (bypassing paths filter).
        var rustSkill = Helpers.SkillDef("rust-skill", paths: ["**/rust-project"], body: "Rust body.");
        var tool = new SkillTool([rustSkill], new SkillSessionState());

        // Direct invocation (model invoking via tool) always succeeds regardless of workspace.
        var result = await Helpers.InvokeAsync(tool, "rust-skill");

        Assert.False(result.IsError);
        Assert.Contains("Rust body.", result.Content);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test 10 — skill with none of Phase-2 fields behaves exactly as before
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A Phase-1 skill with no Phase-2 fields has null ShapeDelta and behaves identically to
/// the pre-Phase-2 implementation.
/// </summary>
public sealed class SkillBaselineBehaviourTests
{
    [Fact]
    public async Task Phase1_skill_has_null_ShapeDelta()
    {
        var skill = new SkillDefinition("my-skill", "A skill.", "Do something.");
        var tool = new SkillTool([skill], new SkillSessionState());

        var result = await Helpers.InvokeAsync(tool, "my-skill");

        Assert.Null(result.ShapeDelta);
        Assert.False(result.IsError);
        Assert.Contains("Do something.", result.Content);
    }

    [Fact]
    public async Task Phase1_skill_re_invocation_returns_already_loaded_note()
    {
        var skill = new SkillDefinition("my-skill", "A skill.", "Do something.");
        var state = new SkillSessionState();
        var tool = new SkillTool([skill], state);

        var result1 = await Helpers.InvokeAsync(tool, "my-skill");
        var result2 = await Helpers.InvokeAsync(tool, "my-skill");

        Assert.Contains("Do something.", result1.Content);
        Assert.Contains("already loaded", result2.Content);
    }

    [Fact]
    public void BuildSkillDelta_returns_null_for_plain_skill()
    {
        var skill = new SkillDefinition("my-skill", "A skill.", "Body.");
        var delta = SkillTurnShapeComposer.BuildSkillDelta(skill);
        Assert.Null(delta);
    }

    [Fact]
    public void SkillFrontmatterParser_parses_phase2_fields()
    {
        var content = """
            ---
            name: test-skill
            model: claude-opus-4.8
            effort: high
            context: fork
            agent: custom-agent
            allowed-tools:
              - bash
              - read_file
            disallowed-tools:
              - run_command
            paths:
              - "**/rust-project"
            ---
            Body here.
            """;
        var fm = SkillFrontmatterParser.Parse(content);

        Assert.Equal("claude-opus-4.8", fm.Model);
        Assert.Equal("high", fm.Effort);
        Assert.Equal(SkillContextMode.Fork, fm.ContextMode);
        Assert.Equal("custom-agent", fm.Agent);
        Assert.Contains("bash", fm.AllowedTools);
        Assert.Contains("read_file", fm.AllowedTools);
        Assert.Contains("run_command", fm.DisallowedTools);
        Assert.Contains("**/rust-project", fm.Paths);
        Assert.Equal("Body here.", fm.Body);
    }

    [Fact]
    public void SkillLoader_ParseSkillFile_maps_phase2_fields()
    {
        var content = """
            ---
            name: test
            model: gpt-4o
            effort: medium
            allowed-tools: [bash]
            context: fork
            ---
            Do fork.
            """;
        var def = SkillLoader.ParseSkillFile(content, "test");

        Assert.Equal("gpt-4o", def.Model);
        Assert.Equal("medium", def.Effort);
        Assert.Equal(SkillContextMode.Fork, def.ContextMode);
        Assert.Contains("bash", def.AllowedTools);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TurnShape.Layer unit tests
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Unit tests for <see cref="TurnShape.Layer"/> composition rules.</summary>
public sealed class SkillTurnShapeLayerTests
{
    [Fact]
    public void Layer_null_existing_returns_delta()
    {
        var delta = new TurnShape { Model = "x" };
        var result = TurnShape.Layer(null, delta);
        Assert.Equal("x", result?.Model);
    }

    [Fact]
    public void Layer_null_delta_returns_existing()
    {
        var existing = new TurnShape { Model = "x" };
        var result = TurnShape.Layer(existing, null);
        Assert.Equal("x", result?.Model);
    }

    [Fact]
    public void Layer_both_null_returns_null()
    {
        Assert.Null(TurnShape.Layer(null, null));
    }

    [Fact]
    public void Layer_both_empty_returns_null()
    {
        Assert.Null(TurnShape.Layer(new TurnShape(), new TurnShape()));
    }

    [Fact]
    public void Layer_AllowedTools_intersects_when_both_have_lists()
    {
        var existing = new TurnShape { AllowedTools = ["bash", "read_file"] };
        var delta = new TurnShape { AllowedTools = ["bash", "run_command"] };

        var result = TurnShape.Layer(existing, delta);

        Assert.NotNull(result?.AllowedTools);
        Assert.Contains("bash", result!.AllowedTools!);
        Assert.DoesNotContain("read_file", result.AllowedTools!);
        Assert.DoesNotContain("run_command", result.AllowedTools!);
    }

    [Fact]
    public void Layer_AllowedTools_uses_delta_when_existing_is_null()
    {
        var existing = new TurnShape { Model = "m" }; // no AllowedTools
        var delta = new TurnShape { AllowedTools = ["bash"] };

        var result = TurnShape.Layer(existing, delta);

        Assert.NotNull(result?.AllowedTools);
        Assert.Contains("bash", result!.AllowedTools!);
    }

    [Fact]
    public void Layer_AllowedTools_keeps_existing_when_delta_has_none()
    {
        var existing = new TurnShape { AllowedTools = ["bash"] };
        var delta = new TurnShape { Model = "new-model" }; // no AllowedTools

        var result = TurnShape.Layer(existing, delta);

        Assert.NotNull(result?.AllowedTools);
        Assert.Contains("bash", result!.AllowedTools!);
    }

    [Fact]
    public void Layer_DeniedTools_unions_both_lists()
    {
        var existing = new TurnShape { DeniedTools = ["run_command"] };
        var delta = new TurnShape { DeniedTools = ["bash"] };

        var result = TurnShape.Layer(existing, delta);

        Assert.NotNull(result?.DeniedTools);
        Assert.Contains("run_command", result!.DeniedTools!);
        Assert.Contains("bash", result.DeniedTools!);
    }

    [Fact]
    public void Layer_NonSkill_fields_existing_wins()
    {
        var existing = new TurnShape { SystemPrompt = "hook-prompt", ToolChoice = "auto" };
        var delta = new TurnShape { Model = "skill-model" };

        var result = TurnShape.Layer(existing, delta);

        Assert.Equal("hook-prompt", result?.SystemPrompt);
        Assert.Equal("auto", result?.ToolChoice);
        Assert.Equal("skill-model", result?.Model);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null sink stub
// ─────────────────────────────────────────────────────────────────────────────

file sealed class NullAgentSink : IAgentSink
{
    public void OnAssistantText(string delta) { }
    public void OnAssistantTextComplete() { }
    public void OnToolCall(string toolName, string inputPreview) { }
    public void OnToolResult(string toolName, ToolResult result) { }
    public void OnError(string message) { }
    public void OnResponseRewritten(string hookCommand, string original, string display, string? modified) { }
}
