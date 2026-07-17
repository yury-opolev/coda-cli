using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Ui.Events;

/// <summary>Applies a rendered <see cref="UiSessionSnapshot"/> to a concrete frontend.</summary>
public interface IUiFrameSink
{
    /// <summary>Render <paramref name="snapshot"/> as the next frame.</summary>
    ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Observes every <see cref="UiEvent"/> in queue order, before it is folded into state.</summary>
public interface IUiEventObserver
{
    /// <summary>Handle a single event.</summary>
    ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken);
}

/// <summary>An <see cref="IUiFrameSink"/> that renders nothing; useful for headless runs and tests.</summary>
public sealed class NullUiFrameSink : IUiFrameSink
{
    /// <summary>The shared no-op instance.</summary>
    public static NullUiFrameSink Instance { get; } = new();

    private NullUiFrameSink()
    {
    }

    /// <inheritdoc />
    public ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>
/// The single reader of a <see cref="UiEventMailbox"/>. It awaits one event, drains the rest of the
/// current burst with <see cref="UiEventMailbox.TryRead"/>, folds every event through
/// <see cref="UiReducer"/>, and applies at most one frame per burst. Non-critical streaming frames are
/// capped at ~30&#160;FPS; critical events (completion, error, permission, cancellation, prompt, mode,
/// session and turn boundaries) apply immediately. Unchanged snapshots skip the frame entirely.
/// </summary>
public sealed class UiActor
{
    private const long MinStreamingFrameIntervalMs = 33;

    private readonly UiEventMailbox _mailbox;
    private readonly IUiFrameSink _frameSink;
    private readonly IUiEventObserver? _eventObserver;
    private readonly ActorUiPromptService? _prompts;
    private UiSessionSnapshot _current;
    private long _lastFrameTicks = long.MinValue;

    /// <summary>Create an actor that reduces events from <paramref name="mailbox"/> into frames.</summary>
    public UiActor(
        UiEventMailbox mailbox,
        IUiFrameSink frameSink,
        UiSessionSnapshot initial,
        IUiEventObserver? eventObserver = null,
        ActorUiPromptService? prompts = null)
    {
        _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        _frameSink = frameSink ?? throw new ArgumentNullException(nameof(frameSink));
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
        _eventObserver = eventObserver;
        _prompts = prompts;
    }

    /// <summary>The most recently applied snapshot.</summary>
    public UiSessionSnapshot Current => Volatile.Read(ref _current);

    /// <summary>Run the reduce loop until <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UiEvent first;
                try
                {
                    first = await _mailbox.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                var batch = new List<UiEvent> { first };
                while (_mailbox.TryRead(out var next))
                {
                    batch.Add(next!);
                }

                var snapshot = _current;
                var critical = false;
                foreach (var uiEvent in batch)
                {
                    if (_prompts is not null && uiEvent is UiPromptResponseSubmittedEvent submitted)
                    {
                        _prompts.Complete(submitted);
                    }

                    if (_eventObserver is not null)
                    {
                        await _eventObserver.ApplyEventAsync(uiEvent, cancellationToken).ConfigureAwait(false);
                    }

                    snapshot = UiReducer.Reduce(snapshot, uiEvent);
                    critical |= IsCritical(uiEvent);
                }

                if (snapshot == _current)
                {
                    continue;
                }

                if (!critical)
                {
                    await ThrottleStreamingFrameAsync(cancellationToken).ConfigureAwait(false);
                }

                await _frameSink.ApplyAsync(snapshot, cancellationToken).ConfigureAwait(false);
                _lastFrameTicks = Environment.TickCount64;
                Volatile.Write(ref _current, snapshot);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool IsCritical(UiEvent uiEvent) => uiEvent switch
    {
        AssistantTextCompletedEvent => true,
        ToolCompletedEvent => true,
        AgentErrorEvent => true,
        LimitReachedEvent => true,
        PermissionRequestedEvent => true,
        PermissionResolvedEvent => true,
        UserPromptSubmittedEvent => true,
        UserQuestionRequestedEvent => true,
        UserQuestionResolvedEvent => true,
        PlanApprovalRequestedEvent => true,
        PlanApprovalResolvedEvent => true,
        UiPromptRequestedEvent => true,
        UiPromptResponseSubmittedEvent => true,
        TurnStartedEvent => true,
        TurnCompletedEvent => true,
        TurnInterruptedEvent => true,
        ModeChangedEvent => true,
        SessionMetadataChangedEvent => true,
        TranscriptSeededEvent => true,
        TranscriptClearedEvent => true,
        ConsoleClearRequestedEvent => true,
        _ => false,
    };

    private async ValueTask ThrottleStreamingFrameAsync(CancellationToken cancellationToken)
    {
        if (_lastFrameTicks == long.MinValue)
        {
            return;
        }

        var wait = MinStreamingFrameIntervalMs - (Environment.TickCount64 - _lastFrameTicks);
        if (wait > 0)
        {
            await Task.Delay((int)wait, cancellationToken).ConfigureAwait(false);
        }
    }
}
