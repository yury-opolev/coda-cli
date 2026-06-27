using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace LlmAuth.Storage.Windows;

/// <summary>
/// Windows secure token store: serializes the credential JSON and encrypts it
/// with DPAPI (<see cref="DataProtectionScope.CurrentUser"/>) before writing one
/// file per key under <c>~/.coda/credentials</c>. Only the current Windows user can
/// decrypt it. This is the default secure store for the Windows-first v1; other
/// OSes plug in their own <see cref="ITokenStore"/>. DPAPI is keyed to the user,
/// not the path, so credentials migrated from the legacy location still decrypt.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStore : ITokenStore
{
    private static readonly byte[]? noEntropy = null;

    private readonly string directory;

    public DpapiTokenStore(string? directory = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new UnsupportedPlatformException("DpapiTokenStore requires Windows. Use a different ITokenStore on this OS.");
        }

        // When no directory is given, use ~/.coda/credentials, migrating any
        // credentials from the legacy location on first run.
        this.directory = directory ?? CredentialStoreLocation.ResolveDefault();
        Directory.CreateDirectory(this.directory);
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = this.PathFor(key);
        if (!File.Exists(path))
        {
            return Task.FromResult<string?>(null);
        }

        var protectedBytes = File.ReadAllBytes(path);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, noEntropy, DataProtectionScope.CurrentUser);
        return Task.FromResult<string?>(Encoding.UTF8.GetString(plainBytes));
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        var plainBytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(plainBytes, noEntropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(this.PathFor(key), protectedBytes);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = this.PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        // Make the key filesystem-safe (keys look like "llmauth:claude-ai").
        var safe = string.Concat(key.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c));
        return Path.Combine(this.directory, safe + ".cred");
    }
}
