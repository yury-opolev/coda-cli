using Coda.Agent.Settings;
using Coda.Tui.Commands;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Tests;

[Collection("SettingsDirEnv")]
public sealed class LogCommandTests : IDisposable
{
    private readonly string tempDir;
    private readonly string? priorSettingsDir;

    public LogCommandTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-log-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.priorSettingsDir = Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR");
        Environment.SetEnvironmentVariable("CODA_SETTINGS_DIR", this.tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODA_SETTINGS_DIR", this.priorSettingsDir);
        try
        {
            if (Directory.Exists(this.tempDir))
            {
                Directory.Delete(this.tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task Log_debug_persists_enabled_and_level()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();

        var command = new LogCommand();
        await command.ExecuteAsync(context, ["debug"], CancellationToken.None);

        var loaded = SettingsLoader.Load(context.Session.WorkingDirectory).Telemetry;
        Assert.NotNull(loaded);
        Assert.True(loaded!.Enabled);
        Assert.Equal(LogLevel.Debug, loaded.MinLevel);
    }

    [Fact]
    public async Task Log_off_disables_telemetry()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();

        // First enable.
        var command = new LogCommand();
        await command.ExecuteAsync(context, ["info"], CancellationToken.None);

        // Then turn off.
        await command.ExecuteAsync(context, ["off"], CancellationToken.None);

        var loaded = SettingsLoader.Load(context.Session.WorkingDirectory).Telemetry;
        Assert.NotNull(loaded);
        Assert.False(loaded!.Enabled);
    }

    [Fact]
    public async Task Log_no_args_does_not_throw_and_prints_telemetry()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var command = new LogCommand();
        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.NotEmpty(console.Output);
        // Output must mention "Telemetry" or the log dir or "log"
        Assert.True(
            console.Output.Contains("Telemetry", StringComparison.OrdinalIgnoreCase)
            || console.Output.Contains("log", StringComparison.OrdinalIgnoreCase),
            $"Expected output to mention telemetry/log, got: {console.Output}");
    }

    [Fact]
    public async Task Log_stderr_on_persists_flag_and_keeps_level()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();

        var command = new LogCommand();
        await command.ExecuteAsync(context, ["debug"], CancellationToken.None);
        await command.ExecuteAsync(context, ["stderr", "on"], CancellationToken.None);

        var loaded = SettingsLoader.Load(context.Session.WorkingDirectory).Telemetry;
        Assert.NotNull(loaded);
        Assert.True(loaded!.LogToStderr);
        Assert.Equal(LogLevel.Debug, loaded.MinLevel); // level preserved across the stderr toggle
        Assert.True(loaded.Enabled);
    }

    [Fact]
    public async Task Log_bogus_arg_does_not_persist_and_warns()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var command = new LogCommand();
        await command.ExecuteAsync(context, ["bogus"], CancellationToken.None);

        // Settings file should not have been written, so loaded telemetry is null.
        var loaded = SettingsLoader.Load(context.Session.WorkingDirectory).Telemetry;
        Assert.Null(loaded);

        Assert.Contains("Invalid", console.Output, StringComparison.OrdinalIgnoreCase);
    }
}
