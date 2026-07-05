namespace Coda.Sdk.Providers;

/// <summary>
/// Thrown when a headless runner is asked to start a session but no provider was configured —
/// neither a <c>--provider</c> flag nor a connected credential. (A resolved provider always has a
/// built-in model, so the model side never triggers this once a provider is known.)
/// </summary>
public sealed class ProviderModelNotConfiguredException : InvalidOperationException
{
    public ProviderModelNotConfiguredException(string message)
        : base(message)
    {
    }
}
