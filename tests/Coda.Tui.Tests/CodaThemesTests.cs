using System.Reflection;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

public sealed class CodaThemesTests : IDisposable
{
    private readonly object? originalTheme;
    private readonly Type registryType;

    public CodaThemesTests()
    {
        this.registryType = GetRegistryType();
        this.originalTheme = GetCurrentThemeOrNull();
    }

    public void Dispose()
    {
        if (this.originalTheme is not null)
        {
            SetTheme(this.originalTheme);
        }
    }

    [Fact]
    public void Registry_exposes_exactly_three_built_in_themes_and_defaults_to_default()
    {
        var all = GetAllThemes();

        Assert.Equal(["default", "warm-ember", "cool-dark"], all.Select(GetName).ToArray());
        Assert.All(all, theme => Assert.False(string.IsNullOrWhiteSpace(GetDisplayName(theme))));
        Assert.Equal("default", GetName(GetCurrentTheme()));
    }

    [Fact]
    public void Try_get_returns_known_theme_and_rejects_unknown_name()
    {
        var warmEmber = TryGet("warm-ember");
        var unknown = TryGet("unknown");

        Assert.NotNull(warmEmber);
        Assert.Equal("warm-ember", GetName(warmEmber!));
        Assert.Null(unknown);
    }

    [Fact]
    public void Set_changes_current_and_raises_changed_exactly_once_per_call()
    {
        var original = GetCurrentTheme();
        var warmEmber = TryGet("warm-ember")!;
        var changed = 0;
        Action handler = () => changed++;
        var changedEvent = this.registryType.GetEvent("Changed", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(changedEvent);
        changedEvent!.AddEventHandler(null, handler);

        try
        {
            SetTheme(warmEmber);

            Assert.Equal("warm-ember", GetName(GetCurrentTheme()));
            Assert.Equal(1, changed);

            SetTheme(original);

            Assert.Equal(GetName(original), GetName(GetCurrentTheme()));
            Assert.Equal(2, changed);
        }
        finally
        {
            changedEvent.RemoveEventHandler(null, handler);
            SetTheme(original);
        }
    }

    [Fact]
    public void Tui_theme_has_a_public_parameterless_constructor()
    {
        var constructor = typeof(TuiTheme).GetConstructor(Type.EmptyTypes);

        Assert.NotNull(constructor);
        Assert.True(constructor!.IsPublic);
    }

    private static Type GetRegistryType() =>
        typeof(TuiTheme).Assembly.GetType("Coda.Tui.Ui.Rendering.CodaThemes", throwOnError: true)!;

    private object? GetCurrentThemeOrNull() => this.registryType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

    private object GetCurrentTheme() => GetCurrentThemeOrNull() ?? throw new InvalidOperationException("Current theme unavailable.");

    private IReadOnlyList<object> GetAllThemes() =>
        ((System.Collections.IEnumerable?)this.registryType.GetProperty("All", BindingFlags.Public | BindingFlags.Static)?.GetValue(null))
            ?.Cast<object>()
            .ToArray()
        ?? throw new InvalidOperationException("Theme registry did not expose All.");

    private object? TryGet(string name)
    {
        var args = new object?[] { name, null };
        var method = this.registryType.GetMethod("TryGet", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var found = (bool)method!.Invoke(null, args)!;
        return found ? args[1] : null;
    }

    private void SetTheme(object theme)
    {
        var method = this.registryType.GetMethod("Set", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [theme]);
    }

    private static string GetName(object theme) =>
        (string)(theme.GetType().GetProperty("Name")?.GetValue(theme)
            ?? throw new InvalidOperationException("Theme name unavailable."));

    private static string GetDisplayName(object theme) =>
        (string)(theme.GetType().GetProperty("DisplayName")?.GetValue(theme)
            ?? throw new InvalidOperationException("Theme display name unavailable."));
}
