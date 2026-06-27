using System.Runtime.CompilerServices;

namespace LlmClient;

/// <summary>
/// HTTP-layer timeout guards shared by the streaming LLM clients. These bound a
/// hung call where the operation actually lives — the HTTP request/response — using
/// linked <see cref="CancellationTokenSource"/>s with <c>CancelAfter</c> rather than
/// <see cref="HttpClient.Timeout"/> (which would cap a long-but-healthy stream).
///
/// A trip raises <see cref="LlmHttpTimeoutException"/> and is carefully
/// distinguished from genuine outer (user/host) cancellation: when the supplied
/// <paramref name="outerToken"/> is the one that fired, the original
/// <see cref="OperationCanceledException"/> is rethrown unchanged.
/// </summary>
public static class LlmHttpTimeoutGuards
{
    /// <summary>
    /// Send <paramref name="request"/> with <see cref="HttpCompletionOption.ResponseHeadersRead"/>,
    /// bounding the wait for response headers by <see cref="LlmHttpTimeoutConfig.ResponseHeadersTimeout"/>.
    /// On a headers timeout, throws <see cref="LlmHttpTimeoutException"/>; on outer
    /// cancellation, rethrows the <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithHeadersTimeoutAsync(
        HttpClient http,
        HttpRequestMessage request,
        LlmHttpTimeoutConfig config,
        CancellationToken outerToken)
    {
        if (!config.IsHeadersGuardEnabled)
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, outerToken).ConfigureAwait(false);
        }

        using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        headersCts.CancelAfter(config.ResponseHeadersTimeout);
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!outerToken.IsCancellationRequested && headersCts.IsCancellationRequested)
        {
            throw LlmHttpTimeoutException.Headers(config.ResponseHeadersTimeout);
        }
    }

    /// <summary>
    /// Send a non-streaming request (default <see cref="HttpCompletionOption.ResponseContentRead"/>),
    /// bounding the whole exchange by the sum of the two guards (headers wait + a single
    /// idle window for the buffered body). This is the non-streaming equivalent of the
    /// header + per-chunk guards used for the streaming path, and likewise avoids relying
    /// on <see cref="HttpClient.Timeout"/>. On a timeout, throws
    /// <see cref="LlmHttpTimeoutException"/>; on outer cancellation, rethrows the
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<HttpResponseMessage> SendNonStreamingAsync(
        HttpClient http,
        HttpRequestMessage request,
        LlmHttpTimeoutConfig config,
        CancellationToken outerToken)
    {
        var bound = NonStreamingBound(config);
        if (bound == Timeout.InfiniteTimeSpan)
        {
            return await http.SendAsync(request, outerToken).ConfigureAwait(false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        cts.CancelAfter(bound);
        try
        {
            return await http.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!outerToken.IsCancellationRequested && cts.IsCancellationRequested)
        {
            throw LlmHttpTimeoutException.Headers(bound);
        }
    }

    /// <summary>
    /// Overall bound for a non-streaming exchange: headers + idle when both are
    /// enabled; whichever single guard is enabled otherwise; infinite if neither.
    /// </summary>
    private static TimeSpan NonStreamingBound(LlmHttpTimeoutConfig config)
    {
        if (config.IsHeadersGuardEnabled && config.IsIdleGuardEnabled)
        {
            return config.ResponseHeadersTimeout + config.StreamIdleTimeout;
        }

        if (config.IsHeadersGuardEnabled)
        {
            return config.ResponseHeadersTimeout;
        }

        if (config.IsIdleGuardEnabled)
        {
            return config.StreamIdleTimeout;
        }

        return Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Wrap an SSE event stream so a mid-stream stall (no event within
    /// <see cref="LlmHttpTimeoutConfig.StreamIdleTimeout"/>) trips
    /// <see cref="LlmHttpTimeoutException"/>. The deadline is rescheduled on every
    /// yielded event, so an actively-progressing stream is never truncated. Outer
    /// cancellation passes through as <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async IAsyncEnumerable<AssistantStreamEvent> WithStreamIdleTimeout(
        IAsyncEnumerable<AssistantStreamEvent> source,
        LlmHttpTimeoutConfig config,
        Action<TimeSpan>? onFirstTokenHeartbeat,
        [EnumeratorCancellation] CancellationToken outerToken = default)
    {
        if (!config.IsIdleGuardEnabled && !config.IsFirstTokenGuardEnabled)
        {
            await foreach (var item in source.WithCancellation(outerToken).ConfigureAwait(false))
            {
                yield return item;
            }

            yield break;
        }

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        await using var enumerator = source.GetAsyncEnumerator(idleCts.Token);

        var first = true;
        while (true)
        {
            bool moved;
            if (first)
            {
                first = false;

                // The FIRST event (time-to-first-token) gets its own generous budget: a reasoning
                // model reading a huge prompt is WORKING, not idle. Emit periodic liveness heartbeats
                // while waiting so the orchestrator's own watchdog isn't tripped meanwhile.
                var budget = config.IsFirstTokenGuardEnabled ? config.FirstTokenTimeout : config.StreamIdleTimeout;
                idleCts.CancelAfter(budget);
                var moveTask = enumerator.MoveNextAsync().AsTask();
                var hb = config.FirstTokenHeartbeatInterval > TimeSpan.Zero ? config.FirstTokenHeartbeatInterval : budget;
                var elapsed = TimeSpan.Zero;
                try
                {
                    while (!moveTask.IsCompleted)
                    {
                        var winner = await Task.WhenAny(moveTask, Task.Delay(hb)).ConfigureAwait(false);
                        if (winner == moveTask)
                        {
                            break;
                        }

                        elapsed += hb;
                        onFirstTokenHeartbeat?.Invoke(elapsed);
                    }

                    moved = await moveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!outerToken.IsCancellationRequested && idleCts.IsCancellationRequested)
                {
                    throw LlmHttpTimeoutException.StreamIdle(budget);
                }
            }
            else
            {
                idleCts.CancelAfter(config.StreamIdleTimeout);
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!outerToken.IsCancellationRequested && idleCts.IsCancellationRequested)
                {
                    throw LlmHttpTimeoutException.StreamIdle(config.StreamIdleTimeout);
                }
            }

            if (!moved)
            {
                yield break;
            }

            // Stop the clock while the consumer processes this event so slow downstream
            // work is never mistaken for a provider stall; it is rearmed at the loop top.
            idleCts.CancelAfter(Timeout.InfiniteTimeSpan);
            yield return enumerator.Current;
        }
    }
}
