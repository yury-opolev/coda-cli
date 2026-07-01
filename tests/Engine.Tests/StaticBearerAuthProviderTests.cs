using System.Net;
using Coda.Mcp.Auth;

namespace Engine.Tests;

/// <summary>
/// Covers <see cref="StaticBearerAuthProvider"/>, the <c>auth.mode = "bearer"</c> case that
/// attaches a fixed token and — unlike OAuth — cannot recover from a 401.
/// </summary>
public sealed class StaticBearerAuthProviderTests
{
    [Fact]
    public async Task GetAccessToken_returns_the_configured_token()
    {
        var provider = new StaticBearerAuthProvider("tok-static");

        Assert.Equal("tok-static", await provider.GetAccessTokenAsync());
    }

    [Theory]
    [InlineData(null)]   // ArgumentNullException
    [InlineData("")]     // ArgumentException
    public void Constructor_rejects_null_or_empty_token(string? token)
    {
        // ThrowIfNullOrEmpty throws ArgumentNullException for null and ArgumentException for
        // empty; both derive from ArgumentException, which is the contract we care about here.
        Assert.ThrowsAny<ArgumentException>(() => new StaticBearerAuthProvider(token!));
    }

    [Fact]
    public async Task HandleUnauthorized_cannot_recover_and_returns_false()
    {
        var provider = new StaticBearerAuthProvider("tok-static");
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        Assert.False(await provider.HandleUnauthorizedAsync(response));
    }

    [Fact]
    public async Task Token_is_stable_across_calls_even_after_a_401()
    {
        var provider = new StaticBearerAuthProvider("tok-static");
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        await provider.HandleUnauthorizedAsync(response);

        // A static token never rotates: the second read matches the first.
        Assert.Equal("tok-static", await provider.GetAccessTokenAsync());
    }
}
