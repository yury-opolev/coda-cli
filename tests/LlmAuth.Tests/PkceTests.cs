using System.Security.Cryptography;
using System.Text;

namespace LlmAuth.Tests;

public class PkceTests
{
    [Fact]
    public void Base64UrlEncode_HasNoUrlUnsafeCharacters()
    {
        // 0xFF bytes guarantee a '+'/'/' would appear in plain base64.
        var bytes = new byte[] { 0xFB, 0xFF, 0xFE, 0xFC, 0xF8 };
        var encoded = Pkce.Base64UrlEncode(bytes);

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Fact]
    public void Base64UrlEncode_KnownInput_MatchesManualExpectation()
    {
        // 32 bytes 0..31.
        var input = new byte[32];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (byte)i;
        }

        var expected = Convert.ToBase64String(input)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        Assert.Equal(expected, Pkce.Base64UrlEncode(input));
        // Sanity: base64url of 32 bytes is 43 chars (no padding).
        Assert.Equal(43, Pkce.Base64UrlEncode(input).Length);
    }

    [Fact]
    public void GenerateCodeChallenge_IsBase64UrlOfSha256OfAsciiVerifier()
    {
        var verifier = Pkce.GenerateCodeVerifier();
        var expectedHash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var expected = Pkce.Base64UrlEncode(expectedHash);

        Assert.Equal(expected, Pkce.GenerateCodeChallenge(verifier));
    }

    [Fact]
    public void GenerateCodeVerifier_Is43Chars()
    {
        Assert.Equal(43, Pkce.GenerateCodeVerifier().Length);
    }

    [Fact]
    public void GenerateState_Is43CharsAndDiffersBetweenCalls()
    {
        var a = Pkce.GenerateState();
        var b = Pkce.GenerateState();

        Assert.Equal(43, a.Length);
        Assert.Equal(43, b.Length);
        Assert.NotEqual(a, b);
    }
}
