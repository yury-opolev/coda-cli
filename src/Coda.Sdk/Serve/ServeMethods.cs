namespace Coda.Sdk.Serve;

public static class ServeMethods
{
    public const string ProtocolVersion = "1";

    // Requests (orchestrator → Coda)
    public const string Initialize = "initialize";
    public const string Prompt = "session/prompt";
    public const string Interrupt = "session/interrupt";
    public const string Steer = "session/steer";
    public const string History = "session/history";
    public const string Messages = "session/messages";
    public const string Models = "session/models";
    public const string SetGoal = "session/setGoal";
    public const string Shutdown = "shutdown";

    // Events / notifications (Coda → orchestrator)
    public const string EventAssistantText = "event/assistantText";
    public const string EventAssistantTextComplete = "event/assistantTextComplete";
    public const string EventToolCall = "event/toolCall";
    public const string EventToolResult = "event/toolResult";
    public const string EventError = "event/error";
    public const string EventLimitReached = "event/limitReached";
    public const string EventStop = "event/stop";
    public const string EventUsage = "event/usage";
    public const string EventTurnComplete = "event/turnComplete";
    public const string EventStreamProgress = "event/streamProgress";
    public const string EventToolProgress = "event/toolProgress";

    // Server-initiated requests (Coda → orchestrator)
    public const string RequestPermission = "request/permission";
    public const string RequestQuestion = "request/question";
    public const string RequestPlanApproval = "request/planApproval";
}
