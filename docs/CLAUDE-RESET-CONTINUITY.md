# Claude reset evidence and source continuity

The quota service used to choose an entire source snapshot by observation time. A newer Desktop history receipt supplied percentages but constructed both windows with null reset timestamps, erasing valid server/Code dates. Independently, a valid OAuth response with absent/null `resets_at` replaced the previous live sample. Both failures are reproduced by regression tests before the production fix.

Usage source selection remains separate from reset evidence. Five-hour and weekly windows each retain a bounded ledger of actual provided timestamps, their source and original observation clock, last observed percentage/clock, invalidation and reset-grant counts. The latest usable percentage still comes from the existing source arbitration; borrowing a date never relabels that percentage as server data or renews its receipt time.

## Chosen policy

| Approach considered | Safety / cost |
| --- | --- |
| Whole-source selection | Simple, but loses dates that Desktop never supplies |
| Unconditional copying | Simple, but carries revoked/elapsed dates into a different cycle |
| In-memory continuity only | Detects changes, but restart replays pre-invalidation caches |
| Separate bounded binding ledger (chosen) | Independent window provenance and durable invalidation; a small sibling file, no quota-cache migration |

| Observation | Reset treatment |
| --- | --- |
| New verified server timestamp | Prefer the actually supplied date, including a changed date at the same clock resolution; never add a period |
| Newer Desktop or null-reset server window | Retain prior evidence only within an ordered, nondecreasing chain of observations with gaps at most 15 minutes |
| Usage decreases, a window disappears, clocks conflict or reverse, or an observation gap exceeds 15 minutes | Invalidate that window; later percentage growth cannot resurrect its old date |
| Local date contradicts a still-future server period without a period-change signal | Withhold the uncertain date; matching local dates remain direct Code observations |
| Reset-grant count declines | Invalidate the specified target windows (unknown targets invalidate both), unless that response also supplies a new verified server date |
| Auth/identity failure, binding rotation or disconnect | Do not borrow evidence across the failure/binding; existing identity protection still hides mismatched quota |
| Date has elapsed | Show awaiting update; never infer a new date or reset the percentage |

Preservation requires the supplied date to be more than 30 seconds in the future; this is a guard against an ambiguous boundary, not a measured server clock correction. Evidence is restricted to a supplied date within the reported window's duration plus five minutes of its observation. Future observation clocks cannot seed reset evidence or supersede a valid server/Desktop sample. Legacy Code-only percentages retain their existing clock-rollback warning and immutable received values. Unbound legacy snapshots keep their existing contract and do not participate in borrowing. The legacy profile-only Code cache cannot seed a bound reset at exactly `ConnectedAt` (the receipt must be later).

The original source/clock stays attached to **prior / 이전 확인** dates in detail and widget tooltips. Direct, preserved, missing/unverified, elapsed, stale continuity and global refresh-failure states remain distinct. If preserved data receives no fresh observation for 15 minutes, its countdown becomes refresh-needed. Raw past dates in direct snapshots remain available for compatibility; presentation identifies them as elapsed.

Percentages alone cannot prove that no unobserved reset and subsequent consumption occurred between receipts. Preservation therefore describes *observed continuity of previously confirmed evidence*, never fresh server confirmation. A contradictory signal fails closed. Desktop provides no period identifier or reset date, so the app does not invent one. Reset-grant comparisons use raw outstanding server counts, including paused/future/expired grants, to avoid treating a local expiration clock as a redemption; a server removing a grant is conservatively uncertain.

## Cache and performance

`accounts/<profile>/claude-reset-timing.json` is a new optional, version-1 sibling capped at 8 KiB. Existing live, Desktop and statusLine caches, settings and money/history formats are unchanged. Only typed quota evidence and a SHA-256 hash of the exact connection binding are written: no credential, email, raw organization ID, message, prompt or project content. The binding hash covers identity, connection time and generation. Saves take the existing connection lease, verify the current account/binding and compare the hash of the bytes read; a stale concurrent writer cannot overwrite a durable invalidation.

New dates and invalidations commit immediately before normal projection publication. Ordinary continuity receipts coalesce to at most one write per minute; unchanged two-second passive polls never write or renew clocks. I/O failure clears the current reset projection, reloads the safe ledger and backs off writes for one minute. A corrupt/unreadable primary, or missing primary with an old backup, quarantines prior evidence through the read time. Backups never restore potentially revoked evidence. A fresh verified observation after quarantine can establish a date again. A never-created ledger can seed from compatible existing quota caches; deliberate deletion of every ledger artifact cannot reveal an invalidation that was also deleted.

No endpoint, model call, credential access or reset redemption was added. The passive gate commits UI-only elapsed/stale transitions once so unchanged polls do not repeatedly rebind the UI. This reducer does not strengthen the unofficial OAuth/Desktop schema into a public contract. The [official Claude Code statusLine documentation](https://code.claude.com/docs/en/statusline) describes supplied `rate_limits` and Unix `resets_at`; [Limit Reset documentation](https://support.claude.com/en/articles/17007452-what-is-a-limit-reset) describes early quota restoration. Checked 2026-10-13. Missing/null OAuth reset fields are tested through the actual parser; Code's existing strict present-window parser stays intact.

## Verification

`ClaudeResetContinuityTests` and `ClaudeResetTimingStoreTests` cover source transitions, per-window omission/decrease, future/elapsed/skew boundaries, grants, clock ties/reversal, failures/recovery, exact binding isolation, restart/corruption, concurrent saves and disk throttling. `--claude-reset artifacts/previews` runs 60 actual production WPF detail/widget states in EN/KO, Dark/Light, 80/100/150% with provenance, numeric precision and clipping checks.

The build-local focus failure also had an attribution defect: an uncontrolled external foreground HWND changing was called widget focus theft even when no widget activation occurred. The default recovery suite now watches every recreated widget's native activation, keyboard focus and foreground events and rejects transient theft even after focus returns. Exact foreground/native/WPF retention is additionally required by `--widget-recovery-foreground` in the manual Windows workflow. On the local Codex host, that controlled mode refuses input because the intended guard is occluded by another HWND; this is an explicit environment failure, not a passing or skipped assertion. The hosted result is recorded in [Validation](VALIDATION.md).
