using System.Net;
using System.Text;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Tests the HTTP response/idle timeouts that bound a hung LLM call inside the
/// client itself (no outer turn watchdog). Two bounds are exercised per client:
///   (1) response-headers timeout — the server never sends headers;
///   (2) stream-idle timeout — headers arrive but the body stalls mid-stream.
/// Both must throw <see cref="LlmHttpTimeoutException"/> quickly. Genuine outer
/// cancellation must still surface as <see cref="OperationCanceledException"/>.
/// No ConfigureAwait(false) in tests; all awaits guarded by WaitAsync.
/// </summary>
public sealed class LlmHttpTimeoutTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Blocks the request (never returns headers) until cancelled or released.</summary>
    private sealed class HeaderBlackHoleHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => this.gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await this.gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"type\":\"message_stop\"}\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    /// <summary>
    /// Returns headers immediately but the response body stalls forever after an
    /// optional prefix: a stream that emits some bytes then blocks indefinitely.
    /// </summary>
    private sealed class StallingBodyHandler : HttpMessageHandler
    {
        private readonly string prefix;

        public StallingBodyHandler(string prefix) => this.prefix = prefix;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new StallingStream(Encoding.UTF8.GetBytes(this.prefix)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>
    /// Returns headers immediately, then drips the supplied SSE chunks one read at a
    /// time with a fixed gap between them: a slow-but-healthy stream. The gap is kept
    /// below the idle bound while the total span exceeds it, so the per-chunk idle
    /// timer must be rearmed on every chunk or this would falsely trip.
    /// </summary>
    private sealed class SlowHealthyBodyHandler : HttpMessageHandler
    {
        private readonly IReadOnlyList<string> chunks;
        private readonly TimeSpan gap;

        public SlowHealthyBodyHandler(IReadOnlyList<string> chunks, TimeSpan gap)
        {
            this.chunks = chunks;
            this.gap = gap;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new SlowHealthyStream(this.chunks, this.gap));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Emits each chunk on its own read, waiting <c>gap</c> before each, then EOF.</summary>
    private sealed class SlowHealthyStream : Stream
    {
        private readonly Queue<byte[]> pending;
        private readonly TimeSpan gap;

        public SlowHealthyStream(IReadOnlyList<string> chunks, TimeSpan gap)
        {
            this.pending = new Queue<byte[]>(chunks.Select(c => Encoding.UTF8.GetBytes(c)));
            this.gap = gap;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            this.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (this.pending.Count == 0)
            {
                return 0;
            }

            // Wait a healthy (sub-idle-bound) gap before delivering the next chunk.
            await Task.Delay(this.gap, cancellationToken);
            var chunk = this.pending.Dequeue();
            var n = Math.Min(buffer.Length, chunk.Length);
            chunk.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        }
    }

    /// <summary>A stream that yields its prefix bytes, then blocks forever on the next read.</summary>
    private sealed class StallingStream : Stream
    {
        private readonly byte[] prefix;
        private int position;

        public StallingStream(byte[] prefix) => this.prefix = prefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            this.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (this.position < this.prefix.Length)
            {
                var n = Math.Min(buffer.Length, this.prefix.Length - this.position);
                this.prefix.AsSpan(this.position, n).CopyTo(buffer.Span);
                this.position += n;
                return n;
            }

            // Prefix exhausted: stall forever (until the idle-timeout cancels the read).
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private static CredentialManager SignedInClaude()
    {
        var store = new InMemoryTokenStore();
        var creds = new CredentialManager(store, [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
        creds.StoreAsync(ClaudeAiProvider.Id, new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "AT",
        }).GetAwaiter().GetResult();
        return creds;
    }

    private static CredentialManager SignedInCopilot()
    {
        var store = new InMemoryTokenStore();
        var creds = new CredentialManager(store, [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
        creds.StoreAsync(GitHubCopilotProvider.Id, new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "copilot-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        }).GetAwaiter().GetResult();
        return creds;
    }

    private static ChatRequest Request(string model) => new()
    {
        Model = model,
        Messages = [ChatMessage.UserText("hi")],
    };

    private static readonly LlmHttpTimeoutConfig FastBounds = new(
        ResponseHeadersTimeout: TimeSpan.FromMilliseconds(200),
        StreamIdleTimeout: TimeSpan.FromMilliseconds(200));

    // ── Response-headers timeout ───────────────────────────────────────────────

    [Fact]
    public async Task Anthropic_headers_blackhole_throws_timeout_fast()
    {
        var handler = new HeaderBlackHoleHandler();
        var http = new HttpClient(handler);
        var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http, timeoutConfig: FastBounds);

        var ex = await Assert.ThrowsAsync<LlmHttpTimeoutException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(Request("claude-opus-4-8"))) { }
        }).WaitAsync(WaitTimeout);

        Assert.Contains("headers", ex.Message, StringComparison.OrdinalIgnoreCase);
        handler.Release();
    }

    [Fact]
    public async Task Copilot_headers_blackhole_throws_timeout_fast()
    {
        var handler = new HeaderBlackHoleHandler();
        var http = new HttpClient(handler);
        var client = new CopilotChatClient(SignedInCopilot(), GitHubCopilotProvider.Id, httpClient: http, timeoutConfig: FastBounds);

        var ex = await Assert.ThrowsAsync<LlmHttpTimeoutException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(Request("gpt-4o"))) { }
        }).WaitAsync(WaitTimeout);

        Assert.Contains("headers", ex.Message, StringComparison.OrdinalIgnoreCase);
        handler.Release();
    }

    // ── Stream-idle timeout ────────────────────────────────────────────────────

    [Fact]
    public async Task Anthropic_stream_idle_throws_timeout_fast()
    {
        // Headers arrive, a partial SSE line is sent, then the body stalls forever.
        var http = new HttpClient(new StallingBodyHandler("data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n"));
        var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http, timeoutConfig: FastBounds);

        var ex = await Assert.ThrowsAsync<LlmHttpTimeoutException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(Request("claude-opus-4-8"))) { }
        }).WaitAsync(WaitTimeout);

        Assert.Contains("idle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Copilot_stream_idle_throws_timeout_fast()
    {
        var http = new HttpClient(new StallingBodyHandler("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n"));
        var client = new CopilotChatClient(SignedInCopilot(), GitHubCopilotProvider.Id, httpClient: http, timeoutConfig: FastBounds);

        var ex = await Assert.ThrowsAsync<LlmHttpTimeoutException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(Request("gpt-4o"))) { }
        }).WaitAsync(WaitTimeout);

        Assert.Contains("idle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Slow-but-healthy stream: per-chunk idle reset must NOT time out ─────────

    // Idle bound of 1s with chunks dripped 300ms apart: each per-chunk gap is safely
    // below the bound, while the whole stream spans well past it in total. Generous
    // absolute values keep the timing robust against test-host scheduling jitter; the
    // load-bearing invariant is gap < idleBound < totalSpan.
    private static readonly LlmHttpTimeoutConfig SlowHealthyBounds = new(
        ResponseHeadersTimeout: TimeSpan.FromSeconds(10),
        StreamIdleTimeout: TimeSpan.FromSeconds(1));

    private static readonly TimeSpan SlowHealthyGap = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task Anthropic_slow_but_healthy_stream_completes_without_timeout()
    {
        // Several SSE chunks delivered 300ms apart (below the 1s idle bound) so the
        // whole stream spans well past the bound in total, but no single gap exceeds
        // it. The idle timer must rearm on every chunk; if it didn't, this would
        // falsely trip LlmHttpTimeoutException.
        var chunks = new[]
        {
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":3}}}\n\n",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"one \"}}\n\n",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"two \"}}\n\n",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"three\"}}\n\n",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}\n\n",
            "data: {\"type\":\"message_stop\"}\n\n",
        };
        var http = new HttpClient(new SlowHealthyBodyHandler(chunks, SlowHealthyGap));
        var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http, timeoutConfig: SlowHealthyBounds);

        var text = new StringBuilder();
        await foreach (var ev in client.StreamAsync(Request("claude-opus-4-8")))
        {
            if (ev.Kind == AssistantEventKind.TextDelta)
            {
                text.Append(ev.Text);
            }
        }

        // No exception thrown, and all chunks were observed (timer kept rearming).
        Assert.Equal("one two three", text.ToString());
    }

    [Fact]
    public async Task Copilot_slow_but_healthy_stream_completes_without_timeout()
    {
        var chunks = new[]
        {
            "data: {\"choices\":[{\"delta\":{\"content\":\"one \"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"two \"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"three\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
            "data: [DONE]\n\n",
        };
        var http = new HttpClient(new SlowHealthyBodyHandler(chunks, SlowHealthyGap));
        var client = new CopilotChatClient(SignedInCopilot(), GitHubCopilotProvider.Id, httpClient: http, timeoutConfig: SlowHealthyBounds);

        var text = new StringBuilder();
        await foreach (var ev in client.StreamAsync(Request("gpt-4o")))
        {
            if (ev.Kind == AssistantEventKind.TextDelta)
            {
                text.Append(ev.Text);
            }
        }

        Assert.Equal("one two three", text.ToString());
    }

    // ── Genuine user cancellation stays an OperationCanceledException ───────────

    [Fact]
    public async Task Anthropic_user_cancellation_is_not_masked_as_timeout()
    {
        var handler = new HeaderBlackHoleHandler();
        var http = new HttpClient(handler);
        // Long bounds so they never fire; only the outer token cancels.
        var client = new AnthropicMessagesClient(
            SignedInClaude(), ClaudeAiProvider.Id, httpClient: http,
            timeoutConfig: new LlmHttpTimeoutConfig(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)));

        using var cts = new CancellationTokenSource();
        var enumerate = Task.Run(async () =>
        {
            await foreach (var _ in client.StreamAsync(Request("claude-opus-4-8"), cts.Token)) { }
        });

        cts.CancelAfter(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerate).WaitAsync(WaitTimeout);
        handler.Release();
    }

    // ── Config: env overrides + disable ────────────────────────────────────────

    [Fact]
    public void Config_defaults_are_sane()
    {
        var cfg = LlmHttpTimeoutConfig.Default;
        Assert.True(cfg.ResponseHeadersTimeout > TimeSpan.Zero);

        // Stream-idle bounds "no SSE bytes" — which includes a big-prompt time-to-first-token
        // and silent mid-stream "thinking" gaps. It must be generous (an LLM legitimately goes
        // silent far longer than a minute) yet stay below the Bridge's 300s process-kill
        // watchdog, so a genuine hang fails cleanly on coda's side (resumable) first.
        Assert.True(
            cfg.StreamIdleTimeout >= TimeSpan.FromSeconds(180),
            $"idle bound too tight for an LLM stream: {cfg.StreamIdleTimeout}");
        Assert.True(
            cfg.StreamIdleTimeout < TimeSpan.FromSeconds(300),
            $"idle bound must stay under the 300s Bridge watchdog: {cfg.StreamIdleTimeout}");
    }

    [Fact]
    public void Config_from_environment_reads_overrides_in_seconds()
    {
        var cfg = LlmHttpTimeoutConfig.FromEnvironment(
            headersEnv: "5",
            idleEnv: "7");

        Assert.Equal(TimeSpan.FromSeconds(5), cfg.ResponseHeadersTimeout);
        Assert.Equal(TimeSpan.FromSeconds(7), cfg.StreamIdleTimeout);
    }

    [Fact]
    public void Config_from_environment_disables_on_non_positive()
    {
        var cfg = LlmHttpTimeoutConfig.FromEnvironment(headersEnv: "0", idleEnv: "-1");

        Assert.Equal(Timeout.InfiniteTimeSpan, cfg.ResponseHeadersTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, cfg.StreamIdleTimeout);
    }

    // ── Overall-call deadline: caps an endless, never-completing stream ──────────

    [Fact]
    public async Task Overall_deadline_caps_an_endless_dripping_stream()
    {
        // Each gap (50ms) is well under the 10s idle bound, so the per-chunk idle guard
        // never fires; but the stream never ends. The overall deadline must terminate it.
        var endless = Enumerable.Range(0, 10_000)
            .Select(_ => "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n").ToArray();
        var http = new HttpClient(new SlowHealthyBodyHandler(endless, TimeSpan.FromMilliseconds(50)));
        var bounds = new LlmHttpTimeoutConfig(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10))
        {
            OverallCallTimeout = TimeSpan.FromMilliseconds(400),
        };
        var client = new CopilotChatClient(SignedInCopilot(), GitHubCopilotProvider.Id, httpClient: http, timeoutConfig: bounds);

        var ex = await Assert.ThrowsAsync<LlmHttpTimeoutException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(Request("gpt-4o"))) { }
        }).WaitAsync(WaitTimeout);

        Assert.Contains("exceeded", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Overall_deadline_default_is_generous_and_disable_able()
    {
        Assert.True(LlmHttpTimeoutConfig.Default.OverallCallTimeout >= TimeSpan.FromMinutes(5));
        var cfg = LlmHttpTimeoutConfig.FromEnvironment(headersEnv: "100", idleEnv: "60", callEnv: "0");
        Assert.Equal(Timeout.InfiniteTimeSpan, cfg.OverallCallTimeout);
    }
}
