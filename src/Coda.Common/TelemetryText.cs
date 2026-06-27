namespace Coda.Common;

/// <summary>
/// Truncates long strings to a bounded preview for telemetry logging, so a single
/// log line never balloons with a multi-kilobyte body, message, or command output.
/// Text within the limit is returned unchanged; longer text is cut to the limit and
/// annotated with the full character count (e.g. <c>… [12345 chars total]</c>) so a
/// reader still knows how much was dropped.
/// </summary>
/// <remarks>
/// Lives in <c>Coda.Common</c> because both the LLM clients in <c>LlmClient</c>
/// and the agent tools in <c>Coda.Agent</c> depend on this helper — it is the lowest
/// project all can reference without inverting the dependency direction. Redaction is
/// the caller's responsibility: truncate previews of values that are already safe to
/// log, or redact before truncating.
/// </remarks>
public static class TelemetryText
{
    /// <summary>Default preview length when no explicit limit is supplied.</summary>
    public const int DefaultLimit = 500;

    /// <summary>Lower bound for a configured limit, so a preview stays useful.</summary>
    public const int MinLimit = 40;

    /// <summary>Environment variable overriding the preview length (whole chars).</summary>
    public const string TruncateEnv = "CODA_TELEMETRY_TRUNCATE";

    /// <summary>
    /// Truncates <paramref name="text"/> to <paramref name="max"/> characters. Returns
    /// the empty string for null/empty input, the original text when it is within the
    /// limit, and otherwise the first <paramref name="max"/> characters followed by a
    /// <c>… [{total} chars total]</c> suffix.
    /// </summary>
    public static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length <= max)
        {
            return text;
        }

        return $"{text[..max]}… [{text.Length} chars total]";
    }

    /// <summary>
    /// Truncates using the configured limit (<see cref="TruncateEnv"/> or
    /// <see cref="DefaultLimit"/>).
    /// </summary>
    public static string Truncate(string? text) =>
        Truncate(text, ResolveLimit(Environment.GetEnvironmentVariable(TruncateEnv)));

    /// <summary>
    /// Resolves the preview limit from the raw <see cref="TruncateEnv"/> value:
    /// <see cref="DefaultLimit"/> when unset/unparseable, otherwise the parsed value
    /// clamped up to <see cref="MinLimit"/>.
    /// </summary>
    public static int ResolveLimit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var limit))
        {
            return DefaultLimit;
        }

        return limit < MinLimit ? MinLimit : limit;
    }
}
