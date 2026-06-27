using Coda.Agent.Settings;
using Coda.Tui.Commands;

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
    public async Task Choosing_a_provider_persists_it_and_resets_model()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        // Pre-existing persisted model that should be cleared when the provider changes.
        SettingsWriter.SetUserDefaults(defaultModel: "claude-opus-4-8", userSettingsDir: this.home);

        // No flag — choosing persists automatically.
        await new ProviderCommand().ExecuteAsync(context, ["copilot"], CancellationToken.None);

        var settings = SettingsLoader.Load(this.home, this.home);
        Assert.Equal("github-copilot", settings.DefaultProvider);
        Assert.Null(settings.DefaultModel); // reset so startup uses the provider default
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
    public async Task Legacy_default_flag_is_still_accepted()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();

        await new ProviderCommand().ExecuteAsync(context, ["copilot", "--default"], CancellationToken.None);

        var settings = SettingsLoader.Load(this.home, this.home);
        Assert.Equal("github-copilot", settings.DefaultProvider);
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
