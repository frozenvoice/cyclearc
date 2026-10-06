# Observed quota history — 2026-10-05

> **Historical evidence; feature removed from the current product in work completed
> 2026-10-07 Asia/Seoul (started 2026-10-06).**
> The graph, history recorder/store/worker and their dedicated views/tests were removed.
> Current quotas and provider source/receipt/reset timestamps remain supported. Existing
> quota-observation files are left untouched and inert for rollback. This dated record,
> its measurements and screenshots are preserved; commands below require the recorded
> earlier source revision and are not current-product test commands. See
> [current architecture](ARCHITECTURE.md#current-quota-and-retired-observation-history)
> and [removal validation](VALIDATION.md).

This development change adds a small graph of actual quota observations and simplifies
the detail window controls. It does not estimate depletion, consumption pace or time
remaining, recommend accounts, switch another application's login, or call a model.

## Behavior and boundaries

- Refresh, display-account selection, settings and the independently saved zoom controls
  remain directly accessible. Pin and close are in Window options. Period help is in
  settings; the card names its current period. Healthy missing credits use a folded
  auxiliary row. Unknown, zero, stale, failure and reconnect states remain distinct.
- Existing provider snapshots feed the recorder. A repeated or older original timestamp
  cannot create a fresh point or update its first app-receipt timestamp. Refreshing does
  not add a point; stale, failed, missing or unknown values break continuity.
- Profiles, providers, connection generations, limit kinds/durations, units, reset windows
  and measurement bases stay separate. A reset, reconnect or basis change cannot connect
  to the preceding segment. A point with no confirmed reset boundary is drawn alone.
- The graph labels **used %**; the existing ring labels the remainder. Used and remaining
  amounts have separate selectable series and units. The expandable actual-value list
  preserves original observation and app-receipt times. There is no backfill or generated
  historical sample. One observation produces one dot.
- Local history contains quota metadata only. It excludes credentials, emails, account
  home paths, prompts, conversations and raw provider payloads. Versioned atomic files
  use the existing account-owned storage root, with bounded reads, validated backups and
  independent corruption handling. Quota/account/settings recovery does not depend on
  history recovery. Startup and removal also clean only exact observation-owned temporary
  file names, with bounded enumeration and directory/file link checks.
- Retention is at most seven days and 2,048 observations per logical limit/metric, with
  per-profile caps of 32 series, 16,384 total points and 8 MiB per history file. Expired
  points are removed on history activity/load. No new cleanup or polling timer is added.
- The immutable current-window view and chart geometry are reused until their inputs or
  layout change. Only the selected widget account creates a visible chart. Formatted
  observation rows are created only while expanded and released when collapsed. Account
  and metric options retain their item sources across quota-only updates; the compact
  widget skips the unused metric selector and its unused metric snapshots.

Implementation contracts are in [Architecture](ARCHITECTURE.md#observed-quota-history).
The default GC configuration, two-second passive loop, configured automatic refresh,
input limits, cancellation and widget visibility recovery remain in place.
Codex and Cursor reconnects await two flushed generation-marker copies with a bounded,
asynchronous two-second budget. If storage cannot acknowledge the reset, this session hides
the affected history and still permits current quota checks. Delayed invalidation continues
after a timeout. Complete write failure or process loss before the first marker reaches disk
cannot provide durable proof of that reconnect at the next start.

## Verification and production-view captures

All test data comes from synthetic providers/profiles and isolated roots. The UI harness
loads production WPF views with application startup overridden. It does not open a real
account store, provider cache, credential source or installed app.

The baseline capture and dedicated measurement harness were built from `684f20e` before
source edits. The UI-only change is committed separately as `ff2ed7a`; the verified feature
source and its tests are `31beeba`. The subsequent option-cache optimization and its
regression checks are `65007be`.

Focused cleanup verification passed 65 unit tests, a Release WPF build, 108 period/dropdown
renders, 180 credit-state cases, four native popup interaction scenarios, 771 zoom/DPI
checks and 127 settings checks plus refresh/save/cancel/timer application.

Feature checks use a synthetic clock for duplicate/reversed/stale/failed/zero observations,
window boundaries, retention limits, save failure and restart. The dedicated UI entry point
is `--observed-trends [output-directory]`; the ordinary full WPF gate also includes it.

```powershell
dotnet test tests/CycleArc.Tests/CycleArc.Tests.csproj -c Release --filter 'FullyQualifiedName~QuotaObservation'
dotnet build tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build -- --observed-trends artifacts/observed-trends/after/trends
pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch
```

The complete unit suite passed **2,064/2,064**, with no skips. The integrated gate then
completed on the final code with `-NoLaunch -Fast` in **3m49s**: the unchanged, already-passing unit suite was
reused, while the full production WPF, desktop IPC, installer/script guards, test-flavour
build/publish, single-file native SQLite and receiver checks, production publish, Velopack
packaging and isolated portable update/recovery checks all passed. Logs and TRX reports
remain under `artifacts/observed-trends/{full-gate.log,test-results-final}`.
The earlier pre-optimization gate also passed in 5m22s; its log is retained separately.
The final gate retained cached NU1900 vulnerability-metadata fetch warnings from the
restricted focused build. A subsequent normal-network `dotnet restore CycleArc.sln
--force-evaluate` passed without warnings (`final-restore-audit.log`); auditing stayed
enabled and no package or configuration change was made.

Full verification exposed test assumptions that needed integration with the new recorder
and graph. Existing manager fixtures now await recorder shutdown before deleting their
isolated roots. Widget compactness checks retain the old summary-body size limits and
measure the graph separately; the mixed-height scroll fixture retains a short first row
and a taller selected last row. Focused checks passed 67 fixture/interaction unit tests,
100 Cursor layouts, 151 layout cases, 771 zoom checks and 180 DPI cases. The final WPF gate
also passed 36 observed-trend combinations, including large text, exact-value access,
unit isolation, empty/zero/stale/reconnect states and stable geometry.

The additional option-cache checks cover source/item identity through quota and refresh
updates, independent selection, account rename/order/provider/membership changes, metric
availability/unit/language changes and profile/binding changes. All 33 related unit tests
passed. The four native popup scenarios retain their strict activation assertions. Their
focused sandbox invocation had no foreground window because its desktop differed from
the Windows input desktop; the same compiled checks passed on the normal desktop. No
production activation behavior or NuGet audit setting was changed.

The first PR Windows run at `583ed4a` exposed one additional test teardown race: the
Claude live integration fixture deleted its temporary root while a queued observation
write still held its temporary file. The run passed 2,063 tests and failed that one; it
did not reach WPF/package or disposable setup checks. The fixture now owns and awaits
its managers. An audit also made the history-restart fixture own every restarted manager
on assertion-failure paths and joined the flyout fixture's background reconciliation
before cleanup. These changes affect three test files only; they do not alter production
code or measured binaries. Focused follow-up checks passed nine Claude integration tests,
16 observation-manager tests and all four strict native popup scenarios. The original
CI failure is retained rather than retried unchanged.
After these fixes converged, the complete local unit suite passed again: **2,064/2,064**,
zero skipped, in **1m29s**, using the freshly built tests. The TRX is retained under
`artifacts/observed-trends/test-results-ci-fix`; the log is `ci-fix/full-unit.log`.
Unchanged production WPF, publishing, packaging and performance checks were not repeated.

The same two-account production fixture is retained in
[before](images/observed-trends-before-ko-dark.png) and
[after](images/observed-trends-after-ko-dark.png) captures. Its initial history is empty.
Separate fixed-clock synthetic views show [populated observations](images/observed-trends-populated-ko-dark.png),
[exact values and both timestamps](images/observed-trends-values-en-light.png),
[independent remaining USD](images/observed-trends-remaining-ko-dark.png) and the
[selected widget account](images/observed-trends-widget-ko-dark.png).

## Measurement scope

The isolated `CycleArc.IdleMeasure` harness retains the production manager, provider
projections, native tray and WPF views. The baseline and changed build use the same five
synthetic accounts and a one-minute automatic refresh cadence with default GC settings.
The product default cadence remains five minutes. The measurement does not launch or
control the user's installed CycleArc, read real provider/account data, force GC or trim
the working set. No build or test runs concurrently with a timed batch.

Each batch has three fresh processes, with 20 seconds of warmup and six 30-second phases:
initial tray, visible detail, tray after detail, visible widget, widget after refresh and
final tray. All use .NET 10.0.12 x64 workstation/concurrent GC with ConserveMemory 0. A
phase begins at its action boundary, so its CPU includes view creation, deferred work
and refresh work as well as the later settled interval. These are sequential batches
on one desktop, not randomized or isolated machine-wide benchmarks. Trial numbers pair
the same scenario; they do not control for background OS activity or process scheduling.

The preserved baseline batch measures assemblies from `684f20e`, even though the parent
measurement script ran with checkout HEAD `31beeba`. Assembly product versions and
SHA-256 fingerprints identify the measured code separately from that parent checkout.
The initial feature batch measures `31beeba`; the optimized batch measures `65007be`.
Each batch checks unchanged executable/runtime fingerprints and checkout HEAD before
and after. Only phase pairs with matching boundary counts for all five synthetic active
request counters enter the paired result. No request was sent to a provider service.

## CPU and memory results

All three optimized trials completed in **10m14s**. The baseline-to-optimized comparison
has **15/18 eligible phase pairs**: three trials for each of the first five phases. All
three final-tray pairs are excluded because active-request end counters differ. These
excluded results remain in the evidence and are not treated as an idle regression or
improvement. The initial-feature-to-optimized comparison has 17/18 eligible pairs.

The table shows medians across the three eligible trials. Each trial's CPU is its phase
CPU-time delta divided by wall time, expressed as percentage of one core. Memory is the
median of sampled values within each phase, in MiB. Working set and Private Bytes describe
different quantities, so they must not be added together. Each cell is **baseline → optimized**.

| Phase | CPU, one core % | Private working set, MiB | Private Bytes, MiB |
| --- | ---: | ---: | ---: |
| Initial tray | 0.729 → 0.365 | 22.266 → 22.906 | 28.371 → 29.160 |
| Detail visible | 7.552 → 6.146 | 80.934 → 85.219 | 138.379 → 150.793 |
| Tray after detail | 0.052 → 0.000 | 83.881 → 88.297 | 140.363 → 154.084 |
| Widget visible | 4.375 → 6.146 | 107.881 → 112.051 | 194.559 → 208.588 |
| Widget after refresh | 1.823 → 1.615 | 108.648 → 112.438 | 199.297 → 210.105 |

Paired changes below are **median [minimum, maximum]** of the three trial differences,
not subtraction of the two aggregate medians. CPU changes are percentage points.

| Phase | CPU change, pp | Private WS change, MiB | Private Bytes change, MiB |
| --- | ---: | ---: | ---: |
| Initial tray | −0.208 [−0.625, −0.104] | +0.656 [+0.609, +1.012] | +0.652 [−0.027, +1.379] |
| Detail visible | −1.406 [−1.719, +0.521] | +2.766 [+1.227, +7.053] | +11.852 [+11.383, +14.043] |
| Tray after detail | −0.052 [−0.156, +0.052] | +3.781 [+2.270, +6.549] | +13.721 [+13.457, +13.861] |
| Widget visible | +3.021 [−2.136, +3.177] | +4.451 [−1.441, +5.855] | +12.805 [+2.688, +14.029] |
| Widget after refresh | −0.208 [−0.208, +0.312] | +4.609 [−4.420, +6.094] | +10.809 [+4.762, +13.344] |

The feature still costs memory: median Private Bytes is approximately **11–14 MiB
higher after UI use**, with about **4 MiB more private resident memory** in these runs.
The widget-visible CPU result is also still higher than the old baseline. This is not
a claim of zero overhead or a generally faster app.

The first feature batch exposed repeated rebuilding of account and metric option lists.
`65007be` removes that unnecessary work and skips unused compact-widget picker snapshots.
Compared with that initial implementation, paired CPU differences have medians of
**−0.521 percentage points** for visible detail (range −1.406 to −0.312), **−2.604 points**
for visible widget (−3.177 to effectively zero), and **−0.521 points** after widget refresh
(−0.625 to −0.260). The visible-widget result improved in two trials and was effectively
unchanged in one. Source-identity regression checks verify the removed work independently
of these noisy, three-trial timings. Memory differences are mixed, and one optimized
widget trial has a slower UI p95; no universal memory or interaction-latency improvement
is established. Its widget queue p95 is 86.953 ms versus 21.080 ms in the corresponding
initial-feature trial.

Initial tray, tray after detail and post-refresh widget phases record zero accepted
observations, history writes, publications or recorder failures. During each detail/widget
visible phase, the existing refresh yields five accepted observations and five saved files;
manager `Changed` counts remain nine in both the baseline and optimized build. This does
not indicate a new idle save or notification loop. Whole-run request and timer counts vary
with dispatch/automatic-refresh timing and remain visible separately from eligible pairs.

Post-refresh widget last-GC committed memory is 52.699 MiB in all three optimized trials,
equal to baseline. Private Bytes minus that last-GC value rises by a paired median of
10.809 MiB [4.762, 13.344]. This is a residual against an earlier GC snapshot, not proof
that the difference belongs to WPF or a native allocator. Initial phases with no GC snapshot
retain null GC-heap/commit fields. No measured process used forced GC or working-set trimming.

Public, sanitized evidence contains per-trial values, eligibility reasons, distributions,
runtime/source identities, binary hashes, request/recorder counters and full memory/GC/UI
metrics: [baseline comparison CSV](measurements/observed-trends-2026-10-05.csv),
[baseline evidence JSON](measurements/observed-trends-2026-10-05-evidence.json),
[cache comparison CSV](measurements/observed-trends-cache-2026-10-05.csv) and
[cache evidence JSON](measurements/observed-trends-cache-2026-10-05-evidence.json).
The [baseline sampled spans](measurements/observed-trends-2026-10-05-spans.csv) and
[cache sampled spans](measurements/observed-trends-cache-2026-10-05-spans.csv) retain first/last
sample intervals without treating those partial spans as complete phase costs. Exports
exclude machine/user names, local paths and raw fixture/provider payloads. Original reports,
batch logs and the earlier comparison remain under `artifacts/observed-trends` locally.

As in [Idle-memory evidence](IDLE-MEMORY.md), this is a short synthetic lifecycle, not a
real-account compatibility, long-session leak or installed-update result. Installed-app
update/recovery/removal requires a disposable Windows environment; it is not run against
the working user profile.
