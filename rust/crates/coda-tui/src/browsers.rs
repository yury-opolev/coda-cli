//! Concrete browsers built on the shared [`crate::overlay::Browser`].
//!
//! Each function maps a wire result onto columns and rows. Column widths and
//! footer hints mirror the C# overlays so the layout is familiar.

use std::collections::BTreeMap;

use chrono::{DateTime, Utc};
use coda_agent::SessionSummary;
use coda_proto::messages::{
    ScheduledTask, WireHook, WireModel, WirePlugin, WireSkill,
};

use crate::config::{McpServer, TaskLog};
use crate::overlay::{Browser, Column, Item};

/// Status glyphs shared across browsers.
mod glyph {
    /// Enabled, running, or otherwise healthy.
    pub const ACTIVE: &str = crate::render::glyphs::DOT;       // ●
    /// Disabled or idle.
    pub const INACTIVE: &str = crate::render::glyphs::DOT_HOLLOW; // ○
    /// Failed or blocked.
    pub const BLOCKED: &str = crate::render::glyphs::CROSS;    // ✗
    /// Neither active nor failed.
    pub const OTHER: &str = crate::render::glyphs::SQUARE;     // ■
    /// The current selection, e.g. the active model.
    pub const CURRENT: &str = crate::render::glyphs::CHECK;    // ✓
}

/// Formats a context window as a compact token count, e.g. `200K`.
fn context_size(limit: Option<i64>) -> String {
    match limit {
        Some(limit) if limit >= 1_000_000 => format!("{}M", limit / 1_000_000),
        Some(limit) if limit >= 1_000 => format!("{}K", limit / 1_000),
        Some(limit) => limit.to_string(),
        None => String::new(),
    }
}

fn yes_no(value: bool) -> &'static str {
    if value {
        "yes"
    } else {
        "no"
    }
}

/// The model picker.
pub fn models(models: &[WireModel], current: Option<&str>, source: &str) -> Browser {
    let mut browser = Browser::new(
        format!("Models — {} models — {source}", models.len()),
        vec![
            Column::new("", 1),
            Column::new("id", 40),
            Column::new("name", 30),
            Column::new("context", 7),
            Column::new("effort", 24),
        ],
    )
    .with_footer("↑/↓ k/j move · Enter select · r reload · / filter · Esc q close")
    .without_detail();

    browser.set_items(
        models
            .iter()
            .map(|model| {
                let is_current = current.is_some_and(|id| id == model.id);
                Item::new(
                    &model.id,
                    vec![
                        if is_current { glyph::CURRENT } else { " " }.to_string(),
                        model.id.clone(),
                        model.display_name.clone().unwrap_or_default(),
                        context_size(model.context_limit),
                        String::new(),
                    ],
                )
            })
            .collect(),
    );
    browser
}

/// The scheduled-task browser. List only; the C# has no detail pane here.
pub fn schedules(tasks: &[ScheduledTask]) -> Browser {
    let mut browser = Browser::new(
        format!("Schedules — {}", tasks.len()),
        vec![
            Column::new("", 1),
            Column::new("id", 14),
            Column::new("name", 16),
            Column::new("rule", 14),
            Column::new("tz", 8),
            Column::new("next run", 16),
            Column::new("state", 12),
        ],
    )
    .with_footer("↑/↓ k/j move · d delete · n new · r reload · / filter · Esc q close")
    .with_extra_keys(&['d', 'n'])
    .without_detail();

    browser.set_items(
        tasks
            .iter()
            .map(|task| {
                let status = match task.state.as_str() {
                    "running" => glyph::ACTIVE,
                    "pending" => glyph::INACTIVE,
                    _ => glyph::OTHER,
                };
                Item::new(
                    &task.id,
                    vec![
                        status.to_string(),
                        task.id.clone(),
                        task.name.clone().unwrap_or_default(),
                        task.rule.clone(),
                        task.time_zone.clone().unwrap_or_default(),
                        task.next_run_utc.clone().unwrap_or_default(),
                        task.state.clone(),
                    ],
                )
            })
            .collect(),
    );
    browser
}

/// The skill browser.
pub fn skills(skills: &[WireSkill]) -> Browser {
    let mut browser = Browser::new(
        format!("Skills — {}", skills.len()),
        vec![
            Column::new("", 1),
            Column::new("name", 24),
            Column::new("kind", 12),
            Column::new("description", 40),
        ],
    )
    .with_footer("↑/↓ k/j move · Enter detail · Space toggle · r reload · / filter · Esc q close");

    browser.set_items(
        skills
            .iter()
            .map(|skill| {
                let kind = if skill.user_invocable {
                    "invocable"
                } else {
                    "model-only"
                };
                let mut detail = vec![
                    format!("name           {}", skill.name),
                    format!("user-invocable {}", yes_no(skill.user_invocable)),
                    format!("enabled        {}", yes_no(skill.enabled)),
                ];
                if let Some(origin) = &skill.origin {
                    detail.push(format!("origin         {origin}"));
                }
                if let Some(path) = &skill.source_path {
                    detail.push(format!("path           {path}"));
                }
                if let Some(hint) = &skill.argument_hint {
                    detail.push(format!("arguments      {hint}"));
                }
                if let Some(description) = &skill.description {
                    detail.push(String::new());
                    detail.push(description.clone());
                }

                Item::new(
                    &skill.name,
                    vec![
                        if skill.enabled { glyph::ACTIVE } else { glyph::INACTIVE }.to_string(),
                        skill.name.clone(),
                        kind.to_string(),
                        skill.description.clone().unwrap_or_default(),
                    ],
                )
                .with_detail(detail)
            })
            .collect(),
    );
    browser
}

/// The plugin browser.
pub fn plugins(plugins: &[WirePlugin]) -> Browser {
    let mut browser = Browser::new(
        format!("Plugins — {}", plugins.len()),
        vec![
            Column::new("", 1),
            Column::new("name", 24),
            Column::new("version", 10),
            Column::new("scope", 9),
            Column::new("trust", 7),
        ],
    )
    .with_footer(
        "↑/↓ k/j move · Enter detail · Space toggle · u update · r reload · / filter · Esc q close",
    )
    .with_extra_keys(&['u']);

    browser.set_items(
        plugins
            .iter()
            .map(|plugin| {
                let status = if !plugin.trusted {
                    glyph::BLOCKED
                } else if plugin.enabled {
                    glyph::ACTIVE
                } else {
                    glyph::INACTIVE
                };
                let detail = vec![
                    format!("name     {}", plugin.name),
                    format!("version  {}", plugin.version.clone().unwrap_or_default()),
                    format!("enabled  {}", yes_no(plugin.enabled)),
                    format!("trusted  {}", yes_no(plugin.trusted)),
                    format!("external {}", yes_no(plugin.is_external)),
                ];

                Item::new(
                    &plugin.name,
                    vec![
                        status.to_string(),
                        plugin.name.clone(),
                        plugin.version.clone().unwrap_or_default(),
                        if plugin.is_external { "external" } else { "local" }.to_string(),
                        if plugin.trusted { "trusted" } else { "blocked" }.to_string(),
                    ],
                )
                .with_detail(detail)
            })
            .collect(),
    );
    browser
}

/// The hook browser.
pub fn hooks(hooks: &[WireHook]) -> Browser {
    let mut browser = Browser::new(
        format!("Hooks — {}", hooks.len()),
        vec![
            Column::new("", 1),
            Column::new("event", 22),
            Column::new("handler", 10),
            Column::new("matcher", 16),
            Column::new("scope", 9),
        ],
    )
    .with_footer("↑/↓ k/j move · Enter detail · r reload · / filter · Esc q close");

    browser.set_items(
        hooks
            .iter()
            .map(|hook| {
                let detail = vec![
                    format!("index    {}", hook.index),
                    format!("event    {}", hook.event),
                    format!(
                        "handler  {}",
                        hook.handler_type.clone().unwrap_or_default()
                    ),
                    format!("matcher  {}", hook.matcher.clone().unwrap_or_default()),
                    format!("scope    {}", hook.scope.clone().unwrap_or_default()),
                    format!("enabled  {}", yes_no(hook.enabled)),
                ];

                Item::new(
                    hook.index.to_string(),
                    vec![
                        if hook.enabled { glyph::ACTIVE } else { glyph::INACTIVE }.to_string(),
                        hook.event.clone(),
                        hook.handler_type.clone().unwrap_or_default(),
                        hook.matcher.clone().unwrap_or_default(),
                        hook.scope.clone().unwrap_or_default(),
                    ],
                )
                .with_detail(detail)
            })
            .collect(),
    );
    browser
}

/// The MCP server browser.
///
/// Configuration comes from the local `.mcp.json` files. Live connection
/// status needs the engine, so the status column reflects configuration only.
pub fn mcp(servers: &[McpServer]) -> Browser {
    let mut browser = Browser::new(
        format!("MCP servers — {}", servers.len()),
        vec![
            Column::new("", 1),
            Column::new("name", 25),
            Column::new("transport", 9),
            Column::new("scope", 7),
            Column::new("target", 40),
        ],
    )
    .with_footer("↑/↓ k/j move · Enter detail · Space toggle · n new · e edit · d delete · r reload · Esc q close")
    .with_extra_keys(&['n', 'e', 'd']);

    browser.set_items(
        servers
            .iter()
            .map(|server| {
                let mut detail = vec![
                    format!("name       {}", server.name),
                    format!("scope      {}", server.scope.label()),
                    format!("transport  {}", server.transport),
                    format!("enabled    {}", yes_no(server.enabled)),
                ];
                if let Some(command) = &server.command {
                    detail.push(format!("command    {command}"));
                }
                if !server.args.is_empty() {
                    detail.push(format!("arguments  {}", server.args.join(" ")));
                }
                if let Some(url) = &server.url {
                    detail.push(format!("url        {url}"));
                }
                if !server.env_keys.is_empty() {
                    // Names only: values routinely hold tokens.
                    detail.push(format!("env        {}", server.env_keys.join(", ")));
                }

                Item::new(
                    &server.name,
                    vec![
                        if server.enabled { glyph::ACTIVE } else { glyph::INACTIVE }.to_string(),
                        server.name.clone(),
                        server.transport.to_string(),
                        server.scope.label().to_string(),
                        server.target(),
                    ],
                )
                .with_detail(detail)
            })
            .collect(),
    );
    browser
}

/// The background task browser.
///
/// Tasks are in-process engine state, but the runtime persists a log per task
/// under `~/.coda/task-logs/<session>/`, and `event/taskCompleted` reports
/// outcomes live. Together those give a usable listing without the engine
/// exposing a task-listing method.
pub fn tasks(logs: &[TaskLog], outcomes: &BTreeMap<String, TaskOutcome>) -> Browser {
    let mut browser = Browser::new(
        format!("Tasks — {}", logs.len()),
        vec![
            Column::new("", 1),
            Column::new("id", 16),
            Column::new("status", 10),
            Column::new("size", 8),
            Column::new("description", 44),
        ],
    )
    .with_footer("↑/↓ k/j move · Enter output · r reload · / filter · Esc q close");

    browser.set_items(
        logs.iter()
            .map(|log| {
                let outcome = outcomes.get(&log.id);
                let status = outcome.map(|o| o.status.as_str()).unwrap_or("");
                let glyph = match status {
                    "completed" => glyph::ACTIVE,
                    "failed" => glyph::BLOCKED,
                    "stopped" => glyph::OTHER,
                    _ => glyph::INACTIVE,
                };
                let description = outcome.map(|o| o.description.as_str()).unwrap_or("");

                let mut detail = vec![
                    format!("id          {}", log.id),
                    format!("session     {}", log.session_id),
                    format!("status      {}", if status.is_empty() { "unknown" } else { status }),
                    format!("log         {}", log.path.display()),
                    format!("size        {}", human_size(log.size_bytes)),
                ];
                if let Some(report) = outcome.and_then(|o| o.report.as_deref()) {
                    detail.push(String::new());
                    detail.push("report".to_string());
                    detail.extend(report.lines().map(str::to_string));
                }
                detail.push(String::new());
                detail.push("output (tail)".to_string());
                detail.extend(crate::config::read_task_log_tail(&log.path, 200));

                // Task ids restart per session, so the row id must include the
                // session or two rows would be indistinguishable.
                Item::new(
                    format!("{}/{}", log.session_id, log.id),
                    vec![
                        glyph.to_string(),
                        log.id.clone(),
                        if status.is_empty() { "unknown" } else { status }.to_string(),
                        human_size(log.size_bytes),
                        description.to_string(),
                    ],
                )
                .with_detail(detail)
            })
            .collect(),
    );
    browser
}

/// The session picker for `/resume`.
///
/// Sessions are listed newest-first (the order `SessionTranscriptStore::list`
/// returns them).  Each row shows a 1-based index, the session id, message
/// count, age, and a short preview of the first user message.  Enter on a
/// row fires `Intent::Activate(session_id)` which the host uses to resume.
pub fn sessions(summaries: &[SessionSummary]) -> Browser {
    let mut browser = Browser::new(
        format!("Sessions — {}", summaries.len()),
        vec![
            Column::new("#", 3),
            Column::new("id", 14),
            Column::new("msgs", 4),
            Column::new("age", 8),
            Column::new("preview", 60),
        ],
    )
    .with_footer("↑/↓ k/j move · Enter resume · / filter · Esc q close")
    .without_detail();

    browser.set_items(
        summaries
            .iter()
            .enumerate()
            .map(|(i, s)| {
                let age = format_session_age(s.created_utc);
                let preview = if s.preview.len() > 60 {
                    format!("{}…", &s.preview[..60])
                } else {
                    s.preview.clone()
                };
                Item::new(
                    &s.id,
                    vec![
                        (i + 1).to_string(),
                        s.id.clone(),
                        s.message_count.to_string(),
                        age,
                        preview,
                    ],
                )
            })
            .collect(),
    );
    browser
}

/// Formats the age of a session relative to now.
///
/// Mirrors C# `ResumeCommand.FormatAge`: "just now" / "Nm ago" / "Nh ago" / "Nd ago".
pub fn format_session_age(created_utc: DateTime<Utc>) -> String {
    let age = Utc::now() - created_utc;
    let secs = age.num_seconds().max(0);
    if secs < 60 {
        "just now".to_string()
    } else if secs < 3600 {
        format!("{}m ago", secs / 60)
    } else if secs < 86400 {
        format!("{}h ago", secs / 3600)
    } else {
        format!("{}d ago", secs / 86400)
    }
}

/// What the engine reported about a finished task.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TaskOutcome {
    pub status: String,
    pub description: String,
    pub report: Option<String>,
}

/// Formats a byte count for a narrow column.
fn human_size(bytes: u64) -> String {
    if bytes >= 1024 * 1024 {
        format!("{:.1}M", bytes as f64 / (1024.0 * 1024.0))
    } else if bytes >= 1024 {
        format!("{}K", bytes / 1024)
    } else {
        format!("{bytes}B")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn model(id: &str, name: Option<&str>, limit: Option<i64>) -> WireModel {
        serde_json::from_value(json!({
            "id": id,
            "displayName": name,
            "contextLimit": limit
        }))
        .expect("model")
    }

    #[test]
    fn formats_context_sizes_compactly() {
        assert_eq!(context_size(Some(200_000)), "200K");
        assert_eq!(context_size(Some(1_000_000)), "1M");
        assert_eq!(context_size(Some(512)), "512");
        assert_eq!(context_size(None), "");
    }

    #[test]
    fn the_model_browser_lists_every_model() {
        let models_list = [
            model("a", Some("Model A"), Some(200_000)),
            model("b", None, None),
        ];
        let browser = models(&models_list, None, "live");

        assert_eq!(browser.len(), 2);
        assert!(browser.title().contains("2 models"));
        assert!(browser.title().contains("live"));
    }

    #[test]
    fn the_model_browser_marks_the_current_model() {
        let models_list = [model("a", None, None), model("b", None, None)];
        let browser = models(&models_list, Some("b"), "live");

        let rows = browser.visible_items();
        assert_eq!(rows[0].cells[0], " ");
        assert_eq!(rows[1].cells[0], glyph::CURRENT);
    }

    #[test]
    fn the_model_browser_activates_rather_than_opening_a_detail_pane() {
        use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
        let mut browser = models(&[model("a", None, None)], None, "live");
        let intent = browser.handle(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
        assert_eq!(intent, crate::overlay::Intent::Activate("a".into()));
    }

    #[test]
    fn the_schedule_browser_maps_state_to_a_glyph() {
        let tasks: Vec<ScheduledTask> = serde_json::from_value(json!([
            { "id": "s1", "state": "running", "rule": "every 5m" },
            { "id": "s2", "state": "pending", "rule": "cron" },
            { "id": "s3", "state": "idle", "rule": "at" }
        ]))
        .expect("tasks");

        let browser = schedules(&tasks);
        let rows = browser.visible_items();
        assert_eq!(rows[0].cells[0], glyph::ACTIVE);
        assert_eq!(rows[1].cells[0], glyph::INACTIVE);
        assert_eq!(rows[2].cells[0], glyph::OTHER);
    }

    #[test]
    fn the_schedule_browser_binds_delete_and_new() {
        use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
        let tasks: Vec<ScheduledTask> =
            serde_json::from_value(json!([{ "id": "s1", "state": "idle" }])).expect("tasks");
        let mut browser = schedules(&tasks);

        assert_eq!(
            browser.handle(KeyEvent::new(KeyCode::Char('d'), KeyModifiers::NONE)),
            crate::overlay::Intent::Key('d', Some("s1".into()))
        );
        assert_eq!(
            browser.handle(KeyEvent::new(KeyCode::Char('n'), KeyModifiers::NONE)),
            crate::overlay::Intent::Key('n', Some("s1".into()))
        );
    }

    #[test]
    fn the_skill_browser_distinguishes_invocable_from_model_only() {
        let skills_list: Vec<WireSkill> = serde_json::from_value(json!([
            { "name": "pdf", "userInvocable": true, "enabled": true },
            { "name": "internal", "userInvocable": false, "enabled": true }
        ]))
        .expect("skills");

        let browser = skills(&skills_list);
        let rows = browser.visible_items();
        assert_eq!(rows[0].cells[2], "invocable");
        assert_eq!(rows[1].cells[2], "model-only");
    }

    #[test]
    fn the_skill_browser_builds_a_detail_pane() {
        let skills_list: Vec<WireSkill> = serde_json::from_value(json!([
            { "name": "pdf", "description": "PDF tools", "enabled": true, "sourcePath": "/x/SKILL.md" }
        ]))
        .expect("skills");

        let browser = skills(&skills_list);
        let detail = browser.visible_items()[0].detail.join("\n");
        assert!(detail.contains("pdf"));
        assert!(detail.contains("/x/SKILL.md"));
        assert!(detail.contains("PDF tools"));
    }

    #[test]
    fn the_plugin_browser_marks_untrusted_plugins_as_blocked() {
        let plugins_list: Vec<WirePlugin> = serde_json::from_value(json!([
            { "name": "ok", "enabled": true, "trusted": true },
            { "name": "off", "enabled": false, "trusted": true },
            { "name": "bad", "enabled": true, "trusted": false }
        ]))
        .expect("plugins");

        let browser = plugins(&plugins_list);
        let rows = browser.visible_items();
        assert_eq!(rows[0].cells[0], glyph::ACTIVE);
        assert_eq!(rows[1].cells[0], glyph::INACTIVE);
        assert_eq!(
            rows[2].cells[0],
            glyph::BLOCKED,
            "an untrusted plugin must be visibly distinct even when enabled"
        );
    }

    #[test]
    fn the_hook_browser_keys_rows_by_index() {
        let hooks_list: Vec<WireHook> = serde_json::from_value(json!([
            { "index": 0, "event": "PreToolUse", "enabled": true },
            { "index": 1, "event": "PostToolUse", "enabled": false }
        ]))
        .expect("hooks");

        let browser = hooks(&hooks_list);
        assert_eq!(browser.selected_id(), Some("0"));
        assert_eq!(browser.visible_items()[1].id, "1");
    }

    #[test]
    fn the_mcp_browser_shows_scope_and_transport() {
        let servers = vec![
            McpServer {
                name: "memory".into(),
                scope: crate::config::Scope::User,
                transport: "stdio",
                command: Some("memory.exe".into()),
                args: vec![],
                url: None,
                enabled: true,
                env_keys: vec!["DATA".into()],
            },
            McpServer {
                name: "remote".into(),
                scope: crate::config::Scope::Project,
                transport: "http",
                command: None,
                args: vec![],
                url: Some("https://example.com".into()),
                enabled: false,
                env_keys: vec![],
            },
        ];

        let browser = mcp(&servers);
        let rows = browser.visible_items();

        assert_eq!(rows[0].cells[0], glyph::ACTIVE);
        assert_eq!(rows[0].cells[2], "stdio");
        assert_eq!(rows[0].cells[3], "user");
        assert_eq!(rows[1].cells[0], glyph::INACTIVE);
        assert_eq!(rows[1].cells[3], "project");
    }

    #[test]
    fn the_mcp_detail_lists_env_names_but_not_values() {
        let servers = vec![McpServer {
            name: "s".into(),
            scope: crate::config::Scope::User,
            transport: "stdio",
            command: Some("x".into()),
            args: vec![],
            url: None,
            enabled: true,
            env_keys: vec!["API_TOKEN".into()],
        }];

        let detail = mcp(&servers).visible_items()[0].detail.join("\n");
        assert!(detail.contains("API_TOKEN"), "env names should be shown");
        assert!(
            !detail.contains("secret") && !detail.contains("value"),
            "env values must never be rendered"
        );
    }

    #[test]
    fn the_task_browser_maps_outcomes_to_glyphs() {
        let logs = vec![
            TaskLog {
                id: "task-0001".into(),
                session_id: "s".into(),
                path: std::path::PathBuf::from("a.log"),
                size_bytes: 2048,
            },
            TaskLog {
                id: "task-0002".into(),
                session_id: "s".into(),
                path: std::path::PathBuf::from("b.log"),
                size_bytes: 10,
            },
        ];

        let mut outcomes = BTreeMap::new();
        outcomes.insert(
            "task-0001".to_string(),
            TaskOutcome {
                status: "completed".into(),
                description: "build".into(),
                report: Some("all good".into()),
            },
        );
        outcomes.insert(
            "task-0002".to_string(),
            TaskOutcome {
                status: "failed".into(),
                description: "tests".into(),
                report: None,
            },
        );

        let browser = tasks(&logs, &outcomes);
        let rows = browser.visible_items();

        assert_eq!(rows[0].cells[0], glyph::ACTIVE);
        assert_eq!(rows[0].cells[2], "completed");
        assert_eq!(rows[0].cells[3], "2K");
        assert_eq!(rows[0].cells[4], "build");
        assert_eq!(rows[1].cells[0], glyph::BLOCKED);
        assert_eq!(rows[1].cells[2], "failed");
    }

    #[test]
    fn task_rows_are_uniquely_keyed_across_sessions() {
        // Task ids restart per session; two rows must not share an id or the
        // browser's select-by-id restore would jump between them.
        let logs = vec![
            TaskLog {
                id: "task-0001".into(),
                session_id: "alpha".into(),
                path: std::path::PathBuf::from("a.log"),
                size_bytes: 1,
            },
            TaskLog {
                id: "task-0001".into(),
                session_id: "beta".into(),
                path: std::path::PathBuf::from("b.log"),
                size_bytes: 1,
            },
        ];

        let browser = tasks(&logs, &BTreeMap::new());
        let rows = browser.visible_items();
        assert_ne!(rows[0].id, rows[1].id);
        assert_eq!(rows[0].id, "alpha/task-0001");
        // The displayed id stays short.
        assert_eq!(rows[0].cells[1], "task-0001");
    }

    #[test]
    fn a_task_with_no_reported_outcome_shows_as_unknown() {
        let logs = vec![TaskLog {
            id: "t".into(),
            session_id: "s".into(),
            path: std::path::PathBuf::from("t.log"),
            size_bytes: 0,
        }];

        let browser = tasks(&logs, &BTreeMap::new());
        assert_eq!(browser.visible_items()[0].cells[2], "unknown");
    }

    #[test]
    fn formats_byte_counts_compactly() {
        assert_eq!(human_size(512), "512B");
        assert_eq!(human_size(2048), "2K");
        assert_eq!(human_size(5 * 1024 * 1024), "5.0M");
    }

    #[test]
    fn every_browser_handles_an_empty_result_without_panicking() {
        assert!(models(&[], None, "live").is_empty());
        assert!(schedules(&[]).is_empty());
        assert!(skills(&[]).is_empty());
        assert!(plugins(&[]).is_empty());
        assert!(hooks(&[]).is_empty());
        assert!(mcp(&[]).is_empty());
        assert!(tasks(&[], &BTreeMap::new()).is_empty());
        assert!(sessions(&[]).is_empty());
    }

    #[test]
    fn sessions_browser_activates_on_enter_without_detail() {
        use chrono::Utc;
        use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
        let summaries = vec![SessionSummary {
            id: "abc123456789".into(),
            created_utc: Utc::now(),
            message_count: 4,
            preview: "hello".into(),
        }];
        let mut browser = sessions(&summaries);
        // Enter must fire Activate (not open a detail pane) for a no-detail browser.
        let intent = browser.handle(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
        assert_eq!(intent, crate::overlay::Intent::Activate("abc123456789".into()));
    }

    #[test]
    fn sessions_browser_shows_count_and_index() {
        use chrono::Utc;
        let summaries = vec![
            SessionSummary { id: "aaa".into(), created_utc: Utc::now(), message_count: 3, preview: "first".into() },
            SessionSummary { id: "bbb".into(), created_utc: Utc::now(), message_count: 7, preview: "second".into() },
        ];
        let browser = sessions(&summaries);
        assert!(browser.title().contains("2"));
        let rows = browser.visible_items();
        // First column is the 1-based index.
        assert_eq!(rows[0].cells[0], "1");
        assert_eq!(rows[1].cells[0], "2");
        // Message counts.
        assert_eq!(rows[0].cells[2], "3");
        assert_eq!(rows[1].cells[2], "7");
    }

    #[test]
    fn format_session_age_just_now() {
        let t = Utc::now();
        assert_eq!(format_session_age(t), "just now");
    }

    #[test]
    fn format_session_age_minutes() {
        let t = Utc::now() - chrono::Duration::seconds(90);
        assert_eq!(format_session_age(t), "1m ago");
    }

    #[test]
    fn format_session_age_hours() {
        let t = Utc::now() - chrono::Duration::seconds(7200);
        assert_eq!(format_session_age(t), "2h ago");
    }

    #[test]
    fn format_session_age_days() {
        let t = Utc::now() - chrono::Duration::seconds(86400 * 3);
        assert_eq!(format_session_age(t), "3d ago");
    }


    #[test]
    fn every_browser_truncates_rows_to_its_column_widths() {
        let long = "x".repeat(200);
        let models_list = [model(&long, Some(&long), Some(200_000))];
        let browser = models(&models_list, None, "live");

        for (cell, column) in browser
            .format_row(browser.visible_items()[0])
            .iter()
            .zip(browser.columns())
        {
            assert!(
                coda_render::text::width(cell) <= column.max_width,
                "cell {cell:?} exceeds column {}",
                column.header
            );
        }
    }
}
