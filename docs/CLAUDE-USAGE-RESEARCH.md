# Claude shared subscription usage

Reviewed **2026-09-12** against official Anthropic documentation and the installed
Claude CLI **2.1.233** (`--help` and `auth status --help` only).

Rechecked **2026-09-14** for a Desktop Code report: the connected terminal configuration had
no projected receipt. The official [Desktop comparison](https://code.claude.com/docs/en/desktop#coming-from-the-cli)
describes its graphical interface over the same engine, while the
[statusLine guide](https://code.claude.com/docs/en/statusline) describes terminal output and
local script callbacks. CycleArc's collector remains the terminal statusLine path; using
the Desktop Code tab is not evidence that this callback ran. The UI now names the terminal
explicitly. This is CycleArc's supported-integration boundary, not an explicit assertion in
Anthropic's Desktop documentation that shared settings can never run statusLine. The Desktop
docs describe shared settings but do not promise this terminal callback. No independent
Desktop usage API or collector is introduced.

## Finding and decision

Claude Web, Desktop and Code consume the same subscription usage allowance.
CycleArc's five-hour and seven-day percentages describe that **shared subscription
quota**. Claude Code is the source through which CycleArc receives a sample.
See [how usage limits work](https://support.claude.com/en/articles/11647753-how-do-usage-and-length-limits-work)
and [using Code with Pro or Max](https://support.claude.com/en/articles/11145838-use-claude-code-with-your-pro-or-max-plan).

The reviewed public documentation provides no supported API or stable noninteractive
CLI command for independently reading an individual subscription's current shared
five-hour/seven-day utilization without a model request. This is a finding about
the documented interfaces on the review date, not a claim about private endpoints.

Keep the existing official statusLine receiver. Add an **Open usage page** action
for a manual check in the user's browser. Label the display **Claude subscription
usage**, explain its shared scope, and describe values as **last received** even
when a callback arrived recently. No additional collector is introduced.

## Interfaces checked

| Official interface | What it supplies | Decision |
| --- | --- | --- |
| [Claude Code statusLine](https://code.claude.com/docs/en/statusline) | `rate_limits.five_hour` and `seven_day`, each with utilization and reset time, when available. The docs say subscription rate-limit fields appear only after the first API response in a session. | Retain as a passive sample source. |
| [Claude CLI reference](https://code.claude.com/docs/en/cli-reference) | Official login/status commands provide authentication metadata. The documented commands and installed CLI help expose no separate JSON subscription-quota query. | Keep login verification separate from usage receipt. |
| [Interactive commands](https://code.claude.com/docs/en/commands) | `/usage` is a human-facing plan-usage display. | Do not scrape terminal output or treat `claude -p /usage` as a quota API. |
| [Agent SDK TypeScript reference](https://code.claude.com/docs/en/agent-sdk/typescript) | `accountInfo()` provides identity/subscription metadata. Rate-limit events belong to a session; local slash-command output is text. | No documented independent structured shared-quota query identified. |
| [Usage and Cost API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api) | Organization API usage and costs, with Admin API access. | Different scope; does not provide personal subscription utilization. |
| [Rate Limits API](https://platform.claude.com/docs/en/manage-claude/rate-limits-api) | Configured organization/workspace Messages API limits, such as requests or tokens per minute. Admin API access is unavailable to individual accounts. | Different limits and account scope. |
| [Claude Code Analytics API](https://platform.claude.com/docs/en/manage-claude/claude-code-analytics-api) | Organization-level daily Code activity, token and cost aggregates, with delayed availability. | Does not expose the current shared subscription remainder. |
| [Claude usage page](https://claude.ai/settings/usage) | The signed-in user's interactive usage view; linked by the [official routines guide](https://code.claude.com/docs/en/routines). | Open normally for manual inspection. No page content, cookies or tokens are collected. |

## Freshness and UI contract

- Web/Desktop activity contributes to the shared allowance, but those surfaces do
  not send callbacks to this integration. A previously received percentage may
  therefore differ from the account's current usage.
- A statusLine command can run for events other than a model response. Its timer
  option reruns the local command; the documentation does not promise an independent
  server quota fetch. CycleArc's receipt time describes **local delivery**, not when
  Anthropic last measured usage. A recent callback must not be presented as a live
  account query.
- Keep unknown windows unknown. Preserve last-good values as stale after the existing
  five-minute receipt threshold, expired reset, or missing/malformed input. Polling,
  Refresh and opening the usage page do not renew a receipt or reset values to zero.
- The usage-page button uses the user's default browser. The user must select the
  intended Claude login there; selecting a CycleArc profile does not switch browser
  accounts. Opening the page does not import values into CycleArc.
- Do not add web scraping, cookie/token extraction, undocumented endpoints, terminal
  screen parsing, SDK-generated model turns, or a model request merely to measure quota.

The investigation used public documentation and local help output. No model request,
credential-file access or authenticated private endpoint probe was performed.
