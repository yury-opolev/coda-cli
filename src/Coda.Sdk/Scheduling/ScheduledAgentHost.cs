using Coda.Agent;
using Coda.Agent.Scheduling;
using Coda.Agent.Settings;
using Coda.Sdk.Turns;
using LlmAuth;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Coda.Sdk.Scheduling;

/// <summary>
/// Concrete <see cref="IScheduledAgentHost"/>: runs a scheduled root as an ISOLATED conversation.
/// Each firing snapshots the CURRENT session options, builds a per-execution provider client, and
/// assembles the scheduled <see cref="AgentLoopSpec"/> via <see cref="TurnPipelineBuilder.BuildScheduledSpec"/>
/// so the run reuses the live provider/model/effort/output style, the shared
/// <see cref="Coda.Agent.PermissionModeState"/>/rules, the shared task manager, LSP, user hooks, and
/// prompt services — while never mutating the session's main history or calling
/// <c>CodaSession.RunAsync</c>.
/// </summary>
/// <remarks>
/// The live options source (<c>currentOptions</c>) is evaluated on every firing, not once at
/// construction, so a mid-session provider/model/effort/output-style/tool/permission change is
/// observed by the next scheduled run. Cancellation and exceptions propagate unchanged after tool
/// activity is finalized, so the task manager records Stopped/Failed authoritatively.
/// </remarks>
public sealed class ScheduledAgentHost : IScheduledAgentHost
{
    private readonly Func<SessionOptions> currentOptions;
    private readonly ILlmClientFactory clientFactory;
    private readonly IAgentLoopFactory loopFactory;
    private readonly CredentialManager credentials;
    private readonly ClientFingerprint fingerprint;
    private readonly HttpClient http;
    private readonly ILoggerFactory loggerFactory;
    private readonly TurnPipelineBuilder pipeline;
    private readonly IStreamProgressSink? streamProgressSink;

    /// <summary>Creates the host with the session's stable scheduled-execution collaborators.</summary>
    /// <param name="currentOptions">Live accessor for the session options; evaluated per firing.</param>
    /// <param name="clientFactory">Builds the per-execution provider client.</param>
    /// <param name="loopFactory">Builds the agent loop from the scheduled spec.</param>
    /// <param name="credentials">Credential store used to authenticate the client.</param>
    /// <param name="fingerprint">Stable client fingerprint sent with provider requests.</param>
    /// <param name="http">Shared HTTP client; per-execution clients do not own it.</param>
    /// <param name="loggerFactory">Factory for the loop's tool/turn loggers.</param>
    /// <param name="pipeline">Builder that assembles the scheduled loop spec.</param>
    /// <param name="streamProgressSink">Optional LLM-stream liveness sink; null disables.</param>
    public ScheduledAgentHost(
        Func<SessionOptions> currentOptions,
        ILlmClientFactory clientFactory,
        IAgentLoopFactory loopFactory,
        CredentialManager credentials,
        ClientFingerprint fingerprint,
        HttpClient http,
        ILoggerFactory loggerFactory,
        TurnPipelineBuilder pipeline,
        IStreamProgressSink? streamProgressSink = null)
    {
        this.currentOptions = currentOptions ?? throw new ArgumentNullException(nameof(currentOptions));
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.loopFactory = loopFactory ?? throw new ArgumentNullException(nameof(loopFactory));
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        this.pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        this.streamProgressSink = streamProgressSink;
    }

    /// <inheritdoc />
    public async Task<string> RunScheduledAsync(
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(steering);
        ArgumentException.ThrowIfNullOrEmpty(taskId);

        var rootToolActivity = ToolActivityContext.CreateRoot();
        var recording = new RecordingSink(sink);
        var toolActivityFinalized = false;

        void CompleteToolActivity(bool interrupted)
        {
            if (toolActivityFinalized)
            {
                return;
            }

            toolActivityFinalized = true;
            recording.CompleteActivity(interrupted);
        }

        ILlmClient? client = null;
        try
        {
            // Snapshot the live options AT ENTRY so a mid-firing mutation cannot tear the run.
            var options = this.currentOptions();

            client = this.clientFactory.Create(
                options.ProviderId,
                this.credentials,
                this.fingerprint,
                this.http,
                this.loggerFactory,
                options.LlmHttpTimeoutOverride,
                this.streamProgressSink);
            if (client is null)
            {
                throw new InvalidOperationException($"No chat client for provider '{options.ProviderId}'.");
            }

            var settings = SettingsLoader.Load(options.WorkingDirectory);

            var spec = this.pipeline.BuildScheduledSpec(options, client, settings, taskId, depth) with
            {
                // Apply the task's steering inbox so task_send can redirect the scheduled run.
                Steering = steering,
                ToolActivity = rootToolActivity,
            };

            var loop = this.loopFactory.Create(spec);

            // The scheduled run is ISOLATED: its history is exactly the firing's prompt. The session's
            // main history is never referenced or mutated here.
            var history = new List<ChatMessage> { ChatMessage.UserText(prompt) };

            await loop.RunAsync(history, recording, cancellationToken).ConfigureAwait(false);

            CompleteToolActivity(interrupted: false);
            var text = recording.FinalText;
            return string.IsNullOrWhiteSpace(text) ? "(scheduled task completed)" : text;
        }
        catch
        {
            CompleteToolActivity(interrupted: true);
            throw;
        }
        finally
        {
            // Dispose the per-execution client on success, failure, OR cancellation.
            if (client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
