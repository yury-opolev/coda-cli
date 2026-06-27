namespace Coda.Sdk.Providers;

/// <summary>
/// Thrown when a headless runner is asked to start a session but neither the caller
/// (a <c>--provider</c>/<c>--model</c> flag) nor the user (settings.json
/// <c>defaultProvider</c>/<c>defaultModel</c>) configured the provider or model.
/// There is no built-in default: the provider/model MUST be configured explicitly.
/// </summary>
public sealed class ProviderModelNotConfiguredException : InvalidOperationException
{
    public ProviderModelNotConfiguredException(string message)
        : base(message)
    {
    }
}
