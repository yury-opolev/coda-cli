using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Sdk.Telemetry;
using LlmClient;

namespace Coda.Sdk;

/// <summary>Parsed options for the headless <c>coda run</c> subcommand.</summary>
public sealed record HeadlessOptions
{
    public required string Prompt { get; init; }
    public bool Json { get; init; }
    public PermissionMode PermissionMode { get; init; } = PermissionMode.Default;
    public string? ProviderId { get; init; }
    public string? Model { get; init; }
    public string? WorkingDirectory { get; init; }

    /// <summary>Reasoning effort level (low/medium/high/max), or null for the model default. Honored only by Claude models that support effort.</summary>
    public string? Effort { get; init; }

    /// <summary>Telemetry verbosity for this run (trace/debug/info/warn/error/off), or null to use settings. Maps to CODA_LOG_LEVEL.</summary>
    public string? LogLevel { get; init; }

    /// <summary>
    /// When true (set by <c>--yolo-safe</c>), bypass mode routes mutating actions through the
    /// safety classifier instead of blanket-allowing them.
    /// </summary>
    public bool EnableClassifier { get; init; }

    /// <summary>Autonomous goal: the agent keeps working until a judge says the goal is met.</summary>
    public string? Goal { get; init; }

    /// <summary>When true, the background SessionMemory watcher is enabled after work-bearing turns.</summary>
    public bool EnableSessionMemory { get; init; }

    /// <summary>Bound on stop-hook forced continuations per run (goal loop). Default 10.</summary>
    public int MaxStopContinuations { get; init; } = 10;

    /// <summary>Wall-clock budget for a goal run. Null → settings/default (24h).</summary>
    public TimeSpan? GoalMaxDuration { get; init; }

    /// <summary>
    /// Explicit override for the goal turn (continuation) backstop — set only when <c>--max-continuations</c>
    /// is explicitly provided on the command line. Null means "use the goal default" (60000).
    /// </summary>
    public int? GoalMaxContinuationsOverride { get; init; }

    /// <summary>
    /// Parse <c>run</c> arguments (everything after the <c>run</c> token):
    /// <c>-p/--prompt &lt;text&gt; [--json] [--yolo] [--yolo-safe] [--permission-mode m]
    /// [--provider id] [--model id] [--effort level] [--cwd path] [--goal &lt;text&gt;]
    /// [--goal-timeout &lt;duration&gt;] [--session-memory] [--max-continuations &lt;n&gt;]</c>.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, out HeadlessOptions options, out string? error)
    {
        options = new HeadlessOptions { Prompt = string.Empty };
        error = null;

        string? prompt = null;
        var json = false;
        var mode = PermissionMode.Default;
        string? provider = null;
        string? model = null;
        string? cwd = null;
        string? effort = null;
        string? logLevel = null;
        var enableClassifier = false;
        string? goal = null;
        TimeSpan? goalMaxDuration = null;
        var enableSessionMemory = false;
        var maxContinuations = 10;
        int? goalMaxContinuationsOverride = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-p" or "--prompt":
                    if (++i >= args.Count) { error = "Missing value for -p/--prompt."; return false; }
                    prompt = args[i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--yolo":
                    mode = PermissionMode.BypassPermissions;
                    break;
                case "--yolo-safe":
                    mode = PermissionMode.BypassPermissions;
                    enableClassifier = true;
                    break;
                case "--permission-mode":
                    if (++i >= args.Count) { error = "Missing value for --permission-mode."; return false; }
                    if (!TryParseMode(args[i], out mode)) { error = $"Unknown permission mode '{args[i]}'."; return false; }
                    break;
                case "--provider":
                    if (++i >= args.Count) { error = "Missing value for --provider."; return false; }
                    provider = args[i];
                    break;
                case "--model":
                    if (++i >= args.Count) { error = "Missing value for --model."; return false; }
                    model = args[i];
                    break;
                case "--cwd":
                    if (++i >= args.Count) { error = "Missing value for --cwd."; return false; }
                    cwd = args[i];
                    break;
                case "--effort":
                    if (++i >= args.Count) { error = "Missing value for --effort."; return false; }
                    var effortArg = args[i].ToLowerInvariant();
                    if (effortArg is "auto" or "unset")
                    {
                        effort = null;
                    }
                    else if (EffortSupport.IsEffortLevel(effortArg))
                    {
                        effort = effortArg;
                    }
                    else
                    {
                        error = $"Invalid value for --effort: '{args[i]}'. Valid options are: low, medium, high, max, auto.";
                        return false;
                    }

                    break;
                case "--log-level":
                    if (++i >= args.Count) { error = "Missing value for --log-level."; return false; }
                    var levelArg = args[i].ToLowerInvariant();
                    if (TelemetryResolver.IsOff(levelArg))
                    {
                        logLevel = "off";
                    }
                    else if (TelemetryResolver.TryParseLevel(levelArg, out _))
                    {
                        logLevel = levelArg;
                    }
                    else
                    {
                        error = $"Invalid value for --log-level: '{args[i]}'. Valid options are: trace, debug, info, warn, error, off.";
                        return false;
                    }

                    break;
                case "--goal":
                    if (++i >= args.Count) { error = "Missing value for --goal."; return false; }
                    goal = args[i];
                    break;
                case "--goal-timeout":
                    if (++i >= args.Count) { error = "Missing value for --goal-timeout."; return false; }
                    if (!DurationParser.TryParse(args[i], out var parsedDuration))
                    {
                        error = $"Invalid value for --goal-timeout: '{args[i]}'. Use forms like 30m, 2h, 1d, or hh:mm:ss.";
                        return false;
                    }

                    goalMaxDuration = parsedDuration;
                    break;
                case "--session-memory":
                    enableSessionMemory = true;
                    break;
                case "--max-continuations":
                    if (++i >= args.Count) { error = "Missing value for --max-continuations."; return false; }
                    if (!int.TryParse(args[i], out maxContinuations) || maxContinuations <= 0) { error = $"Invalid value for --max-continuations: '{args[i]}' must be a positive integer."; return false; }
                    goalMaxContinuationsOverride = maxContinuations;
                    break;
                default:
                    error = $"Unknown argument '{arg}'.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            error = "A prompt is required: coda run -p \"<task>\".";
            return false;
        }

        if (goalMaxDuration.HasValue && string.IsNullOrWhiteSpace(goal))
        {
            error = "--goal-timeout requires --goal to be set.";
            return false;
        }

        options = new HeadlessOptions
        {
            Prompt = prompt,
            Json = json,
            PermissionMode = mode,
            ProviderId = provider,
            Model = model,
            WorkingDirectory = cwd,
            Effort = effort,
            LogLevel = logLevel,
            EnableClassifier = enableClassifier,
            Goal = goal,
            GoalMaxDuration = goalMaxDuration,
            EnableSessionMemory = enableSessionMemory,
            MaxStopContinuations = maxContinuations,
            GoalMaxContinuationsOverride = goalMaxContinuationsOverride,
        };
        return true;
    }

    private static bool TryParseMode(string value, out PermissionMode mode)
    {
        switch (value.ToLowerInvariant())
        {
            case "default": mode = PermissionMode.Default; return true;
            case "acceptedits" or "accept-edits": mode = PermissionMode.AcceptEdits; return true;
            case "plan": mode = PermissionMode.Plan; return true;
            case "bypass" or "bypasspermissions" or "yolo": mode = PermissionMode.BypassPermissions; return true;
            default: mode = PermissionMode.Default; return false;
        }
    }
}
