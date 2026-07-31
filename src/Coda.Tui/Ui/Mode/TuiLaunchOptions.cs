namespace Coda.Tui.Ui.Mode;

public sealed record TuiLaunchOptions(
    TuiPreference Preference,
    bool Plain,
    IReadOnlyList<string> RemainingArgs,
    string? Error,
    bool MouseDisabled = false)
{
    public SystemPromptSource? SystemPromptSource { get; init; }
    public string? SystemPromptOverride { get; init; }

    /// <summary>
    /// The permission mode asked for at launch, or null when none was — in which case the session keeps
    /// whatever default it would otherwise have.
    /// </summary>
    public Coda.Agent.PermissionMode? PermissionMode { get; init; }

    /// <summary>
    /// Set by <c>--yolo-safe</c>: bypass mode, but every mutating action is first classified so risky
    /// ones are still escalated. Meaningless unless <see cref="PermissionMode"/> is bypass.
    /// </summary>
    public bool EnableBypassClassifier { get; init; }

    public static TuiLaunchOptions Parse(IReadOnlyList<string> args)
    {
        if (!SystemPromptSourceResolver.TryExtract(args, out var remainingArgs, out var systemPromptSource, out var error))
        {
            return new(TuiPreference.Auto, false, remainingArgs, error);
        }

        var preference = TuiPreference.Auto;
        var plain = false;
        var mouseDisabled = false;
        Coda.Agent.PermissionMode? permissionMode = null;
        var enableClassifier = false;
        var remaining = new List<string>();

        for (var i = 0; i < remainingArgs.Count; i++)
        {
            var arg = remainingArgs[i];

            if (arg == "--plain")
            {
                plain = true;
                continue;
            }

            if (arg == "--no-mouse")
            {
                mouseDisabled = true;
                continue;
            }

            // The permission flags are consumed here rather than left in RemainingArgs: a stray --yolo
            // ahead of --resume is exactly what used to defeat the session intent.
            if (arg is "--yolo" or "--yolo-safe")
            {
                permissionMode = Coda.Agent.PermissionMode.BypassPermissions;
                enableClassifier = arg == "--yolo-safe";
                continue;
            }

            if (arg == "--permission-mode" || arg.StartsWith("--permission-mode=", StringComparison.Ordinal))
            {
                string? value;
                if (arg.StartsWith("--permission-mode=", StringComparison.Ordinal))
                {
                    value = arg["--permission-mode=".Length..];
                }
                else if (i + 1 < remainingArgs.Count)
                {
                    value = remainingArgs[++i];
                }
                else
                {
                    value = null;
                }

                if (!TryParsePermissionMode(value, out var parsed))
                {
                    return new(preference, plain, remaining, $"Invalid --permission-mode value '{value}'. Expected default, acceptEdits, plan, or bypass.", mouseDisabled)
                    {
                        SystemPromptSource = systemPromptSource,
                    };
                }

                permissionMode = parsed;
                continue;
            }

            if (arg.StartsWith("--tui=", StringComparison.Ordinal))
            {
                var value = arg["--tui=".Length..];
                preference = value switch
                {
                    "auto" => TuiPreference.Auto,
                    "inline" => TuiPreference.Inline,
                    "fullscreen" => TuiPreference.Fullscreen,
                    _ => preference,
                };

                if (value is not ("auto" or "inline" or "fullscreen"))
                {
                    return new(preference, plain, remaining, $"Invalid --tui value '{value}'. Expected auto, inline, or fullscreen.", mouseDisabled)
                    {
                        SystemPromptSource = systemPromptSource,
                    };
                }

                continue;
            }

            remaining.Add(arg);
        }

        return new(preference, plain, remaining, null, mouseDisabled)
        {
            SystemPromptSource = systemPromptSource,
            PermissionMode = permissionMode,
            EnableBypassClassifier = enableClassifier,
        };
    }

    /// <summary>Maps a <c>--permission-mode</c> value onto a mode, matching the /permissions vocabulary.</summary>
    private static bool TryParsePermissionMode(string? value, out Coda.Agent.PermissionMode mode)
    {
        switch (value?.ToLowerInvariant())
        {
            case "default": mode = Coda.Agent.PermissionMode.Default; return true;
            case "acceptedits": mode = Coda.Agent.PermissionMode.AcceptEdits; return true;
            case "plan": mode = Coda.Agent.PermissionMode.Plan; return true;
            case "bypass" or "bypasspermissions" or "yolo":
                mode = Coda.Agent.PermissionMode.BypassPermissions;
                return true;
            default: mode = Coda.Agent.PermissionMode.Default; return false;
        }
    }
}
