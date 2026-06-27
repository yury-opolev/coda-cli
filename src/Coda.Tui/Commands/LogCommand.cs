using Coda.Agent.Settings;
using Coda.Sdk.Telemetry;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Shows or sets the telemetry logging level. Changes are persisted to user settings
/// and apply to the next session.
/// </summary>
public sealed class LogCommand : ISlashCommand
{
    public string Name => "log";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show or set telemetry logging level";

    public CommandHelp Help => new(
        "/log [<level> | stderr on|off]",
        Description: "Show or set telemetry logging. With no argument, prints the current state and log directory. A level enables file logging at that verbosity (persisted to settings, applied next session). Secrets are always redacted; full request bodies appear only at trace.",
        Options:
        [
            ("(no args)", "show current enabled/level/stderr + log directory"),
            ("trace|debug|info|warn|error", "enable logging at this level"),
            ("off", "disable logging"),
            ("stderr on|off", "also echo log lines to stderr"),
        ],
        Examples: ["/log", "/log debug", "/log off", "/log stderr on"]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (args.Count == 0 || args[0] is "current" or "status")
        {
            this.ShowCurrent(context);
            return Task.FromResult(CommandResult.Continue);
        }

        var arg = args[0].ToLowerInvariant();

        if (arg == "stderr")
        {
            return Task.FromResult(this.HandleStderr(context, args));
        }

        if (TelemetryResolver.IsOff(arg))
        {
            var current = SettingsLoader.Load(context.Session.WorkingDirectory).Telemetry ?? TelemetrySettings.Disabled;
            SettingsWriter.SetTelemetry(enabled: false, level: current.MinLevel, stderr: current.LogToStderr);
            context.Console.MarkupLine("Telemetry disabled.");
            return Task.FromResult(CommandResult.Continue);
        }

        if (TelemetryResolver.TryParseLevel(arg, out var level))
        {
            var current = SettingsLoader.Load(context.Session.WorkingDirectory).Telemetry ?? TelemetrySettings.Disabled;
            SettingsWriter.SetTelemetry(enabled: true, level: level, stderr: current.LogToStderr);
            var dir = current.DirectoryOverride ?? CodaLoggerFactory.DefaultLogDirectory;
            context.Console.MarkupLine(
                $"Telemetry enabled at {Theme.AccentMarkup(arg)}. " +
                $"Logs: {Theme.DimMarkup(dir)}. " +
                Theme.DimMarkup("Applies to the next session."));
            return Task.FromResult(CommandResult.Continue);
        }

        context.Console.MarkupLine(Theme.WarnMarkup(
            $"Invalid argument: {arg}. Valid: trace, debug, info, warn, error, off, or 'stderr on|off'."));
        return Task.FromResult(CommandResult.Continue);
    }

    private void ShowCurrent(CommandContext context)
    {
        var current = SettingsLoader.Load(context.Session.WorkingDirectory).Telemetry ?? TelemetrySettings.Disabled;
        var enabledText = current.Enabled ? "enabled" : "disabled";
        var levelText = current.MinLevel.ToString().ToLowerInvariant();
        var stderrText = current.LogToStderr ? "on" : "off";
        var dir = current.DirectoryOverride ?? CodaLoggerFactory.DefaultLogDirectory;

        context.Console.MarkupLine($"Telemetry: {Theme.AccentMarkup(enabledText)}");
        context.Console.MarkupLine($"Log level:  {Theme.AccentMarkup(levelText)}");
        context.Console.MarkupLine($"Stderr:     {Theme.AccentMarkup(stderrText)}");
        context.Console.MarkupLine($"Log dir:    {Theme.DimMarkup(dir)}");
        context.Console.MarkupLine(Theme.DimMarkup("Changes apply to the next session."));
    }

    private CommandResult HandleStderr(CommandContext context, IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[1].ToLowerInvariant() is not ("on" or "off"))
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                "Usage: /log stderr on|off"));
            return CommandResult.Continue;
        }

        var on = args[1].ToLowerInvariant() == "on";
        var current = SettingsLoader.Load(context.Session.WorkingDirectory).Telemetry ?? TelemetrySettings.Disabled;
        SettingsWriter.SetTelemetry(enabled: current.Enabled, level: current.MinLevel, stderr: on);
        context.Console.MarkupLine($"Stderr logging: {Theme.AccentMarkup(on ? "on" : "off")}.");
        return CommandResult.Continue;
    }
}
