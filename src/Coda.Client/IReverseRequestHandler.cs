namespace Coda.Client;

/// <summary>
/// Decides how server-initiated requests (permission, question, plan approval)
/// are answered. Supplying one is <b>mandatory</b> before any work runs: there is
/// no implicit default, because an implicit default would have to either grant or
/// deny on the operator's behalf, and silently granting is exactly the failure
/// this contract forbids.
///
/// A handler must answer the request it is given (via the typed methods on
/// <see cref="ReverseRequest"/>) or leave it to be declined on disposal. It runs
/// off the transport's dispatch loop, so it may itself call back into the client
/// — for instance to <c>interrupt</c> before declining — without deadlocking.
/// </summary>
public interface IReverseRequestHandler
{
    ValueTask HandleAsync(ReverseRequest request, CancellationToken cancellationToken);
}

/// <summary>Ready-made <see cref="IReverseRequestHandler"/> policies.</summary>
public static class ReverseRequestHandlers
{
    /// <summary>
    /// The safe documented policy: decline every request explicitly, letting the
    /// engine apply each request's fail-closed default (deny / no-answer /
    /// reject). This mirrors the Rust reference client, which "cannot answer this
    /// request" and says so rather than guessing. Use it for any non-interactive
    /// worker that has no human to ask.
    /// </summary>
    public static IReverseRequestHandler Decline { get; } =
        new DelegateReverseRequestHandler((request, _) =>
        {
            request.Decline("this client cannot answer server-initiated requests");
            return ValueTask.CompletedTask;
        });

    /// <summary>Builds a handler from a delegate, for callers that want to route
    /// requests to their own UI or policy.</summary>
    public static IReverseRequestHandler FromDelegate(Func<ReverseRequest, CancellationToken, ValueTask> handler) =>
        new DelegateReverseRequestHandler(handler);

    private sealed class DelegateReverseRequestHandler : IReverseRequestHandler
    {
        private readonly Func<ReverseRequest, CancellationToken, ValueTask> handler;

        public DelegateReverseRequestHandler(Func<ReverseRequest, CancellationToken, ValueTask> handler)
        {
            this.handler = handler;
        }

        public ValueTask HandleAsync(ReverseRequest request, CancellationToken cancellationToken) =>
            this.handler(request, cancellationToken);
    }
}
