using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

public sealed class ToolDisplayModeResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_blank_values_resolve_to_summary_without_invalid_warning(string? raw)
    {
        var resolution = ToolDisplayModeResolver.Resolve(raw);

        Assert.Equal(ToolDisplayMode.Summary, resolution.Mode);
        Assert.True(resolution.IsValid);
        Assert.Equal(raw, resolution.RawValue);
    }

    [Fact]
    public void Invalid_values_resolve_to_summary_and_are_reported()
    {
        var resolution = ToolDisplayModeResolver.Resolve("invalid");

        Assert.Equal(ToolDisplayMode.Summary, resolution.Mode);
        Assert.False(resolution.IsValid);
        Assert.Equal("invalid", resolution.RawValue);
    }

    [Theory]
    [InlineData("  VeRbOsE  ", ToolDisplayMode.Verbose)]
    [InlineData("  CoMpAcT  ", ToolDisplayMode.Compact)]
    [InlineData("  SuMmArY  ", ToolDisplayMode.Summary)]
    [InlineData("  TiNy  ", ToolDisplayMode.Tiny)]
    public void Explicit_values_are_case_insensitive_while_raw_value_is_preserved(
        string raw,
        ToolDisplayMode expected)
    {
        var resolution = ToolDisplayModeResolver.Resolve(raw);

        Assert.Equal(expected, resolution.Mode);
        Assert.True(resolution.IsValid);
        Assert.Equal(raw, resolution.RawValue);
    }

    [Fact]
    public void Out_parameter_reports_only_unrecognized_non_blank_values()
    {
        var mode = ToolDisplayModeResolver.Resolve("loud", out var wasInvalid);

        Assert.Equal(ToolDisplayMode.Summary, mode);
        Assert.True(wasInvalid);
    }

    [Fact]
    public void Invalid_value_warning_says_it_is_using_summary()
    {
        Assert.Equal(
            "Invalid toolDisplayMode 'loud'; using summary.",
            ToolDisplayModeResolver.InvalidValueWarning("loud"));
    }

    [Theory]
    [InlineData(typeof(TerminalGuiShellBase))]
    [InlineData(typeof(FullscreenTuiShell))]
    [InlineData(typeof(InlineTuiShell))]
    public void Tui_shell_defaults_are_summary(Type shellType)
    {
        var constructor = Assert.Single(shellType.GetConstructors(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic));

        var parameter = Assert.Single(
            constructor.GetParameters(),
            parameter => parameter.Name == "toolDisplayMode");

        Assert.Equal(ToolDisplayMode.Summary, parameter.DefaultValue);
    }
}
