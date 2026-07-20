using System.Text;
using Coda.Common;

namespace Coda.Agent.Tasks;

/// <summary>
/// Incremental, streaming secret redactor that mirrors the patterns of
/// <see cref="SecretRedactor.Redact"/> (case-insensitive Bearer tokens and
/// <c>sk-</c> API keys) but operates on an arbitrary stream of characters fed in
/// any-sized chunks.
///
/// Unlike a buffer-then-regex approach, this redactor never needs to retain the
/// full body of a secret: as soon as the minimum token length is reached the
/// secret is confirmed, a placeholder is emitted, and every remaining token
/// character is discarded (O(1) memory) until a delimiter ends the token. This
/// makes it safe for arbitrarily long, newline-free secrets that would otherwise
/// straddle a fixed flush boundary and leak.
///
/// Ordinary text is preserved exactly. An unconfirmed, incomplete candidate held
/// at the end of the stream is emitted verbatim by <see cref="Flush"/>.
/// </summary>
internal sealed class StreamingSecretRedactor
{
    private const string SkPrefix = "sk-";
    private const int SkMinBody = 8;

    private const string BearerPrefixLower = "bearer";
    private const int BearerMinBody = 20;
    private const string BearerPlaceholder = "******";

    /// <summary>
    /// Upper bound on the unconfirmed candidate buffer. Only the whitespace run of a
    /// <c>Bearer\s+</c> prefix can grow without confirming a secret; capping it keeps
    /// memory bounded. Whitespace is not secret, so flushing it as text is safe.
    /// </summary>
    private const int MaxCandidate = 8192;

    private enum Mode
    {
        Text,
        Sk,
        SkBody,
        DiscardSkBody,
        BearerPrefix,
        BearerSpace,
        BearerBody,
        DiscardBearerBody,
        DiscardBearerEq,
    }

    private readonly StringBuilder _pending = new();
    private Mode _mode = Mode.Text;
    private int _bodyCount;

    /// <summary>Processes <paramref name="input"/>, appending redacted output to <paramref name="output"/>.</summary>
    public void Process(string input, StringBuilder output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            Step(input[i], output);
        }
    }

    /// <summary>
    /// Emits any incomplete, unconfirmed candidate as ordinary text and resets state.
    /// A confirmed secret already emitted its placeholder, so nothing secret is retained.
    /// </summary>
    public void Flush(StringBuilder output)
    {
        if (_pending.Length > 0)
        {
            output.Append(_pending);
        }

        _pending.Clear();
        _mode = Mode.Text;
        _bodyCount = 0;
    }

    private void Step(char c, StringBuilder output)
    {
        switch (_mode)
        {
            case Mode.Text:
                if (c == 's')
                {
                    _mode = Mode.Sk;
                    _pending.Append(c);
                }
                else if (c is 'b' or 'B')
                {
                    _mode = Mode.BearerPrefix;
                    _pending.Append(c);
                }
                else
                {
                    output.Append(c);
                }

                break;

            case Mode.Sk:
                if (c == SkPrefix[_pending.Length])
                {
                    _pending.Append(c);
                    if (_pending.Length == SkPrefix.Length)
                    {
                        _mode = Mode.SkBody;
                        _bodyCount = 0;
                    }
                }
                else
                {
                    Backtrack(c, output);
                }

                break;

            case Mode.SkBody:
                if (IsSkBody(c))
                {
                    _pending.Append(c);
                    if (++_bodyCount >= SkMinBody)
                    {
                        // Confirmed: emit the placeholder and discard the rest of the token.
                        output.Append(SecretRedactor.Placeholder);
                        _pending.Clear();
                        _mode = Mode.DiscardSkBody;
                    }
                }
                else
                {
                    Backtrack(c, output);
                }

                break;

            case Mode.DiscardSkBody:
                if (!IsSkBody(c))
                {
                    _mode = Mode.Text;
                    Step(c, output);
                }

                break;

            case Mode.BearerPrefix:
                if (char.ToLowerInvariant(c) == BearerPrefixLower[_pending.Length])
                {
                    _pending.Append(c);
                    if (_pending.Length == BearerPrefixLower.Length)
                    {
                        _mode = Mode.BearerSpace;
                    }
                }
                else
                {
                    Backtrack(c, output);
                }

                break;

            case Mode.BearerSpace:
                {
                    var haveWhitespace = _pending.Length > BearerPrefixLower.Length;
                    if (char.IsWhiteSpace(c))
                    {
                        if (_pending.Length >= MaxCandidate)
                        {
                            // Pathological whitespace run: flush and stop holding it.
                            Backtrack(c, output);
                        }
                        else
                        {
                            _pending.Append(c);
                        }
                    }
                    else if (haveWhitespace && IsBearerBody(c))
                    {
                        _pending.Append(c);
                        _bodyCount = 1;
                        _mode = Mode.BearerBody;
                    }
                    else
                    {
                        Backtrack(c, output);
                    }
                }

                break;

            case Mode.BearerBody:
                if (IsBearerBody(c))
                {
                    _pending.Append(c);
                    if (++_bodyCount >= BearerMinBody)
                    {
                        output.Append(BearerPlaceholder);
                        _pending.Clear();
                        _mode = Mode.DiscardBearerBody;
                    }
                }
                else
                {
                    Backtrack(c, output);
                }

                break;

            case Mode.DiscardBearerBody:
                if (c == '=')
                {
                    _mode = Mode.DiscardBearerEq;
                }
                else if (!IsBearerBody(c))
                {
                    _mode = Mode.Text;
                    Step(c, output);
                }

                break;

            case Mode.DiscardBearerEq:
                if (c != '=')
                {
                    _mode = Mode.Text;
                    Step(c, output);
                }

                break;
        }
    }

    /// <summary>
    /// The current unconfirmed candidate cannot include <paramref name="c"/>. Emit the first
    /// held character as ordinary text, then reprocess the remaining held characters and
    /// <paramref name="c"/> from the <see cref="Mode.Text"/> state so a secret starting later
    /// inside the failed candidate is still detected. The candidate is bounded, so this
    /// terminates.
    /// </summary>
    private void Backtrack(char c, StringBuilder output)
    {
        var held = _pending.ToString();
        _pending.Clear();
        _mode = Mode.Text;
        _bodyCount = 0;

        output.Append(held[0]);
        for (var i = 1; i < held.Length; i++)
        {
            Step(held[i], output);
        }

        Step(c, output);
    }

    private static bool IsSkBody(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';

    private static bool IsBearerBody(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
        c is '-' or '.' or '_' or '~' or '+' or '/';
}
