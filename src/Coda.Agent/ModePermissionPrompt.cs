namespace Coda.Agent;

/// <summary>
/// Applies a <see cref="PermissionMode"/> via <see cref="PermissionPolicy"/>, only
/// delegating to an inner interactive prompt when the decision is
/// <see cref="PermissionDecision.Ask"/>. With no inner prompt (headless), an
/// <c>Ask</c> denies. This is what the agent loop receives, so the loop stays
/// unaware of modes.
/// </summary>
public sealed class ModePermissionPrompt : IPermissionPrompt
{
    private readonly PermissionModeState state;
    private readonly IPermissionPrompt? inner;

    /// <summary>
    /// Fixed-mode compatibility overload: wraps <paramref name="mode"/> in a private
    /// <see cref="PermissionModeState"/> so the mode never changes for this prompt's lifetime.
    /// </summary>
    public ModePermissionPrompt(PermissionMode mode, IPermissionPrompt? inner)
        : this(new PermissionModeState(mode), inner)
    {
    }

    /// <summary>
    /// Live overload: reads the current mode from the shared <paramref name="state"/> on every
    /// request, so a mid-run mode change is applied to the next permission decision.
    /// </summary>
    public ModePermissionPrompt(PermissionModeState state, IPermissionPrompt? inner)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.inner = inner;
    }

    /// <summary>The mode this prompt currently reads (the live value of its backing state).</summary>
    internal PermissionMode CurrentMode => this.state.Mode;

    public async Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PermissionPolicy.Decide(this.state.Mode, tool) switch
        {
            PermissionDecision.Allow => true,
            PermissionDecision.Deny => false,
            PermissionDecision.Ask when this.inner is not null =>
                await this.inner.RequestAsync(tool, inputPreview, cancellationToken).ConfigureAwait(false),
            _ => false,
        };
    }
}
