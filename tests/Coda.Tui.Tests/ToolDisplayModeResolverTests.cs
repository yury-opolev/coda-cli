using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

public sealed class ToolDisplayModeResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_blank_values_resolve_to_tiny_without_invalid_warning(string? raw)
    {
        var resolution = ToolDisplayModeResolver.Resolve(raw);

        Assert.Equal(ToolDisplayMode.Tiny, resolution.Mode);
        Assert.True(resolution.IsValid);
        Assert.Equal(raw, resolution.RawValue);
    }

    [Fact]
    public void Invalid_values_resolve_to_tiny_and_are_reported()
    {
        var resolution = ToolDisplayModeResolver.Resolve("invalid");

        Assert.Equal(ToolDisplayMode.Tiny, resolution.Mode);
        Assert.False(resolution.IsValid);
        Assert.Equal("invalid", resolution.RawValue);
    }

    [Fact]
    public void Values_are_case_insensitive_while_raw_value_is_preserved()
    {
        var resolution = ToolDisplayModeResolver.Resolve("  CoMpAcT  ");

        Assert.Equal(ToolDisplayMode.Compact, resolution.Mode);
        Assert.True(resolution.IsValid);
        Assert.Equal("  CoMpAcT  ", resolution.RawValue);
    }

    [Fact]
    public void Out_parameter_reports_only_unrecognized_non_blank_values()
    {
        var mode = ToolDisplayModeResolver.Resolve("loud", out var wasInvalid);

        Assert.Equal(ToolDisplayMode.Tiny, mode);
        Assert.True(wasInvalid);
    }
}
