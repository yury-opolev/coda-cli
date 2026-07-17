using System.Collections.Generic;
using Coda.Tui.Ui.Events;

namespace Coda.Tui.Ui.Prompts;

/// <summary>
/// The interactive prompt surface for the actor-driven UI. A request registers a pending
/// <see cref="TaskCompletionSource{TResult}"/> keyed by request id and publishes a
/// <see cref="UiPromptRequestedEvent"/>; the actor later feeds the matching
/// <see cref="UiPromptResponseSubmittedEvent"/> back through <see cref="Complete"/> to resolve it.
/// Cancellation removes and cancels only the caller's own pending prompt, and publishes a matching
/// cancellation <see cref="UiPromptResponseSubmittedEvent"/> so the reducer clears its stale
/// <c>PendingPrompt</c>.
/// </summary>
public sealed class ActorUiPromptService : IUiPromptService
{
    private readonly IUiEventPublisher _publisher;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, TaskCompletionSource<UiPromptResponse>> _pending = new();

    /// <summary>Create a service that publishes requests through <paramref name="publisher"/>.</summary>
    public ActorUiPromptService(IUiEventPublisher publisher)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    /// <inheritdoc />
    public bool IsInteractive => true;

    /// <inheritdoc />
    public Task<UiPromptResponse> RequestAsync(UiPromptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Deny an already-cancelled request before touching state or publishing anything, so a
        // pre-cancelled token can never emit a request event that no one will ever answer.
        cancellationToken.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<UiPromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _pending[request.Id] = tcs;
        }

        // Publish the request BEFORE registering the cancellation callback. A cancellation that
        // races in during Publish must never emit its response ahead of the request, which would
        // leave the reducer with a stale PendingPrompt. Publishing first, then registering, means
        // that if the token is (or becomes) cancelled by now, CancellationToken.Register invokes
        // the callback synchronously below — after the request is already published — guaranteeing
        // request -> cancellation-response order.
        try
        {
            _publisher.Publish(new UiPromptRequestedEvent(request));
        }
        catch
        {
            lock (_lock)
            {
                if (_pending.TryGetValue(request.Id, out var existing) && ReferenceEquals(existing, tcs))
                {
                    _pending.Remove(request.Id);
                }
            }

            throw;
        }

        // Register after publishing; removing on cancellation touches only this request's entry so
        // other pending prompts survive. If the token was already cancelled during Publish, Register
        // runs the callback synchronously right here, emitting the cancellation response after the
        // request that is now live in the reducer.
        if (cancellationToken.CanBeCanceled)
        {
            CancellationTokenRegistration registration = cancellationToken.Register(static state =>
            {
                var (service, id, source, token) = ((ActorUiPromptService, Guid, TaskCompletionSource<UiPromptResponse>, CancellationToken))state!;

                // Remove our own entry first so a subsequent UiActor.Complete (from the response we
                // publish below) no-ops instead of racing to resolve an already-cancelled prompt.
                bool owned;
                lock (service._lock)
                {
                    owned = service._pending.TryGetValue(id, out var existing) && ReferenceEquals(existing, source);
                    if (owned)
                    {
                        service._pending.Remove(id);
                    }
                }

                source.TrySetCanceled(token);

                // The request event was already published, so its PendingPrompt is live in the
                // reducer. Emit a matching cancellation response to clear it; only owned prompts
                // ever published a request. Swallow only shutdown-specific failures from a
                // cancelled/disposed mailbox — never a broader set.
                if (owned)
                {
                    try
                    {
                        service._publisher.Publish(new UiPromptResponseSubmittedEvent(
                            id, new UiPromptResponse(true, [], null)));
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }, (this, request.Id, tcs, cancellationToken));

            // Dispose the registration once the prompt resolves so long-lived tokens don't leak it.
            // Safe even if the callback already ran synchronously during Register above: the task is
            // then already completed and this continuation runs synchronously to dispose.
            tcs.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return tcs.Task;
    }

    /// <summary>
    /// Resolve the pending prompt matching <paramref name="submitted"/>. Returns false (a no-op) when
    /// no matching pending prompt exists — e.g. an unknown or duplicate response.
    /// </summary>
    public bool Complete(UiPromptResponseSubmittedEvent submitted)
    {
        ArgumentNullException.ThrowIfNull(submitted);

        TaskCompletionSource<UiPromptResponse>? tcs;
        lock (_lock)
        {
            if (!_pending.Remove(submitted.RequestId, out tcs))
            {
                return false;
            }
        }

        return tcs.TrySetResult(submitted.Response);
    }
}
