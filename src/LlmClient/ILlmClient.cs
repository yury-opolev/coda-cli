namespace LlmClient;

/// <summary>A streaming chat client for one provider.</summary>
public interface ILlmClient
{
    string ProviderId { get; }

    /// <summary>Stream an assistant turn for the given request.</summary>
    IAsyncEnumerable<AssistantStreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count the input tokens a request would consume, if the provider supports
    /// it. Returns <c>null</c> when unsupported (e.g. Copilot) so callers fall
    /// back to a local estimate. Default: unsupported.
    /// </summary>
    Task<int?> CountTokensAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(null);

    /// <summary>
    /// List the models the provider/subscription actually grants, if it exposes a
    /// model-listing endpoint. Best-effort: returns an empty list when unsupported
    /// or on any failure (network/auth/parse), so callers fall back to a built-in
    /// list. Default: unsupported (empty).
    /// </summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModelInfo>>([]);
}
