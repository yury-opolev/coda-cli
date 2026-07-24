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

    public static TuiLaunchOptions Parse(IReadOnlyList<string> args)
    {
        if (!SystemPromptSourceResolver.TryExtract(args, out var remainingArgs, out var systemPromptSource, out var error))
        {
            return new(TuiPreference.Auto, false, remainingArgs, error);
        }

        var preference = TuiPreference.Auto;
        var plain = false;
        var mouseDisabled = false;
        var remaining = new List<string>();

        foreach (var arg in remainingArgs)
        {
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
        };
    }
}
