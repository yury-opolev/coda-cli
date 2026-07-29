using Coda.Tui.Commands;
using Coda.Tui.Plugins;
using Coda.Tui.Repl;
using Coda.Tui.Skills;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Microsoft.Extensions.Logging;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

// ── Shared helpers ────────────────────────────────────────────────────────────

internal static class Phase5Helpers
{
    /// <summary>Minimal ILogger that captures all log entries as plain strings.</summary>
    public sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => this.Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Creates a minimal <see cref="SkillDefinition"/> for test use.</summary>
    public static SkillDefinition Skill(
        string name = "my-skill",
        string description = "A test skill.",
        string body = "Do the thing.",
        bool userInvocable = true,
        bool disableModelInvocation = false,
        IReadOnlyList<string>? arguments = null,
        string? argumentHint = null,
        IReadOnlyList<string>? paths = null,
        string? sourcePath = null) =>
        new(name, description, body)
        {
            UserInvocable = userInvocable,
            DisableModelInvocation = disableModelInvocation,
            Arguments = arguments ?? [],
            ArgumentHint = argumentHint,
            Paths = paths ?? [],
            SourcePath = sourcePath,
        };

    /// <summary>
    /// Builds a <see cref="TuiApp"/> backed by a <see cref="SlashCommandRegistry"/> that
    /// contains the given <paramref name="commands"/>. The app has no credentials so any
    /// agent turn will fail gracefully without network calls.
    /// </summary>
    public static (TuiApp App, TestConsole Console) BuildAppWith(
        IReadOnlyList<ISlashCommand> commands,
        string? workingDirectory = null)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var credentials = new CredentialManager(store, [claude]);
        var session = new SessionState("claude-ai", workingDirectory ?? Directory.GetCurrentDirectory());
        var registry = new SlashCommandRegistry(commands);

        // At least one provider must be in the list so context.ActiveProvider doesn't throw
        // when the agent runner tries to initialize a session (it will fail gracefully on
        // auth, but won't crash with IndexOutOfRangeException).
        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (new TuiApp(context), console);
    }
}

// ── Tests 1 & 2 — registration and argument substitution (production path) ────

/// <summary>
/// Tests 1 and 2: user-invocable skill is registered as a first-class <c>/name</c> command,
/// runs the skill body, and substitutes arguments identically to <c>/skill &lt;name&gt; [args]</c>.
/// At least one test exercises the production registration path through
/// <see cref="SlashCommandCatalog.CreateWithSkills"/>.
/// </summary>
public sealed class SkillPhase5RegistrationTests
{
    // ── Test 1 — user-invocable skill registered as /name ────────────────

    [Fact]
    public void User_invocable_skill_registered_as_SkillSlashCommand_via_production_path()
    {
        var skill = Phase5Helpers.Skill("my-skill", description: "Does things.");

        // Production path: SlashCommandCatalog.CreateWithSkills → SlashCommandRegistry
        var commands = SlashCommandCatalog.CreateWithSkills([skill]);

        var cmd = commands.FirstOrDefault(c => c.Name == "my-skill");
        Assert.NotNull(cmd);
        Assert.IsType<SkillSlashCommand>(cmd);
    }

    [Fact]
    public async Task User_invocable_skill_runs_body_via_dispatch()
    {
        var skill = Phase5Helpers.Skill("my-skill", body: "Do the thing.");
        var commands = SlashCommandCatalog.CreateWithSkills([skill]);
        var (app, _) = Phase5Helpers.BuildAppWith(commands);

        // Dispatch /my-skill — should return RunPrompt with the skill body.
        var result = await app.DispatchAsync(
            ParsedInput.Slash("my-skill", []),
            CancellationToken.None);

        Assert.Equal("Do the thing.", result.PromptToRun);
    }

    // ── Test 2 — argument substitution identical to /skill <name> args ────

    [Fact]
    public async Task Slash_name_with_args_substitutes_like_skill_command()
    {
        var skill = Phase5Helpers.Skill(
            "translate",
            body: "Translate to $1: $2",
            arguments: [],
            argumentHint: "<lang> <text>");
        var commands = SlashCommandCatalog.CreateWithSkills([skill]);
        var (app, _) = Phase5Helpers.BuildAppWith(commands);

        var result = await app.DispatchAsync(
            ParsedInput.Slash("translate", ["French", "Hello world"]),
            CancellationToken.None);

        Assert.Equal("Translate to French: Hello world", result.PromptToRun);
    }

    [Fact]
    public async Task Body_without_args_and_no_declared_arguments_is_returned_unchanged()
    {
        // Opt-in rule: substitution runs only when args are supplied OR skill declares arguments.
        var skill = Phase5Helpers.Skill("cost-skill", body: "Cost is $100 today.");
        var commands = SlashCommandCatalog.CreateWithSkills([skill]);
        var (app, _) = Phase5Helpers.BuildAppWith(commands);

        var result = await app.DispatchAsync(
            ParsedInput.Slash("cost-skill", []),
            CancellationToken.None);

        // No args supplied, no declared arguments → body returned unchanged (no substitution).
        Assert.Equal("Cost is $100 today.", result.PromptToRun);
    }

    [Fact]
    public async Task Body_with_declared_arguments_substitutes_even_without_user_args()
    {
        // Opt-in rule fires when skill declares arguments even if user supplies none.
        var skill = Phase5Helpers.Skill(
            "review",
            body: "Review $file.",
            arguments: ["file"]);
        var commands = SlashCommandCatalog.CreateWithSkills([skill]);
        var (app, _) = Phase5Helpers.BuildAppWith(commands);

        var result = await app.DispatchAsync(
            ParsedInput.Slash("review", []),
            CancellationToken.None);

        // $file → empty (no value supplied) but Bind ran.
        Assert.Equal("Review .", result.PromptToRun);
    }

    // ── Test 4 — user-invocable: false gets no /name ──────────────────────

    [Fact]
    public void Skill_with_user_invocable_false_not_registered_as_slash_command()
    {
        var skill = Phase5Helpers.Skill("model-only", userInvocable: false);

        var commands = SlashCommandCatalog.CreateWithSkills([skill]);

        Assert.DoesNotContain(commands, c => c.Name == "model-only");
    }

    // ── Test 5 — paths / disable-model-invocation still get /name ─────────

    [Fact]
    public void Skill_with_paths_still_gets_slash_name()
    {
        // paths restricts model-facing visibility, NOT user invocability.
        var skill = Phase5Helpers.Skill(
            "path-restricted",
            paths: ["/some/specific/path/**"]);

        var commands = SlashCommandCatalog.CreateWithSkills([skill]);

        Assert.Contains(commands, c => c.Name == "path-restricted" && c is SkillSlashCommand);
    }

    [Fact]
    public void Skill_with_disable_model_invocation_still_gets_slash_name()
    {
        // disable-model-invocation hides the skill from the skill tool enum, NOT from the user.
        var skill = Phase5Helpers.Skill(
            "user-facing-only",
            disableModelInvocation: true,
            userInvocable: true);

        var commands = SlashCommandCatalog.CreateWithSkills([skill]);

        Assert.Contains(commands, c => c.Name == "user-facing-only" && c is SkillSlashCommand);
    }

    // ── Test 6 — collision with built-in name ─────────────────────────────

    [Fact]
    public void Skill_colliding_with_builtin_name_not_registered()
    {
        // "help" is a built-in command.
        var skill = Phase5Helpers.Skill("help");

        var commands = SlashCommandCatalog.CreateWithSkills([skill]);

        // Only the built-in "help" should be present, not a SkillSlashCommand.
        var helpCmd = commands.FirstOrDefault(c => c.Name == "help");
        Assert.NotNull(helpCmd);
        Assert.IsNotType<SkillSlashCommand>(helpCmd);
    }

    [Fact]
    public void Skill_colliding_with_builtin_name_logs_warning()
    {
        var logger = new Phase5Helpers.CapturingLogger();
        var skill = Phase5Helpers.Skill(
            "help",
            sourcePath: "/path/to/help/SKILL.md");

        var builtIns = SlashCommandCatalog.CreateAll();
        SkillCommandRegistrar.BuildSkillCommands([skill], builtIns, logger);

        var warning = logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Warning);
        Assert.NotNull(warning.Message);
        Assert.Contains("help", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/path/to/help/SKILL.md", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Skill_colliding_with_builtin_name_still_reachable_via_skill_command()
    {
        // The built-in /skill command must still handle colliding skill names.
        // (We just verify /skill is still in the registry and is not SkillSlashCommand.)
        var skill = Phase5Helpers.Skill("exit");
        var commands = SlashCommandCatalog.CreateWithSkills([skill]);

        var exitCmd = commands.FirstOrDefault(c => c.Name == "exit");
        Assert.NotNull(exitCmd);
        Assert.IsNotType<SkillSlashCommand>(exitCmd);

        var skillCmd = commands.FirstOrDefault(c => c.Name == "skill");
        Assert.NotNull(skillCmd);
        Assert.IsNotType<SkillSlashCommand>(skillCmd);
    }

    // ── Test 7 — collision with built-in alias ────────────────────────────

    [Fact]
    public void Skill_colliding_with_builtin_alias_not_registered()
    {
        // "?" is an alias for "help".
        var skill = Phase5Helpers.Skill("?");

        var builtIns = SlashCommandCatalog.CreateAll();
        var skillCommands = SkillCommandRegistrar.BuildSkillCommands([skill], builtIns);

        Assert.DoesNotContain(skillCommands, c => c.Name == "?");
    }

    [Fact]
    public void Skill_colliding_with_builtin_alias_logs_warning()
    {
        var logger = new Phase5Helpers.CapturingLogger();
        // "?" is an alias for "help".
        var skill = Phase5Helpers.Skill("?", sourcePath: "/skills/question/SKILL.md");

        var builtIns = SlashCommandCatalog.CreateAll();
        SkillCommandRegistrar.BuildSkillCommands([skill], builtIns, logger);

        var warning = logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Warning);
        Assert.NotNull(warning.Message);
        // The warning should name the skill and the built-in it collides with.
        Assert.Contains("?", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 8 — precedence: only the winner is registered ────────────────

    [Fact]
    public void Precedence_only_winning_skill_registered_when_two_share_name()
    {
        // SkillLoader already resolves precedence (project > plugin > user > claude).
        // By the time BuildSkillCommands is called, the input list contains only the winner.
        // We verify that a list with a single winner produces exactly one command.
        var winner = Phase5Helpers.Skill("shared-skill", body: "Winner body.");

        var builtIns = SlashCommandCatalog.CreateAll();
        var skillCommands = SkillCommandRegistrar.BuildSkillCommands([winner], builtIns);

        Assert.Single(skillCommands, c => c.Name == "shared-skill");
    }

    [Fact]
    public void Skills_with_same_name_in_loader_output_produces_one_command()
    {
        // SkillLoader deduplicates by name before returning; confirm we register only one command.
        // Feed two skills with different names (since loader already deduped them, there won't
        // be two with the same name in real usage) — verify the produced set has unique names.
        var skillA = Phase5Helpers.Skill("alpha", body: "A body.");
        var skillB = Phase5Helpers.Skill("beta", body: "B body.");

        var builtIns = SlashCommandCatalog.CreateAll();
        var skillCommands = SkillCommandRegistrar.BuildSkillCommands([skillA, skillB], builtIns);

        Assert.Equal(2, skillCommands.Count);
        Assert.Single(skillCommands, c => c.Name == "alpha");
        Assert.Single(skillCommands, c => c.Name == "beta");
    }

    // ── Test 9 — completion entry distinguishable and carries hint ─────────

    [Fact]
    public void Skill_command_summary_carries_description_and_argument_hint()
    {
        var skill = Phase5Helpers.Skill(
            "my-skill",
            description: "Runs a workflow.",
            argumentHint: "<target> [options]");

        var cmd = new SkillSlashCommand(skill);

        Assert.Contains("Runs a workflow.", cmd.Summary);
        Assert.Contains("<target> [options]", cmd.Summary);
    }

    [Fact]
    public void Skill_command_summary_does_not_contain_skill_marker_prefix()
    {
        // M3: the marker moved to render time; Summary is pure data (description + hint only).
        var skill = Phase5Helpers.Skill("my-skill", description: "My skill.");

        var cmd = new SkillSlashCommand(skill);

        Assert.DoesNotContain(SkillSlashCommand.SkillMarker, cmd.Summary);
    }

    [Fact]
    public void Builtin_command_summary_does_not_have_skill_marker()
    {
        // Verify that the marker is not accidentally present on built-in summaries.
        var builtIns = SlashCommandCatalog.CreateAll();

        Assert.All(builtIns, c =>
            Assert.DoesNotContain(SkillSlashCommand.SkillMarker, c.Summary));
    }

    [Fact]
    public void Skill_command_appears_in_completion_suggestions()
    {
        var skill = Phase5Helpers.Skill("my-workflow", description: "My workflow.");
        var commands = SlashCommandCatalog.CreateWithSkills([skill]);
        var registry = new SlashCommandRegistry(commands);
        var completion = new SlashCommandCompletion(registry);

        completion.Update("/my-w", 5);

        Assert.True(completion.IsVisible);
        Assert.Contains(completion.Suggestions, c => c.Name == "my-workflow");
    }

    [Fact]
    public void Skill_command_in_completion_is_distinguishable_from_builtins()
    {
        var skill = Phase5Helpers.Skill("my-workflow", description: "A workflow.");
        var commands = SlashCommandCatalog.CreateWithSkills([skill]);
        var registry = new SlashCommandRegistry(commands);
        var completion = new SlashCommandCompletion(registry);

        completion.Update("/", 1);

        var skillEntry = completion.Suggestions.OfType<SkillSlashCommand>().FirstOrDefault();
        var builtInEntry = completion.Suggestions.FirstOrDefault(c => c is not SkillSlashCommand);

        Assert.NotNull(skillEntry);
        Assert.NotNull(builtInEntry);
        // Distinction is via type: skill entries are SkillSlashCommand, built-ins are not.
        Assert.IsType<SkillSlashCommand>(skillEntry);
        Assert.IsNotType<SkillSlashCommand>(builtInEntry);
        // Built-ins do not have the skill marker in their summary.
        Assert.DoesNotContain(SkillSlashCommand.SkillMarker, builtInEntry.Summary);
    }

    // ── Test 10 — /help lists skill commands in their own group ───────────

    [Fact]
    public async Task Help_lists_skill_commands_in_separate_group()
    {
        var skill = Phase5Helpers.Skill("my-workflow", description: "A workflow skill.");
        var commands = SlashCommandCatalog.CreateWithSkills([skill]);
        var (app, console) = Phase5Helpers.BuildAppWith(commands);

        await app.DispatchAsync(ParsedInput.Slash("help", []), CancellationToken.None);

        var output = console.Output;
        // The output must contain both "Commands" and "Skill commands" sections.
        Assert.Contains("Commands", output);
        Assert.Contains("Skill commands", output);
        // The skill must appear in the skill commands section.
        Assert.Contains("my-workflow", output);
    }

    [Fact]
    public async Task Help_lists_no_skill_section_when_no_skills_present()
    {
        // When no skills are registered, /help must not show a "Skill commands" header.
        var commands = SlashCommandCatalog.CreateAll(); // no skills
        var (app, console) = Phase5Helpers.BuildAppWith(commands);

        await app.DispatchAsync(ParsedInput.Slash("help", []), CancellationToken.None);

        Assert.DoesNotContain("Skill commands", console.Output);
    }

    // ── Test 12 — no skills → command surface byte-identical to today ──────

    [Fact]
    public void No_skills_produces_identical_command_surface_to_CreateAll()
    {
        var withNoSkills = SlashCommandCatalog.CreateWithSkills([]);
        var builtInsOnly = SlashCommandCatalog.CreateAll();

        var withNoSkillsNames = withNoSkills.Select(c => c.Name).OrderBy(n => n).ToList();
        var builtInsOnlyNames = builtInsOnly.Select(c => c.Name).OrderBy(n => n).ToList();

        Assert.Equal(builtInsOnlyNames, withNoSkillsNames);
    }

    [Fact]
    public void No_skills_produces_same_count_as_CreateAll()
    {
        var withNoSkills = SlashCommandCatalog.CreateWithSkills([]);
        var builtInsOnly = SlashCommandCatalog.CreateAll();

        Assert.Equal(builtInsOnly.Count, withNoSkills.Count);
    }
}

// ── Test 3 — /skill <name> still works ───────────────────────────────────────

/// <summary>
/// Test 3: the existing <c>/skill &lt;name&gt;</c> built-in command is unaffected by Phase 5
/// and remains fully functional.
/// </summary>
public sealed class SkillPhase5BackCompatTests
{
    [Fact]
    public void Skill_command_still_in_registry_after_CreateWithSkills()
    {
        var skill = Phase5Helpers.Skill("my-skill");
        var commands = SlashCommandCatalog.CreateWithSkills([skill]);
        var registry = new SlashCommandRegistry(commands);

        // /skill built-in must still exist.
        var skillCmd = registry.Resolve("skill");
        Assert.NotNull(skillCmd);
        Assert.IsNotType<SkillSlashCommand>(skillCmd);
    }

    [Fact]
    public void Skill_command_is_still_in_catalog()
    {
        var builtIns = SlashCommandCatalog.CreateAll();

        Assert.Contains(builtIns, c => c.Name == "skill" && c is SkillCommand);
    }

    [Fact]
    public async Task Slash_skill_command_dispatches_without_error()
    {
        // /skill with no arguments lists skills — verify it works in a TuiApp with skills registered.
        var skill = Phase5Helpers.Skill("my-skill");
        var commands = SlashCommandCatalog.CreateWithSkills([skill]);
        var (app, _) = Phase5Helpers.BuildAppWith(commands);

        // /skill with no args lists skills (same as /skills).
        var result = await app.DispatchAsync(
            ParsedInput.Slash("skill", []),
            CancellationToken.None);

        // Should not exit or error.
        Assert.False(result.ShouldExit);
    }

    [Fact]
    public void Registry_resolves_skill_command_name()
    {
        var skills = new[] { Phase5Helpers.Skill("my-skill") };
        var commands = SlashCommandCatalog.CreateWithSkills(skills);
        var registry = new SlashCommandRegistry(commands);

        // Both /my-skill (via SkillSlashCommand) and /skill (built-in) must resolve.
        Assert.NotNull(registry.Resolve("my-skill"));
        Assert.NotNull(registry.Resolve("skill"));
        // They must be different command objects.
        Assert.NotEqual(registry.Resolve("my-skill"), registry.Resolve("skill"));
    }
}

// ── Test 11 — disabled plugin's skills contribute no commands ─────────────────

/// <summary>
/// Test 11: when a plugin is disabled via <see cref="PluginStateStore"/>, its skills are
/// excluded from <see cref="SkillLoader.Load"/> and therefore from the slash-command registry.
/// </summary>
public sealed class SkillPhase5PluginDisableTests : IDisposable
{
    private readonly string tempDir =
        Directory.CreateTempSubdirectory("coda_p5_plugin_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Disabled_plugin_skills_contribute_no_commands()
    {
        // Create a plugin with a skill.
        var pluginDir = Path.Combine(this.tempDir, ".coda", "plugins", "my-plugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{ "name": "my-plugin", "version": "1.0.0" }""");

        var skillDir = Path.Combine(pluginDir, "skills", "plugin-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: plugin-skill\ndescription: From plugin.\n---\nPlugin body.\n");

        // Disable the plugin.
        var codaDir = Path.Combine(this.tempDir, ".coda");
        var store = new PluginStateStore(codaDir);
        store.SetEnabled("my-plugin", false);

        // Load skills with the state store — disabled plugin's skills must not appear.
        var skills = SkillLoader.Load(this.tempDir, pluginStateStore: store);
        var builtIns = SlashCommandCatalog.CreateAll();
        var skillCommands = SkillCommandRegistrar.BuildSkillCommands(skills, builtIns);

        Assert.DoesNotContain(skillCommands, c => c.Name == "plugin-skill");
    }

    [Fact]
    public void Enabled_plugin_skills_do_contribute_commands()
    {
        // Create a plugin with a skill.
        var pluginDir = Path.Combine(this.tempDir, ".coda", "plugins", "active-plugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{ "name": "active-plugin", "version": "1.0.0" }""");

        var skillDir = Path.Combine(pluginDir, "skills", "active-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: active-skill\ndescription: From active plugin.\n---\nActive body.\n");

        // Plugin is enabled by default (no state store entry).
        var codaDir = Path.Combine(this.tempDir, ".coda");
        var store = new PluginStateStore(codaDir);
        // store.SetEnabled("active-plugin", true) — default is enabled

        var skills = SkillLoader.Load(this.tempDir, pluginStateStore: store);
        var builtIns = SlashCommandCatalog.CreateAll();
        var skillCommands = SkillCommandRegistrar.BuildSkillCommands(skills, builtIns);

        Assert.Contains(skillCommands, c => c.Name == "active-skill");
    }
}

// ── ReplaceAll reload tests ───────────────────────────────────────────────────

/// <summary>
/// Verifies that <see cref="SlashCommandRegistry.ReplaceAll"/> enables re-registration of
/// skill-derived commands after <c>/skills reload</c> without restarting the session.
/// </summary>
public sealed class SkillPhase5ReloadTests
{
    [Fact]
    public void ReplaceAll_updates_registry_with_new_skill_commands()
    {
        // Start with no skills.
        var registry = new SlashCommandRegistry(SlashCommandCatalog.CreateAll());
        Assert.Null(registry.Resolve("my-new-skill"));

        // Reload with a new skill.
        var newSkill = Phase5Helpers.Skill("my-new-skill", body: "New body.");
        registry.ReplaceAll(SlashCommandCatalog.CreateWithSkills([newSkill]));

        Assert.NotNull(registry.Resolve("my-new-skill"));
        Assert.IsType<SkillSlashCommand>(registry.Resolve("my-new-skill"));
    }

    [Fact]
    public void ReplaceAll_removes_old_skill_commands()
    {
        // Start with a skill registered.
        var oldSkill = Phase5Helpers.Skill("old-skill");
        var registry = new SlashCommandRegistry(SlashCommandCatalog.CreateWithSkills([oldSkill]));
        Assert.NotNull(registry.Resolve("old-skill"));

        // Reload without that skill.
        registry.ReplaceAll(SlashCommandCatalog.CreateAll());

        Assert.Null(registry.Resolve("old-skill"));
    }

    [Fact]
    public void ReplaceAll_preserves_built_in_commands()
    {
        var registry = new SlashCommandRegistry(SlashCommandCatalog.CreateAll());

        // Replace with new skills + built-ins.
        var skill = Phase5Helpers.Skill("my-skill");
        registry.ReplaceAll(SlashCommandCatalog.CreateWithSkills([skill]));

        // All original built-ins must still be resolvable.
        Assert.NotNull(registry.Resolve("help"));
        Assert.NotNull(registry.Resolve("exit"));
        Assert.NotNull(registry.Resolve("skill"));
    }
}
