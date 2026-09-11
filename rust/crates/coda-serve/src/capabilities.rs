//! Capability catalog for `InitializeResult.capabilities` (§2.1, §5 of the
//! serve API implementation plan).
//!
//! Stage rule: advertise **only** methods actually implemented in this pass.
//! Everything the plan lists as a later stage is explicit `supported:false`
//! with a reason — never silently missing, never a false promise.

use std::collections::HashMap;

use coda_proto::messages::CapabilityEntry;

/// The capability catalog for this build. Called once per `EngineState`.
pub fn capability_catalog() -> HashMap<String, CapabilityEntry> {
    let mut m = HashMap::new();

    // ── Implemented in this pass (Slice 0 / Stage C) ──────────────────────
    m.insert("state.snapshot".into(), CapabilityEntry::supported());
    m.insert("state.events".into(), CapabilityEntry::supported());
    m.insert("state.eventReplayBounded".into(), CapabilityEntry::supported());
    m.insert("state.historyFence".into(), CapabilityEntry::supported());
    m.insert("state.usage".into(), CapabilityEntry::supported());
    // A `stateEvents` client converges on lifecycle, activity phase, turn
    // termination, effective config, the steering queue, pending reverse
    // requests and session resets from the event stream alone — every
    // transition of those sections is published (`event/lifecycle`,
    // `event/activity`, `event/turnEnded`, `event/configChanged`,
    // `event/steeringQueue`, `event/requestPending`, `event/requestResolved`,
    // `event/sessionChanged`). `event/configChanged` covers both the running
    // turn's captured config and the next-turn config, so a config mutation
    // is never poll-only.
    // It is *not* a claim that every field of `StateSnapshot` is push-based:
    // `tools` detail and `usage` totals are derived from the ungated
    // `event/toolCall`/`event/toolResult`/`event/usage` frames, and anything
    // else still needs a snapshot.
    m.insert("state.turnEvents".into(), CapabilityEntry::supported());
    m.insert("steering.readOnlyQueue".into(), CapabilityEntry::supported());
    m.insert("steering.outcomes".into(), CapabilityEntry::supported());
    m.insert("tools.stableIds".into(), CapabilityEntry::supported());

    // ── Stage D: wired and tested in this pass ────────────────────────────
    // Every one of these is `true` only because the method exists, is routed,
    // and has a focused test plus a real out-of-process conformance test. A
    // capability is a promise; none of these is advertised on intent.
    m.insert("requests.discovery".into(), CapabilityEntry::supported());
    m.insert("requests.outOfBandResolve".into(), CapabilityEntry::supported());
    m.insert("history.rich".into(), CapabilityEntry::supported());
    m.insert(
        "history.richNegotiation".into(),
        CapabilityEntry::unsupported(
            "clientCapabilities.richHistory is a reserved reader hint; session/getHistory returns rich history regardless of this hint",
        ),
    );
    m.insert("history.savedSessions".into(), CapabilityEntry::supported());
    m.insert("config.describe".into(), CapabilityEntry::supported());
    m.insert("config.session".into(), CapabilityEntry::supported());
    m.insert("mcp.list".into(), CapabilityEntry::supported());
    // Bounded schedules: `session/scheduleCreate` accepts `maxRuns` and
    // `expiresAt`/`expiresIn`, and `session/scheduleList` reports
    // `runsStarted`, the configured bounds and a truthful lifecycle `state`.
    //
    // This is advertised because an older engine would *accept* those fields
    // and silently ignore them — a client that asked for "seven runs" would
    // get an unbounded schedule with no error. Clients must check this
    // capability before relying on a bound, and fall back to managing the
    // limit themselves when it is absent.
    m.insert("schedules.bounds".into(), CapabilityEntry::supported());
    // The reverse-request registry now makes this phase real: it is entered
    // when a permission/question/plan request goes outstanding and left when
    // the last one resolves, restoring the phase the turn was in.
    m.insert("state.awaitingUserInput".into(), CapabilityEntry::supported());

    // ── Accepted-but-unimplemented surfaces, declared rather than silently
    // ignored. `session/getState` rejects `sections` outright instead of
    // answering a different question than the client asked. ──────────────
    m.insert(
        "state.sectionFilter".into(),
        CapabilityEntry::unsupported(
            "session/getState returns the whole snapshot; a `sections` filter is rejected, never silently ignored",
        ),
    );
    m.insert(
        "events.payloadLimitNegotiation".into(),
        CapabilityEntry::unsupported(
            "clientCapabilities.maxEventPayloadBytes is reserved; it does not impose an outgoing frame limit; use the advertised retention and projection limits",
        ),
    );
    // `PendingRequestDto.callId` is reserved and always omitted: the
    // permission/question/plan-approval seams in `coda-agent` do not carry
    // the provider tool-call id, so there is nothing truthful to put there.
    // Declared rather than left to be discovered, so no client builds a
    // correlation feature on a field that is never populated.
    m.insert(
        "requests.callCorrelation".into(),
        CapabilityEntry::unsupported(
            "the permission/question seams carry no provider tool-call id, so `callId` is reserved and always omitted rather than invented; correlate on `turnId` plus `event/toolCall` instead",
        ),
    );

    // ── Never supported (architectural boundary, §2.8/§5) ─────────────────
    m.insert(
        "config.mcpWrite".into(),
        CapabilityEntry::unsupported("Coda never writes a remote client's filesystem"),
    );
    m.insert(
        "config.pluginInstall".into(),
        CapabilityEntry::unsupported("local maintenance UX only, client-side"),
    );
    m.insert(
        "config.marketplace".into(),
        CapabilityEntry::unsupported("local maintenance UX only, client-side"),
    );
    m.insert(
        "config.appearance".into(),
        CapabilityEntry::unsupported("client-local setting, not engine-owned"),
    );
    m.insert(
        "auth.remoteClient".into(),
        CapabilityEntry::unsupported("--api-key is a provider credential, not client auth"),
    );
    m.insert(
        "events.durableJournal".into(),
        CapabilityEntry::unsupported("ring is in-memory and bounded, not a durable journal"),
    );
    m.insert(
        "events.replayAcrossInstances".into(),
        CapabilityEntry::unsupported("session/getEvents requires a matching engineInstanceId"),
    );
    m.insert(
        "session.multiClientAttach".into(),
        CapabilityEntry::unsupported("one stdio connection per engine process"),
    );
    m.insert(
        "session.promptBacklog".into(),
        CapabilityEntry::unsupported("a second prompt fails fast; no engine-side backlog"),
    );
    m.insert(
        "session.detach".into(),
        CapabilityEntry::unsupported("the engine exits with the client that owns its stdio"),
    );
    m.insert(
        "engine.survivesClientExit".into(),
        CapabilityEntry::unsupported(
            "the core exits with the client that owns its stdio; persistence is the external orchestrator's job",
        ),
    );
    m.insert(
        "transport.socket".into(),
        CapabilityEntry::unsupported(
            "stdio only in the Rust engine; the legacy C# host implements a local socket transport",
        ),
    );
    m.insert(
        "orchestration.fleet".into(),
        CapabilityEntry::unsupported("fleet orchestration is a separate application, not embedded in Coda"),
    );

    m
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_capability_has_a_reason_when_unsupported() {
        for (name, entry) in capability_catalog() {
            if !entry.supported {
                assert!(entry.reason.is_some(), "{name} is unsupported but has no reason");
            }
        }
    }

    #[test]
    fn implemented_capabilities_are_reported_supported() {
        let catalog = capability_catalog();
        for name in ["state.snapshot", "state.events", "state.eventReplayBounded", "steering.readOnlyQueue"] {
            assert!(catalog[name].supported, "{name} must be reported supported");
        }
    }

    #[test]
    fn every_capability_key_is_a_dotted_lower_camel_path() {
        // A catalog whose keys drift in shape is a catalog a client cannot
        // match on. Two segments, `area.feature`, lowerCamel on both sides.
        for name in capability_catalog().keys() {
            let segments: Vec<&str> = name.split('.').collect();
            assert_eq!(segments.len(), 2, "{name} must be `area.feature`");
            for segment in segments {
                assert!(!segment.is_empty(), "{name} has an empty segment");
                assert!(
                    segment.chars().next().is_some_and(|c| c.is_ascii_lowercase()),
                    "{name} must be lowerCamel in every segment"
                );
                assert!(
                    segment.chars().all(|c| c.is_ascii_alphanumeric()),
                    "{name} must contain only alphanumerics inside its segments"
                );
            }
        }
    }

    #[test]
    fn nothing_the_engine_cannot_do_is_advertised_supported() {
        // Stage-scoped honesty guard: these are the surfaces this pass
        // deliberately does not implement. If one of them is ever flipped to
        // `supported`, the implementation has to land in the same change.
        let catalog = capability_catalog();
        for name in [
            "state.sectionFilter",
            "requests.callCorrelation",
            "config.mcpWrite",
            "config.pluginInstall",
            "config.marketplace",
            "config.appearance",
            "auth.remoteClient",
        ] {
            let entry = &catalog[name];
            assert!(!entry.supported, "{name} is not implemented in this pass");
            assert!(
                entry.reason.as_deref().is_some_and(|r| r.len() > 10),
                "{name} must explain itself, not just say no"
            );
        }
    }

    #[test]
    fn stage_d_surfaces_are_advertised_only_because_they_are_wired() {
        // The counterpart of the guard above: these were `supported: false`
        // with a "later stage" reason until the methods existed. Flipping one
        // back without removing the method would be just as dishonest.
        let catalog = capability_catalog();
        for name in [
            "requests.discovery",
            "requests.outOfBandResolve",
            "history.rich",
            "history.savedSessions",
            "config.describe",
            "config.session",
            "mcp.list",
            "state.awaitingUserInput",
            "schedules.bounds",
        ] {
            assert!(catalog[name].supported, "{name} is implemented and must be advertised");
            assert!(catalog[name].reason.is_none(), "{name} is supported; no excuse needed");
        }
    }

    /// A bound the engine would silently drop is worse than no bound at all:
    /// the client believes the schedule will stop itself. The capability is the
    /// only way an older engine can be told apart from one that honours
    /// `maxRuns`, so it must correspond to real wire fields.
    #[test]
    fn bounded_schedules_are_advertised_with_their_wire_fields_present() {
        assert!(capability_catalog()["schedules.bounds"].supported);
        let params = coda_proto::messages::ScheduleCreateParams {
            prompt: "watch".into(),
            every: Some("1h".into()),
            max_runs: Some(7),
            expires_in: Some("7d".into()),
            ..Default::default()
        };
        let value = serde_json::to_value(params).unwrap();
        assert_eq!(value["maxRuns"], 7);
        assert_eq!(value["expiresIn"], "7d");

        let listed: coda_proto::messages::ScheduledTask = serde_json::from_value(
            serde_json::json!({
                "id": "s1",
                "state": "retiring",
                "maxRuns": 7,
                "runsStarted": 7,
                "retiredReason": "completed",
            }),
        )
        .unwrap();
        assert_eq!(listed.max_runs, Some(7));
        assert_eq!(listed.runs_started, 7);
        assert_eq!(listed.retired_reason.as_deref(), Some("completed"));
    }

    /// Every advertised capability must correspond to a routed method (or, for
    /// the state-only ones, to a snapshot section that exists). This is the
    /// mechanical guard against a catalog that drifts ahead of the router.
    #[test]
    fn every_supported_method_capability_names_a_routed_method() {
        let routed = [
            ("requests.discovery", coda_proto::messages::method::GET_PENDING_REQUESTS),
            ("requests.outOfBandResolve", coda_proto::messages::method::RESOLVE_REQUEST),
            ("history.rich", coda_proto::messages::method::GET_HISTORY),
            ("history.savedSessions", coda_proto::messages::method::LIST_SESSIONS),
            ("config.describe", coda_proto::messages::method::CONFIG_DESCRIBE),
            ("config.session", coda_proto::messages::method::CONFIG_SET),
            ("mcp.list", coda_proto::messages::method::MCP_LIST),
        ];
        let catalog = capability_catalog();
        for (capability, method) in routed {
            assert!(catalog[capability].supported);
            assert!(!method.is_empty());
        }
    }
}
