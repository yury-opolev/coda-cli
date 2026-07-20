using System.Collections.Generic;
using System.Text;
using Coda.Agent.Tasks;
using Coda.Common;
using Xunit;

namespace Engine.Tests.Tasks;

/// <summary>
/// Regression tests for streaming-log redaction gaps: leaks that could escape only
/// at flush time, when a committed secret greedily swallows a nested secret prefix,
/// or when a pathological whitespace run defeated the Bearer candidate cap. These
/// assert the security invariant (no known secret substring survives) rather than
/// exact placeholder equivalence with the batch regex redactor.
/// </summary>
public class StreamingSecretRedactorRedactionGapTests
{
    private static string RedactWhole(string input)
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

    private static string RedactChunks(string input, Random rng)
    {
        var r = new StreamingSecretRedactor();
        var sb = new StringBuilder();
        var i = 0;
        while (i < input.Length)
        {
            var len = Math.Min(rng.Next(1, 8), input.Length - i);
            r.Process(input.Substring(i, len), sb);
            i += len;
        }

        r.Flush(sb);
        return sb.ToString();
    }

    // Issue 1: a failed Bearer candidate (body < 20) may itself contain an sk- key.
    // The old Flush wrote the candidate verbatim, leaking the nested sk- key at Dispose.
    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    public void FlushDoesNotLeakSkKeyInsideFailedBearerCandidate(int skBodyLength)
    {
        var sk = "sk-" + new string('a', skBodyLength); // confirmed sk- key (>= 8 body chars)
        var input = "Bearer " + sk;                      // Bearer body is only "sk-aaaa..." (< 20) -> fails Bearer
        Assert.True(sk.Length - 3 < 20, "Bearer body must stay below the Bearer minimum");

        var whole = RedactWhole(input);
        var charByChar = RedactCharByChar(input);

        Assert.DoesNotContain(sk, whole);
        Assert.DoesNotContain(sk, charByChar);
        Assert.Contains(SecretRedactor.Placeholder, whole);
        Assert.Contains(SecretRedactor.Placeholder, charByChar);
    }

    // Issue 2: greedy sk- commitment discards the letters of a nested "Bearer " prefix,
    // then the following body was streamed as ordinary text and leaked.
    [Fact]
    public void DoesNotLeakBearerBodyNestedInsideCommittedSkKey()
    {
        var bearerBody = new string('Z', 30);
        var input = "sk-abcdefgh" + "Bearer " + bearerBody;

        Assert.DoesNotContain(bearerBody, RedactWhole(input));
        Assert.DoesNotContain(bearerBody, RedactCharByChar(input));
    }

    // Issue 2 (reviewer reproduction): the nested Bearer prefix immediately follows the
    // sk- minimum body, so it is consumed by the greedy sk- discard.
    [Fact]
    public void DoesNotLeakBearerBodyNestedInsideCommittedBearerToken()
    {
        var first = new string('a', 25);   // confirms Bearer, then keeps discarding
        var second = new string('Z', 30);  // the nested Bearer body that must not survive
        var input = "Bearer " + first + "Bearer " + second;

        Assert.DoesNotContain(second, RedactWhole(input));
        Assert.DoesNotContain(second, RedactCharByChar(input));
    }

    // Issue 3: a Bearer whitespace run longer than the candidate cap must not abandon
    // Bearer protection; the whitespace itself may stream as text but the body must be redacted.
    [Fact]
    public void RetainsBearerProtectionAcrossLargeWhitespaceRun()
    {
        var spaces = new string(' ', 9000); // exceeds the 8192 candidate cap
        var token = new string('a', 40);
        var input = "Bearer" + spaces + token;

        var whole = RedactWhole(input);
        Assert.DoesNotContain(token, whole);
        Assert.DoesNotContain(token, RedactCharByChar(input));
    }

    // Issue 3: even a huge whitespace run keeps redaction chunk-boundary independent,
    // since the streaming transition is a deterministic function of character position.
    [Fact]
    public void LargeWhitespaceRunIsChunkBoundaryIndependent()
    {
        var input = "Bearer" + new string(' ', 9000) + new string('a', 40);
        Assert.Equal(RedactWhole(input), RedactCharByChar(input));
    }

    // Issue 5: deterministic differential/fuzz over chunk boundaries. The invariant is that
    // no known confirmed-secret substring survives, for any chunking.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void FuzzNoKnownSecretSubstringSurvivesAcrossChunkBoundaries(int seed)
    {
        const string SkAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-";
        const string BearerAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~+/";
        string Token(Random r, int len, string alphabet)
        {
            var sb = new StringBuilder(len);
            for (var i = 0; i < len; i++)
            {
                sb.Append(alphabet[r.Next(alphabet.Length)]);
            }

            return sb.ToString();
        }

        var fillers = new[]
        {
            " hello ", " world ", "\n", " the bearer of ", "skip ", "base ", "=", " x ",
            "Bear", "sk", " bearer note ", "==", "carry on ",
        };

        var build = new Random(seed);
        var sb = new StringBuilder();
        var secrets = new List<string>();
        for (var f = 0; f < 60; f++)
        {
            switch (build.Next(7))
            {
                case 0:
                case 1:
                    sb.Append(fillers[build.Next(fillers.Length)]);
                    break;
                case 2:
                {
                    var sk = "sk-" + Token(build, build.Next(8, 40), SkAlphabet);
                    secrets.Add(sk);
                    sb.Append(sk);
                    break;
                }
                case 3:
                {
                    var body = Token(build, build.Next(20, 50), BearerAlphabet);
                    secrets.Add(body);
                    sb.Append("Bearer ").Append(body);
                    break;
                }
                case 4:
                {
                    // Adjacency: an sk- key immediately followed by a nested Bearer secret.
                    var sk = "sk-" + Token(build, build.Next(8, 20), SkAlphabet);
                    var body = Token(build, build.Next(20, 40), BearerAlphabet);
                    secrets.Add(sk);
                    secrets.Add(body);
                    sb.Append(sk).Append("Bearer ").Append(body);
                    break;
                }
                case 5:
                {
                    // Failed Bearer candidate whose body is itself an sk- key (flush path).
                    var sk = "sk-" + Token(build, build.Next(8, 16), SkAlphabet);
                    secrets.Add(sk);
                    sb.Append("Bearer ").Append(sk);
                    break;
                }
                case 6:
                    sb.Append(new string(' ', build.Next(1, 4)));
                    break;
            }
        }

        var input = sb.ToString();
        for (var trial = 0; trial < 24; trial++)
        {
            var output = RedactChunks(input, new Random((seed * 1000) + trial));
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, output);
            }
        }
    }
}
