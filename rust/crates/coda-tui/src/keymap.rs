//! Key bindings.
//!
//! Resolution is a pure function of the key event plus the current UI context,
//! so every binding can be tested without a terminal. Nothing here touches
//! state; it only names the action to perform.

use crossterm::event::{KeyCode, KeyEvent, KeyEventKind, KeyModifiers};

/// What part of the UI has focus.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Focus {
    /// The normal composing view.
    Composer,
    /// A completion popup is open.
    Completion,
    /// A `Surface` is open.
    ///
    /// The surface owns its navigation completely: it has already declined
    /// this key, so nothing should be interpreted on its behalf.
    ///
    /// This replaced a separate `Overlay` focus that became unreachable once
    /// prompts became `Exclusive` surfaces — a prompt now consumes every key
    /// itself, so the keymap is never consulted on its behalf.
    Surface,
}

/// A destructive action that must be confirmed by pressing the key twice.
///
/// The C# shell arms a chord on the first press and fires on the second within
/// a short window, so a single stray keystroke can never quit the application
/// or abandon a turn.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Chord {
    /// `Ctrl+C` pressed once; a second press exits.
    Exit,
    /// `Esc` pressed once while busy; a second press interrupts.
    Interrupt,
}

/// How long an armed chord stays armed.
pub const CHORD_WINDOW: std::time::Duration = std::time::Duration::from_millis(1500);

/// Context that changes how a key is interpreted.
#[derive(Debug, Clone, Copy)]
pub struct KeyContext {
    pub focus: Focus,
    /// A turn is running.
    pub busy: bool,
    /// The composer buffer is empty.
    pub composer_empty: bool,
    /// The cursor is on the composer's first line.
    pub on_first_line: bool,
    /// The cursor is on the composer's last line.
    pub on_last_line: bool,
    /// A chord armed by a previous keystroke and still within its window.
    pub armed: Option<Chord>,
}

impl Default for KeyContext {
    fn default() -> Self {
        Self {
            focus: Focus::Composer,
            busy: false,
            composer_empty: true,
            on_first_line: true,
            on_last_line: true,
            armed: None,
        }
    }
}

/// An action the application should perform.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Action {
    /// Send the composer contents.
    Submit,
    /// Insert a literal newline.
    Newline,
    /// Insert a typed character.
    Insert(char),

    Backspace,
    Delete,
    DeleteWordBack,
    DeleteToLineStart,
    DeleteToLineEnd,

    MoveLeft,
    MoveRight,
    MoveUp,
    MoveDown,
    MoveWordLeft,
    MoveWordRight,
    MoveLineStart,
    MoveLineEnd,

    HistoryPrevious,
    HistoryNext,

    CompletionNext,
    CompletionPrevious,
    /// Fill the input from the popup, without running it.
    CompletionAccept,
    /// Run the command: take the chosen candidate first, if there was one.
    CompletionSubmit,
    CompletionCancel,
    /// Request completions for the token under the cursor.
    CompletionRequest,

    ScrollUp,
    ScrollDown,
    PageUp,
    PageDown,
    ScrollTop,
    ScrollBottom,

    /// Cancel the running turn.
    Interrupt,
    /// Arm a two-press chord and tell the user it is armed.
    Arm(Chord),
    /// Leave the application.
    Quit,
    /// Dismiss an overlay or clear the composer.
    Cancel,
    /// Confirm the focused overlay choice.
    Confirm,
    /// Clear the transcript.
    ClearTranscript,
    /// Force a full repaint.
    Repaint,
    /// Copy the transcript selection.
    Copy,
    /// Paste from the clipboard.
    Paste,

    /// The key has no binding in this context.
    None,
}

/// Resolves a key event to an action.
pub fn resolve(key: KeyEvent, context: KeyContext) -> Action {
    // Windows reports both press and release; acting on both would double every
    // keystroke.
    if key.kind == KeyEventKind::Release {
        return Action::None;
    }

    let ctrl = key.modifiers.contains(KeyModifiers::CONTROL);
    let shift = key.modifiers.contains(KeyModifiers::SHIFT);
    let alt = key.modifiers.contains(KeyModifiers::ALT);

    // Bindings that apply regardless of focus.
    match key.code {
        // Ctrl+C never acts on a single press: it arms, and a second press
        // within the chord window confirms. A stray keystroke must not be able
        // to quit or abandon a running turn.
        KeyCode::Char('c') if ctrl => {
            return match context.armed {
                Some(Chord::Exit) if context.busy => Action::Interrupt,
                Some(Chord::Exit) => Action::Quit,
                _ => Action::Arm(Chord::Exit),
            }
        }
        KeyCode::End if ctrl => return Action::ScrollBottom,
        KeyCode::Home if ctrl => return Action::ScrollTop,
        // Ctrl+L means "redraw" in every other terminal program; it must never
        // destroy the transcript.
        KeyCode::Char('l') if ctrl => return Action::Repaint,
        _ => {}
    }

    match context.focus {
        Focus::Completion => resolve_completion(key, ctrl),
        // Everything reaching here was already declined by the surface, and
        // the global chords were handled above. Swallow the rest.
        Focus::Surface => Action::None,
        Focus::Composer => resolve_composer(key, context, ctrl, shift, alt),
    }
}

fn resolve_completion(key: KeyEvent, ctrl: bool) -> Action {
    match key.code {
        // Tab fills the input and stops there, so arguments can be added and
        // the command read back before it runs. Enter runs. Tab used to cycle
        // the list, which left no key meaning "take this one, I am not done
        // typing yet".
        KeyCode::Tab => Action::CompletionAccept,
        KeyCode::Down => Action::CompletionNext,
        KeyCode::BackTab | KeyCode::Up => Action::CompletionPrevious,
        KeyCode::Enter => Action::CompletionSubmit,
        KeyCode::Esc => Action::CompletionCancel,
        // Typing keeps filtering rather than dismissing the popup.
        KeyCode::Char(c) if !ctrl => Action::Insert(c),
        KeyCode::Backspace => Action::Backspace,
        _ => Action::None,
    }
}


fn resolve_composer(
    key: KeyEvent,
    context: KeyContext,
    ctrl: bool,
    shift: bool,
    alt: bool,
) -> Action {
    match key.code {
        // Enter submits; every modified Enter inserts a newline instead. Not
        // all terminals report Shift+Enter, hence the several accepted chords.
        KeyCode::Enter if shift || ctrl || alt => Action::Newline,
        KeyCode::Char('j') if ctrl => Action::Newline,
        KeyCode::Enter => Action::Submit,

        KeyCode::Backspace if ctrl || alt => Action::DeleteWordBack,
        KeyCode::Backspace => Action::Backspace,
        KeyCode::Delete => Action::Delete,

        KeyCode::Char('w') if ctrl => Action::DeleteWordBack,
        KeyCode::Char('u') if ctrl => Action::DeleteToLineStart,
        KeyCode::Char('k') if ctrl => Action::DeleteToLineEnd,
        KeyCode::Char('v') if ctrl || alt => Action::Paste,
        KeyCode::Char('y') if ctrl => Action::Copy,
        KeyCode::Char('a') if ctrl => Action::MoveLineStart,
        KeyCode::Char('e') if ctrl => Action::MoveLineEnd,

        // Ctrl+arrow forces history navigation from anywhere in a multi-line
        // draft, where a bare Up/Down would move the cursor instead.
        KeyCode::Up if ctrl => Action::HistoryPrevious,
        KeyCode::Down if ctrl => Action::HistoryNext,

        KeyCode::Left if ctrl || alt => Action::MoveWordLeft,
        KeyCode::Right if ctrl || alt => Action::MoveWordRight,
        KeyCode::Left => Action::MoveLeft,
        KeyCode::Right => Action::MoveRight,

        // Up on the first line recalls history; elsewhere it moves the cursor.
        // This keeps a multi-line draft navigable without losing recall.
        KeyCode::Up if context.on_first_line => Action::HistoryPrevious,
        KeyCode::Up => Action::MoveUp,
        KeyCode::Down if context.on_last_line => Action::HistoryNext,
        KeyCode::Down => Action::MoveDown,

        KeyCode::Home => Action::MoveLineStart,
        KeyCode::End => Action::MoveLineEnd,
        KeyCode::PageUp => Action::PageUp,
        KeyCode::PageDown => Action::PageDown,

        KeyCode::Tab => Action::CompletionRequest,

        // While a turn is running Esc arms an interrupt rather than firing one,
        // so a reflexive press cannot throw away work in progress.
        KeyCode::Esc if context.busy => match context.armed {
            Some(Chord::Interrupt) => Action::Interrupt,
            _ => Action::Arm(Chord::Interrupt),
        },
        KeyCode::Esc => Action::Cancel,

        KeyCode::Char(c) if !ctrl => Action::Insert(c),
        _ => Action::None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn with(code: KeyCode, modifiers: KeyModifiers) -> KeyEvent {
        KeyEvent::new(code, modifiers)
    }

    fn composing() -> KeyContext {
        KeyContext::default()
    }

    fn busy() -> KeyContext {
        KeyContext {
            busy: true,
            ..KeyContext::default()
        }
    }

    #[allow(dead_code)]
    fn typing() -> KeyContext {
        KeyContext {
            composer_empty: false,
            ..KeyContext::default()
        }
    }

    #[test]
    fn enter_submits() {
        assert_eq!(resolve(key(KeyCode::Enter), composing()), Action::Submit);
    }

    #[test]
    fn modified_enter_inserts_a_newline() {
        for modifier in [
            KeyModifiers::SHIFT,
            KeyModifiers::CONTROL,
            KeyModifiers::ALT,
        ] {
            assert_eq!(
                resolve(with(KeyCode::Enter, modifier), composing()),
                Action::Newline,
                "{modifier:?} should insert a newline"
            );
        }
    }

    #[test]
    fn printable_characters_are_inserted() {
        assert_eq!(resolve(key(KeyCode::Char('a')), composing()), Action::Insert('a'));
        assert_eq!(resolve(key(KeyCode::Char(' ')), composing()), Action::Insert(' '));
        assert_eq!(resolve(key(KeyCode::Char('/')), composing()), Action::Insert('/'));
    }

    #[test]
    fn shifted_characters_are_still_inserted() {
        assert_eq!(
            resolve(with(KeyCode::Char('A'), KeyModifiers::SHIFT), composing()),
            Action::Insert('A')
        );
    }

    #[test]
    fn a_single_ctrl_c_only_arms_and_never_quits() {
        assert_eq!(
            resolve(with(KeyCode::Char('c'), KeyModifiers::CONTROL), composing()),
            Action::Arm(Chord::Exit),
            "a stray Ctrl+C must not be able to quit"
        );
    }

    #[test]
    fn a_second_ctrl_c_quits_when_idle() {
        let armed = KeyContext { armed: Some(Chord::Exit), ..composing() };
        assert_eq!(
            resolve(with(KeyCode::Char('c'), KeyModifiers::CONTROL), armed),
            Action::Quit
        );
    }

    #[test]
    fn a_second_ctrl_c_interrupts_while_busy_rather_than_quitting() {
        let armed = KeyContext { armed: Some(Chord::Exit), ..busy() };
        assert_eq!(
            resolve(with(KeyCode::Char('c'), KeyModifiers::CONTROL), armed),
            Action::Interrupt
        );
    }

    #[test]
    fn ctrl_l_repaints_and_never_clears_the_transcript() {
        let action = resolve(with(KeyCode::Char('l'), KeyModifiers::CONTROL), composing());
        assert_eq!(action, Action::Repaint);
        assert_ne!(action, Action::ClearTranscript, "Ctrl+L must not be destructive");
    }

    #[test]
    fn a_single_escape_only_arms_an_interrupt_while_busy() {
        assert_eq!(
            resolve(key(KeyCode::Esc), busy()),
            Action::Arm(Chord::Interrupt)
        );
    }

    #[test]
    fn a_second_escape_interrupts() {
        let armed = KeyContext { armed: Some(Chord::Interrupt), ..busy() };
        assert_eq!(resolve(key(KeyCode::Esc), armed), Action::Interrupt);
    }

    #[test]
    fn ctrl_j_inserts_a_newline_for_terminals_without_shift_enter() {
        assert_eq!(
            resolve(with(KeyCode::Char('j'), KeyModifiers::CONTROL), composing()),
            Action::Newline
        );
    }

    #[test]
    fn ctrl_arrows_force_history_from_anywhere_in_a_draft() {
        let interior = KeyContext { on_first_line: false, on_last_line: false, ..composing() };
        assert_eq!(
            resolve(with(KeyCode::Up, KeyModifiers::CONTROL), interior),
            Action::HistoryPrevious
        );
        assert_eq!(
            resolve(with(KeyCode::Down, KeyModifiers::CONTROL), interior),
            Action::HistoryNext
        );
    }

    #[test]
    fn alt_v_pastes_for_terminals_that_swallow_ctrl_v() {
        assert_eq!(
            resolve(with(KeyCode::Char('v'), KeyModifiers::ALT), composing()),
            Action::Paste
        );
    }

    #[test]
    fn key_releases_are_ignored() {
        let mut event = key(KeyCode::Char('a'));
        event.kind = KeyEventKind::Release;
        assert_eq!(resolve(event, composing()), Action::None);
    }

    #[test]
    fn up_recalls_history_from_the_first_line() {
        assert_eq!(
            resolve(key(KeyCode::Up), composing()),
            Action::HistoryPrevious
        );
    }

    #[test]
    fn up_moves_the_cursor_when_not_on_the_first_line() {
        let context = KeyContext {
            on_first_line: false,
            ..composing()
        };
        assert_eq!(resolve(key(KeyCode::Up), context), Action::MoveUp);
    }

    #[test]
    fn down_moves_the_cursor_when_not_on_the_last_line() {
        let context = KeyContext {
            on_last_line: false,
            ..composing()
        };
        assert_eq!(resolve(key(KeyCode::Down), context), Action::MoveDown);
    }

    #[test]
    fn word_movement_uses_ctrl_or_alt() {
        for modifier in [KeyModifiers::CONTROL, KeyModifiers::ALT] {
            assert_eq!(
                resolve(with(KeyCode::Left, modifier), composing()),
                Action::MoveWordLeft
            );
            assert_eq!(
                resolve(with(KeyCode::Right, modifier), composing()),
                Action::MoveWordRight
            );
        }
    }

    #[test]
    fn word_deletion_has_several_chords() {
        for event in [
            with(KeyCode::Backspace, KeyModifiers::CONTROL),
            with(KeyCode::Backspace, KeyModifiers::ALT),
            with(KeyCode::Char('w'), KeyModifiers::CONTROL),
        ] {
            assert_eq!(resolve(event, composing()), Action::DeleteWordBack);
        }
    }

    #[test]
    fn readline_line_editing_chords_are_bound() {
        let cases = [
            ('u', Action::DeleteToLineStart),
            ('k', Action::DeleteToLineEnd),
            ('a', Action::MoveLineStart),
            ('e', Action::MoveLineEnd),
        ];
        for (c, expected) in cases {
            assert_eq!(
                resolve(with(KeyCode::Char(c), KeyModifiers::CONTROL), composing()),
                expected,
                "ctrl+{c}"
            );
        }
    }

    #[test]
    fn home_and_end_work_on_the_line() {
        assert_eq!(resolve(key(KeyCode::Home), composing()), Action::MoveLineStart);
        assert_eq!(resolve(key(KeyCode::End), composing()), Action::MoveLineEnd);
    }

    #[test]
    fn ctrl_end_jumps_the_transcript_to_the_bottom() {
        assert_eq!(
            resolve(with(KeyCode::End, KeyModifiers::CONTROL), composing()),
            Action::ScrollBottom
        );
    }

    #[test]
    fn ctrl_end_works_even_inside_an_overlay() {
        let context = KeyContext {
            focus: Focus::Surface,
            ..composing()
        };
        assert_eq!(
            resolve(with(KeyCode::End, KeyModifiers::CONTROL), context),
            Action::ScrollBottom
        );
    }

    #[test]
    fn paging_scrolls_the_transcript() {
        assert_eq!(resolve(key(KeyCode::PageUp), composing()), Action::PageUp);
        assert_eq!(resolve(key(KeyCode::PageDown), composing()), Action::PageDown);
    }

    #[test]
    fn tab_requests_completions_while_composing() {
        assert_eq!(
            resolve(key(KeyCode::Tab), composing()),
            Action::CompletionRequest
        );
    }

    #[test]
    fn tab_fills_the_input_from_an_open_completion_popup() {
        let context = KeyContext {
            focus: Focus::Completion,
            ..composing()
        };
        assert_eq!(resolve(key(KeyCode::Tab), context), Action::CompletionAccept);
        assert_eq!(
            resolve(key(KeyCode::BackTab), context),
            Action::CompletionPrevious
        );
    }

    #[test]
    fn enter_runs_rather_than_only_filling_the_input() {
        let context = KeyContext {
            focus: Focus::Completion,
            ..composing()
        };
        assert_eq!(resolve(key(KeyCode::Enter), context), Action::CompletionSubmit);
    }

    #[test]
    fn escape_dismisses_a_completion_popup() {
        let context = KeyContext {
            focus: Focus::Completion,
            ..composing()
        };
        assert_eq!(resolve(key(KeyCode::Esc), context), Action::CompletionCancel);
    }

    #[test]
    fn typing_continues_to_filter_an_open_completion() {
        let context = KeyContext {
            focus: Focus::Completion,
            ..composing()
        };
        assert_eq!(resolve(key(KeyCode::Char('x')), context), Action::Insert('x'));
    }

    #[test]
    fn arrows_navigate_an_open_completion() {
        let context = KeyContext {
            focus: Focus::Completion,
            ..composing()
        };
        assert_eq!(resolve(key(KeyCode::Down), context), Action::CompletionNext);
        assert_eq!(resolve(key(KeyCode::Up), context), Action::CompletionPrevious);
    }

    #[test]
    fn a_surface_swallows_everything_it_declined() {
        // A surface owns its own navigation and has already had first refusal
        // on this key, so the keymap must interpret nothing on its behalf.
        // Anything else would act twice on one keystroke, or act on a key the
        // surface deliberately ignored.
        let context = KeyContext {
            focus: Focus::Surface,
            ..composing()
        };
        for code in [
            KeyCode::Down,
            KeyCode::Up,
            KeyCode::Char('j'),
            KeyCode::Char('k'),
            KeyCode::Enter,
            KeyCode::Esc,
            KeyCode::Char('z'),
            KeyCode::Backspace,
            KeyCode::Tab,
        ] {
            assert_eq!(
                resolve(key(code), context),
                Action::None,
                "{code:?} was interpreted on an open surface's behalf"
            );
        }
    }

    #[test]
    fn escape_cancels_while_composing_and_idle() {
        assert_eq!(resolve(key(KeyCode::Esc), composing()), Action::Cancel);
    }

    #[test]
    fn unbound_keys_resolve_to_nothing() {
        assert_eq!(resolve(key(KeyCode::F(5)), composing()), Action::None);
        assert_eq!(resolve(key(KeyCode::Insert), composing()), Action::None);
    }

    #[test]
    fn ctrl_chords_never_leak_through_as_typed_characters() {
        for c in 'a'..='z' {
            let action = resolve(with(KeyCode::Char(c), KeyModifiers::CONTROL), composing());
            assert_ne!(
                action,
                Action::Insert(c),
                "ctrl+{c} was inserted as a literal character"
            );
        }
    }

    #[test]
    fn tab_completes_and_enter_runs() {
        // Tab fills the input from the popup; Enter runs what is in the
        // input. Tab used to cycle the list, which left no key that meant
        // "take this one and let me add arguments" -- Enter did that instead,
        // so there was no way to run a chosen command without a second press.
        let context = KeyContext {
            focus: Focus::Completion,
            ..composing()
        };
        assert_eq!(resolve(key(KeyCode::Tab), context), Action::CompletionAccept);
        assert_eq!(resolve(key(KeyCode::Enter), context), Action::CompletionSubmit);
    }

    #[test]
    fn the_arrows_still_move_through_the_completion_list() {
        // Tab no longer cycles, so the arrows are the only way to choose.
        // Losing them would leave the list unnavigable.
        let context = KeyContext {
            focus: Focus::Completion,
            ..composing()
        };
        assert_eq!(resolve(key(KeyCode::Down), context), Action::CompletionNext);
        assert_eq!(resolve(key(KeyCode::Up), context), Action::CompletionPrevious);
        assert_eq!(
            resolve(key(KeyCode::BackTab), context),
            Action::CompletionPrevious
        );
    }
}