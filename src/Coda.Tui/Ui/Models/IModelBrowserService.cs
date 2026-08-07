using Coda.Sdk;

namespace Coda.Tui.Ui.Models;

/// <summary>
/// Host-neutral surface for the interactive model picker. In Terminal.Gui mode this is implemented
/// by the shell, which shows <see cref="ModelBrowserOverlay"/> and returns the user's choice; in all
/// other modes (plain, Spectre, tests) the property on <see cref="Coda.Tui.Repl.CommandContext"/> is
/// null and the callers fall back to the generic prompt overlay.
/// </summary>
public interface IModelBrowserService
{
    /// <summary>
    /// Shows the model browser populated from <paramref name="result"/> with
    /// <paramref name="currentModelId"/> marked. Returns the selection (model id and effort), or
    /// <c>null</c> when the user dismisses without selecting.
    /// </summary>
    /// <param name="initialEffortByModel">
    /// Optional pre-seeded per-model efforts (e.g. from the session) so existing choices appear in the
    /// browser without the user needing to re-pick them. Keys are <c>"{providerId}/{modelId}"</c> or
    /// just <c>modelId</c> — whatever the caller uses.
    /// </param>
    Task<ModelSelection?> SelectModelAsync(
        ModelListResult result,
        string? currentModelId,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? initialEffortByModel = null);
}
