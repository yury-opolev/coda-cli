//! Tests that drive the surface stack the way the application does.
//!
//! Unit tests cannot see a handler that is never reached. These can: they push
//! real surfaces onto a real stack and feed it real key events, so a surface
//! that is wired at one end only fails here even though its own tests pass.

use coda_tui::config::Settings;
use coda_tui::state::PendingPrompt;
use coda_tui::surface::prompt::PromptSurface;
use coda_tui::surface::settings::SettingsSurface;
use coda_tui::surface::stack::{StackOutcome, SurfaceStack};
use coda_tui::surface::{Surface, SurfaceAction};
use coda_render::theme::Theme;
use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
use ratatui::layout::Rect;

fn key(code: KeyCode) -> KeyEvent {
    KeyEvent::new(code, KeyModifiers::NONE)
}

fn settings() -> Settings {
    Settings::empty_at(std::env::temp_dir().join("coda-surface-integration.json"))
}

fn permission() -> Box<dyn Surface> {
    Box::new(PromptSurface::new(PendingPrompt::Permission {
        tool: "write_file".into(),
        preview: "src/main.rs".into(),
    }))
}

#[test]
fn a_prompt_blocks_a_settings_surface_from_opening_over_it() {
    // The permission gate blocks the turn. Anything opening above it would
    // hide the question the engine is waiting on.
    let mut stack = SurfaceStack::default();
    stack.push(permission());

    assert!(
        !stack.push(Box::new(SettingsSurface::new(&settings()))),
        "a settings surface opened over a blocking permission prompt"
    );
    assert_eq!(stack.len(), 1);
    assert_eq!(stack.top_title().as_deref(), Some("Permission required"));
}

#[test]
fn escape_cannot_dismiss_a_prompt_through_the_stack() {
    // Not just that PromptSurface says Exclusive, but that the stack honours
    // it on the real key path.
    let mut stack = SurfaceStack::default();
    stack.push(permission());

    match stack.handle_key(key(KeyCode::Esc)) {
        StackOutcome::Action(SurfaceAction::AnswerPrompt { allowed, .. }) => {
            assert!(!allowed, "Esc approved a permission request");
        }
        _ => panic!("Esc must deny the prompt, not dismiss it"),
    }
    // The surface stays until the application retires it, having replied.
    assert_eq!(stack.len(), 1);
}

#[test]
fn answering_a_prompt_reaches_the_application() {
    let mut stack = SurfaceStack::default();
    stack.push(permission());
    match stack.handle_key(key(KeyCode::Char('y'))) {
        StackOutcome::Action(SurfaceAction::AnswerPrompt { allowed: true, .. }) => {}
        _ => panic!("the answer never reached the application"),
    }
}

#[test]
fn a_prompt_swallows_stray_keys_rather_than_leaking_them() {
    // Ignored would let the stack's own Esc handling pop a prompt the engine
    // is still waiting on, and would let stray keys reach the composer.
    let mut stack = SurfaceStack::default();
    stack.push(permission());
    for code in [KeyCode::Char('z'), KeyCode::Tab, KeyCode::Backspace, KeyCode::Up] {
        assert!(
            matches!(stack.handle_key(key(code)), StackOutcome::Handled),
            "{code:?} leaked out of a blocking prompt"
        );
    }
    assert_eq!(stack.len(), 1);
}

#[test]
fn a_settings_surface_saves_through_the_stack() {
    let mut stack = SurfaceStack::default();
    stack.push(Box::new(SettingsSurface::new(&settings())));
    match stack.handle_key(key(KeyCode::Enter)) {
        StackOutcome::Action(SurfaceAction::SaveSettings) => {}
        _ => panic!("Enter did not ask the application to save"),
    }
}

#[test]
fn a_settings_surface_can_be_dismissed_but_a_prompt_cannot() {
    let mut stack = SurfaceStack::default();
    stack.push(Box::new(SettingsSurface::new(&settings())));
    stack.handle_key(key(KeyCode::Esc));
    assert!(stack.is_empty(), "Esc did not close a normal surface");
}

#[test]
fn every_surface_stays_inside_its_area_at_any_terminal_size() {
    // A cramped terminal must show a usable surface, not a clipped one.
    let theme = Theme::default();

    for (w, h) in [(80u16, 24u16), (40, 10), (20, 6), (200, 60), (12, 3), (1, 1)] {
        let area = Rect::new(0, 0, w, h);

        for surface in [
            permission(),
            Box::new(SettingsSurface::new(&settings())) as Box<dyn Surface>,
            Box::new(PromptSurface::new(PendingPrompt::Question {
                question: "A rather long question that will certainly need wrapping".into(),
                options: (0..30).map(|i| format!("option number {i}")).collect(),
                multi_select: false,
                allow_free_text: false,
            })),
        ] {
            let mut stack = SurfaceStack::default();
            stack.push(surface);

            for rendered in stack.render(area, &theme) {
                assert!(
                    rendered.lines.len() <= rendered.content.height as usize,
                    "{} produced {} lines for {} rows at {w}x{h}",
                    rendered.title,
                    rendered.lines.len(),
                    rendered.content.height
                );
                for line in &rendered.lines {
                    let width: usize = line
                        .spans
                        .iter()
                        .map(|s| coda_render::text::width(&s.content))
                        .sum();
                    assert!(
                        width <= rendered.content.width as usize,
                        "{} produced a {width}-cell line for {} cells at {w}x{h}",
                        rendered.title,
                        rendered.content.width
                    );
                }
                assert!(
                    rendered.region.right() <= area.right()
                        && rendered.region.bottom() <= area.bottom(),
                    "{} escaped the screen at {w}x{h}: {:?}",
                    rendered.title,
                    rendered.region
                );
            }
        }
    }
}

#[test]
fn every_surface_offers_hints() {
    // A surface with no hints leaves the user guessing which keys work. The
    // footer is drawn from this, so an empty one is a blank line.
    let surfaces: Vec<Box<dyn Surface>> = vec![
        permission(),
        Box::new(SettingsSurface::new(&settings())),
        Box::new(PromptSurface::new(PendingPrompt::PlanApproval {
            plan: "do the thing".into(),
        })),
    ];
    for surface in surfaces {
        assert!(
            !surface.hints().trim().is_empty(),
            "{} offers no key hints",
            surface.title()
        );
        assert!(
            !surface.title().trim().is_empty(),
            "a surface has no title"
        );
    }
}

#[test]
fn the_application_can_retire_a_prompt_the_user_cannot_dismiss() {
    // The engine clears a prompt without an answer whenever a turn ends or is
    // interrupted. An Exclusive surface left behind would be undismissable and
    // would wedge the interface, so pop() must bypass the exclusivity that
    // handle_key honours.
    let mut stack = SurfaceStack::default();
    stack.push(permission());

    // The user cannot get rid of it.
    stack.handle_key(key(KeyCode::Esc));
    assert_eq!(stack.len(), 1, "Esc dismissed a blocking prompt");

    // The application can.
    assert!(stack.pop().is_some());
    assert!(stack.is_empty(), "the application could not retire the prompt");
}

#[test]
fn a_superseded_prompt_does_not_block_its_replacement() {
    // A second prompt fails the first responder and replaces it. If the stale
    // surface stayed, its exclusivity would refuse the replacement and leave a
    // prompt on screen bound to a responder that has already failed.
    let mut stack = SurfaceStack::default();
    stack.push(permission());

    assert!(!stack.push(permission()), "exclusivity is not being enforced");
    stack.pop();
    assert!(
        stack.push(permission()),
        "the replacement prompt could not open after the stale one was retired"
    );
    assert_eq!(stack.len(), 1);
}

fn skills_browser() -> Box<dyn Surface> {
    use coda_tui::overlay::{Browser, Column, Item};
    use coda_tui::surface::browser::{BrowserKind, BrowserSurface};

    let mut browser = Browser::new(
        "Skills",
        vec![Column::new("Name", 24), Column::new("Summary", 40)],
    )
    .with_footer("Enter select");
    browser.set_items(vec![
        Item::new("a", vec!["alpha".into(), "first".into()]),
        Item::new("b", vec!["beta".into(), "second".into()]),
    ]);
    Box::new(BrowserSurface::new(BrowserKind::Skills, browser))
}

#[test]
fn a_browser_cannot_open_over_a_prompt() {
    // Before this phase, a prompt outranked a browser because of the order of
    // two if statements in on_key. Now it is a property of the prompt.
    let mut stack = SurfaceStack::default();
    stack.push(permission());
    assert!(
        !stack.push(skills_browser()),
        "a browser opened over a blocking permission prompt"
    );
    assert_eq!(stack.len(), 1);
}

#[test]
fn a_browser_row_action_reaches_the_host_with_its_kind() {
    use coda_tui::overlay::Intent;
    use coda_tui::surface::browser::BrowserKind;

    let mut stack = SurfaceStack::default();
    stack.push(skills_browser());
    match stack.handle_key(key(KeyCode::Char(' '))) {
        StackOutcome::Action(SurfaceAction::Browser { kind, intent }) => {
            assert_eq!(kind, BrowserKind::Skills);
            assert_eq!(intent, Intent::Toggle("a".into()));
        }
        _ => panic!("the toggle never reached the host"),
    }
}

#[test]
fn a_browser_navigates_without_troubling_the_host() {
    let mut stack = SurfaceStack::default();
    stack.push(skills_browser());
    assert!(matches!(
        stack.handle_key(key(KeyCode::Down)),
        StackOutcome::Handled
    ));
}

#[test]
fn a_browser_closes_on_escape() {
    let mut stack = SurfaceStack::default();
    stack.push(skills_browser());
    stack.handle_key(key(KeyCode::Esc));
    assert!(stack.is_empty(), "Esc did not close the browser");
}

#[test]
fn a_browser_stays_inside_its_area_at_any_size() {
    let theme = Theme::default();
    for (w, h) in [(80u16, 24u16), (40, 10), (20, 6), (12, 3), (1, 1)] {
        let area = Rect::new(0, 0, w, h);
        let mut stack = SurfaceStack::default();
        stack.push(skills_browser());
        for rendered in stack.render(area, &theme) {
            assert!(
                rendered.lines.len() <= rendered.content.height as usize,
                "browser produced {} lines for {} rows at {w}x{h}",
                rendered.lines.len(),
                rendered.content.height
            );
            for line in &rendered.lines {
                let width: usize = line
                    .spans
                    .iter()
                    .map(|s| coda_render::text::width(&s.content))
                    .sum();
                assert!(
                    width <= rendered.content.width as usize,
                    "browser produced a {width}-cell line for {} cells at {w}x{h}",
                    rendered.content.width
                );
            }
        }
    }
}

#[test]
fn a_browser_declares_its_own_row_actions() {
    // The point of RowActions: a browser's keys are defined where the browser
    // is, so adding one cannot be half done by forgetting a match arm in the
    // host. Nothing here mentions App.
    use coda_tui::overlay::{Browser, Column, Item};
    use coda_tui::surface::browser::{BrowserKind, BrowserSurface, RowActions};

    let mut browser = Browser::new("Things", vec![Column::new("Name", 24)]);
    browser.set_items(vec![
        Item::new("first", vec!["alpha".into()]),
        Item::new("second", vec!["beta".into()]),
    ]);

    let actions = RowActions::new()
        .on_activate(|id| SurfaceAction::SwitchModel(id.to_string()))
        .on_toggle(|id| SurfaceAction::TogglePlugin(id.to_string()))
        .on_key('u', |id| SurfaceAction::UpdatePlugin(id.to_string()));

    let mut stack = SurfaceStack::default();
    stack.push(Box::new(
        BrowserSurface::new(BrowserKind::Plugins, browser).with_actions(actions),
    ));

    match stack.handle_key(key(KeyCode::Enter)) {
        StackOutcome::Action(SurfaceAction::SwitchModel(id)) => assert_eq!(id, "first"),
        _ => panic!("Enter did not raise the configured activate action"),
    }
    match stack.handle_key(key(KeyCode::Char(' '))) {
        StackOutcome::Action(SurfaceAction::TogglePlugin(id)) => assert_eq!(id, "first"),
        _ => panic!("Space did not raise the configured toggle action"),
    }
    match stack.handle_key(key(KeyCode::Char('u'))) {
        StackOutcome::Action(SurfaceAction::UpdatePlugin(id)) => assert_eq!(id, "first"),
        _ => panic!("the custom key did not raise its configured action"),
    }
}

#[test]
fn a_browser_without_actions_still_reaches_the_host() {
    // Additive, not a cliff: a browser that declares nothing behaves exactly
    // as every browser did before, so the conversion could be done one at a
    // time without a flag day.
    use coda_tui::overlay::{Browser, Column, Item};
    use coda_tui::surface::browser::{BrowserKind, BrowserSurface};

    let mut browser = Browser::new("Things", vec![Column::new("Name", 24)]);
    browser.set_items(vec![Item::new("only", vec!["alpha".into()])]);

    let mut stack = SurfaceStack::default();
    stack.push(Box::new(BrowserSurface::new(BrowserKind::Hooks, browser)));

    match stack.handle_key(key(KeyCode::Char(' '))) {
        StackOutcome::Action(SurfaceAction::Browser { kind, .. }) => {
            assert_eq!(kind, BrowserKind::Hooks);
        }
        _ => panic!("an unconfigured row action did not reach the host"),
    }
}

#[test]
fn a_key_that_needs_no_row_works_on_an_empty_list() {
    // "New MCP server" does not act on a row, so it must work when there are
    // no rows -- which is exactly when a user reaches for it. Routing it
    // through the row-key path made it fire only when something was selected,
    // so on a project with no .mcp.json the footer advertised `n new` and
    // pressing it did nothing, with hand-editing JSON the only way in.
    use coda_tui::overlay::{Browser, Column, Item};
    use coda_tui::surface::browser::{BrowserKind, BrowserSurface, RowActions};

    let empty = Browser::new("MCP servers", vec![Column::new("Name", 24)]);

    let mut stack = SurfaceStack::default();
    stack.push(Box::new(
        BrowserSurface::new(BrowserKind::Mcp, empty).with_actions(
            RowActions::new()
                .on_bare_key('n', || SurfaceAction::NewMcpServer)
                .on_key('d', |id| SurfaceAction::DeleteMcpServer(id.to_string())),
        ),
    ));

    match stack.handle_key(key(KeyCode::Char('n'))) {
        StackOutcome::Action(SurfaceAction::NewMcpServer) => {}
        _ => panic!("`n` on an empty list did not open the editor"),
    }

    // The converse must hold too: a key that does act on a row must not fire
    // with no row, or it would delete a server named "".
    match stack.handle_key(key(KeyCode::Char('d'))) {
        StackOutcome::Action(SurfaceAction::DeleteMcpServer(id)) => {
            panic!("`d` deleted with nothing selected, targeting {id:?}")
        }
        _ => {}
    }

    // And with a row present, both still work.
    let mut filled = Browser::new("MCP servers", vec![Column::new("Name", 24)]);
    filled.set_items(vec![Item::new("ctx7", vec!["ctx7".into()])]);
    let mut stack = SurfaceStack::default();
    stack.push(Box::new(
        BrowserSurface::new(BrowserKind::Mcp, filled).with_actions(
            RowActions::new()
                .on_bare_key('n', || SurfaceAction::NewMcpServer)
                .on_key('d', |id| SurfaceAction::DeleteMcpServer(id.to_string())),
        ),
    ));
    match stack.handle_key(key(KeyCode::Char('n'))) {
        StackOutcome::Action(SurfaceAction::NewMcpServer) => {}
        _ => panic!("`n` stopped working once a row existed"),
    }
    match stack.handle_key(key(KeyCode::Char('d'))) {
        StackOutcome::Action(SurfaceAction::DeleteMcpServer(id)) => assert_eq!(id, "ctx7"),
        _ => panic!("`d` did not delete the selected row"),
    }
}

#[test]
fn the_del_key_deletes_what_d_deletes() {
    // Del is an alias for `d`. The browser reports it as its own intent, so
    // without an arm for it the action set never sees it and Del quietly
    // stops deleting -- working before this refactor, dead after, with
    // nothing to say so.
    use coda_tui::overlay::{Browser, Column, Item};
    use coda_tui::surface::browser::{BrowserKind, BrowserSurface, RowActions};

    let mut browser = Browser::new("Schedules", vec![Column::new("Name", 24)]);
    browser.set_items(vec![Item::new("nightly", vec!["nightly".into()])]);

    let mut stack = SurfaceStack::default();
    stack.push(Box::new(
        BrowserSurface::new(BrowserKind::Schedules, browser)
            .with_actions(RowActions::new().on_key('d', |id| SurfaceAction::DeleteSchedule(id.to_string()))),
    ));

    match stack.handle_key(key(KeyCode::Delete)) {
        StackOutcome::Action(SurfaceAction::DeleteSchedule(id)) => assert_eq!(id, "nightly"),
        _ => panic!("the Del key did not raise the same action as `d`"),
    }
}
