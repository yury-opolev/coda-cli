using Coda.Agent.Hooks;
using Coda.Agent.OutputStyles;
using Coda.Tui.Commands;
using Coda.Tui.Plugins;
using Coda.Tui.Ui.Rendering;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for Phase 4 plugin-supplied components visible from the TUI layer (Tests 6–10 of the spec):
/// <list type="bullet">
///   <item>Test 6 — <c>/hooks list</c> and <c>info</c> attribute plugin hooks to their plugin.</item>
///   <item>Test 7 — Plugin output style and theme are selectable; built-in name collision resolves to built-in.</item>
///   <item>Test 8 — A disabled plugin contributes no agents, MCP servers, hooks, output styles, or themes.</item>
///   <item>Test 9 — A malformed component is skipped without preventing the rest from loading.</item>
///   <item>Test 10 — A plugin declaring none of the components behaves exactly as before.</item>
/// </list>
/// </summary>
[Collection("ThemeState")]
public sealed class PluginPhase4Tests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_p4_tui_").FullName;

    public void Dispose()
    {
        BuiltInOutputStyles.ClearPluginStyles();
        CodaThemes.ClearPluginThemes();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    // =========================================================================
    // Test 6 — /hooks list and info attribute plugin-origin hooks
    // =========================================================================

    [Fact]
    public void HooksView_FormatList_shows_plugin_origin_label()
    {
        var hook = new UserHook(
            "PreToolUse",
            "./check.sh",
            Scope: HookScope.Project,
            PluginOrigin: ("acme-gate", "3.1.0"));

        var output = HooksView.FormatList([hook]);

        Assert.Contains("plugin:", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("acme-gate", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.1.0", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HooksView_FormatInfo_shows_plugin_origin()
    {
        var hook = new UserHook(
            "PostToolUse",
            "./post.sh",
            Scope: HookScope.User,
            PluginOrigin: ("team-hooks", "1.2.3"));

        var output = HooksView.FormatInfo(0, hook, lastRun: null);

        Assert.Contains("plugin:", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("team-hooks", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.2.3", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HooksView_FormatList_non_plugin_hook_shows_scope_not_plugin()
    {
        var userHook = new UserHook("PreToolUse", "./check.sh", Scope: HookScope.User);
        var projectHook = new UserHook("PreToolUse", "./check.sh", Scope: HookScope.Project);

        var userOutput = HooksView.FormatList([userHook]);
        var projectOutput = HooksView.FormatList([projectHook]);

        // Should show scope labels, NOT plugin:.
        Assert.Contains("[user]", userOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[project]", projectOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plugin:", userOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plugin:", projectOutput, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Test 7 — Plugin output style and theme are selectable
    // =========================================================================

    [Fact]
    public void BuiltInOutputStyles_plugin_style_is_selectable_via_Resolve()
    {
        var pluginStyle = new OutputStyle("formal", "Formal writing style", "Be formal at all times.");
        BuiltInOutputStyles.RegisterPlugin(pluginStyle);

        var resolved = BuiltInOutputStyles.Resolve("formal");
        Assert.Equal("formal", resolved.Name);
        Assert.Equal("Be formal at all times.", resolved.SystemPromptSuffix);
    }

    [Fact]
    public void BuiltInOutputStyles_plugin_style_is_recognized_by_IsKnown()
    {
        var pluginStyle = new OutputStyle("terse-json", "Terse JSON style", "Return JSON.");
        BuiltInOutputStyles.RegisterPlugin(pluginStyle);

        Assert.True(BuiltInOutputStyles.IsKnown("terse-json"));
        Assert.False(BuiltInOutputStyles.IsKnown("not-registered-style"));
    }

    [Fact]
    public void BuiltInOutputStyles_builtin_name_collision_rejects_plugin_style_with_warning()
    {
        var collisionStyle = new OutputStyle("concise", "Trying to hijack concise", "Hijacked!");
        var logger = new TestLogger();

        var registered = BuiltInOutputStyles.RegisterPlugin(collisionStyle, logger);

        Assert.False(registered);
        // Built-in wins: Resolve("concise") must return the built-in.
        var resolved = BuiltInOutputStyles.Resolve("concise");
        Assert.NotEqual("Hijacked!", resolved.SystemPromptSuffix);
        // Warning must be logged.
        Assert.Contains(logger.Warnings, w => w.Contains("concise", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CodaThemes_plugin_theme_is_selectable_via_TryGet()
    {
        var palette = new ConsolePalette("#00FF88", "#888888", "#40FF40", "#FFCC00", "#FF4040");
        var theme = new CodaTheme("neon", "Neon", CodaThemes.Default.Tui, palette);

        CodaThemes.RegisterPlugin(theme);

        var found = CodaThemes.TryGet("neon", out var resolved);
        Assert.True(found);
        Assert.NotNull(resolved);
        Assert.Equal("neon", resolved.Name);
        Assert.Equal("#00FF88", resolved.Console.Accent);
    }

    [Fact]
    public void CodaThemes_builtin_name_collision_rejects_plugin_theme_with_warning()
    {
        var palette = new ConsolePalette("#111111", "#222222", "#333333", "#444444", "#555555");
        var hijackTheme = new CodaTheme("default", "Hijacked Default", CodaThemes.Default.Tui, palette);
        var logger = new TestLogger();

        var registered = CodaThemes.RegisterPlugin(hijackTheme, logger);

        Assert.False(registered);
        // Built-in still wins.
        var found = CodaThemes.TryGet("default", out var resolved);
        Assert.True(found);
        Assert.Equal("Default", resolved!.DisplayName); // built-in display name preserved
        Assert.Contains(logger.Warnings, w => w.Contains("default", StringComparison.OrdinalIgnoreCase));
    }

    // =========================================================================
    // Test 7 — Output style loader via PluginOutputStyleLoader
    // =========================================================================

    [Fact]
    public void PluginOutputStyleLoader_registers_styles_from_directory()
    {
        var stylesDir = Directory.CreateDirectory(Path.Combine(this.tempDir, "output-styles")).FullName;
        File.WriteAllText(Path.Combine(stylesDir, "academic.json"),
            """{"name":"academic","description":"Academic writing","systemPromptSuffix":"Write academically."}""");

        var manifest = new PluginManifest { Name = "test-plugin", Version = "1.0.0" };
        var plugin = new PluginInfo("test-plugin", "1.0.0", "Test", this.tempDir)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        PluginOutputStyleLoader.RegisterAll([plugin]);

        Assert.True(BuiltInOutputStyles.IsKnown("academic"));
        var style = BuiltInOutputStyles.Resolve("academic");
        Assert.Equal("Write academically.", style.SystemPromptSuffix);
    }

    // =========================================================================
    // Test 7 — Theme loader via PluginThemeLoader
    // =========================================================================

    [Fact]
    public void PluginThemeLoader_registers_themes_from_directory_via_composer()
    {
        var themesDir = Directory.CreateDirectory(Path.Combine(this.tempDir, "themes")).FullName;
        File.WriteAllText(Path.Combine(themesDir, "retro.json"),
            """
            {
              "name":"retro",
              "displayName":"Retro",
              "consolePalette":{
                "accent":"#FFD700","dim":"#808080",
                "success":"#00FF00","warn":"#FFA500","error":"#FF0000"
              }
            }
            """);

        var manifest = new PluginManifest { Name = "test-plugin", Version = "1.0.0" };
        var plugin = new PluginInfo("test-plugin", "1.0.0", "Test", this.tempDir)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        PluginComponentComposer.Compose([plugin], this.tempDir);

        var found = CodaThemes.TryGet("retro", out var theme);
        Assert.True(found);
        Assert.Equal("#FFD700", theme!.Console.Accent);
    }

    // =========================================================================
    // Test 8 — Disabled plugin contributes nothing
    // =========================================================================

    [Fact]
    public void PluginComponentComposer_disabled_plugin_contributes_nothing()
    {
        // Create all the component files so the loader would find them if enabled.
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.tempDir, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "agent.md"),
            "---\ntype: disabled-agent\n---\nBody.");

        var hookFile = Path.Combine(this.tempDir, "hooks.json");
        File.WriteAllText(hookFile, """{"PreToolUse":[{"command":"./check.sh"}]}""");

        var mcpFile = Path.Combine(this.tempDir, "servers.json");
        File.WriteAllText(mcpFile, """{"mcpServers":{"disabled-tool":{"command":"node","args":[]}}}""");

        var stylesDir = Directory.CreateDirectory(Path.Combine(this.tempDir, "output-styles")).FullName;
        File.WriteAllText(Path.Combine(stylesDir, "disabled-style.json"),
            """{"name":"disabled-style","description":"D","systemPromptSuffix":"D."}""");

        var themesDir = Directory.CreateDirectory(Path.Combine(this.tempDir, "themes")).FullName;
        File.WriteAllText(Path.Combine(themesDir, "disabled-theme.json"),
            """{"name":"disabled-theme","displayName":"D","consolePalette":{"accent":"#000","dim":"#111","success":"#222","warn":"#333","error":"#444"}}""");

        var manifest = new PluginManifest
        {
            Name = "disabled-plugin",
            Version = "1.0.0",
            Hooks = ["hooks.json"],
            McpServers = ["servers.json"],
        };
        var plugin = new PluginInfo("disabled-plugin", "1.0.0", "Test", this.tempDir)
        {
            IsEnabled = false,
            Manifest = manifest,
        };

        var composition = PluginComponentComposer.Compose([plugin], this.tempDir);

        Assert.Empty(composition.Agents);
        Assert.Empty(composition.Hooks);
        Assert.Empty(composition.McpServers);
        Assert.False(BuiltInOutputStyles.IsKnown("disabled-style"));
        Assert.False(CodaThemes.TryGet("disabled-theme", out _));
    }

    // =========================================================================
    // Test 9 — Malformed component skipped without preventing others from loading
    // =========================================================================

    [Fact]
    public void PluginComponentComposer_malformed_component_skipped_others_still_load()
    {
        // Agent file: one valid, one without frontmatter.
        var agentsDir = Directory.CreateDirectory(Path.Combine(this.tempDir, "agents")).FullName;
        File.WriteAllText(Path.Combine(agentsDir, "valid.md"),
            "---\ntype: good-agent\ndescription: Good\n---\nGood body.");
        File.WriteAllText(Path.Combine(agentsDir, "invalid.md"),
            "No frontmatter at all — should be skipped.");

        // Output style: one valid, one invalid JSON.
        var stylesDir = Directory.CreateDirectory(Path.Combine(this.tempDir, "output-styles")).FullName;
        File.WriteAllText(Path.Combine(stylesDir, "good.json"),
            """{"name":"good-style","description":"G","systemPromptSuffix":"Good."}""");
        File.WriteAllText(Path.Combine(stylesDir, "bad.json"), "NOT JSON {{{");

        var manifest = new PluginManifest { Name = "test-plugin", Version = "1.0.0" };
        var plugin = new PluginInfo("test-plugin", "1.0.0", "Test", this.tempDir)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        // Should not throw.
        var composition = PluginComponentComposer.Compose([plugin], this.tempDir);

        // Valid agent loads.
        Assert.Contains(composition.Agents, a => a.Type == "good-agent");
        // Valid style registers.
        Assert.True(BuiltInOutputStyles.IsKnown("good-style"));
    }

    // =========================================================================
    // Test 10 — Plugin with no components behaves exactly as before
    // =========================================================================

    [Fact]
    public void PluginComponentComposer_plugin_with_no_components_produces_empty_composition()
    {
        var manifest = new PluginManifest { Name = "empty-plugin", Version = "1.0.0" };
        var plugin = new PluginInfo("empty-plugin", "1.0.0", "Empty plugin", this.tempDir)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        var composition = PluginComponentComposer.Compose([plugin], this.tempDir);

        Assert.Empty(composition.Agents);
        Assert.Empty(composition.Hooks);
        Assert.Empty(composition.McpServers);
    }

    [Fact]
    public void PluginComponentComposer_empty_plugin_list_produces_empty_composition()
    {
        var composition = PluginComponentComposer.Compose([], this.tempDir);

        Assert.Empty(composition.Agents);
        Assert.Empty(composition.Hooks);
        Assert.Empty(composition.McpServers);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>Minimal ILogger that captures warning messages as plain strings.</summary>
    private sealed class TestLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                this.Warnings.Add(formatter(state, exception));
            }
        }
    }
}
