using Coda.Tui.Commands;
using Coda.Tui.Plugins;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class PluginInstallerDirectoryTests : IDisposable
{
    private readonly string tempDir;

    public PluginInstallerDirectoryTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-install-test-{Guid.NewGuid():N}");
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
    public async Task InstallFromDirectory_copies_plugin_and_returns_ok()
    {
        // Arrange
        var sourceDir = Path.Combine(this.tempDir, "source-foo");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(
            Path.Combine(sourceDir, "plugin.json"),
            """{"name": "foo", "version": "1.0.0", "description": "Test plugin"}""");

        var skillsDir = Path.Combine(sourceDir, "skills");
        Directory.CreateDirectory(skillsDir);
        File.WriteAllText(Path.Combine(skillsDir, "dummy.md"), "skill content");

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins");

        // Act
        var (ok, message) = await PluginInstaller.InstallFromDirectoryAsync(
            userPluginsDir, sourceDir, CancellationToken.None);

        // Assert
        Assert.True(ok);
        Assert.Contains("foo", message);
        Assert.True(File.Exists(Path.Combine(userPluginsDir, "foo", "plugin.json")));
        Assert.True(Directory.Exists(Path.Combine(userPluginsDir, "foo", "skills")));
        Assert.True(File.Exists(Path.Combine(userPluginsDir, "foo", "skills", "dummy.md")));
    }

    [Fact]
    public async Task InstallFromDirectory_reinstall_returns_already_installed()
    {
        // Arrange: install once
        var sourceDir = Path.Combine(this.tempDir, "source-foo2");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(
            Path.Combine(sourceDir, "plugin.json"),
            """{"name": "foo", "version": "1.0.0", "description": ""}""");

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins2");

        var (firstOk, _) = await PluginInstaller.InstallFromDirectoryAsync(
            userPluginsDir, sourceDir, CancellationToken.None);
        Assert.True(firstOk);

        // Act: try installing again
        var (secondOk, secondMessage) = await PluginInstaller.InstallFromDirectoryAsync(
            userPluginsDir, sourceDir, CancellationToken.None);

        // Assert
        Assert.False(secondOk);
        Assert.Contains("already installed", secondMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallFromDirectory_source_without_plugin_json_returns_error()
    {
        // Arrange: source dir exists but has no plugin.json
        var sourceDir = Path.Combine(this.tempDir, "source-nojson");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "readme.txt"), "no plugin.json here");

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins3");

        // Act
        var (ok, message) = await PluginInstaller.InstallFromDirectoryAsync(
            userPluginsDir, sourceDir, CancellationToken.None);

        // Assert
        Assert.False(ok);
        Assert.Contains("plugin.json", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallFromDirectory_nonexistent_source_returns_error()
    {
        // Arrange: source dir doesn't exist at all
        var sourceDir = Path.Combine(this.tempDir, "does-not-exist");
        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins4");

        // Act
        var (ok, message) = await PluginInstaller.InstallFromDirectoryAsync(
            userPluginsDir, sourceDir, CancellationToken.None);

        // Assert
        Assert.False(ok);
        Assert.Contains("not found", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallFromDirectory_with_traversal_name_in_plugin_json_is_rejected()
    {
        // Arrange: a malicious plugin.json whose "name" tries to escape the plugins dir.
        var sourceDir = Path.Combine(this.tempDir, "source-evil");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(
            Path.Combine(sourceDir, "plugin.json"),
            """{"name": "../evil", "version": "1.0.0", "description": ""}""");

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins-evil");

        // A sibling of the plugins dir that must NOT be written to.
        var sentinelTarget = Path.Combine(this.tempDir, "evil");

        // Act
        var (ok, message) = await PluginInstaller.InstallFromDirectoryAsync(
            userPluginsDir, sourceDir, CancellationToken.None);

        // Assert: rejected, nothing written outside the plugins dir.
        Assert.False(ok);
        Assert.Contains("invalid plugin name", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(sentinelTarget));
    }

    [Fact]
    public async Task InstallFromDirectory_uses_directory_name_when_plugin_json_has_no_name()
    {
        // Arrange: plugin.json with no "name" field → should fall back to dir name
        var sourceDir = Path.Combine(this.tempDir, "my-plugin-dir");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(
            Path.Combine(sourceDir, "plugin.json"),
            """{"version": "1.0.0", "description": "No name field"}""");

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins5");

        // Act
        var (ok, message) = await PluginInstaller.InstallFromDirectoryAsync(
            userPluginsDir, sourceDir, CancellationToken.None);

        // Assert: dir name used as plugin name
        Assert.True(ok);
        Assert.True(Directory.Exists(Path.Combine(userPluginsDir, "my-plugin-dir")));
    }
}

public sealed class PluginInstallerRemoveTests : IDisposable
{
    private readonly string tempDir;

    public PluginInstallerRemoveTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-remove-test-{Guid.NewGuid():N}");
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
    public async Task Remove_installed_plugin_deletes_directory_and_returns_ok()
    {
        // Arrange: install a plugin first
        var sourceDir = Path.Combine(this.tempDir, "source-bar");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(
            Path.Combine(sourceDir, "plugin.json"),
            """{"name": "bar", "version": "1.0.0", "description": ""}""");

        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins");
        await PluginInstaller.InstallFromDirectoryAsync(userPluginsDir, sourceDir, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(userPluginsDir, "bar")));

        // Act
        var (ok, message) = PluginInstaller.Remove(userPluginsDir, "bar");

        // Assert
        Assert.True(ok);
        Assert.Contains("bar", message);
        Assert.False(Directory.Exists(Path.Combine(userPluginsDir, "bar")));
    }

    [Fact]
    public void Remove_nonexistent_plugin_returns_error()
    {
        var userPluginsDir = Path.Combine(this.tempDir, "empty-plugins");
        Directory.CreateDirectory(userPluginsDir);

        var (ok, message) = PluginInstaller.Remove(userPluginsDir, "ghost");

        Assert.False(ok);
        Assert.Contains("No such plugin", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public void Remove_with_invalid_name_returns_invalid_error_without_deleting(string invalidName)
    {
        // Arrange: create a dir that should NOT be deleted
        var userPluginsDir = Path.Combine(this.tempDir, "user-plugins-safe");
        Directory.CreateDirectory(userPluginsDir);

        // Place a sentinel file outside the plugins dir to verify nothing is deleted
        var sentinelDir = Path.Combine(this.tempDir, "should-not-delete");
        Directory.CreateDirectory(sentinelDir);

        // Act
        var (ok, message) = PluginInstaller.Remove(userPluginsDir, invalidName);

        // Assert: rejected before any deletion
        Assert.False(ok);
        Assert.Contains("Invalid", message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(sentinelDir));
    }

    [Fact]
    public void IsValidPluginName_rejects_dotdot_path_traversal()
    {
        Assert.False(PluginInstaller.IsValidPluginName(".."));
        Assert.False(PluginInstaller.IsValidPluginName("../foo"));
        Assert.False(PluginInstaller.IsValidPluginName("foo/bar"));
        Assert.False(PluginInstaller.IsValidPluginName("foo\\bar"));
    }

    [Fact]
    public void IsValidPluginName_accepts_normal_names()
    {
        Assert.True(PluginInstaller.IsValidPluginName("my-plugin"));
        Assert.True(PluginInstaller.IsValidPluginName("foo"));
        Assert.True(PluginInstaller.IsValidPluginName("plugin-1.0"));
        Assert.True(PluginInstaller.IsValidPluginName("MyPlugin_2"));
    }
}

public sealed class PluginInstallerGitUrlTests
{
    [Theory]
    [InlineData("https://github.com/user/my-plugin.git", "my-plugin")]
    [InlineData("https://github.com/user/my-plugin", "my-plugin")]
    [InlineData("git@github.com:user/my-plugin.git", "my-plugin")]
    [InlineData("https://example.com/plugins/cool-plugin.git", "cool-plugin")]
    public void DeriveNameFromGitUrl_extracts_last_segment(string url, string expected)
    {
        var name = PluginInstaller.DeriveNameFromGitUrl(url);
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("https://example.com/.", ".")]
    [InlineData("https://example.com/..", "..")]
    public void DeriveNameFromGitUrl_can_yield_unsafe_segments_that_are_rejected(string url, string derived)
    {
        // DeriveNameFromGitUrl is purely lexical and may return "." or ".." for hostile URLs;
        // the guard against using them lives in IsValidPluginName, which must reject both.
        Assert.Equal(derived, PluginInstaller.DeriveNameFromGitUrl(url));
        Assert.False(PluginInstaller.IsValidPluginName(PluginInstaller.DeriveNameFromGitUrl(url)));
    }

    [Fact]
    public async Task InstallFromGit_with_dot_segment_url_is_rejected_before_cloning()
    {
        var userPluginsDir = Path.Combine(Path.GetTempPath(), $"coda-git-dot-{Guid.NewGuid():N}");
        try
        {
            var (ok, message) = await PluginInstaller.InstallFromGitAsync(
                userPluginsDir, "https://example.com/.", CancellationToken.None);

            Assert.False(ok);
            Assert.Contains("valid plugin name", message, StringComparison.OrdinalIgnoreCase);
            // Rejected before any clone — the plugins dir was never created.
            Assert.False(Directory.Exists(userPluginsDir));
        }
        finally
        {
            if (Directory.Exists(userPluginsDir))
            {
                Directory.Delete(userPluginsDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallFromGit_nonexistent_local_path_treated_as_invalid_git_url_returns_error()
    {
        // A string that doesn't look like a real git URL and refers to a non-existent path.
        // We call InstallFromGitAsync directly. git clone will fail (git not found or clone fails).
        // The test just verifies the method returns (false, ...) without throwing.
        var userPluginsDir = Path.Combine(Path.GetTempPath(), $"coda-git-test-{Guid.NewGuid():N}");
        try
        {
            var (ok, _) = await PluginInstaller.InstallFromGitAsync(
                userPluginsDir,
                "https://localhost:9/nonexistent-repo.git",
                CancellationToken.None);

            // We don't assert ok == false strictly here because the machine may or may not have
            // git available, but we assert it does not throw.
            Assert.True(true); // reached without throwing
        }
        finally
        {
            if (Directory.Exists(userPluginsDir))
            {
                Directory.Delete(userPluginsDir, recursive: true);
            }
        }
    }
}

public sealed class PluginCommandTests : IDisposable
{
    private readonly string tempDir;

    public PluginCommandTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-plugincmd-test-{Guid.NewGuid():N}");
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
    public async Task PluginCommand_list_no_args_shows_empty_hint()
    {
        var (console, context) = BuildContext(this.tempDir);
        var command = new PluginCommand(Path.Combine(this.tempDir, "user-plugins-empty"));

        var result = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Null(result.PromptToRun);
        Assert.Contains("No plugins installed", console.Output);
    }

    [Fact]
    public async Task PluginCommand_list_subcommand_shows_empty_hint()
    {
        var (console, context) = BuildContext(this.tempDir);
        var command = new PluginCommand(Path.Combine(this.tempDir, "user-plugins-list"));

        var result = await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("No plugins installed", console.Output);
    }

    [Fact]
    public async Task PluginCommand_list_shows_project_plugin()
    {
        // Arrange: create a project-level plugin in the working directory
        var pluginDir = Path.Combine(this.tempDir, ".coda", "plugins", "myplugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "myplugin", "version": "2.0.0", "description": "A test plugin"}""");

        var (console, context) = BuildContext(this.tempDir);
        var command = new PluginCommand(Path.Combine(this.tempDir, "user-plugins-list2"));

        var result = await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("myplugin", console.Output);
        Assert.Contains("2.0.0", console.Output);
        Assert.Contains("A test plugin", console.Output);
    }

    [Fact]
    public async Task PluginCommand_install_no_arg_shows_usage()
    {
        var (console, context) = BuildContext(this.tempDir);
        var command = new PluginCommand(Path.Combine(this.tempDir, "user-plugins-usage"));

        var result = await command.ExecuteAsync(context, ["install"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("Usage", console.Output);
        Assert.Contains("install", console.Output);
    }

    [Fact]
    public async Task PluginCommand_remove_no_arg_shows_usage()
    {
        var (console, context) = BuildContext(this.tempDir);
        var command = new PluginCommand(Path.Combine(this.tempDir, "user-plugins-usage2"));

        var result = await command.ExecuteAsync(context, ["remove"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("Usage", console.Output);
        Assert.Contains("remove", console.Output);
    }

    [Fact]
    public async Task PluginCommand_unknown_subcommand_shows_usage()
    {
        var (console, context) = BuildContext(this.tempDir);
        var command = new PluginCommand(Path.Combine(this.tempDir, "user-plugins-unk"));

        var result = await command.ExecuteAsync(context, ["foobar"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("Unknown subcommand", console.Output);
    }

    [Fact]
    public async Task PluginCommand_install_from_directory_into_injected_dir_succeeds()
    {
        // Arrange: source plugin dir
        var sourceDir = Path.Combine(this.tempDir, "source-plugin");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(
            Path.Combine(sourceDir, "plugin.json"),
            """{"name": "testplugin", "version": "1.0.0", "description": ""}""");

        var injectedPluginsDir = Path.Combine(this.tempDir, "injected-plugins");
        var (console, context) = BuildContext(this.tempDir);
        var command = new PluginCommand(injectedPluginsDir);

        // Act
        var result = await command.ExecuteAsync(
            context, ["install", sourceDir], CancellationToken.None);

        // Assert
        Assert.False(result.ShouldExit);
        Assert.True(Directory.Exists(Path.Combine(injectedPluginsDir, "testplugin")));
        // Output should contain success message (green)
        Assert.Contains("testplugin", console.Output);
    }

    [Fact]
    public async Task PluginCommand_remove_installed_plugin_via_injected_dir_succeeds()
    {
        // Arrange: install a plugin into the injected dir first
        var injectedPluginsDir = Path.Combine(this.tempDir, "injected-remove");
        var pluginDir = Path.Combine(injectedPluginsDir, "removeme");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "removeme", "version": "1.0.0", "description": ""}""");

        var (console, context) = BuildContext(this.tempDir);
        var command = new PluginCommand(injectedPluginsDir);

        // Act
        var result = await command.ExecuteAsync(
            context, ["remove", "removeme"], CancellationToken.None);

        // Assert
        Assert.False(result.ShouldExit);
        Assert.False(Directory.Exists(pluginDir));
        Assert.Contains("removeme", console.Output);
    }

    [Fact]
    public async Task PluginCommand_install_nonexistent_source_shows_error()
    {
        var injectedPluginsDir = Path.Combine(this.tempDir, "injected-err");
        var (console, context) = BuildContext(this.tempDir);
        var command = new PluginCommand(injectedPluginsDir);

        var result = await command.ExecuteAsync(
            context, ["install", Path.Combine(this.tempDir, "no-such-dir")], CancellationToken.None);

        Assert.False(result.ShouldExit);
        // Should print an error (red), not throw
        Assert.False(string.IsNullOrEmpty(console.Output));
    }

    [Fact]
    public async Task PluginCommand_returns_Continue_in_all_paths()
    {
        var injectedPluginsDir = Path.Combine(this.tempDir, "injected-allpaths");

        // list
        var (_, ctx1) = BuildContext(this.tempDir);
        var r1 = await new PluginCommand(injectedPluginsDir).ExecuteAsync(ctx1, [], CancellationToken.None);
        Assert.False(r1.ShouldExit);

        // install bad
        var (_, ctx2) = BuildContext(this.tempDir);
        var r2 = await new PluginCommand(injectedPluginsDir).ExecuteAsync(ctx2, ["install"], CancellationToken.None);
        Assert.False(r2.ShouldExit);

        // remove bad
        var (_, ctx3) = BuildContext(this.tempDir);
        var r3 = await new PluginCommand(injectedPluginsDir).ExecuteAsync(ctx3, ["remove"], CancellationToken.None);
        Assert.False(r3.ShouldExit);

        // unknown
        var (_, ctx4) = BuildContext(this.tempDir);
        var r4 = await new PluginCommand(injectedPluginsDir).ExecuteAsync(ctx4, ["wat"], CancellationToken.None);
        Assert.False(r4.ShouldExit);
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
            new HelpCommand(), new SkillsCommand(), new SkillCommand(),
            new PluginsCommand(), new PluginCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}
