//! Concrete browsers built on the shared [`crate::overlay::Browser`].
//!
//! Each function maps a wire result onto columns and rows. Column widths and
//! footer hints mirror the C# overlays so the layout is familiar.

use coda_proto::messages::{
    ScheduledTask, WireHook, WireModel, WirePlugin, WireSkill,
};

use crate::overlay::{Browser, Column, Item};

/// Status glyphs shared across browsers.
mod glyph {
    /// Enabled, running, or otherwise healthy.
    pub const ACTIVE: &str = "\u{25CF}"; // ●
    /// Disabled or idle.
    pub const INACTIVE: &str = "\u{25CB}"; // ○
    /// Failed or blocked.
    pub const BLOCKED: &str = "\u{2717}"; // ✗
    /// Neither active nor failed.
    pub const OTHER: &str = "\u{25A0}"; // ■
    /// The current selection, e.g. the active model.
    pub const CURRENT: &str = "\u{2713}"; // ✓
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
    fn every_browser_handles_an_empty_result_without_panicking() {
        assert!(models(&[], None, "live").is_empty());
        assert!(schedules(&[]).is_empty());
        assert!(skills(&[]).is_empty());
        assert!(plugins(&[]).is_empty());
        assert!(hooks(&[]).is_empty());
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
