namespace LlmClient;

/// <summary>
/// Bounds that detect a hung LLM HTTP call at the HTTP layer (where the operation
/// lives), independent of any outer turn/session watchdog. Two distinct guards:
/// <list type="bullet">
///   <item><see cref="ResponseHeadersTimeout"/> — the max time to receive the
///   response headers after the request is sent. Catches a black-holed request
///   whose headers never arrive.</item>
///   <item><see cref="StreamIdleTimeout"/> — the max gap between streamed chunks
///   while reading the response body. Reset on each received chunk, so it never
///   truncates a long-but-healthy stream; it only fires on a true mid-stream stall.</item>
/// </list>
/// A value of <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> (or any
/// non-positive value) disables that individual guard. These deliberately do NOT
/// use <see cref="System.Net.Http.HttpClient.Timeout"/>, which would cap the total
/// stream duration and kill a long, healthy response.
/// </summary>
public sealed record LlmHttpTimeoutConfig(TimeSpan ResponseHeadersTimeout, TimeSpan StreamIdleTimeout)
{
    /// <summary>Environment variable overriding <see cref="ResponseHeadersTimeout"/> (whole seconds; &lt;= 0 disables).</summary>
    public const string ResponseHeadersTimeoutEnv = "CODA_LLM_RESPONSE_HEADERS_TIMEOUT";

    /// <summary>Environment variable overriding <see cref="StreamIdleTimeout"/> (whole seconds; &lt;= 0 disables).</summary>
    public const string StreamIdleTimeoutEnv = "CODA_LLM_STREAM_IDLE_TIMEOUT";

    /// <summary>
    /// Overall wall-clock bound for a single LLM call while coda is actively awaiting
    /// the network. Generous (default 10 min) so it never truncates a healthy flowing
    /// stream — which keeps progressing and so never reaches the bound — and only
    /// catches an endless / black-holed stream. A non-cooperative consumer wedge is NOT
    /// catchable here (the Bridge watchdog handles that). ≤0 / Infinite disables.
    /// </summary>
    public TimeSpan OverallCallTimeout { get; init; } = TimeSpan.FromSeconds(600);

    /// <summary>Environment variable overriding <see cref="OverallCallTimeout"/> (whole seconds; &lt;= 0 disables).</summary>
    public const string OverallCallTimeoutEnv = "CODA_LLM_CALL_TIMEOUT";

    /// <summary>
    /// The max time to wait for the FIRST streamed event (the time-to-first-token), separate from
    /// <see cref="StreamIdleTimeout"/>. A reasoning model reading a very large prompt legitimately
    /// takes a long time before its first token — that is the model working, not a hung stream — so
    /// this is generous (default 8 min). A periodic liveness heartbeat (see
    /// <see cref="FirstTokenHeartbeatInterval"/>) is emitted while waiting so the orchestrator's own
    /// watchdog isn't tripped meanwhile. ≤0 / Infinite disables (falls back to the idle guard).
    /// </summary>
    public TimeSpan FirstTokenTimeout { get; init; } = TimeSpan.FromSeconds(480);

    /// <summary>Environment variable overriding <see cref="FirstTokenTimeout"/> (whole seconds; &lt;= 0 disables).</summary>
    public const string FirstTokenTimeoutEnv = "CODA_LLM_FIRST_TOKEN_TIMEOUT";

    /// <summary>How often to emit a liveness heartbeat while awaiting the first streamed event.</summary>
    public TimeSpan FirstTokenHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Defaults: 100s to receive headers (a black-holed request that never returns
    /// headers fails reasonably fast) and 240s of stream silence — i.e. no SSE bytes,
    /// which covers a large-prompt time-to-first-token and silent mid-stream "thinking"
    /// gaps that legitimately exceed a minute. 240s deliberately stays under the Bridge's
    /// 300s process-kill watchdog so a genuine hang trips this clean (resumable) coda-side
    /// failure first; the 600s overall deadline backstops an endless dripping stream.
    /// </summary>
    public static LlmHttpTimeoutConfig Default { get; } = new(
        ResponseHeadersTimeout: TimeSpan.FromSeconds(100),
        StreamIdleTimeout: TimeSpan.FromSeconds(240));

    /// <summary>
    /// Resolve the bounds from the process environment, honoring
    /// <see cref="ResponseHeadersTimeoutEnv"/> / <see cref="StreamIdleTimeoutEnv"/>
    /// (whole seconds; non-positive disables that guard) and falling back to
    /// <see cref="Default"/> for any unset/unparseable value.
    /// </summary>
    public static LlmHttpTimeoutConfig FromEnvironment() => FromEnvironment(
        Environment.GetEnvironmentVariable(ResponseHeadersTimeoutEnv),
        Environment.GetEnvironmentVariable(StreamIdleTimeoutEnv),
        Environment.GetEnvironmentVariable(OverallCallTimeoutEnv));

    /// <summary>Testable core of <see cref="FromEnvironment()"/> taking the headers/idle env values.</summary>
    public static LlmHttpTimeoutConfig FromEnvironment(string? headersEnv, string? idleEnv) =>
        FromEnvironment(headersEnv, idleEnv, null);

    /// <summary>Testable core of <see cref="FromEnvironment()"/> taking the raw env values.</summary>
    public static LlmHttpTimeoutConfig FromEnvironment(string? headersEnv, string? idleEnv, string? callEnv) => new(
        ResponseHeadersTimeout: ParseSeconds(headersEnv, Default.ResponseHeadersTimeout),
        StreamIdleTimeout: ParseSeconds(idleEnv, Default.StreamIdleTimeout))
    {
        OverallCallTimeout = ParseSeconds(callEnv, Default.OverallCallTimeout),
        FirstTokenTimeout = ParseSeconds(Environment.GetEnvironmentVariable(FirstTokenTimeoutEnv), Default.FirstTokenTimeout),
    };

    /// <summary>True when the first-token guard is active (a finite, positive bound).</summary>
    public bool IsFirstTokenGuardEnabled => this.FirstTokenTimeout > TimeSpan.Zero
        && this.FirstTokenTimeout != Timeout.InfiniteTimeSpan;

    /// <summary>True when this guard is active (a finite, positive bound).</summary>
    public bool IsHeadersGuardEnabled => this.ResponseHeadersTimeout > TimeSpan.Zero
        && this.ResponseHeadersTimeout != Timeout.InfiniteTimeSpan;

    /// <summary>True when this guard is active (a finite, positive bound).</summary>
    public bool IsIdleGuardEnabled => this.StreamIdleTimeout > TimeSpan.Zero
        && this.StreamIdleTimeout != Timeout.InfiniteTimeSpan;

    /// <summary>True when the overall-call deadline is active (a finite, positive bound).</summary>
    public bool IsOverallGuardEnabled => this.OverallCallTimeout > TimeSpan.Zero
        && this.OverallCallTimeout != Timeout.InfiniteTimeSpan;

    private static TimeSpan ParseSeconds(string? raw, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var seconds))
        {
            return fallback;
        }

        return seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
    }
}
