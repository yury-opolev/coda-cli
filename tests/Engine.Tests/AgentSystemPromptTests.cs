using Coda.Agent;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// The wire system prompt uses Coda's own tool names; these tests pin its
/// structure and the provider-prefix gating behaviour.
/// </summary>
public sealed class AgentSystemPromptTests
{
    private const string Cwd = "/work/project";

    [Fact]
    public void Build_with_prefix_starts_with_claude_code_prefix()
    {
        var prompt = AgentSystemPrompt.Build(Cwd, includeAnthropicSystemPrefix: true);

        Assert.StartsWith(AnthropicModels.AnthropicSystemPrefix, prompt);
    }

    [Fact]
    public void Build_without_prefix_omits_claude_code_prefix()
    {
        var prompt = AgentSystemPrompt.Build(Cwd, includeAnthropicSystemPrefix: false);

        Assert.DoesNotContain(AnthropicModels.AnthropicSystemPrefix, prompt);
        // The agent instructions themselves are still present.
        Assert.Contains("You are an interactive agent that helps users", prompt);
    }

    [Fact]
    public void Build_includes_intro_with_cyber_risk_and_url_guidance()
    {
        var prompt = AgentSystemPrompt.Build(Cwd);

        Assert.Contains("You are an interactive agent that helps users with software engineering tasks.", prompt);
        Assert.Contains("Assist with authorized security testing, defensive security, CTF challenges", prompt);
        Assert.Contains("You must NEVER generate or guess URLs", prompt);
    }

    [Fact]
    public void Build_includes_system_and_doing_tasks_sections()
    {
        var prompt = AgentSystemPrompt.Build(Cwd);

        Assert.Contains("# System", prompt);
        Assert.Contains("# Doing tasks", prompt);
        Assert.Contains("do not propose changes to code you haven't read", prompt);
        Assert.Contains("Do not create files unless they're absolutely necessary", prompt);
    }

    [Fact]
    public void Build_includes_executing_actions_with_care_section_verbatim()
    {
        var prompt = AgentSystemPrompt.Build(Cwd);

        Assert.Contains("# Executing actions with care", prompt);
        Assert.Contains("Carefully consider the reversibility and blast radius of actions.", prompt);
        Assert.Contains("measure twice, cut once", prompt);
    }

    [Fact]
    public void Build_using_your_tools_section_uses_coda_tool_names()
    {
        var prompt = AgentSystemPrompt.Build(Cwd);

        Assert.Contains("# Using your tools", prompt);
        Assert.Contains("To read files use read_file instead of cat, head, tail, or sed", prompt);
        Assert.Contains("To edit files use edit_file instead of sed or awk", prompt);
        Assert.Contains("To create files use write_file", prompt);
        Assert.Contains("To search for files use glob", prompt);
        Assert.Contains("To search the content of files, use grep", prompt);
        // Internal tool-name constants must not leak into the prompt.
        Assert.DoesNotContain("BASH_TOOL_NAME", prompt);
        Assert.DoesNotContain("FILE_READ_TOOL_NAME", prompt);
    }

    [Fact]
    public void Build_using_your_tools_keeps_run_command_line_verbatim()
    {
        var prompt = AgentSystemPrompt.Build(Cwd);

        // Original renders BASH_TOOL_NAME inline: "Do NOT use the run_command to run commands".
        Assert.Contains("Do NOT use the run_command to run commands when a relevant dedicated tool is provided.", prompt);
    }

    [Fact]
    public void Build_task_subagent_bullet_uses_adapted_wording()
    {
        var prompt = AgentSystemPrompt.Build(Cwd);

        // First sentence adapted to Coda's generic task tool.
        Assert.Contains("Use the task tool to delegate a self-contained subtask to a subagent that works autonomously and reports back.", prompt);
        Assert.Contains("Subagents are valuable for parallelizing independent queries or for protecting the main context window from excessive results", prompt);
        Assert.Contains("avoid duplicating work that subagents are already doing", prompt);
    }

    [Fact]
    public void Build_executing_actions_first_paragraph_is_verbatim()
    {
        var prompt = AgentSystemPrompt.Build(Cwd);

        const string firstParagraph =
            "Carefully consider the reversibility and blast radius of actions. Generally you can freely take local, reversible actions like editing files or running tests. But for actions that are hard to reverse, affect shared systems beyond your local environment, or could otherwise be risky or destructive, check with the user before proceeding.";
        Assert.Contains(firstParagraph, prompt);
    }

    [Fact]
    public void Build_includes_tone_and_style_with_code_reference_pattern()
    {
        var prompt = AgentSystemPrompt.Build(Cwd);

        Assert.Contains("# Tone and style", prompt);
        Assert.Contains("file_path:line_number", prompt);
        Assert.Contains("Only use emojis if the user explicitly requests it.", prompt);
    }

    [Fact]
    public void Build_includes_output_efficiency_section()
    {
        var prompt = AgentSystemPrompt.Build(Cwd);

        Assert.Contains("# Output efficiency", prompt);
        Assert.Contains("Go straight to the point", prompt);
    }

    [Fact]
    public void Build_includes_environment_with_working_directory()
    {
        var prompt = AgentSystemPrompt.Build(Cwd);

        Assert.Contains("# Environment", prompt);
        Assert.Contains(Cwd, prompt);
    }

    [Fact]
    public void Build_omits_internal_only_and_product_specific_guidance()
    {
        var prompt = AgentSystemPrompt.Build(Cwd);

        // Internal / product-specific lines that must not appear.
        Assert.DoesNotContain("USER_TYPE", prompt);
        Assert.DoesNotContain("claude-code-feedback", prompt);
        Assert.DoesNotContain("/share", prompt);
        Assert.DoesNotContain("/issue", prompt);
        Assert.DoesNotContain("ISSUES_EXPLAINER", prompt);
    }

    [Fact]
    public void Build_appends_project_context_section_when_provided()
    {
        var prompt = AgentSystemPrompt.Build(Cwd, includeAnthropicSystemPrefix: true, projectContext: "Always use 4 spaces.");
        Assert.Contains("# Project context", prompt);
        Assert.Contains("Always use 4 spaces.", prompt);
    }

    [Fact]
    public void Build_without_project_context_has_no_project_section()
    {
        var prompt = AgentSystemPrompt.Build(Cwd);
        Assert.DoesNotContain("# Project context", prompt);
    }

    [Fact]
    public void BuildSubagent_keeps_prefix_gating_and_reports_back()
    {
        var withPrefix = AgentSystemPrompt.BuildSubagent(Cwd, includeAnthropicSystemPrefix: true);
        var withoutPrefix = AgentSystemPrompt.BuildSubagent(Cwd, includeAnthropicSystemPrefix: false);

        Assert.StartsWith(AnthropicModels.AnthropicSystemPrefix, withPrefix);
        Assert.DoesNotContain(AnthropicModels.AnthropicSystemPrefix, withoutPrefix);
        Assert.Contains(Cwd, withPrefix);
    }
}
