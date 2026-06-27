using LlmAuth;
using LlmAuth.Storage.Windows;

namespace Coda.Tui;

/// <summary>Selects the secure credential store for the current OS: DPAPI on Windows, an AES-GCM file store elsewhere.</summary>
public static class CredentialStoreFactory
{
    public static ITokenStore Create(string? directory = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return new DpapiTokenStore(directory);
        }

        return new FileTokenStore(directory);
    }
}
