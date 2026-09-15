//! Shared vocabulary for the goal budget: the "no limit" tokens and the wire
//! sentinel that carries them.
//!
//! This lives in the protocol crate because three layers must agree on it and
//! they cannot all see each other: the CLI parsers in `coda` and `coda-boot`
//! turn an operator's `none` into a wire value, and the engine in `coda-serve`
//! turns that wire value back into a budget. `coda-boot` deliberately does not
//! depend on `coda-serve` (which already dev-depends on `coda-boot`), so the
//! definition has to sit below both.

/// Tokens an operator may use, on either budget dimension, to mean "no ceiling
/// at all", so `--goal-max-duration none` and `--goal-max-continuations none`
/// read the same.
pub const UNLIMITED_TOKENS: [&str; 3] = ["none", "unlimited", "off"];

/// Wire encoding of "no continuation limit" for `maxContinuations`.
///
/// `maxContinuations` is an integer on the wire, and `null`/absent already
/// means "use the default", so the third state needs its own value. `-1` was
/// previously rejected outright, which makes adopting it here a pure extension:
/// no request that used to be valid changes meaning.
pub const UNLIMITED_CONTINUATIONS: i32 = -1;

/// True when `raw` is one of [`UNLIMITED_TOKENS`], ignoring case and surrounding
/// whitespace.
pub fn is_unlimited_token(raw: &str) -> bool {
    let trimmed = raw.trim();
    UNLIMITED_TOKENS.iter().any(|token| trimmed.eq_ignore_ascii_case(token))
}

/// `clap` value parser for `--goal-max-continuations`.
///
/// Accepts a non-negative integer, or any of [`UNLIMITED_TOKENS`] which map to
/// [`UNLIMITED_CONTINUATIONS`]. Rejecting other negatives here means the CLI
/// fails fast with a readable message instead of deferring to the engine's
/// `-32602`.
pub fn parse_max_continuations(raw: &str) -> Result<i32, String> {
    if is_unlimited_token(raw) {
        return Ok(UNLIMITED_CONTINUATIONS);
    }
    match raw.trim().parse::<i32>() {
        Ok(n) if n >= 0 => Ok(n),
        Ok(n) => Err(format!(
            "{n} is negative; use a count of 0 or more, or `none` for no limit"
        )),
        Err(_) => Err(format!(
            "expected a whole number of turns or `none`, got `{raw}`"
        )),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_unlimited_token_is_recognised_in_any_casing() {
        for token in UNLIMITED_TOKENS {
            assert!(is_unlimited_token(token), "{token}");
            assert!(is_unlimited_token(&token.to_uppercase()), "{token} uppercased");
            assert!(is_unlimited_token(&format!("  {token}  ")), "{token} padded");
        }
    }

    #[test]
    fn ordinary_values_are_not_unlimited_tokens() {
        for value in ["30m", "0", "5", "", "nonexistent"] {
            assert!(!is_unlimited_token(value), "{value}");
        }
    }

    #[test]
    fn unlimited_tokens_parse_to_the_wire_sentinel() {
        for token in UNLIMITED_TOKENS {
            assert_eq!(parse_max_continuations(token), Ok(UNLIMITED_CONTINUATIONS));
        }
    }

    #[test]
    fn non_negative_counts_parse_unchanged() {
        assert_eq!(parse_max_continuations("0"), Ok(0));
        assert_eq!(parse_max_continuations("60000"), Ok(60_000));
        assert_eq!(parse_max_continuations(" 42 "), Ok(42));
    }

    /// The sentinel must not be reachable by typing it directly: `-1` should
    /// read as a mistake, and `none` is the only spelling of "no limit".
    #[test]
    fn negative_counts_are_rejected_with_a_pointer_to_none() {
        let err = parse_max_continuations("-1").expect_err("-1 must not parse");
        assert!(err.contains("none"), "{err}");
        assert!(parse_max_continuations("-7").is_err());
    }

    #[test]
    fn non_numeric_values_are_rejected() {
        let err = parse_max_continuations("lots").expect_err("must not parse");
        assert!(err.contains("none"), "{err}");
    }
}
