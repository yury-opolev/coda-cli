using Coda.Agent.Settings;
using Coda.Tui.Commands;
using LlmAuth.Providers.ClaudeAi;

namespace Coda.Tui.Tests;

[Collection("SettingsDirEnv")]
public sealed class DefaultProviderModelTests : IDisposable
{
    private readonly string home;
    private readonly string? prior;

    public DefaultProviderModelTests()
    {
        this.home = Path.Combine(Path.GetTempPath(), "coda_defaults_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.home);
        this.prior = Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR");
        Environment.SetEnvironmentVariable("CODA_SETTINGS_DIR", this.home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODA_SETTINGS_DIR", this.prior);
        if (Directory.Exists(this.home))
        {
            Directory.Delete(this.home, recursive: true);
        }
    }

    [Fact]
    public async Task Provider_command_with_id_connects_but_does_not_persist_default_provider()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        // Pre-existing persisted model — connecting must leave it untouched: provider
        // identity is now derived from the connected credential, not a settings pointer,
        // so there's no cross-provider stale-model concern to guard against anymore.
        SettingsWriter.SetUserDefaults(defaultModel: "claude-opus-4-8", userSettingsDir: this.home);

        // Use the API-key provider so the connect flow completes synchronously in-test —
        // OAuth loopback/device-code flows require real browser/network interaction.
        await new ProviderCommand().ExecuteAsync(context, [ApiKeyProvider.Id], CancellationToken.None);

        Assert.Equal(ApiKeyProvider.Id, context.Session.ActiveProviderId); // connected in-session
        var settings = SettingsLoader.Load(this.home, this.home);
        Assert.Null(settings.DefaultProvider); // no settings pointer written
        Assert.Equal("claude-opus-4-8", settings.DefaultModel); // left untouched
    }

    [Fact]
    public async Task Signing_in_connects_the_provider_without_persisting_a_default()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        // A pre-existing persisted model that login must leave alone (no clearing —
        // login no longer touches settings.json at all).
        SettingsWriter.SetUserDefaults(defaultModel: "claude-opus-4-8", userSettingsDir: this.home);

        // The Anthropic API-key provider has no interactive step, so /login completes
        // synchronously (no browser/device flow) — exercising the connect path.
        await new LoginCommand().ExecuteAsync(context, [ApiKeyProvider.Id], CancellationToken.None);

        Assert.Equal(ApiKeyProvider.Id, context.Session.ActiveProviderId); // connected in-session
        var settings = SettingsLoader.Load(this.home, this.home);
        Assert.Null(settings.DefaultProvider); // retired selector — never written
        Assert.Equal("claude-opus-4-8", settings.DefaultModel); // untouched
    }

    [Fact]
    public async Task Choosing_a_model_persists_it()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();

        await new ModelCommand().ExecuteAsync(context, ["claude-opus-4-8"], CancellationToken.None);

        var settings = SettingsLoader.Load(this.home, this.home);
        Assert.Equal("claude-opus-4-8", settings.DefaultModel);
        Assert.Equal("claude-opus-4-8", context.Session.Model);
    }

    [Fact]
    public async Task Legacy_default_flag_is_accepted_but_no_longer_persists_anything()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();

        await new ProviderCommand().ExecuteAsync(context, [ApiKeyProvider.Id, "--default"], CancellationToken.None);

        Assert.Equal(ApiKeyProvider.Id, context.Session.ActiveProviderId);
        var settings = SettingsLoader.Load(this.home, this.home);
        Assert.Null(settings.DefaultProvider);
    }

    [Fact]
    public async Task Listing_providers_or_models_does_not_persist()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();

        await new ProviderCommand().ExecuteAsync(context, [], CancellationToken.None);
        await new ModelCommand().ExecuteAsync(context, [], CancellationToken.None);

        var settings = SettingsLoader.Load(this.home, this.home);
        Assert.Null(settings.DefaultProvider);
        Assert.Null(settings.DefaultModel);
    }
}

[CollectionDefinition("SettingsDirEnv", DisableParallelization = true)]
public sealed class SettingsDirEnvCollection { }
