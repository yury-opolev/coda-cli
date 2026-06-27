using System.Security.Cryptography;
using System.Text;

namespace LlmAuth;

/// <summary>
/// Cross-platform secure token store: encrypts each credential with AES-GCM using a
/// per-installation 256-bit key stored in <c>key.bin</c>. One file per token key under
/// the store directory (default <c>~/.coda/credentials</c>). On Unix, the directory,
/// key file, and credential files are all restricted to the owning user (0700/0600).
/// </summary>
public sealed class FileTokenStore : ITokenStore
{
    private readonly string directory;
    private readonly byte[] key;

    public FileTokenStore(string? directory = null)
    {
        // When no directory is given, use ~/.coda/credentials, migrating any
        // credentials from the legacy location on first run.
        this.directory = directory ?? CredentialStoreLocation.ResolveDefault();
        Directory.CreateDirectory(this.directory);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                this.directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        this.key = this.LoadOrCreateKey();
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = this.PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        if (bytes.Length < 12 + 16)
        {
            return null;
        }

        var nonce = bytes[0..12];
        var tag = bytes[12..28];
        var ciphertext = bytes[28..];
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using (var aes = new AesGcm(this.key, AesGcm.TagByteSizes.MaxSize))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }
        }
        catch (CryptographicException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];

        using (var aes = new AesGcm(this.key, AesGcm.TagByteSizes.MaxSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var fileBytes = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(fileBytes, 0);
        tag.CopyTo(fileBytes, nonce.Length);
        ciphertext.CopyTo(fileBytes, nonce.Length + tag.Length);

        var path = this.PathFor(key);
        await File.WriteAllBytesAsync(path, fileBytes, cancellationToken).ConfigureAwait(false);
        SetOwnerOnly(path);
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

    private byte[] LoadOrCreateKey()
    {
        var keyPath = Path.Combine(this.directory, "key.bin");
        if (File.Exists(keyPath))
        {
            var existing = File.ReadAllBytes(keyPath);
            if (existing.Length == 32)
            {
                return existing;
            }
        }

        var newKey = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(keyPath, newKey);
        SetOwnerOnly(keyPath);
        return newKey;
    }

    private string PathFor(string key)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safe = string.Concat(key.Select(c => Array.IndexOf(invalidChars, c) >= 0 ? '_' : c));
        return Path.Combine(this.directory, safe + ".cred");
    }

    private static void SetOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
