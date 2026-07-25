using System.Reflection;
using Coda.Tui.Ui.Rendering;
using TgColor = Terminal.Gui.Drawing.Color;
using TgName = Terminal.Gui.Drawing.ColorName16;

namespace Coda.Tui.Tests;

public sealed class RoleParityTests
{
    [Fact]
    public void Every_built_in_theme_defines_every_tui_role_and_console_palette_field()
    {
        var registryType = typeof(TuiTheme).Assembly.GetType("Coda.Tui.Ui.Rendering.CodaThemes", throwOnError: true)!;
        var all = ((System.Collections.IEnumerable?)registryType.GetProperty("All", BindingFlags.Public | BindingFlags.Static)?.GetValue(null))
            ?.Cast<object>()
            .ToArray();

        Assert.NotNull(all);

        var tuiRoleProperties = typeof(TuiTheme)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.PropertyType == typeof(TuiThemeColor))
            .ToArray();
        Assert.NotEmpty(tuiRoleProperties);

        var warmEmberTuiTheme = TuiTheme.WarmEmber;

        foreach (var theme in all!)
        {
            var tui = theme.GetType().GetProperty("Tui")!.GetValue(theme);
            Assert.NotNull(tui);
            var tuiTheme = (TuiTheme)tui!;

            AssertAllRolesExplicitlySet(tuiRoleProperties, tuiTheme, warmEmberTuiTheme);

            var console = theme.GetType().GetProperty("Console")!.GetValue(theme);
            Assert.NotNull(console);
            var consoleFields = console!.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.PropertyType == typeof(string))
                .ToArray();
            Assert.Equal(["Accent", "Dim", "Success", "Warn", "Error"], consoleFields.Select(field => field.Name).ToArray());
            foreach (var field in consoleFields)
            {
                Assert.False(string.IsNullOrWhiteSpace((string?)field.GetValue(console)));
            }
        }
    }

    /// <summary>
    /// Proves the guard is not a tautology: a <see cref="TuiTheme"/> that omits roles (so they
    /// silently inherit the WarmEmber init defaults) MUST fail the parity check. Without this canary
    /// the main test could silently pass even if a future theme forgets to set a role.
    /// </summary>
    [Fact]
    public void Parity_guard_catches_a_non_warm_ember_theme_that_omits_a_role()
    {
        // Only Background is explicitly set — all other roles silently inherit the WarmEmber init defaults.
        var incomplete = new TuiTheme
        {
            Background = new(new TgColor(1, 2, 3), TgName.Blue),
        };

        var tuiRoleProperties = typeof(TuiTheme)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.PropertyType == typeof(TuiThemeColor))
            .ToArray();

        // The real parity check would throw an XunitException for any omitted role.
        var failure = Record.Exception(() => AssertAllRolesExplicitlySet(tuiRoleProperties, incomplete, TuiTheme.WarmEmber));

        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(failure);
    }

    // ---------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------

    private static void AssertAllRolesExplicitlySet(
        PropertyInfo[] roles,
        TuiTheme theme,
        TuiTheme warmEmber)
    {
        if (ReferenceEquals(theme, warmEmber))
        {
            // WarmEmber is the source of the init defaults. Guard that its own roles are non-trivial
            // (i.e. not the zero-init struct), so we know the defaults are meaningful colors.
            foreach (var role in roles)
            {
                var value = (TuiThemeColor)role.GetValue(theme)!;
                Assert.NotEqual(default(TuiThemeColor), value);
            }
        }
        else
        {
            // Non-WarmEmber themes must explicitly set EVERY role. If a role is omitted, the init
            // default silently inherits the WarmEmber value — this assertion catches that.
            foreach (var role in roles)
            {
                var value = (TuiThemeColor)role.GetValue(theme)!;
                var warmEmberValue = (TuiThemeColor)role.GetValue(warmEmber)!;
                Assert.NotEqual(warmEmberValue, value);
            }
        }
    }
}
