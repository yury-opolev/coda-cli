//! Pure decoder from console input records to [`InputAction`]s.

use std::collections::VecDeque;
use std::time::{Duration, Instant};

use crossterm::event::{
    KeyCode, KeyEvent, KeyEventKind, KeyModifiers, MouseButton, MouseEvent, MouseEventKind,
};

use crate::input::keys::{self, MappedKey};
use crate::input::{InputAction, PasteAbort, PasteSource};

pub const MAX_PASTE_BYTES: usize = 1 << 20;
pub const ESC_TIMEOUT: Duration = Duration::from_millis(30);
pub const PASTE_STALL_TIMEOUT: Duration = Duration::from_secs(2);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct KeyRecord {
    pub vk: u16,
    pub scan: u16,
    pub unit: u16,
    pub down: bool,
    pub state: u32,
    pub repeat: u16,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MouseRecord {
    pub column: u16,
    pub row: u16,
    pub buttons: u32,
    pub flags: u32,
    pub state: u32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Record {
    Key(KeyRecord),
    Mouse(MouseRecord),
    Resize { cols: u16, rows: u16 },
    Focus(bool),
}

const ESC: char = '\x1b';
/// The bracketed-paste end marker, matched one character at a time.
const PASTE_END: [char; 6] = ['\x1b', '[', '2', '0', '1', '~'];
/// Longest win32-input-mode wrapper body the outer stage buffers.
const MAX_WRAPPER_CHARS: usize = 32;
const MAX_WRAPPER_PARAMS: usize = 6;
const MAX_CSI_CHARS: usize = 64;

const FROM_LEFT_1ST_BUTTON_PRESSED: u32 = 0x1;
const RIGHTMOST_BUTTON_PRESSED: u32 = 0x2;
const FROM_LEFT_2ND_BUTTON_PRESSED: u32 = 0x4;
const MOUSE_MOVED: u32 = 0x1;
const DOUBLE_CLICK: u32 = 0x2;
const MOUSE_WHEELED: u32 = 0x4;
const MOUSE_HWHEELED: u32 = 0x8;

#[derive(Debug, Default, Clone, Copy)]
struct MouseButtonsPressed {
    left: bool,
    right: bool,
    middle: bool,
}

/// An open bracketed paste.
#[derive(Debug)]
struct Paste {
    txn: u64,
    text: String,
    /// UTF-8 bytes of normalized content seen, stored or not.
    bytes: usize,
    overflowed: bool,
    /// How many characters of [`PASTE_END`] are tentatively matched.
    held: usize,
    last_cr: bool,
}

impl Paste {
    fn new(txn: u64) -> Self {
        Self {
            txn,
            text: String::new(),
            bytes: 0,
            overflowed: false,
            held: 0,
            last_cr: false,
        }
    }

    /// Appends content, folding `\r\n` and lone `\r` into `\n`.
    fn push_literal(&mut self, c: char) {
        let c = match c {
            '\r' => {
                self.last_cr = true;
                '\n'
            }
            '\n' if self.last_cr => {
                self.last_cr = false;
                return;
            }
            other => {
                self.last_cr = false;
                other
            }
        };
        let len = c.len_utf8();
        self.bytes += len;
        if self.overflowed {
            return;
        }
        if self.text.len() + len > MAX_PASTE_BYTES {
            self.overflowed = true;
            self.text = String::new();
            return;
        }
        self.text.push(c);
    }
}

/// The VT parser stage.
#[derive(Debug, Default)]
enum Inner {
    #[default]
    Ground,
    Esc,
    Csi(String),
    Ss3,
    Paste(Paste),
}

/// A UTF-16 unit resolved to a character, or a surrogate without a partner.
enum Unit {
    Char(char),
    Lone,
}

/// Joins UTF-16 units through `pending`, yielding up to two results.
fn join_unit(pending: &mut Option<u16>, unit: u16) -> [Option<Unit>; 2] {
    let mut out = [None, None];
    if (0xDC00..0xE000).contains(&unit) {
        out[0] = Some(match pending.take() {
            Some(high) => char::decode_utf16([high, unit])
                .next()
                .and_then(Result::ok)
                .map_or(Unit::Lone, Unit::Char),
            None => Unit::Lone,
        });
        return out;
    }
    if pending.take().is_some() {
        out[0] = Some(Unit::Lone);
    }
    if (0xD800..0xDC00).contains(&unit) {
        *pending = Some(unit);
    } else {
        out[1] = Some(char::from_u32(u32::from(unit)).map_or(Unit::Lone, Unit::Char));
    }
    out
}

/// Parses the body of a win32-input-mode wrapper (`Vk;Sc;Uc;Kd;Cs;Rc`).
fn parse_wrapper(body: &str) -> Option<KeyRecord> {
    let mut values = [0u32, 0, 0, 0, 0, 1];
    for (index, part) in body.split(';').enumerate() {
        let slot = values.get_mut(index)?;
        if !part.is_empty() {
            *slot = part.parse().ok()?;
        }
    }
    let [vk, scan, unit, down, state, repeat] = values;
    Some(KeyRecord {
        vk: u16::try_from(vk).ok()?,
        scan: u16::try_from(scan).ok()?,
        unit: u16::try_from(unit).ok()?,
        down: down == 1,
        state,
        repeat: u16::try_from(repeat).ok()?,
    })
}

/// Decodes an SGR mouse report body (after `<`).
fn sgr_mouse(params: &str, press: bool) -> Option<MouseEvent> {
    let mut parts = params.split(';');
    let code: u16 = parts.next()?.parse().ok()?;
    let x: u16 = parts.next()?.parse().ok()?;
    let y: u16 = parts.next()?.parse().ok()?;
    if parts.next().is_some() {
        return None;
    }
    let mut modifiers = KeyModifiers::NONE;
    if code & 4 != 0 {
        modifiers |= KeyModifiers::SHIFT;
    }
    if code & 8 != 0 {
        modifiers |= KeyModifiers::ALT;
    }
    if code & 16 != 0 {
        modifiers |= KeyModifiers::CONTROL;
    }
    let button = match code & 3 {
        0 => Some(MouseButton::Left),
        1 => Some(MouseButton::Middle),
        2 => Some(MouseButton::Right),
        _ => None,
    };
    let kind = if code & 64 != 0 {
        match code & 3 {
            0 => MouseEventKind::ScrollUp,
            1 => MouseEventKind::ScrollDown,
            2 => MouseEventKind::ScrollLeft,
            _ => MouseEventKind::ScrollRight,
        }
    } else if code & 32 != 0 {
        button.map_or(MouseEventKind::Moved, MouseEventKind::Drag)
    } else {
        let button = button.unwrap_or(MouseButton::Left);
        if press {
            MouseEventKind::Down(button)
        } else {
            MouseEventKind::Up(button)
        }
    };
    Some(MouseEvent {
        kind,
        column: x.saturating_sub(1),
        row: y.saturating_sub(1),
        modifiers,
    })
}

/// Decodes console input records into [`InputAction`]s.
///
/// Two stages run over characters. The outer stage extracts
/// win32-input-mode wrappers (`ESC [ Vk;Sc;Uc;Kd;Cs;Rc _`) and forwards
/// everything else; the inner stage is a VT parser that also assembles
/// bracketed pastes. Records with a real virtual-key code take the key path.
#[derive(Debug, Default)]
pub struct Decoder {
    queue: VecDeque<InputAction>,
    /// Buffered outer-stage characters: empty, `ESC`, or `ESC [` + params.
    outer: String,
    inner: Inner,
    outer_high: Option<u16>,
    inner_high: Option<u16>,
    key_high: Option<u16>,
    buttons: MouseButtonsPressed,
    txn: u64,
    last_input: Option<Instant>,
}

impl Decoder {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn feed(&mut self, record: Record, now: Instant) {
        self.last_input = Some(now);
        match record {
            Record::Resize { cols, rows } => self.push(InputAction::Resize(cols, rows)),
            Record::Focus(true) => self.push(InputAction::FocusGained),
            Record::Focus(false) => self.push(InputAction::FocusLost),
            Record::Mouse(mouse) => self.native_mouse(mouse),
            Record::Key(key) if key.vk == 0 && key.down && key.unit != 0 => {
                for unit in join_unit(&mut self.outer_high, key.unit)
                    .into_iter()
                    .flatten()
                {
                    if let Some(c) = self.resolve(unit) {
                        self.outer_char(c);
                    }
                }
            }
            Record::Key(key) => self.key_record(key),
        }
    }

    /// Resolves timeouts: a lone or partial escape after [`ESC_TIMEOUT`], an
    /// open paste after [`PASTE_STALL_TIMEOUT`].
    pub fn tick(&mut self, now: Instant) {
        let Some(last) = self.last_input else {
            return;
        };
        let idle = now.saturating_duration_since(last);
        if self.in_paste() {
            if idle >= PASTE_STALL_TIMEOUT {
                self.abort_paste(PasteAbort::Incomplete);
            }
            return;
        }
        if idle >= ESC_TIMEOUT && self.has_pending() {
            self.flush_partial();
        }
    }

    /// Aborts an open paste and discards every partial sequence.
    pub fn cancel(&mut self) {
        self.abort_paste(PasteAbort::Cancelled);
    }

    pub fn next_action(&mut self) -> Option<InputAction> {
        self.queue.pop_front()
    }

    /// Whether an action is ready for [`next_action`](Self::next_action).
    pub fn has_action(&self) -> bool {
        !self.queue.is_empty()
    }

    pub fn in_paste(&self) -> bool {
        matches!(self.inner, Inner::Paste(_))
    }

    pub fn has_pending(&self) -> bool {
        !self.outer.is_empty() || !matches!(self.inner, Inner::Ground)
    }

    fn push(&mut self, action: InputAction) {
        self.queue.push_back(action);
    }

    fn push_key(&mut self, event: KeyEvent) {
        self.push(InputAction::Key(event));
    }

    /// A lone surrogate is kept as U+FFFD inside a paste and dropped otherwise.
    fn resolve(&self, unit: Unit) -> Option<char> {
        match unit {
            Unit::Char(c) => Some(c),
            Unit::Lone => self.in_paste().then_some(char::REPLACEMENT_CHARACTER),
        }
    }

    fn outer_char(&mut self, c: char) {
        if self.outer.is_empty() {
            if c == ESC {
                self.outer.push(c);
            } else {
                self.inner_char(c);
            }
            return;
        }
        if self.outer.len() == 1 {
            if c == '[' {
                self.outer.push(c);
            } else {
                self.outer.clear();
                self.inner_char(ESC);
                self.outer_char(c);
            }
            return;
        }
        if c == '_' {
            let wrapper = std::mem::take(&mut self.outer);
            match parse_wrapper(&wrapper[2..]) {
                Some(record) => self.wrapped_record(record),
                None => {
                    self.forward(&wrapper);
                    self.inner_char(c);
                }
            }
            return;
        }
        let params = self.outer.matches(';').count() + 1;
        let fits =
            self.outer.len() < MAX_WRAPPER_CHARS && (c != ';' || params < MAX_WRAPPER_PARAMS);
        if (c.is_ascii_digit() || c == ';') && fits {
            self.outer.push(c);
            return;
        }
        let buffered = std::mem::take(&mut self.outer);
        self.forward(&buffered);
        self.inner_char(c);
    }

    fn forward(&mut self, chars: &str) {
        for c in chars.chars() {
            self.inner_char(c);
        }
    }

    fn wrapped_record(&mut self, record: KeyRecord) {
        if record.vk == 0 && record.down && record.unit != 0 {
            for unit in join_unit(&mut self.inner_high, record.unit)
                .into_iter()
                .flatten()
            {
                if let Some(c) = self.resolve(unit) {
                    self.inner_char(c);
                }
            }
        } else {
            self.key_record(record);
        }
    }

    /// The key path: records that carry a real virtual-key code.
    fn key_record(&mut self, record: KeyRecord) {
        let alt_code = record.vk == keys::VK_MENU && !record.down && record.unit != 0;
        if self.in_paste() {
            if alt_code || (record.down && record.unit != 0) {
                for unit in join_unit(&mut self.key_high, record.unit)
                    .into_iter()
                    .flatten()
                {
                    if let Some(c) = self.resolve(unit) {
                        self.paste_char(c);
                    }
                }
            }
            return;
        }
        let pressed = |kind: KeyEventKind| alt_code || kind != KeyEventKind::Release;
        match keys::map_key_record(&record) {
            None => {}
            Some(MappedKey::Key(event)) => {
                if !pressed(event.kind) {
                    return;
                }
                self.key_high = None;
                self.flush_partial();
                self.push_key(KeyEvent::new_with_kind(
                    event.code,
                    event.modifiers,
                    KeyEventKind::Press,
                ));
            }
            Some(MappedKey::Surrogate {
                unit,
                modifiers,
                kind,
            }) => {
                if !pressed(kind) {
                    return;
                }
                for unit in join_unit(&mut self.key_high, unit).into_iter().flatten() {
                    if let Unit::Char(c) = unit {
                        self.flush_partial();
                        self.push_key(KeyEvent::new_with_kind(
                            KeyCode::Char(c),
                            modifiers,
                            KeyEventKind::Press,
                        ));
                    }
                }
            }
        }
    }

    /// Resolves buffered partial escapes as typed keys, as if they timed out.
    fn flush_partial(&mut self) {
        if self.in_paste() {
            return;
        }
        let buffered = std::mem::take(&mut self.outer);
        self.forward(&buffered);
        match std::mem::take(&mut self.inner) {
            Inner::Esc => self.push_key(keys::vt_char(ESC, false)),
            Inner::Csi(params) => {
                self.push_key(keys::vt_char('[', true));
                for c in params.chars() {
                    self.push_key(keys::vt_char(c, false));
                }
            }
            Inner::Ss3 => self.push_key(keys::vt_char('O', true)),
            other => self.inner = other,
        }
    }

    fn inner_char(&mut self, c: char) {
        match &mut self.inner {
            Inner::Ground => {
                if c == ESC {
                    self.inner = Inner::Esc;
                } else {
                    self.push_key(keys::vt_char(c, false));
                }
            }
            Inner::Esc => match c {
                '[' => self.inner = Inner::Csi(String::new()),
                'O' => self.inner = Inner::Ss3,
                ESC => self.push_key(keys::vt_char(ESC, false)),
                _ => {
                    self.inner = Inner::Ground;
                    self.push_key(keys::vt_char(c, true));
                }
            },
            Inner::Csi(params) => {
                if ('\x40'..='\x7e').contains(&c) {
                    let params = std::mem::take(params);
                    self.inner = Inner::Ground;
                    self.finish_csi(&params, c);
                } else if c == ESC {
                    self.inner = Inner::Esc;
                } else {
                    params.push(c);
                    if params.len() > MAX_CSI_CHARS {
                        self.inner = Inner::Ground;
                    }
                }
            }
            Inner::Ss3 => {
                self.inner = Inner::Ground;
                if let Some(event) = keys::ss3_key(c) {
                    self.push_key(event);
                }
            }
            Inner::Paste(_) => self.paste_char(c),
        }
    }

    fn finish_csi(&mut self, params: &str, final_byte: char) {
        match (params, final_byte) {
            ("200", '~') => {
                self.txn += 1;
                self.push(InputAction::PasteStarted { txn: self.txn });
                self.inner = Inner::Paste(Paste::new(self.txn));
            }
            ("201", '~') => {}
            ("", 'I') => self.push(InputAction::FocusGained),
            ("", 'O') => self.push(InputAction::FocusLost),
            _ => {
                if let Some(rest) = params.strip_prefix('<') {
                    if matches!(final_byte, 'M' | 'm') {
                        if let Some(event) = sgr_mouse(rest, final_byte == 'M') {
                            self.push(InputAction::Mouse(event));
                        }
                    }
                } else if let Some(event) = keys::csi_key(params, final_byte) {
                    self.push_key(event);
                }
            }
        }
    }

    /// The single entry for paste content from every path.
    fn paste_char(&mut self, c: char) {
        let Inner::Paste(paste) = &mut self.inner else {
            return;
        };
        loop {
            if c == PASTE_END[paste.held] {
                paste.held += 1;
                if paste.held == PASTE_END.len() {
                    self.end_paste();
                }
                return;
            }
            if paste.held == 0 {
                paste.push_literal(c);
                return;
            }
            let held = std::mem::take(&mut paste.held);
            for &h in &PASTE_END[..held] {
                paste.push_literal(h);
            }
        }
    }

    fn end_paste(&mut self) {
        if let Inner::Paste(paste) = std::mem::take(&mut self.inner) {
            let action = if paste.overflowed {
                InputAction::PasteAborted {
                    txn: paste.txn,
                    reason: PasteAbort::TooLarge,
                    bytes: paste.bytes,
                }
            } else {
                InputAction::PasteText {
                    text: paste.text,
                    source: PasteSource::Terminal,
                    txn: paste.txn,
                }
            };
            self.push(action);
        }
    }

    fn abort_paste(&mut self, reason: PasteAbort) {
        if let Inner::Paste(paste) = std::mem::take(&mut self.inner) {
            self.push(InputAction::PasteAborted {
                txn: paste.txn,
                reason,
                bytes: paste.bytes,
            });
        }
        self.outer.clear();
        self.outer_high = None;
        self.inner_high = None;
        self.key_high = None;
    }

    /// A port of crossterm's Windows `parse_mouse_event_record`.
    fn native_mouse(&mut self, record: MouseRecord) {
        let left = record.buttons & FROM_LEFT_1ST_BUTTON_PRESSED != 0;
        let right = record.buttons & RIGHTMOST_BUTTON_PRESSED != 0;
        let middle = record.buttons & FROM_LEFT_2ND_BUTTON_PRESSED != 0;
        let was = self.buttons;
        let delta = (record.buttons >> 16) as u16 as i16;
        let kind = match record.flags {
            0 | DOUBLE_CLICK => {
                if left && !was.left {
                    Some(MouseEventKind::Down(MouseButton::Left))
                } else if !left && was.left {
                    Some(MouseEventKind::Up(MouseButton::Left))
                } else if right && !was.right {
                    Some(MouseEventKind::Down(MouseButton::Right))
                } else if !right && was.right {
                    Some(MouseEventKind::Up(MouseButton::Right))
                } else if middle && !was.middle {
                    Some(MouseEventKind::Down(MouseButton::Middle))
                } else if !middle && was.middle {
                    Some(MouseEventKind::Up(MouseButton::Middle))
                } else {
                    None
                }
            }
            MOUSE_MOVED => Some(if right {
                MouseEventKind::Drag(MouseButton::Right)
            } else if middle {
                MouseEventKind::Drag(MouseButton::Middle)
            } else if left {
                MouseEventKind::Drag(MouseButton::Left)
            } else {
                MouseEventKind::Moved
            }),
            MOUSE_WHEELED if delta > 0 => Some(MouseEventKind::ScrollUp),
            MOUSE_WHEELED if delta < 0 => Some(MouseEventKind::ScrollDown),
            MOUSE_HWHEELED if delta > 0 => Some(MouseEventKind::ScrollRight),
            MOUSE_HWHEELED if delta < 0 => Some(MouseEventKind::ScrollLeft),
            _ => None,
        };
        self.buttons = MouseButtonsPressed {
            left,
            right,
            middle,
        };
        if let Some(kind) = kind {
            self.push(InputAction::Mouse(MouseEvent {
                kind,
                column: record.column,
                row: record.row,
                modifiers: keys::modifiers_from_state(record.state),
            }));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::input::{PasteAbort, PasteSource};
    use crossterm::event::{
        KeyCode, KeyEvent, KeyModifiers, MouseButton, MouseEvent, MouseEventKind,
    };

    fn raw(s: &str) -> Vec<Record> {
        s.encode_utf16()
            .map(|unit| {
                Record::Key(KeyRecord {
                    vk: 0,
                    unit,
                    down: true,
                    repeat: 1,
                    ..KeyRecord::default()
                })
            })
            .collect()
    }

    fn wrap_unit(vk: u16, sc: u16, unit: u16, down: bool, state: u32) -> String {
        let kd = u8::from(down);
        format!("\x1b[{vk};{sc};{unit};{kd};{state};1_")
    }

    fn w32_string(s: &str) -> String {
        s.encode_utf16()
            .map(|u| wrap_unit(0, 0, u, true, 0))
            .collect()
    }

    fn w32_chars(s: &str) -> Vec<Record> {
        raw(&w32_string(s))
    }

    fn w32_text(text: &str) -> String {
        let mut out = String::new();
        for ch in text.chars() {
            match ch {
                '\n' => {
                    out += &wrap_unit(17, 29, 0, true, 8);
                    out += &wrap_unit(13, 28, 10, true, 8);
                    out += &wrap_unit(13, 28, 10, false, 8);
                    out += &wrap_unit(17, 29, 0, false, 0);
                }
                '\r' => {
                    out += &wrap_unit(13, 28, 13, true, 0);
                    out += &wrap_unit(13, 28, 13, false, 0);
                }
                c if (c as u32) < 0x10000 => {
                    let vk = if c.is_ascii_alphabetic() {
                        c.to_ascii_uppercase() as u16
                    } else if c == ' ' {
                        0x20
                    } else {
                        0
                    };
                    let unit = c as u16;
                    out += &wrap_unit(vk, 0, unit, true, 0);
                    out += &wrap_unit(vk, 0, unit, false, 0);
                }
                c => {
                    let mut buf = [0u16; 2];
                    for unit in c.encode_utf16(&mut buf).iter() {
                        out += &alt_code(*unit);
                    }
                }
            }
        }
        out
    }

    fn alt_code(unit: u16) -> String {
        let mut out = String::new();
        out += &wrap_unit(18, 56, 0, true, 2);
        out += &wrap_unit(102, 77, 0, true, 2);
        out += &wrap_unit(102, 77, 0, false, 2);
        out += &wrap_unit(18, 56, unit, false, 0);
        out
    }

    fn w32_paste(text: &str) -> Vec<Record> {
        let s = w32_string("\x1b[200~") + &w32_text(text) + &w32_string("\x1b[201~");
        raw(&s)
    }

    fn drain(decoder: &mut Decoder) -> Vec<InputAction> {
        std::iter::from_fn(|| decoder.next_action()).collect()
    }

    fn decode(records: Vec<Record>) -> Vec<InputAction> {
        let mut decoder = Decoder::new();
        let t0 = Instant::now();
        for record in records {
            decoder.feed(record, t0);
        }
        drain(&mut decoder)
    }

    fn key(code: KeyCode, modifiers: KeyModifiers) -> InputAction {
        InputAction::Key(KeyEvent::new(code, modifiers))
    }

    fn ch(c: char) -> InputAction {
        key(KeyCode::Char(c), KeyModifiers::NONE)
    }

    fn pasted(text: &str, txn: u64) -> Vec<InputAction> {
        vec![
            InputAction::PasteStarted { txn },
            InputAction::PasteText {
                text: text.into(),
                source: PasteSource::Terminal,
                txn,
            },
        ]
    }

    fn raw_paste(text: &str) -> Vec<Record> {
        raw(&format!("\x1b[200~{text}\x1b[201~"))
    }

    fn mouse(kind: MouseEventKind, column: u16, row: u16) -> InputAction {
        InputAction::Mouse(MouseEvent {
            kind,
            column,
            row,
            modifiers: KeyModifiers::NONE,
        })
    }

    #[test]
    fn a_raw_bracketed_paste_is_one_text_action_with_normalized_newlines() {
        let actions = decode(raw("\x1b[200~first\r\nsecond\r\n\r\nfourth\r\n\x1b[201~"));
        assert_eq!(actions, pasted("first\nsecond\n\nfourth\n", 1));
    }

    #[test]
    fn line_endings_are_normalized_and_other_text_preserved() {
        assert_eq!(decode(raw_paste("a\rb")), pasted("a\nb", 1));
        assert_eq!(decode(raw_paste("a\nb")), pasted("a\nb", 1));
        assert_eq!(decode(raw_paste("a\r\n\r\nb\n\r")), pasted("a\n\nb\n\n", 1));
        let text = "\tindent\n    code \u{e9}\u{1F600}";
        assert_eq!(decode(raw_paste(text)), pasted(text, 1));
    }

    #[test]
    fn control_text_inside_a_paste_stays_literal() {
        let text = "/clear\x03\x1b[A\x1b[200~x";
        assert_eq!(decode(raw_paste(text)), pasted(text, 1));
    }

    #[test]
    fn a_stray_end_marker_is_ignored() {
        assert_eq!(decode(raw("\x1b[201~")), vec![]);
    }

    #[test]
    fn an_oversized_paste_is_aborted_and_input_recovers() {
        let mut decoder = Decoder::new();
        let t0 = Instant::now();
        let big = "a".repeat(MAX_PASTE_BYTES + 1);
        for record in raw_paste(&big) {
            decoder.feed(record, t0);
        }
        let actions = drain(&mut decoder);
        assert_eq!(actions.len(), 2);
        assert_eq!(actions[0], InputAction::PasteStarted { txn: 1 });
        match &actions[1] {
            InputAction::PasteAborted { txn, reason, bytes } => {
                assert_eq!(*txn, 1);
                assert_eq!(*reason, PasteAbort::TooLarge);
                assert!(*bytes > MAX_PASTE_BYTES);
            }
            other => panic!("expected an abort, got {other:?}"),
        }
        for record in raw("x") {
            decoder.feed(record, t0);
        }
        assert_eq!(drain(&mut decoder), vec![ch('x')]);
    }

    #[test]
    fn a_stalled_paste_is_aborted_as_incomplete() {
        let mut decoder = Decoder::new();
        let t0 = Instant::now();
        for record in raw("\x1b[200~abc") {
            decoder.feed(record, t0);
        }
        assert!(decoder.in_paste());
        decoder.tick(t0 + ESC_TIMEOUT);
        assert_eq!(
            drain(&mut decoder),
            vec![InputAction::PasteStarted { txn: 1 }]
        );
        decoder.tick(t0 + PASTE_STALL_TIMEOUT);
        assert_eq!(
            drain(&mut decoder),
            vec![InputAction::PasteAborted {
                txn: 1,
                reason: PasteAbort::Incomplete,
                bytes: 3
            }]
        );
        assert!(!decoder.in_paste());
        let t1 = t0 + PASTE_STALL_TIMEOUT;
        for record in raw("x") {
            decoder.feed(record, t1);
        }
        assert_eq!(drain(&mut decoder), vec![ch('x')]);
    }

    #[test]
    fn cancelling_an_open_paste_aborts_it() {
        let mut decoder = Decoder::new();
        let t0 = Instant::now();
        for record in raw("\x1b[200~ab") {
            decoder.feed(record, t0);
        }
        decoder.cancel();
        assert_eq!(
            drain(&mut decoder),
            vec![
                InputAction::PasteStarted { txn: 1 },
                InputAction::PasteAborted {
                    txn: 1,
                    reason: PasteAbort::Cancelled,
                    bytes: 2
                }
            ]
        );
        assert!(!decoder.has_pending());
    }

    #[test]
    fn win32_input_mode_keys_decode() {
        assert_eq!(
            decode(raw(&wrap_unit(13, 28, 13, true, 16))),
            vec![key(KeyCode::Enter, KeyModifiers::SHIFT)]
        );
        assert_eq!(
            decode(raw("\x1b[67;46;3;1;8;1_")),
            vec![key(KeyCode::Char('c'), KeyModifiers::CONTROL)]
        );
        assert_eq!(decode(raw(&wrap_unit(67, 46, 99, false, 0))), vec![]);
        assert_eq!(
            decode(raw("\x1b[27;1;27;1;0;1_")),
            vec![key(KeyCode::Esc, KeyModifiers::NONE)]
        );
        let emoji = alt_code(0xD83D) + &alt_code(0xDE00);
        assert_eq!(decode(raw(&emoji)), vec![ch('\u{1F600}')]);
    }

    #[test]
    fn a_win32_input_mode_paste_is_one_text_action() {
        let actions = decode(w32_paste("first\r\nsecond\n\nfourth\n\u{1F600}"));
        assert_eq!(actions, pasted("first\nsecond\n\nfourth\n\u{1F600}", 1));
    }

    #[test]
    fn mouse_and_focus_sequences_decode() {
        assert_eq!(
            decode(raw("\x1b[<0;5;5M")),
            vec![mouse(MouseEventKind::Down(MouseButton::Left), 4, 4)]
        );
        assert_eq!(
            decode(raw("\x1b[<0;5;5m")),
            vec![mouse(MouseEventKind::Up(MouseButton::Left), 4, 4)]
        );
        assert_eq!(
            decode(raw("\x1b[<64;1;1M")),
            vec![mouse(MouseEventKind::ScrollUp, 0, 0)]
        );
        assert_eq!(
            decode(w32_chars("\x1b[<0;5;5M")),
            vec![mouse(MouseEventKind::Down(MouseButton::Left), 4, 4)]
        );
        assert_eq!(decode(raw("\x1b[I")), vec![InputAction::FocusGained]);
        assert_eq!(decode(raw("\x1b[O")), vec![InputAction::FocusLost]);
    }

    #[test]
    fn a_lone_escape_resolves_on_timeout() {
        let mut decoder = Decoder::new();
        let t0 = Instant::now();
        for record in raw("\x1b") {
            decoder.feed(record, t0);
        }
        decoder.tick(t0 + ESC_TIMEOUT / 2);
        assert_eq!(drain(&mut decoder), vec![]);
        assert!(decoder.has_pending());
        decoder.tick(t0 + ESC_TIMEOUT);
        assert_eq!(
            drain(&mut decoder),
            vec![key(KeyCode::Esc, KeyModifiers::NONE)]
        );
        assert!(!decoder.has_pending());

        assert_eq!(
            decode(raw("\x1bx")),
            vec![key(KeyCode::Char('x'), KeyModifiers::ALT)]
        );
        assert_eq!(
            decode(raw("\x1b[1;5A")),
            vec![key(KeyCode::Up, KeyModifiers::CONTROL)]
        );
    }

    #[test]
    fn a_partial_csi_resolves_on_timeout_as_typed_keys() {
        let mut decoder = Decoder::new();
        let t0 = Instant::now();
        for record in raw("\x1b[1") {
            decoder.feed(record, t0);
        }
        decoder.tick(t0 + ESC_TIMEOUT);
        assert_eq!(
            drain(&mut decoder),
            vec![key(KeyCode::Char('['), KeyModifiers::ALT), ch('1')]
        );
    }

    #[test]
    fn typed_characters_are_presses() {
        assert_eq!(
            decode(raw("ab\r")),
            vec![ch('a'), ch('b'), key(KeyCode::Enter, KeyModifiers::NONE)]
        );
    }

    #[test]
    fn native_records_decode() {
        let mouse_record = |buttons: u32, flags: u32| {
            Record::Mouse(MouseRecord {
                column: 3,
                row: 2,
                buttons,
                flags,
                state: 0,
            })
        };
        let actions = decode(vec![
            mouse_record(0x1, 0),
            mouse_record(0x1, 1),
            mouse_record(0, 0),
            mouse_record(0, 1),
            mouse_record(120 << 16, 4),
            mouse_record((-120i32 as u32) & 0xFFFF_0000, 4),
            Record::Resize { cols: 80, rows: 24 },
            Record::Focus(true),
            Record::Focus(false),
        ]);
        assert_eq!(
            actions,
            vec![
                mouse(MouseEventKind::Down(MouseButton::Left), 3, 2),
                mouse(MouseEventKind::Drag(MouseButton::Left), 3, 2),
                mouse(MouseEventKind::Up(MouseButton::Left), 3, 2),
                mouse(MouseEventKind::Moved, 3, 2),
                mouse(MouseEventKind::ScrollUp, 3, 2),
                mouse(MouseEventKind::ScrollDown, 3, 2),
                InputAction::Resize(80, 24),
                InputAction::FocusGained,
                InputAction::FocusLost,
            ]
        );
    }

    #[test]
    fn a_key_record_flushes_a_pending_escape_first() {
        let mut records = raw("\x1b");
        records.push(Record::Key(KeyRecord {
            vk: 0x41,
            unit: u16::from(b'a'),
            down: true,
            repeat: 1,
            ..KeyRecord::default()
        }));
        assert_eq!(
            decode(records),
            vec![key(KeyCode::Esc, KeyModifiers::NONE), ch('a')]
        );
    }

    fn next(seed: &mut u64) -> u64 {
        *seed = seed
            .wrapping_mul(6364136223846793005)
            .wrapping_add(1442695040888963407);
        *seed >> 33
    }

    fn decode_in_batches(records: &[Record], cuts: &[usize]) -> Vec<InputAction> {
        let mut decoder = Decoder::new();
        let t0 = Instant::now();
        let mut start = 0;
        let mut out = Vec::new();
        for (k, &cut) in cuts
            .iter()
            .chain(std::iter::once(&records.len()))
            .enumerate()
        {
            let now = t0 + Duration::from_millis(k as u64);
            for record in &records[start..cut] {
                decoder.feed(*record, now);
            }
            start = cut;
            decoder.tick(now);
            out.extend(drain(&mut decoder));
        }
        out
    }

    #[test]
    fn batching_never_changes_the_result() {
        let streams = [
            raw("\x1b[200~first\r\nsecond\r\n\r\nfourth\r\n\x1b[201~"),
            w32_paste("first\r\nsecond\n\nfourth\n\u{1F600}"),
            raw("ab\r\x1b[200~x\r\ny\x1b[201~\x1b[1;5A"),
            raw_paste("\u{1F600}x\u{1F600}"),
        ];
        for stream in &streams {
            let whole = decode(stream.clone());
            assert!(!whole.is_empty());
            for seed in 0..200u64 {
                let mut state = seed;
                let count = (next(&mut state) % 20) as usize;
                let mut cuts: Vec<usize> = (0..count)
                    .map(|_| (next(&mut state) as usize) % (stream.len() + 1))
                    .collect();
                cuts.sort_unstable();
                assert_eq!(
                    decode_in_batches(stream, &cuts),
                    whole,
                    "seed {seed} cuts {cuts:?}"
                );
            }
        }
        // A surrogate pair split exactly between batches.
        let split = raw_paste("\u{1F600}");
        let high = split
            .iter()
            .position(|r| matches!(r, Record::Key(k) if k.unit == 0xD83D))
            .unwrap_or(0);
        assert_eq!(
            decode_in_batches(&split, &[high + 1]),
            pasted("\u{1F600}", 1)
        );
        let typed = raw("\u{1F600}");
        assert_eq!(decode_in_batches(&typed, &[1]), vec![ch('\u{1F600}')]);
    }
}
