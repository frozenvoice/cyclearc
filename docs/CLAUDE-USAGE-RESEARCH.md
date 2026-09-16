# Claude shared subscription usage and Desktop history

Reviewed **2026-09-12** against official Anthropic documentation and the installed
Claude CLI **2.1.233** (`--help` and `auth status --help` only).

Rechecked **2026-09-16** with Claude Desktop **1.52386.3.0** and its Code tab after
the user's reported Code use. Its displayed subscription usage matched a local sample in
`%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\plan-usage-history.json`:
version 2, samples with Unix-millisecond `t`, `org`, and `u.fh` / `u.sd` percentage
fields. The legacy `%APPDATA%\Claude\plan-usage-history.json` path is also recognized.
The Desktop Code display (15% five-hour, 7% weekly in the check) matched the file, and
opening Desktop created a new sample without a separate CLI model request. This is an
observed app-owned local schema, not a public Anthropic API contract.

## Finding and decision

Claude Web, Desktop and Code consume the same subscription usage allowance.
CycleArc's five-hour and seven-day percentages describe that **shared subscription
quota**. CycleArc can receive samples from either Claude Code's statusLine or Claude
Desktop's subscription usage history.
See [how usage limits work](https://support.claude.com/en/articles/11647753-how-do-usage-and-length-limits-work)
and [using Code with Pro or Max](https://support.claude.com/en/articles/11145838-use-claude-code-with-your-pro-or-max-plan).

The reviewed public documentation provides no supported API or stable noninteractive
CLI command for independently reading an individual subscription's current shared
five-hour/seven-day utilization. The Desktop history file is a practical local source
observed in the installed app, but its schema can change or disappear. CycleArc therefore
reads it passively when available, verifies the signed-in Pro/Max identity and organization
against the saved binding, and keeps the last good sample when the file is unavailable or
unrecognized. No server polling, cookie/token access, or model request is used.

Keep the existing official statusLine receiver and add the Desktop history reader. When
both sources exist, display the sample with the newer observation time. Desktop history
does not include reset timestamps, so its five-hour and weekly reset times remain unknown;
CLI statusLine reset times are retained when that source is the newer observation. Keep
the **Open usage page** action for a manual browser check, and label values as **last
received/read** rather than live/current.

## Interfaces checked

| Official interface | What it supplies | Decision |
| --- | --- | --- |
| [Claude Code statusLine](https://code.claude.com/docs/en/statusline) | `rate_limits.five_hour` and `seven_day`, each with utilization and reset time, when available. The docs say subscription rate-limit fields appear only after the first API response in a session. | Retain as a passive sample source. |
| Claude Desktop local `plan-usage-history.json` (observed in Desktop 1.52386.3.0) | Version 2 samples with Unix-millisecond observation time, organization ID and `fh` / `sd` percentages. It does not provide reset timestamps. | Read passively when available; treat the app-owned schema as best-effort and keep last-good data on change or failure. |
| [Claude CLI reference](https://code.claude.com/docs/en/cli-reference) | Official login/status commands provide authentication metadata. The documented commands and installed CLI help expose no separate JSON subscription-quota query. | Keep login verification separate from usage receipt. |
| [Interactive commands](https://code.claude.com/docs/en/commands) | `/usage` is a human-facing plan-usage display. | Do not scrape terminal output or treat `claude -p /usage` as a quota API. |
| [Agent SDK TypeScript reference](https://code.claude.com/docs/en/agent-sdk/typescript) | `accountInfo()` provides identity/subscription metadata. Rate-limit events belong to a session; local slash-command output is text. | No documented independent structured shared-quota query identified. |
| [Usage and Cost API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api) | Organization API usage and costs, with Admin API access. | Different scope; does not provide personal subscription utilization. |
| [Rate Limits API](https://platform.claude.com/docs/en/manage-claude/rate-limits-api) | Configured organization/workspace Messages API limits, such as requests or tokens per minute. Admin API access is unavailable to individual accounts. | Different limits and account scope. |
| [Claude Code Analytics API](https://platform.claude.com/docs/en/manage-claude/claude-code-analytics-api) | Organization-level daily Code activity, token and cost aggregates, with delayed availability. | Does not expose the current shared subscription remainder. |
| [Claude usage page](https://claude.ai/settings/usage) | The signed-in user's interactive usage view; linked by the [official routines guide](https://code.claude.com/docs/en/routines). | Open normally for manual inspection. No page content, cookies or tokens are collected. |

## Freshness and UI contract

- Web/Desktop activity contributes to the shared allowance. Claude Desktop Code also
  writes a local history sample, but writes can be delayed and the file is not a server
  query. A previously received/read percentage may therefore differ from current usage.
- A statusLine command can run for events other than a model response. Its timer
  option reruns the local command; the documentation does not promise an independent
  server quota fetch. CycleArc's receipt time describes **local delivery**, not when
  Anthropic last measured usage. A recent callback must not be presented as a live
  account query.
- Desktop history's `t` is the observation timestamp used to compare it with a statusLine
  sample. CycleArc reads at the normal two-second passive/manual refresh cadence and does
  not ask Desktop to create a sample. If Desktop has not written after a use, keep the
  last-good values and report the source as unavailable or stale as appropriate.
- Desktop samples are accepted only for a verified matching Pro/Max binding and
  organization. An unverified identity is rejected without replacing the last-good data.
- Keep unknown windows unknown. Idle time and elapsed reset timestamps preserve the Received
  state and original receipt. Missing/malformed input or a verified request/authentication failure
  preserves last-good values as stale. Polling, Refresh and opening the usage page do not renew
  a receipt or reset values to zero.
- The usage-page button uses the user's default browser. The user must select the
  intended Claude login there; selecting a CycleArc profile does not switch browser
  accounts. Opening the page does not import values into CycleArc.
- Do not add web scraping, cookie/token extraction, undocumented endpoints, terminal
  screen parsing, SDK-generated model turns, or a model request merely to measure quota.
  The Desktop reader may open only the app-owned history file and never auth, token,
  cookie, conversation or transcript files.

The investigation used public documentation, local CLI help and a real Desktop Code
observation on Desktop 1.52386.3.0. It did not access credential files, cookies or a
private endpoint, and it did not create a separate model request to measure usage.
