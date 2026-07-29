using Coda.Tui.Plugins;
using Coda.Tui.Ui.Prompts;
using LlmAuth;

namespace Coda.Tui.Tests;

// ============================================================================
// Test 1 — Full manifest parses; unknown top-level fields ignored; missing name rejected
// ============================================================================

public sealed class PluginManifestParserTests : IDisposable
{
    private readonly string tempDir;

    public PluginManifestParserTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-manifest-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir)) Directory.Delete(this.tempDir, recursive: true);
    }

    [Fact]
    public void Parse_fullManifest_allFieldsSet()
    {
        var json = """
            {
                "name": "my-plugin",
                "version": "2.3.4",
                "description": "A test plugin",
                "displayName": "My Plugin",
                "author": "Alice",
                "homepage": "https://example.com",
                "repository": "https://github.com/alice/my-plugin",
                "license": "MIT",
                "keywords": ["foo", "bar"],
                "defaultEnabled": false,
                "skills": ["extras/skills"],
                "commands": "src/commands",
                "agents": "src/agents",
                "outputStyles": "styles",
                "themes": "themes",
                "userConfig": [
                    { "key": "API_KEY", "type": "secret", "label": "API Key", "required": true },
                    { "key": "mode", "type": "choice", "options": ["fast", "slow"], "default": "fast" }
                ],
                "dependencies": { "base-plugin": "^1.0.0" }
            }
            """;

        var dir = Path.Combine(this.tempDir, "my-plugin");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "extras", "skills"));
        Directory.CreateDirectory(Path.Combine(dir, "src", "commands"));
        Directory.CreateDirectory(Path.Combine(dir, "src", "agents"));
        Directory.CreateDirectory(Path.Combine(dir, "styles"));
        Directory.CreateDirectory(Path.Combine(dir, "themes"));

        var manifest = PluginManifestParser.Parse(json, dir);

        Assert.Equal("my-plugin", manifest.Name);
        Assert.Equal("2.3.4", manifest.Version);
        Assert.Equal("A test plugin", manifest.Description);
        Assert.Equal("My Plugin", manifest.DisplayName);
        Assert.Equal("Alice", manifest.Author);
        Assert.Equal("https://example.com", manifest.Homepage);
        Assert.Equal("MIT", manifest.License);
        Assert.Equal(["foo", "bar"], manifest.Keywords);
        Assert.False(manifest.DefaultEnabled);
        Assert.Equal(["extras/skills"], manifest.Skills);
        Assert.Equal("src/commands", manifest.Commands);
        Assert.Equal("src/agents", manifest.Agents);
        Assert.Equal("styles", manifest.OutputStyles);
        Assert.Equal("themes", manifest.Themes);
        Assert.Equal(2, manifest.UserConfig.Count);
        Assert.Equal("API_KEY", manifest.UserConfig[0].Key);
        Assert.Equal(UserConfigFieldType.Secret, manifest.UserConfig[0].Type);
        Assert.True(manifest.UserConfig[0].Required);
        Assert.Equal("mode", manifest.UserConfig[1].Key);
        Assert.Equal(UserConfigFieldType.Choice, manifest.UserConfig[1].Type);
        Assert.Equal(["fast", "slow"], manifest.UserConfig[1].Options);
        Assert.Equal("fast", manifest.UserConfig[1].Default);
        Assert.Single(manifest.Dependencies);
        Assert.Equal("base-plugin", manifest.Dependencies[0].PluginName);
        Assert.Equal("^1.0.0", manifest.Dependencies[0].SemVerRange);
    }

    [Fact]
    public void Parse_unknownTopLevelFields_ignoredWithoutError()
    {
        var json = """
            {
                "name": "plugin-x",
                "unknownField1": "some value",
                "vscode:engines": { "vscode": "^1.60.0" },
                "private": true,
                "scripts": { "test": "jest" }
            }
            """;

        var dir = Path.Combine(this.tempDir, "plugin-x");
        Directory.CreateDirectory(dir);

        var manifest = PluginManifestParser.Parse(json, dir);

        Assert.Equal("plugin-x", manifest.Name);
    }

    [Fact]
    public void Parse_missingName_throwsPluginManifestParseException()
    {
        var json = """{ "version": "1.0.0", "description": "no name here" }""";
        var dir = Path.Combine(this.tempDir, "no-name");
        Directory.CreateDirectory(dir);

        var ex = Assert.Throws<PluginManifestParseException>(() =>
            PluginManifestParser.Parse(json, dir));

        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_emptyName_throwsPluginManifestParseException()
    {
        var json = """{ "name": "", "version": "1.0.0" }""";
        var dir = Path.Combine(this.tempDir, "empty-name");
        Directory.CreateDirectory(dir);

        Assert.Throws<PluginManifestParseException>(() =>
            PluginManifestParser.Parse(json, dir));
    }
}

// ============================================================================
// Test 2 — skills adds to convention directory; commands/agents/outputStyles/themes replace
// ============================================================================

public sealed class PluginManifestDirectoryBehaviorTests : IDisposable
{
    private readonly string tempDir;

    public PluginManifestDirectoryBehaviorTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-manifest-dir-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir)) Directory.Delete(this.tempDir, recursive: true);
    }

    [Fact]
    public void SkillDirsFor_skills_addsToConventionDirectory()
    {
        var pluginDir = CreatePlugin("my-plugin", $$"""
            {
                "name": "my-plugin",
                "version": "1.0.0",
                "skills": ["extra-skills"]
            }
            """);

        // Create both directories on disk
        Directory.CreateDirectory(Path.Combine(pluginDir, "skills"));
        Directory.CreateDirectory(Path.Combine(pluginDir, "extra-skills"));

        var dirs = PluginLoader.SkillDirsFor(this.tempDir);

        // Should contain BOTH the convention dir and the extra dir
        Assert.Equal(2, dirs.Count);
        Assert.Contains(dirs, d => d.EndsWith(Path.Combine(pluginDir, "skills"), StringComparison.OrdinalIgnoreCase) || d == Path.Combine(pluginDir, "skills"));
        Assert.Contains(dirs, d => d.EndsWith(Path.Combine(pluginDir, "extra-skills"), StringComparison.OrdinalIgnoreCase) || d == Path.Combine(pluginDir, "extra-skills"));
    }

    [Fact]
    public void Manifest_commands_replacesDefaultConvention()
    {
        var pluginDir = CreatePlugin("cmd-plugin", """
            {
                "name": "cmd-plugin",
                "version": "1.0.0",
                "commands": "src/commands"
            }
            """);

        var plugins = PluginLoader.Load(this.tempDir);
        Assert.Single(plugins);

        var plugin = plugins[0];
        Assert.NotNull(plugin.Manifest);
        Assert.Equal("src/commands", plugin.Manifest!.Commands);
        Assert.Null(plugin.Manifest.Agents);
        Assert.Null(plugin.Manifest.OutputStyles);
        Assert.Null(plugin.Manifest.Themes);
    }

    [Fact]
    public void Manifest_agentsOutputStylesThemes_exposed()
    {
        var pluginDir = CreatePlugin("full-plugin", $$"""
            {
                "name": "full-plugin",
                "version": "1.0.0",
                "agents": "my-agents",
                "outputStyles": "my-styles",
                "themes": "my-themes"
            }
            """);

        var plugins = PluginLoader.Load(this.tempDir);
        var plugin = plugins[0];

        Assert.NotNull(plugin.Manifest);
        Assert.Equal("my-agents", plugin.Manifest!.Agents);
        Assert.Equal("my-styles", plugin.Manifest.OutputStyles);
        Assert.Equal("my-themes", plugin.Manifest.Themes);
    }

    [Fact]
    public void SkillDirsFor_noManifestSkillsField_returnsOnlyConventionDir()
    {
        var pluginDir = CreatePlugin("simple-plugin", """
            { "name": "simple-plugin", "version": "1.0.0" }
            """);

        Directory.CreateDirectory(Path.Combine(pluginDir, "skills"));

        var dirs = PluginLoader.SkillDirsFor(this.tempDir);
        Assert.Single(dirs);
        Assert.Equal(Path.Combine(pluginDir, "skills"), dirs[0]);
    }

    private string CreatePlugin(string name, string json)
    {
        var pluginDir = Path.Combine(this.tempDir, ".coda", "plugins", name);
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), json);
        return pluginDir;
    }
}

// ============================================================================
// Test 3 — A path escaping the plugin directory is rejected
// ============================================================================

public sealed class PluginPathValidationTests : IDisposable
{
    private readonly string tempDir;

    public PluginPathValidationTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-path-val-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir)) Directory.Delete(this.tempDir, recursive: true);
    }

    [Theory]
    [InlineData("../../secret")]
    [InlineData("../sibling")]
    [InlineData("subdir/../../escape")]
    public void Parse_escapingPath_inSkills_throwsException(string escapingPath)
    {
        var dir = Path.Combine(this.tempDir, "bad-plugin");
        Directory.CreateDirectory(dir);

        var json = $$"""
            {
                "name": "bad-plugin",
                "skills": ["{{escapingPath}}"]
            }
            """;

        Assert.ThrowsAny<PluginManifestParseException>(() =>
            PluginManifestParser.Parse(json, dir));
    }

    [Theory]
    [InlineData("../../secret")]
    [InlineData("../sibling")]
    public void Parse_escapingPath_inCommands_throwsException(string escapingPath)
    {
        var dir = Path.Combine(this.tempDir, "bad-cmd-plugin");
        Directory.CreateDirectory(dir);

        var json = $$"""
            {
                "name": "bad-cmd-plugin",
                "commands": "{{escapingPath}}"
            }
            """;

        Assert.ThrowsAny<PluginManifestParseException>(() =>
            PluginManifestParser.Parse(json, dir));
    }

    [Fact]
    public void ValidatePath_absolutePath_throwsException()
    {
        var dir = Path.Combine(this.tempDir, "abs-plugin");
        Directory.CreateDirectory(dir);

        Assert.ThrowsAny<PluginManifestParseException>(() =>
            PluginManifestParser.ValidatePath("/etc/passwd", dir));
    }

    [Fact]
    public void ValidatePath_safePath_doesNotThrow()
    {
        var dir = Path.Combine(this.tempDir, "safe-plugin");
        Directory.CreateDirectory(dir);

        // Should not throw for a safe relative path
        PluginManifestParser.ValidatePath("subdir/skills", dir);
    }

    [Fact]
    public void ValidatePath_pathWithVariable_doesNotThrow()
    {
        var dir = Path.Combine(this.tempDir, "var-plugin");
        Directory.CreateDirectory(dir);

        // Variable-containing paths are exempt from the static check
        PluginManifestParser.ValidatePath("${CODA_PLUGIN_DATA}/extra", dir);
    }
}

// ============================================================================
// Test 4 — Variable interpolation
// ============================================================================

public sealed class PluginVariableInterpolatorTests : IDisposable
{
    private readonly string tempDir;

    public PluginVariableInterpolatorTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-interp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir)) Directory.Delete(this.tempDir, recursive: true);
    }

    [Fact]
    public void Interpolate_codaPluginRoot_expandsToPluginRoot()
    {
        var result = PluginVariableInterpolator.Interpolate(
            "${CODA_PLUGIN_ROOT}/bin/tool",
            pluginRoot: "/plugins/myplugin",
            pluginDataDir: "/data/myplugin",
            projectDir: "/myproject");

        Assert.Equal("/plugins/myplugin/bin/tool", result);
    }

    [Fact]
    public void Interpolate_codaPluginData_expandsToDataDir()
    {
        var result = PluginVariableInterpolator.Interpolate(
            "${CODA_PLUGIN_DATA}/cache",
            pluginRoot: "/plugins/myplugin",
            pluginDataDir: "/data/myplugin",
            projectDir: "/myproject");

        Assert.Equal("/data/myplugin/cache", result);
    }

    [Fact]
    public void Interpolate_codaProjectDir_expandsToProjectDir()
    {
        var result = PluginVariableInterpolator.Interpolate(
            "${CODA_PROJECT_DIR}/.coda",
            pluginRoot: "/plugins/myplugin",
            pluginDataDir: "/data/myplugin",
            projectDir: "/myproject");

        Assert.Equal("/myproject/.coda", result);
    }

    [Fact]
    public void Interpolate_unknownVariable_leftLiteral()
    {
        var result = PluginVariableInterpolator.Interpolate(
            "${UNKNOWN_VARIABLE}/path",
            pluginRoot: "/plugins/myplugin",
            pluginDataDir: "/data/myplugin",
            projectDir: "/myproject");

        // Unknown variable must remain literal — expanding to "" would produce a valid-but-wrong path
        Assert.Equal("${UNKNOWN_VARIABLE}/path", result);
    }

    [Fact]
    public void EnsurePluginDataDir_createsDirectoryOnDemand()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");

        var dataDir = PluginVariableInterpolator.EnsurePluginDataDir(codaDir, "my-plugin");

        Assert.Equal(Path.Combine(codaDir, "plugin-data", "my-plugin"), dataDir);
        Assert.True(Directory.Exists(dataDir));
    }

    [Fact]
    public void EnsurePluginDataDir_calledTwice_doesNotThrow()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");

        var dir1 = PluginVariableInterpolator.EnsurePluginDataDir(codaDir, "idempotent-plugin");
        var dir2 = PluginVariableInterpolator.EnsurePluginDataDir(codaDir, "idempotent-plugin");

        Assert.Equal(dir1, dir2);
    }

    [Fact]
    public void InterpolateWithUserConfig_expandsUserConfigKeys()
    {
        var config = new Dictionary<string, string> { ["API_KEY"] = "secret123" };
        var result = PluginVariableInterpolator.InterpolateWithUserConfig(
            "Bearer ${user_config.API_KEY}",
            pluginRoot: "/p",
            pluginDataDir: "/d",
            projectDir: "/proj",
            userConfig: config);

        Assert.Equal("Bearer secret123", result);
    }
}

// ============================================================================
// Test 5 — enable/disable persist; defaultEnabled: false installs off
// ============================================================================

public sealed class PluginEnableDisableTests : IDisposable
{
    private readonly string tempDir;

    public PluginEnableDisableTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-enable-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir)) Directory.Delete(this.tempDir, recursive: true);
    }

    [Fact]
    public void Disable_persists_andPluginContributesNothing()
    {
        var pluginDir = CreatePlugin("alpha", """{ "name": "alpha", "version": "1.0.0" }""");

        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);
        var store = new PluginStateStore(codaDir);

        // Plugin should be enabled by default
        var plugins = PluginLoader.Load(this.tempDir, codaDir, store);
        Assert.Single(plugins);

        // Disable the plugin
        store.SetEnabled("alpha", false);

        // Now Load with the store should return empty (disabled contributes nothing)
        var afterDisable = PluginLoader.Load(this.tempDir, codaDir, store);
        Assert.Empty(afterDisable);
    }

    [Fact]
    public void Enable_persists_andPluginIsVisible()
    {
        var pluginDir = CreatePlugin("beta", """{ "name": "beta", "version": "1.0.0" }""");

        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);
        var store = new PluginStateStore(codaDir);

        // Disable first, then re-enable
        store.SetEnabled("beta", false);
        var afterDisable = PluginLoader.Load(this.tempDir, codaDir, store);
        Assert.Empty(afterDisable);

        store.SetEnabled("beta", true);
        var afterEnable = PluginLoader.Load(this.tempDir, codaDir, store);
        Assert.Single(afterEnable);
    }

    [Fact]
    public void DefaultEnabled_false_pluginStartsDisabled()
    {
        var pluginDir = CreatePlugin("gamma", """
            { "name": "gamma", "version": "1.0.0", "defaultEnabled": false }
            """);

        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);
        var store = new PluginStateStore(codaDir);

        // No explicit enable/disable — defaultEnabled: false means it starts off
        var plugins = PluginLoader.Load(this.tempDir, codaDir, store);
        Assert.Empty(plugins);
    }

    [Fact]
    public void DefaultEnabled_false_canBeExplicitlyEnabled()
    {
        var pluginDir = CreatePlugin("delta", """
            { "name": "delta", "version": "1.0.0", "defaultEnabled": false }
            """);

        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);
        var store = new PluginStateStore(codaDir);

        store.SetEnabled("delta", true);
        var plugins = PluginLoader.Load(this.tempDir, codaDir, store);
        Assert.Single(plugins);
    }

    [Fact]
    public void EnableDisable_persistsAcrossStoreInstances()
    {
        var pluginDir = CreatePlugin("epsilon", """{ "name": "epsilon", "version": "1.0.0" }""");

        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);

        var store1 = new PluginStateStore(codaDir);
        store1.SetEnabled("epsilon", false);

        // Re-create the store from the same directory — state must survive
        var store2 = new PluginStateStore(codaDir);
        Assert.False(store2.IsEnabled("epsilon"));
    }

    private string CreatePlugin(string name, string json)
    {
        var dir = Path.Combine(this.tempDir, ".coda", "plugins", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), json);
        return dir;
    }
}

// ============================================================================
// Test 6 — update reports version change; local install cannot be updated
// ============================================================================

public sealed class PluginUpdateTests : IDisposable
{
    private readonly string tempDir;

    public PluginUpdateTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-update-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir)) Directory.Delete(this.tempDir, recursive: true);
    }

    [Fact]
    public async Task Update_localInstall_reportsCannotUpdate()
    {
        var pluginDir = CreatePluginDir("my-local-plugin", "1.0.0");
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);
        var stateStore = new PluginStateStore(codaDir);

        var installInfo = new PluginInstallInfo("1.0.0", "local", null, null, DateTimeOffset.UtcNow);
        var updater = new PluginUpdater(codaDir);

        var result = await updater.UpdateAsync(pluginDir, installInfo);

        Assert.False(result.Ok);
        Assert.Contains("local directory", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_gitInstall_reportsVersionChange()
    {
        var pluginDir = CreatePluginDir("my-git-plugin", "1.0.0");
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);

        var installInfo = new PluginInstallInfo("1.0.0", "git", "https://example.com/plugin.git", null, DateTimeOffset.UtcNow);

        // Inject a fake git fetch that overwrites plugin.json with version 2.0.0
        Func<string, PluginInstallInfo, CancellationToken, Task<bool>> fakeGit = (dir, info, ct) =>
        {
            File.WriteAllText(
                Path.Combine(dir, "plugin.json"),
                """{ "name": "my-git-plugin", "version": "2.0.0" }""");
            return Task.FromResult(true);
        };

        var updater = new PluginUpdater(codaDir, gitFetchOverride: fakeGit);
        var result = await updater.UpdateAsync(pluginDir, installInfo);

        Assert.True(result.Ok);
        Assert.Equal("1.0.0", result.OldVersion);
        Assert.Equal("2.0.0", result.NewVersion);
        Assert.Contains("1.0.0", result.Message);
        Assert.Contains("2.0.0", result.Message);
    }

    private string CreatePluginDir(string name, string version)
    {
        var dir = Path.Combine(this.tempDir, "plugins", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "plugin.json"),
            $$"""{ "name": "{{name}}", "version": "{{version}}" }""");
        return dir;
    }
}

// ============================================================================
// Test 7 — orphan grace period retains superseded copy; purges after 14 days
// ============================================================================

public sealed class PluginOrphanGracePeriodTests : IDisposable
{
    private readonly string tempDir;

    public PluginOrphanGracePeriodTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-orphan-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir)) Directory.Delete(this.tempDir, recursive: true);
    }

    [Fact]
    public void MoveToOrphan_movesDirectoryAndKeepsItForGracePeriod()
    {
        var pluginDir = Path.Combine(this.tempDir, "plugins", "my-plugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """{ "name": "my-plugin" }""");

        var codaDir = Path.Combine(this.tempDir, ".coda");
        var clock = new ManualTimeProvider();

        PluginOrphanManager.MoveToOrphan(pluginDir, codaDir, clock);

        // Original directory is gone
        Assert.False(Directory.Exists(pluginDir));

        // Orphan directory exists
        var orphans = PluginOrphanManager.ListOrphans(codaDir);
        Assert.Single(orphans);

        // Advancing less than 14 days → orphan is retained
        clock.Advance(TimeSpan.FromDays(13));
        PluginOrphanManager.PurgeExpired(codaDir, clock);

        var orphansAfter13 = PluginOrphanManager.ListOrphans(codaDir);
        Assert.Single(orphansAfter13);
    }

    [Fact]
    public void PurgeExpired_afterGracePeriod_deletesOrphan()
    {
        var pluginDir = Path.Combine(this.tempDir, "plugins", "old-plugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """{ "name": "old-plugin" }""");

        var codaDir = Path.Combine(this.tempDir, ".coda");
        var clock = new ManualTimeProvider();

        PluginOrphanManager.MoveToOrphan(pluginDir, codaDir, clock);

        // Advance past 14 days
        clock.Advance(TimeSpan.FromDays(15));
        PluginOrphanManager.PurgeExpired(codaDir, clock);

        var orphans = PluginOrphanManager.ListOrphans(codaDir);
        Assert.Empty(orphans);
    }

    [Fact]
    public void PurgeExpired_multipleOrphans_onlyPurgesExpired()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        var clock = new ManualTimeProvider();

        // Create first orphan
        var dir1 = Path.Combine(this.tempDir, "plugins", "plugin-a");
        Directory.CreateDirectory(dir1);
        PluginOrphanManager.MoveToOrphan(dir1, codaDir, clock);

        // Advance 10 days, create second orphan
        clock.Advance(TimeSpan.FromDays(10));
        var dir2 = Path.Combine(this.tempDir, "plugins", "plugin-b");
        Directory.CreateDirectory(dir2);
        PluginOrphanManager.MoveToOrphan(dir2, codaDir, clock);

        // Advance another 10 days (total: 20 days from first, 10 from second)
        clock.Advance(TimeSpan.FromDays(10));
        PluginOrphanManager.PurgeExpired(codaDir, clock);

        var remaining = PluginOrphanManager.ListOrphans(codaDir);
        // First orphan (20 days old) is gone, second (10 days old) is kept
        Assert.Single(remaining);
    }

    [Fact]
    public void Load_purgesExpiredOrphansOnEveryCall()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);
        var pluginsDir = Path.Combine(codaDir, "plugins");
        Directory.CreateDirectory(pluginsDir);

        var clock = new ManualTimeProvider();

        // Pre-populate an orphan directory manually (simulating a previous update)
        var orphanDir = Path.Combine(codaDir, "plugin-orphans");
        Directory.CreateDirectory(orphanDir);
        var ticks = clock.GetUtcNow().UtcTicks;
        var staleOrphan = Path.Combine(orphanDir, $"stale-plugin-{ticks}");
        Directory.CreateDirectory(staleOrphan);

        // Advance past 14 days
        clock.Advance(TimeSpan.FromDays(15));

        // Load with the advanced clock
        PluginLoader.Load(this.tempDir, codaDir, clock: clock);

        // Stale orphan should be purged
        Assert.False(Directory.Exists(staleOrphan));
    }
}

// ============================================================================
// Test 8 — userConfig: types, secrets to credential store, unattended defaults
// ============================================================================

public sealed class PluginUserConfigTests : IDisposable
{
    private readonly string tempDir;

    public PluginUserConfigTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-userconfig-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir)) Directory.Delete(this.tempDir, recursive: true);
    }

    [Fact]
    public async Task Configure_secretField_storedInCredentialStore_notInSettings()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);

        var store = new PluginStateStore(codaDir);
        var credStore = new InMemoryTokenStore();

        var fields = new List<UserConfigField>
        {
            new("API_KEY", UserConfigFieldType.Secret, "API Key", Required: true, Default: null, Options: []),
        };

        var prompts = new ScriptedPromptService([("API Key", "my-secret-value")]);

        var result = await PluginUserConfigService.ConfigureAsync(
            "test-plugin", fields, prompts, credStore, store);

        Assert.True(result.Ok);

        // Secret must be in credential store
        var credKey = PluginUserConfigService.CredentialKey("test-plugin", "API_KEY");
        var storedSecret = await credStore.GetAsync(credKey);
        Assert.Equal("my-secret-value", storedSecret);

        // Secret must NOT be in the state store (plaintext)
        var config = store.GetPluginConfig("test-plugin");
        Assert.DoesNotContain("API_KEY", config.Keys);
    }

    [Fact]
    public async Task Configure_nonSecretField_storedInStateStore()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);

        var store = new PluginStateStore(codaDir);
        var credStore = new InMemoryTokenStore();

        var fields = new List<UserConfigField>
        {
            new("MODE", UserConfigFieldType.String, "Mode", Required: false, Default: null, Options: []),
        };

        var prompts = new ScriptedPromptService([("Mode", "fast")]);

        await PluginUserConfigService.ConfigureAsync(
            "settings-plugin", fields, prompts, credStore, store);

        var config = store.GetPluginConfig("settings-plugin");
        Assert.Equal("fast", config["MODE"]);
    }

    [Fact]
    public async Task Configure_allTypes_acceptedWithoutError()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);

        var store = new PluginStateStore(codaDir);
        var credStore = new InMemoryTokenStore();

        var fields = new List<UserConfigField>
        {
            new("STR_KEY", UserConfigFieldType.String, "String", false, "hello", []),
            new("BOOL_KEY", UserConfigFieldType.Boolean, "Boolean", false, "true", []),
            new("NUM_KEY", UserConfigFieldType.Number, "Number", false, "42", []),
            new("CHOICE_KEY", UserConfigFieldType.Choice, "Choice", false, "a", ["a", "b", "c"]),
        };

        // Use defaults (non-interactive)
        var result = await PluginUserConfigService.ConfigureAsync(
            "multi-plugin", fields, prompts: null, credStore, store);

        Assert.True(result.Ok);
        var config = store.GetPluginConfig("multi-plugin");
        Assert.Equal("hello", config["STR_KEY"]);
        Assert.Equal("true", config["BOOL_KEY"]);
        Assert.Equal("42", config["NUM_KEY"]);
        Assert.Equal("a", config["CHOICE_KEY"]);
    }

    [Fact]
    public async Task Configure_unattended_required_noDefault_disablesPlugin()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);

        var store = new PluginStateStore(codaDir);
        var credStore = new InMemoryTokenStore();

        var fields = new List<UserConfigField>
        {
            new("REQUIRED_KEY", UserConfigFieldType.String, "Required", Required: true, Default: null, Options: []),
        };

        // Non-interactive, no default → plugin should be disabled
        var result = await PluginUserConfigService.ConfigureAsync(
            "blocked-plugin", fields, prompts: null, credStore, store);

        Assert.False(result.Ok);
        Assert.NotNull(result.DisabledReason);
        Assert.Contains("REQUIRED_KEY", result.DisabledReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Configure_unattended_optional_noDefault_skipsGracefully()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);

        var store = new PluginStateStore(codaDir);
        var credStore = new InMemoryTokenStore();

        var fields = new List<UserConfigField>
        {
            new("OPT_KEY", UserConfigFieldType.String, "Optional", Required: false, Default: null, Options: []),
        };

        // Non-interactive, optional, no default → ok with no value
        var result = await PluginUserConfigService.ConfigureAsync(
            "partial-plugin", fields, prompts: null, credStore, store);

        Assert.True(result.Ok);
    }

    // Simple scripted prompt service for testing
    private sealed class ScriptedPromptService : IUiPromptService
    {
        private readonly Dictionary<string, string> answers;

        public ScriptedPromptService(IEnumerable<(string title, string answer)> answers)
        {
            this.answers = answers.ToDictionary(a => a.title, a => a.answer, StringComparer.OrdinalIgnoreCase);
        }

        public bool IsInteractive => true;

        public Task<UiPromptResponse> RequestAsync(UiPromptRequest request, CancellationToken cancellationToken = default)
        {
            if (this.answers.TryGetValue(request.Title, out var answer))
            {
                return Task.FromResult(new UiPromptResponse(false, [], answer));
            }

            return Task.FromResult(new UiPromptResponse(
                false,
                request.Options.Length > 0 ? [request.Options[0].Id] : [],
                null));
        }
    }
}

// ============================================================================
// Test 9 — dependencies: unmet reported; cycle refused; prune
// ============================================================================

public sealed class PluginDependencyTests : IDisposable
{
    private readonly string tempDir;

    public PluginDependencyTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-dep-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir)) Directory.Delete(this.tempDir, recursive: true);
    }

    [Fact]
    public void FindUnmet_missingDependency_reported()
    {
        var manifest = new PluginManifest
        {
            Name = "my-plugin",
            Dependencies = [new PluginDependency("required-plugin", "^1.0.0")],
        };

        var installed = Array.Empty<PluginInfo>();
        var unmet = PluginDependencyResolver.FindUnmet(manifest, installed);

        Assert.Single(unmet);
        Assert.Equal("required-plugin", unmet[0].RequiredPluginName);
    }

    [Fact]
    public void FindUnmet_installedVersionSatisfiesRange_returnsEmpty()
    {
        var manifest = new PluginManifest
        {
            Name = "my-plugin",
            Dependencies = [new PluginDependency("dep-a", "^1.0.0")],
        };

        var installed = new[]
        {
            new PluginInfo("dep-a", "1.2.3", "", Path.Combine(this.tempDir, "dep-a")),
        };

        var unmet = PluginDependencyResolver.FindUnmet(manifest, installed);
        Assert.Empty(unmet);
    }

    [Fact]
    public void FindUnmet_installedVersionOutsideRange_reported()
    {
        var manifest = new PluginManifest
        {
            Name = "my-plugin",
            Dependencies = [new PluginDependency("dep-b", "^2.0.0")],
        };

        var installed = new[]
        {
            new PluginInfo("dep-b", "1.9.9", "", Path.Combine(this.tempDir, "dep-b")),
        };

        var unmet = PluginDependencyResolver.FindUnmet(manifest, installed);
        Assert.Single(unmet);
        Assert.Equal("dep-b", unmet[0].RequiredPluginName);
    }

    [Fact]
    public void HasCycle_noCycle_returnsFalse()
    {
        var manifests = new List<PluginManifest>
        {
            new() { Name = "a", Dependencies = [new PluginDependency("b", null)] },
            new() { Name = "b", Dependencies = [] },
        };

        Assert.False(PluginDependencyResolver.HasCycle(manifests, out _));
    }

    [Fact]
    public void HasCycle_directCycle_returnsTrue()
    {
        var manifests = new List<PluginManifest>
        {
            new() { Name = "a", Dependencies = [new PluginDependency("b", null)] },
            new() { Name = "b", Dependencies = [new PluginDependency("a", null)] },
        };

        var hasCycle = PluginDependencyResolver.HasCycle(manifests, out var description);
        Assert.True(hasCycle);
        Assert.Contains("a", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("b", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HasCycle_transitiveChain_noCycle_returnsFalse()
    {
        var manifests = new List<PluginManifest>
        {
            new() { Name = "a", Dependencies = [new PluginDependency("b", null)] },
            new() { Name = "b", Dependencies = [new PluginDependency("c", null)] },
            new() { Name = "c", Dependencies = [] },
        };

        Assert.False(PluginDependencyResolver.HasCycle(manifests, out _));
    }

    [Fact]
    public void Prune_orphanedDependency_isListed()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);
        var stateStore = new PluginStateStore(codaDir);

        // "dep" was installed as a dependency (source = "dependency")
        stateStore.SetInstalledInfo("dep", new PluginInstallInfo(
            "1.0.0", "dependency", null, null, DateTimeOffset.UtcNow));

        // "main" was installed by the user and no longer requires "dep"
        stateStore.SetInstalledInfo("main", new PluginInstallInfo(
            "2.0.0", "git", "https://example.com/main.git", null, DateTimeOffset.UtcNow));

        var mainPluginDir = Path.Combine(this.tempDir, "main");
        Directory.CreateDirectory(mainPluginDir);
        var depPluginDir = Path.Combine(this.tempDir, "dep");
        Directory.CreateDirectory(depPluginDir);

        var installed = new[]
        {
            new PluginInfo("main", "2.0.0", "", mainPluginDir)
            {
                Manifest = new PluginManifest { Name = "main", Dependencies = [] },
            },
            new PluginInfo("dep", "1.0.0", "", depPluginDir)
            {
                Manifest = new PluginManifest { Name = "dep", Dependencies = [] },
            },
        };

        var pruneable = PluginDependencyResolver.FindPruneable(installed, stateStore);

        Assert.Single(pruneable);
        Assert.Equal("dep", pruneable[0]);
    }

    [Fact]
    public void Prune_dependencyStillRequired_isNotListed()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);
        var stateStore = new PluginStateStore(codaDir);

        stateStore.SetInstalledInfo("dep", new PluginInstallInfo(
            "1.0.0", "dependency", null, null, DateTimeOffset.UtcNow));
        stateStore.SetInstalledInfo("main", new PluginInstallInfo(
            "2.0.0", "git", "https://example.com/main.git", null, DateTimeOffset.UtcNow));

        var mainDir = Path.Combine(this.tempDir, "main-required");
        Directory.CreateDirectory(mainDir);
        var depDir = Path.Combine(this.tempDir, "dep-required");
        Directory.CreateDirectory(depDir);

        var installed = new[]
        {
            new PluginInfo("main", "2.0.0", "", mainDir)
            {
                // "main" still depends on "dep"
                Manifest = new PluginManifest
                {
                    Name = "main",
                    Dependencies = [new PluginDependency("dep", null)],
                },
            },
            new PluginInfo("dep", "1.0.0", "", depDir)
            {
                Manifest = new PluginManifest { Name = "dep", Dependencies = [] },
            },
        };

        var pruneable = PluginDependencyResolver.FindPruneable(installed, stateStore);
        Assert.Empty(pruneable);
    }
}

// ============================================================================
// Test 10 — Plugin using only today's three fields behaves exactly as before
// ============================================================================

public sealed class PluginBackwardCompatibilityTests : IDisposable
{
    private readonly string tempDir;

    public PluginBackwardCompatibilityTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-compat-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir)) Directory.Delete(this.tempDir, recursive: true);
    }

    [Fact]
    public void ThreeFieldPlugin_loadedCorrectly()
    {
        var pluginDir = CreateProjectPluginDir("compat-plugin");
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "compat-plugin", "version": "1.2.3", "description": "Old-style plugin"}""");

        var plugins = PluginLoader.Load(this.tempDir);

        Assert.Single(plugins);
        var plugin = plugins[0];
        Assert.Equal("compat-plugin", plugin.Name);
        Assert.Equal("1.2.3", plugin.Version);
        Assert.Equal("Old-style plugin", plugin.Description);
        Assert.Equal(pluginDir, plugin.Directory);
        Assert.True(plugin.IsEnabled);
    }

    [Fact]
    public void ThreeFieldPlugin_missingName_stillFallsBackToDirName()
    {
        var pluginDir = CreateProjectPluginDir("fallback-dir");
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"description": "Just a description"}""");

        var plugins = PluginLoader.Load(this.tempDir);

        Assert.Single(plugins);
        Assert.Equal("fallback-dir", plugins[0].Name);
        Assert.Equal("0.0.0", plugins[0].Version);
        Assert.Equal("Just a description", plugins[0].Description);
    }

    [Fact]
    public void ThreeFieldPlugin_malformedJson_fallsBackToDefaults()
    {
        var pluginDir = CreateProjectPluginDir("broken");
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), "not valid json {{{{");

        var plugins = PluginLoader.Load(this.tempDir);

        Assert.Single(plugins);
        Assert.Equal("broken", plugins[0].Name);
        Assert.Equal("0.0.0", plugins[0].Version);
    }

    [Fact]
    public void SkillDirsFor_threeFieldPlugin_worksUnchanged()
    {
        var pluginDir = CreateProjectPluginDir("skill-compat");
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "skill-compat", "version": "1.0.0", "description": ""}""");

        var skillsDir = Path.Combine(pluginDir, "skills");
        Directory.CreateDirectory(skillsDir);

        var dirs = PluginLoader.SkillDirsFor(this.tempDir);
        Assert.Single(dirs);
        Assert.Equal(skillsDir, dirs[0]);
    }

    [Fact]
    public void ProjectOverridesUser_withThreeFieldPlugins()
    {
        var userBase = Path.Combine(this.tempDir, "user-home");
        Directory.CreateDirectory(userBase);
        var userPluginDir = Path.Combine(userBase, "plugins", "shared");
        Directory.CreateDirectory(userPluginDir);
        File.WriteAllText(
            Path.Combine(userPluginDir, "plugin.json"),
            """{"name": "shared", "version": "0.1.0", "description": "User version"}""");

        var projectDir = Path.Combine(this.tempDir, "project");
        Directory.CreateDirectory(projectDir);
        var projectPluginDir = Path.Combine(projectDir, ".coda", "plugins", "shared");
        Directory.CreateDirectory(projectPluginDir);
        File.WriteAllText(
            Path.Combine(projectPluginDir, "plugin.json"),
            """{"name": "shared", "version": "2.0.0", "description": "Project version"}""");

        var plugins = PluginLoader.Load(projectDir, userBase);

        Assert.Single(plugins);
        Assert.Equal("2.0.0", plugins[0].Version);
        Assert.Equal("Project version", plugins[0].Description);
    }

    private string CreateProjectPluginDir(string pluginName)
    {
        var dir = Path.Combine(this.tempDir, ".coda", "plugins", pluginName);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
