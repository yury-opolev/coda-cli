using System.Text.Json;
using Coda.Agent;
using Coda.Tui.Skills;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Tests;

/// <summary>Tests for the model-invocable skill tool (Phase 1 — Item 1–5).</summary>
public sealed class SkillToolTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static SkillDefinition Skill(
        string name,
        string description = "A skill.",
        string body = "Do something.",
        bool disableModelInvocation = false,
        bool userInvocable = true,
        string? whenToUse = null,
        IReadOnlyList<string>? arguments = null) =>
        new(name, description, body)
        {
            DisableModelInvocation = disableModelInvocation,
            UserInvocable = userInvocable,
            WhenToUse = whenToUse,
            Arguments = arguments ?? [],
        };

    private static async Task<ToolResult> InvokeAsync(
        SkillTool tool,
        string name,
        string? arguments = null)
    {
        var props = new Dictionary<string, object?> { ["name"] = name };
        if (arguments is not null)
        {
            props["arguments"] = arguments;
        }

        var json = JsonSerializer.Serialize(props);
        var element = JsonDocument.Parse(json).RootElement;
        return await tool.ExecuteAsync(element, new ToolContext(Directory.GetCurrentDirectory()));
    }

    // ── Item 1 — SkillTool enum ────────────────────────────────────────────

    [Fact]
    public void Enum_contains_exactly_model_invocable_skills()
    {
        var skills = new[]
        {
            Skill("alpha"),
            Skill("beta", disableModelInvocation: true),
            Skill("gamma"),
        };
        var state = new SkillSessionState();
        var tool = new SkillTool(skills.Where(s => !s.DisableModelInvocation).ToList(), state);

        var schema = JsonDocument.Parse(tool.InputSchemaJson).RootElement;
        var enumValues = schema
            .GetProperty("properties")
            .GetProperty("name")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

        Assert.Equal(["alpha", "gamma"], enumValues);
    }

    [Fact]
    public void CreateOrNull_returns_null_when_no_model_invocable_skills()
    {
        var skills = new[]
        {
            Skill("alpha", disableModelInvocation: true),
            Skill("beta", disableModelInvocation: true),
        };
        var state = new SkillSessionState();

        var tool = SkillTool.CreateOrNull(skills, state);

        Assert.Null(tool);
    }

    [Fact]
    public void CreateOrNull_returns_tool_when_at_least_one_model_invocable_skill()
    {
        var skills = new[]
        {
            Skill("alpha", disableModelInvocation: true),
            Skill("beta"),
        };
        var state = new SkillSessionState();

        var tool = SkillTool.CreateOrNull(skills, state);

        Assert.NotNull(tool);
    }

    [Fact]
    public void CreateOrNull_enum_excludes_disable_model_invocation_skills()
    {
        var skills = new[]
        {
            Skill("alpha"),
            Skill("beta", disableModelInvocation: true),
        };
        var state = new SkillSessionState();

        var tool = SkillTool.CreateOrNull(skills, state)!;
        var schema = JsonDocument.Parse(tool.InputSchemaJson).RootElement;
        var enumValues = schema
            .GetProperty("properties")
            .GetProperty("name")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

        Assert.Equal(["alpha"], enumValues);
        Assert.DoesNotContain("beta", enumValues);
    }

    [Fact]
    public void Schema_has_required_name()
    {
        var skills = new[] { Skill("alpha") };
        var state = new SkillSessionState();
        var tool = new SkillTool(skills, state);

        var schema = JsonDocument.Parse(tool.InputSchemaJson).RootElement;
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Contains("name", required);
    }

    [Fact]
    public void Schema_has_optional_arguments_property()
    {
        var skills = new[] { Skill("alpha") };
        var state = new SkillSessionState();
        var tool = new SkillTool(skills, state);

        var schema = JsonDocument.Parse(tool.InputSchemaJson).RootElement;
        var hasArguments = schema.GetProperty("properties").TryGetProperty("arguments", out _);

        Assert.True(hasArguments);
    }

    // ── Item 1 — execution ────────────────────────────────────────────────

    [Fact]
    public async Task Unknown_skill_name_returns_error_naming_valid_options()
    {
        var skills = new[] { Skill("alpha"), Skill("beta") };
        var state = new SkillSessionState();
        var tool = new SkillTool(skills, state);

        var result = await InvokeAsync(tool, "nonexistent");

        Assert.True(result.IsError);
        Assert.Contains("alpha", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("beta", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Known_skill_resolves_case_insensitively()
    {
        var skills = new[] { Skill("MySkill", body: "The body.") };
        var state = new SkillSessionState();
        var tool = new SkillTool(skills, state);

        var result = await InvokeAsync(tool, "MYSKILL");

        Assert.False(result.IsError);
        Assert.Contains("The body.", result.Content);
    }

    [Fact]
    public async Task First_invocation_returns_body()
    {
        var skills = new[] { Skill("alpha", body: "Alpha body.") };
        var state = new SkillSessionState();
        var tool = new SkillTool(skills, state);

        var result = await InvokeAsync(tool, "alpha");

        Assert.False(result.IsError);
        Assert.Equal("Alpha body.", result.Content);
    }

    [Fact]
    public async Task Identical_re_invocation_returns_already_loaded_note()
    {
        var skills = new[] { Skill("alpha", body: "Alpha body.") };
        var state = new SkillSessionState();
        var tool = new SkillTool(skills, state);

        await InvokeAsync(tool, "alpha");
        var result = await InvokeAsync(tool, "alpha");

        Assert.False(result.IsError);
        // Should be an "already loaded" note, NOT the full body again
        Assert.DoesNotContain("Alpha body.", result.Content);
        Assert.Contains("alpha", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Re_invocation_with_different_arguments_returns_new_body()
    {
        var skills = new[]
        {
            Skill("alpha", body: "Review $1.", arguments: ["target"])
        };
        var state = new SkillSessionState();
        var tool = new SkillTool(skills, state);

        await InvokeAsync(tool, "alpha", "file1.cs");
        var result = await InvokeAsync(tool, "alpha", "file2.cs");

        Assert.False(result.IsError);
        Assert.Contains("file2.cs", result.Content);
    }

    // ── Item 2 — argument binding opt-in rule ─────────────────────────────

    [Fact]
    public async Task Body_with_literal_dollar_and_no_arguments_is_returned_unchanged()
    {
        // $variable in body but no 'arguments' declared in skill and none passed → no substitution
        var skills = new[] { Skill("alpha", body: "Cost is $100 total.") };
        var state = new SkillSessionState();
        var tool = new SkillTool(skills, state);

        var result = await InvokeAsync(tool, "alpha");

        Assert.False(result.IsError);
        Assert.Equal("Cost is $100 total.", result.Content);
    }

    [Fact]
    public async Task Argument_substitution_fires_when_arguments_string_supplied()
    {
        var skills = new[] { Skill("alpha", body: "Translate: $1.") };
        var state = new SkillSessionState();
        var tool = new SkillTool(skills, state);

        var result = await InvokeAsync(tool, "alpha", "hello");

        Assert.False(result.IsError);
        Assert.Contains("hello", result.Content);
    }

    [Fact]
    public async Task Argument_substitution_fires_when_skill_declares_arguments()
    {
        var skills = new[]
        {
            Skill("alpha", body: "Target: $file.", arguments: ["file"])
        };
        var state = new SkillSessionState();
        var tool = new SkillTool(skills, state);

        // Even with no 'arguments' input, the skill declares arguments → Bind runs
        var result = await InvokeAsync(tool, "alpha");

        Assert.False(result.IsError);
        // $file → empty (no value supplied) but substitution ran
        Assert.Equal("Target: .", result.Content);
    }

    // ── Item 2 — description cap ──────────────────────────────────────────

    [Fact]
    public void Description_contains_preamble_and_skill_entries()
    {
        var skills = new[]
        {
            Skill("alpha", "Does alpha stuff"),
            Skill("beta", "Does beta stuff"),
        };
        var state = new SkillSessionState();
        var tool = new SkillTool(skills, state);

        Assert.Contains("alpha", tool.Description);
        Assert.Contains("Does alpha stuff", tool.Description);
        Assert.Contains("beta", tool.Description);
    }

    [Fact]
    public void Description_appends_when_to_use_when_present()
    {
        var skills = new[]
        {
            Skill("alpha", "Does alpha stuff", whenToUse: "Use when refactoring"),
        };
        var state = new SkillSessionState();
        var tool = new SkillTool(skills, state);

        Assert.Contains("Use when refactoring", tool.Description);
    }

    [Fact]
    public void Description_respects_cap_and_drops_are_noted()
    {
        // Build many skills with long descriptions to exceed a small cap
        var skills = Enumerable.Range(1, 10)
            .Select(i => Skill($"skill-{i:D2}", new string('x', 100)))
            .ToList();
        var state = new SkillSessionState();
        // Small cap: about 250 chars; preamble alone is ~50 chars, so only ~2 skills fit
        var tool = new SkillTool(skills, state, descriptionCap: 250);

        Assert.True(tool.Description.Length <= 300); // small over-run for the "not listed" line
        Assert.Contains("not listed", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dropped_skills_remain_in_enum_when_description_cap_exceeded()
    {
        var skills = Enumerable.Range(1, 5)
            .Select(i => Skill($"skill-{i:D2}", new string('x', 200)))
            .ToList();
        var state = new SkillSessionState();
        var tool = new SkillTool(skills, state, descriptionCap: 100);

        var schema = JsonDocument.Parse(tool.InputSchemaJson).RootElement;
        var enumValues = schema
            .GetProperty("properties")
            .GetProperty("name")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        // All 5 skills should still be in the enum regardless of description cap
        Assert.Equal(5, enumValues.Length);
    }

    // ── Tool properties ───────────────────────────────────────────────────

    [Fact]
    public void Name_is_skill()
    {
        var tool = new SkillTool([Skill("alpha")], new SkillSessionState());
        Assert.Equal("skill", tool.Name);
    }

    [Fact]
    public void IsReadOnly_is_true()
    {
        var tool = new SkillTool([Skill("alpha")], new SkillSessionState());
        Assert.True(tool.IsReadOnly);
    }
}

/// <summary>Tests for <see cref="SkillSessionState"/> (Item 3 and 4).</summary>
public sealed class SkillSessionStateTests
{
    // ── TryLoad behavior ─────────────────────────────────────────────────

    [Fact]
    public void First_load_returns_true_and_body()
    {
        var state = new SkillSessionState();

        var (isFirst, content) = state.TryLoad("skill1", "body text");

        Assert.True(isFirst);
        Assert.Equal("body text", content);
    }

    [Fact]
    public void Identical_re_invocation_returns_false_and_already_loaded_note()
    {
        var state = new SkillSessionState();
        state.TryLoad("skill1", "body text");

        var (isFirst, content) = state.TryLoad("skill1", "body text");

        Assert.False(isFirst);
        Assert.DoesNotContain("body text", content);
        Assert.Contains("skill1", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Different_body_re_invocation_returns_false_and_new_body()
    {
        var state = new SkillSessionState();
        state.TryLoad("skill1", "original body");

        var (isFirst, content) = state.TryLoad("skill1", "updated body");

        Assert.False(isFirst);
        Assert.Equal("updated body", content);
    }

    [Fact]
    public void Case_insensitive_skill_name_matching()
    {
        var state = new SkillSessionState();
        state.TryLoad("MySkill", "first load");

        var (isFirst, _) = state.TryLoad("MYSKILL", "first load");

        Assert.False(isFirst); // same skill, same body → already loaded
    }

    [Fact]
    public void Different_skills_tracked_independently()
    {
        var state = new SkillSessionState();

        var (first1, _) = state.TryLoad("skill1", "body1");
        var (first2, _) = state.TryLoad("skill2", "body2");

        Assert.True(first1);
        Assert.True(first2);
    }

    // ── GetReattachContent ────────────────────────────────────────────────

    [Fact]
    public void GetReattachContent_empty_when_no_skills_loaded()
    {
        var state = new SkillSessionState();

        Assert.Equal(string.Empty, state.GetReattachContent());
    }

    [Fact]
    public void GetReattachContent_returns_most_recently_used_first()
    {
        var state = new SkillSessionState();
        state.TryLoad("first", "First body");
        state.TryLoad("second", "Second body");
        state.TryLoad("third", "Third body");

        var content = state.GetReattachContent();

        // "third" should appear before "second" before "first"
        var idxThird = content.IndexOf("Third body", StringComparison.Ordinal);
        var idxSecond = content.IndexOf("Second body", StringComparison.Ordinal);
        var idxFirst = content.IndexOf("First body", StringComparison.Ordinal);

        Assert.True(idxThird < idxSecond);
        Assert.True(idxSecond < idxFirst);
    }

    [Fact]
    public void GetReattachContent_respects_budget()
    {
        var state = new SkillSessionState();
        state.TryLoad("skill1", new string('a', 50));
        state.TryLoad("skill2", new string('b', 50));
        state.TryLoad("skill3", new string('c', 50));

        // Budget = 80, each body is 50 chars. Only first (most-recent = skill3) fits cleanly.
        // skill3=50 + separator(2) + skill2=50 = 102 > 80, so only skill3 is included.
        var content = state.GetReattachContent(charBudget: 80);

        Assert.Contains(new string('c', 50), content);
        Assert.DoesNotContain(new string('b', 50), content);
    }

    [Fact]
    public void GetReattachContent_uses_most_recent_rendered_body_per_skill()
    {
        var state = new SkillSessionState();
        state.TryLoad("skill1", "original body");
        state.TryLoad("skill1", "updated body"); // different args → new body

        var content = state.GetReattachContent();

        Assert.Contains("updated body", content);
        Assert.DoesNotContain("original body", content);
    }
}

/// <summary>Tests for the two opt-outs (Item 5): frontmatter parsing and loader behavior.</summary>
public sealed class SkillOptOutTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => this.Entries.Add((logLevel, formatter(state, exception)));
    }

    // ── Frontmatter parsing ───────────────────────────────────────────────

    [Fact]
    public void DisableModelInvocation_defaults_to_false()
    {
        var skill = SkillLoader.ParseSkillFile(
            "---\nname: test\ndescription: Test\n---\nBody.",
            "test");

        Assert.False(skill.DisableModelInvocation);
    }

    [Fact]
    public void UserInvocable_defaults_to_true()
    {
        var skill = SkillLoader.ParseSkillFile(
            "---\nname: test\ndescription: Test\n---\nBody.",
            "test");

        Assert.True(skill.UserInvocable);
    }

    [Fact]
    public void DisableModelInvocation_true_parsed_correctly()
    {
        var skill = SkillLoader.ParseSkillFile(
            "---\nname: test\ndescription: Test\ndisable-model-invocation: true\n---\nBody.",
            "test");

        Assert.True(skill.DisableModelInvocation);
    }

    [Fact]
    public void UserInvocable_false_parsed_correctly()
    {
        var skill = SkillLoader.ParseSkillFile(
            "---\nname: test\ndescription: Test\nuser-invocable: false\n---\nBody.",
            "test");

        Assert.False(skill.UserInvocable);
    }

    [Fact]
    public void Disable_model_invocation_skill_absent_from_tool_enum()
    {
        var skills = new[]
        {
            SkillLoader.ParseSkillFile(
                "---\nname: tool-skip\ndescription: Skip me\ndisable-model-invocation: true\n---\nBody.",
                "tool-skip"),
            SkillLoader.ParseSkillFile(
                "---\nname: tool-include\ndescription: Include me\n---\nBody.",
                "tool-include"),
        };
        var state = new SkillSessionState();
        var tool = SkillTool.CreateOrNull(skills, state);

        Assert.NotNull(tool);
        var schema = JsonDocument.Parse(tool.InputSchemaJson).RootElement;
        var enumValues = schema
            .GetProperty("properties")
            .GetProperty("name")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        Assert.DoesNotContain("tool-skip", enumValues);
        Assert.Contains("tool-include", enumValues);
    }

    [Fact]
    public void User_invocable_false_skill_is_present_in_tool_enum()
    {
        // user-invocable: false → model-only → present in tool enum
        var skills = new[]
        {
            SkillLoader.ParseSkillFile(
                "---\nname: model-only\ndescription: Model only\nuser-invocable: false\n---\nBody.",
                "model-only"),
        };
        var state = new SkillSessionState();
        var tool = SkillTool.CreateOrNull(skills, state);

        Assert.NotNull(tool);
        var schema = JsonDocument.Parse(tool.InputSchemaJson).RootElement;
        var enumValues = schema
            .GetProperty("properties")
            .GetProperty("name")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        Assert.Contains("model-only", enumValues);
    }

    // ── Both exclusions ───────────────────────────────────────────────────

    [Fact]
    public void Both_exclusions_logs_warning_and_makes_skill_user_invocable()
    {
        var logger = new CapturingLogger();
        var tempDir = Directory.CreateTempSubdirectory("coda-opt-out-test-").FullName;
        try
        {
            var skillDir = Path.Combine(tempDir, ".coda", "skills", "conflicted");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(
                Path.Combine(skillDir, "SKILL.md"),
                "---\nname: conflicted\ndescription: Both\ndisable-model-invocation: true\nuser-invocable: false\n---\nBody.");

            var skills = SkillLoader.Load(
                tempDir,
                userSkillsDir: Path.Combine(tempDir, "_no_user"),
                claudeSkillsDir: Path.Combine(tempDir, "_no_claude"),
                logger: logger);

            Assert.Single(skills);
            var skill = skills[0];
            // Warning should have been logged
            Assert.Contains(logger.Entries, e =>
                e.Level == LogLevel.Warning &&
                e.Message.Contains("conflicted", StringComparison.OrdinalIgnoreCase));
            // Should be treated as user-invocable
            Assert.True(skill.UserInvocable);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }
}
