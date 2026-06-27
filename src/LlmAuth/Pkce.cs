using System.Security.Cryptography;
using System.Text;

namespace LlmAuth;

/// <summary>
/// PKCE (RFC 7636) + state helpers, matching the Claude Code v2.1.156 client:
/// <c>verifier = base64url(randomBytes(32))</c>,
/// <c>challenge = base64url(SHA256(verifier))</c>,
/// <c>state = base64url(randomBytes(32))</c>.
/// </summary>
public static class Pkce
{
    /// <summary>base64 with <c>+→-</c>, <c>/→_</c>, padding stripped (matches the TS helper).</summary>
    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static string GenerateCodeVerifier()
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }

    public static string GenerateCodeChallenge(string verifier)
    {
        // The verifier string is hashed as its byte representation. The verifier is
        // base64url (ASCII), so ASCII == UTF-8 here.
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    public static string GenerateState()
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }
}
