namespace Coda.Sdk;

/// <summary>Lightweight description of a persisted session, returned by <see cref="SessionTranscriptStore.ListAsync"/>.</summary>
public sealed record SessionSummary(string Id, DateTime CreatedUtc, int MessageCount, string Preview);
