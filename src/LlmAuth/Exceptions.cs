namespace LlmAuth;

/// <summary>Base type for all LlmAuth failures.</summary>
public class LlmAuthException : Exception
{
    public LlmAuthException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The OAuth authorization-code exchange failed (non-2xx from the token endpoint).</summary>
public sealed class OAuthExchangeException : LlmAuthException
{
    public OAuthExchangeException(int statusCode, string responseBody, Exception? inner = null)
        : base($"OAuth token exchange failed (HTTP {statusCode}).", inner)
    {
        this.StatusCode = statusCode;
        this.ResponseBody = responseBody;
    }

    public int StatusCode { get; }

    public string ResponseBody { get; }
}

/// <summary>Refreshing an OAuth access token failed.</summary>
public sealed class TokenRefreshException : LlmAuthException
{
    public TokenRefreshException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The interactive login was canceled, timed out, or the IdP returned an error.</summary>
public sealed class LoginCanceledException : LlmAuthException
{
    public LoginCanceledException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>No credential is stored for the requested provider.</summary>
public sealed class CredentialNotFoundException : LlmAuthException
{
    public CredentialNotFoundException(string message) : base(message) { }
}

/// <summary>The requested provider id was never registered with the manager.</summary>
public sealed class ProviderNotRegisteredException : LlmAuthException
{
    public ProviderNotRegisteredException(string providerId)
        : base($"No credential provider registered for id '{providerId}'.") { }
}

/// <summary>A platform-specific feature (e.g. DPAPI storage) was used on an unsupported OS.</summary>
public sealed class UnsupportedPlatformException : LlmAuthException
{
    public UnsupportedPlatformException(string message) : base(message) { }
}
