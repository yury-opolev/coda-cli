namespace LlmAuth;

/// <summary>
/// A provider whose interactive login uses the OAuth Device Authorization Grant
/// (RFC 8628) instead of a browser redirect — the user types a code at a
/// verification URL. The provider calls back to the host (<paramref name="onPrompt"/>)
/// with the code/URL, then polls until authorized. GitHub Copilot uses this.
/// </summary>
public interface IDeviceCodeLoginProvider : ICredentialProvider
{
    Task<Credential> LoginWithDeviceCodeAsync(
        LoginOptions options,
        Func<DeviceCodePrompt, CancellationToken, Task> onPrompt,
        CancellationToken cancellationToken = default);
}
