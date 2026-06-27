using Coda.Tui;
using LlmAuth;
using LlmAuth.Storage.Windows;

namespace Coda.Tui.Tests;

public sealed class CredentialStoreFactoryTests : IDisposable
{
    private readonly string tempDir;

    public CredentialStoreFactoryTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    [Fact]
    public void Create_returns_os_appropriate_store()
    {
        var store = CredentialStoreFactory.Create(this.tempDir);

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<DpapiTokenStore>(store);
        }
        else
        {
            Assert.IsType<FileTokenStore>(store);
        }
    }

    [Fact]
    public async Task Create_store_round_trips()
    {
        var store = CredentialStoreFactory.Create(this.tempDir);

        await store.SetAsync("k", "v");

        Assert.Equal("v", await store.GetAsync("k"));
    }
}
