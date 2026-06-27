namespace Coda.Tui.Skills;

/// <summary>A discovered skill with its name, description, and body prompt.</summary>
public sealed record SkillDefinition(string Name, string Description, string Body);
