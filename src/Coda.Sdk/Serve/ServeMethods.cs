namespace Coda.Sdk.Serve;

public static class ServeMethods
{
    public const string ProtocolVersion = "1";

    // Requests (orchestrator → Coda)
    public const string Initialize = "initialize";
    public const string Prompt = "session/prompt";
    public const string Interrupt = "session/interrupt";
    public const string Steer = "session/steer";
    public const string RecallSteering = "session/recallSteering";
    public const string History = "session/history";
    public const string Messages = "session/messages";
    public const string Models = "session/models";
    public const string SetGoal = "session/setGoal";
    public const string ReasoningCapability = "model/reasoningCapability";
    public const string SetEffort = "session/setEffort";
    public const string ScheduleList = "session/scheduleList";
    public const string ScheduleCreate = "session/scheduleCreate";
    public const string ScheduleDelete = "session/scheduleDelete";
    public const string Shutdown = "shutdown";

    // Events / notifications (Coda → orchestrator)
    public const string EventAssistantText = "event/assistantText";
    public const string EventAssistantTextComplete = "event/assistantTextComplete";
    public const string EventToolCall = "event/toolCall";
    public const string EventToolResult = "event/toolResult";
    public const string EventError = "event/error";
    public const string EventLimitReached = "event/limitReached";
    public const string EventSteeringDelivered = "event/steeringDelivered";
    public const string EventStop = "event/stop";
    public const string EventUsage = "event/usage";
    public const string EventTurnComplete = "event/turnComplete";
    public const string EventStreamProgress = "event/streamProgress";
    public const string EventToolProgress = "event/toolProgress";
    public const string EventScheduleLifecycle = "event/scheduleLifecycle";
    public const string EventThinking = "event/thinking";
    public const string EventThinkingComplete = "event/thinkingComplete";
    public const string EventPromptRewritten = "event/promptRewritten";
    public const string EventResponseRewritten = "event/responseRewritten";
    public const string EventToolInputModified = "event/toolInputModified";
    public const string EventToolResultModified = "event/toolResultModified";
    public const string EventPermissionDecided = "event/permissionDecided";
    public const string EventPermissionsUpdated = "event/permissionsUpdated";
    public const string EventTaskCompleted = "event/taskCompleted";
    public const string EventSubagentBlocked = "event/subagentBlocked";
    public const string EventSubagentResultModified = "event/subagentResultModified";
    public const string EventCompactionCancelled = "event/compactionCancelled";
    public const string EventPostCompactContextInjected = "event/postCompactContextInjected";

    // Server-initiated requests (Coda → orchestrator)
    public const string RequestPermission = "request/permission";
    public const string RequestQuestion = "request/question";
    public const string RequestPlanApproval = "request/planApproval";

    // Hook management (orchestrator → Coda)
    public const string HookList = "hooks/list";
    public const string HookInfo = "hooks/info";
    public const string HookTrust = "hooks/trust";

    // Skills / plugins management (orchestrator → Coda)
    public const string SkillList = "skills/list";
    public const string PluginList = "plugins/list";
    public const string SkillTrust = "skills/trust";
}
