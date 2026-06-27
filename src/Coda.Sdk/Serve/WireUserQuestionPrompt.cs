using Coda.Agent;
using Coda.JsonRpc;
using Coda.Sdk.Serve.Messages;

namespace Coda.Sdk.Serve;

/// <summary>
/// IUserQuestionPrompt implementation that forwards questions as JSON-RPC requests
/// over an IJsonRpcConnection. On failure or cancellation returns the first option if any,
/// otherwise string.Empty — a safe, non-blocking default for headless/disconnected scenarios.
/// </summary>
public sealed class WireUserQuestionPrompt : IUserQuestionPrompt
{
    private readonly IJsonRpcConnection connection;

    public WireUserQuestionPrompt(IJsonRpcConnection connection)
    {
        this.connection = connection;
    }

    public async Task<string> AskAsync(
        string question,
        IReadOnlyList<string> options,
        bool multiSelect,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var node = await this.connection
                .SendRequestAsync(
                    ServeMethods.RequestQuestion,
                    ServeJson.ToNode(new QuestionRequest(question, options, multiSelect)),
                    cancellationToken)
                .ConfigureAwait(false);

            var resp = ServeJson.FromNode<QuestionResponse>(node);
            return resp?.Answer ?? (options.Count > 0 ? options[0] : string.Empty);
        }
        catch
        {
            // On failure or cancellation, return the first option (safe default) or empty string.
            return options.Count > 0 ? options[0] : string.Empty;
        }
    }
}
