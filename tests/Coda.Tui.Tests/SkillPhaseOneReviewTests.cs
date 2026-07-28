using System.Runtime.CompilerServices;
using Coda.Agent;
using Coda.Agent.Compaction;
using Coda.Agent.Scheduling;
using Coda.Agent.Settings;
using Coda.Agent.Tasks;
using Coda.Sdk;
using Coda.Sdk.Turns;
using Coda.Tui.Skills;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for Phase 1 review findings 1–4 on the model-invocable skills feature.
/// </summary>

// ── Finding 1 — subagent registry must not include the skill tool ──────────────────────────────

/// <summary>
/// Verifies that <see cref="TurnPipelineBuilder"/> strips the <c>skill</c> tool from subagent
/// registries while leaving it in the parent/root registry.
/// </summary>
public sealed class SkillSubagentRegistryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_skill_subagent_").FullName;

    private TurnPipelineBuilder NewBuilder() => new(
        new TodoStore(),
        new ScheduledTaskStore(),
        new TaskManager(sessionId: "s", logRoot: null),
        lspManager: null,
        lspDiagnostics: null,
        toolSearchCoordinator: null,
        NullLoggerFactory.Instance,
        (_, _, _) => Task.CompletedTask,
        () => null);

    private static ILlmClient StubClient() => new StubLlmClient();

    [Fact]
    public void Parent_registry_contains_skill_and_subagent_does_not()
    {
        var state = new SkillSessionState();
        var skillTool = new SkillTool(
            [new SkillDefinition("alpha", "Does alpha.", "Alpha body.")],
            state);

        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
            ExtraTools = [skillTool],
        };

        var spec = this.NewBuilder().BuildSpec(options, StubClient(), CodaSettings.Empty);

        // Parent registry includes the skill tool.
        var parentNames = spec.Tools.All.Select(t => t.Name).ToHashSet();
        Assert.Contains("skill", parentNames);

        // Subagent registry must NOT include the skill tool — skills share mutable
        // SkillSessionState with the root; letting a subagent call skill would permanently
        // mark a skill loaded in the root's state without the body entering the root's history.
        var subagentHost = (SubagentHost)spec.Subagents!;
        var subagentNames = subagentHost.SubagentTools.All.Select(t => t.Name).ToHashSet();
        Assert.DoesNotContain("skill", subagentNames);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }

    private sealed class StubLlmClient : ILlmClient
    {
        public string ProviderId => ClaudeAiProvider.Id;

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }
}

// ── Findings 2, 3, 4 — SkillSessionState pure unit tests ──────────────────────────────────────

/// <summary>
/// Pure unit tests for <see cref="SkillSessionState"/> covering the four review findings:
/// eviction on budget overflow, budget derivation scaling, and continue-past-large packing.
/// </summary>
public sealed class SkillSessionStateFindingsTests
{
    // ── Finding 2 — eviction reconciles loaded set with reattach output ───────────────

    [Fact]
    public void Skill_dropped_due_to_budget_evicted_and_returns_full_body_on_next_invocation()
    {
        var state = new SkillSessionState();
        var body = new string('x', 100);
        state.TryLoad("big-skill", body);

        // Budget too small to include the body; the skill must be evicted from the loaded set.
        var content = state.GetReattachContent(charBudget: 10);

        // Ceiling guarantee: most-recent body <= DefaultReattachBudget is always emitted, so
        // "big-skill" (100 chars <= 20 000) IS in the output despite the tight budget.
        // The eviction only fires for skills that are entirely absent from the output.
        // Verify the case where two skills exist and only one fits within budget:
        var state2 = new SkillSessionState();
        var smallBody = new string('a', 5);
        var largeBody = new string('b', 500);
        state2.TryLoad("small-skill", smallBody);
        state2.TryLoad("large-skill", largeBody); // most recent

        // Budget = 10: large-skill (500) > 10, so large-skill goes via ceiling path (500 <= 20000);
        // small-skill (5) fits in remaining budget only if large wasn't included... but here
        // ceiling kicks in for most-recent, then small-skill is tried next — 5 fits but
        // totalLength check: first pass skips large (500>10), continues to small (5<=10 → include).
        // BUT ceiling fires only when parts.Count==0 after the loop, so small IS included first,
        // large is added via the ceiling path only if parts is empty. Let's use a cleaner setup.

        // Clean test: one large body that cannot fit in budget, one small that can.
        var state3 = new SkillSessionState();
        state3.TryLoad("older-small", new string('s', 5));
        state3.TryLoad("newest-large", new string('L', 500)); // most recent, 500 chars

        // Budget = 20: newest-large (500 > 20) → skipped in loop (not via ceiling since parts could be non-empty).
        // After loop: parts may contain older-small (5 <= 20). But newest-large (500 <= 20000) is
        // emitted via ceiling only if parts.Count==0. With budget=20: oldest fits (5 < 20), so
        // parts=[older-small] → ceiling does NOT fire → newest-large is evicted.
        var reattach = state3.GetReattachContent(charBudget: 20);

        // oldest-small was included; newest-large was not → newest-large should be evicted.
        Assert.Contains(new string('s', 5), reattach);
        Assert.DoesNotContain(new string('L', 500), reattach);

        // After eviction: TryLoad("newest-large", same body) must return the full body, NOT "already loaded".
        var (isFirst, content2) = state3.TryLoad("newest-large", new string('L', 500));
        Assert.True(isFirst);
        Assert.Contains(new string('L', 500), content2);
    }

    // ── Finding 3 — budget derives from compaction threshold ─────────────────────────

    [Fact]
    public void DeriveReattachBudget_small_threshold_yields_proportional_budget()
    {
        // threshold=1000 → 1000 * 0.25 * 4 = 1000 < 20000 → budget=1000
        var budget = SkillSessionState.DeriveReattachBudget(1000);
        Assert.Equal(1000, budget);
    }

    [Fact]
    public void DeriveReattachBudget_large_threshold_is_capped_at_ceiling()
    {
        // threshold=100_000 → 100000 * 0.25 * 4 = 100000 > 20000 → capped at 20000
        var budget = SkillSessionState.DeriveReattachBudget(100_000);
        Assert.Equal(SkillSessionState.DefaultReattachBudget, budget);
    }

    [Fact]
    public void DeriveReattachBudget_proportional_below_ceiling()
    {
        // threshold=10_000 → 10000 * 0.25 * 4 = 10000 < 20000 → budget=10000
        var budget = SkillSessionState.DeriveReattachBudget(10_000);
        Assert.Equal(10_000, budget);
    }

    [Fact]
    public void DeriveReattachBudget_zero_threshold_returns_default()
    {
        Assert.Equal(SkillSessionState.DefaultReattachBudget, SkillSessionState.DeriveReattachBudget(0));
    }

    // ── Finding 4 — continue-past-large packs smaller bodies ─────────────────────────

    [Fact]
    public void GetReattachContent_continues_past_large_to_pack_smaller_ones()
    {
        // Load in order: small1 (oldest), big (middle), small2 (newest/most-recent).
        // Invocation order: small1=0, big=1, small2=2.
        // Most-recently-used order: small2, big, small1.
        var state = new SkillSessionState();
        state.TryLoad("small1", new string('a', 5)); // invocation order 0
        state.TryLoad("big", new string('B', 200));  // invocation order 1
        state.TryLoad("small2", new string('c', 5)); // invocation order 2 (most recent)

        // Budget = 30: small2 (5) fits → include. big (200+2) doesn't fit → continue.
        // small1 (5+2=7 additional) fits → include. Result has both small bodies.
        var content = state.GetReattachContent(charBudget: 30);

        Assert.Contains(new string('c', 5), content);  // small2 included
        Assert.DoesNotContain(new string('B', 200), content); // big skipped
        Assert.Contains(new string('a', 5), content);  // small1 included despite big being in between
    }

    [Fact]
    public void GetReattachContent_oversized_most_recent_within_ceiling_still_emits()
    {
        // Only one skill, body larger than the dynamic budget but <= DefaultReattachBudget.
        var state = new SkillSessionState();
        var body = new string('Z', 500); // 500 chars, fits within 20000 ceiling
        state.TryLoad("single", body);

        // Tiny budget that won't fit the body, but ceiling guarantee must fire.
        var content = state.GetReattachContent(charBudget: 10);

        Assert.Contains(body, content);
    }
}

// ── Finding 3 — pipeline-level skip and exactly-once injection tests ───────────────────────────

/// <summary>
/// Integration tests using <see cref="TurnPipelineBuilder.BuildSpec"/> to verify the two
/// pipeline-level Finding 3 guards: skip injection when history is already near the compaction
/// threshold, and inject exactly once rather than twice on back-to-back compactions.
/// </summary>
public sealed class SkillReattachPipelineTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_skill_pipe_").FullName;
    private int compactCalls;

    private TurnPipelineBuilder NewBuilder() => new(
        new TodoStore(),
        new ScheduledTaskStore(),
        new TaskManager(sessionId: "p", logRoot: null),
        lspManager: null,
        lspDiagnostics: null,
        toolSearchCoordinator: null,
        NullLoggerFactory.Instance,
        (_, _, _) =>
        {
            // No-op compaction: history is NOT modified, simulating post-compact state
            // that's already at or near the threshold.
            Interlocked.Increment(ref this.compactCalls);
            return Task.CompletedTask;
        },
        () => null);

    private static ILlmClient StubClient() => new StubLlmClient();

    /// <summary>
    /// Creates a <see cref="SessionOptions"/> snapshot with a goal (to get <c>CompactAsync</c>
    /// wired), a small threshold, and the given <paramref name="skillReattachProvider"/>.
    /// </summary>
    private SessionOptions Options(
        int threshold,
        Func<int, string>? skillReattachProvider = null) =>
        new()
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
            Goal = "ship it",
            AutoCompactTokenThreshold = threshold,
            SkillReattachContentProvider = skillReattachProvider,
        };

    [Fact]
    public async Task Skill_reattach_skipped_when_post_compact_history_near_threshold()
    {
        const int Threshold = 100;
        const string ReattachBody = "SKILL-REATTACH-CONTENT"; // 22 chars → 5 tokens

        var state = new SkillSessionState();
        state.TryLoad("my-skill", ReattachBody);

        var options = this.Options(
            Threshold,
            skillReattachProvider: t => state.GetReattachContent(SkillSessionState.DeriveReattachBudget(t)));

        var spec = this.NewBuilder().BuildSpec(options, StubClient(), CodaSettings.Empty);
        Assert.NotNull(spec.CompactAsync);

        // Pre-populate history so post-compact token estimate + reattach tokens >= threshold.
        // Threshold=100. TokenEstimator: chars/4. Reattach=22 chars → 5 tokens.
        // Need postCompact tokens ≥ 95, i.e., chars ≥ 380.
        // Use 380 chars of text → 95 tokens. 95 + 5 = 100 ≥ 100 → skip.
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextBlock(new string('h', 380))]),
        };
        var historyCountBefore = history.Count;

        await spec.CompactAsync!(history, CancellationToken.None);

        // Reattach must NOT have been injected because history was already near the threshold.
        Assert.Equal(historyCountBefore, history.Count);
        Assert.DoesNotContain(history, m =>
            m.Role == ChatRole.User
            && m.Content is [TextBlock tb]
            && tb.Text.Contains(ReattachBody));
    }

    [Fact]
    public async Task Skill_reattach_injected_exactly_once_when_compact_fires_twice()
    {
        const int Threshold = 200_000; // large enough that skip-near-threshold never fires
        const string ReattachBody = "REATTACH-BODY";

        var state = new SkillSessionState();
        state.TryLoad("my-skill", ReattachBody);

        var options = this.Options(
            Threshold,
            skillReattachProvider: t => state.GetReattachContent(SkillSessionState.DeriveReattachBudget(t)));

        var spec = this.NewBuilder().BuildSpec(options, StubClient(), CodaSettings.Empty);
        Assert.NotNull(spec.CompactAsync);

        var history = new List<ChatMessage>();

        // First compaction: reattach content is injected.
        await spec.CompactAsync!(history, CancellationToken.None);
        Assert.Single(history);

        // Second compaction without an intervening turn: trailing-message guard must prevent
        // a second copy of the reattach content from being added.
        await spec.CompactAsync!(history, CancellationToken.None);

        var reattachMessages = history
            .Where(m =>
                m.Role == ChatRole.User
                && m.Content is [TextBlock tb]
                && tb.Text == ReattachBody)
            .ToList();

        Assert.Single(reattachMessages); // exactly once, not twice
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }

    private sealed class StubLlmClient : ILlmClient
    {
        public string ProviderId => ClaudeAiProvider.Id;

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }
}
