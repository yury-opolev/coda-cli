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
        BearerSpaceStreaming,
        BearerBody,
        DiscardBearerBody,
        DiscardBearerSpace,
        DiscardBearerEq,
    }

    private readonly StringBuilder _pending = new();
    private Mode _mode = Mode.Text;
    private int _bodyCount;

    /// <summary>
    /// Length of the trailing run of characters (within a discard state) that matches the
    /// case-insensitive <c>bearer</c> prefix. Lets a committed secret's greedy discard notice
    /// a nested <c>Bearer&#160;</c> prefix and keep redacting its body instead of leaking it.
    /// </summary>
    private int _bearerMatch;

    /// <summary>Processes <paramref name="input"/>, appending redacted output to <paramref name="output"/>.</summary>
    public void Process(string input, StringBuilder output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            Step(input[i], output);
        }
    }

    /// <summary>
    /// Drains any incomplete, unconfirmed candidate at end of stream. Rather than emitting the
    /// held candidate verbatim (which could leak a secret that starts later inside a failed
    /// candidate, e.g. an <c>sk-</c> key inside a too-short <c>Bearer</c> body), each pass emits
    /// only the first held character as ordinary text and rescans the remainder through the state
    /// machine so any nested secret is still confirmed and redacted. Every pass permanently emits
    /// at least one character, so the loop strictly shrinks the candidate and terminates.
    /// A confirmed secret already emitted its placeholder, so nothing secret is retained.
    /// </summary>
    public void Flush(StringBuilder output)
    {
        while (_pending.Length > 0)
        {
            var held = _pending.ToString();
            _pending.Clear();
            _mode = Mode.Text;
            _bodyCount = 0;
            _bearerMatch = 0;

            output.Append(held[0]);
            for (var i = 1; i < held.Length; i++)
            {
                Step(held[i], output);
            }
        }

        _mode = Mode.Text;
        _bodyCount = 0;
        _bearerMatch = 0;
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
                        _bearerMatch = 0;
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
                    // Track a nested "bearer" prefix continuously so it is still detected even if
                    // the token confirms partway through the word (the tail alone would miss it).
                    _bearerMatch = AdvanceBearerMatch(_bearerMatch, c);
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
                if (IsSkBody(c))
                {
                    // The greedy sk- discard also consumes the letters of a nested "Bearer"
                    // prefix; track how much of it we have seen so a following whitespace can
                    // hand off to nested-Bearer redaction instead of leaking the bearer body.
                    _bearerMatch = AdvanceBearerMatch(_bearerMatch, c);
                }
                else if (_bearerMatch == BearerPrefixLower.Length && char.IsWhiteSpace(c))
                {
                    _mode = Mode.DiscardBearerSpace;
                    _bearerMatch = 0;
                }
                else
                {
                    _mode = Mode.Text;
                    _bearerMatch = 0;
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
                            // Pathological whitespace run. The "Bearer" prefix and its whitespace
                            // are not themselves secret, so stream them as ordinary text and keep
                            // only a compact state that still redacts a body if one arrives. This
                            // bounds memory without abandoning Bearer protection.
                            output.Append(_pending);
                            output.Append(c);
                            _pending.Clear();
                            _mode = Mode.BearerSpaceStreaming;
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
                        _bearerMatch = AdvanceBearerMatch(0, c);
                        _mode = Mode.BearerBody;
                    }
                    else
                    {
                        Backtrack(c, output);
                    }
                }

                break;

            case Mode.BearerSpaceStreaming:
                if (char.IsWhiteSpace(c))
                {
                    // Whitespace is not secret: stream it directly, retaining O(1) state.
                    output.Append(c);
                }
                else if (IsBearerBody(c))
                {
                    _pending.Append(c);
                    _bodyCount = 1;
                    _bearerMatch = AdvanceBearerMatch(0, c);
                    _mode = Mode.BearerBody;
                }
                else
                {
                    output.Append(c);
                    _mode = Mode.Text;
                }

                break;

            case Mode.BearerBody:
                if (IsBearerBody(c))
                {
                    _pending.Append(c);
                    // Track a nested "bearer" prefix continuously so a nested Bearer secret is
                    // still detected even when this token confirms partway through that word.
                    _bearerMatch = AdvanceBearerMatch(_bearerMatch, c);
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
                else if (IsBearerBody(c))
                {
                    _bearerMatch = AdvanceBearerMatch(_bearerMatch, c);
                }
                else if (_bearerMatch == BearerPrefixLower.Length && char.IsWhiteSpace(c))
                {
                    _mode = Mode.DiscardBearerSpace;
                    _bearerMatch = 0;
                }
                else
                {
                    _mode = Mode.Text;
                    _bearerMatch = 0;
                    Step(c, output);
                }

                break;

            case Mode.DiscardBearerSpace:
                if (char.IsWhiteSpace(c))
                {
                    // Keep discarding the whitespace of a nested "Bearer " prefix (O(1) state).
                }
                else if (IsBearerBody(c))
                {
                    // Conservatively redact the complete nested Bearer secret rather than risk
                    // leaking it; output may differ from the batch regex but never leaks.
                    _mode = Mode.DiscardBearerBody;
                    _bearerMatch = AdvanceBearerMatch(0, c);
                }
                else
                {
                    _mode = Mode.Text;
                    _bearerMatch = 0;
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
        _bearerMatch = 0;

        output.Append(held[0]);
        for (var i = 1; i < held.Length; i++)
        {
            Step(held[i], output);
        }

        Step(c, output);
    }

    /// <summary>
    /// Advances the case-insensitive rolling match against the <c>bearer</c> prefix. The word
    /// "bearer" has no proper prefix that is also a suffix, so a failed extension can only restart
    /// a fresh match at a leading <c>b</c>; this keeps the tracker O(1) and allocation-free.
    /// </summary>
    private static int AdvanceBearerMatch(int match, char c)
    {
        var lower = char.ToLowerInvariant(c);
        if (match < BearerPrefixLower.Length && lower == BearerPrefixLower[match])
        {
            return match + 1;
        }

        return lower == BearerPrefixLower[0] ? 1 : 0;
    }

    private static bool IsSkBody(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';

    private static bool IsBearerBody(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
        c is '-' or '.' or '_' or '~' or '+' or '/';
}
