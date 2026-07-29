using System.Text.Json;
using Coda.Agent;
using Coda.Tui.Plugins;
using Coda.Tui.Skills;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for Skills Phase 7 — Trust: origin-based gating for the model-invocable skill tool.
/// (Tests 7–10 from the Phase 7 spec.)
/// </summary>
public sealed class SkillPhase7TrustTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static SkillDefinition Skill(
        string name,
        SkillOrigin origin,
        string body = "Skill body.",
        string description = "A skill.")
        => new(name, description, body) { Origin = origin };

    private static async Task<ToolResult> InvokeAsync(SkillTool tool, string name)
    {
        var props = new Dictionary<string, object?> { ["name"] = name };
        var json = JsonSerializer.Serialize(props);
        var element = JsonDocument.Parse(json).RootElement;
        return await tool.ExecuteAsync(element, new ToolContext(Directory.GetCurrentDirectory()));
    }

    // =========================================================================
    // Test 7 — Model-invoked Claude/Plugin skill requires approval;
    //           Project/User origins do not
    // =========================================================================

    [Theory]
    [InlineData(SkillOrigin.Project)]
    [InlineData(SkillOrigin.User)]
    public async Task TrustedOrigins_load_without_approval(SkillOrigin origin)
    {
        var skill = Skill("my-skill", origin);
        var state = new SkillSessionState();

        // Gate with a callback that always denies (it should never be called for trusted origins)
        var promptCalled = false;
        var gate = new SkillOriginGate(state,
            promptCallback: (_, _) =>
            {
                promptCalled = true;
                return Task.FromResult(false);
            });

        var tool = new SkillTool([skill], state, originGate: gate);
        var result = await InvokeAsync(tool, "my-skill");

        // Trusted origins must not call the prompt and must load the body
        Assert.False(promptCalled, "Prompt should not be called for trusted origins");
        Assert.False(result.IsError);
        Assert.DoesNotContain("requires approval", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SkillOrigin.Claude)]
    [InlineData(SkillOrigin.Plugin)]
    public async Task UntrustedOrigins_blocked_until_approved(SkillOrigin origin)
    {
        var skill = Skill("ext-skill", origin);
        var state = new SkillSessionState();

        // Gate that always denies
        var gate = new SkillOriginGate(state, promptCallback: (_, _) => Task.FromResult(false));
        var tool = new SkillTool([skill], state, originGate: gate);

        var result = await InvokeAsync(tool, "ext-skill");

        Assert.False(result.IsError, "Refusal should not be an error");
        Assert.Contains("requires approval", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SkillOrigin.Claude)]
    [InlineData(SkillOrigin.Plugin)]
    public async Task UntrustedOrigins_load_after_approval(SkillOrigin origin)
    {
        var skill = Skill("ext-skill", origin, body: "The real body.");
        var state = new SkillSessionState();

        // Gate that always approves
        var gate = new SkillOriginGate(state, promptCallback: (_, _) => Task.FromResult(true));
        var tool = new SkillTool([skill], state, originGate: gate);

        var result = await InvokeAsync(tool, "ext-skill");

        // Should load after approval
        Assert.False(result.IsError);
        Assert.DoesNotContain("requires approval", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Test 8 — /skill <name> on a Claude-origin skill runs without a prompt
    // =========================================================================

    [Fact]
    public async Task SkillOriginGate_trustedOrigin_never_calls_promptCallback()
    {
        // Project and User origins pass without any prompt
        var projectSkill = Skill("project-skill", SkillOrigin.Project);
        var userSkill = Skill("user-skill", SkillOrigin.User);
        var state = new SkillSessionState();

        var promptCallCount = 0;
        var gate = new SkillOriginGate(state,
            promptCallback: (_, _) =>
            {
                promptCallCount++;
                return Task.FromResult(true);
            });

        Assert.True(await gate.MayLoadAsync(projectSkill, CancellationToken.None));
        Assert.True(await gate.MayLoadAsync(userSkill, CancellationToken.None));
        Assert.Equal(0, promptCallCount);
    }

    [Fact]
    public async Task SkillOriginGate_approvedSession_cached_across_invocations()
    {
        // Once approved in this session, subsequent invocations skip the prompt
        var skill = Skill("ext-skill", SkillOrigin.Claude);
        var state = new SkillSessionState();

        var callCount = 0;
        var gate = new SkillOriginGate(state,
            promptCallback: (_, _) =>
            {
                callCount++;
                return Task.FromResult(true);
            });

        // First call: prompts
        var first = await gate.MayLoadAsync(skill, CancellationToken.None);
        // Second call: cached → no prompt
        var second = await gate.MayLoadAsync(skill, CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(1, callCount); // prompted only once
    }

    // =========================================================================
    // Test 9 — Unattended model-invocation returns "approval required" without loading
    // =========================================================================

    [Theory]
    [InlineData(SkillOrigin.Claude)]
    [InlineData(SkillOrigin.Plugin)]
    public async Task Unattended_untrustedOrigin_returns_approvalRequired_without_body(SkillOrigin origin)
    {
        var secretBody = "SECRET BODY CONTENT";
        var skill = Skill("unattended-skill", origin, body: secretBody);
        var state = new SkillSessionState();

        // Unattended: null prompt callback
        var gate = new SkillOriginGate(state, promptCallback: null);
        var tool = new SkillTool([skill], state, originGate: gate);

        var result = await InvokeAsync(tool, "unattended-skill");

        // Should return approval-required message (not an error)
        Assert.False(result.IsError);
        Assert.Contains("requires approval", result.Content, StringComparison.OrdinalIgnoreCase);

        // Must NOT expose the skill body
        Assert.DoesNotContain(secretBody, result.Content);
    }

    [Fact]
    public async Task Unattended_trustedOrigin_loads_without_prompt()
    {
        // Project/User origins still load in unattended mode (no prompt needed)
        var body = "TRUSTED BODY";
        var skill = Skill("proj-skill", SkillOrigin.Project, body: body);
        var state = new SkillSessionState();

        var gate = new SkillOriginGate(state, promptCallback: null);
        var tool = new SkillTool([skill], state, originGate: gate);

        var result = await InvokeAsync(tool, "proj-skill");

        Assert.False(result.IsError);
        Assert.DoesNotContain("requires approval", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Test 10 — Corrupt trust file is nothing-trusted, not crash, not grant
    // =========================================================================

    [Fact]
    public void CorruptTrustFile_isWorkspaceTrusted_returns_false()
    {
        var trustDir = Directory.CreateTempSubdirectory("coda_p7_skill_corrupt_").FullName;
        try
        {
            var codaDir = Path.Combine(trustDir, ".coda");
            Directory.CreateDirectory(codaDir);
            File.WriteAllText(Path.Combine(codaDir, "plugin-trust.json"), "{ invalid json !!!");

            var trustStore = new PluginTrustStore(trustDir);
            var project = Path.Combine(trustDir, "some-project");

            // Should not throw
            var isTrusted = trustStore.IsWorkspaceTrusted(project);
            Assert.False(isTrusted, "Corrupt file must not grant workspace trust");
        }
        finally
        {
            try { Directory.Delete(trustDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CorruptTrustFile_getApprovedClasses_returns_empty()
    {
        var trustDir = Directory.CreateTempSubdirectory("coda_p7_skill_corrupt2_").FullName;
        try
        {
            var codaDir = Path.Combine(trustDir, ".coda");
            Directory.CreateDirectory(codaDir);
            File.WriteAllText(Path.Combine(codaDir, "plugin-trust.json"), "[not an object]");

            var trustStore = new PluginTrustStore(trustDir);
            var hash = PluginContentHash.Compute("any-plugin", "1.0.0");

            // Should not throw
            var approvals = trustStore.GetApprovedClasses(hash);
            Assert.Empty(approvals);

            var hasRecord = trustStore.HasApprovalRecord(hash);
            Assert.False(hasRecord);
        }
        finally
        {
            try { Directory.Delete(trustDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CorruptTrustFile_write_after_corrupt_recovers()
    {
        var trustDir = Directory.CreateTempSubdirectory("coda_p7_skill_corrupt3_").FullName;
        try
        {
            var codaDir = Path.Combine(trustDir, ".coda");
            Directory.CreateDirectory(codaDir);
            var trustFile = Path.Combine(codaDir, "plugin-trust.json");
            File.WriteAllText(trustFile, "CORRUPT");

            var trustStore = new PluginTrustStore(trustDir);

            // Write should succeed even after a corrupt read
            var hash = PluginContentHash.Compute("recovery-plugin", "1.0.0");
            trustStore.SetApprovedClasses(hash, [PluginComponentClass.Skill]);

            // Now read should work
            Assert.True(trustStore.HasApprovalRecord(hash));
            Assert.Contains(PluginComponentClass.Skill, trustStore.GetApprovedClasses(hash));
        }
        finally
        {
            try { Directory.Delete(trustDir, recursive: true); } catch { }
        }
    }

    // =========================================================================
    // SkillSessionState origin consent tracking
    // =========================================================================

    [Fact]
    public void SkillSessionState_GrantOriginConsent_and_HasOriginConsent()
    {
        var state = new SkillSessionState();

        Assert.False(state.HasOriginConsent("ext-skill"));
        state.GrantOriginConsent("ext-skill");
        Assert.True(state.HasOriginConsent("ext-skill"));

        // Other skills are not affected
        Assert.False(state.HasOriginConsent("other-skill"));
    }

    [Fact]
    public void SkillSessionState_consent_is_case_insensitive()
    {
        var state = new SkillSessionState();

        state.GrantOriginConsent("My-Skill");
        Assert.True(state.HasOriginConsent("my-skill"));
        Assert.True(state.HasOriginConsent("MY-SKILL"));
    }

    // =========================================================================
    // Headless gate (null callback) — production behaviour for HeadlessRunner / ServeRunner
    // =========================================================================

    [Theory]
    [InlineData(SkillOrigin.Claude)]
    [InlineData(SkillOrigin.Plugin)]
    public async Task NullCallback_gate_blocks_external_skills_unattended(SkillOrigin origin)
    {
        // In headless / serve mode the production roots construct a SkillOriginGate with
        // promptCallback: null, which must refuse Claude- and Plugin-origin skills rather
        // than loading them silently.  (L2 fix: the removed test blessed null-gate-passes-all
        // as correct production behaviour — this test documents the correct requirement.)
        var skill = Skill("ext-skill", origin, body: "SECRET");
        var state = new SkillSessionState();

        var gate = new SkillOriginGate(state, promptCallback: null);
        var tool = new SkillTool([skill], state, originGate: gate);
        var result = await InvokeAsync(tool, "ext-skill");

        Assert.False(result.IsError, "Refusal must not be reported as an error");
        Assert.Contains("requires approval", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET", result.Content);
    }

    [Theory]
    [InlineData(SkillOrigin.Project)]
    [InlineData(SkillOrigin.User)]
    public async Task NullCallback_gate_allows_trusted_origins_unattended(SkillOrigin origin)
    {
        // Project- and User-origin skills must still load when the gate has no callback.
        var skill = Skill("trusted-skill", origin, body: "TRUSTED BODY");
        var state = new SkillSessionState();

        var gate = new SkillOriginGate(state, promptCallback: null);
        var tool = new SkillTool([skill], state, originGate: gate);
        var result = await InvokeAsync(tool, "trusted-skill");

        Assert.False(result.IsError);
        Assert.DoesNotContain("requires approval", result.Content, StringComparison.OrdinalIgnoreCase);
    }
}
