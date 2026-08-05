using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;
using Coda.Tui.Skills;

namespace Engine.Tests;

/// <summary>
/// Characterisation tests (locks) for Task 8: two existing propagation mechanisms must remain
/// UNCHANGED by the <c>agent.tools</c> feature. These tests capture current behaviour — they
/// must PASS against the codebase — so that any future change to these paths requires deliberate
/// re-examination rather than silent breakage.
///
/// <list type="number">
///   <item>
///     A skill's <c>disallowed-tools</c> DOES still narrow subagents: <see cref="SkillTurnShapeComposer"/>
///     sets <see cref="TurnShape.DeniedTools"/>, <see cref="TurnShapeResolver"/> records a
///     <c>DeniedOnlyInput</c>, and <see cref="TurnShapeResolution.ToToolRestrictionShape"/> forwards
///     a deny-only shape to children (not an allow-list intersection).
///   </item>
///   <item>
///     A hook setting <c>allowedTools</c> DOES still propagate: the resolved
///     <see cref="TurnShape.AllowedTools"/> becomes <see cref="TurnShape.AllowedTools"/> in the
///     parent restriction handed to the child, which intersects the child's own registry.
///   </item>
/// </list>
/// </summary>
public sealed class AgentToolsPropagationCharacterizationTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private sealed class StubTool(string name) : ITool
    {
        public string Name => name;
        public string Description => name;
        public string InputSchemaJson => "{}";
        public bool IsReadOnly => true;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult("ok"));
    }

    private static ToolRegistry Registry(params string[] names) =>
        new(names.Select(n => (ITool)new StubTool(n)));

    private static TurnShapeResolution Resolve(ToolRegistry tools, TurnShape? shape) =>
        TurnShapeResolver.Resolve("sys", "model", sessionEffort: null, tools, shape);

    // ------------------------------------------------------------------
    // Lock 1 — Skill's DisallowedTools still propagates as deny-only to subagents
    // ------------------------------------------------------------------

    /// <summary>
    /// <see cref="SkillTurnShapeComposer.BuildSkillDelta"/> maps <c>disallowed-tools</c> to
    /// <see cref="TurnShape.DeniedTools"/> (not AllowedTools). Verifies the delta shape.
    /// </summary>
    [Fact]
    public void SkillTurnShapeComposer_maps_disallowed_tools_to_DeniedTools_not_AllowedTools()
    {
        var skill = new SkillDefinition("test-skill", Description: string.Empty, Body: string.Empty)
        {
            AllowedTools = [],
            DisallowedTools = ["run_command", "write_file"],
        };

        var delta = SkillTurnShapeComposer.BuildSkillDelta(skill);

        Assert.NotNull(delta);
        // Skill disallowedTools → DeniedTools, not AllowedTools restriction.
        Assert.NotNull(delta!.DeniedTools);
        Assert.Null(delta.AllowedTools);
        Assert.Contains("run_command", delta.DeniedTools!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("write_file", delta.DeniedTools!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When the parent turn was shaped by <c>DeniedTools</c> alone (the skill path),
    /// <see cref="TurnShapeResolution.ToToolRestrictionShape"/> propagates a deny-only shape
    /// to children — NOT an allow-list of the remaining tools. This preserves the child's
    /// access to tools it has that the parent doesn't, while still blocking the explicitly
    /// denied ones.
    /// </summary>
    [Fact]
    public void Skill_DeniedTools_propagates_as_deny_only_not_as_allowed_intersection()
    {
        var tools = Registry("read_file", "run_command", "write_file", "task");
        var resolution = Resolve(tools, new TurnShape { DeniedTools = ["run_command"] });

        var childShape = resolution.ToToolRestrictionShape();

        Assert.NotNull(childShape);
        // Must propagate as DeniedTools (deny-only), not AllowedTools.
        Assert.NotNull(childShape!.DeniedTools);
        Assert.Null(childShape.AllowedTools);
        Assert.Contains("run_command", childShape.DeniedTools!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// End-to-end: a child subagent resolved with a deny-only parent restriction loses the
    /// denied tool but keeps all other tools in its own registry — including tools the parent
    /// does not have.
    /// </summary>
    [Fact]
    public void Child_with_deny_only_parent_restriction_loses_only_denied_tool()
    {
        var parentTools = Registry("read_file", "run_command", "task");
        var parentResolution = Resolve(parentTools, new TurnShape { DeniedTools = ["run_command"] });
        var childRestriction = parentResolution.ToToolRestrictionShape();

        // Child has a superset: it also has "glob" and "grep" (which the parent didn't advertise).
        var childTools = Registry("read_file", "run_command", "glob", "grep", "task");
        var childResolution = Resolve(childTools, childRestriction);

        var childNames = childResolution.ToolDefinitions.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Denied tool is gone.
        Assert.DoesNotContain("run_command", childNames);
        // Non-denied tools survive — including "glob" and "grep" which the parent didn't have.
        Assert.Contains("read_file", childNames);
        Assert.Contains("glob", childNames);
        Assert.Contains("grep", childNames);
        Assert.Contains("task", childNames);
    }

    // ------------------------------------------------------------------
    // Lock 2 — Hook allowedTools still propagates as intersection to subagents
    // ------------------------------------------------------------------

    /// <summary>
    /// When the parent turn was shaped by <c>AllowedTools</c> (the hook path), the resolved
    /// <see cref="TurnShapeResolution.ToToolRestrictionShape"/> returns a shape with
    /// <see cref="TurnShape.AllowedTools"/> — NOT a deny list. This causes the child's resolver
    /// to intersect the parent's allowed set with the child's registry.
    /// </summary>
    [Fact]
    public void Hook_AllowedTools_propagates_as_allowed_list_not_as_deny_list()
    {
        var tools = Registry("read_file", "run_command", "task", "write_file");
        // Simulate hook response: allowedTools = ["task", "read_file"].
        var resolution = Resolve(tools, new TurnShape { AllowedTools = ["task", "read_file"] });

        var childShape = resolution.ToToolRestrictionShape();

        Assert.NotNull(childShape);
        // Must propagate as AllowedTools (not DeniedTools).
        Assert.NotNull(childShape!.AllowedTools);
        Assert.Null(childShape.DeniedTools);
    }

    /// <summary>
    /// End-to-end: a child subagent resolved with a hook's allowed-list parent restriction
    /// sees only the INTERSECTION of the parent's allowed set with the child's own registry.
    /// Tools the parent did not permit (even if the child has them) are excluded.
    /// </summary>
    [Fact]
    public void Child_with_hook_allowed_parent_restriction_is_intersected_with_parent_allowed_set()
    {
        var parentTools = Registry("read_file", "run_command", "task", "write_file");
        // Hook allows only read_file + task.
        var parentResolution = Resolve(parentTools, new TurnShape { AllowedTools = ["task", "read_file"] });
        var childRestriction = parentResolution.ToToolRestrictionShape();

        // Child has a superset: glob, grep, write_file, etc.
        var childTools = Registry("read_file", "run_command", "glob", "grep", "task", "write_file");
        var childResolution = Resolve(childTools, childRestriction);

        var childNames = childResolution.ToolDefinitions.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only tools in the parent's allowed set survive the intersection.
        Assert.Contains("task", childNames);
        Assert.Contains("read_file", childNames);
        // Tools not in the parent's allowed set are excluded even if child has them.
        Assert.DoesNotContain("run_command", childNames);
        Assert.DoesNotContain("glob", childNames);
        Assert.DoesNotContain("grep", childNames);
        Assert.DoesNotContain("write_file", childNames);
    }
}
