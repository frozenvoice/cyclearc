# Isolated Windows idle-memory measurements

## Scope and identity

This experiment measures the current source's production providers and WPF views in
`tests/CycleArc.IdleMeasure`, using five synthetic accounts: two Codex, two Claude and one
Cursor. Each Claude account has 64 synthetic Desktop history observations. External
adapters are fake, while production protocol parsing, collectors, account binding checks,
cache writes, projections and refresh guards remain in the path. Each synthetic request
keeps the same bounded delay and request count across configurations.

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

After warmup, the six measured phases are `tray-idle`, `flyout-visible`,
`tray-after-flyout`, `widget-visible`, `post-refresh-widget`, and `tray-after-refresh`.
The real tray stays registered, the flyout is pinned while shown, and the widget is
enabled only for its two phases. Account/settings controls that could start external
login or configuration flows are disconnected in the fixture. The one-minute automatic
refresh and display timers and two-second passive/visibility timer remain active.
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

**Pending measured batch and integrated validation.** No production GC setting has been
selected by this document. Fill the identities, exact parameters, repeat summaries,
provider call counts, native inventory and validation evidence from completed reports.
Aggregate each phase within each trial before comparing trials; per-second samples from
one process are not independent experimental repeats. Keep spread/outliers visible.

| Variant | Stable idle WS / private WS / Private Bytes | Managed / GC commit | CPU / allocations / GC counts / pause | UI p95 / max | Three-pair decision |
| --- | --- | --- | --- | --- | --- |
| Default | Pending | Pending | Pending | Pending | Baseline |
| Conserve 5 | Pending | Pending | Pending | Pending | Pending |
| Conserve 7 | Pending | Pending | Pending | Pending | Pending |
| Background GC off | Pending | Pending | Pending | Pending | Experiment only |

Source/published/installed identity: pending. Measured phases and parameters: pending.
Provider/data/visibility assertions and request-equivalence evidence: pending.
Native inventory and handle behavior: pending. Unit/WPF/Release validation: pending.
Final decision and any separate tuning commit SHA: pending.
