using System.IO.Pipelines;

namespace Engine.Tests;

/// <summary>
/// In-memory duplex stream pair: the connection-under-test reads from clientReads and writes to
/// clientWrites; the fake server reads from serverReads and writes to serverWrites.
/// Uses System.IO.Pipelines Pipe objects so no anonymous pipes are needed.
/// </summary>
internal sealed class DuplexStreamPair : IDisposable
{
    private readonly Pipe serverToClient = new();
    private readonly Pipe clientToServer = new();

    // JsonRpcConnection reads from this (server→client direction).
    public Stream ClientReads => this.serverToClient.Reader.AsStream();

    // JsonRpcConnection writes to this (client→server direction).
    public Stream ClientWrites => this.clientToServer.Writer.AsStream();

    // Fake server reads what the connection wrote.
    public Stream ServerReads => this.clientToServer.Reader.AsStream();

    // Fake server writes responses the connection will read.
    public Stream ServerWrites => this.serverToClient.Writer.AsStream();

    public void CloseServerWrite()
    {
        this.serverToClient.Writer.Complete();
    }

    public void Dispose()
    {
        this.serverToClient.Writer.Complete();
        this.serverToClient.Reader.Complete();
        this.clientToServer.Writer.Complete();
        this.clientToServer.Reader.Complete();
    }
}
