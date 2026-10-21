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

use crate::exit::InteractiveExit;
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

pub(super) async fn feedback(
    cleanup: impl Future<Output = InteractiveExit>,
    guard: &mut TerminalGuard,
    theme: &Theme,
) -> InteractiveExit {
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

async fn drive(
    cleanup: impl Future<Output = InteractiveExit>,
    mut events: impl Stream<Item = io::Result<Event>> + Unpin,
    mut draw: impl FnMut(usize, bool) -> io::Result<()>,
) -> InteractiveExit {
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
            mut exit = &mut cleanup => {
                coda_diagnostics::record(coda_diagnostics::Event::ShutdownPhase {
                    phase: "cleanup",
                    duration_ms: started.elapsed().as_millis() as u64,
                    outcome: if exit.outcome.is_ok() { "completed" } else { "failed" },
                });
                exit.outcome = combine(exit.outcome, feedback_error.map_or(Ok(()), Err));
                return exit;
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
    use crate::branding::ExitSummary;
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
        let cleanup = async {
            InteractiveExit {
                summary: Some(ExitSummary::default()),
                outcome: rx.await.context("cleanup was abandoned"),
            }
        };
        let exit = tokio::time::timeout(
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
        exit.outcome.expect("cleanup");
        assert!(exit.summary.is_some());
        assert_eq!(frames[0], (0, false), "feedback precedes cleanup polling");
        assert_eq!(frames.last(), Some(&(3, true)));
    }

    #[tokio::test]
    async fn a_render_failure_does_not_abandon_cleanup_or_hide_its_original_error() {
        let completed = std::cell::Cell::new(false);
        let exit = drive(
            async {
                tokio::time::sleep(Duration::from_millis(20)).await;
                completed.set(true);
                InteractiveExit {
                    summary: Some(ExitSummary {
                        session_id: Some("s1".into()),
                        ..Default::default()
                    }),
                    outcome: Err(anyhow::anyhow!("original application failure")),
                }
            },
            futures::stream::pending(),
            |_, _| Err(io::Error::other("draw failed")),
        )
        .await;
        assert!(completed.get());
        let message = exit.outcome.unwrap_err().to_string();
        assert!(message.contains("original application failure"));
        assert!(message.contains("draw failed"));
        assert_eq!(exit.summary.and_then(|s| s.session_id).as_deref(), Some("s1"));
    }

    #[tokio::test]
    async fn an_input_failure_does_not_abandon_cleanup_and_keeps_the_summary() {
        let completed = std::cell::Cell::new(false);
        let exit = tokio::time::timeout(
            Duration::from_secs(2),
            drive(
                async {
                    tokio::time::sleep(Duration::from_millis(20)).await;
                    completed.set(true);
                    InteractiveExit {
                        summary: Some(ExitSummary {
                            session_id: Some("s1".into()),
                            ..Default::default()
                        }),
                        outcome: Ok(()),
                    }
                },
                futures::stream::iter([Err(io::Error::other("console read failed"))]),
                |_, _| Ok(()),
            ),
        )
        .await
        .expect("shutdown must finish");
        assert!(completed.get());
        assert_eq!(exit.summary.and_then(|s| s.session_id).as_deref(), Some("s1"));
        let message = format!("{:#}", exit.outcome.unwrap_err());
        assert!(message.contains("shutdown input failed"), "{message}");
        assert!(message.contains("console read failed"), "{message}");
    }

    #[tokio::test]
    async fn a_render_failure_after_clean_cleanup_is_reported_but_keeps_the_summary() {
        let exit = drive(
            async {
                tokio::time::sleep(Duration::from_millis(20)).await;
                InteractiveExit { summary: Some(ExitSummary::default()), outcome: Ok(()) }
            },
            futures::stream::pending(),
            |_, _| Err(io::Error::other("draw failed")),
        )
        .await;
        assert!(exit.summary.is_some());
        assert!(format!("{:#}", exit.outcome.unwrap_err()).contains("draw failed"));
    }
}
