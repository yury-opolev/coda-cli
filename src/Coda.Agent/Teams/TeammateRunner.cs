namespace Coda.Agent.Teams;

/// <summary>
/// In-process run loop for a single teammate: drives its prompt/shutdown wait loop.
///
/// CANCELLATION CONTRACT: every wait inside this class must be bounded by the lifecycle
/// CancellationToken. No unbounded awaits exist.
/// </summary>
public sealed class TeammateRunner
{
    private const int PollMs = 500;

    private readonly TeammateIdentity identity;
    private readonly ITeammateAgent agent;
    private readonly Mailbox mailbox;
    private readonly TeamStore store;
    private readonly TaskBoard board;

    // Set by SignalShutdownApproved() (called from the send_message approve path, Task 9,
    // or by the fake in tests). Volatile ensures the loop sees the write promptly.
    private volatile bool shutdownApproved;

    public TeammateRunner(
        TeammateIdentity identity,
        ITeammateAgent agent,
        Mailbox mailbox,
        TeamStore store,
        TaskBoard board)
    {
        this.identity = identity;
        this.agent = agent;
        this.mailbox = mailbox;
        this.store = store;
        this.board = board;
    }

    /// <summary>
    /// Signal (from the send_message approve path in Task 9) that this teammate's
    /// shutdown was approved by the model. The run loop will exit on the next check.
    /// </summary>
    public void SignalShutdownApproved()
    {
        this.shutdownApproved = true;
    }

    /// <summary>
    /// Runs the teammate's main loop until the lifecycle token is cancelled or a
    /// shutdown is approved.
    /// </summary>
    public async Task RunAsync(string initialPrompt, CancellationToken lifecycle)
    {
        var currentPrompt = TeamMessages.FormatTeammateMessage(TeamConstants.TeamLeadName, initialPrompt);

        while (!lifecycle.IsCancellationRequested && !this.shutdownApproved)
        {
            string result;
            try
            {
                result = await this.agent.RunTurnAsync(currentPrompt, lifecycle).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Teammate failed — notify the leader and exit.
                var nowIsoOnError = DateTimeOffset.UtcNow.ToString("O");
                var failText = TeamMessages.BuildIdleNotification(
                    this.identity.AgentName,
                    idleReason: "failed",
                    failureReason: ex.Message);
                var failMsg = new TeammateMessage(
                    this.identity.AgentName,
                    failText,
                    nowIsoOnError,
                    false,
                    this.identity.Color,
                    null);
                try
                {
                    await this.mailbox.WriteAsync(TeamConstants.TeamLeadName, this.identity.TeamName, failMsg, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // best-effort; ignore secondary failure
                }

                break;
            }

            _ = result; // result may be used by callers in Task 9; suppress unused warning

            // Mark member idle in the team store (sync call).
            this.store.SetMemberActive(this.identity.TeamName, this.identity.AgentName, false);

            // Send idle_notification to the team lead.
            var nowIso = DateTimeOffset.UtcNow.ToString("O");
            var idleText = TeamMessages.BuildIdleNotification(this.identity.AgentName, idleReason: "available");
            var idleMessage = new TeammateMessage(
                this.identity.AgentName,
                idleText,
                nowIso,
                false,
                this.identity.Color,
                null);

            try
            {
                await this.mailbox.WriteAsync(TeamConstants.TeamLeadName, this.identity.TeamName, idleMessage, lifecycle)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // If shutdown was approved during the turn, exit.
            if (this.shutdownApproved)
            {
                break;
            }

            // Wait for the next prompt, shutdown request, available task, or abort.
            WaitOutcome waitOutcome;
            try
            {
                waitOutcome = await this.WaitForNextAsync(lifecycle).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            switch (waitOutcome)
            {
                case WaitOutcome.ShutdownRequest(var text, var from):
                    // Feed the shutdown request to the model so it can decide to approve/reject.
                    currentPrompt = TeamMessages.FormatTeammateMessage(from, text);
                    break;

                case WaitOutcome.NewMessage(var from, var text, var color, var summary):
                    currentPrompt = from == "user"
                        ? text
                        : TeamMessages.FormatTeammateMessage(from, text, color, summary);
                    this.store.SetMemberActive(this.identity.TeamName, this.identity.AgentName, true);
                    break;

                case WaitOutcome.TaskClaimed(var prompt):
                    currentPrompt = prompt;
                    this.store.SetMemberActive(this.identity.TeamName, this.identity.AgentName, true);
                    break;

                case WaitOutcome.Aborted:
                    return;
            }
        }
    }

    // ── Private: poll loop ────────────────────────────────────────────────────

    /// <summary>
    /// Polls the teammate's mailbox every 500 ms, checking (in priority order):
    /// 1. Shutdown request (highest priority)
    /// 2. Unread message from team-lead
    /// 3. Any other unread message
    /// 4. An available board task to claim
    /// Exits when the lifecycle token is cancelled, shutdownApproved is set, or an
    /// outcome is found.
    /// </summary>
    private async Task<WaitOutcome> WaitForNextAsync(CancellationToken lifecycle)
    {
        while (!lifecycle.IsCancellationRequested && !this.shutdownApproved)
        {
            IReadOnlyList<TeammateMessage> messages;
            try
            {
                messages = await this.mailbox.ReadAsync(this.identity.AgentName, this.identity.TeamName, lifecycle)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new WaitOutcome.Aborted();
            }

            // Priority 1: scan for shutdown_request
            for (var i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m.Read)
                {
                    continue;
                }

                var parsed = TeamMessages.TryParseShutdownRequest(m.Text);
                if (parsed is not null)
                {
                    try
                    {
                        await this.mailbox.MarkReadByIndexAsync(this.identity.AgentName, this.identity.TeamName, i, lifecycle)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return new WaitOutcome.Aborted();
                    }

                    return new WaitOutcome.ShutdownRequest(m.Text, m.From);
                }
            }

            // Priority 2: first unread from team-lead, else first unread (any)
            var selectedIndex = -1;
            for (var i = 0; i < messages.Count; i++)
            {
                if (!messages[i].Read && messages[i].From == TeamConstants.TeamLeadName)
                {
                    selectedIndex = i;
                    break;
                }
            }

            if (selectedIndex == -1)
            {
                for (var i = 0; i < messages.Count; i++)
                {
                    if (!messages[i].Read)
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }

            if (selectedIndex != -1)
            {
                var msg = messages[selectedIndex];
                try
                {
                    await this.mailbox.MarkReadByIndexAsync(this.identity.AgentName, this.identity.TeamName, selectedIndex, lifecycle)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new WaitOutcome.Aborted();
                }

                return new WaitOutcome.NewMessage(msg.From, msg.Text, msg.Color, msg.Summary);
            }

            // Priority 3: try to claim an available board task
            var taskPrompt = await this.TryClaimNextTaskAsync(lifecycle).ConfigureAwait(false);
            if (taskPrompt is not null)
            {
                return new WaitOutcome.TaskClaimed(taskPrompt);
            }

            // Nothing found — sleep before next poll.
            try
            {
                await Task.Delay(PollMs, lifecycle).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new WaitOutcome.Aborted();
            }
        }

        return new WaitOutcome.Aborted();
    }

    // ── Private: task claiming helpers ────────────────────────────────────────

    private async Task<string?> TryClaimNextTaskAsync(CancellationToken ct)
    {
        try
        {
            var tasks = await this.board.ListAsync(this.identity.TeamName, ct).ConfigureAwait(false);
            var available = TaskBoard.FindAvailable(tasks);
            if (available is null)
            {
                return null;
            }

            var (ok, _) = await this.board.ClaimAsync(this.identity.TeamName, available.Id, this.identity.AgentName, ct)
                .ConfigureAwait(false);
            if (!ok)
            {
                return null;
            }

            await this.board.UpdateAsync(
                this.identity.TeamName,
                available.Id,
                new TeamTaskPatch { Status = TeamTaskStatus.InProgress },
                ct).ConfigureAwait(false);

            var prompt = $"Complete task #{available.Id}: {available.Subject}";
            if (available.Description is not null)
            {
                prompt += $"\n\n{available.Description}";
            }

            return prompt;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}
