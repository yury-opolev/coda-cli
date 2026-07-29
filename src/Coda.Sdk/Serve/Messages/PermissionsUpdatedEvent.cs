using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// A <c>PermissionRequest</c> hook's <c>updatedPermissions</c> was applied to the live session:
/// a mode was changed and/or allow/deny rules were added. Emitted for auditability per §8 of the spec.
/// </summary>
/// <remarks>
/// Emitted only when something was actually mutated. A no-op update does not produce this event.
/// <c>modeApplied</c> is absent when only rules were added.
/// </remarks>
public sealed record PermissionsUpdatedEvent(
    [property: JsonPropertyName("hookCommand")] string HookCommand,
    [property: JsonPropertyName("modeApplied")] string? ModeApplied,
    [property: JsonPropertyName("addedAllow")] IReadOnlyList<string> AddedAllow,
    [property: JsonPropertyName("addedDeny")] IReadOnlyList<string> AddedDeny);
