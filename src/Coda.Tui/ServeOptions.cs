using Coda.Agent;
using Coda.Sdk.Telemetry;

namespace Coda.Tui;

/// <summary>Parsed options for the <c>coda serve</c> subcommand.</summary>
public sealed record ServeOptions
{
    public string? ProviderId { get; init; }
    public string? Model { get; init; }
    public string? WorkingDirectory { get; init; }
    public PermissionMode PermissionMode { get; init; } = PermissionMode.Default;

    /// <summary>When true (yolo-safe), bypass mode routes each mutating action through the
    /// safety classifier and escalates the risky ones instead of blanket-allowing.</summary>
    public bool EnableClassifier { get; init; }

    public string? ApiKey { get; init; }
    public string? Endpoint { get; init; }

    /// <summary>Autonomous goal text — the agent keeps working until a judge says it is met.</summary>
    public string? Goal { get; init; }

    /// <summary>When true, the session-memory background watcher is enabled.</summary>
    public bool EnableSessionMemory { get; init; }

    /// <summary>Bound on stop-hook forced continuations per run (used by the goal loop). Default 10.</summary>
    public int MaxStopContinuations { get; init; } = 10;

    /// <summary>Wall-clock budget for a goal run. Null → settings/default (24h).</summary>
    public TimeSpan? GoalMaxDuration { get; init; }

    /// <summary>Turn (continuation) backstop for a goal run. Null → settings/default (60000).</summary>
    public int? GoalMaxContinuations { get; init; }

    /// <summary>When true (<c>--telemetry</c>), force telemetry/structured logging on for this
    /// serve run regardless of <c>~/.coda/settings.json</c>. Never mutates the on-disk settings.</summary>
    public bool ForceTelemetry { get; init; }

    /// <summary>Telemetry verbosity for this run (<c>trace|debug|info|warn|error|off</c>), or null
    /// to keep the loaded settings' level. Honored only when telemetry is forced on.</summary>
    public string? TelemetryLevel { get; init; }

    /// <summary>When false (<c>--no-mcp</c> or <c>CODA_SERVE_DISABLE_MCP</c>), skip connecting MCP
    /// servers. Defaults to true for parity with the TUI and <c>coda run</c>.</summary>
    public bool EnableMcp { get; init; } = true;

    /// <summary>When false (<c>--no-project-mcp</c> or <c>CODA_DISABLE_PROJECT_MCP</c>), ignore the
    /// project-level <c>&lt;cwd&gt;/.mcp.json</c> and load only the user layer. Defaults to true
    /// (full host visibility); an orchestrator sets it for a curated, repo-isolated MCP set.</summary>
    public bool EnableProjectMcp { get; init; } = true;

    /// <summary>
    /// Parse <c>serve</c> arguments (everything after the <c>serve</c> token):
    /// <c>[--provider id] [--model id] [--cwd path] [--permission-mode m] [--yolo] [--api-key key] [--endpoint name]</c>.
    /// </summary>
    public static ServeOptions Parse(IReadOnlyList<string> args)
    {
        string? provider = null;
        string? model = null;
        string? cwd = null;
        var mode = PermissionMode.Default;
        var enableClassifier = false;
        string? apiKey = null;
        string? endpoint = null;
        string? goal = null;
        var enableSessionMemory = false;
        var maxStopContinuations = 10;
        TimeSpan? goalMaxDuration = null;
        int? goalMaxContinuations = null;
        var forceTelemetry = false;
        string? telemetryLevel = null;
        var enableMcp = true;
        var enableProjectMcp = true;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--provider":
                    if (++i < args.Count)
                    {
                        provider = args[i];
                    }

                    break;

                case "--model":
                    if (++i < args.Count)
                    {
                        model = args[i];
                    }

                    break;

                case "--cwd":
                    if (++i < args.Count)
                    {
                        cwd = args[i];
                    }

                    break;

                case "--permission-mode":
                    if (++i < args.Count)
                    {
                        var value = args[i];
                        if (string.Equals(value, "yolo-safe", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(value, "yolosafe", StringComparison.OrdinalIgnoreCase))
                        {
                            mode = PermissionMode.BypassPermissions;
                            enableClassifier = true;
                        }
                        else
                        {
                            TryParseMode(value, out mode);
                        }
                    }

                    break;

                case "--yolo":
                    mode = PermissionMode.BypassPermissions;
                    break;

                case "--yolo-safe":
                    mode = PermissionMode.BypassPermissions;
                    enableClassifier = true;
                    break;

                case "--api-key":
                    if (++i < args.Count)
                    {
                        apiKey = args[i];
                    }

                    break;

                case "--endpoint":
                    if (++i < args.Count)
                    {
                        endpoint = args[i];
                    }

                    break;

                case "--goal":
                    if (++i < args.Count)
                    {
                        goal = args[i];
                    }

                    break;

                case "--session-memory":
                    enableSessionMemory = true;
                    break;

                case "--no-mcp":
                    enableMcp = false;
                    break;

                case "--mcp":
                    enableMcp = true;
                    break;

                case "--no-project-mcp":
                    enableProjectMcp = false;
                    break;

                case "--max-continuations":
                    // Matches `coda run`: bounds non-goal stop-hooks AND sets the goal turn backstop.
                    if (++i < args.Count && int.TryParse(args[i], out var parsedMax) && parsedMax > 0)
                    {
                        maxStopContinuations = parsedMax;
                        goalMaxContinuations = parsedMax;
                    }

                    break;

                // --goal-timeout matches the `coda run` flag name; --goal-max-duration is a
                // back-compat alias. Both set the goal wall-clock budget.
                case "--goal-timeout":
                case "--goal-max-duration":
                    if (++i < args.Count && Coda.Agent.Goals.DurationParser.TryParse(args[i], out var parsedGmd))
                    {
                        goalMaxDuration = parsedGmd;
                    }

                    break;

                case "--goal-max-continuations":
                    if (++i < args.Count && int.TryParse(args[i], out var parsedGmc) && parsedGmc > 0)
                    {
                        goalMaxContinuations = parsedGmc;
                    }

                    break;

                case "--telemetry":
                    forceTelemetry = true;
                    break;

                // Validate against the same level set as `coda run --log-level`
                // (trace|debug|info|warn|error|off). An invalid value is silently
                // ignored — serve parsing is forward-compatible (matches the other cases).
                case "--telemetry-level":
                    if (++i < args.Count)
                    {
                        var levelArg = args[i].Trim().ToLowerInvariant();
                        if (TelemetryResolver.IsOff(levelArg))
                        {
                            telemetryLevel = "off";
                        }
                        else if (TelemetryResolver.TryParseLevel(levelArg, out _))
                        {
                            telemetryLevel = levelArg;
                        }
                    }

                    break;

                // Unknown flags are silently ignored — serve is forward-compatible.
                default:
                    break;
            }
        }

        return new ServeOptions
        {
            ProviderId = provider,
            Model = model,
            WorkingDirectory = cwd,
            PermissionMode = mode,
            EnableClassifier = enableClassifier,
            ApiKey = apiKey,
            Endpoint = endpoint,
            Goal = goal,
            EnableSessionMemory = enableSessionMemory,
            MaxStopContinuations = maxStopContinuations,
            GoalMaxDuration = goalMaxDuration,
            GoalMaxContinuations = goalMaxContinuations,
            ForceTelemetry = forceTelemetry,
            TelemetryLevel = telemetryLevel,
            EnableMcp = enableMcp,
            EnableProjectMcp = enableProjectMcp,
        };
    }

    private static bool TryParseMode(string value, out PermissionMode mode)
    {
        switch (value.ToLowerInvariant())
        {
            case "default":
                mode = PermissionMode.Default;
                return true;
            case "acceptedits" or "accept-edits":
                mode = PermissionMode.AcceptEdits;
                return true;
            case "plan":
                mode = PermissionMode.Plan;
                return true;
            case "bypass" or "bypasspermissions" or "yolo":
                mode = PermissionMode.BypassPermissions;
                return true;
            default:
                mode = PermissionMode.Default;
                return false;
        }
    }
}
