using Coda.Agent.Settings;

namespace Engine.Tests.Settings;

/// <summary>
/// Verifies the persisted <c>githubEnterpriseDomain</c> setting (used to select the
/// GitHub Copilot data-residency tenant) round-trips through <see cref="SettingsLoader"/>
/// and <see cref="SettingsWriter.SetGitHubEnterpriseDomain"/>: user/project merge,
/// blank → null, and writer preservation of unrelated keys.
/// </summary>
public sealed class GitHubEnterpriseDomainSettingsTests : IDisposable
{
    private readonly string userHome = Path.Combine(Path.GetTempPath(), "coda-ghe-" + Guid.NewGuid().ToString("N"));
    private readonly string projectDir = Path.Combine(Path.GetTempPath(), "coda-ghe-proj-" + Guid.NewGuid().ToString("N"));

    public GitHubEnterpriseDomainSettingsTests()
    {
        Directory.CreateDirectory(Path.Combine(this.userHome, ".coda"));
        Directory.CreateDirectory(Path.Combine(this.projectDir, ".coda"));
    }

    public void Dispose()
    {
        TryDelete(this.userHome);
        TryDelete(this.projectDir);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } }
        catch (IOException) { }
    }

    private void WriteUser(string json) =>
        File.WriteAllText(Path.Combine(this.userHome, ".coda", "settings.json"), json);

    private void WriteProject(string json) =>
        File.WriteAllText(Path.Combine(this.projectDir, ".coda", "settings.json"), json);

    [Fact]
    public void Loads_githubEnterpriseDomain_from_user_file()
    {
        this.WriteUser("""{ "githubEnterpriseDomain": "octocorp.ghe.com" }""");

        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        Assert.Equal("octocorp.ghe.com", settings.GitHubEnterpriseDomain);
    }

    [Fact]
    public void Project_overrides_user()
    {
        this.WriteUser("""{ "githubEnterpriseDomain": "user.ghe.com" }""");
        this.WriteProject("""{ "githubEnterpriseDomain": "proj.ghe.com" }""");

        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        Assert.Equal("proj.ghe.com", settings.GitHubEnterpriseDomain);
    }

    [Fact]
    public void Blank_normalizes_to_null()
    {
        this.WriteUser("""{ "githubEnterpriseDomain": "   " }""");

        var settings = SettingsLoader.Load(this.projectDir, this.userHome);

        Assert.Null(settings.GitHubEnterpriseDomain);
    }

    [Fact]
    public void SetGitHubEnterpriseDomain_round_trips_and_preserves_other_keys()
    {
        this.WriteUser("""{ "defaultProvider": "keepme", "permissions": { "allow": ["read_file"] } }""");

        SettingsWriter.SetGitHubEnterpriseDomain("octocorp.ghe.com", this.userHome);

        var reloaded = SettingsLoader.Load(this.projectDir, this.userHome);
        Assert.Equal("octocorp.ghe.com", reloaded.GitHubEnterpriseDomain);
        Assert.Equal("keepme", reloaded.DefaultProvider);
        Assert.Contains("read_file", reloaded.Allow);
    }

    [Fact]
    public void SetGitHubEnterpriseDomain_empty_clears_the_key()
    {
        SettingsWriter.SetGitHubEnterpriseDomain("octocorp.ghe.com", this.userHome);
        SettingsWriter.SetGitHubEnterpriseDomain(string.Empty, this.userHome);

        var reloaded = SettingsLoader.Load(this.projectDir, this.userHome);
        Assert.Null(reloaded.GitHubEnterpriseDomain);
    }
}
