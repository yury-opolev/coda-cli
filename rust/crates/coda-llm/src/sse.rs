//! Server-sent events framing.
//!
//! Every streaming provider uses SSE, so the framing is shared. The decoder is
//! incremental: chunks arrive at arbitrary boundaries and a single event is
//! routinely split across several, so it buffers until an event terminates.
//!
//! Only the fields the providers actually use are surfaced (`event` and
//! `data`); comments, `id` and `retry` are consumed and discarded.

/// One decoded SSE event.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SseEvent {
    /// The `event:` field, empty when the stream omits it.
    pub name: String,
    /// The `data:` field. Multiple `data:` lines are joined with newlines, as
    /// the specification requires.
    pub data: String,
}

impl SseEvent {
    pub fn is_empty(&self) -> bool {
        self.name.is_empty() && self.data.is_empty()
    }
}

/// Incremental SSE decoder.
#[derive(Debug, Default)]
pub struct SseDecoder {
    buffer: String,
    name: String,
    data: Vec<String>,
}

impl SseDecoder {
    pub fn new() -> Self {
        Self::default()
    }

    /// Feeds a chunk and returns every event it completed.
    pub fn push(&mut self, chunk: &str) -> Vec<SseEvent> {
        self.buffer.push_str(chunk);
        let mut events = Vec::new();

        // Only consume up to the last line terminator; a partial trailing line
        // must stay buffered until the rest of it arrives.
        while let Some(index) = self.buffer.find('\n') {
            let line: String = self.buffer.drain(..=index).collect();
            let line = line.trim_end_matches('\n').trim_end_matches('\r');

            if line.is_empty() {
                if let Some(event) = self.take() {
                    events.push(event);
                }
                continue;
            }
            self.field(line);
        }

        events
    }

    /// Flushes any event left buffered when the stream ends without a final
    /// blank line, which some proxies do.
    pub fn finish(&mut self) -> Option<SseEvent> {
        let remaining = std::mem::take(&mut self.buffer);
        for line in remaining.lines() {
            if !line.is_empty() {
                self.field(line);
            }
        }
        self.take()
    }

    fn field(&mut self, line: &str) {
        // A leading colon marks a comment, used as a keep-alive.
        if line.starts_with(':') {
            return;
        }

        let (name, value) = match line.split_once(':') {
            Some((name, value)) => (name, value.strip_prefix(' ').unwrap_or(value)),
            // A bare field name has an empty value.
            None => (line, ""),
        };

        match name {
            "event" => self.name = value.to_string(),
            "data" => self.data.push(value.to_string()),
            // `id` and `retry` are irrelevant: these streams are not resumable.
            _ => {}
        }
    }

    fn take(&mut self) -> Option<SseEvent> {
        let name = std::mem::take(&mut self.name);
        let data = std::mem::take(&mut self.data);

        if name.is_empty() && data.is_empty() {
            return None;
        }
        Some(SseEvent {
            name,
            data: data.join("\n"),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn decode(chunks: &[&str]) -> Vec<SseEvent> {
        let mut decoder = SseDecoder::new();
        let mut events = Vec::new();
        for chunk in chunks {
            events.extend(decoder.push(chunk));
        }
        events
    }

    #[test]
    fn decodes_a_named_event_with_data() {
        let events = decode(&["event: message_start\ndata: {\"a\":1}\n\n"]);
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].name, "message_start");
        assert_eq!(events[0].data, "{\"a\":1}");
    }

    #[test]
    fn decodes_several_events_from_one_chunk() {
        let events = decode(&["event: a\ndata: 1\n\nevent: b\ndata: 2\n\n"]);
        assert_eq!(events.len(), 2);
        assert_eq!(events[1].name, "b");
    }

    #[test]
    fn decodes_an_event_split_across_arbitrary_boundaries() {
        let stream = "event: content_block_delta\ndata: {\"text\":\"hi\"}\n\n";
        for split in 1..stream.len() {
            let (head, tail) = stream.split_at(split);
            let events = decode(&[head, tail]);
            assert_eq!(events.len(), 1, "lost the event splitting at {split}");
            assert_eq!(events[0].name, "content_block_delta");
            assert_eq!(events[0].data, "{\"text\":\"hi\"}");
        }
    }

    #[test]
    fn joins_multiple_data_lines_with_newlines() {
        let events = decode(&["data: one\ndata: two\n\n"]);
        assert_eq!(events[0].data, "one\ntwo");
    }

    #[test]
    fn accepts_crlf_line_endings() {
        let events = decode(&["event: a\r\ndata: 1\r\n\r\n"]);
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].data, "1");
    }

    #[test]
    fn ignores_comment_keepalives() {
        let events = decode(&[": keep-alive\n\nevent: a\ndata: 1\n\n"]);
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].name, "a");
    }

    #[test]
    fn ignores_id_and_retry_fields() {
        let events = decode(&["id: 7\nretry: 100\nevent: a\ndata: 1\n\n"]);
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].name, "a");
        assert_eq!(events[0].data, "1");
    }

    #[test]
    fn accepts_data_without_a_leading_space() {
        let events = decode(&["data:{\"a\":1}\n\n"]);
        assert_eq!(events[0].data, "{\"a\":1}");
    }

    #[test]
    fn preserves_spaces_beyond_the_first() {
        // Only one leading space is part of the framing.
        let events = decode(&["data:   padded\n\n"]);
        assert_eq!(events[0].data, "  padded");
    }

    #[test]
    fn an_event_with_no_name_still_decodes() {
        let events = decode(&["data: bare\n\n"]);
        assert_eq!(events.len(), 1);
        assert!(events[0].name.is_empty());
        assert_eq!(events[0].data, "bare");
    }

    #[test]
    fn a_partial_event_is_not_emitted_early() {
        let mut decoder = SseDecoder::new();
        assert!(decoder.push("event: a\ndata: incomp").is_empty());
        assert!(decoder.push("lete\n").is_empty(), "no blank line yet");
        assert_eq!(decoder.push("\n").len(), 1);
    }

    #[test]
    fn flushes_a_trailing_event_with_no_final_blank_line() {
        let mut decoder = SseDecoder::new();
        assert!(decoder.push("event: a\ndata: 1\n").is_empty());

        let event = decoder.finish().expect("a trailing event");
        assert_eq!(event.name, "a");
        assert_eq!(event.data, "1");
    }

    #[test]
    fn finish_yields_nothing_when_the_stream_ended_cleanly() {
        let mut decoder = SseDecoder::new();
        decoder.push("event: a\ndata: 1\n\n");
        assert!(decoder.finish().is_none());
    }

    #[test]
    fn consecutive_blank_lines_do_not_emit_empty_events() {
        let events = decode(&["\n\n\nevent: a\ndata: 1\n\n\n\n"]);
        assert_eq!(events.len(), 1);
    }

    #[test]
    fn state_does_not_leak_between_events() {
        let events = decode(&["event: a\ndata: 1\n\ndata: 2\n\n"]);
        assert_eq!(events[0].name, "a");
        assert!(
            events[1].name.is_empty(),
            "the previous event's name leaked into the next"
        );
    }

    #[test]
    fn handles_a_byte_at_a_time() {
        let stream = "event: content_block_delta\ndata: {\"t\":\"x\"}\n\nevent: message_stop\ndata: {}\n\n";
        let mut decoder = SseDecoder::new();
        let mut events = Vec::new();
        for c in stream.chars() {
            events.extend(decoder.push(&c.to_string()));
        }
        assert_eq!(events.len(), 2);
        assert_eq!(events[0].name, "content_block_delta");
        assert_eq!(events[1].name, "message_stop");
    }

    #[test]
    fn handles_multibyte_characters_split_between_chunks() {
        // Chunks are decoded as UTF-8 upstream, so a grapheme never splits
        // here, but a multi-character token can.
        let events = decode(&["data: \u{1F680} roc", "ket\n\n"]);
        assert_eq!(events[0].data, "\u{1F680} rocket");
    }
}
