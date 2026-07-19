namespace Coda.Agent.Classifier;

/// <summary>
/// A mode-aware permission prompt that dispatches per request based on the live
/// <see cref="PermissionModeState"/>: when the current mode is
/// <see cref="PermissionMode.BypassPermissions"/> it routes every mutating action through the
/// safety <see cref="ClassifierPermissionPrompt"/> (escalating risky ones); for any other mode it
/// applies the standard <see cref="ModePermissionPrompt"/> (mode policy + inner prompt).
/// </summary>
/// <remarks>
/// The mode is read from the shared state on EVERY request (never captured once at construction),
/// so a mid-run switch is honoured immediately: a live <c>Default → Bypass</c> starts consulting
/// the classifier, and a live <c>Bypass → Default</c> stops using it and falls back to asking. When
/// wrapped over a fixed state (a headless run with no shared state) the behaviour is identical to
/// the former build-time choice between the classifier and mode prompts.
/// </remarks>
public sealed class LiveBypassClassifierPermissionPrompt : IPermissionPrompt
{
    private readonly PermissionModeState state;
    private readonly IPermissionPrompt bypassPrompt;
    private readonly IPermissionPrompt modePrompt;

    public LiveBypassClassifierPermissionPrompt(
        PermissionModeState state,
        IToolActionClassifier classifier,
        IPermissionPrompt? inner)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentNullException.ThrowIfNull(classifier);
        this.bypassPrompt = new ClassifierPermissionPrompt(classifier, inner);
        this.modePrompt = new ModePermissionPrompt(state, inner);
    }

    /// <summary>The mode this prompt currently reads (the live value of its backing state).</summary>
    internal PermissionMode CurrentMode => this.state.Mode;

    public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
    {
        // Read the mode live per request so a mid-run change routes the NEXT decision correctly.
        return this.state.Mode == PermissionMode.BypassPermissions
            ? this.bypassPrompt.RequestAsync(tool, inputPreview, cancellationToken)
            : this.modePrompt.RequestAsync(tool, inputPreview, cancellationToken);
    }
}
