namespace Coda.Tui.Tests;

using Coda.Tui.Plugins;

public sealed class MarketplaceInputParserTests : IDisposable
{
    private readonly List<string> tempPaths = [];

    public void Dispose()
    {
        foreach (var path in this.tempPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private string CreateTempFile(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{extension}");
        File.WriteAllText(path, "{}");
        this.tempPaths.Add(path);
        return path;
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        this.tempPaths.Add(path);
        return path;
    }

    [Fact]
    public void Github_shorthand()
    {
        var (source, error) = MarketplaceInputParser.Parse("owner/repo");

        var github = Assert.IsType<GithubSource>(source);
        Assert.Equal("owner/repo", github.Repo);
        Assert.Null(github.Ref);
        Assert.Null(error);
    }

    [Fact]
    public void Github_shorthand_with_hash_ref()
    {
        var (source, error) = MarketplaceInputParser.Parse("owner/repo#dev");

        var github = Assert.IsType<GithubSource>(source);
        Assert.Equal("owner/repo", github.Repo);
        Assert.Equal("dev", github.Ref);
        Assert.Null(error);
    }

    [Fact]
    public void Github_shorthand_with_at_ref()
    {
        var (source, error) = MarketplaceInputParser.Parse("owner/repo@v1");

        var github = Assert.IsType<GithubSource>(source);
        Assert.Equal("owner/repo", github.Repo);
        Assert.Equal("v1", github.Ref);
        Assert.Null(error);
    }

    [Fact]
    public void Ssh_url()
    {
        var (source, error) = MarketplaceInputParser.Parse("git@github.com:owner/repo.git");

        var git = Assert.IsType<GitSource>(source);
        Assert.Equal("git@github.com:owner/repo.git", git.Url);
        Assert.Null(error);
    }

    [Fact]
    public void Https_github_url_becomes_git_with_dot_git()
    {
        var (source, error) = MarketplaceInputParser.Parse("https://github.com/owner/repo");

        var git = Assert.IsType<GitSource>(source);
        Assert.EndsWith(".git", git.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Null(error);
    }

    [Fact]
    public void Https_dot_git_url()
    {
        var (source, error) = MarketplaceInputParser.Parse("https://example.com/x.git");

        var git = Assert.IsType<GitSource>(source);
        Assert.Equal("https://example.com/x.git", git.Url);
        Assert.Null(error);
    }

    [Fact]
    public void Https_non_git_url_is_error()
    {
        var (source, error) = MarketplaceInputParser.Parse("https://example.com/foo");

        Assert.Null(source);
        Assert.NotNull(error);
    }

    [Fact]
    public void Local_json_file_is_LocalFileSource()
    {
        var path = this.CreateTempFile(".json");

        var (source, error) = MarketplaceInputParser.Parse(path);

        var file = Assert.IsType<LocalFileSource>(source);
        Assert.Equal(path, file.Path);
        Assert.Null(error);
    }

    [Fact]
    public void Local_directory_is_LocalDirectorySource()
    {
        var path = this.CreateTempDir();

        var (source, error) = MarketplaceInputParser.Parse(path);

        var dir = Assert.IsType<LocalDirectorySource>(source);
        Assert.Equal(path, dir.Path);
        Assert.Null(error);
    }

    [Fact]
    public void Local_non_json_file_is_error()
    {
        var path = this.CreateTempFile(".txt");

        var (source, error) = MarketplaceInputParser.Parse(path);

        Assert.Null(source);
        Assert.NotNull(error);
        Assert.Contains(".json", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nonexistent_local_path_is_error()
    {
        var (source, error) = MarketplaceInputParser.Parse("./does-not-exist-xyz/");

        Assert.Null(source);
        Assert.NotNull(error);
        Assert.Contains("does not exist", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unrecognized_returns_null_null()
    {
        var (source, error) = MarketplaceInputParser.Parse("just some text");

        Assert.Null(source);
        Assert.Null(error);
    }
}
