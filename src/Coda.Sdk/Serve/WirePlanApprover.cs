using Coda.Agent;
using Coda.JsonRpc;
using Coda.Sdk.Serve.Messages;

namespace Coda.Sdk.Serve;

/// <summary>
/// IPlanApprover implementation that forwards plan approval requests as JSON-RPC requests
/// over an IJsonRpcConnection. Returns false (reject) on any failure — never throws.
/// </summary>
public sealed class WirePlanApprover : IPlanApprover
{
    private readonly IJsonRpcConnection connection;

    public WirePlanApprover(IJsonRpcConnection connection)
    {
        this.connection = connection;
    }

    public async Task<bool> ApproveAsync(string plan, CancellationToken cancellationToken = default)
    {
        try
        {
            var node = await this.connection
                .SendRequestAsync(
                    ServeMethods.RequestPlanApproval,
                    ServeJson.ToNode(new PlanApprovalRequest(plan)),
                    cancellationToken)
                .ConfigureAwait(false);

            var resp = ServeJson.FromNode<PlanApprovalResponse>(node);
            return resp?.Approve ?? false;
        }
        catch
        {
            // On any failure (including OperationCanceledException), reject the plan.
            return false;
        }
    }
}
