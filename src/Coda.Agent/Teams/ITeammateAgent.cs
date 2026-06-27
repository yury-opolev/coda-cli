namespace Coda.Agent.Teams;

public interface ITeammateAgent
{
    /// <summary>
    /// Runs one "turn" on the given prompt and returns the assistant's text result.
    /// </summary>
    Task<string> RunTurnAsync(string prompt, CancellationToken cancellationToken);
}
