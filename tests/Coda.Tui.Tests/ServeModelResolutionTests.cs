using Coda.Agent.Settings;
using Coda.Sdk.Providers;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;

namespace Coda.Tui.Tests;

/// <summary>
/// coda serve resolves the effective (provider, model) the same way every other entry point does:
/// the provider comes from the <c>--provider</c> flag or the connected credential, and the MODEL is
/// resolved AGAINST THE EFFECTIVE PROVIDER (<c>--model</c> → the provider's configured model →
/// its built-in default). These lock the ordering that matters for the relay — the model is
/// resolved AFTER the connected-credential provider substitution — and that there is no global
/// default model.
/// </summary>
public sealed class ServeModelResolutionTests
{
    [Fact]
    public void Uses_connected_providers_configured_model()
    {
        // The relay case: no --provider / --model; the model is the connected provider's configured one.
        var settings = CodaSettings.Empty with
        {
            ModelByProvider = new Dictionary<string, string> { [GitHubCopilotProvider.Id] = "claude-opus-4.8" },
        };

        var (provider, model) = ServeRunner.ResolveEffective(
            requestedProviderId: null, modelFlag: null, settings,
            connectedProviderId: GitHubCopilotProvider.Id, apiKeyEnvPresent: false);

        Assert.Equal(GitHubCopilotProvider.Id, provider);
        Assert.Equal("claude-opus-4.8", model);
    }

    [Fact]
    public void Falls_back_to_provider_built_in_model_when_none_configured()
    {
        var (provider, model) = ServeRunner.ResolveEffective(
            requestedProviderId: null, modelFlag: null, CodaSettings.Empty,
            connectedProviderId: GitHubCopilotProvider.Id, apiKeyEnvPresent: false);

        Assert.Equal(GitHubCopilotProvider.Id, provider);
        Assert.Equal(ProviderDefaults.ModelFor(GitHubCopilotProvider.Id), model);
    }

    [Fact]
    public void Model_flag_wins()
    {
        var settings = CodaSettings.Empty with
        {
            ModelByProvider = new Dictionary<string, string> { [GitHubCopilotProvider.Id] = "configured-model" },
        };

        var (_, model) = ServeRunner.ResolveEffective(
            requestedProviderId: null, modelFlag: "flag-model", settings,
            connectedProviderId: GitHubCopilotProvider.Id, apiKeyEnvPresent: false);

        Assert.Equal("flag-model", model);
    }

    [Fact]
    public void Model_follows_the_substituted_provider_not_the_requested_one()
    {
        // Requested a provider with no credential → serve substitutes the connected provider, AND the
        // model must be the CONNECTED provider's model. This is the whole point of resolving the model
        // after the provider — the bug that made the relay fail with "No model configured".
        var settings = CodaSettings.Empty with
        {
            ModelByProvider = new Dictionary<string, string>
            {
                [GitHubCopilotProvider.Id] = "copilot-model",
                [ClaudeAiProvider.Id] = "claude-model",
            },
        };

        var (provider, model) = ServeRunner.ResolveEffective(
            requestedProviderId: ClaudeAiProvider.Id, modelFlag: null, settings,
            connectedProviderId: GitHubCopilotProvider.Id, apiKeyEnvPresent: false);

        Assert.Equal(GitHubCopilotProvider.Id, provider); // substituted to the connected credential
        Assert.Equal("copilot-model", model);             // model follows THAT provider, not Claude's
    }

    [Fact]
    public void No_provider_configured_resolves_to_null()
    {
        var (provider, model) = ServeRunner.ResolveEffective(
            requestedProviderId: null, modelFlag: null, CodaSettings.Empty,
            connectedProviderId: null, apiKeyEnvPresent: false);

        Assert.Null(provider);
        Assert.Null(model);
    }

    [Fact]
    public void Parse_honors_explicit_flags()
    {
        using var env = new TempEnv(userJson: null, projectJson: null);

        var serve = ServeRunner.Parse(["--provider", "claude", "--model", "flag-model", "--cwd", env.WorkingDir], env.UserHome);

        Assert.Equal(ClaudeAiProvider.Id, serve.ProviderId);
        Assert.Equal("flag-model", serve.Model);
    }

    /// <summary>A throwaway user-home + project working dir so the Parse test never touches the machine.</summary>
    private sealed class TempEnv : IDisposable
    {
        public string UserHome { get; }
        public string WorkingDir { get; }

        public TempEnv(string? userJson, string? projectJson)
        {
            this.UserHome = Path.Combine(Path.GetTempPath(), "coda-serve-user-" + Guid.NewGuid().ToString("N"));
            this.WorkingDir = Path.Combine(Path.GetTempPath(), "coda-serve-proj-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(this.UserHome, ".coda"));
            Directory.CreateDirectory(Path.Combine(this.WorkingDir, ".coda"));
            if (userJson is not null)
            {
                File.WriteAllText(Path.Combine(this.UserHome, ".coda", "settings.json"), userJson);
            }

            if (projectJson is not null)
            {
                File.WriteAllText(Path.Combine(this.WorkingDir, ".coda", "settings.json"), projectJson);
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
