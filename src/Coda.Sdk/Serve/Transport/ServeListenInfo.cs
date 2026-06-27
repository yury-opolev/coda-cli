namespace Coda.Sdk.Serve.Transport;

/// <summary>Resolved listen info for the stdout readiness line. Transport is "pipe" | "unix".</summary>
public readonly record struct ServeListenInfo(string Transport, string Endpoint);
