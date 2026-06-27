using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;
using Coda.Common;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

/// <summary>
/// Full-telemetry logging of tool/shell execution: on start the tool name, command,
/// and cwd are logged; on completion the exit code, duration, timed-out flag, and
/// truncated stdout/stderr previews are logged. Emitted at Debug/Trace.
/// </summary>
public sealed class ToolExecutionLoggingTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => this.Entries.Add((logLevel, formatter(state, exception)));
    }

    private readonly string workingDirectory;

    public ToolExecutionLoggingTests()
    {
        this.workingDirectory = Path.Combine(Path.GetTempPath(), "coda-toollog-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.workingDirectory);
    }

    [Fact]
    public async Task RunCommand_logs_start_and_completion()
    {
        var logger = new CapturingLogger();
        var tool = new RunCommandTool();
        var context = new ToolContext(this.workingDirectory) { Logger = logger };
        var input = JsonSerializer.Deserialize<JsonElement>("""{"command":"echo telemetry-marker"}""");

        var result = await tool
            .ExecuteAsync(input, context)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.IsError);

        // Start log: tool name, command, cwd.
        var startLine = Assert.Single(logger.Entries, e => e.Message.Contains("tool start"));
        Assert.Contains("run_command", startLine.Message);
        Assert.Contains("echo telemetry-marker", startLine.Message);
        Assert.Contains(this.workingDirectory, startLine.Message);

        // Completion log: exit code, duration, timedOut, output preview.
        var doneLine = Assert.Single(logger.Entries, e => e.Message.Contains("tool done"));
        Assert.Contains("run_command", doneLine.Message);
        Assert.Contains("exit=0", doneLine.Message);
        Assert.Contains("ms", doneLine.Message);
        Assert.Contains("timedOut=False", doneLine.Message);
        Assert.Contains("telemetry-marker", doneLine.Message);
    }

    [Fact]
    public async Task RunCommand_completion_log_truncates_long_output()
    {
        var logger = new CapturingLogger();
        Environment.SetEnvironmentVariable(TelemetryText.TruncateEnv, "80");
        try
        {
            var tool = new RunCommandTool();
            var context = new ToolContext(this.workingDirectory) { Logger = logger };
            // Emit far more than the 80-char preview limit.
            var command = OperatingSystem.IsWindows()
                ? "Write-Output ('z' * 4000)"
                : "printf 'z%.0s' {1..4000}";
            var input = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(new { command }));

            await tool.ExecuteAsync(input, context).WaitAsync(TimeSpan.FromSeconds(30));

            var doneLine = Assert.Single(logger.Entries, e => e.Message.Contains("tool done"));
            Assert.Contains("chars total", doneLine.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TelemetryText.TruncateEnv, null);
        }
    }

    [Fact]
    public async Task RunCommand_without_logger_does_not_throw()
    {
        // No logger wired (subagent/team context): execution must still work.
        var tool = new RunCommandTool();
        var context = new ToolContext(this.workingDirectory);
        var input = JsonSerializer.Deserialize<JsonElement>("""{"command":"echo ok"}""");

        var result = await tool.ExecuteAsync(input, context).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.IsError);
    }
}
