//! Renders a sample session to stdout, for eyeballing the theme and layout
//! without starting an engine.

use coda_proto::events::ToolCallStatus;
use coda_proto::{Correlation, Event};
use coda_render::theme::{ColorDepth, Theme};
use coda_tui::composer::Composer;
use coda_tui::state::{UiEvent, UiState};
use coda_tui::{draw, viewport::Viewport};
use ratatui::backend::TestBackend;
use ratatui::Terminal;

fn main() {
    let (width, height) = (86u16, 26u16);
    let mut state = UiState::with_clock(|| "09:41".to_string());
    state.apply(UiEvent::Connected { session_id: "demo".into() });
    state.apply(UiEvent::ModelChanged { id: "claude-opus-5".into(), context_limit: Some(200_000) });
    state.apply(UiEvent::Submitted { text: "Add a retry policy to the HTTP client.".into() });
    state.apply(UiEvent::Engine(Event::AssistantText {
        delta: "I'll add exponential backoff.\n\n- retry on 429 and 5xx\n- cap at `5` attempts\n\n```rust\nlet policy = Retry::new(5);\n```\n".into(),
    }));
    state.apply(UiEvent::Engine(Event::AssistantTextComplete));

    let c = Correlation { root_turn_id: Some("t".into()), activity_id: Some("a".into()), call_id: Some("c1".into()), source_id: Some("root:t".into()) };
    state.apply(UiEvent::Engine(Event::ToolCall { tool_name: "edit".into(), input_json: r#"{"path":"src/http.rs"}"#.into(), correlation: c.clone() }));
    state.apply(UiEvent::Engine(Event::ToolResult { tool_name: "edit".into(), content: "ok".into(), is_error: false, status: Some(ToolCallStatus::Succeeded), correlation: c }));
    state.apply(UiEvent::Engine(Event::Usage { input_tokens: 24_000, output_tokens: 900 }));
    state.apply(UiEvent::Engine(Event::TurnComplete { stop_reason: Some("end_turn".into()), interrupted: false, root_turn_id: None, activity_id: None }));

    let mut composer = Composer::new();
    composer.set_text("now add tests for it");

    let theme = Theme::warm_ember().with_depth(ColorDepth::TrueColor);
    let mut terminal = Terminal::new(TestBackend::new(width, height)).unwrap();
    let regions = draw::layout(ratatui::layout::Rect::new(0, 0, width, height), composer.line_count(), false);
    let rows = state.transcript.render(regions.transcript.width as usize, state.display_mode);
    let mut viewport = Viewport::new();
    viewport.update(rows.len(), regions.transcript.height as usize);
    terminal.draw(|f| draw::draw(f, &state, &composer, &viewport, &rows, &theme)).unwrap();

    let buffer = terminal.backend().buffer().clone();
    println!("{}", "-".repeat(width as usize));
    for y in 0..height {
        let line: String = (0..width).map(|x| buffer[(x, y)].symbol()).collect();
        println!("{}", line.trim_end());
    }
    println!("{}", "-".repeat(width as usize));
}
