namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// <c>event/streamProgress</c> payload — a liveness pulse emitted while an LLM turn
/// streams, so the orchestrator can tell "mid-LLM-call, working" from "hung". The Bridge
/// stamps its own receipt time (it drives the idle watchdog), so no timestamp is carried.
/// <paramref name="Phase"/> is <c>"first-token"</c>, <c>"progress"</c>, or <c>"complete"</c>.
/// </summary>
public sealed record StreamProgressEvent(string Phase, int Chunks, int Chars, long ElapsedMs);
