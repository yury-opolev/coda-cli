using System.Collections.Concurrent;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Skills;
using Coda.Tui.Ui.Shells;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Tests;

// ─── I1: Atomic registry swap ────────────────────────────────────────────────

/// <summary>
/// I1: <see cref="SlashCommandRegistry.ReplaceAll"/> must publish the new command set atomically
/// so concurrent readers always observe either the complete old snapshot or the complete new one,
/// never a half-rebuilt state that throws <see cref="InvalidOperationException"/> or returns a
/// mixed result.
/// </summary>
public sealed class SkillPhase5ReviewFix_I1_AtomicRegistryTests
{
    [Fact]
    public void ReplaceAll_exposes_only_complete_new_set_immediately_after_swap()
    {
        var oldSkill = Phase5Helpers.Skill("old-skill");
        var registry = new SlashCommandRegistry(SlashCommandCatalog.CreateWithSkills([oldSkill]));

        var newSkill = Phase5Helpers.Skill("new-skill");
        registry.ReplaceAll(SlashCommandCatalog.CreateWithSkills([newSkill]));

        var names = registry.ListSorted().Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // After a swap the old entry is gone and the new one is present — never a mix.
        Assert.DoesNotContain("old-skill", names);
        Assert.Contains("new-skill", names);
    }

    [Fact]
    public async Task ReplaceAll_and_ListSorted_are_safe_under_concurrent_access()
    {
        // Build two non-overlapping skill sets large enough to stress the old Clear + re-add path.
        var skills1 = Enumerable.Range(0, 40).Select(i => Phase5Helpers.Skill($"skill-a{i}")).ToList();
        var skills2 = Enumerable.Range(0, 40).Select(i => Phase5Helpers.Skill($"skill-b{i}")).ToList();

        var registry = new SlashCommandRegistry(SlashCommandCatalog.CreateWithSkills(skills1));

        var errors = new ConcurrentBag<Exception>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));

        var readerTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    _ = registry.ListSorted();
                    _ = registry.Resolve("skill-a0");
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        }, CancellationToken.None);

        var writerTask = Task.Run(async () =>
        {
            for (var i = 0; i < 200 && !cts.Token.IsCancellationRequested; i++)
            {
                registry.ReplaceAll(SlashCommandCatalog.CreateWithSkills(i % 2 == 0 ? skills1 : skills2));
                await Task.Delay(1, CancellationToken.None).ConfigureAwait(false);
            }
        }, CancellationToken.None);

        await Task.WhenAll(readerTask, writerTask).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(errors);
    }

    [Fact]
    public void ListSorted_after_ReplaceAll_returns_only_entries_from_new_set()
    {
        // Stronger form: every name in the list belongs to the new command set.
        var original = SlashCommandCatalog.CreateAll();
        var registry = new SlashCommandRegistry(original);

        var uniqueSkill = Phase5Helpers.Skill("unique-post-reload-skill");
        var freshSet = SlashCommandCatalog.CreateWithSkills([uniqueSkill]);
        registry.ReplaceAll(freshSet);

        var expected = freshSet.Select(c => c.Name).OrderBy(n => n).ToList();
        var actual = registry.ListSorted().Select(c => c.Name).ToList(); // already sorted

        Assert.Equal(expected, actual);
    }
}

// ─── M1: Invalid skill name validation ───────────────────────────────────────

/// <summary>
/// M1: <see cref="SkillCommandRegistrar.BuildSkillCommands"/> must reject names that are
/// unreachable from <see cref="Repl.CommandParser.Parse"/>: empty/whitespace, containing
/// internal whitespace, or starting with <c>/</c>.
/// </summary>
public sealed class SkillPhase5ReviewFix_M1_NameValidationTests
{
    private static Phase5Helpers.CapturingLogger Logger() => new();

    // ── slash-prefix names ───────────────────────────────────────────────

    [Fact]
    public void Skill_with_slash_prefix_name_not_registered()
    {
        var skill = Phase5Helpers.Skill("/deploy");
        var builtIns = SlashCommandCatalog.CreateAll();
        var commands = SkillCommandRegistrar.BuildSkillCommands([skill], builtIns);
        Assert.DoesNotContain(commands, c => c.Name == "/deploy");
    }

    [Fact]
    public void Skill_with_slash_prefix_name_logs_warning()
    {
        var logger = Logger();
        var skill = Phase5Helpers.Skill("/deploy", sourcePath: "/skills/deploy/SKILL.md");
        var builtIns = SlashCommandCatalog.CreateAll();
        SkillCommandRegistrar.BuildSkillCommands([skill], builtIns, logger);

        var warning = logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Warning);
        Assert.NotNull(warning.Message);
        Assert.Contains("/deploy", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── internal-whitespace names ────────────────────────────────────────

    [Fact]
    public void Skill_with_internal_whitespace_name_not_registered()
    {
        var skill = Phase5Helpers.Skill("deploy prod");
        var builtIns = SlashCommandCatalog.CreateAll();
        var commands = SkillCommandRegistrar.BuildSkillCommands([skill], builtIns);
        Assert.DoesNotContain(commands, c => c.Name.Contains(' '));
    }

    [Fact]
    public void Skill_with_internal_whitespace_logs_warning()
    {
        var logger = Logger();
        var skill = Phase5Helpers.Skill("deploy prod", sourcePath: "/skills/deploy-prod/SKILL.md");
        var builtIns = SlashCommandCatalog.CreateAll();
        SkillCommandRegistrar.BuildSkillCommands([skill], builtIns, logger);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Skill_with_tab_in_name_not_registered()
    {
        var skill = Phase5Helpers.Skill("deploy\tprod");
        var builtIns = SlashCommandCatalog.CreateAll();
        var commands = SkillCommandRegistrar.BuildSkillCommands([skill], builtIns);
        Assert.DoesNotContain(commands, c => c.Name.Contains('\t'));
    }

    // ── empty / whitespace-only names ────────────────────────────────────

    [Fact]
    public void Skill_with_empty_name_not_registered()
    {
        var skill = Phase5Helpers.Skill("");
        var builtIns = SlashCommandCatalog.CreateAll();
        var commands = SkillCommandRegistrar.BuildSkillCommands([skill], builtIns);
        Assert.DoesNotContain(commands, c => string.IsNullOrWhiteSpace(c.Name));
    }

    [Fact]
    public void Skill_with_whitespace_only_name_not_registered()
    {
        var skill = Phase5Helpers.Skill("   ");
        var builtIns = SlashCommandCatalog.CreateAll();
        var commands = SkillCommandRegistrar.BuildSkillCommands([skill], builtIns);
        Assert.DoesNotContain(commands, c => string.IsNullOrWhiteSpace(c.Name));
    }

    // ── valid name still registers ────────────────────────────────────────

    [Fact]
    public void Skill_with_valid_name_still_registers()
    {
        var skill = Phase5Helpers.Skill("valid-skill");
        var builtIns = SlashCommandCatalog.CreateAll();
        var commands = SkillCommandRegistrar.BuildSkillCommands([skill], builtIns);
        Assert.Contains(commands, c => c.Name == "valid-skill");
    }
}

// ─── M2: Reload accuracy ──────────────────────────────────────────────────────

/// <summary>
/// M2: <c>/skills reload</c> must report the number of skill commands that were
/// <em>actually registered</em>, not the count of user-invocable skills in the input.
/// When collisions or invalid names reduce the registered set, the reported number
/// reflects that reduction.
/// </summary>
public sealed class SkillPhase5ReviewFix_M2_ReloadAccuracyTests
{
    [Fact]
    public void BuildSkillCommands_returns_actual_registered_count_excluding_collisions()
    {
        // "help" collides with the built-in; only "my-skill" should register.
        var helpSkill = Phase5Helpers.Skill("help");
        var goodSkill = Phase5Helpers.Skill("my-skill");
        var builtIns = SlashCommandCatalog.CreateAll();

        var commands = SkillCommandRegistrar.BuildSkillCommands([helpSkill, goodSkill], builtIns);

        // "help" is excluded → actual count is 1, not 2.
        Assert.Single(commands);
        Assert.DoesNotContain(commands, c => c.Name == "help");
        Assert.Contains(commands, c => c.Name == "my-skill");
    }

    [Fact]
    public void BuildSkillCommands_returns_actual_registered_count_excluding_invalid_names()
    {
        // "/deploy" has an invalid name; only "my-skill" should register.
        var invalidSkill = Phase5Helpers.Skill("/deploy");
        var goodSkill = Phase5Helpers.Skill("my-skill");
        var builtIns = SlashCommandCatalog.CreateAll();

        var commands = SkillCommandRegistrar.BuildSkillCommands([invalidSkill, goodSkill], builtIns);

        Assert.Single(commands);
        Assert.Contains(commands, c => c.Name == "my-skill");
    }

    [Fact]
    public void BuildSkillCommands_count_matches_registered_skill_commands_with_no_issues()
    {
        // No collisions, no invalid names → count matches input user-invocable count.
        var skills = new[]
        {
            Phase5Helpers.Skill("skill-one"),
            Phase5Helpers.Skill("skill-two"),
            Phase5Helpers.Skill("skill-three"),
        };
        var builtIns = SlashCommandCatalog.CreateAll();
        var commands = SkillCommandRegistrar.BuildSkillCommands(skills, builtIns);
        Assert.Equal(3, commands.Count);
    }
}

// ─── M3: ◆ marker moved to render time ───────────────────────────────────────

/// <summary>
/// M3: <see cref="SkillSlashCommand.Summary"/> must return the description unchanged
/// (no <see cref="SkillSlashCommand.SkillMarker"/> prefix). The marker is prepended only
/// by UI renderers so the completion-ranking heuristic and JSON output are unaffected.
/// </summary>
public sealed class SkillPhase5ReviewFix_M3_SummaryMarkerTests
{
    [Fact]
    public void Skill_command_summary_does_not_contain_skill_marker()
    {
        var cmd = new SkillSlashCommand(Phase5Helpers.Skill("my-skill", description: "My skill."));
        Assert.DoesNotContain(SkillSlashCommand.SkillMarker, cmd.Summary);
    }

    [Fact]
    public void Skill_command_summary_returns_description_unchanged()
    {
        const string description = "Runs a workflow.";
        var cmd = new SkillSlashCommand(Phase5Helpers.Skill("my-skill", description: description));
        Assert.Equal(description, cmd.Summary);
    }

    [Fact]
    public void Skill_command_summary_includes_argument_hint_without_marker()
    {
        var cmd = new SkillSlashCommand(Phase5Helpers.Skill(
            "my-skill", description: "My skill.", argumentHint: "<target>"));
        Assert.Contains("My skill.", cmd.Summary);
        Assert.Contains("<target>", cmd.Summary);
        Assert.DoesNotContain(SkillSlashCommand.SkillMarker, cmd.Summary);
    }

    [Fact]
    public void Skill_command_description_less_summary_is_empty_not_just_marker()
    {
        // A skill with no description used to produce a cell with only "◆ " — now it's empty.
        var cmd = new SkillSlashCommand(Phase5Helpers.Skill("my-skill", description: ""));
        Assert.DoesNotContain(SkillSlashCommand.SkillMarker, cmd.Summary);
    }

    [Fact]
    public void Typing_skill_marker_character_does_not_match_skill_via_summary_rank()
    {
        // With the marker removed from Summary, typing "◆" no longer produces a rank-4 match
        // for every skill.
        var skill = Phase5Helpers.Skill("my-skill", description: "Workflow.");
        var registry = new SlashCommandRegistry(SlashCommandCatalog.CreateWithSkills([skill]));
        var completion = new SlashCommandCompletion(registry);

        completion.Update("/◆", 2); // query = "◆"

        // The skill's name/aliases/summary no longer contain "◆", so it should not match.
        Assert.DoesNotContain(completion.Suggestions, c => c.Name == "my-skill");
    }

    [Fact]
    public void Completion_view_row_text_includes_skill_marker_for_skill_entries()
    {
        var skill = Phase5Helpers.Skill("my-skill", description: "My skill.");
        var registry = new SlashCommandRegistry(SlashCommandCatalog.CreateWithSkills([skill]));
        var completion = new SlashCommandCompletion(registry);
        completion.Update("/my", 3);

        var view = new CommandCompletionView();
        view.SetSuggestions(completion.Suggestions, 0);
        var rows = view.RenderVisibleRows(width: 80);

        Assert.Contains(rows, row => row.Contains(SkillSlashCommand.SkillMarker));
    }
}

// ─── L1: Shared BindOptIn helper ─────────────────────────────────────────────

/// <summary>
/// L1: <see cref="SkillArgumentBinder.BindOptIn"/> is the single shared helper for the
/// opt-in substitution rule, replacing the copy-pasted guard in
/// <see cref="SkillSlashCommand"/> and <see cref="SkillCommand"/>.
/// </summary>
public sealed class SkillPhase5ReviewFix_L1_BindOptInTests
{
    [Fact]
    public void BindOptIn_returns_body_unchanged_when_no_args_and_no_declared_arguments()
    {
        var skill = Phase5Helpers.Skill("test", body: "Cost is $100 today.");
        var result = SkillArgumentBinder.BindOptIn(skill, []);
        Assert.Equal("Cost is $100 today.", result);
    }

    [Fact]
    public void BindOptIn_substitutes_when_args_are_provided()
    {
        var skill = Phase5Helpers.Skill("test", body: "Translate to $1: $2");
        var result = SkillArgumentBinder.BindOptIn(skill, ["French", "Hello"]);
        Assert.Equal("Translate to French: Hello", result);
    }

    [Fact]
    public void BindOptIn_substitutes_when_skill_declares_arguments_even_without_user_args()
    {
        var skill = Phase5Helpers.Skill("test", body: "Review $file.", arguments: ["file"]);
        var result = SkillArgumentBinder.BindOptIn(skill, []);
        Assert.Equal("Review .", result); // $file → empty
    }

    [Fact]
    public void BindOptIn_result_is_identical_to_manual_guard_with_args()
    {
        var skill = Phase5Helpers.Skill("test", body: "Hello $1!");
        IReadOnlyList<string> args = ["World"];

        var manual = (args.Count > 0 || skill.Arguments.Count > 0)
            ? SkillArgumentBinder.Bind(skill.Body, skill.Arguments, args)
            : skill.Body;

        var helper = SkillArgumentBinder.BindOptIn(skill, args);
        Assert.Equal(manual, helper);
    }

    [Fact]
    public void BindOptIn_result_is_identical_to_manual_guard_without_args()
    {
        var skill = Phase5Helpers.Skill("test", body: "Hello $1!");
        IReadOnlyList<string> args = [];

        var manual = (args.Count > 0 || skill.Arguments.Count > 0)
            ? SkillArgumentBinder.Bind(skill.Body, skill.Arguments, args)
            : skill.Body;

        var helper = SkillArgumentBinder.BindOptIn(skill, args);
        Assert.Equal(manual, helper);
    }
}

// ─── L3: Collision test gaps ─────────────────────────────────────────────────

/// <summary>
/// L3: Additional collision tests covering gaps identified in the review:
/// case-insensitive name collisions, word-alias collisions, and name-validation rejections
/// from M1 combined with the collision path.
/// </summary>
public sealed class SkillPhase5ReviewFix_L3_CollisionGapTests
{
    // ── Case-insensitive collisions ──────────────────────────────────────

    [Fact]
    public void Skill_named_HELP_upper_case_collides_case_insensitively()
    {
        var skill = Phase5Helpers.Skill("HELP");
        var builtIns = SlashCommandCatalog.CreateAll();
        var commands = SkillCommandRegistrar.BuildSkillCommands([skill], builtIns);
        Assert.DoesNotContain(commands, c => c.Name.Equals("HELP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Skill_named_HELP_upper_case_logs_collision_warning()
    {
        var logger = new Phase5Helpers.CapturingLogger();
        var skill = Phase5Helpers.Skill("HELP");
        var builtIns = SlashCommandCatalog.CreateAll();
        SkillCommandRegistrar.BuildSkillCommands([skill], builtIns, logger);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Skill_named_Help_mixed_case_collides_case_insensitively()
    {
        var skill = Phase5Helpers.Skill("Help");
        var builtIns = SlashCommandCatalog.CreateAll();
        var commands = SkillCommandRegistrar.BuildSkillCommands([skill], builtIns);
        Assert.DoesNotContain(commands, c => c.Name.Equals("Help", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Skill_named_Quit_collides_with_exit_alias()
    {
        // "quit" is an alias for /exit — a skill named "Quit" should collide.
        var skill = Phase5Helpers.Skill("Quit");
        var builtIns = SlashCommandCatalog.CreateAll();
        var commands = SkillCommandRegistrar.BuildSkillCommands([skill], builtIns);
        Assert.DoesNotContain(commands, c => c.Name.Equals("Quit", StringComparison.OrdinalIgnoreCase));
    }

    // ── Word-alias collisions ────────────────────────────────────────────

    [Fact]
    public void Skill_named_quit_collides_with_exit_alias()
    {
        // "quit" is a word-alias of /exit.
        var skill = Phase5Helpers.Skill("quit");
        var builtIns = SlashCommandCatalog.CreateAll();
        var commands = SkillCommandRegistrar.BuildSkillCommands([skill], builtIns);
        Assert.DoesNotContain(commands, c => c.Name == "quit");
    }

    [Fact]
    public void Skill_named_quit_logs_collision_with_exit_built_in()
    {
        var logger = new Phase5Helpers.CapturingLogger();
        var skill = Phase5Helpers.Skill("quit", sourcePath: "/skills/quit/SKILL.md");
        var builtIns = SlashCommandCatalog.CreateAll();
        SkillCommandRegistrar.BuildSkillCommands([skill], builtIns, logger);

        var warning = logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Warning);
        Assert.NotNull(warning.Message);
        // The warning references the colliding built-in ("exit").
        Assert.Contains("exit", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── M1 name rejections don't trigger collision path ──────────────────

    [Fact]
    public void Slash_prefix_skill_does_not_produce_false_collision_warning()
    {
        var logger = new Phase5Helpers.CapturingLogger();
        // "/exit" has a slash prefix — must be rejected by name validation, not as a collision.
        var skill = Phase5Helpers.Skill("/exit");
        var builtIns = SlashCommandCatalog.CreateAll();
        SkillCommandRegistrar.BuildSkillCommands([skill], builtIns, logger);

        // One warning fired; must be about invalid name, not about collision.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }
}
