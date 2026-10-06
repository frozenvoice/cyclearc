# Isolated Windows idle-memory measurements

## Scope and identity

This experiment measures the current source's production providers and WPF views in
`tests/CycleArc.IdleMeasure`, using five synthetic accounts: two Codex, two Claude and one
Cursor. Each Claude account has 64 synthetic Desktop history observations. External
adapters are fake, while production protocol parsing, collectors, account binding checks,
cache writes, projections and refresh guards remain in the path. Each synthetic request
uses the same bounded delay across configurations. Reports retain request counts;
comparisons with unequal active work are excluded from paired aggregates.

This is a source-based, self-contained Release WPF harness. It does not execute ordinary
installed-app startup, update/startup IPC or account discovery. It uses production tray,
environment-monitor and window objects with fixture event wiring. Its startup and
ready timings are harness timings. Its process includes fixture and measurement overhead.
It cannot establish the installed app's absolute footprint, real-account latency, cold-disk
startup, native heap ownership or long-session leak behavior.

Record these identities separately:

| Identity | Evidence |
| --- | --- |
| Current checkout | Repository remote, complete HEAD SHA and preserved status/diff |
| Source assemblies in the harness | Assembly informational version/SHA, file/product versions and SHA-256 |
| Measured local Release process | Dedicated `CycleArc.IdleMeasure.exe` identity, companion/runtime hashes and actual loaded CLR version |
| Installed executable | Read-only resolved installed path, file/product version and SHA-256; runtime identity only if verified independently |

Do not attribute an older installed 0.9.0 measurement to source 0.10.x. An installed
executable's SHA-256 is a binary hash, not evidence of its source commit. The SDK version,
installed shared-runtime list and actual runtime loaded by a self-contained process are
different facts. A binary lacking reliable source-SHA evidence stays labelled unknown.

The runner accepts only the dedicated harness executable. It creates a new output and
fixture directory per invocation/trial, changes only child-process GC environment variables,
and preserves reports, settings and logs. It never resolves or targets an installed app,
reads user accounts/tokens/history, attaches a dump/tracer to another process, or controls
an existing app. Failure cleanup can stop only the exact child PID it launched.

## Reproduce

Use Windows x64, PowerShell 7 and the SDK selected by `global.json`. Build/publish before
timing. Keep the artifact fixed throughout a batch; avoid builds, tests and other deliberate
machine load while measuring. Preserve the same display/DPI/theme, power mode and machine
conditions for each trial. File-system caches are not flushed: these are repeated launch
measurements, not a claim of cold startup.

From the repository root, publish the dedicated harness separately from installation:

```powershell
dotnet publish tests/CycleArc.IdleMeasure/CycleArc.IdleMeasure.csproj -c Release -r win-x64 --self-contained true -o artifacts/idle-measure -p:PublishSingleFile=false -p:UseSharedCompilation=false -nr:false
pwsh -NoProfile -File scripts/Measure-Idle.ps1 -Executable artifacts/idle-measure/CycleArc.IdleMeasure.exe -Pilot
pwsh -NoProfile -File scripts/Measure-Idle.ps1 -Executable artifacts/idle-measure/CycleArc.IdleMeasure.exe
pwsh -NoProfile -File scripts/Summarize-Idle.ps1 -RunDirectory <completed-run-directory>
```

The pilot uses one trial, ten seconds per phase and five seconds of warmup to verify the
fixture and report path. It does not qualify a tuning change for adoption. The measured
defaults are three trials, thirty seconds per phase and twenty seconds of warmup; changing
these parameters must be reported. The runner does not build, install or automatically
retry. Every output directory must be new. Raw local files under `artifacts/` are ignored;
checked-in summaries must contain synthetic measurements only.

Each trial records initialization, warmup, stable tray-equivalent idle, visible flyout,
visible widget, synthetic refresh/post-refresh and returned idle conditions. Exact phase
labels, durations, sample counts and transitions are retained in the JSON report. The
two-second passive refresh loop continues; synthetic fresh source observations still pass
through the real reader/parser. Manual/configured refresh reaches the fake external
adapters through production provider code. Counter evidence must show the same request
work across GC variants; skipping refresh or omitting a provider is not a valid comparison.
Latest quota/source-time, account isolation and widget enabled/visible assertions must
pass. Existing separate unit/WPF regressions retain input-cap and cancellation coverage.

After warmup, the harness now measures eight phases: `tray-idle`, `widget-before-flyout`
(widget shown before the popup has ever been created), `flyout-visible`,
`tray-after-flyout`, `widget-visible`, `post-refresh-widget`, `tray-after-refresh` and
`widget-after-churn` (after five popup open/close cycles and ten account switches that
return to the first selection). The 2026-10-05 GC batch below used the earlier six
phases without `widget-before-flyout` and `widget-after-churn`. Transitions record the
synchronous action and time until `ApplicationIdle`, plus how many account rows, avatars,
popup detail rows and tray icons were newly displayed, compared by reference after each
fixture call into production presentation. Outside each sampled phase window the harness
also records process and UI-thread CPU cycles (`QueryProcessCycleTime` /
`QueryThreadCycleTime`, not quantized to the 15.6 ms tick), `RefreshSnapshot` calls,
kernel handles by object type and the busiest threads' CPU. The real tray stays
registered, the flyout is pinned while shown, and the widget is enabled only for its
widget phases. Account/settings controls that could start external
login or configuration flows are disconnected in the fixture. The one-minute automatic
refresh and display timers and two-second passive/visibility timer remain active.
The fixture deliberately selects the supported **one-minute** automatic refresh interval
to exercise that path. Production defaults to **five minutes**. Here, `default` means
GC defaults, not the production default refresh interval.
The explicit synthetic refresh runs between the two widget phases; a separate phase
label prevents its samples from entering an idle median. Assertions join overlapping
refreshes after taking the phase endpoints, without cancelling or suppressing requests.

Process samples run every second; dispatcher probes use an absolute 500 ms schedule.
Buffers are allocated before warmup. Native region scans and JSON serialization are
outside steady phase endpoints; the final report is serialized after sampling stops.
The summarizer also filters samples/probes by exact endpoint times, aggregates within
each trial before comparing trials, and leaves absent GC snapshots as null. Startup
snapshots and refresh duration remain separate. Short pilot results never enter the
three-trial comparison.
Window show/hide transitions also record the synchronous action and time until a WPF
`ApplicationIdle` callback, after higher-priority layout/render work; this milestone
does not prove that the compositor has presented pixels on the physical display.

The parent writes `run.json`, trial `parent.json`, child `report.json`, stdout/stderr and
ready/progress markers. Fingerprints are verified before/after every child and for the
batch. Source HEAD must remain fixed during the batch. The parent observes ready markers
on a polling interval; this timing includes observation latency and is distinct from the
child's internal initialization time.
Paired aggregates require matching Codex process/quota, Claude live and Cursor usage/HTTP
counts at both phase boundaries; whole-run comparisons require matching final counts.
Every raw observation/delta remains available. Passive/history-read counts are reported;
this eligibility check does not establish identical timer scheduling.

## GC variants and adoption rule

| Variant | Only child override |
| --- | --- |
| `default` | GC tuning environment overrides removed; published defaults retained |
| `conserve5` | `DOTNET_GCConserveMemory=5` |
| `conserve7` | `DOTNET_GCConserveMemory=7` |
| `concurrent-off` | `DOTNET_gcConcurrent=0` |

Trials use the supplied order, its reverse, then a rotation. Match every setting to the
default in the same trial block. Change one setting at a time; do not combine conserve
memory with background-GC disablement. The runner records removed inherited key names,
requested settings and effective GC configuration. It does not alter global environment
variables or published runtime configuration.

Conserve memory accepts 0–9; nonzero values increase memory conservation and can trigger
LOH compaction, at the cost of more collections or longer pauses. GC configuration is read
at startup. Numeric runtime JSON is decimal; environment values are hexadecimal (single
digits 5/7 are identical). Background GC normally remains enabled. Server-GC DATAS uses
implicit conserve 5 when unset; record the actual flavor rather than assuming unset equals
explicit zero for every runtime. [Microsoft GC configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector)
and [configuration precedence](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/)
describe these behaviors.

Predeclared adoption threshold: compared with default, a candidate must reduce the median
Private Bytes by **both at least 5 MiB and at least 10% in all three trial pairs** in the
steady and post-refresh idle conditions. A Working Set decrease alone does not qualify.
Check all visible/refresh states for regressions. CPU, cumulative GC pause, UI p95/max,
timeouts and behavior must show no repeatable regression. No improvement claim follows
from one favorable trial or the pilot. If differences are inconsistent, too small, or
accompanied by CPU/pause/response degradation, keep production defaults. Background-GC
disablement is an experiment, not a presumed default fix. Any adopted setting belongs in
a separate minimal commit after evidence and regression checks.

## Metric definitions and limits

| Report metric | Meaning and limit |
| --- | --- |
| Working Set | Currently resident pageable process memory; includes shareable pages and can change through OS trimming |
| Private Working Set | Resident process-private pages, measured with `PROCESS_MEMORY_COUNTERS_EX2`; unavailable on an EX fallback is `null` with a reason |
| Private Bytes | Process-private commit (`PrivateUsage`), including pageable/nonresident private memory; differs from physical residency |
| ManagedAllocatedBytes | `GC.GetTotalMemory(false)`: approximate currently allocated managed bytes excluding fragmentation; unreachable objects may await collection |
| TotalAllocatedBytesApprox | Cumulative `GC.GetTotalAllocatedBytes(false)`; phase differences include instrumentation allocations and exclude native allocations |
| Gen0/1/2 collections | Cumulative `GC.CollectionCount`; higher-generation collections also collect younger generations, so counts overlap |
| Total GC pause | Cumulative `GC.GetTotalPauseDuration`; phase differences measure total suspended time, not complete individual-pause distributions |
| Last GC heap/fragmentation/commit | `GCMemoryInfo` snapshots from the last collection; fields can remain stale while new objects are allocated |
| CPU total | Cumulative user + kernel process CPU milliseconds; phase CPU/wall time gives one-core utilization; divide by logical CPUs only for machine-normalized percentage |
| Handles/GDI/USER | Process kernel-handle and GUI-object counts; a valid zero GUI count differs from a failed query |
| Thread count | Boundary `Process.Threads` snapshot with its timestamp; intentionally not refreshed on every tick because it allocates wrappers/queries OS metadata |
| UI probe | Dispatcher queue delay and timer scheduling lateness; responsiveness proxy, not measured click-to-pixel latency |

`GCMemoryInfo.Index == 0` means no matching GC occurred, so last-GC heap/commit and residual
fields remain null rather than claiming a zero heap. `GetTotalMemory(true)`, forced
`GC.Collect`, `EmptyWorkingSet`, working-set limits and periodic memory-emptying are excluded.
Allocation polling uses approximate mode; precise mode has a documented performance cost.
The last snapshot's `PauseDurations` cannot reconstruct pauses missed between samples.
See [GCMemoryInfo](https://learn.microsoft.com/en-us/dotnet/api/system.gcmemoryinfo?view=net-10.0),
[GetTotalMemory](https://learn.microsoft.com/en-us/dotnet/api/system.gc.gettotalmemory?view=net-10.0),
[GetTotalAllocatedBytes](https://learn.microsoft.com/en-us/dotnet/api/system.gc.gettotalallocatedbytes?view=net-10.0)
and [GetTotalPauseDuration](https://learn.microsoft.com/en-us/dotnet/api/system.gc.gettotalpauseduration?view=net-10.0).

The native sampler uses `K32GetProcessMemoryInfo` EX2, falling back to EX without inventing
Private Working Set. EX2 requires Windows 10/11 22H2 with the September 2023 update or newer.
Native CPU/resource queries target only the current measurement process. See
[EX2 definitions](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex2),
[GetProcessMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getprocessmemoryinfo),
[Windows Working Set](https://learn.microsoft.com/en-us/windows/win32/memory/working-set)
and [GetGuiResources](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguiresources).

An opt-in phase-boundary `VirtualQuery` inventory separately reports committed and reserved
PRIVATE, MAPPED and IMAGE address ranges. Reserved bytes consume virtual address space,
not committed RAM. Committed image/mapped ranges may be shared and are not a process's
unique physical footprint. PRIVATE-type committed bytes are not identical to Private
Bytes: copy-on-write image/mapping commit can contribute to private commit while retaining
its original mapping type. The scan can race normal allocations/GC and is excluded from
the frequent sampler. It establishes allocation-category evidence, not native heap
attribution. [VirtualQuery](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualquery)
documents these mapping distinctions.

`PrivateBytes - last-GC TotalCommittedBytes` is explicitly labelled a **non-GC private-commit
residual estimate**. It includes native heaps, thread stacks, JIT/runtime bookkeeping and
other private allocations, with stale GC-snapshot uncertainty; it is not native live-heap
size. Preserve the raw value and GC index instead of clamping or treating subtraction as
proof of a native leak.

The sampler's own allocations are part of every variant. A focused own-console probe on
Windows 10.0.26300/.NET 10.0.12 observed about 288 bytes per warmed capture and 0.0045–0.0057
ms per capture over 1,000 calls. Its dedicated 2 MiB `VirtualAlloc` was reflected exactly
in the committed PRIVATE inventory; naturally triggered GC produced valid counts/pause
and last-GC snapshots without `GC.Collect`. These numbers validate the instrument and
are **not CycleArc footprint measurements**. JSON, fixture, UI probes and boundary inventory
add separate overhead. Identical overhead improves comparisons but does not make the
harness equivalent to ordinary installed execution.

The WPF Dispatcher executes queued work according to priority and cannot interrupt a
running UI operation. Timing starts from the intended probe deadline as well as enqueue
time so a delayed background callback does not silently omit a preceding pause. Rendering
or action-completion timing is reported separately where measured. See
[WPF threading model](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model).
Direct APIs avoid an EventPipe installation/dependency; optional diagnostic traces would
add overhead and would not attribute Windows native allocations. See
[runtime metrics/API mapping](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime)
and [EventPipe scope](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventpipe).

## Results and decision

**Keep production GC defaults.** ConserveMemory=5 reduced memory after displaying WPF
views, but did not meet the predeclared threshold across states and repeats. Initial
tray idle did not improve materially, one post-refresh pair fell below 10%, and total
GC pauses increased. CPU and response observations were mixed. No production source
or GC setting was changed; background GC remains enabled.

The formal batch ran on 2026-10-05, 00:17:08.586–00:57:47.936 UTC (40m39.35s), on
Windows 10.0.26300 x64 with 16 logical CPUs. All 12 processes completed: four GC
configurations, three trials each, 20-second warmup and six 30-second phases. The
self-contained artifact and its before/after fingerprints stayed fixed. No deliberate
builds or tests overlapped timing. This is one desktop session, not a controlled
multi-machine experiment. Short pilots are excluded.

The checked-in [84 phase rows](measurements/idle-memory-2026-10-05.csv) contain trial
medians, CPU/allocation/GC deltas, UI quantiles, sample counts and request boundaries.
The [run/native evidence](measurements/idle-memory-2026-10-05-evidence.json) contains
startup/final snapshots, effective runtime settings, transitions, counters, hashes and
84 native inventories, without local paths or PIDs. Raw local samples/logs remain under
`artifacts/idle-memory/full-b75a289`; the final analysis is under
`artifacts/idle-memory/full-b75a289-summary-v2`.

### Identity

The starting checkout was clean at `b318ca6c376e1b2ec2e8ffb3a9c81d3bbf9e9ab8`, matching
`origin/main` in `frozenvoice/cyclearc`. The measured executable/source commit is
`b75a289f9da7562d52504577b5651dad4806553d`. The analysis-only correction is
`57379151a9cbb30fe8cc0d31810c338dfb434f17`; it does not change the measured executable.

| Artifact | Version/source evidence | Runtime evidence |
| --- | --- | --- |
| Installed `current/CycleArc.exe`, inspected read-only | 0.6.1 + `7adacd231406dd8c8e0d47255e0b098374ba39ea` informational version | Embedded runtime configuration names .NET / WindowsDesktop 8.0.30; loaded CLR not independently attested |
| Existing local Release files | 0.10.0 + `f908dd8`, stale relative to starting HEAD | Existing self-contained files name 10.0.12; excluded from measurement |
| Fresh production/harness assemblies | 0.10.0 + `b75a289f9da7562d52504577b5651dad4806553d` | Actual child CLR .NET 10.0.12, x64 workstation GC in all 12 runs |

| File | SHA-256 |
| --- | --- |
| Installed `current/CycleArc.exe` | `639C9E707C882650F4F1C3B1DBF2F6C47DE0DD8718245908C33B1D5E1CB9FE1B` |
| Measured `CycleArc.IdleMeasure.exe` | `589A1B6C1E833B3943ACD885A7CB0FD808D68E3B077A4B6C4946DEA545461F52` |
| Measured production `CycleArc.dll` | `D1D802575528BFEF14982B2C3E2781A0BF7D947200C692CCC54B93F325B3B5DC` |
| Measured `coreclr.dll` | `128AEE8C62A673D64739E585E3876B61133571CEC83A46240A62581D5465639B` |

Effective ConserveMem was 0 / 5 / 7 / 0, and ConcurrentGC true / true / true / false.
Concurrent off also reported Batch latency mode and a 128 MiB gen-0 maximum budget;
the others reported Interactive and 48 MiB. These are observed consequences of the
single override, not additional overrides. Build SDK was 10.0.401. Installed-app memory
was not measured, and the running app, accounts, credentials and settings were untouched.

### Memory

Values are MiB, medians of three per-trial phase medians. Individual seconds are not
independent experimental repeats. Tables retain all observations, including the final
phase's unequal-work observation qualified below.

| Default GC phase | Working Set | Private WS | Private Bytes (trial range) | Managed allocated | Last-GC commit |
| --- | ---: | ---: | ---: | ---: | ---: |
| Initial tray idle | 91.99 | 22.23 | 27.97 (27.80–28.23) | 8.72 | unavailable |
| Flyout visible | 194.01 | 80.94 | 131.00 (130.01–131.26) | 28.07 | unavailable |
| Tray after flyout | 195.93 | 82.77 | 134.61 (131.02–135.15) | 32.44 | unavailable |
| Widget visible | 225.83 | 105.93 | 164.22 (164.08–165.52) | 14.68 | 52.70 |
| Widget after refresh | 225.87 | 104.39 | 173.61 (171.79–173.73) | 37.17 | 52.70 |
| Tray after refresh | 225.62 | 104.35 | 172.14 (169.71–172.33) | 43.53 | 52.70 |

Under default GC, no natural GC occurred in the initial three measured phases. Managed bytes include
uncollected objects, not just retained live objects. Views, resources, runtime state
and fixture overhead remain after hiding windows; these short observations cannot
distinguish intentional retention from long-term leaks.

| Private Bytes phase | Default | Conserve 5 | Conserve 7 | Concurrent off |
| --- | ---: | ---: | ---: | ---: |
| Initial tray idle | 27.97 | 27.99 | 28.18 | 27.99 |
| Flyout visible | 131.00 | 129.36 | 131.70 | 129.37 |
| Tray after flyout | 134.61 | 131.31 | 128.47 | 131.68 |
| Widget visible | 164.22 | 145.30 | 149.24 | 164.48 |
| Widget after refresh | 173.61 | 150.02 | 159.45 | 172.49 |
| Tray after refresh | 172.14 | 150.78 | 163.90 | 171.48 |

Every initial-idle paired change is below 0.6 MiB. Conserve 5's three eligible
post-refresh-widget pairs save **25.73 / 21.76 / 16.65 MiB** (**14.82% / 12.67% /
9.58%**). Its first two last-GC commits fall from 52.70 to about 29.1 MiB, supporting
a GC-commit contribution. This is an observed benefit; the third pair misses 10%.
Its two eligible final-tray pairs save 26.61 / 18.93 MiB (15.44% / 11.15%).
Conserve 7's post-refresh changes are -24.59 / +2.18 / -14.28 MiB; concurrent off's
are -1.12 / +0.99 / -1.39 MiB. Neither has repeatable savings in all trials.

### CPU, allocation, pauses and response

One-core CPU percentages are median phase CPU/wall-time percentages; divide by 16 for
machine normalization. CPU accounting is quantized to 15.625 ms, so a zero measured
delta does not prove zero activity.

| Phase | Default | Conserve 5 | Conserve 7 | Concurrent off |
| --- | ---: | ---: | ---: | ---: |
| Initial tray idle | 0.417% | 0.469% | 0.417% | 0.417% |
| Flyout visible | 7.396% | 8.594% | 8.021% | 7.500% |
| Tray after flyout | 0.208% | 0.000% | 0.052% | 0.104% |
| Widget visible | 8.125% | 6.875% | 6.667% | 7.552% |
| Widget after refresh | 1.719% | 1.719% | 1.562% | 1.615% |
| Tray after refresh | 0.677% | 0.573% | 0.885% | 1.042% |

Initial idle allocates about 2.44–2.50 MiB per 30 seconds across settings. Entry-to-final
CPU medians are 7,625 / 7,891 / 7,766 / 7,516 ms and allocation medians 99.22 / 92.76 /
98.36 / 100.89 MiB. These whole-run observations include less active work in default
trial 3 and cannot establish a CPU or allocation improvement.

| Configuration | Total GC pause, trial 1 / 2 / 3 (ms) | Final gen0/gen1/gen2 counts, trial 1; 2; 3 |
| --- | --- | --- |
| Default | 32.448 / 31.068 / 13.775 | 2/1/0; 2/1/0; 1/0/0 |
| Conserve 5 | 34.471 / 35.934 / 31.209 | 3/2/1; 3/2/1; 2/1/1 |
| Conserve 7 | 36.715 / 28.150 / 36.372 | 3/2/1; 2/1/0; 3/2/1 |
| Concurrent off | 46.091 / 32.284 / 29.796 | 2/1/0; 2/1/0; 2/1/0 |

Using only the two eligible whole-run pairs, Conserve 5 increases total pause by
2.023 / 4.866 ms and concurrent off by 13.643 / 1.216 ms. Conserve 5 performs a gen-2
collection in each trial; default does not. These are cumulative pauses, not individual
maximum pauses.

| Response observation (ms) | Default | Conserve 5 | Conserve 7 | Concurrent off |
| --- | ---: | ---: | ---: | ---: |
| Entry to ready, median | 623 | 554 | 783 | 711 |
| Explicit synthetic refresh, median | 192 | 217 | 194 | 194 |
| Initial idle probe due-lateness p95, median | 0.822 | 0.807 | 2.419 | 9.552 |
| Flyout due-lateness p95, median | 3.935 | 8.917 | 11.443 | 8.549 |
| Widget due-lateness p95, median | 31.397 | 1.286 | 2.673 | 25.242 |
| Post-refresh widget due-lateness p95, median | 25.760 | 2.890 | 14.964 | 20.588 |
| Largest steady-phase due lateness | 321.418 | 104.717 | 153.821 | 174.174 |

Conserve 5 has better widget-phase scheduling observations, without consistent improvement
in initial/flyout states. Initial-tray latency differences occur without GC, cautioning
against attributing every latency difference to GC policy. Widget show-to-Dispatcher-idle ranges are
228–4,415 / 106–143 / 100–175 / 105–724 ms; hide ranges are 244–3,183 / 14–39 /
29–2,131 / 73–563 ms. Outliers remain; host/compositor effects are not isolated.
These proxies do not establish click-to-pixel timing or regression-free user experience.
Full due/queue quantiles and transitions are preserved in the evidence.

### Native memory and resources

All 84 VirtualQuery inventories completed. All 2,425 periodic samples and 204
entry/ready/final/phase-boundary samples obtained native metrics and Private WS without
fallback/failure. Default initial-tray boundary PRIVATE-type commit was 19.28–20.24 MiB,
IMAGE commit 150.35 MiB and MAPPED commit 270.62–270.67 MiB. Post-refresh-widget PRIVATE
commit was 142.43–144.30 MiB, IMAGE 335.07 MiB and MAPPED 317.40–317.76 MiB.
These categories are address mappings, not native heap ownership or unique resident RAM.

Default post-refresh-widget non-GC private-commit residual estimates were
121.80 / 120.02 / 121.96 MiB, based on boundary Private Bytes minus last-GC commit.
Initial estimates remain null because no GC had occurred. These boundary snapshots
must not be substituted for periodic phase medians. They cannot identify a native leak.

Across settings, initial-tray median handle counts were about 537–538, GDI 16 and USER 17;
flyout about 802–805 / 37 / 35; widget about 793–796 / 45 / 37; final tray about
792–795 / 45 / 36–37. GUI resources remain after hiding views. Thread counts are dated
boundary snapshots, not refreshed each second. No long-run resource trend was measured.

### Request equivalence and validation

All 12 runs passed five-account isolation, latest passive values/original timestamps,
provider refresh, window visibility and active passive-timer assertions. Each explicit
refresh made two Codex quota requests, two Claude live requests, one Cursor usage request
and three Cursor HTTP requests. No provider path was removed to lower measured cost.

Eleven runs ended with three automatic ticks and Codex/Claude/Cursor usage/HTTP totals
10 / 10 / 5 / 15. Default trial 3 ended with two automatic ticks and 8 / 8 / 4 / 12.
The finite observation ends around a timer boundary; its exact timing cause was not
traced, so it is not attributed to a GC setting. The summarizer excludes trial 3's
final-tray pair for each variant and their whole-run comparisons: **60 of 63 phase
pairs** (including warmup) and **six of nine whole-run pairs** remain eligible. Other
phases retain three pairs. No run was discarded/retried to obtain equal counts.
Passive ticks ranged 86–100 and history reads 180–214; this scheduling variation also
limits causal CPU/allocation claims.

The integrated local `dev-run.ps1 -NoLaunch` gate passed once on executable commit
`b75a289` in 7m03.4s: Release build, 2,008/2,008 unit tests without skips, full WPF,
desktop-instance and installer-script fixtures, test-flavour publish, native SQLite
synthetic probe, built/published receiver, self-contained single-file publish, Velopack
packaging and portable rollback/recovery component checks. Logs/previews are under
`artifacts/idle-memory`. The analysis-only change passed parser and saved-report checks
for exact exclusions/counts, eligible-only aggregates, CSV request boundaries and
preservation of 42 phases without GC snapshots.

Ordinary installed startup, real network/account latency, physical display presentation,
long-session retention and native heap ownership remain unmeasured. Destructive installed
update testing requires a disposable Windows user/VM and was not run on this working
profile. Passive refresh, cancellation/input limits, visibility recovery, installer/update
flow and production GC settings are unchanged. No tuning commit was adopted.

## UI reuse change, 2026-10-06

Code change, not a GC setting. Starting checkout `4159435dc4075b5a0d156ec1e4e00613470908b6`
(clean `main`). Measured "before" production code is unchanged from it (artifact
`0.10.0+51341f5`, the harness-only commit); "after" is `0.10.0+2e04f57`.

What changed and why:

| Cost found | Evidence (scratch allocation probe, 5 accounts, same-data rebind) | Change |
| --- | --- | --- |
| A hidden popup was fully rebound on every snapshot change | `BindAccounts` ~1.6 MiB and ~27-31 ms UI thread per call, hidden or shown | Bound once just before `Show()`; while hidden, rebound at once only when it would keep a removed account, another login's email or newly identity-protected quota |
| Account rows were cleared and recreated on every bind | ~200 KiB per row; 5 rows ~1 MiB | Rows kept per account ID, re-rendered only when their displayed output changes; moved on reorder, dropped on removal |
| Selected and widget avatars recreated each bind | widget update 378 to 284 KiB | Kept while initials, color, size and tooltip are equal |
| Tray icon redrawn on every snapshot | ~1.0 to 0.26 ms per update | Redrawn only when a drawing input (style, size, digits, sweep, band color, exactness, taskbar theme) changes; tooltip always updated |
| Claude Desktop history read copied through stream buffer, chunk, `MemoryStream` and `ToArray` | 61 to 29 KiB per read (13 KiB file, 2 accounts every 2 s) | One file-sized buffer, unbuffered stream, parse the read span; same byte limit and failure meanings |
| Finished spinner clocks were only detached (`BeginAnimation(p, null)`) | ~120 KiB/s UI-thread allocation and CPU after a refresh with the widget shown, until a GC collected the clock | Clock kept, then stopped and removed when motion ends; found because fewer GCs made it last longer |

Formal batch: Windows 10.0.26300 x64, 16 logical CPUs, .NET 10.0.12 workstation GC
defaults (ConserveMem 0, concurrent on) in every child, identical `coreclr.dll`,
English, Dark, harness 100% zoom on the existing desktop DPI. Six single-trial
`Measure-Idle.ps1 -Configurations default` runs ordered before, after, after, before,
before, after (11:14-11:41 UTC); pairs are (1,2), (4,3), (5,6). 20 s warmup and eight
30 s phases each. No build/test/publish overlapped. An earlier batch on `78695ff`
exposed the spinner-clock regression in `widget-visible` and is superseded.

| Artifact | SHA-256 |
| --- | --- |
| before `CycleArc.dll` | `DB036F8651E8D39C25AEC425BCDA37CB21177D0C8DEAEE6A844FB749B163FDFD` |
| before `CycleArc.Core.dll` | `912C1F55BCC1318E8911A62F57C71E3FB06B66ACF7BEE18ADD6241AE496CD71F` |
| after `CycleArc.dll` | `D4239F9B0BD470C3710C0728E449183F1A786024F0E5F534E3EA12F1D722D48E` |
| after `CycleArc.Core.dll` | `80F8829F949E799138334191405DD0F7C4E33D31C76A498DE14029069CF619FE` |
| `coreclr.dll` (both) | `128AEE8C62A673D64739E585E3876B61133571CEC83A46240A62581D5465639B` |

Every run made the same external requests (Codex/Claude/Cursor usage/Cursor HTTP
10/10/5/15), automatic (3) and display (4) ticks, and passed the latest-value,
isolation, visibility and churn-return assertions. "After" completed 133 passive ticks
versus 127-129, so it did slightly more local work, not less.

Phase medians, per pair in MiB (before to after, or the pair difference):

| Phase | Private Bytes | Private WS | Alloc KiB/s |
| --- | --- | --- | --- |
| tray-idle | 28.72 to 27.45, 28.45 to 30.55, 28.52 to 27.52 | 22.82 to 21.56, 22.58 to 24.38, 22.58 to 21.62 | 83 to 51 (all) |
| widget-before-flyout | -6.45, +2.60, -3.94 | -8.30, -1.52, -8.20 | 296-302 to 166-172 |
| flyout-visible | -8.05, +0.05, -9.24 | -9.86, -4.43, -12.24 | 200-218 to 68-105 |
| tray-after-flyout | 189.18 to 152.20, 175.07 to 149.55, 188.70 to 151.85 | -29.26, -17.73, -30.36 | 628-633 to 119-120 |
| widget-visible | -29.38, -20.62, -28.71 | -23.16, -10.02, -24.77 | 101-103 to 69 |
| post-refresh-widget | -27.50, -15.28, -29.09 | -12.56, -0.06, -20.12 | 319-327 to 88-89 |
| tray-after-refresh | -27.32, -16.46, -27.68 | -8.78, -1.57, -16.48 | 657-662 to 118-121 |
| widget-after-churn | 251.37 to 202.28, 236.62 to 198.73, 237.14 to 204.12 | -10.92, -2.46, -15.13 | 167-168 to 80-81 |

Whole run from ready to final: allocated 134.75-135.54 to 54.50-55.42 MiB; CPU
7,219 / 7,859 / 7,094 to 6,859 / 7,453 / 6,813 ms; cumulative GC pause 26.81 / 23.96 /
24.26 to 11.65 / 12.56 / 12.78 ms; gen0/gen1 collections 2/1 to 1/0. Explicit refresh
184-188 to 165-169 ms. Account switch until `ApplicationIdle` (median of five per run):
popup hidden 53.0 / 25.3 / 24.8 to 12.0 / 11.2 / 11.9 ms, popup shown 49.7 / 26.2 / 25.6
to 22.6 / 24.2 / 22.5 ms; popup reopen 13.2 / 14.3 / 12.8 to 13.5 / 13.3 / 13.6 ms. Each
churn reopen or switch used to create 5 rows and 11 avatars and redraw the tray; now no
rows, and avatars only for a changed selected account. GDI medians were equal or lower
(49 to 45 in `tray-after-flyout`) and USER 35-39 against 36-38. Kernel handle medians were
about 10-20 higher in several phases after a window was shown (for example 708-729 to
725-731 in `widget-visible`), with no growth across phases and 700-729 to 704-710 after
churn; the cause, possibly later finalization with fewer collections, was not traced.
Pair tables with CPU, GC, UI quantiles and transition maxima:
[phase pairs](measurements/ui-reuse-2026-10-06-phases.csv) and
[transition pairs](measurements/ui-reuse-2026-10-06-transitions.csv).

Classification: **A** (resident reduction) for states after any window has been shown:
Private Bytes and Private WS are lower in all three pairs from `tray-after-flyout`
onward, and Private WS also in `widget-before-flyout` and `flyout-visible`. Initial
`tray-idle`/warmup memory is inconclusive (pair 2 was 2-3 MiB higher from its first
sample); only its allocation rate fell (B). Not explained: `widget-visible` CPU
62 to 94-125 ms per 30 s (at most 0.21% of one core) with equal passive work in all three
pairs. `flyout-visible` CPU was +0.36 to +1.09% of one core, but before skipped 1-3 of
its 15 passive ticks there, so that pair is not equal-work. Entry-to-ready had one
1,471 ms "after" outlier (others 543-743 ms against 536-587 ms) in startup code this
change does not touch.

Visual check: 501 production previews (accounts, mixed providers, Cursor, tray icons,
widget accounts, usage credits, ring bands, tooltips, reopened popup; EN/KO, Dark/Light,
80/100/150%) were rendered from before and after sources one mode after the other: 499
were pixel-identical, and two Korean account-window previews differed only in clock
digits (20:11 against 20:12) because fixture times follow the wall clock. No preview or
README image was updated.

Limits: one desktop session; synthetic harness including fixture and instrumentation;
no installed-app, real-account, physical-display or long-session measurement. The
user-observed 80 MB was not attributed to a metric or build and is not compared here.

## Residual performance questions, 2026-10-06

Follow-up to the UI reuse change. Start `50484a0847216e874a9515c0b399c600f2cd0415`
("optimized"); measured fix `e83c6e2` ("fixed"). Same machine, runtime, default GC and
harness as above; the harness gained the boundary diagnostics described earlier.

Questions and findings:

| Question | Finding | Action |
| --- | --- | --- |
| `widget-visible` CPU 0.21% to 0.31-0.42% of one core | With cycle counts, original/optimized UI-thread work in that phase was 52-93 / 51-61 M cycles and process totals 557-759 / 755-842 M cycles with equal passive work and no `RefreshSnapshot`: the difference was off the UI thread and within run-to-run spread. In the final batch fixed measured 0.26% in all three runs versus 0.42 / 0.52 / 1.93% | No CPU-specific change. A widget hidden mid-refresh kept its activity rotation clock ticking because a hidden widget is not rebound; the rotation now also requires `IsVisible` |
| Popup reopen maximum | A 50-cycle probe split reopen into bind/show/layout. After an unchanged or quota-only hidden period, optimized reopened faster than original (6.0 / 8.5-8.9 ms against 12.3-13.5 ms). After the **selected account** changed while hidden, laying out the new detail section at `Show()` made it slower (median 14 to 16 ms, p90 22-26 to 28 ms) | A selection change while hidden queues one coalesced bind at `ApplicationIdle`, after visible windows render. Quota changes still wait for the show; the pre-show bind stays |
| Kernel handles 10-20 higher | Grouped by type, the extra handles were Thread +5, Key +5 and Event ~+10 (temporary thread-pool threads after an automatic refresh); they matched again after churn. 50 popup open/hide and switch cycles: 727/729 to 722/718 (optimized), 728/728 to 723/721 (fixed); forced collection in a separate probe changed nothing | Not a leak; no change |

Final batch: six single-trial default-GC runs ordered optimized, fixed, fixed, optimized,
optimized, fixed (21:53-22:20 local); pairs (1,2), (4,3), (5,6). Every run made Codex/
Claude/Cursor usage/Cursor HTTP requests 10/10/5/15, 133 passive ticks, 280 history reads
and the same `RefreshSnapshot` counts per phase (12 / 12 / 0 / 1 / 11 / 1). No popup bind
happened before churn in either build.

| Artifact | SHA-256 |
| --- | --- |
| optimized `CycleArc.dll` | `1B95E8B67546D76773F9CF6032C72CCCF0F81457ECE3D3A0C6FD05E022F26BD2` |
| optimized `CycleArc.Core.dll` | `7F7BF0E14B84FB148D210547ECD990A28C112867FA0F2B201377F40FAB2606A7` |
| fixed `CycleArc.dll` | `84BABC081697CF829947CDEC49C6803C31332EC9DC8D2623E183F48181050CE4` |
| fixed `CycleArc.Core.dll` | `220942F8448DC8E525EB1396CEAE671FA5C9540754A9F7D6AA461ABEC401F378` |
| `coreclr.dll` (both) | `128AEE8C62A673D64739E585E3876B61133571CEC83A46240A62581D5465639B` |

| Observation (optimized to fixed) | Pair 1 | Pair 2 | Pair 3 |
| --- | --- | --- | --- |
| Churn popup reopen median, ms | 12.3 to 7.8 | 18.7 to 7.5 | 14.3 to 6.6 |
| Churn popup reopen max, ms | 20.2 to 14.4 | 32.4 to 11.8 | 36.1 to 14.6 |
| Hidden switch median, ms (until its own idle marker) | 11.5 to 11.2 | 11.0 to 11.2 | 11.7 to 11.5 |
| `widget-visible` CPU, % of one core | 0.42 to 0.26 | 0.52 to 0.26 | 1.93 to 0.26 |
| `widget-visible` UI dispatcher p95 / max, ms | 0.53/0.70 to 0.54/0.67 | 0.59/0.71 to 0.39/0.68 | 0.73/1.14 to 0.77/0.86 |
| Whole-run allocated, MiB | 54.37 to 63.44 | 58.34 to 56.88 | 56.13 to 58.92 |
| Whole-run CPU, ms | 6,969 to 7,547 | 7,391 to 6,578 | 6,828 to 6,953 |
| Whole-run GC pause, ms | 11.18 to 11.92 | 12.38 to 13.99 | 13.54 to 12.76 |

The fixed runs of pairs 1 and 3 each had one burst in `widget-before-flyout` (before any popup
exists, identical code path): about 4-5 MiB allocated within two seconds, GDI +6 and
USER +12 objects, ALPC +5, ETW +13, Key +5 and wait-packet +6 handles, which then
stayed. It occurred at different times (2 s and 20 s into the phase) while the desktop
was in use; it resembles a first tooltip or UI Automation client attaching and is
treated as environmental. It accounts for their higher whole-run allocation and CPU,
the 18 ms dispatcher maximum in that phase and about 25 extra handles afterwards; the
clean pair 2 differs by 2 handles. Private Bytes were lower in most phases; Private WS
in the clean pair was 2-4 MiB higher in several pre-churn phases where both builds did
identical work, and every fixed value there lies inside the range the same optimized
binary showed in the previous batch (for example `tray-after-refresh` 92.09-95.26
against 90.16-100.09 MiB). After churn, Private WS was lower in all three pairs
(-4.69 / -2.11 / -7.63 MiB) and Private Bytes mixed (-11.98 / +18.22 / +14.49 MiB, no
collection in the phase). The idle selection bind adds a popup bind per hidden
selection change (6 detail rows and 3 avatars after the last churn switch).

50-cycle probe (two runs each, popup open, visible switch, hide, hidden switch):
reopen until `ApplicationIdle` median 14.3 / 15.2 to 5.1 / 5.0 ms, p95 18.9 / 21.6 to
7.5 / 7.1 ms, max 20.7 / 30.0 to 16.1 / 15.2 ms; no rows recreated in either; handles
plateaued. Per-phase pairs: [phases](measurements/ui-residual-2026-10-06-phases.csv),
[transitions](measurements/ui-residual-2026-10-06-transitions.csv).

Visual check: 805 production previews (the earlier set plus widget status rows) rendered
from optimized and fixed one mode after the other: 767 identical; the other 38 differ only
in wall-clock minute digits (21:50 against 21:51, 09:51 against 09:52).

Limits: one desktop session in use during measurement; synthetic harness; the cause of the
environmental burst was not identified; no installed-app or real-account measurement.
