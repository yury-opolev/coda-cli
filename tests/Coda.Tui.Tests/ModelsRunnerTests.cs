using Coda.Tui;

namespace Coda.Tui.Tests;

public sealed class ModelsRunnerTests
{
    [Fact]
    public void TryParse_reads_all_flags()
    {
        var ok = ModelsRunner.TryParse(
            ["--provider", "copilot", "--cwd", @"C:\x", "--json", "--refresh"],
            out var provider, out var cwd, out var json, out var refresh, out var error);

        Assert.True(ok, error);
        Assert.Equal("copilot", provider);
        Assert.Equal(@"C:\x", cwd);
        Assert.True(json);
        Assert.True(refresh);
    }

    [Fact]
    public void TryParse_defaults_when_no_args()
    {
        var ok = ModelsRunner.TryParse([], out var provider, out var cwd, out var json, out var refresh, out _);

        Assert.True(ok);
        Assert.Null(provider);
        Assert.Null(cwd);
        Assert.False(json);
        Assert.False(refresh);
    }

    [Fact]
    public void TryParse_rejects_unknown_arg()
    {
        var ok = ModelsRunner.TryParse(["--bogus"], out _, out _, out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("Unknown argument", error);
    }

    [Fact]
    public void TryParse_rejects_missing_value()
    {
        var ok = ModelsRunner.TryParse(["--provider"], out _, out _, out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("Missing value", error);
    }
}
