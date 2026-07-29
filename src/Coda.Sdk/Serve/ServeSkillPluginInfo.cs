namespace Coda.Sdk.Serve;

/// <summary>
/// Wire-facing view of a discovered skill for the <c>skills/list</c> serve method. Populated by a
/// provider delegate injected into <see cref="ServeHost"/> from the TUI layer (which owns skill
/// loading), so the SDK does not take a dependency on the TUI project.
/// </summary>
public sealed record ServeSkillInfo(
    string Name,
    string Description,
    string Origin,
    bool Enabled,
    bool UserInvocable,
    string? SourcePath,
    string? ArgumentHint);

/// <summary>
/// Wire-facing view of a discovered plugin for the <c>plugins/list</c> serve method. Populated by a
/// provider delegate injected into <see cref="ServeHost"/> from the TUI layer.
/// </summary>
public sealed record ServePluginInfo(
    string Name,
    string Version,
    bool Enabled,
    bool Trusted,
    bool IsExternal);
