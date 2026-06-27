using Coda.Agent.Settings;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

public sealed class TelemetrySettingsTests
{
    [Fact]
    public void Disabled_has_safe_defaults()
    {
        var settings = TelemetrySettings.Disabled;

        Assert.False(settings.Enabled);
        Assert.Equal(LogLevel.Information, settings.MinLevel);
        Assert.False(settings.LogToStderr);
        Assert.Equal(7, settings.RetainedFileCount);
        Assert.Equal(20L * 1024 * 1024, settings.MaxFileSizeBytes);
        Assert.Equal(10, settings.MaxRunParts);
        Assert.Null(settings.DirectoryOverride);
    }

    [Fact]
    public void With_expression_overrides_single_field()
    {
        var settings = TelemetrySettings.Disabled with { Enabled = true, MinLevel = LogLevel.Trace };

        Assert.True(settings.Enabled);
        Assert.Equal(LogLevel.Trace, settings.MinLevel);
        Assert.Equal(7, settings.RetainedFileCount);
    }
}
