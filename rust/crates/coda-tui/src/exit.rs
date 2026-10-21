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
    pub summary: Option<ExitSummary>,
    pub outcome: anyhow::Result<()>,
}

/// Restores the terminal, then prints the summary and returns the outcome.
pub fn finish_interactive(guard: TerminalGuard, exit: InteractiveExit) -> anyhow::Result<()> {
    let restoration = guard.restore_for_exit();
    conclude(exit, restoration, crate::branding::print_exit)
}

/// Prints the summary only once the terminal is known to be restored.
pub fn conclude(
    exit: InteractiveExit,
    restoration: anyhow::Result<()>,
    print: impl FnOnce(&ExitSummary),
) -> anyhow::Result<()> {
    if let Err(restoration) = restoration {
        // The terminal may still be in the alternate screen, where the card
        // would be wiped or garble the shell; the summary rides in the error.
        let recorded = exit
            .summary
            .as_ref()
            .map(|summary| format!(" [{}]", crate::branding::exit_summary_line(summary)))
            .unwrap_or_default();
        return Err(match exit.outcome {
            Ok(()) => restoration.context(format!("terminal restoration failed{recorded}")),
            Err(original) => {
                let message =
                    format!("{original:#}; terminal restoration failed{recorded}: {restoration:#}");
                original.context(message)
            }
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
