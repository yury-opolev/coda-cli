namespace Coda.Sdk.Serve.Transport;

/// <summary>Default transport: the process's standard input/output streams. No endpoint, no auth.</summary>
public sealed class StdioServeTransport : IServeTransport
{
    public Task<ServeListenInfo?> StartAsync(CancellationToken cancellationToken) =>
        Task.FromResult<ServeListenInfo?>(null);

    public Task<ServeStreams> AcceptAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ServeStreams(Console.OpenStandardInput(), Console.OpenStandardOutput()));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
