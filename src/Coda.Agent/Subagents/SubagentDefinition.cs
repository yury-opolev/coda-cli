namespace Coda.Agent.Subagents;

/// <summary>
/// Describes a named subagent type: its system-prompt body, and whether it is
/// restricted to read-only tools.
/// </summary>
public sealed record SubagentDefinition(
    string Type,
    string Description,
    string SystemPromptBody,
    bool ReadOnlyToolsOnly);
