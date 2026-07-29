namespace Coda.Tui.Skills;

/// <summary>Specifies how a skill's body is executed when the skill fires.</summary>
public enum SkillContextMode
{
    /// <summary>The skill body is injected inline into the current conversation (default).</summary>
    Inline,

    /// <summary>The skill body runs in a forked subagent; only the subagent's final report
    /// returns to the main conversation, keeping the main context window clean.</summary>
    Fork,
}
