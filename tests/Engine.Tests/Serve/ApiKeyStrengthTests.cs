using Coda.Sdk.Serve;

namespace Engine.Tests.Serve;

public sealed class ApiKeyStrengthTests
{
    [Fact]
    public void Strong_128char_hex_token_passes()
    {
        var key = string.Concat(Enumerable.Range(0, 8).Select(_ => "0123456789abcdef")); // 128 chars, 16 distinct
        var (ok, reason) = ApiKeyStrength.Validate(key);
        Assert.True(ok, reason);
        Assert.Null(reason);
    }

    [Fact]
    public void Strong_66char_base64url_token_passes()
    {
        var key = "ABCDEFGHIJKLMNOPqrstuvwx0123456789-_ABCDEFGHIJKLMNOPqrstuvwx012345"; // 64 chars, mixed
        var (ok, _) = ApiKeyStrength.Validate(key);
        Assert.True(ok);
    }

    [Fact]
    public void Exactly_64char_hex_passes()
    {
        // 64 lowercase hex chars: distinct=16, pool=36, bits = 64 * log2(36) ≈ 331 > 256.
        var key = string.Concat(Enumerable.Range(0, 4).Select(_ => "0123456789abcdef"));
        Assert.Equal(64, key.Length);
        var (ok, reason) = ApiKeyStrength.Validate(key);
        Assert.True(ok, reason);
    }

    [Fact]
    public void Too_short_fails()
    {
        var key = new string('A', 63);
        var (ok, reason) = ApiKeyStrength.Validate(key);
        Assert.False(ok);
        Assert.Contains("64", reason!);
    }

    [Fact]
    public void Repeated_word_fails_low_variety()
    {
        var key = string.Concat(Enumerable.Repeat("password", 9)); // 72 chars, 7 distinct
        var (ok, reason) = ApiKeyStrength.Validate(key);
        Assert.False(ok);
        Assert.Contains("variety", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void All_same_char_fails_degenerate()
    {
        var key = new string('a', 64);
        var (ok, reason) = ApiKeyStrength.Validate(key);
        Assert.False(ok);
        Assert.Contains("degenerate", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Null_or_empty_fails()
    {
        Assert.False(ApiKeyStrength.Validate(null).Ok);
        Assert.False(ApiKeyStrength.Validate("").Ok);
    }
}
