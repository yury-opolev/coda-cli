namespace Coda.Agent.Teams;

/// <summary>
/// Discriminated result returned by <see cref="TeammateRunner"/>'s internal wait loop.
/// All cases form a closed hierarchy defined in this file (one-file-per-file exception
/// for tightly coupled subtypes of the same abstraction).
/// </summary>
public abstract record WaitOutcome
{
    private WaitOutcome() { }

    /// <summary>A shutdown_request was received and should be fed to the model.</summary>
    public sealed record ShutdownRequest(string Text, string From) : WaitOutcome;

    /// <summary>A new message arrived from a teammate or the team lead.</summary>
    public sealed record NewMessage(string From, string Text, string? Color, string? Summary) : WaitOutcome;

    /// <summary>A board task was claimed; the prompt drives the next turn.</summary>
    public sealed record TaskClaimed(string Prompt) : WaitOutcome;

    /// <summary>The lifecycle token was cancelled or shutdown was approved — exit the loop.</summary>
    public sealed record Aborted() : WaitOutcome;
}
