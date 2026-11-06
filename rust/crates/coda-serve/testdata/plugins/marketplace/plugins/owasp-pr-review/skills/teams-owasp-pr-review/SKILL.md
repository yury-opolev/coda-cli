---
name: teams-owasp-pr-review
description: Review one pull request against the OWASP Top 10, one subagent per category.
argument-hint: <pr-url-or-number>
---
# OWASP single-PR review

The shared material lives in the sibling skill folder; read it in place:

- the reviewer contract: `../teams-owasp-pr-review-monitor/reviewer-contract.md`
- the ten category briefs: `../teams-owasp-pr-review-monitor/categories/a01-*.md` … `a10-*.md`
- the helper script: `../teams-owasp-pr-review-monitor/scripts/Get-OwaspCategories.ps1`

Steps:
1. Read the reviewer contract and note its contract id.
2. Run the helper script with PowerShell (`pwsh -NoProfile -File <script>`) and note its output.
3. For each category file, start one subagent (`task` tool) with that category brief and the PR ($ARGUMENTS).
4. Merge the reports following the contract.
