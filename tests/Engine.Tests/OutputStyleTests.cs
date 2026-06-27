using Coda.Agent;
using Coda.Agent.OutputStyles;

namespace Engine.Tests;

/// <summary>Tests for output-style resolution and system-prompt injection.</summary>
public sealed class OutputStyleTests
{
    private const string Cwd = "/work/project";

    // ── BuiltInOutputStyles.Resolve ──────────────────────────────────────────

    [Fact]
    public void Resolve_null_returns_default_style()
    {
        var style = BuiltInOutputStyles.Resolve(null);
        Assert.Equal("default", style.Name);
    }

    [Fact]
    public void Resolve_empty_string_returns_default_style()
    {
        var style = BuiltInOutputStyles.Resolve(string.Empty);
        Assert.Equal("default", style.Name);
    }

    [Fact]
    public void Resolve_default_name_returns_default_style()
    {
        var style = BuiltInOutputStyles.Resolve("default");
        Assert.Equal("default", style.Name);
    }

    [Fact]
    public void Resolve_unknown_name_returns_default_style()
    {
        var style = BuiltInOutputStyles.Resolve("nonexistent-style");
        Assert.Equal("default", style.Name);
    }

    [Fact]
    public void Resolve_concise_returns_concise_style()
    {
        var style = BuiltInOutputStyles.Resolve("concise");
        Assert.Equal("concise", style.Name);
    }

    [Fact]
    public void Resolve_is_case_insensitive()
    {
        var style = BuiltInOutputStyles.Resolve("CONCISE");
        Assert.Equal("concise", style.Name);

        var style2 = BuiltInOutputStyles.Resolve("Explanatory");
        Assert.Equal("explanatory", style2.Name);

        var style3 = BuiltInOutputStyles.Resolve("CODE-REVIEWER");
        Assert.Equal("code-reviewer", style3.Name);
    }

    [Fact]
    public void Resolve_explanatory_returns_explanatory_style()
    {
        var style = BuiltInOutputStyles.Resolve("explanatory");
        Assert.Equal("explanatory", style.Name);
    }

    [Fact]
    public void Resolve_code_reviewer_returns_code_reviewer_style()
    {
        var style = BuiltInOutputStyles.Resolve("code-reviewer");
        Assert.Equal("code-reviewer", style.Name);
    }

    // ── BuiltInOutputStyles.All ──────────────────────────────────────────────

    [Fact]
    public void All_contains_exactly_four_styles()
    {
        Assert.Equal(4, BuiltInOutputStyles.All.Count);
    }

    [Fact]
    public void All_contains_default_concise_explanatory_code_reviewer()
    {
        var names = BuiltInOutputStyles.All.Select(s => s.Name).ToList();
        Assert.Contains("default", names);
        Assert.Contains("concise", names);
        Assert.Contains("explanatory", names);
        Assert.Contains("code-reviewer", names);
    }

    [Fact]
    public void Default_style_has_empty_suffix()
    {
        var style = BuiltInOutputStyles.Resolve("default");
        Assert.Equal(string.Empty, style.SystemPromptSuffix);
    }

    [Fact]
    public void Non_default_styles_have_non_empty_descriptions_and_suffixes()
    {
        foreach (var style in BuiltInOutputStyles.All.Where(s => s.Name != "default"))
        {
            Assert.False(string.IsNullOrWhiteSpace(style.Description),
                $"Style '{style.Name}' should have a non-empty description.");
            Assert.False(string.IsNullOrWhiteSpace(style.SystemPromptSuffix),
                $"Style '{style.Name}' should have a non-empty SystemPromptSuffix.");
        }
    }

    // ── AgentSystemPrompt.Build — output style section ───────────────────────

    [Fact]
    public void Build_with_output_style_suffix_appends_output_style_section()
    {
        var prompt = AgentSystemPrompt.Build(Cwd, includeAnthropicSystemPrefix: false, outputStyleSuffix: "Be very terse.");

        Assert.Contains("# Output style", prompt);
        Assert.Contains("Be very terse.", prompt);
    }

    [Fact]
    public void Build_without_output_style_suffix_omits_output_style_section()
    {
        var prompt = AgentSystemPrompt.Build(Cwd);

        Assert.DoesNotContain("# Output style", prompt);
    }

    [Fact]
    public void Build_with_null_output_style_suffix_omits_output_style_section()
    {
        var prompt = AgentSystemPrompt.Build(Cwd, outputStyleSuffix: null);

        Assert.DoesNotContain("# Output style", prompt);
    }

    [Fact]
    public void Build_with_empty_output_style_suffix_omits_output_style_section()
    {
        var prompt = AgentSystemPrompt.Build(Cwd, outputStyleSuffix: string.Empty);

        Assert.DoesNotContain("# Output style", prompt);
    }

    [Fact]
    public void Build_with_whitespace_output_style_suffix_omits_output_style_section()
    {
        var prompt = AgentSystemPrompt.Build(Cwd, outputStyleSuffix: "   ");

        Assert.DoesNotContain("# Output style", prompt);
    }

    [Fact]
    public void Build_output_style_section_appended_after_project_context_when_both_provided()
    {
        var prompt = AgentSystemPrompt.Build(
            Cwd,
            includeAnthropicSystemPrefix: false,
            projectContext: "Use 4 spaces.",
            outputStyleSuffix: "Keep it terse.");

        var projectContextIndex = prompt.IndexOf("# Project context", StringComparison.Ordinal);
        var outputStyleIndex = prompt.IndexOf("# Output style", StringComparison.Ordinal);

        Assert.True(projectContextIndex >= 0, "# Project context section should be present.");
        Assert.True(outputStyleIndex >= 0, "# Output style section should be present.");
        Assert.True(outputStyleIndex > projectContextIndex,
            "# Output style section should appear after # Project context section.");
    }

    [Fact]
    public void Build_existing_tests_unaffected_by_new_param_project_context_still_works()
    {
        var prompt = AgentSystemPrompt.Build(Cwd, includeAnthropicSystemPrefix: true, projectContext: "Always use 4 spaces.");

        Assert.Contains("# Project context", prompt);
        Assert.Contains("Always use 4 spaces.", prompt);
        Assert.DoesNotContain("# Output style", prompt);
    }

    [Fact]
    public void Build_with_concise_style_suffix_appears_in_prompt()
    {
        var style = BuiltInOutputStyles.Resolve("concise");

        var prompt = AgentSystemPrompt.Build(Cwd, outputStyleSuffix: style.SystemPromptSuffix);

        Assert.Contains("# Output style", prompt);
        Assert.Contains(style.SystemPromptSuffix, prompt);
    }
}
