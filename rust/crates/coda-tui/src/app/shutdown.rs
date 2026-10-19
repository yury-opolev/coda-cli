//! Shutdown keeps the renderer alive until cleanup relinquishes its resources.

use std::future::Future;
use std::io;
use std::time::Duration;

use anyhow::{Context, Result};
use crossterm::event::{Event, KeyCode, KeyEventKind, KeyModifiers};
use futures::{Stream, StreamExt};
use ratatui::layout::{Alignment, Rect};
use ratatui::text::Line;
use ratatui::widgets::Paragraph;

use crate::render::glyphs;
use crate::terminal::TerminalGuard;
use coda_render::theme::{Role, Theme};

pub(super) fn combine<T>(outcome: Result<T>, cleanup: Result<()>) -> Result<T> {
    match (outcome, cleanup) {
        (outcome, Ok(())) => outcome,
        (Ok(_), Err(error)) => Err(error),
        (Err(original), Err(cleanup)) => {
            let message = format!("{original}; cleanup failed: {cleanup:#}");
            Err(original.context(message))
        }
    }
}

pub(super) async fn feedback<T>(
    cleanup: impl Future<Output = Result<T>>,
    guard: &mut TerminalGuard,
    theme: &Theme,
) -> Result<T> {
    let events = futures::stream::unfold((), |_| async {
        Some((crate::terminal::read_event().await, ()))
    })
    .boxed();
    drive(cleanup, events, |frame, acknowledged| {
        guard
            .terminal()
            .draw(|f| {
                let area = f.area();
                let row = Rect::new(area.x, area.y + area.height / 2, area.width, 2);
                let status = format!(
                    "{} Shutting down...",
                    glyphs::SPINNER[frame % glyphs::SPINNER.len()]
                );
                let hint = if acknowledged {
                    "Shutdown is already in progress; waiting for safe cleanup."
                } else {
                    "Finishing session and stopping engine."
                };
                f.render_widget(
                    Paragraph::new(vec![Line::from(status), Line::from(hint)])
                        .style(theme.style(Role::Notification))
                        .alignment(Alignment::Center),
                    row,
                );
            })
            .map(|_| ())
    })
    .await
}

async fn drive<T>(
    cleanup: impl Future<Output = Result<T>>,
    mut events: impl Stream<Item = io::Result<Event>> + Unpin,
    mut draw: impl FnMut(usize, bool) -> io::Result<()>,
) -> Result<T> {
    let started = std::time::Instant::now();
    coda_diagnostics::record(coda_diagnostics::Event::ShutdownStart);
    let mut feedback_error = draw(0, false).context("shutdown rendering failed").err();
    let mut tick = tokio::time::interval(Duration::from_millis(80));
    tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    tick.tick().await;
    let mut frame = 0;
    let mut acknowledged = false;
    let mut input_open = true;
    tokio::pin!(cleanup);
    loop {
        tokio::select! {
            result = &mut cleanup => {
                coda_diagnostics::record(coda_diagnostics::Event::ShutdownPhase {
                    phase: "cleanup",
                    duration_ms: started.elapsed().as_millis() as u64,
                    outcome: if result.is_ok() { "completed" } else { "failed" },
                });
                return combine(result, feedback_error.map_or(Ok(()), Err));
            }
            event = events.next(), if input_open => match event {
                Some(Ok(Event::Key(key))) if key.kind != KeyEventKind::Release
                    && key.code == KeyCode::Char('c')
                    && key.modifiers.contains(KeyModifiers::CONTROL) => {
                    acknowledged = true;
                }
                Some(Ok(_)) => {}
                Some(Err(error)) => {
                    input_open = false;
                    feedback_error.get_or_insert_with(|| anyhow::Error::new(error)
                        .context("shutdown input failed"));
                }
                None => input_open = false,
            },
            _ = tick.tick() => {
                frame += 1;
                if let Err(error) = draw(frame, acknowledged) {
                    feedback_error.get_or_insert_with(|| anyhow::Error::new(error)
                        .context("shutdown rendering failed"));
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crossterm::event::KeyEvent;

    #[tokio::test]
    async fn cleanup_keeps_animating_and_only_acknowledges_further_input() {
        let (tx, rx) = tokio::sync::oneshot::channel();
        let mut tx = Some(tx);
        let mut frames = Vec::new();
        let input = futures::stream::iter([
            Ok(Event::Key(KeyEvent::new(
                KeyCode::Char('x'),
                KeyModifiers::NONE,
            ))),
            Ok(Event::Key(KeyEvent::new(
                KeyCode::Char('c'),
                KeyModifiers::CONTROL,
            ))),
        ]);
        let cleanup = async { rx.await.context("cleanup was abandoned") };
        let result = tokio::time::timeout(
            Duration::from_secs(2),
            drive(cleanup, input, |frame, ack| {
                frames.push((frame, ack));
                if frame == 3 {
                    tx.take().unwrap().send(()).unwrap();
                }
                Ok(())
            }),
        )
        .await
        .expect("shutdown must finish");
        result.expect("cleanup");
        assert_eq!(frames[0], (0, false), "feedback precedes cleanup polling");
        assert_eq!(frames.last(), Some(&(3, true)));
    }

    #[tokio::test]
    async fn a_render_failure_does_not_abandon_cleanup_or_hide_its_original_error() {
        let completed = std::cell::Cell::new(false);
        let result: Result<()> = drive(
            async {
                tokio::time::sleep(Duration::from_millis(20)).await;
                completed.set(true);
                Err(anyhow::anyhow!("original application failure"))
            },
            futures::stream::pending(),
            |_, _| Err(io::Error::other("draw failed")),
        )
        .await;
        assert!(completed.get());
        let message = result.unwrap_err().to_string();
        assert!(message.contains("original application failure"));
        assert!(message.contains("draw failed"));
    }
}
