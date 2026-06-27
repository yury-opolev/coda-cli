using System.Text.Json;
using Coda.Agent.Teams;

namespace Coda.Agent.Tools;

/// <summary>
/// Sends a message to a teammate, broadcasts to the whole team, or sends structured
/// protocol messages (shutdown_request, shutdown_response, plan_approval_response).
///
/// IsReadOnly note: Coda's ITool.IsReadOnly is a parameterless bool and cannot vary
/// per invocation. Because every execution writes to at least one inbox, this tool
/// conservatively returns false. The TypeScript reference's isReadOnly(input) returned
/// true for plain-text messages — that per-input distinction is not representable here.
/// </summary>
public sealed class SendMessageTool : ITool
{
    public string Name => "send_message";

    public string Description =>
        "Send a message to a teammate or broadcast to the whole team. " +
        "Supports plain text DMs (with a required summary), broadcasts (to=\"*\"), " +
        "and structured protocol messages (shutdown_request, shutdown_response, plan_approval_response).";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "to": {
              "type": "string",
              "description": "Recipient: teammate name or \"*\" for broadcast to all teammates."
            },
            "summary": {
              "type": "string",
              "description": "A 5-10 word summary shown as a preview (required when message is a string)."
            },
            "message": {
              "oneOf": [
                { "type": "string", "description": "Plain text message content." },
                {
                  "type": "object",
                  "description": "Structured protocol message. Must include a \"type\" property.",
                  "properties": {
                    "type": { "type": "string" }
                  },
                  "required": ["type"]
                }
              ]
            }
          },
          "required": ["to", "message"]
        }
        """;

    /// <summary>
    /// Conservatively false — every execution writes to a mailbox.
    /// See class-level documentation for the deviation from the TypeScript reference.
    /// </summary>
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.TeamMailbox is null || context.TeamStore is null || context.TeamName is null)
        {
            return new ToolResult("Not in a team context.", IsError: true);
        }

        // --- Parse "to" ---
        if (!input.TryGetProperty("to", out var toEl) || toEl.ValueKind != JsonValueKind.String)
        {
            return new ToolResult("to is required and must be a string.", IsError: true);
        }

        var to = toEl.GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(to))
        {
            return new ToolResult("to must not be empty.", IsError: true);
        }

        if (to.Contains('@'))
        {
            return new ToolResult(
                "to must be a bare teammate name or \"*\" — there is only one team per session.",
                IsError: true);
        }

        // --- Parse "message" ---
        if (!input.TryGetProperty("message", out var messageEl))
        {
            return new ToolResult("message is required.", IsError: true);
        }

        var sender = context.AgentName ?? TeamConstants.TeamLeadName;
        var senderColor = this.GetSenderColor(context.TeamStore, context.TeamName, sender);

        if (messageEl.ValueKind == JsonValueKind.String)
        {
            return await this.HandleTextMessageAsync(
                input, to, messageEl.GetString()!, sender, senderColor, context, cancellationToken)
                .ConfigureAwait(false);
        }

        if (messageEl.ValueKind == JsonValueKind.Object)
        {
            return await this.HandleStructuredMessageAsync(
                to, messageEl, sender, senderColor, context, cancellationToken)
                .ConfigureAwait(false);
        }

        return new ToolResult("message must be a string or a structured object.", IsError: true);
    }

    // ── Plain-text handling ────────────────────────────────────────────────────

    private async Task<ToolResult> HandleTextMessageAsync(
        JsonElement input,
        string to,
        string text,
        string sender,
        string? senderColor,
        ToolContext context,
        CancellationToken ct)
    {
        // summary is required for plain-text messages
        string? summary = null;
        if (input.TryGetProperty("summary", out var summaryEl) && summaryEl.ValueKind == JsonValueKind.String)
        {
            summary = summaryEl.GetString();
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            return new ToolResult("summary is required when message is a string.", IsError: true);
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("O");

        if (to == "*")
        {
            return await this.HandleBroadcastAsync(
                text, summary, sender, senderColor, timestamp, context, ct)
                .ConfigureAwait(false);
        }

        var msg = new TeammateMessage(sender, text, timestamp, false, senderColor, summary);
        await context.TeamMailbox!.WriteAsync(to, context.TeamName!, msg, ct).ConfigureAwait(false);

        return new ToolResult($"Message sent to {to}'s inbox");
    }

    private async Task<ToolResult> HandleBroadcastAsync(
        string text,
        string summary,
        string sender,
        string? senderColor,
        string timestamp,
        ToolContext context,
        CancellationToken ct)
    {
        var teamFile = context.TeamStore!.Read(context.TeamName!);
        if (teamFile is null)
        {
            return new ToolResult($"Team \"{context.TeamName}\" does not exist.", IsError: true);
        }

        var recipients = teamFile.Members
            .Where(m => !string.Equals(m.Name, sender, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Name)
            .ToList();

        if (recipients.Count == 0)
        {
            return new ToolResult("No teammates to broadcast to (you are the only team member).");
        }

        foreach (var recipientName in recipients)
        {
            var msg = new TeammateMessage(sender, text, timestamp, false, senderColor, summary);
            await context.TeamMailbox!.WriteAsync(recipientName, context.TeamName!, msg, ct).ConfigureAwait(false);
        }

        return new ToolResult(
            $"Message broadcast to {recipients.Count} teammate(s): {string.Join(", ", recipients)}");
    }

    // ── Structured message handling ────────────────────────────────────────────

    private async Task<ToolResult> HandleStructuredMessageAsync(
        string to,
        JsonElement messageEl,
        string sender,
        string? senderColor,
        ToolContext context,
        CancellationToken ct)
    {
        if (to == "*")
        {
            return new ToolResult("structured messages cannot be broadcast (to: \"*\").", IsError: true);
        }

        if (!messageEl.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
        {
            return new ToolResult("Structured message must have a \"type\" string property.", IsError: true);
        }

        var type = typeEl.GetString()!;
        var timestamp = DateTimeOffset.UtcNow.ToString("O");

        switch (type)
        {
            case "shutdown_request":
                return await this.HandleShutdownRequestAsync(
                    to, messageEl, sender, senderColor, timestamp, context, ct)
                    .ConfigureAwait(false);

            case "shutdown_response":
                return await this.HandleShutdownResponseAsync(
                    to, messageEl, sender, senderColor, timestamp, context, ct)
                    .ConfigureAwait(false);

            case "plan_approval_response":
                return await this.HandlePlanApprovalResponseAsync(
                    to, messageEl, sender, senderColor, timestamp, context, ct)
                    .ConfigureAwait(false);

            default:
                return new ToolResult($"Unknown structured message type: {type}.", IsError: true);
        }
    }

    private async Task<ToolResult> HandleShutdownRequestAsync(
        string to,
        JsonElement messageEl,
        string sender,
        string? senderColor,
        string timestamp,
        ToolContext context,
        CancellationToken ct)
    {
        string? reason = null;
        if (messageEl.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String)
        {
            reason = reasonEl.GetString();
        }

        var requestId = $"shutdown-{to}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var structuredText = TeamMessages.BuildShutdownRequest(requestId, sender, reason);
        var msg = new TeammateMessage(sender, structuredText, timestamp, false, senderColor, null);

        await context.TeamMailbox!.WriteAsync(to, context.TeamName!, msg, ct).ConfigureAwait(false);

        return new ToolResult($"Shutdown request sent to {to}. Request ID: {requestId}");
    }

    private async Task<ToolResult> HandleShutdownResponseAsync(
        string to,
        JsonElement messageEl,
        string sender,
        string? senderColor,
        string timestamp,
        ToolContext context,
        CancellationToken ct)
    {
        if (!string.Equals(to, TeamConstants.TeamLeadName, StringComparison.OrdinalIgnoreCase))
        {
            return new ToolResult(
                $"shutdown_response must be sent to \"{TeamConstants.TeamLeadName}\".",
                IsError: true);
        }

        if (!messageEl.TryGetProperty("request_id", out var requestIdEl) ||
            requestIdEl.ValueKind != JsonValueKind.String)
        {
            return new ToolResult("shutdown_response requires a request_id.", IsError: true);
        }

        var requestId = requestIdEl.GetString()!;

        var approve = false;
        if (messageEl.TryGetProperty("approve", out var approveEl))
        {
            if (approveEl.ValueKind == JsonValueKind.True)
            {
                approve = true;
            }
            else if (approveEl.ValueKind == JsonValueKind.False)
            {
                approve = false;
            }
            else if (approveEl.ValueKind == JsonValueKind.String)
            {
                approve = string.Equals(approveEl.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!approve)
        {
            string? reason = null;
            if (messageEl.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String)
            {
                reason = reasonEl.GetString();
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                return new ToolResult("reason is required when rejecting a shutdown request.", IsError: true);
            }

            var rejectedText = TeamMessages.BuildShutdownRejected(requestId, sender, reason);
            var rejectedMsg = new TeammateMessage(sender, rejectedText, timestamp, false, senderColor, null);
            await context.TeamMailbox!.WriteAsync(TeamConstants.TeamLeadName, context.TeamName!, rejectedMsg, ct).ConfigureAwait(false);

            return new ToolResult($"Shutdown rejected. Reason: \"{reason}\". Continuing to work.");
        }

        var approvedText = TeamMessages.BuildShutdownApproved(requestId, sender);
        var approvedMsg = new TeammateMessage(sender, approvedText, timestamp, false, senderColor, null);
        await context.TeamMailbox!.WriteAsync(TeamConstants.TeamLeadName, context.TeamName!, approvedMsg, ct).ConfigureAwait(false);

        // FIX 3: also signal the runner's in-process flag so the TeammateRunner exits its
        // poll loop promptly, without waiting for the leader to drain its inbox and call Kill.
        // Guard: Teams and TeamName may be null in non-team contexts (no-op if so).
        if (context.Teams is not null && context.TeamName is not null)
        {
            var sanitizedName = AgentId.SanitizeAgentName(context.AgentName ?? TeamConstants.TeamLeadName);
            var selfAgentId = AgentId.Format(sanitizedName, context.TeamName);
            context.Teams.SignalShutdownApproved(selfAgentId);
        }

        return new ToolResult($"Shutdown approved. Sent confirmation to team-lead. Agent {sender} is now exiting.");
    }

    private async Task<ToolResult> HandlePlanApprovalResponseAsync(
        string to,
        JsonElement messageEl,
        string sender,
        string? senderColor,
        string timestamp,
        ToolContext context,
        CancellationToken ct)
    {
        if (!messageEl.TryGetProperty("request_id", out var requestIdEl) ||
            requestIdEl.ValueKind != JsonValueKind.String)
        {
            return new ToolResult("plan_approval_response requires a request_id.", IsError: true);
        }

        var requestId = requestIdEl.GetString()!;

        var approve = false;
        if (messageEl.TryGetProperty("approve", out var approveEl))
        {
            if (approveEl.ValueKind == JsonValueKind.True)
            {
                approve = true;
            }
            else if (approveEl.ValueKind == JsonValueKind.String)
            {
                approve = string.Equals(approveEl.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        string? feedback = null;
        if (messageEl.TryGetProperty("feedback", out var feedbackEl) && feedbackEl.ValueKind == JsonValueKind.String)
        {
            feedback = feedbackEl.GetString();
        }

        var responseText = TeamMessages.BuildPlanApprovalResponse(requestId, approve, feedback);
        var msg = new TeammateMessage(sender, responseText, timestamp, false, senderColor, null);
        await context.TeamMailbox!.WriteAsync(to, context.TeamName!, msg, ct).ConfigureAwait(false);

        return approve
            ? new ToolResult($"Plan approved for {to}.")
            : new ToolResult($"Plan rejected for {to}.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string? GetSenderColor(TeamStore store, string teamName, string senderName)
    {
        var teamFile = store.Read(teamName);
        if (teamFile is null)
        {
            return null;
        }

        var member = teamFile.Members.FirstOrDefault(
            m => string.Equals(m.Name, senderName, StringComparison.OrdinalIgnoreCase));

        return member?.Color;
    }
}
