# Synthetic Claude passive measurement

Opt-in console harness outside the solution; no extra packages. For a new comparison,
capture the baseline **before** changing the reader. Running these commands on the
current source alone measures the already optimized reader. Run from the repository root:

```powershell
dotnet build tests/CycleArc.PassiveMeasure/CycleArc.PassiveMeasure.csproj -c Release -o artifacts/zoom-passive-refresh/baseline-bin
dotnet artifacts/zoom-passive-refresh/baseline-bin/CycleArc.PassiveMeasure.dll baseline artifacts/zoom-passive-refresh/passive-baseline.json
```

After an accepted reader change, build into `artifacts/zoom-passive-refresh/after-bin`,
then run that DLL with `after` and `passive-after.json`. Keep both binaries and raw
reports, and compare the median of five trials for each row. Repeat baseline, after,
baseline using these separate DLL paths without editing source. Run measurements
serially without concurrent builds, tests or application work, with identical fixture
generation and Release configuration. Do not turn noisy timing comparisons into tests.

Compare the retained A/B/A reports:

```powershell
./tests/CycleArc.PassiveMeasure/Compare-PassiveMeasure.ps1 -Before @('artifacts/zoom-passive-refresh/passive-baseline.json', 'artifacts/zoom-passive-refresh/passive-baseline-repeat.json') -After 'artifacts/zoom-passive-refresh/passive-after.json' -Output 'artifacts/zoom-passive-refresh/passive-comparison.json'
```

The comparison rejects different scenario/sample/byte shapes or operation/event/auth
counts. It combines the two baseline runs (ten trials) and compares the after run (five
trials), reporting medians and full ranges, plus separate A1/B/A2 run statistics so
environment drift remains visible. All scenarios use process-wide allocations, including
the direct reader scenario. Keep the baseline source copy and SHA-256
hashes of source/Core/harness binaries alongside the reports for exact local reproduction.

Each process creates an isolated temporary root and supplies explicit history paths,
fake authentication, fixed synthetic identities and a clock. It uses no live source,
credentials, actual Claude/Codex configuration, CLI process or network. Cleanup removes
only its generated temporary root. Source version remains the development version in
`Directory.Build.props`; this tool does not establish compatibility or performance of
the publicly released application or real devices/accounts.

Fixtures contain 1 or 5 Claude accounts in a shared version-2 Desktop history file,
with 1, 64 or 512 samples per account (5-minute spacing). The largest is 2,560 samples,
within the 4,096 sample / 1 MiB input limits. Real projected statusLine files, connection
records, failure store and Desktop collector/cache are used. An updated Desktop file is
replaced atomically; statusLine updates use the production parser and atomic store.
Preparation and assertion work is outside measured time/allocation regions.

Four scenarios measure the direct reader with unchanged data, the complete passive
`ClaudeQuotaService.RefreshAsync` call with unchanged data, a new Desktop receipt on
every tick, and a new statusLine receipt on every tick. This is the service path invoked
by the existing passive manager, not a simulation of all provider or widget work.
The harness retains warm file-system caches, warms 12 ticks per scenario, then collects
five trials (80 reader, 40 unchanged passive, or 20 updated passive ticks per trial).
It does not wait two seconds between ticks or model an entire application's idle CPU.

Time is measured with `Stopwatch.GetTimestamp`; allocations use process-wide
`GC.GetTotalAllocatedBytes(precise: true)` because passive work crosses threads. The
isolated process avoids concurrent app/test work, but runtime background allocations
and local file-system noise may remain. Every tick asserts accepted latest percentage,
timestamp and fallback source. Each trial verifies no `Changed` event or fake auth on
unchanged reads, one event per updated account/tick, and fake identity revalidation for
each new Desktop timestamp.

The observer counts **Desktop history file opens and Parse calls only**; they do not
count connection, statusLine, failure or projected-cache file operations. Complete
passive timing/allocation includes those production operations. The observer receives
only an operation enum, never raw file data or account identifiers, and is unset in
normal production construction.

## Recorded synthetic results (2026-10-05)

Development source 0.10.0; .NET 10.0.12, Windows 10.0.26300, x64. The baseline used
the reader from `9e641a2` with the same opt-in operation observer as the after build.
Both builds used identical harness/fixture source. The measured
optimization only replaces repeated allowed-name/duplicate HashSets and property-name
strings with `JsonProperty.NameEquals` and a three-bit seen mask. Every candidate file
is still opened and parsed on every read. Authentication, connection rereads, leases,
semaphores, input limits, timers and equality decisions are unchanged.

The moderate fixtures below contain **64 samples per account**: one account uses
6,487 bytes / 64 samples, five accounts share 32,335 bytes / 320 samples. Updated
Desktop JSON varies by the deterministic fractional percentage (6,489 or 32,345 bytes
at the end of the first trial). Allocation columns compare the combined A1/A2 median
(ten trials) with B (five trials); timings show each run's median and trial range
separately. All values are per measured tick.

| Scenario | Accounts | Allocated bytes before → after (reduction) | A1 ms [range] | B ms [range] | A2 ms [range] |
| --- | ---: | ---: | ---: | ---: | ---: |
| Reader unchanged | 1 | 106,555 → 32,294 (69.7%) | 0.471 [0.328, 0.518] | 0.642 [0.507, 0.801] | 3.809 [0.338, 11.632] |
| Reader unchanged | 5 | 1,755,792 → 667,506 (62.0%) | 6.313 [3.871, 7.383] | 2.907 [2.805, 3.663] | 23.595 [9.056, 33.886] |
| Passive unchanged | 1 | 122,594 → 49,688 (59.5%) | 2.799 [2.575, 4.356] | 2.256 [2.143, 2.811] | 47.955 [33.554, 91.416] |
| Passive unchanged | 5 | 1,851,373 → 763,155 (58.8%) | 26.398 [19.454, 35.605] | 12.351 [10.405, 20.616] | 93.069 [83.347, 132.303] |
| Passive Desktop update | 1 | 147,442 → 75,636 (48.7%) | 6.926 [6.065, 7.846] | 7.289 [7.066, 8.081] | 14.150 [10.954, 32.507] |
| Passive Desktop update | 5 | 2,000,487 → 912,251 (54.4%) | 44.187 [39.886, 52.149] | 49.237 [40.708, 79.480] | 53.070 [42.097, 60.552] |
| Passive statusLine update | 1 | 122,750 → 49,826 (59.4%) | 2.988 [2.331, 3.405] | 2.160 [1.832, 4.917] | 2.774 [1.890, 8.069] |
| Passive statusLine update | 5 | 1,863,436 → 769,405 (58.7%) | 13.193 [11.782, 18.128] | 18.797 [16.460, 30.883] | 24.151 [16.580, 31.638] |

Allocation reduction is repeatable; elapsed times show substantial local environment
drift and sometimes increase. **No latency, idle CPU, physical-device or real-account
improvement is established.** The 1/512-sample scenarios and all raw trial values are
retained in the ignored JSON reports rather than inferred from this moderate table.

All three runs exited successfully: 120 trial rows each, with matching fixture shapes
and counters. Desktop history opens/Parse calls remain exactly 1 per account/tick in
every scenario. Unchanged trials have zero `Changed` events and zero fake auth calls.
Desktop updates have one event and one fake identity verification per account/tick;
statusLine updates have one event and zero fake auth calls. Latest percentage, original
source timestamp and source selection assertions pass for every tick.

Focused synthetic regressions passed **123/123**, with no skips, covering
`ClaudeDesktopUsageReaderTests`, `ClaudeDesktopUsageTests`, `ClaudeStatusLineTests`,
`ClaudeLiveIntegrationTests`, `MixedUsageProviderTests` and `ClaudeCallbackGenerationTests`.
The ten added reader cases cover escaped decoded names/duplicates, ordinal unknown
names, equal-size/equal-mtime atomic replacement, newly appearing candidates and actual
oversized files; existing malformed/future/conflicting timestamp and sample-limit cases
remain. The test result is `artifacts/zoom-passive-refresh/TestResults/passive-focused.trx`.
Harness builds had no errors but could not query NuGet vulnerability metadata (NU1900);
the focused test build also reported two existing xUnit1031 warnings in unrelated files.
