using System.Reflection;
using System.Text.Json.Nodes;
using Coda.Agent.Settings;

namespace Coda.Tui.Tests;

public sealed class ThemeSettingsTests : IDisposable
{
    private readonly TestDirectory directory = TestDirectory.Create();

    public void Dispose() => this.directory.Dispose();

    [Fact]
    public void Coda_settings_exposes_theme_property()
    {
        var property = typeof(CodaSettings).GetProperty("Theme", BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(property);
        Assert.Equal(typeof(string), property!.PropertyType);
    }

    [Fact]
    public void Settings_loader_reads_user_theme_and_project_does_not_override_it()
    {
        WriteSettings(Path.Combine(this.directory.Path, "user", ".coda", "settings.json"), """{"theme":"warm-ember"}""");
        WriteSettings(Path.Combine(this.directory.Path, "project", ".coda", "settings.json"), """{"theme":"cool-dark"}""");

        var settings = SettingsLoader.Load(
            Path.Combine(this.directory.Path, "project"),
            Path.Combine(this.directory.Path, "user"));

        Assert.Equal("warm-ember", GetTheme(settings));
    }

    [Fact]
    public void Settings_writer_sets_theme_atomically_and_preserves_other_keys()
    {
        var home = Path.Combine(this.directory.Path, "user");
        var settingsPath = Path.Combine(home, ".coda", "settings.json");
        WriteSettings(settingsPath, """{"other":"preserve","theme":"default"}""");

        InvokeSetUserTheme("cool-dark", home);

        var root = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        Assert.Equal("cool-dark", (string?)root["theme"]);
        Assert.Equal("preserve", (string?)root["other"]);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(settingsPath)!, ".settings.*.tmp"));
    }

    private static string? GetTheme(CodaSettings settings) =>
        (string?)typeof(CodaSettings).GetProperty("Theme", BindingFlags.Instance | BindingFlags.Public)!.GetValue(settings);

    private static void InvokeSetUserTheme(string theme, string userSettingsDir)
    {
        var method = typeof(SettingsWriter).GetMethod("SetUserTheme", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [theme, userSettingsDir]);
    }

    private static void WriteSettings(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private sealed class TestDirectory : IDisposable
    {
        private TestDirectory(string path) => this.Path = path;

        public string Path { get; }

        public static TestDirectory Create()
        {
            var path = System.IO.Path.Combine(
                Directory.GetCurrentDirectory(),
                "artifacts",
                "theme-settings-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(this.Path))
            {
                Directory.Delete(this.Path, recursive: true);
            }
        }
    }
}

