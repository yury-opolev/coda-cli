namespace Coda.Agent.OutputStyles;

/// <summary>A named persona that appends guidance to the system prompt.</summary>
public sealed record OutputStyle(string Name, string Description, string SystemPromptSuffix);
