using System.Text;

namespace LlmAuth.Tests;

public sealed class FileTokenStoreTests : IDisposable
{
    private readonly string tempDir;

    public FileTokenStoreTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), "LlmAuthFileStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    private FileTokenStore CreateStore() => new(this.tempDir);

    [Fact]
    public async Task Set_then_Get_round_trips()
    {
        var store = this.CreateStore();
        var value = """
            {
              "access_token": "eyJhbGciOiJSUzI1NiJ9.payload.sig",
              "token_type": "Bearer",
              "expires_in": 3600,
              "refresh_token": "rt_abc123",
              "scope": "openid profile email"
            }
            """;

        await store.SetAsync("llmauth:round-trip", value);
        var result = await store.GetAsync("llmauth:round-trip");

        Assert.Equal(value, result);
    }

    [Fact]
    public async Task Second_instance_over_same_dir_reads_first_instance_data()
    {
        // Simulates a process restart: a fresh store over the same directory must
        // reload the persisted AES key and decrypt what the previous instance wrote.
        var store1 = new FileTokenStore(this.tempDir);
        await store1.SetAsync("llmauth:persist-check", "persisted-value");

        var store2 = new FileTokenStore(this.tempDir);
        var result = await store2.GetAsync("llmauth:persist-check");

        Assert.Equal("persisted-value", result);
    }

    [Fact]
    public async Task Get_missing_returns_null()
    {
        var store = this.CreateStore();

        var result = await store.GetAsync("llmauth:does-not-exist");

        Assert.Null(result);
    }

    [Fact]
    public async Task Delete_removes()
    {
        var store = this.CreateStore();

        await store.SetAsync("llmauth:deleteme", "some-value");
        await store.DeleteAsync("llmauth:deleteme");
        var result = await store.GetAsync("llmauth:deleteme");

        Assert.Null(result);
    }

    [Fact]
    public async Task Overwrite_returns_latest()
    {
        var store = this.CreateStore();

        await store.SetAsync("llmauth:overwrite", "a");
        await store.SetAsync("llmauth:overwrite", "b");
        var result = await store.GetAsync("llmauth:overwrite");

        Assert.Equal("b", result);
    }

    [Fact]
    public async Task Encrypted_at_rest()
    {
        var store = this.CreateStore();
        const string PlaintextValue = "SUPER_SECRET_TOKEN_VALUE";

        await store.SetAsync("llmauth:secret", PlaintextValue);

        var credFile = Directory.GetFiles(this.tempDir, "*.cred").Single();
        var rawBytes = await File.ReadAllBytesAsync(credFile);
        var plaintextBytes = Encoding.UTF8.GetBytes(PlaintextValue);

        Assert.DoesNotContain(plaintextBytes, rawBytes);
    }

    [Fact]
    public async Task Tampered_file_returns_null()
    {
        var store = this.CreateStore();

        await store.SetAsync("llmauth:tamper", "original-value");

        var credFile = Directory.GetFiles(this.tempDir, "*.cred").Single();
        var rawBytes = await File.ReadAllBytesAsync(credFile);
        rawBytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(credFile, rawBytes);

        var result = await store.GetAsync("llmauth:tamper");

        Assert.Null(result);
    }

    [Fact]
    public async Task Multiple_keys_isolated()
    {
        var store = this.CreateStore();

        await store.SetAsync("llmauth:key1", "value-one");
        await store.SetAsync("llmauth:key2", "value-two");

        Assert.Equal("value-one", await store.GetAsync("llmauth:key1"));
        Assert.Equal("value-two", await store.GetAsync("llmauth:key2"));
    }

    [Fact]
    public async Task Key_with_colon_maps_to_safe_filename()
    {
        var store = this.CreateStore();

        await store.SetAsync("llmauth:claude-ai", "token-value-for-claude");
        var result = await store.GetAsync("llmauth:claude-ai");

        Assert.Equal("token-value-for-claude", result);
    }

    [Fact]
    public async Task Unix_files_are_0600()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = this.CreateStore();

        await store.SetAsync("llmauth:perm-check", "some-token");

        var credFile = Directory.GetFiles(this.tempDir, "*.cred").Single();
        var credMode = File.GetUnixFileMode(credFile);
        var keyMode = File.GetUnixFileMode(Path.Combine(this.tempDir, "key.bin"));

        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(expected, credMode);
        Assert.Equal(expected, keyMode);
    }

    [Fact]
    public async Task SetThenGet_RoundTripsAcrossInstances()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ftok-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new FileTokenStore(dir).SetAsync("llmauth:copilot", "secret-value");
            var got = await new FileTokenStore(dir).GetAsync("llmauth:copilot");
            Assert.Equal("secret-value", got);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void KeyFile_OnWindows_IsDpapiWrapped_NotRawKey()
    {
        if (!OperatingSystem.IsWindows()) { return; } // Windows-only behaviour
        var dir = Path.Combine(Path.GetTempPath(), "ftok-" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = new FileTokenStore(dir); // creates key.bin
            var raw = File.ReadAllBytes(Path.Combine(dir, "key.bin"));
            // A DPAPI blob is NOT 32 bytes (a raw AES-256 key would be exactly 32).
            Assert.NotEqual(32, raw.Length);
            // And it round-trips back to a 32-byte key.
            Assert.Equal(32, WindowsCredentialProtection.UnprotectKey(raw).Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CorruptKeyFile_IsRegenerated_StoreStillWorks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ftok-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            // A key.bin that is neither a valid raw 32-byte key nor a valid DPAPI blob.
            File.WriteAllBytes(Path.Combine(dir, "key.bin"), new byte[] { 1, 2, 3, 4, 5 });

            var store = new FileTokenStore(dir); // must NOT throw — regenerates the key
            await store.SetAsync("llmauth:copilot", "secret-value");
            Assert.Equal("secret-value", await new FileTokenStore(dir).GetAsync("llmauth:copilot"));
        }
        finally { Directory.Delete(dir, true); }
    }
}

internal static class ByteArrayExtensions
{
    internal static bool Contains(this byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return true;
            }
        }

        return false;
    }
}
