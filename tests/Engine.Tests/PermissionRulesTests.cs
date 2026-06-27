using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Permissions;
using Coda.Agent.Settings;

namespace Engine.Tests;

/// <summary>
/// Tests for E6 — Settings + permission rules:
/// PermissionRule.Parse, PermissionRule.Matches,
/// RulesPermissionPrompt, and SettingsLoader.
/// </summary>
public sealed class PermissionRuleParseTests
{
    [Fact]
    public void Parse_tool_only_rule_returns_null_arg_pattern()
    {
        var rule = PermissionRule.Parse("edit_file");

        Assert.Equal("edit_file", rule.ToolName);
        Assert.Null(rule.ArgPattern);
    }

    [Fact]
    public void Parse_tool_with_glob_pattern_extracts_inner_pattern()
    {
        var rule = PermissionRule.Parse("run_command(git:*)");

        Assert.Equal("run_command", rule.ToolName);
        Assert.Equal("git:*", rule.ArgPattern);
    }

    [Fact]
    public void Parse_tool_with_non_glob_pattern_extracts_inner_pattern()
    {
        var rule = PermissionRule.Parse("run_command(git)");

        Assert.Equal("run_command", rule.ToolName);
        Assert.Equal("git", rule.ArgPattern);
    }

    [Fact]
    public void Parse_preserves_tool_name_case()
    {
        var rule = PermissionRule.Parse("Edit_File");

        Assert.Equal("Edit_File", rule.ToolName);
        Assert.Null(rule.ArgPattern);
    }
}

public sealed class PermissionRuleMatchesTests
{
    [Fact]
    public void Tool_only_rule_matches_any_input_for_that_tool()
    {
        var rule = PermissionRule.Parse("edit_file");

        Assert.True(rule.Matches("edit_file", "{\"path\":\"/foo/bar.txt\"}"));
    }

    [Fact]
    public void Tool_only_rule_does_not_match_different_tool()
    {
        var rule = PermissionRule.Parse("edit_file");

        Assert.False(rule.Matches("run_command", "{\"command\":\"ls\"}"));
    }

    [Fact]
    public void Tool_name_matching_is_case_insensitive()
    {
        var rule = PermissionRule.Parse("edit_file");

        Assert.True(rule.Matches("EDIT_FILE", "{\"path\":\"x\"}"));
        Assert.True(rule.Matches("Edit_File", "{\"path\":\"x\"}"));
    }

    [Fact]
    public void Glob_pattern_matches_command_starting_with_prefix()
    {
        var rule = PermissionRule.Parse("run_command(git:*)");

        Assert.True(rule.Matches("run_command", "{\"command\":\"git status\"}"));
        Assert.True(rule.Matches("run_command", "{\"command\":\"git commit -m 'msg'\"}"));
    }

    [Fact]
    public void Glob_pattern_matches_command_equal_to_prefix()
    {
        // "git" alone (bare command, no arguments) should match "git:*"
        var rule = PermissionRule.Parse("run_command(git:*)");

        Assert.True(rule.Matches("run_command", "{\"command\":\"git\"}"));
    }

    [Fact]
    public void Glob_pattern_matches_command_with_leading_whitespace_after_trim()
    {
        // Leading whitespace must be trimmed so a deny rule cannot be bypassed
        var rule = PermissionRule.Parse("run_command(git:*)");

        Assert.True(rule.Matches("run_command", "{\"command\":\"  git status\"}"));
    }

    [Fact]
    public void Glob_pattern_does_not_match_command_that_extends_prefix_without_space()
    {
        // "gitk --all" must NOT match "git:*" — no word boundary
        var rule = PermissionRule.Parse("run_command(git:*)");

        Assert.False(rule.Matches("run_command", "{\"command\":\"gitk --all\"}"));
    }

    [Fact]
    public void Glob_pattern_does_not_match_command_containing_prefix_in_non_prefix_position()
    {
        // "echo git" must NOT match "git:*" — prefix is not at the start
        var rule = PermissionRule.Parse("run_command(git:*)");

        Assert.False(rule.Matches("run_command", "{\"command\":\"echo git\"}"));
    }

    [Fact]
    public void Deny_glob_pattern_matches_command_with_leading_whitespace()
    {
        // A DENY rule for "rm:*" must not be bypassable via a leading space
        var rule = PermissionRule.Parse("run_command(rm:*)");

        Assert.True(rule.Matches("run_command", "{\"command\":\"  rm -rf /\"}"));
    }

    [Fact]
    public void Glob_pattern_does_not_match_command_with_different_prefix()
    {
        var rule = PermissionRule.Parse("run_command(git:*)");

        Assert.False(rule.Matches("run_command", "{\"command\":\"rm -rf /\"}"));
        Assert.False(rule.Matches("run_command", "{\"command\":\"npm install\"}"));
    }

    [Fact]
    public void Glob_pattern_wrong_tool_name_does_not_match()
    {
        var rule = PermissionRule.Parse("run_command(git:*)");

        Assert.False(rule.Matches("edit_file", "{\"command\":\"git status\"}"));
    }

    [Fact]
    public void Exact_token_pattern_matches_command_starting_with_token()
    {
        var rule = PermissionRule.Parse("run_command(git)");

        Assert.True(rule.Matches("run_command", "{\"command\":\"git status\"}"));
    }

    [Fact]
    public void Exact_token_pattern_does_not_match_command_with_different_start()
    {
        var rule = PermissionRule.Parse("run_command(rm)");

        Assert.False(rule.Matches("run_command", "{\"command\":\"git status\"}"));
    }

    [Fact]
    public void Pattern_falls_back_to_whole_json_when_no_command_property()
    {
        var rule = PermissionRule.Parse("run_command(git:*)");

        // No "command" property — should fall back to matching against full inputJson
        // In this case "git" is NOT in the JSON value, so no match
        Assert.False(rule.Matches("run_command", "{\"path\":\"/foo/bar\"}"));
    }

    [Fact]
    public void Pattern_falls_back_to_whole_json_when_input_is_not_valid_json()
    {
        var rule = PermissionRule.Parse("run_command(git:*)");

        // Invalid JSON — fall back to matching against raw string
        Assert.True(rule.Matches("run_command", "git status --short"));
    }
}

public sealed class RulesPermissionPromptTests
{
    private sealed class FakeTool(string name) : ITool
    {
        public string Name => name;
        public string Description => name;
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => false;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult("ok"));
    }

    private sealed class RecordingPrompt(bool answer) : IPermissionPrompt
    {
        public int Calls { get; private set; }
        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return Task.FromResult(answer);
        }
    }

    private sealed class ThrowingPrompt : IPermissionPrompt
    {
        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Inner should not be consulted.");
    }

    private static readonly ITool EditTool = new FakeTool("edit_file");
    private static readonly ITool RunTool = new FakeTool("run_command");

    [Fact]
    public async Task Deny_rule_match_returns_false()
    {
        var deny = new[] { PermissionRule.Parse("edit_file") };
        var prompt = new RulesPermissionPrompt([], deny, new ThrowingPrompt());

        var result = await prompt.RequestAsync(EditTool, "{}", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task Deny_takes_precedence_over_allow_for_same_tool()
    {
        var allow = new[] { PermissionRule.Parse("edit_file") };
        var deny = new[] { PermissionRule.Parse("edit_file") };
        var prompt = new RulesPermissionPrompt(allow, deny, new ThrowingPrompt());

        var result = await prompt.RequestAsync(EditTool, "{}", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task Allow_rule_match_returns_true_without_consulting_inner()
    {
        var allow = new[] { PermissionRule.Parse("edit_file") };
        var prompt = new RulesPermissionPrompt(allow, [], new ThrowingPrompt());

        var result = await prompt.RequestAsync(EditTool, "{}", CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task No_rule_match_delegates_to_inner_prompt()
    {
        var inner = new RecordingPrompt(answer: true);
        var prompt = new RulesPermissionPrompt([], [], inner);

        var result = await prompt.RequestAsync(RunTool, "{\"command\":\"npm install\"}", CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task No_rule_match_passes_original_args_to_inner()
    {
        string? capturedInput = null;
        ITool? capturedTool = null;
        var inner = new CapturePrompt((tool, input) =>
        {
            capturedTool = tool;
            capturedInput = input;
        });
        var prompt = new RulesPermissionPrompt([], [], inner);

        await prompt.RequestAsync(RunTool, "{\"command\":\"ls\"}", CancellationToken.None);

        Assert.Same(RunTool, capturedTool);
        Assert.Equal("{\"command\":\"ls\"}", capturedInput);
    }

    [Fact]
    public async Task Allow_rule_with_arg_pattern_matches_specific_command()
    {
        var allow = new[] { PermissionRule.Parse("run_command(git:*)") };
        var prompt = new RulesPermissionPrompt(allow, [], new ThrowingPrompt());

        var result = await prompt.RequestAsync(RunTool, "{\"command\":\"git status\"}", CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task Allow_rule_with_arg_pattern_does_not_match_other_command()
    {
        var allow = new[] { PermissionRule.Parse("run_command(git:*)") };
        var inner = new RecordingPrompt(answer: false);
        var prompt = new RulesPermissionPrompt(allow, [], inner);

        var result = await prompt.RequestAsync(RunTool, "{\"command\":\"rm -rf /\"}", CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Deny_rule_with_arg_pattern_blocks_specific_command()
    {
        var deny = new[] { PermissionRule.Parse("run_command(rm:*)") };
        var inner = new RecordingPrompt(answer: true);
        var prompt = new RulesPermissionPrompt([], deny, inner);

        var result = await prompt.RequestAsync(RunTool, "{\"command\":\"rm -rf /\"}", CancellationToken.None);

        Assert.False(result);
        Assert.Equal(0, inner.Calls);
    }

    private sealed class CapturePrompt(Action<ITool, string> capture) : IPermissionPrompt
    {
        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
        {
            capture(tool, inputPreview);
            return Task.FromResult(true);
        }
    }
}

public sealed class SettingsLoaderTests
{
    [Fact]
    public void Load_returns_empty_when_no_settings_files_exist()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var userDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(userDir);

        try
        {
            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Empty(settings.Allow);
            Assert.Empty(settings.Deny);
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void Load_reads_project_settings_json()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var userDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var codaDir = Path.Combine(projectDir, ".coda");
        Directory.CreateDirectory(codaDir);
        Directory.CreateDirectory(userDir);

        try
        {
            File.WriteAllText(
                Path.Combine(codaDir, "settings.json"),
                """
                {
                  "permissions": {
                    "allow": ["edit_file", "run_command(git:*)"],
                    "deny": ["run_command(rm:*)"]
                  }
                }
                """);

            // userDir has no .coda/settings.json — only project settings loaded
            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Equal(["edit_file", "run_command(git:*)"], settings.Allow);
            Assert.Equal(["run_command(rm:*)"], settings.Deny);
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void Load_reads_user_settings_json()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var userDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var userCodaDir = Path.Combine(userDir, ".coda");
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(userCodaDir);

        try
        {
            File.WriteAllText(
                Path.Combine(userCodaDir, "settings.json"),
                """
                {
                  "permissions": {
                    "allow": ["read_file"],
                    "deny": []
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Contains("read_file", settings.Allow);
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void Load_merges_user_and_project_settings_project_appended_after_user()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var userDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projectCodaDir = Path.Combine(projectDir, ".coda");
        var userCodaDir = Path.Combine(userDir, ".coda");
        Directory.CreateDirectory(projectCodaDir);
        Directory.CreateDirectory(userCodaDir);

        try
        {
            File.WriteAllText(
                Path.Combine(userCodaDir, "settings.json"),
                """
                {
                  "permissions": {
                    "allow": ["user_allow"],
                    "deny": ["user_deny"]
                  }
                }
                """);

            File.WriteAllText(
                Path.Combine(projectCodaDir, "settings.json"),
                """
                {
                  "permissions": {
                    "allow": ["project_allow"],
                    "deny": ["project_deny"]
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            // user comes first, project appended after
            Assert.Equal(["user_allow", "project_allow"], settings.Allow);
            Assert.Equal(["user_deny", "project_deny"], settings.Deny);
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void Load_returns_empty_when_settings_json_is_corrupt()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var codaDir = Path.Combine(tempDir, ".coda");
        Directory.CreateDirectory(codaDir);

        try
        {
            File.WriteAllText(Path.Combine(codaDir, "settings.json"), "{ this is not json }}}");

            var settings = SettingsLoader.Load(tempDir, userSettingsDir: tempDir);

            Assert.Empty(settings.Allow);
            Assert.Empty(settings.Deny);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Load_tolerates_missing_permissions_section()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var codaDir = Path.Combine(tempDir, ".coda");
        Directory.CreateDirectory(codaDir);

        try
        {
            File.WriteAllText(Path.Combine(codaDir, "settings.json"), "{}");

            var settings = SettingsLoader.Load(tempDir, userSettingsDir: tempDir);

            Assert.Empty(settings.Allow);
            Assert.Empty(settings.Deny);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CodaSettings_Empty_has_no_rules()
    {
        Assert.Empty(CodaSettings.Empty.Allow);
        Assert.Empty(CodaSettings.Empty.Deny);
    }
}
