using System.Text.Json.Nodes;

using Coda.Client.Protocol;
using Coda.Client.Transport;

namespace Coda.Client;

/// <summary>The kind of a server-initiated request. <see cref="Unknown"/> is a
/// deliberate state: a future <c>request/*</c> method this build does not model
/// is still delivered, with its raw params intact, rather than dropped.</summary>
public enum ReverseRequestKind
{
    Permission,
    Question,
    PlanApproval,
    Unknown,
}

/// <summary>
/// A server-initiated request handed to an <see cref="IReverseRequestHandler"/>.
/// The engine's turn is blocked until this is answered, so a handler must answer
/// it — or let it be disposed, which declines. There is no method on this type
/// that grants a permission or approves a plan without the handler saying so
/// explicitly.
///
/// Answering is idempotent and kind-checked: calling
/// <see cref="AllowPermission"/> on a question, say, throws rather than sending a
/// shape the engine would reject.
/// </summary>
public sealed class ReverseRequest : IDisposable
{
    private readonly CodaInboundRequest inbound;

    /// <summary>Wraps a raw server-initiated request from the transport in the
    /// typed, kind-checked surface. Public so a consumer reading the low-level
    /// <see cref="CodaConnection.Inbound"/> directly can still use the safe
    /// answer helpers rather than hand-building a response.</summary>
    public ReverseRequest(CodaInboundRequest inbound)
    {
        this.inbound = inbound;
        this.Kind = inbound.Method switch
        {
            ServerMethods.Permission => ReverseRequestKind.Permission,
            ServerMethods.Question => ReverseRequestKind.Question,
            ServerMethods.PlanApproval => ReverseRequestKind.PlanApproval,
            _ => ReverseRequestKind.Unknown,
        };
    }

    /// <summary>The raw JSON-RPC method, e.g. <c>request/permission</c>. Preserved
    /// even for an unknown kind.</summary>
    public string Method => this.inbound.Method;

    public ReverseRequestKind Kind { get; }

    /// <summary>The raw params, so an unknown request type is still fully
    /// available to a handler that wants to inspect or forward it.</summary>
    public JsonNode? RawParams => this.inbound.Params;

    /// <summary>Whether this request has been answered (including declined).</summary>
    public bool IsAnswered => this.inbound.Responder.IsAnswered;

    /// <summary>The permission payload, or null when this is not a permission
    /// request.</summary>
    public PermissionRequest? Permission =>
        this.Kind == ReverseRequestKind.Permission ? Parse<PermissionRequest>() : null;

    /// <summary>The question payload, or null when this is not a question request.</summary>
    public QuestionRequest? Question =>
        this.Kind == ReverseRequestKind.Question ? Parse<QuestionRequest>() : null;

    /// <summary>The plan-approval payload, or null when this is not a plan-approval
    /// request.</summary>
    public PlanApprovalRequest? PlanApproval =>
        this.Kind == ReverseRequestKind.PlanApproval ? Parse<PlanApprovalRequest>() : null;

    /// <summary>Answers a permission request. Throws if this is not a permission
    /// request, so a wrong-kind reply cannot be sent by mistake.</summary>
    public void AllowPermission(bool allow)
    {
        Require(ReverseRequestKind.Permission);
        this.inbound.Responder.RespondSuccess(CodaJson.ToNode(new PermissionResponse { Allow = allow }));
    }

    /// <summary>Answers a question request with a concrete answer.</summary>
    public void AnswerQuestion(string answer)
    {
        Require(ReverseRequestKind.Question);
        this.inbound.Responder.RespondSuccess(CodaJson.ToNode(new QuestionResponse { Answer = answer }));
    }

    /// <summary>Answers a plan-approval request.</summary>
    public void ApprovePlan(bool approve)
    {
        Require(ReverseRequestKind.PlanApproval);
        this.inbound.Responder.RespondSuccess(CodaJson.ToNode(new PlanApprovalResponse { Approve = approve }));
    }

    /// <summary>Sends an arbitrary success payload — the escape hatch for an
    /// unknown reverse request this build has no typed reply for.</summary>
    public void RespondRaw(JsonNode? result) => this.inbound.Responder.RespondSuccess(result);

    /// <summary>Declines the request explicitly. The engine applies the request's
    /// fail-closed default (deny / no-answer / reject).</summary>
    public void Decline(string reason) => this.inbound.Responder.Decline(reason);

    /// <summary>Fail-closed cleanup: declines if still unanswered. The client wraps
    /// handler dispatch in <c>using</c>, so a handler that forgets to answer still
    /// cannot leave the engine blocked or grant anything.</summary>
    public void Dispose() => this.inbound.Responder.Dispose();

    private void Require(ReverseRequestKind expected)
    {
        if (this.Kind != expected)
        {
            throw new InvalidOperationException(
                $"cannot answer a {this.Kind} request ({this.Method}) as {expected}");
        }
    }

    private T Parse<T>() => CodaJson.FromNode<T>(this.inbound.Params)!;
}

/// <summary>The methods the engine may call on us. Each expects a reply.</summary>
public static class ServerMethods
{
    public const string Permission = "request/permission";
    public const string Question = "request/question";
    public const string PlanApproval = "request/planApproval";
}
