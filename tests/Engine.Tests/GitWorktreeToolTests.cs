using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;

namespace Engine.Tests;

public sealed class GitWorktreeToolTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_wt_").FullName;

    private ToolContext Ctx => new(this.tempDir);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    // ── metadata ─────────────────────────────────────────────────────────────

    [Fact]
    public void Name_is_git_worktree()
    {
        Assert.Equal("git_worktree", new GitWorktreeTool().Name);
    }

    [Fact]
    public void IsReadOnly_is_false()
    {
        Assert.False(new GitWorktreeTool().IsReadOnly);
    }

    // ── input validation (no git needed) ────────────────────────────────────

    [Fact]
    public async Task Missing_action_returns_error_ToolResult()
    {
        var result = await new GitWorktreeTool().ExecuteAsync(
            Input("""{}"""), this.Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("action", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_action_returns_error_ToolResult()
    {
        var result = await new GitWorktreeTool().ExecuteAsync(
            Input("""{"action":"frobnicate"}"""), this.Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("frobnicate", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Add_without_path_returns_error_ToolResult()
    {
        var result = await new GitWorktreeTool().ExecuteAsync(
            Input("""{"action":"add"}"""), this.Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("path", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remove_without_path_returns_error_ToolResult()
    {
        var result = await new GitWorktreeTool().ExecuteAsync(
            Input("""{"action":"remove"}"""), this.Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("path", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ── list in a non-git directory (invokes git, but repo is absent) ────────

    [Fact]
    public async Task List_in_non_git_dir_returns_ToolResult_without_throwing()
    {
        // tempDir is not a git repo; git exits non-zero with "not a git repository".
        // The tool must return a ToolResult (possibly IsError=true) rather than throw.
        var result = await new GitWorktreeTool().ExecuteAsync(
            Input("""{"action":"list"}"""), this.Ctx, CancellationToken.None);

        // We get back a ToolResult — never an exception.
        Assert.NotNull(result);
        // If git is available, it exits non-zero and IsError is true.
        // If git is NOT on PATH, the catch path also yields IsError=true.
        Assert.True(result.IsError);
    }

    // ── positive test: list in a freshly initialised repo ───────────────────

    [Fact]
    public async Task List_in_fresh_git_repo_returns_main_worktree_entry()
    {
        // Initialise a throwaway repo so `git worktree list` succeeds.
        var repoDir = Directory.CreateTempSubdirectory("coda_wt_repo_").FullName;
        try
        {
            var initResult = await RunGitInitAsync(repoDir);
            if (initResult is not null)
            {
                // git not found or init failed — skip gracefully.
                return;
            }

            var result = await new GitWorktreeTool().ExecuteAsync(
                Input("""{"action":"list"}"""),
                new ToolContext(repoDir),
                CancellationToken.None);

            Assert.False(result.IsError, result.Content);
            // `git worktree list` always prints at least one line for the main worktree.
            Assert.False(string.IsNullOrWhiteSpace(result.Content));
        }
        finally
        {
            try { Directory.Delete(repoDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Returns null on success, or a skip-reason string if git is unavailable or init fails.
    /// </summary>
    private static async Task<string?> RunGitInitAsync(string workingDirectory)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory,
                },
            };
            process.StartInfo.ArgumentList.Add("init");

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return "git init timed out";
            }

            await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            return process.ExitCode == 0 ? null : "git init failed";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return "git not found";
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }
}
