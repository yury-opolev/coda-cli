using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Coda.Sdk.Serve.Transport;

/// <summary>
/// Local socket transport: a named pipe on Windows, a Unix domain socket elsewhere. Binds + listens
/// in <see cref="StartAsync"/> (resolving/generating the endpoint), accepts one client in
/// <see cref="AcceptAsync"/>, and returns the duplex stream as both Input and Output.
/// </summary>
public sealed class LocalSocketServeTransport : IServeTransport
{
    private const int PipeBufferSize = 65536;

    private readonly string? requestedEndpoint;

    private ServeListenInfo info;
    private NamedPipeServerStream? pipe;
    private Socket? listenSocket;
    private string? unixPath;

    public LocalSocketServeTransport(string? endpoint)
    {
        this.requestedEndpoint = endpoint;
    }

    public Task<ServeListenInfo?> StartAsync(CancellationToken cancellationToken)
    {
        this.info = ServeEndpoint.Resolve(this.requestedEndpoint);

        if (this.info.Transport == "pipe")
        {
            this.pipe = CreatePipeServer(this.info.Endpoint);
        }
        else
        {
            this.unixPath = this.info.Endpoint;
            PrepareUnixPath(this.unixPath);
            this.listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            this.listenSocket.Bind(new UnixDomainSocketEndPoint(this.unixPath));
            this.listenSocket.Listen(1);
            TrySetOwnerOnly(this.unixPath);
        }

        return Task.FromResult<ServeListenInfo?>(this.info);
    }

    public async Task<ServeStreams> AcceptAsync(CancellationToken cancellationToken)
    {
        if (this.pipe is not null)
        {
            await this.pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return new ServeStreams(this.pipe, this.pipe);
        }

        var accepted = await this.listenSocket!.AcceptAsync(cancellationToken).ConfigureAwait(false);
        var stream = new NetworkStream(accepted, ownsSocket: true);
        return new ServeStreams(stream, stream);
    }

    private static NamedPipeServerStream CreatePipeServer(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new PipeSecurity();
            var sid = WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(
                name,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: PipeBufferSize,
                outBufferSize: PipeBufferSize,
                pipeSecurity: security);
        }

        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    private static void PrepareUnixPath(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best-effort; Bind will throw a clear error if it is genuinely in use.
            }
        }
    }

    private static void TrySetOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best-effort hardening.
        }
    }

    public ValueTask DisposeAsync()
    {
        this.pipe?.Dispose();
        this.listenSocket?.Dispose();

        if (this.unixPath is not null && File.Exists(this.unixPath))
        {
            try
            {
                File.Delete(this.unixPath);
            }
            catch
            {
                // Best-effort.
            }
        }

        return ValueTask.CompletedTask;
    }
}
