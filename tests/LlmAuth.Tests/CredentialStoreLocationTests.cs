namespace LlmAuth.Tests;

public sealed class CredentialStoreLocationTests : IDisposable
{
    private readonly string root;
    private readonly string legacy;
    private readonly string target;

    public CredentialStoreLocationTests()
    {
        this.root = Path.Combine(Path.GetTempPath(), "CredMigrate_" + Guid.NewGuid().ToString("N"));
        this.legacy = Path.Combine(this.root, "legacy", "LlmAuth");
        this.target = Path.Combine(this.root, ".coda", "credentials");
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public void Migrate_moves_credentials_and_removes_legacy()
    {
        Directory.CreateDirectory(this.legacy);
        File.WriteAllText(Path.Combine(this.legacy, "llmauth_claude-ai.cred"), "secret-a");
        File.WriteAllText(Path.Combine(this.legacy, "key.bin"), "keymaterial");

        CredentialStoreLocation.Migrate(this.legacy, this.target);

        Assert.True(File.Exists(Path.Combine(this.target, "llmauth_claude-ai.cred")));
        Assert.Equal("secret-a", File.ReadAllText(Path.Combine(this.target, "llmauth_claude-ai.cred")));
        Assert.True(File.Exists(Path.Combine(this.target, "key.bin")));
        Assert.False(Directory.Exists(this.legacy)); // legacy removed after a clean copy
    }

    [Fact]
    public void Migrate_does_not_overwrite_existing_target_credentials()
    {
        Directory.CreateDirectory(this.legacy);
        File.WriteAllText(Path.Combine(this.legacy, "a.cred"), "legacy-value");
        Directory.CreateDirectory(this.target);
        File.WriteAllText(Path.Combine(this.target, "a.cred"), "new-value");

        CredentialStoreLocation.Migrate(this.legacy, this.target);

        // Target already had credentials → left untouched, legacy preserved.
        Assert.Equal("new-value", File.ReadAllText(Path.Combine(this.target, "a.cred")));
        Assert.True(Directory.Exists(this.legacy));
    }

    [Fact]
    public void Migrate_creates_target_when_no_legacy_exists()
    {
        CredentialStoreLocation.Migrate(this.legacy, this.target);

        Assert.True(Directory.Exists(this.target));
    }

    [Fact]
    public void Default_path_is_under_dotcoda()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".coda", "credentials");
        Assert.Equal(expected, CredentialStoreLocation.Default);
    }
}
