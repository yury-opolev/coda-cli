using LlmAuth.Providers.GitHubCopilot;

namespace Coda.Tui.Tests;

/// <summary>
/// Parity tests proving <c>coda run</c> (<see cref="HeadlessRunner"/>) and
/// <c>coda models</c> (<see cref="ModelsRunner"/>) resolve their effective
/// (provider, model) from the SAME precedence chain as <c>coda serve</c> and the
/// TUI: explicit flag → connected credential (provider) / settings default
/// (model), with NO built-in fallback. <c>settings.DefaultProvider</c> is no
/// longer a provider selector — asserted below by setting it to something OTHER
/// than the connected provider and expecting the connected provider to win.
/// Regression guard for the bug where these two entry points silently fell
/// through to the Anthropic/Claude.ai default, ignoring the user's configured
/// provider.
/// </summary>
public sealed class RunnerDefaultsParityTests
{
    [Fact]
    public void Run_honors_connected_provider_and_settings_model()
    {
        using var env = new TempEnv("""
        {
            "defaultProvider": "anthropic-api-key",
            "defaultModel": "claude-opus-4-8"
        }
        """);

        var (providerId, model) = HeadlessRunner.ResolveDefaults(
            providerFlag: null, modelFlag: null, env.WorkingDir, env.UserHome,
            connectedProviderId: GitHubCopilotProvider.Id);

        // Connected provider wins over settings.DefaultProvider; model still comes from settings.
        Assert.Equal(GitHubCopilotProvider.Id, providerId);
        Assert.Equal("claude-opus-4-8", model);
    }

    [Fact]
    public void Run_no_settings_or_connected_provider_resolves_to_null_without_inventing_a_default()
    {
        using var env = new TempEnv(userJson: null);

        var (providerId, model) = HeadlessRunner.ResolveDefaults(providerFlag: null, modelFlag: null, env.WorkingDir, env.UserHome);

        Assert.Null(providerId);
        Assert.Null(model);
    }

    [Fact]
    public void Models_honors_connected_provider()
    {
        using var env = new TempEnv("""
        {
            "defaultProvider": "anthropic-api-key",
            "defaultModel": "claude-opus-4-8"
        }
        """);

        var (providerId, _) = ModelsRunner.ResolveDefaults(
            providerFlag: null, env.WorkingDir, env.UserHome, connectedProviderId: GitHubCopilotProvider.Id);

        Assert.Equal(GitHubCopilotProvider.Id, providerId);
    }

    [Fact]
    public void Models_no_settings_or_connected_provider_resolves_to_null()
    {
        using var env = new TempEnv(userJson: null);

        var (providerId, _) = ModelsRunner.ResolveDefaults(providerFlag: null, env.WorkingDir, env.UserHome);

        Assert.Null(providerId);
    }

    /// <summary>Throwaway user-home + project working dir with optional settings.json.</summary>
    private sealed class TempEnv : IDisposable
    {
        public string UserHome { get; }
        public string WorkingDir { get; }

        public TempEnv(string? userJson)
        {
            this.UserHome = Path.Combine(Path.GetTempPath(), "coda-runparity-user-" + Guid.NewGuid().ToString("N"));
            this.WorkingDir = Path.Combine(Path.GetTempPath(), "coda-runparity-proj-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(this.UserHome, ".coda"));
            Directory.CreateDirectory(Path.Combine(this.WorkingDir, ".coda"));
            if (userJson is not null)
            {
                File.WriteAllText(Path.Combine(this.UserHome, ".coda", "settings.json"), userJson);
            }
        }

        public void Dispose()
        {
            TryDelete(this.UserHome);
            TryDelete(this.WorkingDir);
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
