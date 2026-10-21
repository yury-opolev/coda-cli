//! How an interactive session ends: its outcome and the summary to print.

use crate::branding::ExitSummary;
use crate::terminal::TerminalGuard;

/// What an interactive run produced once cleanup was attempted.
///
/// The summary and the outcome are independent: a session whose shutdown
/// failed still has usage totals and a resume reference worth reporting, and
/// reporting them must never hide the failure.
#[derive(Debug)]
pub struct InteractiveExit {
    /// The session to report, or `None` when there is nothing to report.
    /// `App::run` always produces one today; `None` exists for
    /// callers with no session, and simply prints nothing.
    pub summary: Option<ExitSummary>,
    pub outcome: anyhow::Result<()>,
}

/// Ends an interactive session: restores the terminal, then reports.
///
/// Consumes the guard so restoration happens exactly once, before anything is
/// written to the normal screen. The decision of what to print and return is
/// left to [`conclude`].
pub fn finish_interactive(guard: TerminalGuard, exit: InteractiveExit) -> anyhow::Result<()> {
    let restoration = guard.restore_for_exit();
    conclude(exit, restoration, crate::branding::print_exit)
}

/// Decides what to print and return once terminal restoration was attempted.
///
/// When restoration succeeded, the summary (if any) is handed to `print` and
/// the run's outcome is returned unchanged. When it failed, nothing is
/// printed; the restoration failure is returned instead, carrying the run's
/// own failure (if any) and the summary on one plain line.
///
/// `print` is a callback rather than a direct call to
/// [`crate::branding::print_exit`] so tests can observe whether — and how
/// often — the card is printed without writing to stdout.
pub fn conclude(
    exit: InteractiveExit,
    restoration: anyhow::Result<()>,
    print: impl FnOnce(&ExitSummary),
) -> anyhow::Result<()> {
    if let Err(restoration) = restoration {
        // The terminal may still be in the alternate screen or raw mode:
        // printing the card there could wipe or garble the shell's layout.
        // Carried in the error instead, it reaches stderr as one plain line
        // that can be redirected and logged.
        let recorded = exit
            .summary
            .as_ref()
            .map(|summary| format!(" [{}]", crate::branding::exit_summary_line(summary)))
            .unwrap_or_default();
        return Err(match exit.outcome {
            Ok(()) => restoration.context(format!("terminal restoration failed{recorded}")),
            Err(original) => original
                .context(format!("terminal restoration failed{recorded}: {restoration:#}")),
        });
    }
    if let Some(summary) = &exit.summary {
        print(summary);
    }
    exit.outcome
}

#[cfg(test)]
mod tests {
    use super::*;

    fn exit(outcome: anyhow::Result<()>) -> InteractiveExit {
        InteractiveExit {
            summary: Some(ExitSummary { session_id: Some("s1".into()), ..Default::default() }),
            outcome,
        }
    }

    #[test]
    fn a_clean_exit_prints_the_summary_once() {
        let mut printed = 0;
        conclude(exit(Ok(())), Ok(()), |_| printed += 1).expect("clean exit");
        assert_eq!(printed, 1);
    }

    #[test]
    fn a_failed_exit_still_prints_the_summary_and_reports_the_failure() {
        let mut printed = 0;
        let error = conclude(exit(Err(anyhow::anyhow!("cleanup failed"))), Ok(()), |_| {
            printed += 1
        })
        .unwrap_err();
        assert_eq!(printed, 1);
        assert!(format!("{error:#}").contains("cleanup failed"));
    }

    #[test]
    fn a_failed_restoration_never_prints_and_keeps_every_cause() {
        let error = conclude(
            exit(Err(anyhow::anyhow!("cleanup failed"))),
            Err(anyhow::anyhow!("console gone")),
            |_| panic!("the terminal may still be in the alternate screen"),
        )
        .unwrap_err();
        let message = format!("{error:#}");
        assert!(message.contains("cleanup failed"), "{message}");
        assert!(message.contains("terminal restoration failed"), "{message}");
        assert!(message.contains("console gone"), "{message}");
        assert_eq!(message.matches("cleanup failed").count(), 1, "no repeated cause: {message}");
        assert!(message.contains("s1"), "the known session must stay findable: {message}");

        let error = conclude(exit(Ok(())), Err(anyhow::anyhow!("console gone")), |_| {
            panic!("must not print")
        })
        .unwrap_err();
        assert!(format!("{error:#}").contains("terminal restoration failed"));
    }

    #[test]
    fn a_failed_restoration_keeps_the_whole_summary_in_the_error() {
        let summary = ExitSummary {
            message_count: 4,
            provider_id: "openai".into(),
            model: "gpt-x".into(),
            input_tokens: 1200,
            output_tokens: 345,
            session_id: Some("abc123".into()),
            working_directory: "C:\\work".into(),
            ..Default::default()
        };
        for outcome in [Ok(()), Err(anyhow::anyhow!("cleanup failed"))] {
            let exit = InteractiveExit { summary: Some(summary.clone()), outcome };
            let error = conclude(exit, Err(anyhow::anyhow!("console gone")), |_| {
                panic!("must not print")
            })
            .unwrap_err();
            let message = format!("{error:#}");
            assert!(message.contains("1200 in"), "{message}");
            assert!(message.contains("345 out"), "{message}");
            assert!(message.contains("1545 total"), "{message}");
            assert!(message.contains("abc123"), "{message}");
            assert!(message.contains("openai/gpt-x"), "{message}");
            assert!(message.contains("C:\\work"), "{message}");
            assert!(!message.contains('\n'), "the summary stays on one line: {message}");
        }

        let unsaved = InteractiveExit {
            summary: Some(ExitSummary { session_id: None, ..Default::default() }),
            outcome: Ok(()),
        };
        let error = conclude(unsaved, Err(anyhow::anyhow!("console gone")), |_| {
            panic!("must not print")
        })
        .unwrap_err();
        assert!(format!("{error:#}").contains("not saved"));
    }

    #[test]
    fn no_summary_prints_nothing() {
        let none = InteractiveExit { summary: None, outcome: Ok(()) };
        conclude(none, Ok(()), |_| panic!("nothing to print")).expect("clean exit");
    }
}
