using Coda.Agent.Settings;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

public sealed class SettingsWriterTelemetryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coda-sw-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { }
    }

    [Fact]
    public void SetTelemetry_writes_block_and_preserves_unknown_keys()
    {
        var dir = Path.Combine(this.root, ".coda");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "defaultModel": "keepme", "permissions": { "allow": ["read_file"] } }""");

        SettingsWriter.SetTelemetry(enabled: true, level: LogLevel.Debug, stderr: false, userSettingsDir: this.root);

        var reloaded = SettingsLoader.Load(Path.Combine(this.root, "proj"), this.root);
        Assert.NotNull(reloaded.Telemetry);
        Assert.True(reloaded.Telemetry!.Enabled);
        Assert.Equal(LogLevel.Debug, reloaded.Telemetry.MinLevel);
        Assert.Equal("keepme", reloaded.DefaultModel);
        Assert.Contains("read_file", reloaded.Allow);
    }

    [Fact]
    public void SetTelemetry_disabled_round_trips()
    {
        SettingsWriter.SetTelemetry(enabled: false, level: LogLevel.Information, stderr: false, userSettingsDir: this.root);

        var reloaded = SettingsLoader.Load(Path.Combine(this.root, "proj"), this.root);
        Assert.NotNull(reloaded.Telemetry);
        Assert.False(reloaded.Telemetry!.Enabled);
    }

    [Fact]
    public void SetTelemetry_preserves_existing_telemetry_fields()
    {
        var dir = Path.Combine(this.root, ".coda");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "telemetry": { "enabled": false, "level": "info", "retainedFiles": 3 } }""");

        // Changing only level should not wipe retainedFiles.
        SettingsWriter.SetTelemetry(enabled: true, level: LogLevel.Trace, stderr: false, userSettingsDir: this.root);

        var reloaded = SettingsLoader.Load(Path.Combine(this.root, "proj"), this.root);
        Assert.Equal(LogLevel.Trace, reloaded.Telemetry!.MinLevel);
        Assert.Equal(3, reloaded.Telemetry.RetainedFileCount);
    }

    [Fact]
    public void SetTelemetry_round_trips_stderr_flag()
    {
        SettingsWriter.SetTelemetry(enabled: true, level: LogLevel.Information, stderr: true, userSettingsDir: this.root);

        var reloaded = SettingsLoader.Load(Path.Combine(this.root, "proj"), this.root);
        Assert.True(reloaded.Telemetry!.LogToStderr);
    }
}
