using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coda.Agent.Teams;

public static class TeamMessages
{
    public const string TeammateMessageTag = "teammate_message";

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HashSet<string> protocolTypes =
    [
        "idle_notification",
        "shutdown_request",
        "shutdown_approved",
        "shutdown_rejected",
        "plan_approval_request",
        "plan_approval_response",
        "permission_request",
        "permission_response",
        "mode_set_request",
        "team_permission_update",
    ];

    // ── Build factories ───────────────────────────────────────────────────────

    public static string BuildIdleNotification(
        string from,
        string? idleReason = null,
        string? summary = null,
        string? completedTaskId = null,
        string? completedStatus = null,
        string? failureReason = null)
    {
        var msg = new IdleNotification(
            Type: "idle_notification",
            From: from,
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            IdleReason: idleReason,
            Summary: summary,
            CompletedTaskId: completedTaskId,
            CompletedStatus: completedStatus,
            FailureReason: failureReason);

        return JsonSerializer.Serialize(msg, jsonOptions);
    }

    public static string BuildShutdownRequest(string requestId, string from, string? reason = null)
    {
        var msg = new ShutdownRequest(
            Type: "shutdown_request",
            RequestId: requestId,
            From: from,
            Reason: reason,
            Timestamp: DateTimeOffset.UtcNow.ToString("O"));

        return JsonSerializer.Serialize(msg, jsonOptions);
    }

    public static string BuildShutdownApproved(string requestId, string from)
    {
        var msg = new ShutdownApproved(
            Type: "shutdown_approved",
            RequestId: requestId,
            From: from,
            Timestamp: DateTimeOffset.UtcNow.ToString("O"));

        return JsonSerializer.Serialize(msg, jsonOptions);
    }

    public static string BuildShutdownRejected(string requestId, string from, string reason)
    {
        var msg = new ShutdownRejected(
            Type: "shutdown_rejected",
            RequestId: requestId,
            From: from,
            Reason: reason,
            Timestamp: DateTimeOffset.UtcNow.ToString("O"));

        return JsonSerializer.Serialize(msg, jsonOptions);
    }

    public static string BuildPlanApprovalRequest(
        string from,
        string planFilePath,
        string planContent,
        string requestId)
    {
        var msg = new PlanApprovalRequest(
            Type: "plan_approval_request",
            From: from,
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            PlanFilePath: planFilePath,
            PlanContent: planContent,
            RequestId: requestId);

        return JsonSerializer.Serialize(msg, jsonOptions);
    }

    public static string BuildPlanApprovalResponse(string requestId, bool approved, string? feedback = null)
    {
        var msg = new PlanApprovalResponse(
            Type: "plan_approval_response",
            RequestId: requestId,
            Approved: approved,
            Feedback: feedback,
            Timestamp: DateTimeOffset.UtcNow.ToString("O"));

        return JsonSerializer.Serialize(msg, jsonOptions);
    }

    // ── TryParse helpers ──────────────────────────────────────────────────────

    public static IdleNotification? TryParseIdleNotification(string text) =>
        TryParse<IdleNotification>(text, "idle_notification");

    public static ShutdownRequest? TryParseShutdownRequest(string text) =>
        TryParse<ShutdownRequest>(text, "shutdown_request");

    public static ShutdownApproved? TryParseShutdownApproved(string text) =>
        TryParse<ShutdownApproved>(text, "shutdown_approved");

    public static ShutdownRejected? TryParseShutdownRejected(string text) =>
        TryParse<ShutdownRejected>(text, "shutdown_rejected");

    public static PlanApprovalRequest? TryParsePlanApprovalRequest(string text) =>
        TryParse<PlanApprovalRequest>(text, "plan_approval_request");

    public static PlanApprovalResponse? TryParsePlanApprovalResponse(string text) =>
        TryParse<PlanApprovalResponse>(text, "plan_approval_response");

    // ── IsStructuredProtocolMessage ───────────────────────────────────────────

    public static bool IsStructuredProtocolMessage(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            if (node is not JsonObject obj)
            {
                return false;
            }

            var typeNode = obj["type"];
            if (typeNode is null)
            {
                return false;
            }

            var type = typeNode.GetValue<string>();
            return protocolTypes.Contains(type);
        }
        catch
        {
            return false;
        }
    }

    // ── FormatTeammateMessage ─────────────────────────────────────────────────

    public static string FormatTeammateMessage(
        string from,
        string text,
        string? color = null,
        string? summary = null)
    {
        var sb = new StringBuilder();
        sb.Append('<');
        sb.Append(TeammateMessageTag);
        sb.Append(" teammate_id=\"");
        sb.Append(EscapeXmlAttribute(from));
        sb.Append('"');

        if (color is not null)
        {
            sb.Append(" color=\"");
            sb.Append(EscapeXmlAttribute(color));
            sb.Append('"');
        }

        if (summary is not null)
        {
            sb.Append(" summary=\"");
            sb.Append(EscapeXmlAttribute(summary));
            sb.Append('"');
        }

        sb.Append(">\n");
        sb.Append(text);
        sb.Append("\n</");
        sb.Append(TeammateMessageTag);
        sb.Append('>');

        return sb.ToString();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static T? TryParse<T>(string text, string expectedType) where T : class
    {
        try
        {
            var node = JsonNode.Parse(text);
            if (node is not JsonObject obj)
            {
                return null;
            }

            var typeNode = obj["type"];
            if (typeNode is null)
            {
                return null;
            }

            var type = typeNode.GetValue<string>();
            if (type != expectedType)
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(text, jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string EscapeXmlAttribute(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
