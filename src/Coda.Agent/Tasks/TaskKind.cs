namespace Coda.Agent.Tasks;

/// <summary>The kind of work a managed task represents.</summary>
public enum TaskKind
{
    Subagent,
    Shell,
    Scheduled,
}
