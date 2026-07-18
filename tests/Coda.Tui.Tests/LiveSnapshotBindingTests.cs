using Coda.Agent;
using Coda.Tui;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Proves the semantic <c>/status</c> snapshot provider reads the live actor state rather than a
/// snapshot only captured when a plain/Spectre loop stops. In Terminal.Gui mode the controller never
/// captures a snapshot, so binding the provider to the actor is what keeps <c>/status</c> live.
/// </summary>
public sealed class LiveSnapshotBindingTests
{
    [Fact]
    public async Task Status_reflects_live_actor_snapshot_after_metadata_change()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        using var mailbox = new UiEventMailbox(64);
        using var actorCts = new CancellationTokenSource();
        var actor = new UiActor(mailbox, NullUiFrameSink.Instance, UiSessionSnapshot.Empty);
        var actorTask = actor.RunAsync(actorCts.Token);

        var metadata = new SessionMetadataChangedEvent(
            SessionId: "sess-live",
            Provider: "github-copilot",
            Model: "gpt-5-live",
            RequestedEffort: "high",
            EffectiveEffort: "high",
            WorkingDirectory: context.Session.WorkingDirectory,
            PermissionMode: PermissionMode.Plan,
            Connected: true);
        mailbox.Publish(metadata);

        using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.FlushAsync(flushCts.Token);

        // Bind through the exact production helper so the test exercises the real wiring.
        LiveSnapshotBinding.Bind(context, actor);

        await new StatusCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        var output = console.Output;
        Assert.Contains("github-copilot", output);
        Assert.Contains("connected", output);
        Assert.Contains("gpt-5-live", output);
        Assert.Contains("high", output);
        Assert.Contains("Plan", output);

        // SessionMetadataEvents.Build must read the live Connected from the bound provider, so a
        // metadata republish (e.g. after a turn) never resets Connected back to false.
        var rebuilt = SessionMetadataEvents.Build(context);
        Assert.True(rebuilt.Connected);

        actorCts.Cancel();
        await actorTask;
    }

    [Fact]
    public async Task Bound_provider_tracks_subsequent_live_updates()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();

        using var mailbox = new UiEventMailbox(64);
        using var actorCts = new CancellationTokenSource();
        var actor = new UiActor(mailbox, NullUiFrameSink.Instance, UiSessionSnapshot.Empty);
        var actorTask = actor.RunAsync(actorCts.Token);

        LiveSnapshotBinding.Bind(context, actor);
        Assert.False(context.UiSnapshotProvider!().Connected);

        mailbox.Publish(new SessionMetadataChangedEvent(
            SessionId: null,
            Provider: "claude-ai",
            Model: "claude",
            RequestedEffort: null,
            EffectiveEffort: "auto",
            WorkingDirectory: context.Session.WorkingDirectory,
            PermissionMode: PermissionMode.Default,
            Connected: true));

        using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.FlushAsync(flushCts.Token);

        Assert.True(context.UiSnapshotProvider!().Connected);

        actorCts.Cancel();
        await actorTask;
    }
}
