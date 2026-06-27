namespace Coda.Tui.Tests;

using Coda.Tui.Plugins;

public sealed class KnownMarketplacesStoreTests : IDisposable
{
    private readonly string tempDir;

    public KnownMarketplacesStoreTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.tempDir))
            {
                Directory.Delete(this.tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private KnownMarketplacesStore CreateStore() => new(this.tempDir);

    [Fact]
    public void Add_then_List_round_trips()
    {
        var store = this.CreateStore();
        var entry = new KnownMarketplaceEntry(
            new GithubSource("owner/repo", "main", null),
            "/some/install/location",
            "2026-05-31T12:00:00Z");

        store.Add("mymarket", entry);
        var list = store.List();

        Assert.True(list.ContainsKey("mymarket"));
        var retrieved = list["mymarket"];
        var github = Assert.IsType<GithubSource>(retrieved.Source);
        Assert.Equal("owner/repo", github.Repo);
        Assert.Equal("/some/install/location", retrieved.InstallLocation);
        Assert.Equal("2026-05-31T12:00:00Z", retrieved.LastUpdated);
    }

    [Fact]
    public void TryGet_hit_and_miss()
    {
        var store = this.CreateStore();
        var entry = new KnownMarketplaceEntry(
            new LocalDirectorySource("/some/path"),
            "/install/dir",
            "2026-05-31T00:00:00Z");

        store.Add("exists", entry);

        Assert.True(store.TryGet("exists", out var found));
        Assert.NotNull(found);
        Assert.Equal("/install/dir", found.InstallLocation);

        Assert.False(store.TryGet("missing", out var notFound));
        Assert.Null(notFound);
    }

    [Fact]
    public void Remove_removes_entry()
    {
        var store = this.CreateStore();
        var entry = new KnownMarketplaceEntry(
            new LocalDirectorySource("/path"),
            "/loc",
            "2026-05-31T00:00:00Z");

        store.Add("toremove", entry);
        Assert.True(store.List().ContainsKey("toremove"));

        store.Remove("toremove");

        Assert.False(store.List().ContainsKey("toremove"));
    }

    [Fact]
    public void Persists_across_new_store_instance()
    {
        var entry = new KnownMarketplaceEntry(
            new GithubSource("org/repo"),
            "/loc",
            "2026-05-31T00:00:00Z");

        var store1 = this.CreateStore();
        store1.Add("persistent", entry);

        var store2 = this.CreateStore();
        var list = store2.List();

        Assert.True(list.ContainsKey("persistent"));
        var retrieved = list["persistent"];
        var github = Assert.IsType<GithubSource>(retrieved.Source);
        Assert.Equal("org/repo", github.Repo);
    }

    [Fact]
    public void All_source_kinds_round_trip()
    {
        var store = this.CreateStore();

        store.Add("github-entry", new KnownMarketplaceEntry(
            new GithubSource("owner/repo", "v2", "some/path"),
            "/loc1",
            "2026-05-31T00:00:00Z"));

        store.Add("git-entry", new KnownMarketplaceEntry(
            new GitSource("https://example.com/repo.git", "main", "sub/path"),
            "/loc2",
            "2026-05-31T00:00:01Z"));

        store.Add("file-entry", new KnownMarketplaceEntry(
            new LocalFileSource("/some/marketplace.json"),
            "/loc3",
            "2026-05-31T00:00:02Z"));

        store.Add("dir-entry", new KnownMarketplaceEntry(
            new LocalDirectorySource("/some/directory"),
            "/loc4",
            "2026-05-31T00:00:03Z"));

        var store2 = this.CreateStore();
        var list = store2.List();

        var github = Assert.IsType<GithubSource>(list["github-entry"].Source);
        Assert.Equal("owner/repo", github.Repo);
        Assert.Equal("v2", github.Ref);
        Assert.Equal("some/path", github.Path);

        var git = Assert.IsType<GitSource>(list["git-entry"].Source);
        Assert.Equal("https://example.com/repo.git", git.Url);
        Assert.Equal("main", git.Ref);
        Assert.Equal("sub/path", git.Path);

        var file = Assert.IsType<LocalFileSource>(list["file-entry"].Source);
        Assert.Equal("/some/marketplace.json", file.Path);

        var dir = Assert.IsType<LocalDirectorySource>(list["dir-entry"].Source);
        Assert.Equal("/some/directory", dir.Path);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_name_throws_on_Add(string invalidName)
    {
        var store = this.CreateStore();
        var entry = new KnownMarketplaceEntry(
            new LocalDirectorySource("/path"),
            "/loc",
            "2026-05-31T00:00:00Z");

        Assert.Throws<ArgumentException>(() => store.Add(invalidName, entry));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_name_throws_on_Remove(string invalidName)
    {
        var store = this.CreateStore();

        Assert.Throws<ArgumentException>(() => store.Remove(invalidName));
    }

    [Fact]
    public void Corrupt_json_file_yields_empty_List()
    {
        Directory.CreateDirectory(this.tempDir);
        var jsonPath = Path.Combine(this.tempDir, "known_marketplaces.json");
        File.WriteAllText(jsonPath, "THIS IS NOT VALID JSON {{{{");

        var store = this.CreateStore();
        var list = store.List();

        Assert.Empty(list);
    }

    [Theory]
    [InlineData("normal-name", true)]
    [InlineData("my_market", true)]
    [InlineData("market123", true)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("../traversal", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsValidMarketplaceName_accepts_normal_and_rejects_traversal(string name, bool expected)
    {
        Assert.Equal(expected, KnownMarketplacesStore.IsValidMarketplaceName(name));
    }
}
