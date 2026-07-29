using Coda.Common;
using Coda.Tui.Plugins;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for the single shared single-segment name validator used by plugin installation and
/// <c>/skills new</c>. Both callers must agree, otherwise the weaker one becomes the bypass.
/// </summary>
public sealed class SafeNameValidatorTests
{
    [Theory]
    [InlineData("my-plugin")]
    [InlineData("foo")]
    [InlineData("plugin-1.0")]
    [InlineData("MyPlugin_2")]
    public void Accepts_ordinary_single_segment_names(string name)
    {
        Assert.True(SafeNameValidator.IsValidName(name));
        Assert.True(PluginInstaller.IsValidPluginName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../foo")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    public void Rejects_empty_relative_and_multi_segment_names(string name)
    {
        Assert.False(SafeNameValidator.IsValidName(name));
        Assert.False(PluginInstaller.IsValidPluginName(name));
    }

    [Theory]
    [InlineData("foo..bar")]
    [InlineData("a..")]
    [InlineData("..a")]
    public void Rejects_any_dotdot_substring(string name)
    {
        Assert.False(SafeNameValidator.IsValidName(name));
        Assert.False(PluginInstaller.IsValidPluginName(name));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    [InlineData("CON.txt")]
    public void Rejects_reserved_windows_device_names(string name)
    {
        Assert.False(SafeNameValidator.IsValidName(name));
        Assert.False(PluginInstaller.IsValidPluginName(name));
    }

    [Theory]
    [InlineData("COM0")]
    [InlineData("LPT0")]
    [InlineData("console")]
    [InlineData("nullish")]
    public void Accepts_names_that_only_resemble_device_names(string name)
    {
        Assert.True(SafeNameValidator.IsValidName(name));
        Assert.True(PluginInstaller.IsValidPluginName(name));
    }

    [Theory]
    [InlineData("plugin.")]
    [InlineData("plugin ")]
    [InlineData(" plugin")]
    [InlineData("plugin...")]
    public void Rejects_trailing_dots_and_surrounding_spaces(string name)
    {
        Assert.False(SafeNameValidator.IsValidName(name));
        Assert.False(PluginInstaller.IsValidPluginName(name));
    }
}
