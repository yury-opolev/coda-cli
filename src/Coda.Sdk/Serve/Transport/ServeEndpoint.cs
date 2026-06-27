using System.Security.Cryptography;
using System.Text;

namespace Coda.Sdk.Serve.Transport;

/// <summary>
/// Resolves a serve endpoint: validates a supplied one or generates a unique one, choosing the
/// OS-appropriate transport (Windows named pipe / Unix domain socket) and naming convention.
/// </summary>
public static class ServeEndpoint
{
    private const int MaxPipeNameLength = 256;
    private const int MaxUnixPathBytes = 104;

    public static ServeListenInfo Resolve(string? supplied)
    {
        if (OperatingSystem.IsWindows())
        {
            var name = supplied ?? $"coda-serve-{NewId()}";
            ValidatePipeName(name);
            return new ServeListenInfo("pipe", name);
        }

        var path = supplied ?? DefaultUnixPath();
        ValidateUnixPath(path);
        return new ServeListenInfo("unix", path);
    }

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes); // 12 hex chars
    }

    private static string DefaultUnixPath()
    {
        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            dir = Environment.GetEnvironmentVariable("TMPDIR");
        }

        if (string.IsNullOrEmpty(dir))
        {
            dir = "/tmp";
        }

        return Path.Combine(dir, $"coda-serve-{NewId()}.sock");
    }

    private static void ValidatePipeName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("endpoint (pipe name) must be non-empty.");
        }

        if (name.Length > MaxPipeNameLength)
        {
            throw new ArgumentException($"endpoint (pipe name) too long: {name.Length} > {MaxPipeNameLength}.");
        }

        if (name.Contains('\\'))
        {
            throw new ArgumentException("endpoint (pipe name) must not contain backslashes.");
        }
    }

    private static void ValidateUnixPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("endpoint (socket path) must be non-empty.");
        }

        if (Encoding.UTF8.GetByteCount(path) > MaxUnixPathBytes)
        {
            throw new ArgumentException($"endpoint (socket path) too long: must be ≤ {MaxUnixPathBytes} bytes.");
        }
    }
}
