namespace Coda.Client;

using Coda.Client.Transport;

/// <summary>Base type for every error this library raises, so a caller can catch
/// the whole surface with one clause when it wants to.</summary>
public class CodaClientException : Exception
{
    public CodaClientException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The engine answered a request with a JSON-RPC error. This is distinct from a
/// transport failure: the round-trip completed, and the engine's own
/// <see cref="Error"/> — including <c>ok:false</c>-style refusals surfaced as
/// errors — is carried through verbatim rather than being flattened to a bool.
/// </summary>
public sealed class CodaRpcException : CodaClientException
{
    public CodaRpcException(RpcError error)
        : base($"engine returned JSON-RPC error {error.Code}: {error.Message}")
    {
        this.Error = error;
    }

    public RpcError Error { get; }

    public int Code => this.Error.Code;
}

/// <summary>
/// A request could not be completed because the connection ended — EOF from the
/// engine, a failed write, a framing fault, or an explicit close. It is raised
/// at the pending caller so a lost engine surfaces as a visible failure instead
/// of a task that never completes.
/// </summary>
public sealed class CodaConnectionClosedException : CodaClientException
{
    public CodaConnectionClosedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The bounded inbound buffer filled because notifications or reverse requests
/// were produced faster than they were consumed. Raised — rather than silently
/// dropping frames — so a caller learns its view of the event stream has a hole
/// and can re-read authoritative state, exactly as the protocol's
/// <c>event/eventsDropped</c> contract intends.
/// </summary>
public sealed class CodaInboundOverflowException : CodaClientException
{
    public CodaInboundOverflowException(string message)
        : base(message)
    {
    }
}
