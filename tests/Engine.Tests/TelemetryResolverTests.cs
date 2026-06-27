using Coda.Agent.Settings;
using Coda.Sdk.Telemetry;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

[Collection("env")]
public sealed class TelemetryResolverTests : IDisposable
{
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODA_LOG_LEVEL", null);
        Environment.SetEnvironmentVariable("CODA_LOG_STDERR", null);
        Environment.SetEnvironmentVariable("CODA_LOG_FILE", null);
    }

    [Fact]
    public void Null_settings_and_no_env_is_disabled()
    {
        var resolved = TelemetryResolver.Resolve(null);

        Assert.False(resolved.Enabled);
    }

    [Fact]
    public void Settings_block_is_honored()
    {
        var settings = TelemetrySettings.Disabled with { Enabled = true, MinLevel = LogLevel.Debug };

        var resolved = TelemetryResolver.Resolve(settings);

        Assert.True(resolved.Enabled);
        Assert.Equal(LogLevel.Debug, resolved.MinLevel);
    }

    [Fact]
    public void Env_level_overrides_and_enables()
    {
        Environment.SetEnvironmentVariable("CODA_LOG_LEVEL", "trace");

        var resolved = TelemetryResolver.Resolve(null);

        Assert.True(resolved.Enabled);
        Assert.Equal(LogLevel.Trace, resolved.MinLevel);
    }

    [Fact]
    public void Env_off_disables_even_when_settings_enabled()
    {
        Environment.SetEnvironmentVariable("CODA_LOG_LEVEL", "off");
        var settings = TelemetrySettings.Disabled with { Enabled = true };

        var resolved = TelemetryResolver.Resolve(settings);

        Assert.False(resolved.Enabled);
    }

    [Fact]
    public void Env_stderr_sets_flag_without_enabling()
    {
        Environment.SetEnvironmentVariable("CODA_LOG_STDERR", "true");

        var resolved = TelemetryResolver.Resolve(TelemetrySettings.Disabled with { Enabled = true, MinLevel = LogLevel.Information });

        Assert.True(resolved.LogToStderr);
        // stderr alone must NOT flip Enabled on its own.
        Assert.False(TelemetryResolver.Resolve(null).Enabled);
    }

    [Fact]
    public void Env_file_sets_directory_override()
    {
        Environment.SetEnvironmentVariable("CODA_LOG_FILE", @"C:\tmp\codalogs");

        var resolved = TelemetryResolver.Resolve(TelemetrySettings.Disabled with { Enabled = true });

        Assert.Equal(@"C:\tmp\codalogs", resolved.DirectoryOverride);
    }

    // ── ResolveServeOverride: the single authority for `coda serve --telemetry` ──
    // Folds what ServeRunner.BuildTelemetryOverride used to compute. null unless
    // forced on; "off" level is ignored (contradicts the force-on intent).

    [Fact]
    public void ServeOverride_no_force_returns_null()
    {
        var result = TelemetryResolver.ResolveServeOverride(
            forceTelemetry: false, telemetryLevel: null, baseTelemetry: TelemetrySettings.Disabled);

        Assert.Null(result);
    }

    [Fact]
    public void ServeOverride_level_without_force_returns_null()
    {
        // A lone level (no --telemetry) must NOT enable telemetry.
        var result = TelemetryResolver.ResolveServeOverride(
            forceTelemetry: false, telemetryLevel: "debug", baseTelemetry: TelemetrySettings.Disabled);

        Assert.Null(result);
    }

    [Fact]
    public void ServeOverride_force_enables_over_disabled_base()
    {
        var result = TelemetryResolver.ResolveServeOverride(
            forceTelemetry: true, telemetryLevel: null, baseTelemetry: TelemetrySettings.Disabled);

        Assert.NotNull(result);
        Assert.True(result!.Enabled);
    }

    [Fact]
    public void ServeOverride_force_with_null_base_starts_from_disabled()
    {
        var result = TelemetryResolver.ResolveServeOverride(
            forceTelemetry: true, telemetryLevel: null, baseTelemetry: null);

        Assert.NotNull(result);
        Assert.True(result!.Enabled);
    }

    [Fact]
    public void ServeOverride_force_applies_named_level()
    {
        var result = TelemetryResolver.ResolveServeOverride(
            forceTelemetry: true, telemetryLevel: "debug", baseTelemetry: TelemetrySettings.Disabled);

        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.Equal(LogLevel.Debug, result.MinLevel);
    }

    [Fact]
    public void ServeOverride_force_keeps_base_level_when_level_unspecified()
    {
        var baseTelemetry = TelemetrySettings.Disabled with { MinLevel = LogLevel.Warning };
        var result = TelemetryResolver.ResolveServeOverride(
            forceTelemetry: true, telemetryLevel: null, baseTelemetry: baseTelemetry);

        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.Equal(LogLevel.Warning, result.MinLevel);
    }

    [Fact]
    public void ServeOverride_force_ignores_off_level_and_stays_enabled()
    {
        // --telemetry --telemetry-level off: force-on wins; "off" is ignored (the
        // existing level is kept, the override stays enabled).
        var baseTelemetry = TelemetrySettings.Disabled with { MinLevel = LogLevel.Warning };
        var result = TelemetryResolver.ResolveServeOverride(
            forceTelemetry: true, telemetryLevel: "off", baseTelemetry: baseTelemetry);

        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.Equal(LogLevel.Warning, result.MinLevel);
    }

    [Fact]
    public void ServeOverride_force_ignores_invalid_level()
    {
        var baseTelemetry = TelemetrySettings.Disabled with { MinLevel = LogLevel.Warning };
        var result = TelemetryResolver.ResolveServeOverride(
            forceTelemetry: true, telemetryLevel: "notalevel", baseTelemetry: baseTelemetry);

        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.Equal(LogLevel.Warning, result.MinLevel);
    }

    [Fact]
    public void ServeOverride_force_ignores_empty_level()
    {
        var baseTelemetry = TelemetrySettings.Disabled with { MinLevel = LogLevel.Warning };
        var result = TelemetryResolver.ResolveServeOverride(
            forceTelemetry: true, telemetryLevel: "", baseTelemetry: baseTelemetry);

        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.Equal(LogLevel.Warning, result.MinLevel);
    }

    [Fact]
    public void ServeOverride_then_env_off_disables_via_resolve()
    {
        // Proof the serve override flows through Resolve so env still has final say:
        // --telemetry forces on, but CODA_LOG_LEVEL=off disables at session time.
        Environment.SetEnvironmentVariable("CODA_LOG_LEVEL", "off");
        var forced = TelemetryResolver.ResolveServeOverride(
            forceTelemetry: true, telemetryLevel: null, baseTelemetry: TelemetrySettings.Disabled);

        var effective = TelemetryResolver.Resolve(forced);

        Assert.False(effective.Enabled);
    }

    [Fact]
    public void Create_returns_null_factory_when_disabled()
    {
        var setup = CodaLoggerFactory.Create(TelemetrySettings.Disabled);
        using var factory = setup.Factory;

        Assert.Null(setup.LogFilePath);
        Assert.False(factory.CreateLogger("x").IsEnabled(LogLevel.Critical)); // NullLogger reports disabled
    }

    [Fact]
    public void Create_enabled_yields_log_file_path()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "coda-setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = TelemetrySettings.Disabled with { Enabled = true, DirectoryOverride = tempDir };
            var setup = CodaLoggerFactory.Create(settings);
            using (setup.Factory)
            {
                Assert.NotNull(setup.LogFilePath);
                Assert.StartsWith(tempDir, setup.LogFilePath!);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
