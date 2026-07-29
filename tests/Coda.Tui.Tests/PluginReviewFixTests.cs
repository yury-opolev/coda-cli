using Coda.Tui.Commands;
using Coda.Tui.Plugins;
using Coda.Tui.Repl;
using Coda.Tui.Skills;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

// ============================================================================
// C1 — ${...} path escape must be caught; legitimate variable paths work
// ============================================================================

public sealed class PluginVariablePathEscapeTests : IDisposable
{
    private readonly string tempDir;

    public PluginVariablePathEscapeTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-c1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    /// <summary>
    /// C1 — core escape: a ${...} path containing ../.. must be rejected.
    /// Before fix this returns without throwing (the ${...} exemption short-circuits validation).
    /// After fix the containment check runs and rejects the traversal.
    /// </summary>
    [Fact]
    public void ValidatePath_variableWithTraversal_throws()
    {
        var dir = Path.Combine(this.tempDir, "evil-plugin");
        Directory.CreateDirectory(dir);

        Assert.Throws<PluginManifestPathException>(() =>
            PluginManifestParser.ValidatePath("${CODA_PLUGIN_DATA}/../../../../..", dir));
    }

    /// <summary>
    /// C1 — the full parse path must also reject a ${...} traversal in skills.
    /// </summary>
    [Fact]
    public void Parse_variableSkillPathWithTraversal_throws()
    {
        var dir = Path.Combine(this.tempDir, "evil-plugin2");
        Directory.CreateDirectory(dir);

        var json = """{"name": "evil-plugin2", "skills": ["${CODA_PLUGIN_DATA}/../../../../Windows"]}""";
        Assert.Throws<PluginManifestPathException>(() =>
            PluginManifestParser.Parse(json, dir));
    }

    /// <summary>
    /// C1 — a safe ${CODA_PLUGIN_DATA}/skills path passes the static check (no traversal).
    /// </summary>
    [Fact]
    public void ValidatePath_variableWithNoTraversal_doesNotThrow()
    {
        var dir = Path.Combine(this.tempDir, "safe-plugin");
        Directory.CreateDirectory(dir);

        // No traversal — ValidatePath treats ${CODA_PLUGIN_DATA} as a literal subdir name,
        // which is inside the plugin root, so no exception.
        PluginManifestParser.ValidatePath("${CODA_PLUGIN_DATA}/skills", dir);
    }

    /// <summary>
    /// C1 — SkillDirsFor interpolates ${CODA_PLUGIN_DATA}/skills and includes it when the
    /// expanded directory exists. Before fix the literal path doesn't exist, so it's silently
    /// dropped. After fix the interpolated absolute path is found and returned.
    /// </summary>
    [Fact]
    public void SkillDirsFor_variableSkillPath_isInterpolatedAndIncluded()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        var pluginDir = Path.Combine(codaDir, "plugins", "var-skills-plugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "var-skills-plugin", "version": "1.0.0", "skills": ["${CODA_PLUGIN_DATA}/skills"]}""");

        // Create the expected expanded directory
        var expandedDataSkillsDir = Path.Combine(codaDir, "plugin-data", "var-skills-plugin", "skills");
        Directory.CreateDirectory(expandedDataSkillsDir);

        var dirs = PluginLoader.SkillDirsFor(this.tempDir, codaDir);

        // After fix: the interpolated absolute path must appear in the result
        Assert.Contains(dirs, d => string.Equals(
            Path.GetFullPath(d),
            Path.GetFullPath(expandedDataSkillsDir),
            StringComparison.OrdinalIgnoreCase));
    }
}

// ============================================================================
// M1 — SemVer: ^0.0.x too permissive; pre-release ordering; build metadata
// ============================================================================

public sealed class SemVerBugFixTests
{
    /// <summary>
    /// M1a — ^0.0.3 means exactly 0.0.3 (patch is the left-most non-zero element).
    /// Before fix: 0.0.9 satisfies ^0.0.3 (patch >= baseline.Patch).
    /// After fix: 0.0.9 does NOT satisfy ^0.0.3.
    /// </summary>
    [Fact]
    public void SatisfiesRange_caretDoubleZero_patchMustBeExact()
    {
        Assert.True(SemVer.TryParse("0.0.9", out var v009));
        Assert.False(SemVer.SatisfiesRange(v009, "^0.0.3"),
            "^0.0.3 must NOT be satisfied by 0.0.9 — patch is the left-most non-zero element");
    }

    /// <summary>M1a — ^0.0.3 is satisfied by 0.0.3 itself.</summary>
    [Fact]
    public void SatisfiesRange_caretDoubleZero_satisfiedByExactPatch()
    {
        Assert.True(SemVer.TryParse("0.0.3", out var v003));
        Assert.True(SemVer.SatisfiesRange(v003, "^0.0.3"));
    }

    /// <summary>
    /// M1b — Pre-release identifiers that are all-digits must compare numerically.
    /// Before fix: ordinal "10" < "2", so 1.0.0-alpha.10 sorts below 1.0.0-alpha.2.
    /// After fix: numeric comparison makes 1.0.0-alpha.10 > 1.0.0-alpha.2.
    /// </summary>
    [Fact]
    public void CompareTo_preReleaseNumericIdentifiers_compareNumerically()
    {
        Assert.True(SemVer.TryParse("1.0.0-alpha.10", out var alpha10));
        Assert.True(SemVer.TryParse("1.0.0-alpha.2", out var alpha2));

        Assert.True(alpha10 > alpha2,
            "1.0.0-alpha.10 must be greater than 1.0.0-alpha.2 with numeric identifier comparison");
    }

    /// <summary>
    /// M1c — Build metadata (+build) must be stripped before parsing.
    /// Before fix: TryParse("1.2.3+build.1") returns false and the version is reported as unmet.
    /// After fix: parses successfully, ignoring the build metadata.
    /// </summary>
    [Fact]
    public void TryParse_buildMetadata_stripsAndParses()
    {
        Assert.True(SemVer.TryParse("1.2.3+build.1", out var v),
            "Version with build metadata should parse successfully (metadata is not part of precedence)");
        Assert.Equal(1, v.Major);
        Assert.Equal(2, v.Minor);
        Assert.Equal(3, v.Patch);
        Assert.Null(v.PreRelease);
    }

    /// <summary>M1c — Build metadata on a pre-release version is also stripped.</summary>
    [Fact]
    public void TryParse_preReleaseWithBuildMetadata_stripsMetadata()
    {
        Assert.True(SemVer.TryParse("1.0.0-alpha.1+build.42", out var v));
        Assert.Equal("alpha.1", v.PreRelease);
    }
}

// ============================================================================
// M2 — disabled-reason message must interpolate (type: {field.Type})
// ============================================================================

public sealed class PluginDisabledReasonInterpolationTests : IDisposable
{
    private readonly string tempDir;

    public PluginDisabledReasonInterpolationTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-m2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    /// <summary>
    /// M2 — the disabled-reason message must contain the resolved type name, not the
    /// literal template fragment "(type: {field.Type})".
    /// Before fix: message contains literal "{field.Type}" (missing $ makes it a verbatim string).
    /// After fix: message contains "String" (the resolved field type name).
    /// </summary>
    [Fact]
    public async Task Configure_disabledReason_containsResolvedFieldType()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);
        var store = new PluginStateStore(codaDir);
        var credStore = new InMemoryTokenStore();

        var fields = new List<UserConfigField>
        {
            new("REQUIRED_KEY", UserConfigFieldType.String, "Required", Required: true, Default: null, Options: []),
        };

        var result = await PluginUserConfigService.ConfigureAsync(
            "blocked-plugin", fields, prompts: null, credStore, store);

        Assert.False(result.Ok);
        Assert.NotNull(result.DisabledReason);

        // The type name "String" must appear in the message, not the literal "{field.Type}"
        Assert.Contains("String", result.DisabledReason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{field.Type}", result.DisabledReason!, StringComparison.Ordinal);
    }
}

// ============================================================================
// M3 — plugin name must be kebab-case; credential key must be unambiguous
// ============================================================================

public sealed class PluginNameKebabCaseTests : IDisposable
{
    private readonly string tempDir;

    public PluginNameKebabCaseTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-m3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    /// <summary>
    /// M3a — plugin name containing ':' must be rejected at parse time.
    /// Before fix: any non-empty string is accepted.
    /// After fix: kebab-case validation rejects names with ':'.
    /// </summary>
    [Fact]
    public void Parse_pluginNameWithColon_throwsException()
    {
        var dir = Path.Combine(this.tempDir, "colon-test");
        Directory.CreateDirectory(dir);

        Assert.Throws<PluginManifestParseException>(() =>
            PluginManifestParser.Parse("""{"name": "a:b", "version": "1.0.0"}""", dir));
    }

    /// <summary>M3a — valid kebab-case names are accepted.</summary>
    [Theory]
    [InlineData("my-plugin")]
    [InlineData("plugin123")]
    [InlineData("a-b-c")]
    public void Parse_validKebabCaseName_doesNotThrow(string name)
    {
        var dir = Path.Combine(this.tempDir, $"valid-{name}");
        Directory.CreateDirectory(dir);

        var json = $$$"""{"name": "{{{name}}}", "version": "1.0.0"}""";
        var manifest = PluginManifestParser.Parse(json, dir);
        Assert.Equal(name, manifest.Name);
    }

    /// <summary>
    /// M3b — credential keys for (plugin "a:b", field "c") and (plugin "a", field "b:c") must differ.
    /// Before fix: both produce "plugin:a:b:c" — a collision.
    /// After fix: the key format is unambiguous (e.g. uses a non-colon separator).
    /// </summary>
    [Fact]
    public void CredentialKey_ambiguousSeparator_keysAreDifferent()
    {
        var key1 = PluginUserConfigService.CredentialKey("a:b", "c");
        var key2 = PluginUserConfigService.CredentialKey("a", "b:c");

        Assert.NotEqual(key1, key2);
    }
}

// ============================================================================
// M4 — path violation must not be silently swallowed as a legacy plugin
// ============================================================================

public sealed class PluginPathViolationMaskingTests : IDisposable
{
    private readonly string tempDir;

    public PluginPathViolationMaskingTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-m4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    /// <summary>
    /// M4 — ParsePluginJson must propagate path violations rather than falling back to legacy.
    /// Before fix: PluginManifestParseException from a path violation is caught and the plugin
    /// is returned as legacy (name from manifest, no manifest object).
    /// After fix: PluginManifestPathException propagates (not swallowed by the legacy catch).
    /// </summary>
    [Fact]
    public void ParsePluginJson_pathViolation_propagates()
    {
        var dir = Path.Combine(this.tempDir, "path-viol-plugin");
        Directory.CreateDirectory(dir);
        var json = """{"name": "path-viol-plugin", "skills": ["../../evil"]}""";

        // Before fix: returns a PluginInfo with no Manifest (legacy fallback, path silently dropped)
        // After fix: throws PluginManifestPathException
        Assert.Throws<PluginManifestPathException>(() =>
            PluginLoader.ParsePluginJson(json, "path-viol-plugin", dir));
    }

    /// <summary>
    /// M4 — A plugin with escaping path is SKIPPED during directory loading (not loaded as legacy).
    /// Before fix: PluginLoader.Load returns the plugin with the escaping path silently removed.
    /// After fix: the plugin is omitted from results.
    /// </summary>
    [Fact]
    public void Load_pluginWithEscapingPath_isSkipped()
    {
        var pluginDir = Path.Combine(this.tempDir, ".coda", "plugins", "escaping-plugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"name": "escaping-plugin", "version": "1.0.0", "skills": ["../../windows"]}""");

        var plugins = PluginLoader.Load(this.tempDir);

        // Before fix: plugin loaded as legacy with no manifest (name "escaping-plugin", no skills)
        // After fix: plugin is not loaded at all
        Assert.Empty(plugins);
    }

    /// <summary>
    /// M4 — A missing-name plugin still falls back to legacy (the name-only catch still applies).
    /// </summary>
    [Fact]
    public void ParsePluginJson_missingName_stillFallsBackToLegacy()
    {
        var dir = Path.Combine(this.tempDir, "no-name-plugin");
        Directory.CreateDirectory(dir);
        var json = """{"version": "2.0.0", "description": "No name here"}""";

        // Missing name → legacy fallback with dir name
        var info = PluginLoader.ParsePluginJson(json, "no-name-plugin", dir);

        Assert.Equal("no-name-plugin", info.Name);
        Assert.Equal("2.0.0", info.Version);
        Assert.Null(info.Manifest);
    }
}

// ============================================================================
// I1 — enable / disable subcommands wired to PluginStateStore
// ============================================================================

public sealed class PluginEnableDisableCommandTests : IDisposable
{
    private readonly string tempDir;

    public PluginEnableDisableCommandTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-i1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    private (CommandContext Context, TestConsole Console) BuildContext()
    {
        var (_, ctx, console, _) = TestAppBuilder.BuildApp(workingDirectory: this.tempDir);
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);
        ctx.PluginState = new PluginStateStore(codaDir);
        return (ctx, console);
    }

    private string CreatePlugin(string name)
    {
        var dir = Path.Combine(this.tempDir, ".coda", "plugins", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$$"""{"name": "{{{name}}}", "version": "1.0.0"}""");
        return dir;
    }

    /// <summary>
    /// I1 — /plugin disable persists the disabled state; subsequent load excludes the plugin.
    /// </summary>
    [Fact]
    public async Task PluginCommand_disable_persistsAndExcludesPlugin()
    {
        CreatePlugin("my-plugin");
        var (ctx, console) = this.BuildContext();
        var command = new PluginCommand();

        await command.ExecuteAsync(ctx, ["disable", "my-plugin"], CancellationToken.None);

        // The plugin must now be excluded from load with the state store
        var plugins = PluginLoader.Load(this.tempDir, stateStore: ctx.PluginState);
        Assert.Empty(plugins);
    }

    /// <summary>
    /// I1 — /plugin enable re-enables a disabled plugin.
    /// </summary>
    [Fact]
    public async Task PluginCommand_enable_reEnablesPlugin()
    {
        CreatePlugin("alpha-plugin");
        var (ctx, console) = this.BuildContext();
        var command = new PluginCommand();

        // Disable first
        await command.ExecuteAsync(ctx, ["disable", "alpha-plugin"], CancellationToken.None);
        Assert.Empty(PluginLoader.Load(this.tempDir, stateStore: ctx.PluginState));

        // Re-enable
        await command.ExecuteAsync(ctx, ["enable", "alpha-plugin"], CancellationToken.None);
        Assert.Single(PluginLoader.Load(this.tempDir, stateStore: ctx.PluginState));
    }

    /// <summary>
    /// I1 — /plugin disable on an unknown plugin shows a helpful message (does not crash).
    /// </summary>
    [Fact]
    public async Task PluginCommand_disable_unknownPlugin_showsMessage()
    {
        var (ctx, console) = this.BuildContext();
        var command = new PluginCommand();

        var result = await command.ExecuteAsync(ctx, ["disable", "nonexistent"], CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        Assert.Contains("nonexistent", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// I1 — /plugin enable on an unknown plugin shows a helpful message (does not crash).
    /// </summary>
    [Fact]
    public async Task PluginCommand_enable_unknownPlugin_showsMessage()
    {
        var (ctx, console) = this.BuildContext();
        var command = new PluginCommand();

        var result = await command.ExecuteAsync(ctx, ["enable", "nonexistent"], CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        Assert.Contains("nonexistent", console.Output, StringComparison.OrdinalIgnoreCase);
    }
}

// ============================================================================
// I1 — /plugin update subcommand reaches PluginUpdater
// ============================================================================

public sealed class PluginUpdateCommandTests : IDisposable
{
    private readonly string tempDir;

    public PluginUpdateCommandTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-i1-upd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    private CommandContext BuildContext(PluginStateStore store)
    {
        var (_, ctx, _, _) = TestAppBuilder.BuildApp(workingDirectory: this.tempDir);
        ctx.PluginState = store;
        return ctx;
    }

    private string CreatePlugin(string name, string version)
    {
        var dir = Path.Combine(this.tempDir, ".coda", "plugins", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$$"""{"name": "{{{name}}}", "version": "{{{version}}}"}""");
        return dir;
    }

    /// <summary>
    /// I1 — /plugin update &lt;name&gt; calls the updater and reports the result.
    /// </summary>
    [Fact]
    public async Task PluginCommand_update_callsUpdaterAndReportsResult()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);
        var stateStore = new PluginStateStore(codaDir);

        var pluginDir = CreatePlugin("update-me", "1.0.0");
        stateStore.SetInstalledInfo("update-me", new PluginInstallInfo(
            "1.0.0", "git", "https://example.com/plugin.git", null, DateTimeOffset.UtcNow));

        var updateCalled = false;
        Func<string, PluginInstallInfo, CancellationToken, Task<bool>> fakeGit =
            (dir, info, ct) =>
            {
                updateCalled = true;
                File.WriteAllText(Path.Combine(dir, "plugin.json"),
                    """{"name": "update-me", "version": "2.0.0"}""");
                return Task.FromResult(true);
            };

        var ctx = BuildContext(stateStore);
        var updater = new PluginUpdater(codaDir, gitFetchOverride: fakeGit);
        var pluginsDir = Path.Combine(codaDir, "plugins");
        var command = new PluginCommand(userPluginsDirOverride: pluginsDir, updaterOverride: updater);

        var result = await command.ExecuteAsync(ctx, ["update", "update-me"], CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        Assert.True(updateCalled, "The updater must be called for a git-installed plugin");
    }
}

// ============================================================================
// I1 — /plugin prune subcommand
// ============================================================================

public sealed class PluginPruneCommandTests : IDisposable
{
    private readonly string tempDir;

    public PluginPruneCommandTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-i1-prune-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    private CommandContext BuildContext(PluginStateStore store)
    {
        var (_, ctx, _, _) = TestAppBuilder.BuildApp(workingDirectory: this.tempDir);
        ctx.PluginState = store;
        return ctx;
    }

    /// <summary>
    /// I1 — /plugin prune reports orphaned dependency plugins; /plugin prune --apply removes them.
    /// </summary>
    [Fact]
    public async Task PluginCommand_prune_listsOrphanedDependencies()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);
        var stateStore = new PluginStateStore(codaDir);

        // dep-plugin was installed as a dependency but nothing requires it now
        var depDir = Path.Combine(codaDir, "plugins", "dep-plugin");
        Directory.CreateDirectory(depDir);
        File.WriteAllText(Path.Combine(depDir, "plugin.json"),
            """{"name": "dep-plugin", "version": "1.0.0"}""");
        stateStore.SetInstalledInfo("dep-plugin", new PluginInstallInfo(
            "1.0.0", "dependency", null, null, DateTimeOffset.UtcNow));

        var (_, ctx, console, _) = TestAppBuilder.BuildApp(workingDirectory: this.tempDir);
        ctx.PluginState = stateStore;

        var command = new PluginCommand();
        var result = await command.ExecuteAsync(ctx, ["prune"], CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        Assert.Contains("dep-plugin", console.Output, StringComparison.OrdinalIgnoreCase);
    }
}

// ============================================================================
// I1 — install calls ConfigureAsync when userConfig is declared
// ============================================================================

public sealed class PluginInstallConfigureTests : IDisposable
{
    private readonly string tempDir;

    public PluginInstallConfigureTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-i1-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    /// <summary>
    /// I1 — installing a plugin with a default userConfig value persists that value via ConfigureAsync.
    /// Before fix: install never calls ConfigureAsync, so defaults are never stored.
    /// After fix: ConfigureAsync is called and the default is visible via stateStore.GetPluginConfig.
    /// </summary>
    [Fact]
    public async Task Install_pluginWithDefaultUserConfig_configValuePersisted()
    {
        var sourceDir = Path.Combine(this.tempDir, "source-configplugin");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "plugin.json"), """
            {
                "name": "config-plugin",
                "version": "1.0.0",
                "userConfig": [
                    { "key": "MODE", "type": "string", "default": "fast" }
                ]
            }
            """);

        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);
        var stateStore = new PluginStateStore(codaDir);
        var userPluginsDir = Path.Combine(codaDir, "plugins");

        var (_, ctx, _, _) = TestAppBuilder.BuildApp(workingDirectory: this.tempDir);
        ctx.PluginState = stateStore;

        var command = new PluginCommand(userPluginsDirOverride: userPluginsDir);
        await command.ExecuteAsync(ctx, ["install", sourceDir], CancellationToken.None);

        // ConfigureAsync should have been called and stored the default "fast" for MODE
        var config = stateStore.GetPluginConfig("config-plugin");
        Assert.True(config.ContainsKey("MODE"), "MODE config value should be persisted after install");
        Assert.Equal("fast", config["MODE"]);
    }
}

// ============================================================================
// I1 — SkillLoader threads PluginStateStore to PluginLoader.SkillDirsFor
// ============================================================================

public sealed class SkillLoaderStateStoreTests : IDisposable
{
    private readonly string tempDir;

    public SkillLoaderStateStoreTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-i1-skill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    /// <summary>
    /// I1 — SkillLoader.Load must accept a PluginStateStore and exclude skills from disabled plugins.
    /// Before fix: no stateStore parameter; disabled plugin skills are always loaded.
    /// After fix: stateStore is threaded through; disabled plugin has no skill directories.
    /// </summary>
    [Fact]
    public void SkillLoader_withStateStore_excludesDisabledPluginSkills()
    {
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);

        // Create a plugin with a skills directory
        var pluginDir = Path.Combine(codaDir, "plugins", "disabled-skills-plugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"),
            """{"name": "disabled-skills-plugin", "version": "1.0.0"}""");

        var skillDir = Path.Combine(pluginDir, "skills", "my-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: my-skill
            description: A test skill
            ---
            # My Skill
            """);

        // Disable the plugin
        var stateStore = new PluginStateStore(codaDir);
        stateStore.SetEnabled("disabled-skills-plugin", false);

        // Load skills WITH the state store — disabled plugin's skills should not appear
        var skills = SkillLoader.Load(
            this.tempDir,
            userSkillsDir: codaDir,
            pluginStateStore: stateStore);

        Assert.DoesNotContain(skills, s => s.Name == "my-skill");
    }
}

// ============================================================================
// L1 — update uses git fetch + checkout when commit pin is recorded
// ============================================================================

public sealed class PluginUpdateCommitPinTests : IDisposable
{
    private readonly string tempDir;

    public PluginUpdateCommitPinTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-l1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    private string CreatePluginDir(string name, string version)
    {
        var dir = Path.Combine(this.tempDir, "plugins", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$$"""{"name": "{{{name}}}", "version": "{{{version}}}"}""");
        return dir;
    }

    /// <summary>
    /// L1 — when installInfo.Commit is set the updater override receives the commit pin.
    /// The override signature must include PluginInstallInfo so it can honour the pin.
    /// Before fix: override only receives (directory, ct) — pin is not passed through.
    /// After fix: override receives (directory, installInfo, ct) and can access installInfo.Commit.
    /// </summary>
    [Fact]
    public async Task Update_withCommitPin_passesInstallInfoToOverride()
    {
        var pluginDir = CreatePluginDir("pinned-plugin", "1.0.0");
        var codaDir = Path.Combine(this.tempDir, ".coda");
        Directory.CreateDirectory(codaDir);

        const string PinnedCommit = "abc123def456abc123def456abc123def456abc123";

        PluginInstallInfo? capturedInfo = null;

        Func<string, PluginInstallInfo, CancellationToken, Task<bool>> fakeGit =
            (dir, info, ct) =>
            {
                capturedInfo = info;
                File.WriteAllText(Path.Combine(dir, "plugin.json"),
                    """{"name": "pinned-plugin", "version": "1.0.1"}""");
                return Task.FromResult(true);
            };

        var installInfo = new PluginInstallInfo(
            "1.0.0", "git", "https://example.com/plugin.git", PinnedCommit, DateTimeOffset.UtcNow);
        var updater = new PluginUpdater(codaDir, gitFetchOverride: fakeGit);
        var result = await updater.UpdateAsync(pluginDir, installInfo);

        Assert.True(result.Ok);
        Assert.NotNull(capturedInfo);
        Assert.Equal(PinnedCommit, capturedInfo!.Commit);
    }
}
