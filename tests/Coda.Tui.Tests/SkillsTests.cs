using Coda.Tui.Commands;
using Coda.Tui.Plugins;
using Coda.Tui.Repl;
using Coda.Tui.Skills;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Microsoft.Extensions.Logging;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class SkillLoaderTests : IDisposable
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => this.Entries.Add((logLevel, formatter(state, exception)));
    }

    private readonly string tempDir;

    public SkillLoaderTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-skills-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    [Fact]
    public void Load_skill_with_frontmatter_parses_name_description_and_body()
    {
        // Arrange
        var skillDir = CreateProjectSkillDir("foo");
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: my-skill\ndescription: Does something cool\n---\nThis is the skill body.\nIt has multiple lines.\n");

        // Act
        var skills = this.LoadIsolated();

        // Assert
        Assert.Single(skills);
        var skill = skills[0];
        Assert.Equal("my-skill", skill.Name);
        Assert.Equal("Does something cool", skill.Description);
        Assert.Contains("This is the skill body.", skill.Body);
        Assert.Contains("It has multiple lines.", skill.Body);
        // Body should not contain the frontmatter
        Assert.DoesNotContain("---", skill.Body);
    }

    [Fact]
    public void Load_skill_without_frontmatter_uses_dir_name_and_whole_file_as_body()
    {
        // Arrange
        var skillDir = CreateProjectSkillDir("my-dir-skill");
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "Just a plain skill body with no frontmatter.");

        // Act
        var skills = this.LoadIsolated();

        // Assert
        Assert.Single(skills);
        var skill = skills[0];
        Assert.Equal("my-dir-skill", skill.Name);
        Assert.Equal(string.Empty, skill.Description);
        Assert.Equal("Just a plain skill body with no frontmatter.", skill.Body);
    }

    [Fact]
    public void Load_project_skill_overrides_user_skill_with_same_name()
    {
        // Arrange user skill
        var userBase = Path.Combine(this.tempDir, "user-home");
        Directory.CreateDirectory(userBase);
        var userSkillDir = Path.Combine(userBase, "skills", "shared");
        Directory.CreateDirectory(userSkillDir);
        File.WriteAllText(
            Path.Combine(userSkillDir, "SKILL.md"),
            "---\nname: shared\ndescription: User version\n---\nUser body.\n");

        // Arrange project skill (in a sub-folder so WorkingDirectory doesn't overlap)
        var projectDir = Path.Combine(this.tempDir, "project");
        Directory.CreateDirectory(projectDir);
        var projectSkillDir = Path.Combine(projectDir, ".coda", "skills", "shared");
        Directory.CreateDirectory(projectSkillDir);
        File.WriteAllText(
            Path.Combine(projectSkillDir, "SKILL.md"),
            "---\nname: shared\ndescription: Project version\n---\nProject body.\n");

        // Act
        var skills = SkillLoader.Load(projectDir, userBase, Path.Combine(this.tempDir, "_no_claude"));

        // Assert
        Assert.Single(skills);
        var skill = skills[0];
        Assert.Equal("shared", skill.Name);
        Assert.Equal("Project version", skill.Description);
        Assert.Contains("Project body.", skill.Body);
    }

    [Fact]
    public void Load_missing_directories_returns_empty_list()
    {
        var nonExistentDir = Path.Combine(this.tempDir, "does-not-exist");

        var skills = SkillLoader.Load(nonExistentDir, Path.Combine(this.tempDir, "_no_user"), Path.Combine(this.tempDir, "_no_claude"));

        Assert.Empty(skills);
    }

    [Fact]
    public void Load_user_skills_without_project_override()
    {
        // Arrange only user skill
        var userBase = Path.Combine(this.tempDir, "user-home");
        Directory.CreateDirectory(userBase);
        var userSkillDir = Path.Combine(userBase, "skills", "user-only");
        Directory.CreateDirectory(userSkillDir);
        File.WriteAllText(
            Path.Combine(userSkillDir, "SKILL.md"),
            "User-only skill body.");

        var projectDir = Path.Combine(this.tempDir, "empty-project");
        Directory.CreateDirectory(projectDir);

        var skills = SkillLoader.Load(projectDir, userBase, Path.Combine(this.tempDir, "_no_claude"));

        Assert.Single(skills);
        Assert.Equal("user-only", skills[0].Name);
    }

    [Fact]
    public void Load_frontmatter_with_no_name_falls_back_to_dir_name()
    {
        var skillDir = CreateProjectSkillDir("fallback-name");
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\ndescription: Only description here\n---\nBody text.\n");

        var skills = this.LoadIsolated();

        Assert.Single(skills);
        Assert.Equal("fallback-name", skills[0].Name);
        Assert.Equal("Only description here", skills[0].Description);
        Assert.Contains("Body text.", skills[0].Body);
    }

    [Fact]
    public void Load_unreadable_skill_file_is_skipped_and_logged_at_debug()
    {
        // A valid skill plus one whose SKILL.md exists but is unreadable (held with an
        // exclusive, no-share lock) so File.ReadAllText throws IOException → the catch fires.
        CreateProjectSkillDir("good");
        File.WriteAllText(
            Path.Combine(this.tempDir, ".coda", "skills", "good", "SKILL.md"),
            "---\nname: good\ndescription: fine\n---\nbody\n");

        var badDir = CreateProjectSkillDir("bad");
        var badFile = Path.Combine(badDir, "SKILL.md");
        File.WriteAllText(badFile, "placeholder");

        using var lockHandle = new FileStream(badFile, FileMode.Open, FileAccess.Read, FileShare.None);

        var logger = new CapturingLogger();
        var skills = SkillLoader.Load(
            this.tempDir,
            userSkillsDir: Path.Combine(this.tempDir, "_no_user"),
            claudeSkillsDir: Path.Combine(this.tempDir, "_no_claude", "skills"),
            logger: logger);

        // Swallow semantics intact: the unreadable skill is omitted, the valid one survives.
        Assert.Single(skills);
        Assert.Equal("good", skills[0].Name);

        // The skipped skill is now observable at Debug.
        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("malformed/unreadable skill file"));
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("SKILL.md", entry.Message);
    }

    private string CreateProjectSkillDir(string skillName)
    {
        var dir = Path.Combine(this.tempDir, ".coda", "skills", skillName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Isolate from any real ~/.coda/skills and ~/.claude/skills on the test machine.
    private IReadOnlyList<SkillDefinition> LoadIsolated() =>
        SkillLoader.Load(
            this.tempDir,
            userSkillsDir: Path.Combine(this.tempDir, "_no_user"),
            claudeSkillsDir: Path.Combine(this.tempDir, "_no_claude", "skills"));

    private static void WriteSkill(string dir, string name, string description)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\nbody\n");
    }

    [Fact]
    public void Load_includes_claude_cli_skills_read_only()
    {
        var claudeSkills = Path.Combine(this.tempDir, "claude", "skills");
        WriteSkill(Path.Combine(claudeSkills, "shared"), "shared", "from claude");

        var skills = SkillLoader.Load(
            this.tempDir,
            userSkillsDir: Path.Combine(this.tempDir, "_no_user"),
            claudeSkillsDir: claudeSkills);

        Assert.Contains(skills, s => s.Name == "shared" && s.Description == "from claude");
    }

    [Fact]
    public void User_coda_skill_overrides_claude_skill_of_same_name()
    {
        var claudeSkills = Path.Combine(this.tempDir, "claude", "skills");
        WriteSkill(Path.Combine(claudeSkills, "dup"), "dup", "from claude");

        var userBase = Path.Combine(this.tempDir, "userbase");
        WriteSkill(Path.Combine(userBase, "skills", "dup"), "dup", "from coda");

        var skills = SkillLoader.Load(this.tempDir, userSkillsDir: userBase, claudeSkillsDir: claudeSkills);

        var dup = Assert.Single(skills, s => s.Name == "dup");
        Assert.Equal("from coda", dup.Description); // Coda's own skill wins over the Claude CLI's
    }
}

[Collection("SkillSourceEnv")]
public sealed class SkillsCommandTests : IDisposable
{
    private readonly string tempDir;
    private readonly SkillSourceEnvIsolation env;

    public SkillsCommandTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-skillscmd-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.env = new SkillSourceEnvIsolation(this.tempDir);
    }

    public void Dispose()
    {
        this.env.Dispose();
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SkillsCommand_lists_discovered_skills()
    {
        // Arrange a skill
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "review");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: review\ndescription: Code review assistant\n---\nReview my code carefully.\n");

        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillsCommand();

        // Act
        var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        // Assert
        Assert.False(result.ShouldExit);
        Assert.Null(result.PromptToRun);
        Assert.Contains("review", console.Output);
        Assert.Contains("Code review assistant", console.Output);
    }

    [Fact]
    public async Task SkillsCommand_empty_case_shows_hint_message()
    {
        // No .coda/skills dir → nothing to load
        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillsCommand();

        var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("No skills found", console.Output);
        Assert.Contains(".coda/skills", console.Output);
    }

    [Fact]
    public async Task SkillsCommand_skill_name_with_brackets_does_not_throw()
    {
        // A skill whose name contains '[' and ']' must not cause Spectre.Console markup
        // to throw — Theme.AccentMarkup/DimMarkup already call Markup.Escape internally,
        // so no double-escaping in the command is needed.
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "bracket-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: [bracket-skill]\ndescription: Has [brackets] in it\n---\nBody.\n");

        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillsCommand();

        // Must not throw despite brackets in Name/Description
        var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("[bracket-skill]", console.Output);
        Assert.Contains("[brackets]", console.Output);
    }

    private static (TestConsole Console, CommandContext Context) BuildContext(string workingDirectory)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude });

        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai", workingDirectory);
        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new HelpCommand(), new SkillsCommand(), new SkillCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}

[Collection("SkillSourceEnv")]
public sealed class SkillCommandTests : IDisposable
{
    private readonly string tempDir;
    private readonly SkillSourceEnvIsolation env;

    public SkillCommandTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-skillcmd-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.env = new SkillSourceEnvIsolation(this.tempDir);
    }

    public void Dispose()
    {
        this.env.Dispose();
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SkillCommand_with_name_returns_RunPrompt_with_skill_body()
    {
        // Arrange
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "greet");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: greet\ndescription: Greeting skill\n---\nSay hello to the user warmly.\n");

        var (_, context) = BuildContext(this.tempDir);
        var command = new SkillCommand();

        // Act
        var result = await command.ExecuteAsync(context, new[] { "greet" }, CancellationToken.None);

        // Assert
        Assert.False(result.ShouldExit);
        Assert.NotNull(result.PromptToRun);
        Assert.Contains("Say hello to the user warmly.", result.PromptToRun);
    }

    [Fact]
    public async Task SkillCommand_with_unknown_name_shows_error_and_no_prompt()
    {
        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillCommand();

        var result = await command.ExecuteAsync(context, new[] { "nonexistent" }, CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Null(result.PromptToRun);
        Assert.Contains("nonexistent", console.Output);
        Assert.Contains("not found", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SkillCommand_with_no_args_lists_skills()
    {
        // Arrange a skill so there's something to list
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "my-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "Skill body here.");

        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillCommand();

        var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Null(result.PromptToRun);
        Assert.Contains("my-skill", console.Output);
    }

    [Fact]
    public async Task SkillCommand_name_lookup_is_case_insensitive()
    {
        // Arrange
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "CasedSkill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: CasedSkill\ndescription: Testing case\n---\nThe body.\n");

        var (_, context) = BuildContext(this.tempDir);
        var command = new SkillCommand();

        var result = await command.ExecuteAsync(context, new[] { "casedskill" }, CancellationToken.None);

        Assert.NotNull(result.PromptToRun);
        Assert.Contains("The body.", result.PromptToRun);
    }

    [Fact]
    public async Task SkillCommand_unknown_name_lists_available_skills_in_error()
    {
        // Arrange a skill so "available" list is non-empty
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "existing");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "Existing skill body.");

        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillCommand();

        await command.ExecuteAsync(context, new[] { "wrong" }, CancellationToken.None);

        Assert.Contains("existing", console.Output);
    }

    private static (TestConsole Console, CommandContext Context) BuildContext(string workingDirectory)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude });

        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai", workingDirectory);
        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new HelpCommand(), new SkillsCommand(), new SkillCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}

public sealed class CommandResultTests
{
    [Fact]
    public void Continue_has_ShouldExit_false_and_no_prompt()
    {
        var result = CommandResult.Continue;

        Assert.False(result.ShouldExit);
        Assert.Null(result.PromptToRun);
    }

    [Fact]
    public void Exit_has_ShouldExit_true_and_no_prompt()
    {
        var result = CommandResult.Exit;

        Assert.True(result.ShouldExit);
        Assert.Null(result.PromptToRun);
    }

    [Fact]
    public void RunPrompt_has_ShouldExit_false_and_carries_prompt()
    {
        var result = CommandResult.RunPrompt("do something");

        Assert.False(result.ShouldExit);
        Assert.Equal("do something", result.PromptToRun);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SkillLoaderOriginAndPathTests — Origin and SourcePath stamped on each layer
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SkillLoaderOriginAndPathTests : IDisposable
{
    private readonly string tempDir;

    public SkillLoaderOriginAndPathTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-origin-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    [Fact]
    public void Project_skill_has_Origin_Project_and_SourcePath_set()
    {
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "project-skill");
        Directory.CreateDirectory(skillDir);
        var skillFile = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(skillFile,
            "---\nname: project-skill\ndescription: A project skill\n---\nBody.\n");

        var skills = SkillLoader.Load(
            this.tempDir,
            userSkillsDir: Path.Combine(this.tempDir, "_no_user"),
            claudeSkillsDir: Path.Combine(this.tempDir, "_no_claude"));

        var skill = Assert.Single(skills);
        Assert.Equal(SkillOrigin.Project, skill.Origin);
        Assert.Equal(skillFile, skill.SourcePath);
    }

    [Fact]
    public void User_skill_has_Origin_User_and_SourcePath_set()
    {
        var userBase = Path.Combine(this.tempDir, "user-home");
        var skillDir = Path.Combine(userBase, "skills", "user-skill");
        Directory.CreateDirectory(skillDir);
        var skillFile = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(skillFile,
            "---\nname: user-skill\ndescription: A user skill\n---\nBody.\n");

        var skills = SkillLoader.Load(
            Path.Combine(this.tempDir, "empty-project"),
            userSkillsDir: userBase,
            claudeSkillsDir: Path.Combine(this.tempDir, "_no_claude"));

        var skill = Assert.Single(skills);
        Assert.Equal(SkillOrigin.User, skill.Origin);
        Assert.Equal(skillFile, skill.SourcePath);
    }

    [Fact]
    public void Claude_skill_has_Origin_Claude_and_SourcePath_set()
    {
        var claudeSkillsDir = Path.Combine(this.tempDir, "claude-skills");
        var skillDir = Path.Combine(claudeSkillsDir, "claude-skill");
        Directory.CreateDirectory(skillDir);
        var skillFile = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(skillFile,
            "---\nname: claude-skill\ndescription: A claude skill\n---\nBody.\n");

        var skills = SkillLoader.Load(
            Path.Combine(this.tempDir, "empty-project"),
            userSkillsDir: Path.Combine(this.tempDir, "_no_user"),
            claudeSkillsDir: claudeSkillsDir);

        var skill = Assert.Single(skills);
        Assert.Equal(SkillOrigin.Claude, skill.Origin);
        Assert.Equal(skillFile, skill.SourcePath);
    }

    [Fact]
    public void Plugin_skill_has_Origin_Plugin_and_SourcePath_set()
    {
        var userBase = Path.Combine(this.tempDir, "user-home");
        var pluginDir = Path.Combine(userBase, "plugins", "test-plugin");
        var pluginSkillDir = Path.Combine(pluginDir, "skills", "plugin-skill");
        Directory.CreateDirectory(pluginSkillDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "test-plugin", "version": "1.0.0", "description": "test"}""");
        var skillFile = Path.Combine(pluginSkillDir, "SKILL.md");
        File.WriteAllText(skillFile,
            "---\nname: plugin-skill\ndescription: A plugin skill\n---\nBody.\n");

        var skills = SkillLoader.Load(
            Path.Combine(this.tempDir, "empty-project"),
            userSkillsDir: userBase,
            claudeSkillsDir: Path.Combine(this.tempDir, "_no_claude"));

        var skill = Assert.Single(skills, s => s.Name == "plugin-skill");
        Assert.Equal(SkillOrigin.Plugin, skill.Origin);
        Assert.Equal(skillFile, skill.SourcePath);
    }

    [Fact]
    public void Precedence_still_resolves_project_over_user()
    {
        // User skill
        var userBase = Path.Combine(this.tempDir, "user-home");
        var userSkillDir = Path.Combine(userBase, "skills", "shared");
        Directory.CreateDirectory(userSkillDir);
        File.WriteAllText(Path.Combine(userSkillDir, "SKILL.md"),
            "---\nname: shared\ndescription: from user\n---\nUser body.\n");

        // Project skill — higher precedence
        var projectDir = this.tempDir;
        var projectSkillDir = Path.Combine(projectDir, ".coda", "skills", "shared");
        Directory.CreateDirectory(projectSkillDir);
        File.WriteAllText(Path.Combine(projectSkillDir, "SKILL.md"),
            "---\nname: shared\ndescription: from project\n---\nProject body.\n");

        var skills = SkillLoader.Load(
            projectDir,
            userSkillsDir: userBase,
            claudeSkillsDir: Path.Combine(this.tempDir, "_no_claude"));

        var skill = Assert.Single(skills);
        Assert.Equal("from project", skill.Description);
        Assert.Equal(SkillOrigin.Project, skill.Origin);
    }

    [Fact]
    public void When_to_use_and_argument_hint_stamped_from_frontmatter()
    {
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "rich-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: rich-skill\ndescription: d\nwhen-to-use: use when X\nargument-hint: <file>\narguments:\n  - file\n---\nBody.\n");

        var skills = SkillLoader.Load(
            this.tempDir,
            userSkillsDir: Path.Combine(this.tempDir, "_no_user"),
            claudeSkillsDir: Path.Combine(this.tempDir, "_no_claude"));

        var skill = Assert.Single(skills);
        Assert.Equal("use when X", skill.WhenToUse);
        Assert.Equal("<file>", skill.ArgumentHint);
        Assert.Equal(["file"], skill.Arguments);
    }

    [Fact]
    public void Unknown_frontmatter_fields_stamped_in_unknown_fields()
    {
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "ext-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: ext-skill\ndescription: d\nmodel: claude-opus\n---\nBody.\n");

        var skills = SkillLoader.Load(
            this.tempDir,
            userSkillsDir: Path.Combine(this.tempDir, "_no_user"),
            claudeSkillsDir: Path.Combine(this.tempDir, "_no_claude"));

        var skill = Assert.Single(skills);
        Assert.True(skill.UnknownFields.ContainsKey("model"));
        Assert.Equal("claude-opus", skill.UnknownFields["model"]);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SkillsCommandSubcommandTests — /skills list, validate, new
// ─────────────────────────────────────────────────────────────────────────────

[Collection("SkillSourceEnv")]
public sealed class SkillsCommandSubcommandTests : IDisposable
{
    private readonly string tempDir;
    private readonly SkillSourceEnvIsolation env;

    public SkillsCommandSubcommandTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-skillssub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.env = new SkillSourceEnvIsolation(this.tempDir);
    }

    public void Dispose()
    {
        this.env.Dispose();
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Skills_list_subcommand_behaves_same_as_bare_skills()
    {
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "my-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: my-skill\ndescription: Test skill\n---\nBody.\n");

        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillsCommand();

        var result = await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("my-skill", console.Output);
    }

    [Fact]
    public async Task Skills_validate_on_good_file_shows_name_and_no_problems()
    {
        var skillDir = Path.Combine(this.tempDir, "validate-skill");
        Directory.CreateDirectory(skillDir);
        var skillFile = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(skillFile,
            "---\nname: validate-skill\ndescription: A real description\n---\nBody here.\n");

        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillsCommand();

        var result = await command.ExecuteAsync(context, ["validate", skillFile], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Null(result.PromptToRun);
        Assert.Contains("validate-skill", console.Output);
        // Problems row should say "none"
        Assert.Contains("none", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Skills_validate_on_directory_looks_for_SKILL_md_inside()
    {
        var skillDir = Path.Combine(this.tempDir, "dir-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: dir-skill\ndescription: ok\n---\nBody.\n");

        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillsCommand();

        await command.ExecuteAsync(context, ["validate", skillDir], CancellationToken.None);

        Assert.Contains("dir-skill", console.Output);
    }

    [Fact]
    public async Task Skills_validate_on_file_with_unknown_keys_shows_them()
    {
        var skillFile = Path.Combine(this.tempDir, "unknown-keys.md");
        File.WriteAllText(skillFile,
            "---\nname: uk\ndescription: d\nallowed-tools: [read_file]\nmodel: opus\n---\nBody.\n");

        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillsCommand();

        await command.ExecuteAsync(context, ["validate", skillFile], CancellationToken.None);

        // At least one of the unknown keys should appear in the output
        var output = console.Output;
        Assert.True(
            output.Contains("allowed-tools") || output.Contains("model"),
            $"Expected unknown key names in output; got: {output}");
    }

    [Fact]
    public async Task Skills_validate_on_missing_file_shows_error()
    {
        var missing = Path.Combine(this.tempDir, "does-not-exist", "SKILL.md");

        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillsCommand();

        var result = await command.ExecuteAsync(context, ["validate", missing], CancellationToken.None);

        Assert.False(result.ShouldExit);
        // Should mention the missing path
        Assert.Contains("not found", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Skills_new_creates_scaffold_SKILL_md()
    {
        var (_, context) = BuildContext(this.tempDir);
        var command = new SkillsCommand();

        await command.ExecuteAsync(context, ["new", "my-new-skill"], CancellationToken.None);

        var expected = Path.Combine(this.tempDir, ".coda", "skills", "my-new-skill", "SKILL.md");
        Assert.True(File.Exists(expected), $"Expected scaffold at {expected}");

        var content = File.ReadAllText(expected);
        Assert.Contains("name:", content);
        Assert.Contains("description:", content);
        Assert.Contains("my-new-skill", content);
    }

    [Fact]
    public async Task Skills_new_refuses_if_directory_already_exists()
    {
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "existing");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "placeholder");

        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillsCommand();

        await command.ExecuteAsync(context, ["new", "existing"], CancellationToken.None);

        // Should not overwrite — warn and do nothing
        Assert.Contains("existing", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("placeholder", File.ReadAllText(Path.Combine(skillDir, "SKILL.md")));
    }

    [Fact]
    public async Task Skills_new_rejects_path_traversal_name()
    {
        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillsCommand();

        await command.ExecuteAsync(context, ["new", "../evil"], CancellationToken.None);

        // Skill directory must NOT have been created outside .coda/skills/
        Assert.False(Directory.Exists(Path.Combine(this.tempDir, ".coda", "skills", "..", "evil")));
        // Output should mention invalid name
        Assert.Contains("invalid", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Skills_new_rejects_name_with_path_separator()
    {
        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillsCommand();

        // Use backslash — both separators must be rejected
        await command.ExecuteAsync(context, ["new", "a/b"], CancellationToken.None);

        Assert.Contains("invalid", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(this.tempDir, ".coda", "skills", "a")));
    }

    [Fact]
    public async Task Skills_shows_argument_hint_in_listing()
    {
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "hinted");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: hinted\ndescription: d\nargument-hint: <file>\n---\nBody.\n");

        var (console, context) = BuildContext(this.tempDir);
        var command = new SkillsCommand();

        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("<file>", console.Output);
    }

    private static (TestConsole Console, CommandContext Context) BuildContext(string workingDirectory)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude });
        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai", workingDirectory);
        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new HelpCommand(), new SkillsCommand(), new SkillCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SkillCommandArgsTests — /skill <name> arg1 arg2 substitution
// ─────────────────────────────────────────────────────────────────────────────

[Collection("SkillSourceEnv")]
public sealed class SkillCommandArgsTests : IDisposable
{
    private readonly string tempDir;
    private readonly SkillSourceEnvIsolation env;

    public SkillCommandArgsTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-skillargs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.env = new SkillSourceEnvIsolation(this.tempDir);
    }

    public void Dispose()
    {
        this.env.Dispose();
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SkillCommand_with_args_substitutes_positional_placeholders()
    {
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "translate");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: translate\ndescription: Translate text\narguments:\n  - lang\n  - text\n---\nTranslate '$text' to $lang.\n");

        var (_, context) = BuildContext(this.tempDir);
        var command = new SkillCommand();

        var result = await command.ExecuteAsync(context, ["translate", "French", "Hello world"], CancellationToken.None);

        Assert.NotNull(result.PromptToRun);
        Assert.Contains("French", result.PromptToRun);
        Assert.Contains("Hello world", result.PromptToRun);
    }

    [Fact]
    public async Task SkillCommand_skill_without_placeholders_returns_body_unchanged()
    {
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "plain");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: plain\ndescription: Plain skill\n---\nDo the thing.\n");

        var (_, context) = BuildContext(this.tempDir);
        var command = new SkillCommand();

        var result = await command.ExecuteAsync(context, ["plain", "unused-arg"], CancellationToken.None);

        Assert.NotNull(result.PromptToRun);
        Assert.Equal("Do the thing.", result.PromptToRun);
    }

    [Fact]
    public async Task SkillCommand_ARGUMENTS_placeholder_receives_all_trailing_args()
    {
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "echo");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: echo\ndescription: Echo args\n---\nArgs: $ARGUMENTS\n");

        var (_, context) = BuildContext(this.tempDir);
        var command = new SkillCommand();

        var result = await command.ExecuteAsync(context, ["echo", "one", "two", "three"], CancellationToken.None);

        Assert.NotNull(result.PromptToRun);
        Assert.Equal("Args: one two three", result.PromptToRun);
    }

    private static (TestConsole Console, CommandContext Context) BuildContext(string workingDirectory)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude });
        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai", workingDirectory);
        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new HelpCommand(), new SkillsCommand(), new SkillCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SkillCommandLiteralDollarTests — /skill <name> with literal-$ bodies and no args
// ─────────────────────────────────────────────────────────────────────────────

[Collection("SkillSourceEnv")]
public sealed class SkillCommandLiteralDollarTests : IDisposable
{
    private readonly string tempDir;
    private readonly SkillSourceEnvIsolation env;

    public SkillCommandLiteralDollarTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-skilldollar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.env = new SkillSourceEnvIsolation(this.tempDir);
    }

    public void Dispose()
    {
        this.env.Dispose();
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    /// <summary>
    /// When invoked with no arguments and the skill declares no arguments, bodies
    /// containing literal <c>$</c> sequences must be passed through byte-identical.
    /// </summary>
    [Theory]
    [InlineData("echo $HOME and $PATH")]
    [InlineData("Run: git rebase -i HEAD~$1")]
    [InlineData("PID is $$ in bash")]
    [InlineData("Price is $10 today")]
    [InlineData("Use $env:PATH on Windows")]
    public async Task SkillCommand_literal_dollar_body_with_no_args_returns_body_unchanged(
        string bodyContent)
    {
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "dollar-test");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            $"---\nname: dollar-test\ndescription: Test\n---\n{bodyContent}\n");

        var (_, context) = BuildContext(this.tempDir);
        var command = new SkillCommand();

        var result = await command.ExecuteAsync(context, ["dollar-test"], CancellationToken.None);

        Assert.NotNull(result.PromptToRun);
        Assert.Equal(bodyContent, result.PromptToRun);
    }

    [Fact]
    public async Task SkillCommand_substitution_still_occurs_when_invoke_args_supplied()
    {
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "sub-test");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: sub-test\ndescription: Test substitution\n---\nTranslate to $1\n");

        var (_, context) = BuildContext(this.tempDir);
        var command = new SkillCommand();

        var result = await command.ExecuteAsync(context, ["sub-test", "French"], CancellationToken.None);

        Assert.NotNull(result.PromptToRun);
        Assert.Equal("Translate to French", result.PromptToRun);
    }

    [Fact]
    public async Task SkillCommand_substitution_occurs_when_skill_declares_arguments_even_with_no_invoke_args()
    {
        // Skill declares arguments → Bind IS called even when the user supplies no invoke args.
        // $lang has no corresponding value → resolved to empty string.
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "declared-args");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: declared-args\ndescription: Test\narguments:\n  - lang\n---\nTranslate to $lang\n");

        var (_, context) = BuildContext(this.tempDir);
        var command = new SkillCommand();

        var result = await command.ExecuteAsync(context, ["declared-args"], CancellationToken.None);

        Assert.NotNull(result.PromptToRun);
        Assert.Equal("Translate to ", result.PromptToRun);
    }

    private static (TestConsole Console, CommandContext Context) BuildContext(string workingDirectory)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude });

        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai", workingDirectory);
        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new HelpCommand(), new SkillsCommand(), new SkillCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}

[CollectionDefinition("SkillSourceEnv", DisableParallelization = true)]
public sealed class SkillSourceEnvCollection { }

/// <summary>
/// Redirects the user (~/.coda/skills) and Claude (~/.claude/skills) skill sources to
/// nonexistent dirs (via CODA_USER_SKILLS_DIR / CODA_CLAUDE_SKILLS_DIR) so command-level
/// tests do not observe skills present on the test machine. Restores prior values on dispose.
/// </summary>
internal sealed class SkillSourceEnvIsolation : IDisposable
{
    private readonly string? priorClaude;
    private readonly string? priorUser;

    public SkillSourceEnvIsolation(string baseDir)
    {
        this.priorClaude = Environment.GetEnvironmentVariable("CODA_CLAUDE_SKILLS_DIR");
        this.priorUser = Environment.GetEnvironmentVariable("CODA_USER_SKILLS_DIR");
        Environment.SetEnvironmentVariable("CODA_CLAUDE_SKILLS_DIR", Path.Combine(baseDir, "_no_claude"));
        Environment.SetEnvironmentVariable("CODA_USER_SKILLS_DIR", Path.Combine(baseDir, "_no_user"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODA_CLAUDE_SKILLS_DIR", this.priorClaude);
        Environment.SetEnvironmentVariable("CODA_USER_SKILLS_DIR", this.priorUser);
    }
}
