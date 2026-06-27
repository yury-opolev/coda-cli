namespace Coda.Sdk.Serve.Transport;

/// <summary>
/// A serve transport. <see cref="StartAsync"/> binds/listens and resolves the endpoint (returns
/// null for stdio); it must complete before any client can connect. <see cref="AcceptAsync"/>
/// waits for exactly one client and returns the stream pair for ServeHost.
/// </summary>
public interface IServeTransport : IAsyncDisposable
{
    Task<ServeListenInfo?> StartAsync(CancellationToken cancellationToken);

    Task<ServeStreams> AcceptAsync(CancellationToken cancellationToken);
}
