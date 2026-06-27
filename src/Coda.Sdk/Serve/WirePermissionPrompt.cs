using Coda.Agent;
using Coda.JsonRpc;
using Coda.Sdk.Serve.Messages;

namespace Coda.Sdk.Serve;

/// <summary>
/// IPermissionPrompt implementation that forwards permission requests as JSON-RPC requests
/// over an IJsonRpcConnection. Returns false (deny) on any failure — never throws.
/// </summary>
public sealed class WirePermissionPrompt : IPermissionPrompt
{
    private readonly IJsonRpcConnection connection;

    public WirePermissionPrompt(IJsonRpcConnection connection)
    {
        this.connection = connection;
    }

    public async Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
    {
        try
        {
            var node = await this.connection
                .SendRequestAsync(
                    ServeMethods.RequestPermission,
                    ServeJson.ToNode(new PermissionRequest(tool.Name, inputPreview)),
                    cancellationToken)
                .ConfigureAwait(false);

            var resp = ServeJson.FromNode<PermissionResponse>(node);
            return resp?.Allow ?? false;
        }
        catch
        {
            // On any failure (including OperationCanceledException), deny the permission.
            return false;
        }
    }
}
