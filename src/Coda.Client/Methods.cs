namespace Coda.Client;

/// <summary>
/// The routed method names this client calls. Centralised so a call site cannot
/// drift from the wire spelling, and so the raw escape hatch and the typed
/// helpers agree on the same strings.
/// </summary>
public static class Methods
{
    public const string Initialize = "initialize";
    public const string Shutdown = "shutdown";

    public const string Prompt = "session/prompt";
    public const string Interrupt = "session/interrupt";
    public const string SetGoal = "session/setGoal";
    public const string GetState = "session/getState";
    public const string GetEvents = "session/getEvents";
    public const string GetHistory = "session/getHistory";
    public const string GetPendingRequests = "session/getPendingRequests";
    public const string ResolveRequest = "session/resolveRequest";
    public const string CancelRequest = "session/cancelRequest";
}
