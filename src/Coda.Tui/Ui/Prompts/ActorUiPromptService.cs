using System.Collections.Generic;
using Coda.Tui.Ui.Events;

namespace Coda.Tui.Ui.Prompts;

/// <summary>
/// The interactive prompt surface for the actor-driven UI. A request registers a pending
/// <see cref="TaskCompletionSource{TResult}"/> keyed by request id and publishes a
/// <see cref="UiPromptRequestedEvent"/>; the actor later feeds the matching
/// <see cref="UiPromptResponseSubmittedEvent"/> back through <see cref="Complete"/> to resolve it.
/// Cancellation removes and cancels only the caller's own pending prompt.
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

        var tcs = new TaskCompletionSource<UiPromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _pending[request.Id] = tcs;
        }

        // Registering before publishing avoids losing an already-cancelled token; removing on
        // cancellation touches only this request's entry so other pending prompts survive.
        CancellationTokenRegistration registration = default;
        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(static state =>
            {
                var (service, id, source, token) = ((ActorUiPromptService, Guid, TaskCompletionSource<UiPromptResponse>, CancellationToken))state!;
                lock (service._lock)
                {
                    if (service._pending.TryGetValue(id, out var existing) && ReferenceEquals(existing, source))
                    {
                        service._pending.Remove(id);
                    }
                }

                source.TrySetCanceled(token);
            }, (this, request.Id, tcs, cancellationToken));

            // Dispose the registration once the prompt resolves so long-lived tokens don't leak it.
            tcs.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

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

            registration.Dispose();
            throw;
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
