# Credit, spending and reset inventory sources

Review date: 2026-10-13. These are distinct metrics; none is inferred from a quota percentage.

| Provider | Available projection | Deliberately unavailable |
| --- | --- | --- |
| Codex | Remaining workspace usage-credit balance, negative balance, explicit unlimited; existing earned resets and expirations | Actual credits consumed, currency value of credits, reasons for balance changes |
| Claude | OAuth extra-usage monthly spend/cap and calculated amount to cap; optional read-only reset program when supplied | Prepaid wallet through the supported Desktop path; offers requiring a different client surface |
| Cursor | Personal on-demand spend; separate aggregate plan and included/bonus/total counters; separate member/team amounts and Auto/API percentages | Monetary amounts inferred for percentage lanes or model-specific caps; included usage called cash charges |

## Codex

The [official app-server reference](https://developers.openai.com/codex/app-server) defines remaining workspace credit details and optional earned resets on `account/rateLimits/read`. Its separate `account/usage/read` provides token activity, not consumed credits or billed money. The `CreditsSnapshot` contract used by CycleArc contains only `hasCredits`, `unlimited`, and `balance`. No verified consumed-credit counter is available on this path. Balance history is labelled balance/change, never spending: top-ups, grants and corrections cannot be distinguished by subtraction. Credits are never mapped to currency.

## Claude spend and prepaid funds

The [official usage-credit guide](https://support.claude.com/en/articles/12429409-manage-usage-credits-for-paid-claude-plans) distinguishes extra-usage spending and its monthly cap from prepaid funds. Existing normalization and official client schema evidence are recorded in [Claude research](CLAUDE-USAGE-RESEARCH.md). No verified prepaid-wallet field exists on CycleArc's identity-verified Desktop OAuth usage path. Browser-session implementations require cookies and different attribution, so CycleArc explicitly displays prepaid balance as Not provided. The calculated amount to monthly cap never represents prepaid funds. No UTC calendar-month billing boundary is inferred; a measured spend decrease is a discontinuity without an invented reset time.

## Claude free resets: compatibility limit

The [official limit reset article](https://support.claude.com/en/articles/17007452-what-is-a-limit-reset) describes occasional grants restoring five-hour or weekly limits, with optional expiry. Use is irreversible and does not refund extra usage or change prepaid funds. The supported action is Settings > Usage on web/Desktop. CycleArc never sends reset POST requests or consumes a Claude grant.

Cross-checked actual implementations:

- [CodexBar notes](https://github.com/steipete/CodexBar/blob/main/docs/claude.md) and [OAuth fetcher](https://github.com/steipete/CodexBar/blob/main/Sources/CodexBarCore/Providers/Claude/ClaudeOAuth/ClaudeOAuthUsageFetcher.swift) decode `cedar_ember`, opt in with `?cedar_ember=1`, and replace the User-Agent with a Claude CLI surface. Optional errors do not invalidate ordinary quota.
- [OpenUsage notes](https://github.com/robinebers/openusage/blob/main/docs/providers/claude.md) and [client](https://github.com/robinebers/openusage/blob/main/Sources/OpenUsage/Providers/Claude/ClaudeUsageClient.swift) use separate `?cedar_ember=1&skip_spend=1` at most hourly and CLI surface identification. Their client documents plain usage returning null and an unrecognized surface returning ineligible. This does not establish Desktop credential entitlement to a CLI surface.
- [CodexBar compatibility report](https://github.com/steipete/CodexBar/issues/3895) records Claude Code 2.1.280 opt-in behaviour and grant fields; this is an observed report, not an Anthropic contract.
- Installed official Claude Code returned **2.1.289** on this PC. Static inspection of its native executable confirmed `cedar_ember` and `resets_left` symbols. This establishes client implementation presence only. [Official statusLine documentation](https://code.claude.com/docs/en/statusline) supplies subscription windows, not a documented independent inventory API.

CycleArc parses any optional program in the **existing ordinary GET**. Production adds no query or request, impersonates no CLI, reads no CLI credentials/cookies and never redeems. Missing data remains Not provided. Ineligibility remains unavailable, distinct from an eligible empty inventory. Source-gated inventory cannot be guaranteed with this narrow Desktop path; users can inspect/redeem on official Usage. The schema is private and may change.

An explicit one-shot `--claude-reset-query-live-read` compatibility probe on 2026-10-13 verified the existing Desktop identity/organization first, then added only `?cedar_ember=1` to the usage GET with the **unchanged CycleArc User-Agent and credential**. The response preserved subscription quota/Extra usage and supplied `eligible:false` with `ineligible_reason:"surface"`. No raw response, identity, amount or credential was logged or saved. This directly verifies a surface restriction for the tested account/client combination, not a universal entitlement statement. The test-only handler is excluded from ordinary refresh/default tests, bounds reads to 64 KiB and the existing 20-second request operation, and never retries or redeems. Production remains on the ordinary compatible path; a private CLI-surface workaround is not presented as a supported Desktop API.

Eligible inventory requires a grants array. Projected grants contain bounded remaining/total integers, nullable explicit start/expiry times, required paused, optional usable-now/use-requires-limit and recognized five-hour/weekly targets. IDs, labels and redemption handles are excluded. Available count sums active, unpaused, unexpired remaining resets, independently of current use gates; missing usable-now is unknown. Malformed inventory preserves ordinary quota and extra usage. Limits of 200 source rows and 50 remaining resets bound size; the projected atomic cache is limited to 64 KiB. Existing identity, organization, binding-generation and commit guards apply. Failures retain the original observation with a warning, local time expires offers between fetches, and passive cache reads do not renew clocks. Alerts exclude failed inventory.

The existing opt-in `--claude-extra-live-read` probe now also prints only reset presence, eligibility and parser success. It writes no account/cache/settings and prints no amounts, identifiers, raw replies or credentials. Synthetic fixture evidence is separate from real-account compatibility.

## Cursor aggregate amounts and split percentages

The [official models/pricing guide](https://cursor.com/docs/models-and-pricing) separates included usage from extra billing. Current app-owned fields are cross-checked against [CodexBar models](https://github.com/steipete/CodexBar/blob/main/Sources/CodexBarCore/Providers/Cursor/CursorStatusProbe.swift) and [projection](https://github.com/steipete/CodexBar/blob/main/Sources/CodexBarCore/Providers/Cursor/CursorStatusProbe%2BUsageSummary.swift).

`individualUsage.plan.{used,limit,remaining}` and `breakdown.{included,bonus,total}` are aggregate personal-plan USD-cent counters normalized once. Breakdown total is usage, not a new limit. Amount failure has its own clock and does not remove Auto/API percentages. Reported negative remaining is preserved. Unsupported speculative `autoUsed`/`apiUsed` fields and percentage-derived amounts are not used. Personal on-demand remains separate additional usage; member/team counters retain scope and are never added to it. Existing GETs, identity verification, backoff and cache isolation are unchanged. See [Cursor evidence](CURSOR.md) for previously verified production shapes.
