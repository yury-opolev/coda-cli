using System.Reflection;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

public sealed class ThemeResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_blank_values_resolve_to_default_without_invalid_warning(string? raw)
    {
        var resolution = Resolve(raw);

        Assert.Equal("default", GetThemeName(resolution));
        Assert.True(GetIsValid(resolution));
        Assert.Equal(raw, GetRawValue(resolution));
    }

    [Fact]
    public void Invalid_values_resolve_to_default_and_are_reported()
    {
        var resolution = Resolve("invalid");

        Assert.Equal("default", GetThemeName(resolution));
        Assert.False(GetIsValid(resolution));
        Assert.Equal("invalid", GetRawValue(resolution));
    }

    [Theory]
    [InlineData("  DeFaUlT  ", "default")]
    [InlineData("  WaRm-EmBeR  ", "warm-ember")]
    [InlineData("  CoOl-DaRk  ", "cool-dark")]
    public void Explicit_values_are_case_insensitive_while_raw_value_is_preserved(string raw, string expected)
    {
        var resolution = Resolve(raw);

        Assert.Equal(expected, GetThemeName(resolution));
        Assert.True(GetIsValid(resolution));
        Assert.Equal(raw, GetRawValue(resolution));
    }

    [Fact]
    public void Out_parameter_reports_only_unrecognized_non_blank_values()
    {
        var theme = ResolveWithOut("mystery", out var wasInvalid);

        Assert.Equal("default", GetName(theme));
        Assert.True(wasInvalid);
    }

    [Fact]
    public void Invalid_value_warning_says_it_is_using_default()
    {
        Assert.Equal(
            "Invalid theme 'mystery'; using default.",
            InvokeInvalidValueWarning("mystery"));
    }

    private static Type GetResolverType() =>
        typeof(CodaThemes).Assembly.GetType("Coda.Tui.Ui.Rendering.ThemeResolver", throwOnError: true)!;

    private static object Resolve(string? raw)
    {
        var method = Assert.Single(GetResolverType().GetMethods(BindingFlags.Public | BindingFlags.Static), item => item.Name == "Resolve" && item.GetParameters().Length == 1);
        return method.Invoke(null, [raw])!;
    }

    private static object ResolveWithOut(string? raw, out bool wasInvalid)
    {
        var method = Assert.Single(GetResolverType().GetMethods(BindingFlags.Public | BindingFlags.Static), item => item.Name == "Resolve" && item.GetParameters().Length == 2);
        var args = new object?[] { raw, null };
        var theme = method.Invoke(null, args)!;
        wasInvalid = (bool)args[1]!;
        return theme;
    }

    private static string InvokeInvalidValueWarning(string? raw)
    {
        var method = GetResolverType().GetMethod("InvalidValueWarning", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [raw])!;
    }

    private static string GetThemeName(object resolution) => GetName(resolution.GetType().GetProperty("Theme")!.GetValue(resolution)!);

    private static bool GetIsValid(object resolution) => (bool)resolution.GetType().GetProperty("IsValid")!.GetValue(resolution)!;

    private static string? GetRawValue(object resolution) => (string?)resolution.GetType().GetProperty("RawValue")!.GetValue(resolution);

    private static string GetName(object theme) => (string)theme.GetType().GetProperty("Name")!.GetValue(theme)!;
}
