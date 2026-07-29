using Microsoft.Extensions.Logging;

namespace Coda.Tui.Skills;

/// <summary>
/// Builds the set of first-class slash commands derived from user-invocable skills,
/// applying the name-collision policy against the current built-in command surface.
/// Registration is computed once at composition time (or on explicit reload) — never
/// per keystroke — so the cost is paid upfront, not during interactive use.
/// </summary>
public static partial class SkillCommandRegistrar
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skill '{SkillName}' (source: {SourcePath}) collides with built-in command " +
                  "'{BuiltInName}' and will not receive a /{SkillName} entry. " +
                  "Invoke it with /skill {SkillName} instead.")]
    private static partial void LogCollision(
        ILogger logger,
        string skillName,
        string? sourcePath,
        string builtInName);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skill '{SkillName}' (source: {SourcePath}) has an invalid name and will not " +
                  "receive a /{SkillName} entry: {Reason}. " +
                  "Invoke it with /skill {SkillName} instead.")]
    private static partial void LogInvalidName(
        ILogger logger,
        string skillName,
        string? sourcePath,
        string reason);

    /// <summary>
    /// Returns one <see cref="Coda.Tui.Commands.SkillSlashCommand"/> per skill in
    /// <paramref name="skills"/> that passes both tests: the skill must be user-invocable
    /// (<see cref="Coda.Tui.Skills.SkillDefinition.UserInvocable"/> is <see langword="true"/>)
    /// and its name must not collide with any built-in command name or alias. Collisions are
    /// logged as warnings so the skill author can see why their <c>/name</c> did not appear.
    /// </summary>
    /// <param name="skills">
    /// All discovered, precedence-resolved skills. Skills hidden from the model by
    /// <c>paths</c> or <c>disable-model-invocation</c> are still accepted here — those flags
    /// govern model visibility, not user invocability.
    /// </param>
    /// <param name="builtIns">
    /// The compiled slash commands whose names and aliases define the collision namespace.
    /// </param>
    /// <param name="logger">Optional logger for collision warnings.</param>
    public static IReadOnlyList<Coda.Tui.Repl.ISlashCommand> BuildSkillCommands(
        IReadOnlyList<SkillDefinition> skills,
        IReadOnlyList<Coda.Tui.Repl.ISlashCommand> builtIns,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(builtIns);

        // Index every built-in name and alias for O(1) collision checks (case-insensitive).
        var builtInKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in builtIns)
        {
            builtInKeys.Add(cmd.Name);
            foreach (var alias in cmd.Aliases)
            {
                builtInKeys.Add(alias);
            }
        }

        var result = new List<Coda.Tui.Repl.ISlashCommand>();
        foreach (var skill in skills)
        {
            if (!skill.UserInvocable)
            {
                continue;
            }

            // M1: Reject names that CommandParser.Parse can never reach: empty/whitespace,
            // containing internal whitespace (only the first token is the command name), or
            // starting with '/' (the parser strips the leading slash, making the resolved
            // name start with '/' which matches nothing in the registry).
            if (string.IsNullOrWhiteSpace(skill.Name))
            {
                continue; // no usable name to log
            }

            if (skill.Name[0] == '/')
            {
                if (logger is not null)
                {
                    LogInvalidName(logger, skill.Name, skill.SourcePath, "name starts with '/'");
                }

                continue;
            }

            if (skill.Name.Any(static c => char.IsWhiteSpace(c)))
            {
                if (logger is not null)
                {
                    LogInvalidName(logger, skill.Name, skill.SourcePath, "name contains whitespace");
                }

                continue;
            }

            if (builtInKeys.Contains(skill.Name))
            {
                if (logger is not null)
                {
                    LogCollision(
                        logger,
                        skill.Name,
                        skill.SourcePath,
                        FindCollidingBuiltIn(builtIns, skill.Name));
                }

                continue;
            }

            result.Add(new Coda.Tui.Commands.SkillSlashCommand(skill));
        }

        return result;
    }

    /// <summary>
    /// Returns the canonical name of the built-in command whose name or alias matches
    /// <paramref name="skillName"/>. Falls back to <paramref name="skillName"/> itself when
    /// nothing matches (should not happen in practice since the caller already verified a collision).
    /// </summary>
    private static string FindCollidingBuiltIn(
        IReadOnlyList<Coda.Tui.Repl.ISlashCommand> builtIns,
        string skillName)
    {
        foreach (var cmd in builtIns)
        {
            if (string.Equals(cmd.Name, skillName, StringComparison.OrdinalIgnoreCase))
            {
                return cmd.Name;
            }

            if (cmd.Aliases.Any(a => string.Equals(a, skillName, StringComparison.OrdinalIgnoreCase)))
            {
                return cmd.Name;
            }
        }

        return skillName;
    }
}
