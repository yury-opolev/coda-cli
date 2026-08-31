//! Headless rendering checks.
//!
//! These drive the real draw path through ratatui's test backend, so they catch
//! layout and styling regressions that unit tests on individual rows cannot:
//! anything that panics, overflows the buffer, or writes to the wrong region
//! shows up here.

use coda_proto::events::ToolCallStatus;
use coda_proto::{Correlation, Event};
use coda_render::theme::{ColorDepth, Theme};
use coda_render::RenderLine;
use coda_tui::composer::Composer;
use coda_tui::draw;
use coda_tui::state::{PendingPrompt, UiEvent, UiState};
use coda_tui::viewport::Viewport;
use ratatui::backend::TestBackend;
use ratatui::Terminal;

/// Renders a state to a fixed-size buffer and returns the visible text rows.
fn render(state: &UiState, composer: &Composer, width: u16, height: u16) -> Vec<String> {
    let theme = Theme::warm_ember().with_depth(ColorDepth::TrueColor);
    let mut terminal = Terminal::new(TestBackend::new(width, height)).expect("terminal");

    let mut viewport = Viewport::new();
    let regions = draw::layout(
        ratatui::layout::Rect::new(0, 0, width, height),
        composer.line_count(),
        false,
    );
    let rows = state
        .transcript
        .render(regions.transcript.width as usize, state.display_mode);
    viewport.update(rows.len(), regions.transcript.height as usize);

    terminal
        .draw(|frame| draw::draw(frame, state, composer, &viewport, &rows, &theme, None))
        .expect("draw");

    let buffer = terminal.backend().buffer().clone();
    (0..height)
        .map(|y| {
            (0..width)
                .map(|x| buffer[(x, y)].symbol())
                .collect::<String>()
                .trim_end()
                .to_string()
        })
        .collect()
}

fn fixed_clock() -> String {
    "09:41".to_string()
}

fn session() -> UiState {
    let mut state = UiState::with_clock(fixed_clock);
    state.apply(UiEvent::Connected {
        session_id: "test-session".into(),
    });
    state
}

fn correlation(id: &str) -> Correlation {
    Correlation {
        root_turn_id: Some("t1".into()),
        activity_id: Some("a1".into()),
        call_id: Some(id.into()),
        source_id: Some("root:t1".into()),
    }
}

#[test]
fn renders_an_empty_session_without_panicking() {
    let rows = render(&session(), &Composer::new(), 80, 24);
    assert_eq!(rows.len(), 24);
    // The status bar names the current activity.
    assert!(rows[23].contains("ready"), "status bar: {:?}", rows[23]);
}

#[test]
fn renders_a_user_message_with_its_marker() {
    let mut state = session();
    state.apply(UiEvent::Submitted {
        text: "fix the build".into(),
    });

    let rows = render(&state, &Composer::new(), 80, 24);
    assert!(
        rows.iter().any(|r| r.contains("\u{276F} fix the build")),
        "no user row in {rows:?}"
    );
}

#[test]
fn renders_a_streamed_assistant_reply() {
    let mut state = session();
    state.apply(UiEvent::Submitted { text: "hello".into() });
    for delta in ["I will ", "look ", "into it."] {
        state.apply(UiEvent::Engine(Event::AssistantText {
            delta: delta.into(),
        }));
    }

    let rows = render(&state, &Composer::new(), 80, 24);
    assert!(
        rows.iter().any(|r| r.contains("I will look into it.")),
        "assistant text missing from {rows:?}"
    );
}

#[test]
fn renders_markdown_structure_in_an_assistant_reply() {
    let mut state = session();
    state.apply(UiEvent::Engine(Event::AssistantText {
        delta: "# Plan\n\n- first step\n- second step\n".into(),
    }));

    let rows = render(&state, &Composer::new(), 80, 24);
    assert!(rows.iter().any(|r| r.contains("Plan")));
    assert!(
        rows.iter().any(|r| r.contains("\u{2022} first step")),
        "bullets missing from {rows:?}"
    );
    assert!(
        !rows.iter().any(|r| r.contains("# Plan")),
        "heading marker leaked into output"
    );
}

#[test]
fn renders_a_tool_batch_summary() {
    let mut state = session();
    for (name, id) in [("read_file", "c1"), ("grep", "c2")] {
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: name.into(),
            input_json: "{}".into(),
            correlation: correlation(id),
        }));
        state.apply(UiEvent::Engine(Event::ToolResult {
            tool_name: name.into(),
            content: "done".into(),
            is_error: false,
            status: Some(ToolCallStatus::Succeeded),
            correlation: correlation(id),
        }));
    }
    state.apply(UiEvent::Engine(Event::TurnComplete {
        stop_reason: Some("end_turn".into()),
        interrupted: false,
        root_turn_id: None,
        activity_id: None,
    }));

    let rows = render(&state, &Composer::new(), 80, 24);
    assert!(
        rows.iter().any(|r| r.contains("Ran 2 tools")),
        "tool summary missing from {rows:?}"
    );
}

#[test]
fn renders_composer_text_with_its_prompt_glyph() {
    let mut composer = Composer::new();
    composer.set_text("what I am typing");

    let rows = render(&session(), &composer, 80, 24);
    assert!(
        rows.iter().any(|r| r.contains("\u{276F} what I am typing")),
        "composer text missing from {rows:?}"
    );
}

#[test]
fn renders_a_multiline_composer() {
    let mut composer = Composer::new();
    composer.set_text("first line\nsecond line\nthird line");

    let rows = render(&session(), &composer, 80, 24);
    assert!(rows.iter().any(|r| r.contains("first line")));
    assert!(rows.iter().any(|r| r.contains("second line")));
    assert!(rows.iter().any(|r| r.contains("third line")));
}

#[test]
fn renders_a_permission_prompt_over_the_transcript() {
    let mut state = session();
    state.apply(UiEvent::Engine(Event::AssistantText {
        delta: "working on it".into(),
    }));
    state.apply(UiEvent::PromptRequested(PendingPrompt::Permission {
        tool: "run_command".into(),
        preview: "rm -rf build".into(),
    }));

    let rows = render(&state, &Composer::new(), 80, 24);
    assert!(
        rows.iter().any(|r| r.contains("Permission required")),
        "prompt title missing from {rows:?}"
    );
    assert!(rows.iter().any(|r| r.contains("run_command")));
    assert!(
        rows.iter().any(|r| r.contains("y: allow")),
        "prompt hint missing from {rows:?}"
    );
}

#[test]
fn renders_a_question_prompt_with_numbered_options() {
    let mut state = session();
    state.apply(UiEvent::PromptRequested(PendingPrompt::Question {
        question: "Which approach?".into(),
        options: vec!["rewrite".into(), "patch".into()],
        multi_select: false,
        allow_free_text: true,
    }));

    let rows = render(&state, &Composer::new(), 80, 24);
    assert!(rows.iter().any(|r| r.contains("Which approach?")));
    assert!(rows.iter().any(|r| r.contains("1. rewrite")));
    assert!(rows.iter().any(|r| r.contains("2. patch")));
}

/// Selecting a span must actually change what is rendered.
///
/// The selection module was fully implemented and unit-tested, but nothing
/// drove it: mouse events only handled scroll, so `TranscriptSelection` was
/// never constructed and drag-selection silently did nothing. A test that
/// exercises the *drawing path* with a selection is what catches that, since
/// the module's own tests pass either way.
#[test]
fn a_selected_span_is_rendered_differently_from_an_unselected_one() {
    use coda_tui::selection::{SelectionPos, TranscriptSelection};

    let width = 40u16;
    let height = 10u16;
    let rows = vec![RenderLine::new("hello selectable world", coda_render::Role::Assistant)];

    let render = |selection: Option<&TranscriptSelection>| {
        let mut terminal = Terminal::new(TestBackend::new(width, height)).expect("terminal");
        let state = session();
        let composer = Composer::new();
        let mut viewport = Viewport::new();
        viewport.update(rows.len(), height as usize);
        terminal
            .draw(|frame| {
                draw::draw_with_pin(
                    frame,
                    &state,
                    &composer,
                    &viewport,
                    &rows,
                    &Theme::default(),
                    None,
                    None,
                    selection,
                );
            })
            .expect("draw");
        terminal.backend().buffer().clone()
    };

    let plain = render(None);

    let mut selection = TranscriptSelection::new();
    selection.begin(SelectionPos { row: 0, col: 0 });
    selection.update(SelectionPos { row: 0, col: 5 });
    assert!(selection.has_selection(), "the test selection must be non-empty");
    let selected = render(Some(&selection));

    assert_ne!(
        plain, selected,
        "a selection must be visible on screen; if these match, nothing drove the highlight"
    );
}

/// A drawn frame must report where the transcript actually is, or a click
/// cannot be translated into a transcript row.
#[test]
fn drawing_reports_the_transcript_origin_for_mouse_mapping() {
    let width = 40u16;
    let height = 12u16;
    let rows = vec![RenderLine::new("one", coda_render::Role::Assistant), RenderLine::new("two", coda_render::Role::Assistant)];

    let mut terminal = Terminal::new(TestBackend::new(width, height)).expect("terminal");
    let state = session();
    let composer = Composer::new();
    let mut viewport = Viewport::new();
    viewport.update(rows.len(), height as usize);

    let mut origin = (0u16, 0u16);
    terminal
        .draw(|frame| {
            origin = draw::draw_with_pin(
                frame,
                &state,
                &composer,
                &viewport,
                &rows,
                &Theme::default(),
                None,
                None,
                None,
            );
        })
        .expect("draw");

    assert!(origin.1 > 0, "the transcript must have a non-zero height: {origin:?}");
    assert!(
        origin.0 + origin.1 <= height,
        "the transcript must fit on screen: {origin:?} in {height}"
    );
}

#[test]
fn the_status_bar_shows_the_model_and_context_usage() {
    let mut state = session();
    state.apply(UiEvent::ModelChanged {
        id: "claude-opus-5".into(),
        context_limit: Some(200_000),
    });
    state.apply(UiEvent::Engine(Event::Usage {
        input_tokens: 50_000,
        output_tokens: 1_000,
    }));

    let rows = render(&state, &Composer::new(), 80, 24);
    let status = rows.last().expect("a status bar");
    assert!(status.contains("claude-opus-5"), "status: {status:?}");
    assert!(status.contains("context 25%"), "status: {status:?}");
}

#[test]
fn the_status_bar_reports_queued_messages() {
    let mut state = session();
    state.apply(UiEvent::Submitted { text: "go".into() });
    state.apply(UiEvent::Queued {
        text: "and then this".into(),
        id: Some("s1".into()),
    });

    let rows = render(&state, &Composer::new(), 80, 24);
    assert!(
        rows.last().expect("status").contains("1 queued"),
        "status: {:?}",
        rows.last()
    );
}

#[test]
fn renders_an_error_notice() {
    let mut state = session();
    state.apply(UiEvent::Engine(Event::Error {
        message: "provider returned 400".into(),
    }));

    let rows = render(&state, &Composer::new(), 80, 24);
    assert!(rows.iter().any(|r| r.contains("provider returned 400")));
}

#[test]
fn renders_at_every_reasonable_terminal_size() {
    let mut state = session();
    state.apply(UiEvent::Submitted {
        text: "a message long enough to need wrapping in a narrow terminal".into(),
    });
    state.apply(UiEvent::Engine(Event::AssistantText {
        delta: "# Heading\n\nSome body text.\n\n```rust\nlet x = 1;\n```\n".into(),
    }));
    state.apply(UiEvent::Engine(Event::ToolCall {
        tool_name: "run_command".into(),
        input_json: r#"{"command":"cargo test --all-features"}"#.into(),
        correlation: correlation("c1"),
    }));

    let mut composer = Composer::new();
    composer.set_text("draft\nsecond line");

    // A terminal can be resized to anything; none of these may panic or
    // produce a buffer of the wrong shape.
    for width in [20u16, 40, 80, 200] {
        for height in [6u16, 10, 24, 60] {
            let rows = render(&state, &composer, width, height);
            assert_eq!(rows.len(), height as usize, "wrong row count at {width}x{height}");
            for row in &rows {
                assert!(
                    row.chars().count() <= width as usize,
                    "row overflows at {width}x{height}: {row:?}"
                );
            }
        }
    }
}

#[test]
fn renders_a_diff_tool_result_in_full_mode() {
    let mut state = session();
    state.apply(UiEvent::DisplayModeChanged(
        coda_render::tool::ToolDisplayMode::Full,
    ));
    state.apply(UiEvent::Engine(Event::ToolCall {
        tool_name: "edit".into(),
        input_json: r#"{"path":"src/main.rs"}"#.into(),
        correlation: correlation("c1"),
    }));
    state.apply(UiEvent::Engine(Event::ToolResult {
        tool_name: "edit".into(),
        content: "--- a/src/main.rs\n+++ b/src/main.rs\n@@ -1 +1 @@\n-old line\n+new line\n"
            .into(),
        is_error: false,
        status: Some(ToolCallStatus::Succeeded),
        correlation: correlation("c1"),
    }));

    let rows = render(&state, &Composer::new(), 80, 30);
    assert!(
        rows.iter().any(|r| r.contains("Update(src/main.rs)")),
        "diff header missing from {rows:?}"
    );
    assert!(rows.iter().any(|r| r.contains("+ new line")));
    assert!(rows.iter().any(|r| r.contains("- old line")));
}

/// Renders an overlay over a session, returning the visible rows.
fn render_with_browser(browser: &coda_tui::overlay::Browser, width: u16, height: u16) -> Vec<String> {
    let theme = Theme::warm_ember().with_depth(ColorDepth::TrueColor);
    let mut terminal = Terminal::new(TestBackend::new(width, height)).expect("terminal");
    let state = session();
    let composer = Composer::new();
    let mut viewport = Viewport::new();
    viewport.update(0, 1);

    terminal
        .draw(|frame| draw::draw(frame, &state, &composer, &viewport, &[], &theme, Some(browser)))
        .expect("draw");

    let buffer = terminal.backend().buffer().clone();
    (0..height)
        .map(|y| {
            (0..width)
                .map(|x| buffer[(x, y)].symbol())
                .collect::<String>()
                .trim_end()
                .to_string()
        })
        .collect()
}

#[test]
fn renders_a_model_browser_overlay() {
    let models: Vec<coda_proto::messages::WireModel> = serde_json::from_value(serde_json::json!([
        { "id": "claude-opus-5", "displayName": "Claude Opus 5", "contextLimit": 200000 },
        { "id": "gpt-5.6-sol", "displayName": "GPT-5.6 Sol", "contextLimit": 400000 }
    ]))
    .expect("models");

    let browser = coda_tui::browsers::models(&models, Some("claude-opus-5"), "live");
    let rows = render_with_browser(&browser, 90, 20);
    let screen = rows.join("\n");

    assert!(screen.contains("Models"), "no title in\n{screen}");
    assert!(screen.contains("claude-opus-5"), "no rows in\n{screen}");
    assert!(screen.contains("200K"), "no context size in\n{screen}");
    assert!(screen.contains("Esc q close"), "no footer in\n{screen}");
}

#[test]
fn renders_a_browser_detail_pane() {
    let skills: Vec<coda_proto::messages::WireSkill> = serde_json::from_value(serde_json::json!([
        { "name": "pdf", "description": "PDF tools", "enabled": true, "userInvocable": true }
    ]))
    .expect("skills");

    let mut browser = coda_tui::browsers::skills(&skills);
    browser.handle(crossterm::event::KeyEvent::new(
        crossterm::event::KeyCode::Enter,
        crossterm::event::KeyModifiers::NONE,
    ));

    let screen = render_with_browser(&browser, 90, 20).join("\n");
    assert!(screen.contains("user-invocable yes"), "no detail in\n{screen}");
}

#[test]
fn an_overlay_renders_at_every_reasonable_size() {
    let skills: Vec<coda_proto::messages::WireSkill> = serde_json::from_value(serde_json::json!([
        { "name": "a", "description": "first", "enabled": true },
        { "name": "b", "description": "second", "enabled": false }
    ]))
    .expect("skills");
    let browser = coda_tui::browsers::skills(&skills);

    for width in [20u16, 40, 90, 200] {
        for height in [6u16, 12, 40] {
            let rows = render_with_browser(&browser, width, height);
            assert_eq!(rows.len(), height as usize);
            for row in &rows {
                assert!(
                    row.chars().count() <= width as usize,
                    "overlay row overflows at {width}x{height}: {row:?}"
                );
            }
        }
    }
}

#[test]
fn the_composer_panel_is_edged_with_half_blocks() {
    let state = session();
    let composer = Composer::new();
    let lines = render(&state, &composer, 40, 12);

    // The panel sits above the one-row status bar: bottom edge at height - 2,
    // top edge two rows above that for a single-line composer.
    let bottom_edge = &lines[lines.len() - 2];
    let top_edge = &lines[lines.len() - 4];

    assert!(
        top_edge.chars().all(|c| c == '\u{2584}') && !top_edge.is_empty(),
        "top edge should be lower half blocks, got {top_edge:?}"
    );
    assert!(
        bottom_edge.chars().all(|c| c == '\u{2580}') && !bottom_edge.is_empty(),
        "bottom edge should be upper half blocks, got {bottom_edge:?}"
    );
}

#[test]
fn the_startup_banner_renders_in_the_transcript() {
    let mut state = session();
    state.transcript.push(coda_tui::transcript::Block::Banner {
        wordmark: vec!["WORDMARK".to_string()],
        details: vec![String::new(), "cwd: /tmp/project".to_string()],
    });

    let composer = Composer::new();
    let lines = render(&state, &composer, 60, 16).join("\n");

    assert!(lines.contains("WORDMARK"), "wordmark missing from {lines:?}");
    assert!(lines.contains("cwd: /tmp/project"), "details missing from {lines:?}");
}
