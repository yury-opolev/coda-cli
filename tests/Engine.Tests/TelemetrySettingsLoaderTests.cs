using Coda.Agent.Settings;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

public sealed class TelemetrySettingsLoaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coda-set-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { }
    }

    private string WriteUserSettings(string json)
    {
        var dir = Path.Combine(this.root, ".coda");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);
        return this.root;
    }

    [Fact]
    public void Parses_telemetry_block()
    {
        var home = this.WriteUserSettings("""
        { "telemetry": { "enabled": true, "level": "debug", "stderr": true, "retainedFiles": 3, "maxFileSizeMb": 5, "maxRunParts": 4 } }
        """);

        var settings = SettingsLoader.Load(Path.Combine(this.root, "proj"), home);

        Assert.NotNull(settings.Telemetry);
        Assert.True(settings.Telemetry!.Enabled);
        Assert.Equal(LogLevel.Debug, settings.Telemetry.MinLevel);
        Assert.True(settings.Telemetry.LogToStderr);
        Assert.Equal(3, settings.Telemetry.RetainedFileCount);
        Assert.Equal(5L * 1024 * 1024, settings.Telemetry.MaxFileSizeBytes);
        Assert.Equal(4, settings.Telemetry.MaxRunParts);
    }

    [Fact]
    public void No_telemetry_block_yields_null()
    {
        var home = this.WriteUserSettings("""{ "defaultProvider": "x" }""");

        var settings = SettingsLoader.Load(Path.Combine(this.root, "proj"), home);

        Assert.Null(settings.Telemetry);
    }

    [Fact]
    public void Level_word_info_and_warn_map_correctly()
    {
        var home = this.WriteUserSettings("""{ "telemetry": { "enabled": true, "level": "warn" } }""");

        var settings = SettingsLoader.Load(Path.Combine(this.root, "proj"), home);

        Assert.Equal(LogLevel.Warning, settings.Telemetry!.MinLevel);
    }

    [Fact]
    public void MaxFileSizeMb_zero_means_no_cap()
    {
        var home = this.WriteUserSettings("""{ "telemetry": { "enabled": true, "maxFileSizeMb": 0 } }""");

        var settings = SettingsLoader.Load(Path.Combine(this.root, "proj"), home);

        Assert.Equal(0, settings.Telemetry!.MaxFileSizeBytes);
    }

    [Fact]
    public void Invalid_level_word_falls_back_to_information()
    {
        var home = this.WriteUserSettings("""{ "telemetry": { "enabled": true, "level": "bogus" } }""");

        var settings = SettingsLoader.Load(Path.Combine(this.root, "proj"), home);

        Assert.Equal(LogLevel.Information, settings.Telemetry!.MinLevel);
    }
}
