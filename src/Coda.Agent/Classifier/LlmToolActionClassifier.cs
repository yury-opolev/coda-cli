using Coda.Agent.Watchers;
using LlmClient;

namespace Coda.Agent.Classifier;

/// <summary>
/// Classifies a tool action with a single isolated model call (an <see cref="IForkedAgent"/>),
/// then parses the reply fail-closed via <see cref="ToolActionClassifierPrompt.Parse"/>.
/// </summary>
public sealed class LlmToolActionClassifier : IToolActionClassifier
{
    private readonly IForkedAgent fork;

    public LlmToolActionClassifier(IForkedAgent fork)
    {
        this.fork = fork ?? throw new ArgumentNullException(nameof(fork));
    }

    public async Task<ToolActionVerdict> ClassifyAsync(string toolName, string inputJson, CancellationToken cancellationToken = default)
    {
        var userMessage = ToolActionClassifierPrompt.BuildUserMessage(toolName, inputJson);

        try
        {
            var response = await this.fork
                .RunAsync(ToolActionClassifierPrompt.SystemPrompt, [ChatMessage.UserText(userMessage)], cancellationToken)
                .ConfigureAwait(false);

            return ToolActionClassifierPrompt.Parse(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Classifier unavailable (API error etc.) → block for safety.
            return ToolActionVerdict.Ask($"Classifier unavailable ({ex.Message}) — blocking for safety.");
        }
    }
}
