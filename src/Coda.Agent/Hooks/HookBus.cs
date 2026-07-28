using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Agent.Hooks;

/// <summary>
/// Owns ordered hook execution and output merging for all user-configured lifecycle events.
/// </summary>
/// <remarks>
/// <para>
/// Hooks for a given event run sequentially in configuration order. Their outputs are merged
/// by the rules in §6 of the agent-hooks proposal: strictest decision wins, reasons are joined,
/// <c>Continue:false</c> short-circuits remaining hooks, and <c>systemMessage</c> /
/// <c>additionalContext</c> are concatenated.
/// </para>
/// <para>
/// Execution is delegated to the injected <see cref="IHookExecutor"/>; the bus is responsible
/// only for timeout management, exit-code interpretation, output parsing, cap/spill, and merging.
/// Inject a fake executor in unit tests to exercise merge logic without spawning processes.
/// </para>
/// </remarks>
public sealed partial class HookBus
{
    /// <summary>Maximum characters kept in memory per stdout/stderr stream.</summary>
    internal const int OutputCap = 10_000;

    private readonly IReadOnlyList<UserHook> hooks;
    private readonly IHookExecutor executor;
    private readonly HookContext? context;
    private readonly Func<DateTimeOffset>? clock;
    private readonly Func<string>? spillDirFactory;
    private readonly ILogger logger;
    private int spillCounter;

    /// <summary>
    /// Initialises the bus.
    /// </summary>
    /// <param name="hooks">All user-configured hooks for this session.</param>
    /// <param name="executor">
    /// Process executor. Defaults to <see cref="ShellHookExecutor"/> when <see langword="null"/>.
    /// </param>
    /// <param name="context">
    /// Session-level envelope values written into every hook payload.
    /// When <see langword="null"/> the envelope fields are omitted (backward-compatible with
    /// tests that do not supply a context).
    /// </param>
    /// <param name="clock">
    /// Clock used for the payload timestamp and spill-file names.
    /// Defaults to <see cref="DateTimeOffset.UtcNow"/>.
    /// </param>
    /// <param name="spillDirFactory">
    /// Factory returning the directory for output-overflow spill files.
    /// Defaults to <c>~/.coda/hook-output</c>. Inject a temp path in tests.
    /// </param>
    /// <param name="logger">Logger for warnings and override notifications.</param>
    public HookBus(
        IReadOnlyList<UserHook> hooks,
        IHookExecutor? executor = null,
        HookContext? context = null,
        Func<DateTimeOffset>? clock = null,
        Func<string>? spillDirFactory = null,
        ILogger? logger = null)
    {
        this.hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        this.executor = executor ?? new ShellHookExecutor();
        this.context = context;
        this.clock = clock;
        this.spillDirFactory = spillDirFactory;
        this.logger = logger ?? NullLogger.Instance;
        this.HasPreToolUse = hooks.Any(h => string.Equals(h.Event, "PreToolUse", StringComparison.OrdinalIgnoreCase));
        this.HasUserPromptSubmit = hooks.Any(h => string.Equals(h.Event, "UserPromptSubmit", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when at least one <c>PreToolUse</c> hook is configured.</summary>
    public bool HasPreToolUse { get; }

    /// <summary>True when at least one <c>UserPromptSubmit</c> hook is configured.</summary>
    public bool HasUserPromptSubmit { get; }

    // -------------------------------------------------------------------------
    // Public run-methods (mirror the old UserHookRunner public surface)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs all matching <c>PreToolUse</c> hooks in configuration order and returns the
    /// merged result. A hook exiting with code 1 (or any other non-zero code) blocks the
    /// tool call because <c>PreToolUse</c> defaults to fail-closed.
    /// </summary>
    /// <param name="toolName">The name of the tool about to be called.</param>
    /// <param name="inputJson">The tool's input as a JSON string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth for this invocation: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The task identifier for this invocation, or <see langword="null"/> for the main agent.</param>
    public async Task<UserHookResult> RunPreToolUseAsync(
        string toolName,
        string inputJson,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null)
    {
        var matching = this.GetMatchingHooks("PreToolUse", toolName);
        if (matching.Count == 0)
        {
            return UserHookResult.Allow;
        }

        var payload = this.BuildPrePayload(toolName, inputJson, depth, taskId);
        var outputs = await this.RunHooksAsync(matching, "PreToolUse", payload, ct).ConfigureAwait(false);
        return ToUserHookResult(MergeOutputs(outputs));
    }

    /// <summary>
    /// Runs all matching <c>PostToolUse</c> hooks. Exit codes and errors are ignored
    /// (fail-open default). The merged output is not acted on in Phase 0.
    /// </summary>
    /// <param name="toolName">The name of the tool that was called.</param>
    /// <param name="inputJson">The tool's input as a JSON string.</param>
    /// <param name="toolResultText">The tool result text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth for this invocation: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The task identifier for this invocation, or <see langword="null"/> for the main agent.</param>
    public async Task RunPostToolUseAsync(
        string toolName,
        string inputJson,
        string toolResultText,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null)
    {
        var matching = this.GetMatchingHooks("PostToolUse", toolName);
        if (matching.Count == 0)
        {
            return;
        }

        var payload = this.BuildPostPayload(toolName, inputJson, toolResultText, depth, taskId);
        try
        {
            await this.RunHooksAsync(matching, "PostToolUse", payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Individual hook errors are already swallowed in RunSingleHookAsync;
            // this outer catch guards against unexpected failures in the loop itself.
        }
    }

    /// <summary>
    /// Runs all matching <c>Stop</c> hooks. Exit codes and errors are ignored (fail-open default).
    /// The merged output is not acted on in Phase 0.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth for this invocation: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The task identifier for this invocation, or <see langword="null"/> for the main agent.</param>
    public async Task RunStopAsync(CancellationToken ct, int depth = 0, string? taskId = null)
    {
        var matching = this.GetMatchingHooks("Stop", toolName: null);
        if (matching.Count == 0)
        {
            return;
        }

        var payload = this.BuildStopPayload(depth, taskId);
        try
        {
            await this.RunHooksAsync(matching, "Stop", payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Same as PostToolUse.
        }
    }

    /// <summary>
    /// Runs all matching <c>UserPromptSubmit</c> hooks in configuration order and returns the
    /// merged result. A broken or timed-out hook <strong>blocks</strong> (fail-closed, FailOpen
    /// defaults to <see langword="false"/> for this event) because a policy gate that silently
    /// permits on error is no gate at all.
    /// </summary>
    /// <param name="prompt">Concatenated text of all text blocks in the user message.</param>
    /// <param name="attachments">Non-text content-block kinds present (e.g. <c>"image"</c>), or empty.</param>
    /// <param name="historyLength">Number of messages in history before this turn is appended.</param>
    /// <param name="model">The model identifier for this turn.</param>
    /// <param name="permissionMode">The permission mode string for this turn (e.g. <c>"default"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="depth">Agent nesting depth: 0 = main agent, 1–2 = subagent.</param>
    /// <param name="taskId">The running task id, or <see langword="null"/> for the main agent.</param>
    public async Task<UserPromptSubmitResult> RunUserPromptSubmitAsync(
        string prompt,
        IReadOnlyList<string> attachments,
        int historyLength,
        string model,
        string permissionMode,
        CancellationToken ct,
        int depth = 0,
        string? taskId = null)
    {
        var matching = this.GetMatchingHooks("UserPromptSubmit", toolName: null);
        if (matching.Count == 0)
        {
            return UserPromptSubmitResult.Allow;
        }

        var payload = this.BuildUserPromptSubmitPayload(prompt, attachments, historyLength, model, permissionMode, depth, taskId);
        var pairs = new List<(UserHook Hook, HookOutput Output)>(matching.Count);
        foreach (var hook in matching)
        {
            var output = await this.RunSingleHookAsync(hook, "UserPromptSubmit", payload, ct).ConfigureAwait(false);
            pairs.Add((hook, output));
            if (!output.Continue)
            {
                break;
            }
        }

        return this.MergeUserPromptSubmitOutputs(pairs);
    }

    // -------------------------------------------------------------------------
    // Hook loop
    // -------------------------------------------------------------------------

    private async Task<List<HookOutput>> RunHooksAsync(
        IReadOnlyList<UserHook> matching,
        string eventName,
        string payload,
        CancellationToken ct)
    {
        var outputs = new List<HookOutput>(matching.Count);
        foreach (var hook in matching)
        {
            var output = await this.RunSingleHookAsync(hook, eventName, payload, ct).ConfigureAwait(false);
            outputs.Add(output);
            if (!output.Continue)
            {
                break; // Continue:false short-circuits remaining hooks
            }
        }

        return outputs;
    }

    private async Task<HookOutput> RunSingleHookAsync(
        UserHook hook,
        string eventName,
        string payload,
        CancellationToken ct)
    {
        var policy = HookEventPolicy.Get(eventName);
        var timeoutSeconds = hook.TimeoutSeconds ?? policy.TimeoutSeconds;
        var failOpen = hook.FailOpen ?? policy.FailOpen;

        using var hookCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hookCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var (exitCode, rawStdout, rawStderr) = await this.executor
                .ExecAsync(hook.Command, payload, hookCts.Token)
                .ConfigureAwait(false);

            if (exitCode == 0)
            {
                // Apply cap/spill for the display/logging side effect (creates a spill file
                // when the output is large) but parse the full, bounded stdout so that a
                // valid JSON {"decision":"block",...} longer than OutputCap characters is
                // never silently truncated into invalid JSON and downgraded to allow.
                this.ApplyCapWithSpill(rawStdout, eventName, "stdout");
                this.ApplyCapWithSpill(rawStderr, eventName, "stderr");
                return HookOutputParser.Parse(rawStdout);
            }

            // For non-zero exits the capped copy is the human-facing reason.
            var stdout = this.ApplyCapWithSpill(rawStdout, eventName, "stdout");
            var stderr = this.ApplyCapWithSpill(rawStderr, eventName, "stderr");

            if (exitCode == 2)
            {
                var reason = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                    : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
                    : "blocked by hook (exit 2)";
                return new HookOutput { Decision = "block", Reason = reason };
            }

            // Other non-zero exit: warn, apply fail-open policy.
            this.LogHookNonZeroExit(hook.Command, exitCode, eventName);
            if (failOpen)
            {
                return HookOutput.NoOp;
            }

            var blockReason = !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
                : !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                : $"hook exited with code {exitCode}";
            return new HookOutput { Decision = "block", Reason = blockReason };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Hook-specific timeout fired, not the caller's cancellation.
            this.LogHookTimeout(hook.Command, timeoutSeconds, eventName);
            return failOpen
                ? HookOutput.NoOp
                : new HookOutput { Decision = "block", Reason = $"hook timed out after {timeoutSeconds}s" };
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation — propagate.
            throw;
        }
        catch (Exception ex)
        {
            this.LogHookException(hook.Command, eventName, ex);
            return failOpen
                ? HookOutput.NoOp
                : new HookOutput { Decision = "block", Reason = $"hook execution failed: {ex.Message}" };
        }
    }

    // -------------------------------------------------------------------------
    // Merge
    // -------------------------------------------------------------------------

    /// <summary>
    /// Merges multiple hook outputs into a single result following §6 of the proposal.
    /// Exposed as public to enable direct unit testing of the merge logic without spawning processes.
    /// </summary>
    public static HookOutput MergeOutputs(IReadOnlyList<HookOutput> outputs)
    {
        if (outputs.Count == 0)
        {
            return HookOutput.NoOp;
        }

        if (outputs.Count == 1)
        {
            return outputs[0];
        }

        string? decision = null;
        var reasons = new List<string>();
        var systemMessages = new List<string>();
        var additionalContexts = new List<string>();
        var continueRun = true;
        string? stopReason = null;
        var suppressOutput = false;

        foreach (var output in outputs)
        {
            // Decision: strictest wins (allow < ask < deny < block).
            decision = StrictestDecision(decision, output.Decision);

            // Reasons: collected from blocking / denying hooks only.
            if (IsBlockingDecision(output.Decision) && !string.IsNullOrEmpty(output.Reason))
            {
                reasons.Add(output.Reason);
            }

            // systemMessage: concatenated in order.
            if (!string.IsNullOrEmpty(output.SystemMessage))
            {
                systemMessages.Add(output.SystemMessage);
            }

            // Continue: false wins.
            if (!output.Continue)
            {
                continueRun = false;
            }

            // StopReason: last writer wins.
            if (output.StopReason is not null)
            {
                stopReason = output.StopReason;
            }

            // SuppressOutput: any true wins (semantically: once suppressed, keep suppressed).
            if (output.SuppressOutput)
            {
                suppressOutput = true;
            }

            // additionalContext inside hookSpecificOutput: concatenated in order.
            if (output.HookSpecificOutput is { } specific
                && specific.TryGetPropertyValue("additionalContext", out var addCtxNode)
                && addCtxNode is JsonValue addCtxValue
                && addCtxValue.TryGetValue<string>(out var addCtx)
                && !string.IsNullOrWhiteSpace(addCtx))
            {
                additionalContexts.Add(addCtx);
            }
        }

        JsonObject? mergedSpecific = additionalContexts.Count > 0
            ? new JsonObject { ["additionalContext"] = string.Join("\n\n", additionalContexts) }
            : null;

        return new HookOutput
        {
            Decision = decision,
            Reason = reasons.Count > 0 ? string.Join("\n\n", reasons) : null,
            SystemMessage = systemMessages.Count > 0 ? string.Join("\n", systemMessages) : null,
            Continue = continueRun,
            StopReason = stopReason,
            SuppressOutput = suppressOutput,
            HookSpecificOutput = mergedSpecific,
        };
    }

    private static string? StrictestDecision(string? current, string? candidate)
    {
        if (candidate is null)
        {
            return current;
        }

        if (current is null)
        {
            return candidate;
        }

        return DecisionRank(candidate) > DecisionRank(current) ? candidate : current;
    }

    private static int DecisionRank(string? decision) => decision?.ToLowerInvariant() switch
    {
        "allow" => 0,
        "ask"   => 1,
        "deny"  => 2,
        "block" => 3,
        _       => 0,
    };

    private static bool IsBlockingDecision(string? decision) =>
        decision is not null
        && (string.Equals(decision, "block", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "deny", StringComparison.OrdinalIgnoreCase));

    private static UserHookResult ToUserHookResult(HookOutput merged)
    {
        if (!merged.Continue)
        {
            return new UserHookResult(Block: true, merged.StopReason ?? merged.Reason ?? "hook requested stop");
        }

        if (IsBlockingDecision(merged.Decision))
        {
            return new UserHookResult(Block: true, merged.Reason ?? "blocked by hook");
        }

        return UserHookResult.Allow;
    }

    /// <summary>
    /// Merges <c>UserPromptSubmit</c> hook outputs following §6 of the proposal: last-writer-wins
    /// for single-valued fields (logged), union for <c>deniedTools</c>, intersection for
    /// <c>allowedTools</c> (null = no opinion, not an empty list), concatenation for
    /// <c>additionalContext</c> / <c>appendSystemPrompt</c>.
    /// </summary>
    /// <remarks>
    /// The <paramref name="pairs"/> parameter carries the hook alongside its output so that
    /// <c>AllowSystemPromptReplace</c> and <c>UnattendedDecision</c> can be read per hook.
    /// </remarks>
    private UserPromptSubmitResult MergeUserPromptSubmitOutputs(
        IReadOnlyList<(UserHook Hook, HookOutput Output)> pairs)
    {
        if (pairs.Count == 0)
        {
            return UserPromptSubmitResult.Allow;
        }

        string? decision = null;
        var reasons = new List<string>();
        var additionalContexts = new List<string>();
        var appendSystemPrompts = new List<string>();
        var continueRun = true;
        string? stopReason = null;

        // Single-valued last-writer-wins fields for UserPromptSubmit.
        string? modifiedPrompt = null;
        string? modifiedPromptHookCommand = null;
        string? systemPrompt = null;
        string? toolChoice = null;
        string? modelOverride = null;
        string? effortOverride = null;

        // Tool list accumulators.
        List<string>? deniedTools = null;       // union — null until first hook contributes
        List<string>? allowedTools = null;       // intersection — null means "no restriction yet"
        var allowedHasValue = false;             // true once at least one hook expressed an allowedTools list

        foreach (var (hook, output) in pairs)
        {
            // Decision: strictest wins (allow < ask < deny < block).
            decision = StrictestDecision(decision, output.Decision);

            // Reasons: collected from blocking / denying hooks only.
            if (IsBlockingDecision(output.Decision) && !string.IsNullOrEmpty(output.Reason))
            {
                reasons.Add(output.Reason);
            }

            // Continue: false wins.
            if (!output.Continue)
            {
                continueRun = false;
            }

            // StopReason: last writer wins.
            if (output.StopReason is not null)
            {
                stopReason = output.StopReason;
            }

            // --- event-specific fields from hookSpecificOutput ---
            var specific = output.HookSpecificOutput;
            if (specific is null)
            {
                continue;
            }

            // additionalContext — concatenate in order.
            if (TryGetString(specific, "additionalContext", out var addCtx))
            {
                additionalContexts.Add(addCtx!);
            }

            // appendSystemPrompt — concatenate in order.
            if (TryGetString(specific, "appendSystemPrompt", out var appendSp))
            {
                appendSystemPrompts.Add(appendSp!);
            }

            // modifiedPrompt — last writer wins, logged.
            if (TryGetString(specific, "modifiedPrompt", out var mp))
            {
                if (modifiedPrompt is not null)
                {
                    this.LogFieldOverride("modifiedPrompt", modifiedPrompt, mp!);
                }

                modifiedPrompt = mp;
                modifiedPromptHookCommand = hook.Command;
            }

            // systemPrompt — last writer wins, only when AllowSystemPromptReplace.
            if (TryGetString(specific, "systemPrompt", out var sp))
            {
                if (!hook.AllowSystemPromptReplace)
                {
                    this.LogSystemPromptIgnored(hook.Command);
                }
                else
                {
                    if (systemPrompt is not null)
                    {
                        this.LogFieldOverride("systemPrompt", systemPrompt, sp!);
                    }

                    systemPrompt = sp;
                }
            }

            // toolChoice — last writer wins, logged.
            if (TryGetString(specific, "toolChoice", out var tc))
            {
                if (toolChoice is not null)
                {
                    this.LogFieldOverride("toolChoice", toolChoice, tc!);
                }

                toolChoice = tc;
            }

            // model — last writer wins, logged.
            if (TryGetString(specific, "model", out var m))
            {
                if (modelOverride is not null)
                {
                    this.LogFieldOverride("model", modelOverride, m!);
                }

                modelOverride = m;
            }

            // effort — last writer wins, logged.
            if (TryGetString(specific, "effort", out var eff))
            {
                if (effortOverride is not null)
                {
                    this.LogFieldOverride("effort", effortOverride, eff!);
                }

                effortOverride = eff;
            }

            // allowedTools — intersection (null from one hook = "no opinion").
            if (specific.TryGetPropertyValue("allowedTools", out var allowedNode)
                && allowedNode is JsonArray allowedArray)
            {
                var hookAllowed = ReadStringArray(allowedArray);
                if (!allowedHasValue)
                {
                    // First hook with an opinion: start with its list.
                    allowedTools = [.. hookAllowed];
                    allowedHasValue = true;
                }
                else
                {
                    // Subsequent hooks: intersect — a permissive hook cannot widen a restrictive one.
                    var hookSet = new HashSet<string>(hookAllowed, StringComparer.OrdinalIgnoreCase);
                    allowedTools = [.. (allowedTools ?? []).Where(t => hookSet.Contains(t))];
                }
            }

            // deniedTools — union.
            if (specific.TryGetPropertyValue("deniedTools", out var deniedNode)
                && deniedNode is JsonArray deniedArray)
            {
                var hookDenied = ReadStringArray(deniedArray);
                if (deniedTools is null)
                {
                    deniedTools = [.. hookDenied];
                }
                else
                {
                    foreach (var tool in hookDenied)
                    {
                        if (!deniedTools.Any(t => string.Equals(t, tool, StringComparison.OrdinalIgnoreCase)))
                        {
                            deniedTools.Add(tool);
                        }
                    }
                }
            }
        }

        // Resolve ask → unattended decision (§8.2).
        // ask requires an answerer; at this seam there is no interactive answerer wired.
        // Resolve via the hook's UnattendedDecision (per-hook, strictest wins across all
        // hooks that contributed ask). An interactive path is deliberately deferred — this
        // is the §8.2 rule.
        if (string.Equals(decision, "ask", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ResolveUnattendedAsk(pairs);
            this.LogAskResolvedUnattended(resolved);
            decision = resolved ? "allow" : "block";
            if (!resolved)
            {
                reasons.Add("ask resolved as deny (unattended — no interactive answerer)");
            }
        }

        if (!continueRun || IsBlockingDecision(decision))
        {
            var blockReason = reasons.Count > 0
                ? string.Join("\n\n", reasons)
                : stopReason ?? "blocked by hook";
            return new UserPromptSubmitResult { Block = true, Reason = blockReason };
        }

        // Build the TurnShape from merged fields.
        var shape = BuildTurnShape(systemPrompt, appendSystemPrompts, allowedTools, allowedHasValue, deniedTools, toolChoice, modelOverride, effortOverride);

        return new UserPromptSubmitResult
        {
            Block = false,
            ModifiedPrompt = modifiedPrompt,
            ModifiedByHookCommand = modifiedPromptHookCommand,
            AdditionalContext = additionalContexts.Count > 0 ? string.Join("\n\n", additionalContexts) : null,
            Shape = shape?.IsEmpty == false ? shape : null,
        };
    }

    private static TurnShape? BuildTurnShape(
        string? systemPrompt,
        List<string> appendSystemPrompts,
        List<string>? allowedTools,
        bool allowedHasValue,
        List<string>? deniedTools,
        string? toolChoice,
        string? model,
        string? effort)
    {
        var appendSp = appendSystemPrompts.Count > 0 ? string.Join("\n\n", appendSystemPrompts) : null;

        if (systemPrompt is null
            && appendSp is null
            && !allowedHasValue
            && deniedTools is null
            && toolChoice is null
            && model is null
            && effort is null)
        {
            return null;
        }

        return new TurnShape
        {
            SystemPrompt = systemPrompt,
            AppendSystemPrompt = appendSp,
            AllowedTools = allowedHasValue ? allowedTools?.AsReadOnly() : null,
            DeniedTools = deniedTools?.AsReadOnly(),
            ToolChoice = toolChoice,
            Model = model,
            Effort = effort,
        };
    }

    /// <summary>
    /// Resolves an <c>ask</c> decision via each contributing hook's <c>UnattendedDecision</c>.
    /// Returns <see langword="true"/> (allow) only when every hook that contributed <c>ask</c>
    /// has <c>UnattendedDecision == "allow"</c>. Any hook with <c>deny</c> or the default (null)
    /// resolves to <see langword="false"/> (deny/block), because deny is the safer default.
    /// </summary>
    private static bool ResolveUnattendedAsk(IReadOnlyList<(UserHook Hook, HookOutput Output)> pairs)
    {
        foreach (var (hook, output) in pairs)
        {
            if (string.Equals(output.Decision, "ask", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(hook.UnattendedDecision, "allow", StringComparison.OrdinalIgnoreCase))
                {
                    return false; // deny (or null default = deny)
                }
            }
        }

        return true; // all ask-returning hooks opted into allow
    }

    private static bool TryGetString(JsonObject obj, string key, out string? value)
    {
        if (obj.TryGetPropertyValue(key, out var node)
            && node is JsonValue jv
            && jv.TryGetValue<string>(out var s)
            && !string.IsNullOrWhiteSpace(s))
        {
            value = s;
            return true;
        }

        value = null;
        return false;
    }

    private static IEnumerable<string> ReadStringArray(JsonArray array)
    {
        foreach (var item in array)
        {
            if (item is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                yield return s.Trim();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Payload builders
    // -------------------------------------------------------------------------

    private string BuildPrePayload(string toolName, string inputJson, int depth, string? taskId)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "PreToolUse", depth, taskId);
            writer.WriteString("tool", toolName);
            writer.WritePropertyName("input");
            WriteJsonOrString(writer, inputJson);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string BuildPostPayload(string toolName, string inputJson, string resultText, int depth, string? taskId)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "PostToolUse", depth, taskId);
            writer.WriteString("tool", toolName);
            writer.WritePropertyName("input");
            WriteJsonOrString(writer, inputJson);
            writer.WriteString("result", resultText);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string BuildStopPayload(int depth, string? taskId)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "Stop", depth, taskId);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string BuildUserPromptSubmitPayload(
        string prompt,
        IReadOnlyList<string> attachments,
        int historyLength,
        string model,
        string permissionMode,
        int depth,
        string? taskId)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            this.WriteEnvelope(writer, "UserPromptSubmit", depth, taskId);
            writer.WriteString("prompt", prompt);
            writer.WriteStartArray("attachments");
            foreach (var kind in attachments)
            {
                writer.WriteStringValue(kind);
            }

            writer.WriteEndArray();
            writer.WriteNumber("historyLength", historyLength);
            writer.WriteString("model", model);
            writer.WriteString("permissionMode", permissionMode);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void WriteEnvelope(Utf8JsonWriter writer, string eventName, int depth, string? taskId)
    {
        writer.WriteString("event", eventName);
        if (this.context is not null)
        {
            writer.WriteString("sessionId", this.context.SessionId);
            writer.WriteString("cwd", this.context.Cwd);
        }

        writer.WriteString("timestamp", (this.clock?.Invoke() ?? DateTimeOffset.UtcNow).ToString("o"));
        writer.WriteNumber("depth", depth);
        if (taskId is not null)
        {
            writer.WriteString("taskId", taskId);
        }
    }

    private static void WriteJsonOrString(Utf8JsonWriter writer, string json)
    {
        var normalized = string.IsNullOrWhiteSpace(json) ? "{}" : json;
        try
        {
            using var doc = JsonDocument.Parse(normalized);
            doc.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(json);
        }
    }

    // -------------------------------------------------------------------------
    // Output cap and spillover
    // -------------------------------------------------------------------------

    private string ApplyCapWithSpill(string text, string eventName, string stream)
    {
        if (text.Length <= OutputCap)
        {
            return text;
        }

        string? spillFile = null;
        try
        {
            var dir = this.spillDirFactory?.Invoke()
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".coda",
                    "hook-output");
            Directory.CreateDirectory(dir);

            var n = System.Threading.Interlocked.Increment(ref this.spillCounter);
            var ts = (this.clock?.Invoke() ?? DateTimeOffset.UtcNow).ToString("yyyyMMddTHHmmssZ");
            var filename = $"{ts}-{eventName}-{n}-{stream}.txt";
            spillFile = Path.Combine(dir, filename);
            File.WriteAllText(spillFile, text, Encoding.UTF8);
        }
        catch
        {
            // A spill-write failure must never fail the hook — degrade to plain truncation.
        }

        var truncated = text[..OutputCap];
        return spillFile is not null
            ? truncated + $"\n[output truncated; full text written to {spillFile}]"
            : truncated + "\n[output truncated]";
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private IReadOnlyList<UserHook> GetMatchingHooks(string eventName, string? toolName)
    {
        var result = new List<UserHook>();
        foreach (var hook in this.hooks)
        {
            if (!string.Equals(hook.Event, eventName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (toolName is not null && !HookMatcher.Matches(hook.Matcher, toolName))
            {
                continue;
            }

            result.Add(hook);
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Logging (source-generated)
    // -------------------------------------------------------------------------

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "hook exited with code {exitCode} for event '{eventName}' (command: {command})")]
    private partial void LogHookNonZeroExit(string command, int exitCode, string eventName);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "hook timed out after {timeoutSeconds}s for event '{eventName}' (command: {command})")]
    private partial void LogHookTimeout(string command, int timeoutSeconds, string eventName);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "hook threw an exception for event '{eventName}' (command: {command})")]
    private partial void LogHookException(string command, string eventName, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "hook single-valued field '{field}' overridden; previous: '{previous}', new: '{newValue}'")]
    private partial void LogFieldOverride(string field, string previous, string newValue);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "UserPromptSubmit hook '{command}' returned systemPrompt but allowSystemPromptReplace is false — ignoring; set allowSystemPromptReplace:true on the hook definition to enable full replacement")]
    private partial void LogSystemPromptIgnored(string command);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "UserPromptSubmit hook returned ask with no interactive answerer; resolved unattended as {resolution} (§8.2)")]
    private partial void LogAskResolvedUnattended(bool resolution);
}
