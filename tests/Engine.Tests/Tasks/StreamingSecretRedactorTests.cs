using System.Text;
using Coda.Agent.Tasks;
using Coda.Common;
using Xunit;

namespace Engine.Tests.Tasks;

public class StreamingSecretRedactorTests
{
    private static string Redact(string input)
    {
        var r = new StreamingSecretRedactor();
        var sb = new StringBuilder();
        r.Process(input, sb);
        r.Flush(sb);
        return sb.ToString();
    }

    private static string RedactCharByChar(string input)
    {
        var r = new StreamingSecretRedactor();
        var sb = new StringBuilder();
        foreach (var c in input)
        {
            r.Process(c.ToString(), sb);
        }

        r.Flush(sb);
        return sb.ToString();
    }

    [Theory]
    [InlineData("plain text without tokens")]
    [InlineData("skirt and basket, no secrets here")]
    [InlineData("Use a bearer token for authentication")]
    [InlineData("token=sk-abcdefghijklmnop rest")]
    [InlineData("head Bearer abcdefghijklmnopqrstuvwxyz tail")]
    [InlineData("sk-short")]
    [InlineData("Bearer tooShort")]
    [InlineData("BBearer abcdefghijklmnopqrstuvwxyz")]
    [InlineData("Bearsk-abcdefghijklmnop")]
    public void OutputIsIndependentOfChunkBoundaries(string input)
    {
        Assert.Equal(Redact(input), RedactCharByChar(input));
    }

    [Fact]
    public void PreservesOrdinaryText()
    {
        const string text = "the quick brown fox skips over baseball bearings";
        Assert.Equal(text, Redact(text));
    }

    [Fact]
    public void RedactsSkKeyAtMinimumLength()
    {
        // sk- plus exactly 8 body characters is a secret.
        var output = Redact("k=sk-abcdefgh done");
        Assert.DoesNotContain("sk-abcdefgh", output);
        Assert.Contains(SecretRedactor.Placeholder, output);
        Assert.Contains("k=", output);
        Assert.Contains("done", output);
    }

    [Fact]
    public void DoesNotRedactSkKeyBelowMinimumLength()
    {
        // sk- plus only 7 body characters is not long enough to be a secret.
        var output = Redact("k=sk-abcdefg done");
        Assert.Equal("k=sk-abcdefg done", output);
    }

    [Fact]
    public void RedactsBearerTokenAtMinimumLength()
    {
        // Real token of exactly the 20-char Bearer minimum must be redacted, prefix and all.
        var token = "A1b2C3d4E5f6G7h8I9j0"; // 20 body chars
        var input = $"auth Bearer {token} more";
        var output = Redact(input);
        Assert.DoesNotContain(token, output);
        Assert.DoesNotContain($"Bearer {token}", output);
        Assert.NotEqual(input, output);
        Assert.Contains("******", output);
        Assert.Contains("auth ", output);
        Assert.Contains("more", output);
    }

    [Fact]
    public void DoesNotRedactBearerTokenBelowMinimumLength()
    {
        // Real token one char below the 20-char minimum is not a secret and is preserved verbatim.
        var token = "A1b2C3d4E5f6G7h8I9j"; // 19 body chars
        var input = $"auth Bearer {token} end";
        var output = Redact(input);
        Assert.Equal(input, output);
        Assert.DoesNotContain("******", output);
    }

    [Fact]
    public void DiscardsArbitrarilyLongSkSecretToPlaceholderOnly()
    {
        // A 70k-character secret with no delimiter must collapse to a single placeholder
        // and never retain unbounded token content in the output.
        var output = Redact("sk-" + new string('a', 70_000));
        Assert.Equal(SecretRedactor.Placeholder, output);
    }

    [Fact]
    public void DiscardsArbitrarilyLongBearerSecretToPlaceholderOnly()
    {
        var output = Redact("Bearer " + new string('x', 70_000));
        Assert.Equal("******", output);
    }

    [Fact]
    public void FlushEmitsIncompleteNonSecretCandidate()
    {
        var r = new StreamingSecretRedactor();
        var sb = new StringBuilder();
        r.Process("trailing sk-abc", sb); // incomplete sk candidate (3 body chars)
        // Not yet flushed as text because it might still become a secret.
        r.Flush(sb);
        Assert.Equal("trailing sk-abc", sb.ToString());
    }

    [Fact]
    public void ConsumesTrailingEqualsOfBearerToken()
    {
        // A real 20-char token with trailing '=' padding: the token and its '=' must not survive.
        var token = "A1b2C3d4E5f6G7h8I9j0"; // 20 body chars
        var input = $"Bearer {token}== next";
        var output = Redact(input);
        Assert.DoesNotContain(token, output);
        Assert.Contains("******", output);
        Assert.DoesNotContain("==", output);
        Assert.Contains("next", output);
    }
}
