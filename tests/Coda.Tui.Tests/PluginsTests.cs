using Coda.Tui.Commands;
using Coda.Tui.Plugins;
using Coda.Tui.Repl;
using Coda.Tui.Skills;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class PluginLoaderTests : IDisposable
{
    private readonly string tempDir;

    public PluginLoaderTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-plugins-test-{Guid.NewGuid():N}");
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
    public void Load_plugin_with_full_plugin_json_parses_name_version_description()
    {
        // Arrange
        var pluginDir = CreateProjectPluginDir("foo");
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "foo", "version": "1.2.3", "description": "The foo plugin"}""");

        // Act
        var plugins = PluginLoader.Load(this.tempDir);

        // Assert
        Assert.Single(plugins);
        var plugin = plugins[0];
        Assert.Equal("foo", plugin.Name);
        Assert.Equal("1.2.3", plugin.Version);
        Assert.Equal("The foo plugin", plugin.Description);
        Assert.Equal(pluginDir, plugin.Directory);
    }

    [Fact]
    public void Load_plugin_without_plugin_json_is_skipped()
    {
        // A directory exists but has no plugin.json → should be skipped
        var pluginDir = Path.Combine(this.tempDir, ".coda", "plugins", "nojson");
        Directory.CreateDirectory(pluginDir);

        var plugins = PluginLoader.Load(this.tempDir);

        Assert.Empty(plugins);
    }

    [Fact]
    public void Load_missing_plugins_directory_returns_empty()
    {
        // No .coda/plugins dir at all
        var nonExistentDir = Path.Combine(this.tempDir, "no-such-project");

        var plugins = PluginLoader.Load(nonExistentDir);

        Assert.Empty(plugins);
    }

    [Fact]
    public void Load_project_plugin_overrides_user_plugin_by_name()
    {
        // Arrange user plugin
        var userBase = Path.Combine(this.tempDir, "user-home");
        Directory.CreateDirectory(userBase);
        var userPluginDir = Path.Combine(userBase, "plugins", "shared");
        Directory.CreateDirectory(userPluginDir);
        File.WriteAllText(
            Path.Combine(userPluginDir, "plugin.json"),
            """{"name": "shared", "version": "0.1.0", "description": "User version"}""");

        // Arrange project plugin
        var projectDir = Path.Combine(this.tempDir, "project");
        Directory.CreateDirectory(projectDir);
        var projectPluginDir = Path.Combine(projectDir, ".coda", "plugins", "shared");
        Directory.CreateDirectory(projectPluginDir);
        File.WriteAllText(
            Path.Combine(projectPluginDir, "plugin.json"),
            """{"name": "shared", "version": "2.0.0", "description": "Project version"}""");

        // Act
        var plugins = PluginLoader.Load(projectDir, userBase);

        // Assert: project overrides user
        Assert.Single(plugins);
        var plugin = plugins[0];
        Assert.Equal("shared", plugin.Name);
        Assert.Equal("2.0.0", plugin.Version);
        Assert.Equal("Project version", plugin.Description);
    }

    [Fact]
    public void Load_plugin_json_with_missing_fields_uses_defaults()
    {
        // plugin.json with only description — name defaults to dir name, version to "0.0.0"
        var pluginDir = CreateProjectPluginDir("my-plugin");
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"description": "Just a description"}""");

        var plugins = PluginLoader.Load(this.tempDir);

        Assert.Single(plugins);
        var plugin = plugins[0];
        Assert.Equal("my-plugin", plugin.Name);
        Assert.Equal("0.0.0", plugin.Version);
        Assert.Equal("Just a description", plugin.Description);
    }

    [Fact]
    public void Load_malformed_plugin_json_uses_dir_name_defaults()
    {
        // Malformed JSON → should not throw; plugin gets default values from dir name
        var pluginDir = CreateProjectPluginDir("broken");
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), "not valid json {{{{");

        var plugins = PluginLoader.Load(this.tempDir);

        Assert.Single(plugins);
        Assert.Equal("broken", plugins[0].Name);
        Assert.Equal("0.0.0", plugins[0].Version);
    }

    [Fact]
    public void SkillDirsFor_returns_plugin_skills_subdirectory_when_it_exists()
    {
        // Arrange plugin with a skills subdir
        var pluginDir = CreateProjectPluginDir("myplugin");
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "myplugin", "version": "1.0.0", "description": ""}""");

        var skillsDir = Path.Combine(pluginDir, "skills");
        Directory.CreateDirectory(skillsDir);

        // Act
        var dirs = PluginLoader.SkillDirsFor(this.tempDir);

        // Assert
        Assert.Single(dirs);
        Assert.Equal(skillsDir, dirs[0]);
    }

    [Fact]
    public void SkillDirsFor_omits_plugin_without_skills_directory()
    {
        // Plugin exists but has no skills/ subdir
        var pluginDir = CreateProjectPluginDir("noskilldirplugin");
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "noskilldirplugin", "version": "1.0.0", "description": ""}""");

        var dirs = PluginLoader.SkillDirsFor(this.tempDir);

        Assert.Empty(dirs);
    }

    private string CreateProjectPluginDir(string pluginName)
    {
        var dir = Path.Combine(this.tempDir, ".coda", "plugins", pluginName);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public sealed class PluginSkillLoaderTests : IDisposable
{
    private readonly string tempDir;

    public PluginSkillLoaderTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-pluginskills-test-{Guid.NewGuid():N}");
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
    public void SkillLoader_includes_skills_from_plugin_skills_directory()
    {
        // Arrange: plugin with a skill inside its skills/ subdir
        var pluginDir = Path.Combine(this.tempDir, ".coda", "plugins", "myplugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "myplugin", "version": "1.0.0", "description": "My plugin"}""");

        var skillDir = Path.Combine(pluginDir, "skills", "bar");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: bar\ndescription: Bar skill from plugin\n---\nBar skill body.\n");

        // Act
        var skills = SkillLoader.Load(this.tempDir, Path.Combine(this.tempDir, "_no_user"), Path.Combine(this.tempDir, "_no_claude"));

        // Assert
        Assert.Single(skills);
        Assert.Equal("bar", skills[0].Name);
        Assert.Equal("Bar skill from plugin", skills[0].Description);
        Assert.Contains("Bar skill body.", skills[0].Body);
    }

    [Fact]
    public void SkillLoader_project_skill_overrides_plugin_skill_with_same_name()
    {
        // Plugin skill "shared"
        var pluginDir = Path.Combine(this.tempDir, ".coda", "plugins", "myplugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "myplugin", "version": "1.0.0", "description": ""}""");

        var pluginSkillDir = Path.Combine(pluginDir, "skills", "shared");
        Directory.CreateDirectory(pluginSkillDir);
        File.WriteAllText(
            Path.Combine(pluginSkillDir, "SKILL.md"),
            "---\nname: shared\ndescription: Plugin version\n---\nPlugin body.\n");

        // Project skill "shared" (higher precedence)
        var projectSkillDir = Path.Combine(this.tempDir, ".coda", "skills", "shared");
        Directory.CreateDirectory(projectSkillDir);
        File.WriteAllText(
            Path.Combine(projectSkillDir, "SKILL.md"),
            "---\nname: shared\ndescription: Project version\n---\nProject body.\n");

        // Act
        var skills = SkillLoader.Load(this.tempDir, Path.Combine(this.tempDir, "_no_user"), Path.Combine(this.tempDir, "_no_claude"));

        // Assert: project wins
        Assert.Single(skills);
        Assert.Equal("shared", skills[0].Name);
        Assert.Equal("Project version", skills[0].Description);
        Assert.Contains("Project body.", skills[0].Body);
    }

    [Fact]
    public void SkillLoader_no_plugins_returns_normal_skills_unchanged()
    {
        // Arrange a regular project skill with no plugins present
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "normal");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: normal\ndescription: Normal skill\n---\nNormal body.\n");

        // Act
        var skills = SkillLoader.Load(this.tempDir, Path.Combine(this.tempDir, "_no_user"), Path.Combine(this.tempDir, "_no_claude"));

        // Assert: existing skill still found normally
        Assert.Single(skills);
        Assert.Equal("normal", skills[0].Name);
    }
}

public sealed class PluginsCommandTests : IDisposable
{
    private readonly string tempDir;

    public PluginsCommandTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-pluginscmd-test-{Guid.NewGuid():N}");
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
    public async Task PluginsCommand_lists_discovered_plugins()
    {
        // Arrange
        var pluginDir = Path.Combine(this.tempDir, ".coda", "plugins", "awesome");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "awesome", "version": "1.0.0", "description": "An awesome plugin"}""");

        var (console, context) = BuildContext(this.tempDir);
        var command = new PluginsCommand();

        // Act
        var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        // Assert
        Assert.False(result.ShouldExit);
        Assert.Null(result.PromptToRun);
        Assert.Contains("awesome", console.Output);
        Assert.Contains("1.0.0", console.Output);
        Assert.Contains("An awesome plugin", console.Output);
    }

    [Fact]
    public async Task PluginsCommand_empty_case_shows_hint_message()
    {
        // No .coda/plugins dir → nothing to load
        var (console, context) = BuildContext(this.tempDir);
        var command = new PluginsCommand();

        var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Null(result.PromptToRun);
        Assert.Contains("No plugins installed", console.Output);
        Assert.Contains(".coda/plugins", console.Output);
    }

    [Fact]
    public async Task PluginsCommand_plugin_name_with_brackets_does_not_throw()
    {
        // Plugin name with special markup characters — Theme.AccentMarkup escapes them
        var pluginDir = Path.Combine(this.tempDir, ".coda", "plugins", "bracket-plugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "[bracket-plugin]", "version": "0.1.0", "description": "Has [brackets]"}""");

        var (console, context) = BuildContext(this.tempDir);
        var command = new PluginsCommand();

        // Must not throw despite brackets in Name/Description
        var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("[bracket-plugin]", console.Output);
        Assert.Contains("[brackets]", console.Output);
    }

    [Fact]
    public async Task PluginsCommand_returns_Continue()
    {
        var (_, context) = BuildContext(this.tempDir);
        var command = new PluginsCommand();

        var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.False(result.ShouldExit);
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
            new HelpCommand(), new SkillsCommand(), new SkillCommand(), new PluginsCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}
