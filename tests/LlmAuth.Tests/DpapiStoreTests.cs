using LlmAuth.Storage.Windows;

namespace LlmAuth.Tests;

public class DpapiStoreTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LlmAuthTests_" + Guid.NewGuid().ToString("N"));
        return dir;
    }

    [Fact]
    public async Task SetGet_RoundTrips()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = NewTempDir();
        try
        {
            var store = new DpapiTokenStore(dir);
            await store.SetAsync("llmauth:claude-ai", "secret-value", default);
            var loaded = await store.GetAsync("llmauth:claude-ai", default);
            Assert.Equal("secret-value", loaded);
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Get_MissingKey_ReturnsNull()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = NewTempDir();
        try
        {
            var store = new DpapiTokenStore(dir);
            Assert.Null(await store.GetAsync("llmauth:absent", default));
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Delete_RemovesKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = NewTempDir();
        try
        {
            var store = new DpapiTokenStore(dir);
            await store.SetAsync("llmauth:claude-ai", "x", default);
            await store.DeleteAsync("llmauth:claude-ai", default);
            Assert.Null(await store.GetAsync("llmauth:claude-ai", default));
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }
}
