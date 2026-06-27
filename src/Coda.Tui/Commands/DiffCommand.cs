using System.Diagnostics;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Runs <c>git diff</c> in the working directory and prints the output.</summary>
public sealed class DiffCommand : ISlashCommand
{
    public string Name => "diff";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show uncommitted git changes in the working directory";

    public CommandHelp Help => new(
        "/diff",
        Description: "Run git diff in the working directory and print all uncommitted changes.");

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "diff",
                WorkingDirectory = context.Session.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();

            // Start draining stdout/stderr immediately so a large diff can't block
            // git on a full pipe buffer (the classic deadlock) while we wait.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                context.Console.MarkupLine(Theme.WarnMarkup("git diff timed out after 10s."));
                return CommandResult.Continue;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0 || stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            {
                var message = string.IsNullOrWhiteSpace(stderr)
                    ? "git exited with a non-zero status. Is this directory a git repository?"
                    : stderr.Trim();
                context.Console.MarkupLine(Theme.ErrorMarkup(message));
                return CommandResult.Continue;
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                context.Console.MarkupLine(Theme.DimMarkup("No uncommitted changes."));
                return CommandResult.Continue;
            }

            // Write raw output without markup interpretation to preserve diff formatting.
            context.Console.WriteLine(stdout);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            context.Console.MarkupLine(Theme.DimMarkup("git not found. Make sure git is installed and on your PATH."));
        }
        catch (Exception ex)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Failed to run git diff: {ex.Message}"));
        }

        return CommandResult.Continue;
    }
}
