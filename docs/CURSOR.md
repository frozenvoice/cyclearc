# Cursor usage research and contract

This file records the read-only interface research and contract used by the Windows
Cursor provider. Local implementation checks and remaining gaps are recorded in
[Validation](VALIDATION.md).

## Windows verification

On 2026-09-20, with Cursor 3.21.13 signed in on Windows, the local session
database was found at:

`%APPDATA%\Cursor\User\globalStorage\state.vscdb`

The `ItemTable` row named `cursorAuth/accessToken` contained the access JWT.
The database was read only. The access JWT was kept in memory for the check;
no token, refresh token, email, or raw response was recorded here.

The following bounded, read-only requests returned HTTP 200. Response bodies
are intentionally summarized rather than copied.

| Source | Request | Authentication | Result |
| --- | --- | --- | --- |
| Cursor native dashboard RPC | `POST https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage` | `Authorization: Bearer <access JWT>`, `Content-Type: application/json`, `Connect-Protocol-Version: 1`, body `{}` | 200 |
| Cursor native account RPC | `POST https://api2.cursor.sh/aiserver.v1.AuthService/GetUserMeta` | Same Bearer/Connect headers and `{}` body | 200 |
| Cursor web session | `GET https://cursor.com/api/auth/me` | `Cookie: WorkosCursorSessionToken=...` | 200 |
| Cursor web session | `GET https://cursor.com/api/usage-summary` | Same Cookie header | 200 |
| Cursor web session | `POST https://cursor.com/api/dashboard/get-sand-usage-status` | Same Cookie, `Origin: https://cursor.com`, JSON body `{}` | 200 |

The web cookie is derived only in memory from the JWT when the web endpoints
are used. The current upstream construction is
`WorkosCursorSessionToken=<JWT sub user id>%3A%3A<access JWT>`; the final
pipe-delimited component of the JWT `sub` is the user id. The app must not log,
persist, or expose this value. The access JWT is accepted only while its
decoded `exp` is more than 60 seconds away; the upstream Cursor.app reader does
not refresh it. The production .NET reader/client was also verified on this PC at
2026-09-20 12:35 UTC: server identity matched, four separate quota rows were read,
three contained known percentages, four carried reset timestamps, and the optional
Grok request completed. The probe wrote no account, connection or quota cache.

## Usage fields and display rules

The web usage response has this shape, with the optional values preserved as
optional values:

```text
billingCycleStart, billingCycleEnd, membershipType, limitType, isUnlimited
autoModelSelectedDisplayMessage, namedModelSelectedDisplayMessage
individualUsage.plan:
  enabled, used, limit, remaining, breakdown.{included, bonus, total}
  autoPercentUsed, apiPercentUsed, totalPercentUsed
individualUsage.onDemand: enabled, used, limit, remaining
teamUsage: optional shared/team blocks
```

The percentages and the dollar/cents counters are different source metrics.
Cursor's own support response says that `totalPercentUsed`,
`autoPercentUsed`, and `apiPercentUsed` are not calculated as
`totalSpend / limit`; the dashboard `displayMessage` uses
`includedSpend / limit` and can disagree with those percentages. Therefore:

- `autoPercentUsed` is the monthly Cursor Models / Auto + Composer lane;
  display its remaining percentage as `100 - value` when the field is present.
- `apiPercentUsed` is the monthly Other Models / named-model lane; display it
  separately with its own remaining percentage.
- `totalPercentUsed` is an aggregate source metric, not a third independent
  quota. Only when both split percentages are absent, it is shown as the
  explicitly labelled **Plan total** fallback. Do not derive a bar from
  `plan.used / plan.limit`, and do not merge the Auto and API lanes.
- `billingCycleEnd` is the reset timestamp for the monthly lanes. The app's
  `updatedAt` is the actual successful response time, not the billing-cycle
  timestamp.
- `individualUsage.onDemand` is a separate spend allowance. A numeric limit is
  displayable as its own row. A null or missing limit stays unknown; only an
  explicit unlimited flag means unlimited. Neither becomes zero. An explicitly
  disabled allowance is shown as Off.
- `teamUsage` is a separate shared/team scope. It must not be combined with an
  individual allowance; if the source cannot identify the applicable scope,
  leave that lane unavailable.

The optional Sand/Grok Bot response is another independent allowance:

```text
currentPeriodStart, nextResetTimestampUtc, usagePercent
hasAvailableUsage, hasNonZeroIncludedLimit, includedLimitZero
sandTrialExpiresAt
```

Show it only when the response establishes an included allowance or an
unexpired trial. A paid allowance resets at `nextResetTimestampUtc`; a trial
expiry is not a recurring reset. A missing/malformed/expired trial, or a Sand
request failure, must omit only this lane and preserve the monthly lanes.

Missing, null, malformed, or failed values remain unknown. They are not
converted to zero. A failed refresh may retain the last valid snapshot and its
original successful timestamp, but must not claim that a stale value is current.
CodexBar also has a legacy request-based `GET /api/usage?user=<sub>` fallback.
CycleArc does not implement that unverified legacy route; it leaves an unsupported
plan unknown instead of mixing request counts with token-based Auto/API lanes.

## Source evidence

- [Cursor official usage and limits](https://prod.cursor.com/help/models-and-usage/usage-limits) documents the separate monthly Cursor Models and Other Models pools and billing-cycle reset.
- [CodexBar Cursor provider notes](https://github.com/steipete/CodexBar/blob/main/docs/cursor.md) documents the endpoint set, app-token fallback, cookie construction policy, and independent Sand allowance.
- [CodexBar Cursor app auth](https://github.com/steipete/CodexBar/blob/main/Sources/CodexBarCore/Providers/Cursor/CursorAppAuth.swift) shows read-only `state.vscdb` access, JWT `sub`/`exp` handling, and the `WorkosCursorSessionToken` construction.
- [CodexBar status probe](https://github.com/steipete/CodexBar/blob/main/Sources/CodexBarCore/Providers/Cursor/CursorStatusProbe.swift) shows Cookie-only web requests, `Origin` handling for Sand, and legacy request fallback.
- [CodexBar usage-summary projection](https://github.com/steipete/CodexBar/blob/main/Sources/CodexBarCore/Providers/Cursor/CursorStatusProbe%2BUsageSummary.swift) shows the optional fields and separate plan/Auto/API/on-demand/team paths.
- [CodexBar Sand projection](https://github.com/steipete/CodexBar/blob/main/Sources/CodexBarCore/Providers/Cursor/CursorSandUsage.swift) defines the paid/trial/reset semantics.
- [Cursor support forum: usage-summary example](https://forum.cursor.com/t/cursor-usages-is-not-showing-correct-information/170210/16) contains a sanitized public response shape with explicit `null` on-demand limits.
- [Cursor support forum: percentage semantics](https://forum.cursor.com/t/bug-report-dashboard-ui-percentages-frozen-due-to-totalpercentused-calculation-bug-in-get-current-period-usage-api/168210/7) explains why percentage fields and included dollar counters can disagree.
- [CodexBar 0.61.0 release](https://github.com/steipete/CodexBar/releases/tag/v0.61.0) is the current upstream release observed during this research; the `main` source links above may contain later changes.
