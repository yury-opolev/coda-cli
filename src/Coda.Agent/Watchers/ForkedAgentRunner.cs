using System.Text;
using LlmClient;

namespace Coda.Agent.Watchers;

/// <summary>
/// Runs a single tool-less completion against the shared client and returns the
/// collected assistant text. No tools are advertised and no events are surfaced —
/// a fork is invisible to the user and cannot mutate anything itself.
/// </summary>
public sealed class ForkedAgentRunner : IForkedAgent
{
    private readonly ILlmClient client;
    private readonly string model;
    private readonly int maxTokens;

    public ForkedAgentRunner(ILlmClient client, string model, int maxTokens = 4096)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        this.maxTokens = maxTokens;
    }

    public async Task<string> RunAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(messages);

        var request = new ChatRequest
        {
            Model = this.model,
            MaxTokens = this.maxTokens,
            System = systemPrompt,
            Messages = messages,
            // No tools: a fork only thinks/summarizes; it never acts.
        };

        var text = new StringBuilder();
        await foreach (var streamEvent in this.client.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (streamEvent.Kind == AssistantEventKind.TextDelta)
            {
                text.Append(streamEvent.Text);
            }
        }

        return text.ToString().Trim();
    }
}
