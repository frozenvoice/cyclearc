# Credit and spending monitor validation

Date: 2026-10-13 Asia/Seoul. Windows, .NET SDK 10.0.401, host 96 DPI.

## Provider evidence

See [sources and compatibility limits](CREDIT-SOURCES.md) for official references and
cross-checked implementation evidence. All three explicit production probes were read-only:
they wrote no bindings, quota caches, settings or credentials, and printed only fixed shape
metadata. No amounts, account identities or raw replies were recorded.

| Provider | Real response accepted | Unsupported or absent on this path |
| --- | --- | --- |
| Codex | Two existing profiles passed identity verification; credit object flags and balance string parsed | No verified consumed-credit counter or money conversion |
| Claude | Desktop identity and subscription verified; Extra usage supplied with enabled=false, monetary values absent and USD currency | Ordinary OAuth usage GET supplied no reset inventory. This is unknown, not zero. No verified prepaid wallet field |
| Cursor | Four separate lanes, three known percentages and four reset dates; personal on-demand used amount supplied | Personal on-demand cap/remaining absent, so no cap warning can be generated for this account |
| Cursor included plan | Actual aggregate used/limit/remaining and included/bonus/total fields all supplied and parsed | Per-Auto/API/model amounts are not inferred from percentages; included counters are not cash charges |

The additional explicit one-shot `--claude-reset-query-live-read` probe preserved the existing
Desktop credential and CycleArc User-Agent, verified profile/organization attribution, and
received `eligible:false` / `ineligible_reason:"surface"`. Subscription and Extra usage parsing
remained valid. This proves the tested client/account surface restriction; it does not prove
the behaviour of every account or establish a public API. Production does not add this query.

Claude Code 2.1.289's native symbols confirm client implementation presence, not a public
inventory contract. Upstream implementations opt into a private program and identify as a CLI
surface. CycleArc preserves the existing Desktop OAuth GET, identity/organization checks,
DPAPI/AES-GCM in-memory token protection and backoff. It parses an optional inventory if that
ordinary response supplies it; it adds no program request, CLI impersonation or reset POST.
Users inspect/use grants on the official Usage page. Types/start/expiry/paused/eligibility and
unknown usability remain separate, and private schema changes cannot invalidate subscription
quota or Extra usage.

## Focused checks

- Provider/schema/cache suite: 151 passing tests, plus the verified Claude snapshot-identity
  regression. Covers nullable/missing/malformed optional fields, flags, negative balances,
  disabled/unlimited/over-limit amounts, private program absence/ineligibility/empty inventory,
  active/future/expired/paused grants, account/organization/binding isolation and cache restore.
- History: 22 passing tests. Covers original timestamps, 24-hour/7-day/30-day filtering,
  unchanged snapshots, one-minute changed-value coalescing, 15-minute measured heartbeats,
  actual spending versus balances/deltas/included usage, source/period/cap/decrease/gap changes,
  failure/staleness, 31-day retention, bounded validation and backup recovery. A 19-account
  eviction test preserves pending values/write clocks and failed-write warnings. Decimal
  extrema keep exact balances and mark an unrepresentable delta missing rather than overflow.
- Financial alerts and widget summary: 29 passing tests. Covers independent optional-field
  clocks, unknown identity, hidden mismatches, low/negative balances, disabled/unlimited caps,
  duplicate suppression, hysteresis recovery, real period/cap changes and per-expiry-bucket
  identity. Unrelated grant changes do not repeat existing expiry alerts.
- Combined notification: two passing regressions keep both simultaneous usage/financial
  warnings and one stable account click target, preventing tray callback replacement. Both
  policies retain reserved space inside the 255-character Windows balloon body limit, even
  with long account names. See the [Windows notification structure](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw).
- Production WPF monitoring: 12 EN/KO × Dark/Light × 80/100/150% combinations. Checks rings,
  long account names, one-line optional summaries/tooltips, separate spend/cap series,
  unrelated metric failures, immediate identity/tooltip clearing, Claude count visibility and
  official Usage action. Decimal-extreme plotting renders without overflow. Defaults keep
  summaries off and trends collapsed. Existing usage-card checks passed 180 states, including
  large values, null/zero/disabled/failure, expansion persistence and work-area scrolling.

The eight `monitoring-*.png` documentation captures render real production WPF views with
explicitly synthetic accounts/data. They do not represent a user's entitlement or fabricate
unobserved past measurements. Visual review checked clipped names, currencies, reset counts,
plot gaps and the retained ring/countdown hierarchy. The separate full UI gate also exercises
injected DPI/resource states; this host's actual 96 DPI does not establish every physical
multi-monitor or OS high-contrast configuration.

## Performance and storage

No new package dependency or network request was added. The passive two-second loop still
reads local sources. History runs off the dispatcher at most once per minute; popup reads are
asynchronous and the collapsed expander performs none. Cached rereads do not renew timestamps
or write files. Retention is 31 days, 8,192 points/256 statuses/2 MiB primary plus backup per
identity, 64 saved identities and 16 loaded full states. Bounded dirty overlays preserve
coalesced values on eviction; storage/capacity failure is reported rather than inventing data.

An isolated five-account WPF pilot used the production timers, refresh/window code and fake
cancellable 10ms provider transport. Two sequential 85-second runs used the same source and
GC configuration, with history initialization ON/OFF via the harness-only
`CYCLEARC_IDLE_MONITORING=off` switch. Both passed provider-request, latest-data, identity,
visibility and account-churn assertions. Financial evaluation and UI code exist in both runs;
this comparison measures history overhead, not a complete archived pre-change product.

| Stage (10 seconds) | History ON CPU / private memory | History OFF CPU / private memory |
| --- | --- | --- |
| Initial tray | 328ms / 28.08MiB | 266ms / 25.71MiB |
| Tray after refresh | 31ms / 133.80MiB | 0ms / 133.64MiB |
| Widget after account churn | 16ms / 203.14MiB | 16ms / 191.43MiB |

History plus backups totalled **5,473 bytes in seven files** after the ON run, versus zero
in OFF. The duplicate-snapshot and 19-account tests separately verify unchanged bytes and
no writes within the one-minute interval. These are file-level disk-I/O assertions; no
device-wide disk throughput claim is made. Short pilot CPU granularity, GC, JIT, instrumentation
and UI creation affect the numbers; the higher post-churn private memory is not proven retained
memory or a leak. [Phase measurements](measurements/credit-monitor-2026-10-13-history-pilot.csv)
retain only safe derived counters, without fixture paths or account data. No long-duration
performance guarantee is inferred from this pilot.

## Integration gate

Checkpoints: `aeef72e` provider fields/evidence; `95e37c1` Claude verified-identity regression;
`46f6aaf` bounded history; `675e704` opt-in financial policies; `3e2d8a0` history eviction and
expiry-bucket corrections; `ee6ef98` WPF/app integration, combined notifications, compatibility
probes and corrected DPI fixture. Each implementation checkpoint has focused local evidence.

The initial complete unit run passed **2,245 tests with no skips**. Its UI run found an older
Codex-only card assertion, updated to require Claude's read-only card and reject redemption
buttons. A focused header run then reproduced a host-96-DPI fixture defect: CloseIcon was
detached until template realization and could not receive root DPI injection. Realizing the
header templates before injection fixed the fixture, retained the original `.001` tolerance,
and strengthened X/Y checks across ancestors and all transitions. No production header change
was needed. A later focused notification test covers both policies under Windows' size limit;
unchanged unit results and the two latest notification tests are reused by the local `-Fast`
gate. Remote full verification runs all **2,246** final tests without that shortcut.

The final local `dev-run.ps1 -NoLaunch -Fast` on executable source `ee6ef98` exited **0**
in **4m32s**. Argument-free UiSmoke printed `[ui-smoke] COMPLETE all default suites`;
native/test-flavour checks, self-contained publishing, Velopack packaging and package
verification passed. The package check exercised damaged-cache recovery, tamper rejection,
real isolated portable update/restore and data preservation. A subsequent focused `--monitoring`
run passed all 12 combinations and produced the documentation captures. Their
[hash manifest](measurements/credit-monitor-2026-10-13-previews.csv) identifies the executable
source, host DPI and synthetic origin. Existing nullable/analyzer warnings and this host's
unavailable NuGet vulnerability feed were reported; no dependency was added or upgraded.

The repository's shared `dev-run.ps1 -NoLaunch` gate validates Release build, complete unit
tests, argument-free UiSmoke, native dependency/test-flavour contracts, single-file publishing,
Velopack packaging and package verification. It preserves the running installation. GitHub's
Windows workflow is manual-only and verifies the same gate plus disposable installation,
repair, shortcut, portable and startup scenarios before merge. Exact results and run links
are recorded in the PR and [validation log](VALIDATION.md).

Checkpoint commits keep provider projection, history, alerts, integration fixes and WPF work
reviewable independently. Existing settings/cache data and retired observation files remain
compatible and preserved. Raw credentials, emails, labels, prompts, messages and project code
are excluded from the new measurement history.
