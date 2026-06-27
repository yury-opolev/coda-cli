namespace Coda.Agent;

/// <summary>
/// An observe-only hook run after each assistant turn ("post sampling"). It reads
/// the conversation in the background and must not block or mutate the main loop.
/// </summary>
public interface IPostSamplingHook
{
    Task RunAsync(ReplHookContext context, CancellationToken cancellationToken = default);
}
