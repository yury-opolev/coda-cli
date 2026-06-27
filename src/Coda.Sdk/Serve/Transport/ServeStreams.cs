namespace Coda.Sdk.Serve.Transport;

/// <summary>The (input, output) stream pair a ServeHost consumes.</summary>
public readonly record struct ServeStreams(Stream Input, Stream Output);
