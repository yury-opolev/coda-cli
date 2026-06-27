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
    private readonly PermissionMode mode;
    private readonly IPermissionPrompt? inner;

    public ModePermissionPrompt(PermissionMode mode, IPermissionPrompt? inner)
    {
        this.mode = mode;
        this.inner = inner;
    }

    public async Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PermissionPolicy.Decide(this.mode, tool) switch
        {
            PermissionDecision.Allow => true,
            PermissionDecision.Deny => false,
            PermissionDecision.Ask when this.inner is not null =>
                await this.inner.RequestAsync(tool, inputPreview, cancellationToken).ConfigureAwait(false),
            _ => false,
        };
    }
}
