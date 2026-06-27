namespace LlmAuth;

/// <summary>
/// An in-progress interactive login. The low-layer API: a host reads
/// <see cref="AuthorizeUrl"/>, drives the browser/redirect itself, then calls
/// <see cref="CompleteAsync"/> with the returned code. The high-layer
/// <see cref="CredentialManager.LoginAsync"/> drives this for you over loopback.
/// </summary>
public interface ILoginFlow
{
    /// <summary>The URL the user must visit to authorize.</summary>
    Uri AuthorizeUrl { get; }

    /// <summary>The CSRF <c>state</c> value; compare against the redirect's state.</summary>
    string State { get; }

    /// <summary>Exchange the authorization code for a credential.</summary>
    Task<Credential> CompleteAsync(string code, string state, CancellationToken cancellationToken = default);
}
