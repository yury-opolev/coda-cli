using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Tests;

public sealed class InteractiveLineEditorTests
{
    [Fact]
    public async Task ReadKeyAsync_returns_null_when_canceled()
    {
        var input = new BlockingConsoleInput();
        using var cts = new CancellationTokenSource();
        var read = InteractiveLineEditor.ReadKeyAsync(input, cts.Token);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        cts.Cancel();
        var result = await read;

        Assert.Null(result);
    }

    private sealed class BlockingConsoleInput : IAnsiConsoleInput
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsKeyAvailable() => false;

        public ConsoleKeyInfo? ReadKey(bool intercept) => null;

        public async Task<ConsoleKeyInfo?> ReadKeyAsync(
            bool intercept,
            CancellationToken cancellationToken)
        {
            this.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }
}
