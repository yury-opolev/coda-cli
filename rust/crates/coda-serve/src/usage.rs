//! Pricing the engine's usage accounting.
//!
//! Pure functions over [`UsagePair`], so the arithmetic can be asserted
//! without an engine. Each category is charged at its own rate: charging the
//! whole input at the uncached price overstates a mostly-cached session many
//! times over, because cache reads cost a fraction of it.
//!
//! An estimate is only ever *complete* when every measured token had a price.
//! Tokens without one are left out of the dollar figure and the estimate is
//! marked incomplete, so an unpriced model reads as "unknown", never as free.

use coda_proto::state::{CostEstimate, UsagePair};

use crate::catalog::ModelCost;

/// What one model charges, in US dollars per million tokens.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct UsagePrices {
    pub input: f64,
    pub output: f64,
    pub cache_read: Option<f64>,
    pub cache_write_5m: Option<f64>,
    pub cache_write_1h: Option<f64>,
}

/// Anthropic bills a one-hour cache write at twice the base input price (a
/// five-minute write at 1.25x). The catalogue records only one write price —
/// the five-minute one — so the one-hour rate is derived from the input
/// price, and only for a model that has a write price at all.
const ONE_HOUR_WRITE_MULTIPLIER: f64 = 2.0;

impl UsagePrices {
    pub fn from_catalog(cost: &ModelCost) -> Self {
        Self {
            input: cost.input,
            output: cost.output,
            cache_read: cost.cache_read,
            cache_write_5m: cost.cache_write,
            cache_write_1h: cost.cache_write.map(|_| cost.input * ONE_HOUR_WRITE_MULTIPLIER),
        }
    }
}

/// Prices one response (or any sum of them) at a single model's rates.
pub fn price(usage: &UsagePair, prices: Option<&UsagePrices>) -> CostEstimate {
    let Some(prices) = prices else {
        // Nothing to price with: an honest "unknown", whatever was measured.
        return CostEstimate { complete: usage_is_empty(usage), ..CostEstimate::default() };
    };
    // A sender that did not itemise the cache left any cached input unpriced.
    let mut complete = usage.reports_cache();
    let mut charge = |tokens: i64, rate: Option<f64>| -> f64 {
        if tokens <= 0 {
            return 0.0;
        }
        match rate {
            Some(rate) => tokens as f64 / 1_000_000.0 * rate,
            None => {
                complete = false;
                0.0
            }
        }
    };
    let input_usd = charge(usage.input_tokens, Some(prices.input));
    let cache_read_usd = charge(usage.cache_read(), prices.cache_read);
    let cache_write_usd = charge(usage.cache_write_5m(), prices.cache_write_5m)
        + charge(usage.cache_write_1h(), prices.cache_write_1h);
    let output_usd = charge(usage.output_tokens, Some(prices.output));
    CostEstimate {
        total_usd: input_usd + cache_read_usd + cache_write_usd + output_usd,
        input_usd,
        cache_read_usd,
        cache_write_usd,
        output_usd,
        complete,
    }
}

/// Sums two estimates; the result is complete only when both were.
pub fn add(a: CostEstimate, b: CostEstimate) -> CostEstimate {
    CostEstimate {
        total_usd: a.total_usd + b.total_usd,
        input_usd: a.input_usd + b.input_usd,
        cache_read_usd: a.cache_read_usd + b.cache_read_usd,
        cache_write_usd: a.cache_write_usd + b.cache_write_usd,
        output_usd: a.output_usd + b.output_usd,
        complete: a.complete && b.complete,
    }
}

fn usage_is_empty(usage: &UsagePair) -> bool {
    usage.total_input() == 0 && usage.output_tokens == 0
}

#[cfg(test)]
mod tests {
    use super::*;

    fn opus() -> UsagePrices {
        UsagePrices::from_catalog(&ModelCost {
            input: 5.0,
            output: 25.0,
            cache_read: Some(0.5),
            cache_write: Some(6.25),
        })
    }

    #[test]
    fn cached_input_is_not_charged_at_the_uncached_price() {
        // Response 1 of the reported reproduction.
        let cost = price(&UsagePair::from_counts(4, 170_000, 20_000, 0, 449), Some(&opus()));
        assert!((cost.input_usd - 0.000_02).abs() < 1e-12);
        assert!((cost.cache_read_usd - 0.085).abs() < 1e-12);
        assert!((cost.cache_write_usd - 0.125).abs() < 1e-12);
        assert!((cost.output_usd - 0.011_225).abs() < 1e-12);
        assert!(cost.complete);
        let naive = 190_004.0 / 1_000_000.0 * 5.0;
        assert!(cost.total_usd < naive, "{} must be below the naive {naive}", cost.total_usd);
    }

    #[test]
    fn one_hour_writes_cost_more_than_five_minute_writes() {
        let five = price(&UsagePair::from_counts(0, 0, 1_000_000, 0, 0), Some(&opus()));
        let hour = price(&UsagePair::from_counts(0, 0, 0, 1_000_000, 0), Some(&opus()));
        assert_eq!(five.cache_write_usd, 6.25);
        assert_eq!(hour.cache_write_usd, 10.0);
    }

    #[test]
    fn a_model_without_a_write_price_cannot_price_writes() {
        // OpenAI-style catalogue rows have a read price and no write price.
        let prices = UsagePrices::from_catalog(&ModelCost {
            input: 2.0,
            output: 8.0,
            cache_read: Some(0.5),
            cache_write: None,
        });
        assert!(price(&UsagePair::from_counts(10, 10, 0, 0, 10), Some(&prices)).complete);
        assert!(!price(&UsagePair::from_counts(10, 10, 10, 0, 10), Some(&prices)).complete);
    }

    #[test]
    fn unknown_prices_or_an_unitemised_cache_are_incomplete() {
        assert!(!price(&UsagePair::from_counts(1, 0, 0, 0, 1), None).complete);
        let legacy = UsagePair { input_tokens: 10, output_tokens: 10, ..Default::default() };
        assert!(!price(&legacy, Some(&opus())).complete, "its cache use is unknown");
    }

    #[test]
    fn sums_stay_incomplete_once_anything_was() {
        let priced = price(&UsagePair::from_counts(1, 0, 0, 0, 1), Some(&opus()));
        let unpriced = price(&UsagePair::from_counts(1, 0, 0, 0, 1), None);
        assert!(add(priced, priced).complete);
        assert!(!add(priced, unpriced).complete);
        assert_eq!(add(priced, unpriced).total_usd, priced.total_usd);
    }
}
