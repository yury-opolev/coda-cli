using Coda.Agent;
using Coda.Agent.OutputStyles;
using Coda.Sdk;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;

namespace Engine.Tests;

public sealed class EffectiveSystemPromptTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_effective_prompt_").FullName;

    [Fact]
    public void Resolve_returns_explicit_override_byte_for_byte_without_normal_prompt_sections()
    {
        File.WriteAllText(Path.Combine(this.root, "CLAUDE.md"), "PROJECT-CONTEXT-MARKER");
        const string exact = " exact override\nwith whitespace ";

        var prompt = EffectiveSystemPrompt.Resolve(this.Options() with
        {
            SystemPromptOverride = exact,
            OutputStyle = "concise",
        });

        Assert.Equal(exact, prompt);
        Assert.DoesNotContain("You are an interactive agent", prompt);
        Assert.DoesNotContain("PROJECT-CONTEXT-MARKER", prompt);
        Assert.DoesNotContain(BuiltInOutputStyles.Resolve("concise").SystemPromptSuffix, prompt);
        Assert.DoesNotContain(AnthropicModels.AnthropicSystemPrefix, prompt);
    }

    [Fact]
    public void Resolve_preserves_empty_override_and_builds_normal_prompt_when_override_is_null()
    {
        File.WriteAllText(Path.Combine(this.root, "CLAUDE.md"), "PROJECT-CONTEXT-MARKER");

        var empty = EffectiveSystemPrompt.Resolve(this.Options() with { SystemPromptOverride = string.Empty });
        var normal = EffectiveSystemPrompt.Resolve(this.Options() with
        {
            SystemPromptOverride = null,
            OutputStyle = "concise",
        });

        Assert.Equal(string.Empty, empty);
        Assert.Contains(AnthropicModels.AnthropicSystemPrefix, normal);
        Assert.Contains("PROJECT-CONTEXT-MARKER", normal);
        Assert.Contains(BuiltInOutputStyles.Resolve("concise").SystemPromptSuffix, normal);
    }

    private SessionOptions Options() => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
    };

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
