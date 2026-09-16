# Claude shared subscription usage and Desktop live quota

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

The current live proof was run with Claude Desktop **2.110.0.0** on 2026-09-16: the bound account returned 50% five-hour and 12% weekly usage, matching the visible Claude usage page. CycleArc obtains the Desktop OAuth access token from the app-owned config.json and Local State, decrypts it only in memory, then calls Anthropic first-party GET https://api.anthropic.com/api/oauth/profile and GET https://api.anthropic.com/api/oauth/usage. The profile response exposes the account email in its email field and is checked against the bound fingerprint before usage is accepted. Claude Desktop's own Electron GET /api/organizations/{org}/usage route is a separate internal path and is not used by CycleArc. None of these endpoints is an official public subscription-quota API.
## Finding and decision

Claude Web, Desktop and Code consume the same subscription usage allowance.
CycleArc's five-hour and seven-day percentages describe that **shared subscription
quota**. CycleArc can receive samples from either Claude Code's statusLine or Claude
Desktop's subscription usage history.
See [how usage limits work](https://support.claude.com/en/articles/11647753-how-do-usage-and-length-limits-work)
and [using Code with Pro or Max](https://support.claude.com/en/articles/11145838-use-claude-code-with-your-pro-or-max-plan).

The reviewed public documentation provides no supported public API or stable noninteractive CLI command for independently reading an individual subscription's current shared five-hour/seven-day utilization. CycleArc 0.5.9 uses a narrow private/internal compatibility path: it obtains the Desktop OAuth token from app-owned config.json and Local State, decrypts it only in memory with Windows DPAPI/AES-GCM, calls Anthropic first-party profile and usage endpoints, and verifies the returned profile against the bound identity before accepting quota. This is not an official Anthropic API contract and may change without notice. No credential write or refresh, cookie read, model request or transcript access is involved.

Keep the official statusLine receiver and Desktop history reader as fallback sources. When both fallback sources exist, display the newer observation time. A successful live response is labeled **Updated** with its server-fetched time; fallback data is labeled **Received** with its source time. Desktop history has no reset timestamps, so those fields remain unknown. The usage-page action remains available for manual browser inspection. The public interface review still matters: the private live path must be treated as unstable and kept narrow.

## Interfaces checked

| Official interface | What it supplies | Decision |
| --- | --- | --- |
| [Claude Code statusLine](https://code.claude.com/docs/en/statusline) | `rate_limits.five_hour` and `seven_day`, each with utilization and reset time, when available. The docs say subscription rate-limit fields appear only after the first API response in a session. | Retain as a passive sample source. |
| Claude Desktop local `plan-usage-history.json` (observed in Desktop 1.52386.3.0) | Version 2 samples with Unix-millisecond observation time, organization ID and `fh` / `sd` percentages. It does not provide reset timestamps. | Keep as a passive fallback; treat the app-owned schema as best-effort and keep last-good data on change or failure. |
| Claude Desktop OAuth token plus Anthropic first-party profile/usage requests (validated with Desktop 2.110.0.0) | CycleArc calls GET https://api.anthropic.com/api/oauth/profile, verifies the returned email and identity binding, then calls GET https://api.anthropic.com/api/oauth/usage for shared windows. Desktop's separate Electron GET /api/organizations/{org}/usage route is not used. | Narrow 0.5.9 live compatibility path only; private/internal and unstable, not a public API. |
| [Claude CLI reference](https://code.claude.com/docs/en/cli-reference) | Official login/status commands provide authentication metadata. The documented commands and installed CLI help expose no separate JSON subscription-quota query. | Keep login verification separate from usage receipt. |
| [Interactive commands](https://code.claude.com/docs/en/commands) | `/usage` and the installed CLI print mode are human-facing text displays, not a structured independent quota API. | Do not scrape terminal output or treat `claude -p /usage` as a quota API; this path is not implemented. |
| [Agent SDK TypeScript reference](https://code.claude.com/docs/en/agent-sdk/typescript) | `accountInfo()` provides identity/subscription metadata. Rate-limit events belong to a session; local slash-command output is text. | No documented independent structured shared-quota query identified. |
| [Usage and Cost API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api) | Organization API usage and costs, with Admin API access. | Different scope; does not provide personal subscription utilization. |
| [Rate Limits API](https://platform.claude.com/docs/en/manage-claude/rate-limits-api) | Configured organization/workspace Messages API limits, such as requests or tokens per minute. Admin API access is unavailable to individual accounts. | Different limits and account scope. |
| [Claude Code Analytics API](https://platform.claude.com/docs/en/manage-claude/claude-code-analytics-api) | Organization-level daily Code activity, token and cost aggregates, with delayed availability. | Does not expose the current shared subscription remainder. |
| [Claude usage page](https://claude.ai/settings/usage) | The signed-in user's interactive usage view; linked by the [official routines guide](https://code.claude.com/docs/en/routines). | Open normally for manual inspection. No page content, cookies or tokens are collected. |

## Freshness and UI contract

- Web/Desktop activity contributes to the shared allowance. A successful live OAuth response is the current server result at fetch time and is labeled **Updated**. StatusLine and Desktop history are local fallback receipts and may lag behind current usage; they are labeled **Received** with their original source time.
- Manual refresh and the configured account interval actively try the live Desktop request. The two-second passive loop is cache-only: it reads local statusLine and Desktop history projections and does not send a server request, start Desktop, invoke Claude or renew a receipt.
- The live reader uses only app-owned config.json and Local State to obtain the Desktop OAuth token. DPAPI/AES-GCM decryption is memory-only; the access token is never stored, refreshed or written back. Cookies, prompts, responses, conversations and transcripts are outside the reader scope.
- Profile identity is checked before usage. A mismatch or unavailable identity never replaces last-good quota and the mismatched live value is hidden. Authentication, rate-limit, request and unavailable failures preserve the last-good projection as stale and expose a retry path for the intended Desktop login.
- When live data is unavailable, statusLine and Desktop history are compared by observation time. Unknown windows and Desktop reset timestamps remain unknown; no percentage is synthesized from idle time or elapsed resets.
- The usage-page button still opens the user's default browser for manual inspection and does not import page content.
- The private endpoint is documented here as an observed implementation dependency only. It must not be described as an official public API or expanded into general web scraping, arbitrary endpoint access or terminal screen parsing.

The investigation used public documentation, local CLI help, installed Desktop code inspection and real Desktop observations. The local history schema was observed with Desktop 1.52386.3.0; the live OAuth compatibility path was validated with Desktop 2.110.0.0 against the bound 50%/12% shared quota response. This evidence establishes the 0.5.9 implementation behavior, not public endpoint stability. The app does not persist the Desktop token, modify Desktop credentials, read cookies or create a model request to measure usage.
