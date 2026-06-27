using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>Manages git worktrees in the working directory (list / add / remove).</summary>
public sealed class GitWorktreeTool : ITool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public string Name => "git_worktree";

    public string Description => "Manage git worktrees in the working directory: list existing worktrees, add a new one, or remove one. Useful for isolated parallel work.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "add", "remove"],
              "description": "The worktree action to perform."
            },
            "path": {
              "type": "string",
              "description": "Worktree path (required for add and remove)."
            },
            "branch": {
              "type": "string",
              "description": "Branch name to create for the new worktree (optional, for add only)."
            }
          },
          "required": ["action"]
        }
        """;

    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var action = ToolInput.GetString(input, "action");
        if (string.IsNullOrWhiteSpace(action))
        {
            return new ToolResult("Missing required 'action'. Must be one of: list, add, remove.", IsError: true);
        }

        var path = ToolInput.GetString(input, "path");
        var branch = ToolInput.GetString(input, "branch");

        List<string> gitArgs;
        switch (action)
        {
            case "list":
                gitArgs = ["worktree", "list"];
                break;

            case "add":
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new ToolResult("Missing required 'path' for action 'add'.", IsError: true);
                }

                gitArgs = ["worktree", "add", path];
                if (!string.IsNullOrWhiteSpace(branch))
                {
                    gitArgs.Add("-b");
                    gitArgs.Add(branch);
                }

                break;

            case "remove":
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new ToolResult("Missing required 'path' for action 'remove'.", IsError: true);
                }

                gitArgs = ["worktree", "remove", path];
                break;

            default:
                return new ToolResult($"Unknown action '{action}'. Must be one of: list, add, remove.", IsError: true);
        }

        return await this.RunGitAsync(gitArgs, context.WorkingDirectory, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolResult> RunGitAsync(IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            process.Start();

            // Drain stdout and stderr concurrently before awaiting exit to prevent
            // pipe-buffer deadlock on large output.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new ToolResult($"git worktree timed out after {Timeout.TotalSeconds:N0}s.", IsError: true);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var errorText = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return new ToolResult(errorText.TrimEnd(), IsError: true);
            }

            return new ToolResult(stdout.TrimEnd());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return new ToolResult("git not found. Make sure git is installed and on your PATH.", IsError: true);
        }
        catch (Exception ex)
        {
            return new ToolResult($"Failed to run git worktree: {ex.Message}", IsError: true);
        }
        finally
        {
            process?.Dispose();
        }
    }
}
