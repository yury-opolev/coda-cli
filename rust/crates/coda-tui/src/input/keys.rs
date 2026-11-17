//! Pure key mapping for console key records and VT sequences.
//!
//! [`map_key_record`] is a port of crossterm 0.29's Windows
//! `parse_key_event_record` (crossterm is MIT licensed, copyright
//! crossterm-rs contributors), with the layout lookup through `ToUnicodeEx`
//! replaced by a fixed US mapping so this module stays platform independent.
//! [`vt_char`], [`csi_key`] and [`ss3_key`] mirror crossterm's Unix parser.

use crossterm::event::{KeyCode, KeyEvent, KeyEventKind, KeyModifiers};

use crate::input::decoder::KeyRecord;

pub const RIGHT_ALT_PRESSED: u32 = 0x1;
pub const LEFT_ALT_PRESSED: u32 = 0x2;
pub const RIGHT_CTRL_PRESSED: u32 = 0x4;
pub const LEFT_CTRL_PRESSED: u32 = 0x8;
pub const SHIFT_PRESSED: u32 = 0x10;
pub const CAPSLOCK_ON: u32 = 0x80;

pub const VK_BACK: u16 = 0x08;
pub const VK_TAB: u16 = 0x09;
pub const VK_RETURN: u16 = 0x0D;
pub const VK_SHIFT: u16 = 0x10;
pub const VK_CONTROL: u16 = 0x11;
pub const VK_MENU: u16 = 0x12;
pub const VK_ESCAPE: u16 = 0x1B;
pub const VK_PRIOR: u16 = 0x21;
pub const VK_NEXT: u16 = 0x22;
pub const VK_END: u16 = 0x23;
pub const VK_HOME: u16 = 0x24;
pub const VK_LEFT: u16 = 0x25;
pub const VK_UP: u16 = 0x26;
pub const VK_RIGHT: u16 = 0x27;
pub const VK_DOWN: u16 = 0x28;
pub const VK_INSERT: u16 = 0x2D;
pub const VK_DELETE: u16 = 0x2E;
pub const VK_NUMPAD0: u16 = 0x60;
pub const VK_NUMPAD9: u16 = 0x69;
pub const VK_F1: u16 = 0x70;
pub const VK_F24: u16 = 0x87;

/// Converts a console `dwControlKeyState` into crossterm modifiers.
pub fn modifiers_from_state(state: u32) -> KeyModifiers {
    let mut modifiers = KeyModifiers::empty();
    if state & SHIFT_PRESSED != 0 {
        modifiers |= KeyModifiers::SHIFT;
    }
    if state & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED) != 0 {
        modifiers |= KeyModifiers::CONTROL;
    }
    if state & (LEFT_ALT_PRESSED | RIGHT_ALT_PRESSED) != 0 {
        modifiers |= KeyModifiers::ALT;
    }
    modifiers
}

/// The result of mapping one console key record.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MappedKey {
    Key(KeyEvent),
    /// Half of a UTF-16 surrogate pair; the caller joins the halves.
    Surrogate {
        unit: u16,
        modifiers: KeyModifiers,
        kind: KeyEventKind,
    },
}

fn kind_of(down: bool) -> KeyEventKind {
    if down {
        KeyEventKind::Press
    } else {
        KeyEventKind::Release
    }
}

fn unit_to_mapped(unit: u16, modifiers: KeyModifiers, kind: KeyEventKind) -> Option<MappedKey> {
    if (0xD800..=0xDFFF).contains(&unit) {
        return Some(MappedKey::Surrogate {
            unit,
            modifiers,
            kind,
        });
    }
    char::from_u32(u32::from(unit))
        .map(|ch| MappedKey::Key(KeyEvent::new_with_kind(KeyCode::Char(ch), modifiers, kind)))
}

/// The character a key produces on a US layout, used when the record carries
/// a control code (Ctrl+letter) or no character at all.
fn char_for_vk(vk: u16, state: u32) -> Option<char> {
    let byte = u8::try_from(vk).ok()?;
    match byte {
        0x41..=0x5A => {
            let upper = (state & SHIFT_PRESSED != 0) ^ (state & CAPSLOCK_ON != 0);
            let ch = char::from(byte);
            Some(if upper { ch } else { ch.to_ascii_lowercase() })
        }
        0x30..=0x39 | 0x20 => Some(char::from(byte)),
        0xBA => Some(';'),
        0xBB => Some('='),
        0xBC => Some(','),
        0xBD => Some('-'),
        0xBE => Some('.'),
        0xBF => Some('/'),
        0xC0 => Some('`'),
        0xDB => Some('['),
        0xDC => Some('\\'),
        0xDD => Some(']'),
        0xDE => Some('\''),
        _ => None,
    }
}

/// Maps a console key record to a key event, following crossterm's rules.
pub fn map_key_record(r: &KeyRecord) -> Option<MappedKey> {
    let modifiers = modifiers_from_state(r.state);
    let kind = kind_of(r.down);

    // Key releases are normally ignored by callers, but an Alt release that
    // carries a character is an Alt code.
    if r.vk == VK_MENU && !r.down && r.unit != 0 {
        return unit_to_mapped(r.unit, modifiers, kind);
    }

    // No events for numpad digits while they compose an Alt code.
    let only_alt = modifiers.contains(KeyModifiers::ALT)
        && !modifiers.intersects(KeyModifiers::SHIFT | KeyModifiers::CONTROL);
    if only_alt && (VK_NUMPAD0..=VK_NUMPAD9).contains(&r.vk) {
        return None;
    }

    let code = match r.vk {
        VK_SHIFT | VK_CONTROL | VK_MENU => return None,
        VK_BACK => KeyCode::Backspace,
        VK_ESCAPE => KeyCode::Esc,
        VK_RETURN => KeyCode::Enter,
        VK_F1..=VK_F24 => KeyCode::F(u8::try_from(r.vk - VK_F1 + 1).ok()?),
        VK_LEFT => KeyCode::Left,
        VK_UP => KeyCode::Up,
        VK_RIGHT => KeyCode::Right,
        VK_DOWN => KeyCode::Down,
        VK_PRIOR => KeyCode::PageUp,
        VK_NEXT => KeyCode::PageDown,
        VK_HOME => KeyCode::Home,
        VK_END => KeyCode::End,
        VK_DELETE => KeyCode::Delete,
        VK_INSERT => KeyCode::Insert,
        VK_TAB if modifiers.contains(KeyModifiers::SHIFT) => KeyCode::BackTab,
        VK_TAB => KeyCode::Tab,
        _ => match r.unit {
            0x00..=0x1f => KeyCode::Char(char_for_vk(r.vk, r.state)?),
            unit => return unit_to_mapped(unit, modifiers, kind),
        },
    };
    Some(MappedKey::Key(KeyEvent::new_with_kind(
        code, modifiers, kind,
    )))
}

/// Maps one plain VT character, as crossterm's Unix parser does.
pub fn vt_char(c: char, alt: bool) -> KeyEvent {
    let (code, mut modifiers) = match c {
        '\r' => (KeyCode::Enter, KeyModifiers::NONE),
        '\n' => (KeyCode::Char('j'), KeyModifiers::CONTROL),
        '\t' => (KeyCode::Tab, KeyModifiers::NONE),
        '\x7f' | '\x08' => (KeyCode::Backspace, KeyModifiers::NONE),
        '\x1b' => (KeyCode::Esc, KeyModifiers::NONE),
        '\0' => (KeyCode::Char(' '), KeyModifiers::CONTROL),
        '\x01'..='\x1a' => (
            KeyCode::Char(char::from(c as u8 - 0x01 + b'a')),
            KeyModifiers::CONTROL,
        ),
        '\x1c'..='\x1f' => (
            KeyCode::Char(char::from(c as u8 - 0x1c + b'4')),
            KeyModifiers::CONTROL,
        ),
        c if c.is_uppercase() && c.is_alphabetic() => (KeyCode::Char(c), KeyModifiers::SHIFT),
        c => (KeyCode::Char(c), KeyModifiers::NONE),
    };
    if alt {
        modifiers |= KeyModifiers::ALT;
    }
    KeyEvent::new(code, modifiers)
}

/// Decodes an xterm modifier parameter (`1 + bits`).
fn xterm_modifiers(param: Option<&str>) -> Option<KeyModifiers> {
    let Some(param) = param.filter(|p| !p.is_empty()) else {
        return Some(KeyModifiers::NONE);
    };
    let bits = param.parse::<u8>().ok()?.saturating_sub(1);
    let mut modifiers = KeyModifiers::NONE;
    if bits & 1 != 0 {
        modifiers |= KeyModifiers::SHIFT;
    }
    if bits & 2 != 0 {
        modifiers |= KeyModifiers::ALT;
    }
    if bits & 4 != 0 {
        modifiers |= KeyModifiers::CONTROL;
    }
    Some(modifiers)
}

/// Maps a CSI sequence (`ESC [ params final`) to a key, if it is one.
pub fn csi_key(params: &str, final_byte: char) -> Option<KeyEvent> {
    let mut parts = params.split(';');
    let first = parts.next().filter(|p| !p.is_empty());
    let modifiers = xterm_modifiers(parts.next())?;
    if parts.next().is_some() {
        return None;
    }
    let number = |p: Option<&str>| -> Option<u32> {
        match p {
            None => Some(1),
            Some(p) => p.parse().ok(),
        }
    };

    let code = match final_byte {
        'A' => KeyCode::Up,
        'B' => KeyCode::Down,
        'C' => KeyCode::Right,
        'D' => KeyCode::Left,
        'H' => KeyCode::Home,
        'F' => KeyCode::End,
        'P' => KeyCode::F(1),
        'Q' => KeyCode::F(2),
        'R' => KeyCode::F(3),
        'S' => KeyCode::F(4),
        'Z' => {
            return Some(KeyEvent::new(
                KeyCode::BackTab,
                modifiers | KeyModifiers::SHIFT,
            ));
        }
        '~' => match number(first)? {
            1 | 7 => KeyCode::Home,
            2 => KeyCode::Insert,
            3 => KeyCode::Delete,
            4 | 8 => KeyCode::End,
            5 => KeyCode::PageUp,
            6 => KeyCode::PageDown,
            n @ 11..=15 => KeyCode::F(u8::try_from(n - 10).ok()?),
            n @ 17..=21 => KeyCode::F(u8::try_from(n - 11).ok()?),
            23 => KeyCode::F(11),
            24 => KeyCode::F(12),
            _ => return None,
        },
        'u' => match first?.parse::<u32>().ok()? {
            13 => KeyCode::Enter,
            9 => KeyCode::Tab,
            27 => KeyCode::Esc,
            127 => KeyCode::Backspace,
            n => KeyCode::Char(char::from_u32(n)?),
        },
        _ => return None,
    };
    Some(KeyEvent::new(code, modifiers))
}

/// Maps an SS3 sequence (`ESC O c`) to a key.
pub fn ss3_key(c: char) -> Option<KeyEvent> {
    let code = match c {
        'A' => KeyCode::Up,
        'B' => KeyCode::Down,
        'C' => KeyCode::Right,
        'D' => KeyCode::Left,
        'H' => KeyCode::Home,
        'F' => KeyCode::End,
        'P' => KeyCode::F(1),
        'Q' => KeyCode::F(2),
        'R' => KeyCode::F(3),
        'S' => KeyCode::F(4),
        _ => return None,
    };
    Some(KeyEvent::new(code, KeyModifiers::NONE))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn record(vk: u16, unit: u16, down: bool, state: u32) -> KeyRecord {
        KeyRecord {
            vk,
            unit,
            down,
            state,
            repeat: 1,
            ..KeyRecord::default()
        }
    }

    fn key(code: KeyCode, modifiers: KeyModifiers) -> Option<MappedKey> {
        Some(MappedKey::Key(KeyEvent::new(code, modifiers)))
    }

    #[test]
    fn shift_enter_keeps_its_modifier() {
        let mapped = map_key_record(&record(VK_RETURN, 13, true, SHIFT_PRESSED));
        assert_eq!(mapped, key(KeyCode::Enter, KeyModifiers::SHIFT));
    }

    #[test]
    fn ctrl_letter_control_code_maps_back_to_the_letter() {
        let mapped = map_key_record(&record(0x43, 3, true, LEFT_CTRL_PRESSED));
        assert_eq!(mapped, key(KeyCode::Char('c'), KeyModifiers::CONTROL));
    }

    #[test]
    fn shift_tab_is_back_tab() {
        let mapped = map_key_record(&record(VK_TAB, 9, true, SHIFT_PRESSED));
        assert_eq!(mapped, key(KeyCode::BackTab, KeyModifiers::SHIFT));
    }

    #[test]
    fn an_alt_release_with_a_character_is_an_alt_code() {
        let mapped = map_key_record(&record(VK_MENU, 0xE9, false, 0));
        assert_eq!(
            mapped,
            Some(MappedKey::Key(KeyEvent::new_with_kind(
                KeyCode::Char('\u{e9}'),
                KeyModifiers::NONE,
                KeyEventKind::Release
            )))
        );
    }

    #[test]
    fn numpad_digits_composing_an_alt_code_are_silent() {
        assert_eq!(
            map_key_record(&record(0x61, 0, true, LEFT_ALT_PRESSED)),
            None
        );
    }

    #[test]
    fn a_plain_letter_is_a_press() {
        let mapped = map_key_record(&record(0x41, u16::from(b'a'), true, 0));
        assert_eq!(mapped, key(KeyCode::Char('a'), KeyModifiers::NONE));
    }

    #[test]
    fn modifier_keys_and_surrogates() {
        assert_eq!(map_key_record(&record(VK_CONTROL, 0, true, 8)), None);
        assert_eq!(
            map_key_record(&record(0, 0xD83D, true, 0)),
            Some(MappedKey::Surrogate {
                unit: 0xD83D,
                modifiers: KeyModifiers::NONE,
                kind: KeyEventKind::Press
            })
        );
        assert_eq!(
            map_key_record(&record(VK_F1 + 4, 0, true, 0)),
            key(KeyCode::F(5), KeyModifiers::NONE)
        );
    }

    #[test]
    fn vt_characters_follow_the_unix_parser() {
        let none = KeyModifiers::NONE;
        assert_eq!(vt_char('\r', false), KeyEvent::new(KeyCode::Enter, none));
        assert_eq!(
            vt_char('\n', false),
            KeyEvent::new(KeyCode::Char('j'), KeyModifiers::CONTROL)
        );
        assert_eq!(
            vt_char('\x7f', false),
            KeyEvent::new(KeyCode::Backspace, none)
        );
        assert_eq!(
            vt_char('\x01', false),
            KeyEvent::new(KeyCode::Char('a'), KeyModifiers::CONTROL)
        );
        assert_eq!(
            vt_char('A', false),
            KeyEvent::new(KeyCode::Char('A'), KeyModifiers::SHIFT)
        );
        assert_eq!(
            vt_char('x', true),
            KeyEvent::new(KeyCode::Char('x'), KeyModifiers::ALT)
        );
    }

    #[test]
    fn csi_sequences_map_to_keys() {
        assert_eq!(
            csi_key("1;5", 'A'),
            Some(KeyEvent::new(KeyCode::Up, KeyModifiers::CONTROL))
        );
        assert_eq!(
            csi_key("3", '~'),
            Some(KeyEvent::new(KeyCode::Delete, KeyModifiers::NONE))
        );
        assert_eq!(
            csi_key("", 'Z'),
            Some(KeyEvent::new(KeyCode::BackTab, KeyModifiers::SHIFT))
        );
        assert_eq!(
            csi_key("97;5", 'u'),
            Some(KeyEvent::new(KeyCode::Char('a'), KeyModifiers::CONTROL))
        );
        assert_eq!(
            csi_key("15", '~'),
            Some(KeyEvent::new(KeyCode::F(5), KeyModifiers::NONE))
        );
        assert_eq!(csi_key("?1", 'h'), None);
        assert_eq!(csi_key("99", '~'), None);
    }

    #[test]
    fn ss3_sequences_map_to_keys() {
        assert_eq!(
            ss3_key('P'),
            Some(KeyEvent::new(KeyCode::F(1), KeyModifiers::NONE))
        );
        assert_eq!(ss3_key('x'), None);
    }
}
