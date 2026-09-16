using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coda.Client.Protocol;

/// <summary>
/// Parameters for <c>initialize</c>. Only <see cref="ProtocolVersion"/> is
/// required; the rest are additive and omitted when null. <see
/// cref="ClientCapabilities"/> opts into gated state events — absence means
/// legacy behaviour, never an error.
/// </summary>
public sealed record InitializeParams
{
    /// <summary>The wire protocol version. Always <c>"1"</c> for this contract.</summary>
    public string ProtocolVersion { get; init; } = ProtocolConstants.ProtocolVersion;

    public string? ClientInfo { get; init; }

    /// <summary>An LLM-provider credential, <b>not</b> a control-plane password.
    /// Rarely set by an orchestrator that lets the engine use its own configured
    /// credentials.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Resumes a validated saved session when supplied.</summary>
    public string? SessionId { get; init; }

    public ClientCapabilities? ClientCapabilities { get; init; }
}

/// <summary>
/// Optional capability negotiation from the client. Absence of the struct, or of
/// any field in it, means legacy behaviour: no gated event method is emitted.
/// </summary>
public sealed record ClientCapabilities
{
    /// <summary>Opt into the gated <c>event/*</c> state methods (lifecycle,
    /// turnEnded, requestPending, and the rest). The reference client sets this
    /// to true.</summary>
    public bool? StateEvents { get; init; }

    /// <summary>Reserved reader hint; it does not gate <c>session/getHistory</c>.</summary>
    public bool? RichHistory { get; init; }

    /// <summary>Reserved preference; the current engine does not negotiate an
    /// outgoing frame-size limit from it.</summary>
    public long? MaxEventPayloadBytes { get; init; }
}

/// <summary>
/// The <c>initialize</c> response, modelled tolerantly. The current Rust engine
/// always emits <see cref="ContractVersion"/>, <see cref="EngineInstanceId"/>,
/// <see cref="EventCursor"/> and <see cref="Capabilities"/>, but they are typed
/// as nullable so a legacy engine that omits them still parses. Read
/// <see cref="Capabilities"/> — not a product version — to decide what is
/// supported.
/// </summary>
public sealed record InitializeResult
{
    public string ProtocolVersion { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public string ServerInfo { get; init; } = string.Empty;

    public string? TelemetryLogPath { get; init; }

    public string? ContractVersion { get; init; }

    public string? EngineInstanceId { get; init; }

    /// <summary>The authoritative start cursor for <c>session/getEvents</c>, valid
    /// at the moment this call returned.</summary>
    public long? EventCursor { get; init; }

    public Dictionary<string, CapabilityEntry>? Capabilities { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }

    /// <summary>
    /// The runtime authority for whether a named capability is usable. Unknown
    /// (absent) is reported as unsupported <b>by this helper</b>, so a caller must
    /// distinguish "the engine said no" from "the engine never mentioned it" when
    /// that difference matters — see <see cref="TryGetCapability"/>.
    /// </summary>
    public bool IsCapabilitySupported(string capability) =>
        this.Capabilities is not null &&
        this.Capabilities.TryGetValue(capability, out CapabilityEntry? entry) &&
        entry.Supported;

    /// <summary>Looks up a capability entry, returning false when the engine never
    /// advertised it at all (as opposed to advertising it as unsupported).</summary>
    public bool TryGetCapability(string capability, out CapabilityEntry entry)
    {
        if (this.Capabilities is not null && this.Capabilities.TryGetValue(capability, out CapabilityEntry? found))
        {
            entry = found;
            return true;
        }

        entry = new CapabilityEntry();
        return false;
    }
}

/// <summary>One capability entry: whether a named capability is supported by this
/// engine build, with an explanatory reason when not.</summary>
public sealed record CapabilityEntry
{
    public bool Supported { get; init; }

    public string? Reason { get; init; }
}

/// <summary>Shared <c>{ "ok": true }</c> result used by <c>session/interrupt</c>
/// and <c>shutdown</c>.</summary>
public sealed record OkResult
{
    public bool Ok { get; init; }
}

/// <summary>Well-known protocol constants that are not tied to a build.</summary>
public static class ProtocolConstants
{
    /// <summary>The wire protocol version this client speaks.</summary>
    public const string ProtocolVersion = "1";
}
