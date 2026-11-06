---
name: review
description: Adversarial code review. Spawns the advocate, skeptic and architect role agents in parallel and merges their findings.
---
# Adversarial review

1. Decide what to review from the user's request (default: the uncommitted changes).
2. Start the three role agents **in parallel, in one response**, each with the `task` tool:
   - `subagentType: "deep-review:advocate"`
   - `subagentType: "deep-review:skeptic"`
   - `subagentType: "deep-review:architect"`
   Give each the same description of what to review.
3. When all three reports have arrived, merge them into one list of findings, keeping
   disagreements visible. Finish with the line `REVIEW-MERGED (advocate, skeptic, architect)`.
