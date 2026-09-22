# CycleArc validation

## Current work — Codex, Claude and Cursor

- All-provider percentage precision (unreleased, 2026-09-22):
  - From clean main `91d82922f555bbbe1439822a2b55652f0e2cb71d` on
    `codex/percent-display-policy`. Codex, Claude and Cursor now share explicit
    widget/detail text policies. Widget boundaries use `<1%` / `>99%`; valid
    complementary pairs round usage directly and complement that displayed integer.
    Thus 76.5 / 23.5 displays 77% / 23%, and 12.5 / 87.5 displays 13% / 87%.
    Detail rings, account summaries and quota rows keep up to two trimmed decimal
    places, with `<0.01%` / `>99.99%` at the smallest boundaries.
  - Invalid source percentages stay unknown on these surfaces, including when the
    model's existing derived remainder is clamped. No source values, parsing, caches,
    geometry, thresholds, money, unlimited/off states or tray policy were changed.
    Healthy widget footer folding, local receipts and failure states are preserved.
    The detail ring only scales down unusually wide text inside its existing 92-DIP
    inner width; the widget geometry and original font sizes are unchanged.
  - Replaced the old 77% / 24%, 1% / 100%, 100% / 0% and fractional Claude widget
    expectations. Small helper checks passed 23 cases; existing Cursor widget checks
    passed 21 cases. Provider-path checks passed 28 cases across the three providers,
    and the corrected detail-call source contract passed 13 cases.
  - Actual production WPF checks passed 306 percentage cases in EN/KO,
    Dark/Light/System and 80/100/150% zoom, plus three simultaneously shown native
    widget/flyout pairs. They assert the visible widget, detail ring, account card
    and quota-row text, unrounded arc endpoints/colors, and measured text bounds.
    Existing Cursor UI checks also passed. Inspected paired captures are linked in
    `docs/images/README.md`; only four affected current Cursor widget previews
    were replaced. Historical screenshots and run evidence remain unchanged.
  - The first gate exposed one old source assertion for `From(...)`; it now requires
    the detail-specific `FromDetail(...)` call, preserving its other assertions.
    A subsequent desktop process check encountered an existing temporary report-file
    sharing violation. The focused desktop check then passed without any source or
    timeout change. Failed-attempt evidence: `artifacts/percent-display-first-gate.log`,
    `artifacts/percent-display-desktop-failure.log`; focused results:
    `artifacts/percent-display-ui.log`, `artifacts/percent-display-cursor.log`,
    `artifacts/percent-display-desktop-recheck.log`, and
    `artifacts/percent-display/TestResults`.
  - Final `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch -TestResultsDirectory
    artifacts/percent-display/TestResults` passed in 4m 56s (exit 0): Release build,
    all 1,772 unit tests, complete WPF/tray/DPI/layout checks, test-flavour compile,
    self-contained single-file publish, built/published Claude receiver checks,
    installer packaging and isolated package apply/restore. Log:
    `artifacts/percent-display-final-gate.log`.
  - No installed-app replacement or destructive `Verify-InstalledUpdate.ps1` run:
    this is a working Windows profile, not a disposable VM/user, and installation
    behavior is unchanged. No real-account/model request, push, CI execution,
    main merge, version change, tag or release was performed.


- Healthy widget status rows (unreleased, 2026-09-21):
  - Started from main `5875b52` on `codex/widget-hide-healthy-status`. Only the widget
    model's `ShowStatusRow` and its WPF visibility binding change production behavior.
    Available Claude server data (`claude-live`) and successful Cursor data (no failure
    detail or the explicit live marker) collapse the footer when a success timestamp and
    quota windows exist. Signing in, partial Grok failure, stale/error/identity/waiting
    states, missing metadata and all Claude Desktop/Code receipts keep their footer.
    Status text, accessibility, exact tooltip/popup details, original quota values,
    integer Cursor glyphs, geometry, querying and persistence are unchanged.
  - Focused tests passed 94 widget model cases and the new WPF status/source/recovery
    checks. Existing Cursor popup/widget checks, 76 summary layouts and 246 mixed
    Codex/Claude WPF renders passed. Actual state rebinds cover healthy → failure →
    healthy without leftover spacing, hidden errors or changed ring/name alignment.
  - Before/after captures use the same offline fixtures, with baseline production files
    from `5875b52`. All 36 standalone/mixed EN/KO, Dark/Light, 80/100/150% pairs retained
    width and reduced height. At 100% the actual PNG dimensions are: mixed 720 × 192 →
    720 × 174, Cursor 254 × 192 → 254 × 174, Claude 254 × 176 → 254 × 158. The removed
    footer includes its 5-DIP top margin. Inspected views include healthy states, retained
    stale errors and Claude local receipts. See `docs/images/widget-status-*`,
    `artifacts/widget-status-image-metrics.json` and `artifacts/widget-status-{before,after}`.
  - Final `dev-run.ps1 -NoLaunch` passed in 4m 23s: Release build, all 1,721 unit tests,
    full WPF/DPI/layout suite, test-flavour compile, single-file publish, published
    Claude receiver and isolated package apply/restore. Evidence:
    `artifacts/widget-status-final-gate.log` and `artifacts/widget-status/TestResults`.
    NuGet emitted NU1900 warnings because vulnerability metadata could not be reached;
    build and executable checks passed. No source timeout or verification guard changed.
    No main merge, release/tag or installed-app replacement was performed for this change.

- Cursor widget integer percentages (historical, 2026-09-21; superseded by the all-provider precision correction above):
  - This records the earlier implementation and its then-passing tests. Its Cursor-only
    policy and 99.6% → 100% expectation are not the current contract.
  - Only visible Cursor widget strings use whole-percent rounding, directly from the
    original numeric value with midpoint rounding away from zero. Examples: 76.91% used
    becomes 77%; 23.09% and 97.1% remaining become 23% and 97%. The existing precise ring
    and period strings remain available; tooltips, popup, numeric values, calculations,
    arcs and warning thresholds are unchanged. Unknown stays `?`; money, unlimited
    labels and Codex/Claude display formats are unchanged.
  - Focused verification passed 57 widget-model/ring unit cases, including 11 new cases
    for the requested examples, midpoint and double-rounding boundaries, unknown/NaN,
    money/unlimited, other providers, popup/tooltip precision, and 99.6% displayed as
    100% without changing its percentage or danger state. Production WPF tests verify
    the actual fractional arc endpoint and normal color at 99.6%, the integer ring/rows,
    and fractional ring/row/module tooltips and popup text in EN/KO and all themes.
  - All 76 production summary captures passed in EN/KO, Dark/Light and 80/100/150% zoom,
    including mixed/wrapped accounts, unknown/stale/auth states and monetary values.
    The normal mixed fixture stays 720 × 192 DIP with aligned names and rings.
    Current widget previews were visually inspected and updated; historical comparisons
    retain their original screenshots. See `artifacts/cursor-integer-ui` and
    `artifacts/cursor-integer-summary`; all views use synthetic accounts, no live queries.
  - Final `dev-run.ps1 -NoLaunch` passed in 4m 45s: Release build, all 1,668 unit tests,
    full production WPF checks (including 180 DPI/layout renders and native windows on
    three monitors), test-flavour compile, single-file publish, published Claude receiver,
    and isolated package apply/restore. Evidence: `artifacts/cursor-integer-final-gate.log`
    and `artifacts/cursor-integer/TestResults`. No installed app was replaced and no
    main merge, release or tag was created for this change.

- Cursor widget compact ring follow-up (unreleased, 2026-09-21):
  - Current-source baseline is `f0a9964`, which already contains the three-allowance
    summary and account alignment. The repeated two-line caption below the Cursor ring
    now sits inside it as Cursor / Other / Grok Bot. The full represented allowance row
    is emphasized, with complete names and Monthly / Weekly group headings retained.
    Module width stays 232 DIP, ring diameter 64 DIP, allowance/value text 12 DIP and
    ring caption 10.5 DIP. Normal standalone/mixed content falls from 206 to 192 DIP high;
    the mixed fixture stays 720 DIP wide and all name tops/ring centres remain aligned.
  - Widget ring candidates now keep Cursor Models → Other Models → Grok Bot priority
    regardless of provider response order, using the existing known-value fallback.
    The source snapshot, provider queries, quota arithmetic, storage, popup and tray
    selection logic are unchanged. Unknown values, authentication/identity failures,
    retained stale data, omitted budgets and exact reset/update tooltips stay explicit.
    Long monetary values may wrap their full label, and failure status may add a line;
    neither is clipped to force the normal summary height.
  - Focused checks passed 134 Cursor/widget unit tests and 76 actual WPF summary renders:
    standalone/mixed/wrapped accounts, EN/KO, Dark/Light, 80/100/150% zoom, long names,
    unknown values, Other/Grok ring fallback, enabled/disabled on-demand, omitted budgets,
    large monetary values, stale request/auth failures and cached identity mismatch.
    Cursor popup/account checks also passed across EN/KO and Dark/Light/System.
  - Final `dev-run.ps1 -NoLaunch` passed in 4m 31s: Release build, 1,657 unit tests,
    full production WPF suite, 180 DPI/layout renders at 100/125/150/175/200% and native
    windows on three monitors, single-file publish, published Claude receiver, and isolated
    package apply/restore. See `artifacts/cursor-compact-final-gate.log`. The sandbox-only
    preflight could not read running-process metadata; rerunning with process-inspection
    permission passed. No guard was bypassed and no installed process was replaced.
  - After push/merge approval, integrated main `9bed0e2` (0.6.2, shared verification
    and passive DPI recovery fixes) without changing the compact feature delta. The new
    shared `dev-run.ps1 -NoLaunch` passed on the integrated source in 4m 28s, including
    1,657 unit tests, all WPF/DPI checks, test-flavour compile and 0.6.2 package verification.
    Evidence: `artifacts/cursor-compact-integration-gate.log` and
    `artifacts/cursor-compact-integration/TestResults`. No installed-app replacement ran.
  - Before/after images use the same offline accounts and fixed timestamps. See
    `docs/images/cursor-widget-compact-{before,after}-{en,ko}-{dark,light}.png` and
    `artifacts/cursor-compact-{before,after}`. Current standalone widget previews were
    also updated. The changed views were visually inspected; no image generation,
    real account/credential access or installed-app replacement was used.

- Local prerequisite guidance and shared Windows verification (unreleased, 2026-09-21):
  - Started from clean main/origin/main f0a996442762b3d582054669b0478bd3fe33acb9
    on codex/build-local-prerequisites. Remote main was confirmed read-only.
    The initial implementation was kept local; push and merge were subsequently authorized.
    No tag, release or local application installation was requested.
  - Interactive build-local probes the VS 2022 toolchain before offering approved Microsoft
    installation, manual instructions or cancellation. A ready machine needs no download.
    The same resolver still requires vswhere, the VC x64/x86 component, the actual x64
    linker and the Windows SDK x64 kernel32.lib. Build-local rechecks them after installation.
    CI, silent installs, redirected input and the explicit prompt opt-out remain fail-fast.
  - The VS 2022 bootstrapper source is
    [Microsoft's versioned endpoint](https://aka.ms/vs/17/release/vs_buildtools.exe).
    The [official command-line contract](https://learn.microsoft.com/en-us/visualstudio/install/use-command-line-parameters-to-install-visual-studio?view=vs-2022)
    distinguishes bootstrapper --wait from installed setup.exe; --passive shows progress and
    --norestart leaves reboot approval to the person. Build Tools uses VCTools; existing
    IDEs use NativeDesktop. The installed app is untouched by all prerequisite outcomes.
  - Preflight now initializes failure logging before checking prerequisites. PowerShell prints
    the stage/cause once; CMD preserves its exit code without repeating the summary or claiming
    that an absent log proves no stage was reached. Old child build logs are excluded from a
    new preflight failure.
  - Windows CI now calls the same PowerShell gate as local verification. Feature branches use
    the pull_request event instead of also triggering a full push run; push remains main-only.
    Main direct-push history and Release.ps1's exact-SHA successful-push artifact requirement
    make keeping main validation necessary. PR merges can therefore still validate their new
    main SHA; there is no API or commit-message heuristic to suppress them.
  - The GitHub branch-protection endpoint reported unprotected main; branch rules and repository
    rulesets both returned empty lists at audit time. This does not establish external IT or
    organization policy. PR concurrency cancels an obsolete run only for the same PR; release
    workflows and main push runs do not inherit that cancellation policy.
  - Build uses the hosted windows-2022 image's preinstalled VS 2022 toolchain and fails before
    expensive work if its actual linker/SDK contract is absent. The artifact-only managed
    Setup install/repair job stays on windows-latest without SDK/VS installation or rebuilding.
    This separates the VS 2022 compiler requirement from current-Windows installation coverage.
  - Recent successful Windows runs 272-281 (for example [run 281](https://github.com/frozenvoice/cyclearc/actions/runs/35522987565)) measured build jobs at 446-554 seconds,
    setup-dotnet at 27-43 seconds, and restore at 21-28 seconds. Keep setup-dotnet 8.0.x;
    add no NuGet cache, binary cache, or global.json in this change. The duplicate feature
    push run is the avoided work; no end-to-end speedup percentage has been measured.
  - Local focused verification passed: BuildLocal.Tests.ps1 **60 PASS checkpoints**, including
    all prerequisite flow and real HTTP/signature/process-boundary tests with fake adapters.
    After the last review, exact discovered channelId/productId were also added to the
    existing-installation modify command; SetupUiPrerequisites.Tests.ps1 passed again on
    that final helper (artifacts/setup-prerequisites-focused.log).
    Also passed:
    LocalInstall.Tests.ps1 **15 isolated deployment/retry/rollback scenarios** plus its path,
    lease, mutex, discovery and process checks; Release.Tests.ps1 isolated release guards;
    VerificationWorkflow.Tests.ps1 both workflow/evidence-path sections; PowerShell parsing,
    one PyYAML BaseLoader syntax parse and diff checks. Logs are
    artifacts/build-local-prerequisites-focused.log, artifacts/local-first-local-install.log,
    and artifacts/local-first-release-guard.log. The existing Unix-only direct-script launch
    case remains skipped on Windows; the new actual CMD preflight fixture passed.
  - The shared gate was attempted locally before delivery, then checked once more after the
    executable correction below. The final pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch
    passed preflight and workflow-contract, then **failed at setup-ui-toolchain** in 0.635
    seconds (artifacts/delivery-full-gate-final.log). This PC has only VS Build Tools 2026,
    version 18.10.12210.168, and no VS 2022 instance. The 17.x requirement was not relaxed;
    Native AOT package/package-verify need the hosted windows-2022 environment. No local
    successful full-gate or installer-package result is claimed.
  - All locally available stages were exercised separately before pushing. Release solution
    build, desktop-instance IPC checks, **1,655 unit tests**, the full production WPF suite,
    test-flavour compilation, self-contained single-file publish and the published receiver
    checks passed. Final WPF evidence is artifacts/delivery-uismoke-final.log; build/publish
    evidence is artifacts/delivery-local-20260921-103215/verification.log. The unit TRX is under
    artifacts/delivery-local-20260921-101150/TestResults. Existing script checks above were reused.
  - The first local unit run exposed four outdated DevRunScriptGuardTests assumptions about
    commands duplicated in YAML. The guards now assert the common publish contract, package
    upload path and shared stage order. Its 14 focused cases and the full unit suite passed.
  - Local WPF verification also exposed hidden-fixture DPI mismatches: the unshown Window
    was measured at 96 DPI while its separate Content visual root retained 144 DPI. Cursor
    summary and mixed-height fixtures now set both roots to 96 DPI before binding; viewport
    assertions are unchanged. Actual native monitor checks and the dedicated 100-200% DPI
    suite remain intact. EN/Dark 80% and KO/Light 150% captures were visually inspected.
  - An existing secondary-monitor position-reset focus failure was reproduced independently.
    An activation stack identified QueueRelayout -> Relayout -> RecoverTo -> WPF's nested
    DPI SetWindowPos. RecoverTo now uses the existing bounded PassiveUpdate guard around
    its Left/Top assignments. The original lifecycle regression then passed EN/KO and
    Dark/Light on both physical monitors, with the foreground, active window and activation
    count preserved. Diagnostic reproduction is artifacts/delivery-recovery-trace.log;
    passing evidence is artifacts/delivery-recovery-fixed.log. Temporary instrumentation
    was removed, and no timeout, retry or weakened assertion was introduced.
  - The final WPF suite also passed **151** layout checks, **531** zoom checks, **20** settings
    checks, **183** tray renders, **180** DPI/layout renders on two monitors, and **36**
    production resource/layout renders. Native AOT packaging, GitHub events/artifact transfers
    and disposable managed-install checks remain the approved remote validation boundary.
    No real VS install/remove or local CycleArc Setup/install/repair/update/removal ran;
    the existing desktop at publish/local/CycleArc.exe was kept running.
  - The first authorized [PR run 35551400427](https://github.com/frozenvoice/cyclearc/actions/runs/35551400427)
    passed the entire shared gate, including VS 2022 Native AOT packaging and package-verify
    for 0.6.1, but the managed-install job could not download its artifact. The build log
    showed that upload-artifact excluded publish/.dev-velopack because its default ignores
    files within dot-prefixed directories ([official behavior](https://github.com/actions/upload-artifact/blob/v6/README.md#uploading-hidden-files)).
    Only that package upload now sets include-hidden-files: true, and missing assets fail
    the upload step with if-no-files-found: error. Workflow guards and negative copies
    rejecting both old options passed locally. The exact @actions/glob 0.5.0 dependency
    reproduced the default exclusion locally (zero matches) and the corrected inclusion
    (one synthetic CycleArc-Setup.exe match); an isolated js-yaml parse confirmed the
    workflow values. Unchanged executable checks were reused.
    No blind rerun or timeout change was made.

- build-local fixture freshness (unreleased, 2026-09-21):
  - The reported `build-local-regression` failure on `1038363` came from the tests reusing
    the cancellation case's fake Setup.exe for the following exit-1 case. Once that file
    preceded the next run by more than the five-second grace, the production leftover-file
    guard correctly failed at `package` before the expected installer failure at `install`.
  - Normal package fixtures now create their files inside each run's `PackagedSetup` callback.
    The cancellation regression deliberately ages the previous Setup.exe by two days before
    rebuilding, without sleeping; it must reach Setup and report exit 1. The separate stale-file
    case verifies rejection before desktop shutdown, Setup or launch, including silent mode.
    Production build, packaging, freshness guards and application code are unchanged.
  - Reproduced the exact reported exception locally before the fix by aging the reused file
    (`artifacts/build-local-fixture-repro.log`). After the fix,
    `pwsh -NoProfile -File tests/BuildLocal.Tests.ps1` passed all **39 Windows checks**
    (`artifacts/build-local-fixture-fixed.log`). The Unix-only direct script launch is skipped
    on Windows. No real Setup.exe installation or full CMD install was performed; all install
    adapters used isolated synthetic files. Existing executable build/WPF/package evidence is
    reused because this change touches only the script tests and this validation record.

- Cursor widget summary and alignment (unreleased, 2026-09-21):
  - The widget shows at most the reported, enabled Cursor Models / Other Models / Grok Bot
    rows in that order. Monthly and Weekly headings replace repeated cadence/reset lines;
    the original allowance names and fractional remaining values stay readable at the existing
    12-DIP size. Modules remain 232 DIP wide. Names and 64-DIP rings align across providers,
    and a caption below the Cursor ring identifies its allowance. The widget ring's candidates
    are limited to those same three rows; omitted budgets cannot introduce a fourth visible limit.
  - All original allowances, including disabled on-demand and omitted budgets, remain in the
    widget tooltip with exact local reset/update timestamps. The detail popup is unchanged.
    Unknown values remain `?`; stale status retains the failure and age; identity mismatch
    and signed-out views expose neither cached rows nor cached ring values. Optional Grok
    failure stays in the tooltip without marking a fresh monthly response stale.
  - Query, calculation, credential, refresh, registry and cache code are unchanged. Codex and
    Claude retain their period/reset rows. This branch starts at `012d514`, preserving the
    earlier Cursor integer tray glyph change without merging it into main.
  - Actual production WPF before images were exported from `012d514` before production edits,
    using an offline harness with fixed synthetic accounts/times. The same 100% mixed-account
    fixture is 720 × 358 DIP before / 720 × 206 after in Korean (42% lower), and 720 × 374 /
    720 × 206 in English (45% lower). Checked-in comparisons are
    `docs/images/cursor-widget-summary-{before,after}-{en,ko}-{dark,light}.png`.
  - Focused verification passed **106 Cursor/widget unit tests**, **48 production WPF summary
    renders** (single/mixed accounts, EN/KO, Dark/Light, 80/100/150% zoom, unknown values,
    unavailable/stale/auth states, long names and budget-first responses), and **180 DPI/layout renders** at
    100/125/150/175/200%, including native windows on **3 monitors**. Cursor popup/account
    regressions passed in EN/KO and Dark/Light/System, with each named ring target. Bounds,
    value overlap, group headings, name/ring alignment and exact tooltips are asserted.
    The changed documentation previews and comparison images were visually inspected.
  - Logs and full preview matrices are in `artifacts/cursor-widget-summary-*`. The initial
    focused runs exposed outdated widget label/tooltip assertions and a margin included in a
    text measurement; these were corrected locally before the final gate. No remote retry was
    used to diagnose them.
  - The ring-candidate refinement adds two regressions: an omitted budget cannot become a
    fourth visible allowance or substitute its known percentage for unknown summary values.
    The tray precision fixture now uses the real `cursor-auto` ID instead of the generic
    `smoke` ID. Its **183 render cases** passed with the existing integer glyph/fractional arc.
  - One full run failed the existing position-reset focus invariant. HWND/PID/activation
    diagnostics were added without changing the assertion or production focus behavior.
    All 12 standalone recovery combinations and the following full-run recovery stage passed;
    the original failure's exact cause remains unconfirmed. See
    `artifacts/cursor-widget-summary-recovery.log` and `cursor-widget-summary-final-fast-gate.log`.
  - Final local delivery verification is complete: **1,655 unit tests** passed on the final
    production source (`cursor-widget-summary-final-gate.log`); after the test-only diagnostics
    and fixture correction, `dev-run.ps1 -NoLaunch -Fast` passed in **4m 02s**, reusing those
    unchanged unit results. The full WPF suite, script/desktop-instance checks, single-file
    publish, published Claude receiver and isolated package apply/restore all passed.
    Final log: `artifacts/cursor-widget-summary-delivery-gate.log`.
  - Real-account receipt and installed-app update behavior are not exercised by these synthetic
    presentation checks. No main merge, release or installed-app replacement is authorized here.

- Cursor integer tray digits (unreleased, 2026-09-21):
  - Only the Cursor notification-icon glyph rounds to a whole percentage, using the same
    midpoint-away-from-zero rule as Codex. Both number and ring styles display `76.9` as
    `77`, keeping the digits legible in the native 16-pixel slot. Unknown remains `?`.
  - The source percentage, ring arc, danger threshold, popup/widget/tooltip precision,
    provider queries and storage are unchanged. Existing Codex and Claude glyphs retain
    their previous behavior. No installed app is replaced by these checks.
  - Focused production rendering passed **183 icon cases** across 16/24/32 pixels,
    both styles and both taskbar tones. Pixel comparisons cover fractional/midpoint
    rounding, 0/100, 99.6 rounding to 100 without changing its danger color, and unknown.
    Separate checks retain the fractional arc and popup/widget/tooltip text. The initial
    new test incorrectly expected a blue arc at 0%; correcting that expectation passed.
    The native-size Cursor contact sheet was visually inspected and saved in
    `docs/images/cursor-tray-icons.png`; export: `artifacts/cursor-tray-integer`.
  - Final `dev-run.ps1 -NoLaunch` passed once (5m 30s): **1,647 unit tests**, the full
    WPF suite including all 183 icon cases, script/desktop-instance regressions,
    single-file publish, published receiver checks and isolated package apply/restore.
    Log: `artifacts/cursor-tray-integer-full-gate.log`. No real-account query was needed.

- Cursor Plan & Usage wording (unreleased, 2026-09-20):
  - Display-only mapping: `cursor-auto` is Cursor Models / Monthly, `cursor-api` is
    Other Models / Monthly, and `cursor-sand` is Grok Bot / Weekly. The official names
    remain English in both locales; cadence and usage wording are localized. The large
    ring names its represented allowance and whether the usage is monthly or weekly.
  - Account cards, popup, tray and widget share the same label mapping. Cursor popup and
    widget values sit below their labels, avoiding overlap with the longer official names.
    Native tray text keeps the actual update timestamp and prioritizes the three named
    allowances over optional spending budgets within its existing 127-character limit.
  - Internal identifiers, account registry/cache versions, credentials, requests, quota
    arithmetic, source reset times and ring selection are unchanged. Codex and Claude
    keep their existing labels and row layout. Unknown values remain unknown.
  - Focused production WPF checks cover EN/KO, Dark/Light/System and 80/100/150% zoom,
    including each of the three named allowances in the large ring, label/value bounds,
    and account-card wrapping. Reviewed previews use synthetic `example.invalid` accounts.
    Preview output: `artifacts/cursor-naming-ui`.
  - `dev-run.ps1 -NoLaunch` passed once on the final production code (4m 19s): all
    **1,647 unit tests**, the full WPF suite, desktop-instance/script checks, single-file
    publish, production Claude receiver checks and isolated package apply/restore.
    Log: `artifacts/cursor-naming-full-gate.log`. A subsequent test-only timezone fix
    passed all 13 naming tests; it checks the actual local timestamp rather than a fixed
    calendar date. All 12 affected EN/KO Dark/Light documentation previews were inspected.
  - No real-account request, main merge, release or installed-app replacement is part of
    this wording change. Synthetic rendering does not establish real-account compatibility
    or installed-update behavior; those are outside this presentation-only verification.

- Cursor review follow-up to `f84df8d` (unreleased, 2026-09-20):
  - Identity mismatch still hides every quota, but the provider now retains the last attempt
    through cache reconciliation and restart. An interrupted mismatch cache commit uses the
    marker's newer attempt time, so reopening the popup cannot bypass the automatic interval.
  - Sand/Grok failure and retry deadlines are separate from the monthly response. A fresh
    monthly sample remains Available; only Sand waits for its retry deadline, including after
    restart. Older Grok values are omitted rather than assigned a new monthly timestamp.
    The detailed popup explains optional failure; the monthly popup/tray/widget status stays
    Updated. Monthly failures still preserve last-good values and their original timestamp.
  - Usage-cache v2 reads legacy v1 and migrates its coupled Sand failure/backoff metadata
    without changing the receipt time. Rolling back to the original Cursor build can require
    a new usage query because that older reader cannot parse v2 usage caches. This does not
    migrate or delete account bindings or the account registry.
  - Registry component coverage checks v1/v2 upgrades, v3 markers on primary/backup,
    rejection of stale v2 saves, corrupt-primary/backup recovery and later save preservation.
    The initial v3 transition deliberately retains the previous-good profile set in the
    backup: primary damage before the next save may lose the just-added Cursor registry
    reference, while its profile files remain on disk. This is not a claim of seamless
    operation in a pre-Cursor executable; those older readers reject v3.
  - Local verification: `dev-run.ps1 -NoLaunch` passed once on the final production code
    (4m 35s): **1,634 unit tests**, Release build, desktop-instance and script regressions,
    the complete WPF suite, single-file publish, production Claude receivers, packaging and
    isolated package apply/restore with unchanged external data. The focused Cursor run
    before the last added cases passed 61 tests. Logs: `artifacts/cursor-review-full-gate.log`.
  - `tests/scripts/Verify-CursorRegistryCompatibility.ps1` compiles the unchanged account
    store from pre-Cursor commit `36dcf1fdee0cd05381b323886744d999c8f1f567` with minimal
    matching type fixtures. The actual historical reader accepted a valid v2 baseline,
    rejected v3 primary and backup fallback, and preserved the synthetic registry,
    settings, Codex cache and Claude/Cursor binding/cache files byte for byte. This is a
    storage-component check, not execution of the whole old app. Log:
    `artifacts/cursor-review-registry-compat.log`.
  - `--cursor-ui artifacts/cursor-review-previews` passed for synthetic production views
    in EN/KO and Dark/Light/System. Visual review confirmed the optional Grok explanation,
    fresh monthly values and original update time in English dark and Korean light popup
    and widget previews. No real credentials or account responses were used in this follow-up.
  - Actual existing-install v2-to-v3 update and pre-Cursor binary rollback are not verified
    end to end. `Verify-InstalledUpdate.ps1` cannot isolate this working user's data root,
    registry, mutex or shortcuts, so it was not run here. Isolated store/package checks are
    component evidence only. No main merge, release or installed-app replacement was made.

- Cursor integration (unreleased, 2026-09-20):
  - The existing provider/account-service boundary now connects the signed-in Windows
    Cursor account without token copying. It verifies the server identity before usage,
    persists only a binding hash and projected quotas, and neither renews credentials nor
    reads history/browser cookies. Registry version 3 retains Codex/Claude profiles and
    upgrades the backup version before the primary to protect against older builds.
  - Auto/API percentages remain separate from legacy dollar accounting. On-demand/team
    budgets and Grok keep their own rows. Missing values stay unknown; disabled allowances
    show Off. Popup, native tray tooltip and widget show the original successful response
    time. The native tooltip respects its 127-character limit.
  - Deterministic tests cover exact-key SQLite reads, bounded/authenticated HTTP,
    optional-source failure, unknown/zero/disabled values, account mismatch, retry delay,
    cancellation, restart, corrupted cache recovery, interrupted connection writes and
    disconnect during an in-flight query. Mismatch cannot recover an old quota through a
    newer cache, a backup or a cache write failure.
  - The initial unit/WPF runs caught a shared-widget/account-card regression: filtering
    out windows without known values also removed an existing Codex unknown-period row.
    Restoring every supplied period and the original Codex/Claude card policy fixed it.
    A new restart test also needed sequence comparison after JSON restored an array as
    a list. The focused Cursor/widget check passed all 67 cases, and the affected Codex
    WPF check passed 144 renders.
    The period-control suite now targets Codex/Claude explicitly (108 renders); Cursor
    has named allowances rather than five-hour/weekly controls, covered by its own suite.
  - Final local evidence: all **1,616 unit tests passed** in `dev-run.ps1 -NoLaunch`.
    After the WPF-only fixes, `dev-run.ps1 -NoLaunch -Fast` reused that unchanged unit
    suite and passed Release compile, desktop-instance checks, script regressions,
    the entire WPF suite, single-file publish, published Claude receiver checks,
    Velopack packaging and isolated package/update/rollback verification (2m 51.7s).
    No installed app was replaced or launched. Local logs are
    `artifacts/cursor-full-gate-final.log` and `artifacts/cursor-verified-gate.log`.
  - Production WPF checks and visual review cover EN/KO, Dark/Light and the local System
    theme. Checked-in Cursor images use synthetic profiles, including explicit unknown
    and monetary assertions; they are not evidence of a real account.
  - Real read-only compatibility was verified separately with Cursor 3.21.13 on Windows.
    Native and web account/usage routes returned HTTP 200. The compiled production .NET
    probe at 12:35 UTC verified identity and returned four separate quota rows, three known
    percentages and four reset timestamps, including a successful optional Grok request.
    It wrote no account, binding or cache and emitted no credentials or raw account data.
    See [Cursor research](CURSOR.md) for endpoints, semantics and CodexBar references.
  - Not verified with real accounts: team/enterprise, trial-only and legacy request plans.
    Team/trial parsing uses synthetic upstream-shaped fixtures; the legacy request route
    is not implemented. Custom Cursor user-data directories are not searched.
  - Installed-app setup/update/removal E2E is not run on this working Windows profile:
    it requires a disposable VM/user and modifies real installation/registry state.
    Local packaging/receiver checks do not establish installed-update compatibility.

- Setup installer log retention, Restart Manager query outcomes, and a withdrawn timing claim
  (unreleased):
  - **Log fallback was inside the directory the run deletes.** When the default log location
    could not be used, `PrepareLogPath` fell back to a file under the per-run work directory,
    and `Install`'s `finally` deletes that directory whole. A failure caused by the install
    target therefore still lost the log explaining it — the case the fallback existed for. The
    path choice now lives in `SetupLogPaths`, every candidate is outside both the install
    target and the work directory, and the chosen path is proven writable before the engine
    starts. An explicit `--log` is still the only candidate when one is given, and when nothing
    is writable the engine runs without `--log` and the result reports that no log exists
    rather than naming one that does not. `SetupLogPathTests` drives the production code with
    the write probe injected; restoring the old in-work fallback fails
    `NoCandidateIsInsideAPerRunWorkDirectory` with the offending path.
  - **"Could not ask" read as "nobody holds it".** Every Restart Manager failure — interop
    unavailable, `RmStartSession`, `RmRegisterResources`, `RmGetList`, and a second
    `ERROR_MORE_DATA` — returned the same empty array as a clean "none", and
    `Wait-InstallDesktopReleased` used an empty result as its success condition. A caller that
    asked for the install-root check could therefore walk past a directory whose state was
    never confirmed. The query now returns one of three outcomes: queried with no holder,
    queried with holders, or unknown with the API and its error code. The wait treats unknown
    as not-yet-released and reports why; `ERROR_MORE_DATA` is retried with the reported size a
    bounded number of times and then returns unknown rather than looping or claiming none.
    Sessions are ended on both paths, and nothing asks any process to close or restart.
    Restoring the old "empty means free" condition fails the injected-failure cases.
  - A queried result with no holders means Restart Manager reported none. It is not a
    guarantee that the directory can be renamed: this API reports the holders it knows about,
    not every possible handle.
  - **Withdrawn: the 5.1 s reading.** Run
    [35395590182](https://github.com/frozenvoice/cyclearc/actions/runs/35395590182) logged
    `Desktop released its installation after 5.1s`, and the same-version repair then succeeded.
    That was previously written up as proof that the installation stays locked for about five
    seconds after the desktop exits, that this is what caused the earlier rename error 5, and
    that the fix is therefore confirmed. None of that follows. `ElapsedSeconds` is the whole
    runtime of `Wait-InstallDesktopReleased`, which in that run included, for the first time,
    listing the installation's files and querying Restart Manager. That run recorded no holder
    observations, so whether the 5.1 s was spent waiting for a holder or performing the query
    itself cannot be told apart from what was logged. The opposite claim — that the query is
    simply slow — is equally unsupported. What can be said: the wait took 5.1 s in that run and
    the repair that followed succeeded. The cause of the earlier rename failure, and the
    identity of any holder, remain undetermined. The diagnostics are in place, so a future run
    that does observe a holder will say so.

- Desktop-instance shutdown-report race, release asset lists and live build-local progress (unreleased):
  - **Correction to the previous entry.** The earlier note said the reported failure was a
    child that did not write `first.jsonl` within 10 seconds. The user's stack does not
    show that. It was `WaitForReport(...)` at `DesktopInstanceProcessChecks.cs:399` called
    from `Run()` at line 74, and at that commit (`beea096`) line 74 was
    `WaitForReport(first.ReportPath, report => report.State == "shutdown", first.Process)`.
    So the child had already reported ready, answered status and activate, and returned a
    successful shutdown response; the wait that expired was the one for the *shutdown*
    report. Startup delay, Defender and machine load are not established as the cause by
    that log, and are not claimed here. Widening the process budget to 30 seconds remains a
    reasonable budget change, but it is not a fix for what the stack shows.
  - **Reproduced defect (report file read/write race).** The parent read reports with
    `File.ReadLines`, which holds `FileShare.Read` for the life of the enumeration and
    therefore refuses the child's `FileAccess.Write` open. The child's shutdown callback ran
    `Report(...)` before `stop.TrySetResult()`, and `DesktopInstanceServer` flushes the
    acknowledgement *before* invoking the callback and then swallows callback exceptions
    (`InvokeCallbackAsync`). A denied append therefore produced: successful shutdown
    response → lost report → no stop signal → child alive → parent waits out its budget.
    `ChildReportFileTests.LegacyReadLinesEnumeration_RefusesTheChildsAppendOpen` reproduces
    the denial against a live `File.ReadLines` enumerator rather than asserting on source text.
  - **Fix.** Report reads and appends go through one `ChildReportFile` helper:
    `FileShare.ReadWrite | Delete` both ways, records exposed only once their newline has
    landed (decoding stops at the last newline, so a torn UTF-8 sequence never reaches a
    record), and a bounded retry for a transient sharing denial that still throws rather
    than reporting success. The child now wraps each report: a failure is written to stderr
    with the exception, recorded, and the stop signal is released so the child exits with a
    distinct code 6 instead of hanging. The shutdown report is still required — a successful
    shutdown response alone never passes the check, and no `finally` converts a failure to
    success. The production IPC ack/shutdown contract is unchanged.
  - **Before/after.** With the child guard removed, the new `RunLostShutdownReportCheck`
    scenario reproduces the original symptom exactly: `--desktop-instance` fails after
    31.5 s with "A child that could not write its shutdown report kept waiting for a stop
    signal instead of exiting." With the guard, the whole `--desktop-instance` suite passes
    in 1.7 s. Seven `ChildReportFileTests` cases cover the shared-mode overlap, a withheld
    partial record, a truncated multi-byte sequence, a surfaced write failure and a
    200-record interleave driven by a synchronisation point rather than by repetition.
  - **Release asset lists.** `Get-ExpectedReleaseAssetNames` returned four names and was
    also used as the *allow* list for an existing release, while packaging accepts
    `assets.<channel>.json` and `RELEASES` as ordinary outputs. A real 0.6.0 package
    contains all six. Required and allowed are now separate
    (`Get-OptionalReleaseAssetNames`, `Get-AllowedReleaseAssetNames`); the remote name gate
    uses the allow list, and the CI-artifact check applies the same required-plus-optional
    rule `Package.ps1` applies to its own output, so an unexpected file is still refused.
    Checksum, size and GitHub digest verification, the public-release edit refusal and the
    draft-continuation behaviour are unchanged. Before the fix, a six-file draft failed
    preflight with "Release contains unexpected assets; refusing to delete them:
    assets.win.json, RELEASES"; after it, the same fixture returns `Preflight`, and the
    matching public release returns `AlreadyComplete`.
  - `Release.Tests.ps1` now drives `Invoke-Release -Preflight` end to end with only
    `Invoke-NativeCommand` replaced, using a representative release response and a real
    staged artifact: six-file draft, six-file public (AlreadyComplete), four-file package,
    stray artifact file, stray release asset, public release missing a required file, build
    output missing a required file, wrong digest and wrong size. Every case asserts that no
    tag, draft, upload or publish command was issued. `Assert-FullPackageFileVersion` is the
    one stub, because a fixture cannot carry real PE version metadata; that check has its
    own coverage. No GitHub release was created or modified.
  - **Live build-local progress.** `Invoke-ExternalProcess` captured dev-run's stdout to a
    file and only read it back after `WaitForExit`, so the console was silent for the whole
    run. It now streams the stage lines out of the capture file while the child runs: the
    capture handle shares reads and is unbuffered, a 250 ms poll interval blocks in
    `WaitForExit` rather than spinning, a stateful UTF-8 decoder and a retained partial line
    keep a split character or line from being mangled or repeated, and each completed stage
    prints once. dev-run's cumulative stamp and the stage's own cost are printed as separate
    labelled numbers (`dev-run 00:02.5 elapsed | restore passed (stage took 00:02.4)`). The
    overall timeout, both-stream draining and the failure log tails are unchanged; a
    progress-read failure is reported as its own warning and falls back to the captured
    dump rather than being hidden or blamed on the build.
  - Regression: a synthetic child announces two stages and then blocks until a watcher
    confirms the in-flight stage is already on the parent console; the child is released
    only after that, so an end-of-run dump cannot pass. Without `-StreamProgress` the same
    test fails with "the parent console never showed the running stage". Split-UTF-8 and
    progress-fault cases are covered separately, alongside the existing 1 MB stderr flood
    and hanging-child timeout checks.
  - **Two further defects, found by Windows CI on `e39892d` and fixed.**
    - *Exit checked against a stale read.* Both report waits read the report file and only
      then check `HasExited`, so a child that writes its record and exits in that gap was
      reported as having exited without one. The push run failed exactly that way while the
      pull_request run on the same SHA passed — the failure message itself printed the race
      loser's `{"state":"busy","pid":8656}` from the file it had just called empty. An
      observed exit is now decisive only after a fresh look still finds nothing, in both
      `RunFirstLaunchRace` and `ChildProcessReportWait.WaitFor`.
      `WaitFor_ReturnsARecordThatLandedBetweenTheMatchAndTheExitCheck` makes the record
      appear in exactly that gap rather than waiting for a real child to hit it; without the
      fix it fails with the same "exited before" message CI produced.
      `WaitFor_StillFailsWhenAnExitedChildLeftNoRecord` keeps a genuinely empty exit a failure.
    - *Nested stage lines streamed as this run's progress.* The live-progress filter matched
      any `[mm:ss.d] name` line, but dev-run's `release-guard` and `build-local-regression`
      stages run nested scripts that print stage lines of that same shape from their own
      synthetic runs. The real `build-local.cmd` run showed nested stages interleaved with
      dev-run's own and reported `package-verify` passed eight times. dev-run now emits a
      `##dev-run##` marker alongside its unchanged human-readable line, and only that marker
      is streamed. The regression fixture emits both shapes and asserts the nested one is
      not reported. This was caught by the new entry-point assertion, not by a person
      reading the log.
  - **Timing claims.** The earlier "build 27 s + 4 s check, so a failure is known within
    30 s" framing is withdrawn: 4 s was a *passing* check, and tooling/restore time and a
    failing check's own timeout budget are not in it. The verifiable improvement is only
    that `--desktop-instance` now runs before the full unit suite and the rest of UiSmoke.
  - Verification: `dotnet test --filter ChildReportFileTests` (7 passed), `--desktop-instance`
    UiSmoke (passes; fails in 31.5 s with the guard removed), `tests/Release.Tests.ps1`,
    `tests/BuildLocal.Tests.ps1` (30 PASS), and the full `dev-run.ps1 -NoLaunch` gate through
    package-verify on this machine. The real `build-local.cmd` entry point runs only on the
    disposable Windows runner in the build-local entry point workflow; this developer machine
    is a working profile, so it was not run here.
  - Real entry point, run
    [35357386422](https://github.com/frozenvoice/cyclearc/actions/runs/35357386422) on
    `719d9a183aee9bce1e4f424c6f1131bb65ca3b2a`: passed. It drove `cmd /c build-local.cmd`
    with nothing injected — real `dev-run.ps1`, real `CycleArc-Setup.exe`, real managed
    install under `%LOCALAPPDATA%\CycleArc`. Build A installed and answered as the managed
    build (`3A0F588B…`, PID 9192); build B installed over it at the same version with
    different content (`817D58D6…`, PID 8076) and A's process was gone. A deliberately broken
    build then exited 1 through CMD at `Stage: build`, carried the failing sub-stage, the real
    compiler error and the captured log path to the CMD window, and left B still running and
    unchanged. The stages shown live in that CMD console were exactly dev-run's own thirteen —
    preflight, release-guard, restore, tool-restore, build, ui-smoke-desktop-instance,
    local-install-regression, build-local-regression, unit-test, ui-smoke-full, publish,
    package, package-verify — with no nested run's stages and none reported passed twice.
    The two earlier runs of this workflow, `35355737192` and `35355980076`, are what surfaced
    the nested-stage defect; they are not evidence for the fixed code.
- Desktop instance UiSmoke wait budget and fail-fast local gate (unreleased):
  `DesktopInstanceProcessChecks` allowed 10 seconds for a child report under
  `build-local.cmd` / `dev-run.ps1 -NoLaunch`, while the same `--desktop-instance` check
  passed immediately afterwards on its own. **Corrected:** this entry originally attributed
  the reported failure to a slow child that could not write `first.jsonl` in time. The
  user's stack does not support that — see the shutdown-report entry below for what the
  line numbers actually show. The budget change stands on its own as a budget change, not
  as the diagnosis. The process wait is now 30 seconds and stays separate from the
  3-second IPC request timeout. Redirected child stdout/stderr are drained asynchronously with a
  32 KiB snapshot; a wait timeout includes pid, HasExited, exit code when
  known, command line, both streams and the current report file, and the test still
  stops only the children it started. `DesktopInstanceProcessWaitTests` covers a
  synthetic child that becomes ready after the old 10-second budget, a real
  timeout whose message keeps stdout/stderr/pid/command, and a 64 KiB stderr flood
  that must not stall the wait. There is no retry-to-green.
  The local/CI gate is now fail-fast: after Release compile it runs
  `--desktop-instance` before LocalInstall/BuildLocal regressions, the unit suite
  and the rest of UiSmoke. Empty-args UiSmoke no longer repeats that process
  check. Each `dev-run` step prints `[mm:ss.f]` elapsed time and `Failed at:`.
  `Invoke-ExternalProcess` copies `dev-run` stdout/stderr to
  `artifacts/build-local/dev-run.out.log` and `dev-run.err.log` with a bounded
  drain; the child sub-stage is stored in `dev-run.stage` so a UiSmoke timeout is
  reported as `ui-smoke-desktop-instance` rather than `Stage: build`. A failed
  gate prints a tail and `last-failure.txt` includes `Failed at`, `Child log` and
  the captured stderr. Successful runs print the timing lines instead of dumping
  those logs. `scripts/Release.ps1 -Preflight` runs local, package and remote
  verification without creating tags, drafts or uploads; publish does not start a
  new build, test or packaging run. Linux can run
  `DesktopInstanceProcessWaitTests`, `tests/BuildLocal.Tests.ps1` and
  `tests/Release.Tests.ps1`. Windows CI is the executable/UiSmoke proof; this
  environment cannot build the WPF smoke project. `build-local.cmd` end-to-end
  and `Verify-InstalledUpdate.ps1` were not run here.
- Widget bind applies the arranged HWND size (unreleased): `FloatingWidget.BindAccounts` now
  calls the existing Relayout path when the arranged DIP size changed, so
  `FloatingWidgetController.Update()` grows or shrinks the same native window when accounts,
  period lines or status text change. Unchanged numbers skip a native resize. A drag defers
  that apply until pointer release. UiSmoke `BindingResizesTheShownWindow` drives 3→5→1→3
  through `Window.Show()` and `Update()` without calling Relayout/ApplyNativeSize after Update.
- One-click managed install (unreleased): root `build-local.cmd` runs `scripts/Build-Local.ps1`,
  which invokes `dev-run.ps1 -NoLaunch` in a separate `pwsh` process, then installs the
  produced `CycleArc-Setup.exe` with Velopack `--silent` (and `--installto` only for a
  non-default existing InstallLocation). Success requires `current\CycleArc.exe` SHA-256 to
  match this publish. `tests/BuildLocal.Tests.ps1` covers failed build (no Setup), leftover
  Setup refusal, same-version hash mismatch, Setup failure, lock overlap and paths with
  spaces. Real Setup.exe replacement against a developer profile was not run here.
- build-local.cmd double-click failures (unreleased): three defects that stopped the run before
  the build began or hid a stall.
  - `Invoke-BuildLocal` assigned the `dev-run.ps1` path to `$devRun`, which is the
    `[scriptblock]$DevRun` parameter under PowerShell's case-insensitive variable names, so the
    default branch failed with "Cannot convert ... System.String ... to ...ScriptBlock" before
    any build step ran. The path now uses `$devRunPath`; the type constraint is unchanged and no
    `Invoke-Expression` or coercion was added. Every earlier test injected `-DevRun`, so
    `tests/BuildLocal.Tests.ps1` now drives the default branch against a real `dev-run.ps1` for
    both success and a nonzero exit, and an AST check fails any local that shadows a parameter
    by spelling in `Build-Local.ps1`, `LocalInstall.ps1`, `Package.ps1` and `dev-run.ps1`.
  - `Invoke-WindowedProcess` always passed `-WorkingDirectory`, which `Start-Process` rejects
    when it is empty, and the ordinary Setup call named no directory. The parameter is now
    omitted when unset, a named directory must exist, the installer runs in its own directory,
    and the process object is still disposed and its exit code returned. Covered with real
    external processes, including a working directory containing spaces and Hangul.
  - `Invoke-DesktopStatus` / `Invoke-DesktopShutdown` called `ReadToEnd()` before
    `WaitForExit(timeout)`, so a stalled probe or a full stderr pipe never reached the timeout
    check. Both streams are now read concurrently, the wait comes first, output collection has
    its own budget, and only the probe process this script started is stopped; the cause, step
    and streams are appended to `artifacts/build-local/desktop-ipc.log`. Verified that the old
    ordering hangs indefinitely on a 1 MB stderr writer, and that the new code reports a
    timeout, kills only the probe, returns the status past 1 MB of stderr, and bounds output
    collection when a leftover child holds the pipe open.
  - Failure guidance now follows the stage the run reached (`preflight`, `build`, `package`,
    `stop-desktop`, `install`, `verify-install`, `start`). `build-local.cmd` prints
    `artifacts\build-local\last-failure.txt` instead of claiming the previous installation
    survived, and a failure after Setup.exe started says so.
  - `scripts/Verify-BuildLocalEntryPoint.ps1 -ConfirmDisposableEnvironment` and the
    `Windows build-local entry point` workflow run the real CMD entry point on a discarded
    GitHub-hosted runner. Run 35299780569 on a clean windows-latest runner (no pre-existing
    installation, data root, uninstall entry or CycleArc process) passed:
    - Build A: `cmd /c build-local.cmd` with no arguments and no injected scriptblocks ran
      `pwsh -NoProfile -File dev-run.ps1 -NoLaunch`, published `0.6.0.0` at SHA-256
      `569D223A7721C2ADE2A54D70876D935FC956BDD87EA87E3623CCA9E82E6B5B22`, started the packaged
      `CycleArc-Setup.exe --silent`, and left `%LOCALAPPDATA%\CycleArc\current\CycleArc.exe`
      at that same hash with the desktop ready as PID 7932.
    - Build B: the same entry point with one source file changed, so the version number stayed
      `0.6.0.0` while the executable became
      `F565F21405CEACAE2294066CB4792026950B28F5BFF91BADD0C9BC2CBDC4B34E`. It stopped PID 7932
      over desktop IPC before starting Setup.exe, and the installed `current\CycleArc.exe`
      then matched build B with a new desktop at PID 1444, verified to be under the managed
      install root rather than the development location. Running one Setup.exe twice would not
      have shown this; the two packages differed at the same version number.
    - Failure: a deliberately broken source file made `dev-run.ps1 -NoLaunch` exit 1. CMD
      received exit 1 and printed `Stage: build` with the real cause, the running installation
      was neither stopped nor replaced, and PID 1444 was still serving build B afterwards.
    Not run: this was never executed on a developer profile, and no failure injection or
    install/remove cycling was done there. `-Fast` was used for build B, so its unit suite came
    from build A's run. The workflow is `workflow_dispatch` only, which GitHub offers once the
    file is on the default branch; until then it has to be dispatched from a branch that
    triggers it.
  - `managed-setup-install` same-version repair (unreleased): the job ran `--desktop-shutdown`
    and started `Setup.exe` again immediately, checking only the shutdown command's exit code.
    It failed twice on `c5212bc` (run 35300856288, attempts 2 and 3) and passed on `3d64f19`,
    whose sources are identical, so it is a race rather than a code regression -- an earlier
    note in this session calling it intermittent was withdrawn once it reproduced.
    - Root cause, from the two captured `setup-repair.log` files: Velopack failed renaming the
      existing CycleArc directory with Windows error 5 (access denied). Something still held
      that directory. An earlier claim in this session that the single-instance mutex was the
      cause was not supported by those logs and is withdrawn.
    - The job now calls `Wait-InstallDesktopReleased` before repairing, which waits for the
      recorded desktop PID to exit and the single-instance mutex to be released. Both are
      necessary preconditions for replacing a live installation, and neither proves the
      directory can actually be renamed -- a handle this check cannot see can still deny it.
      The wait narrows the window; it does not establish or remove the cause.
    - A wait that times out throws and the job fails without starting `Setup.exe`, rather than
      running the installer over an installation that has not been released. It observes only
      and never terminates the desktop. `tests/LocalInstall.Tests.ps1` covers the released
      case, a still-held mutex (message names the mutex and says the installer was not
      started), and a still-running PID (message names the PID, and the process is verified to
      be left alive).
    - `setup.log` and `setup-repair.log` are printed to the job output on failure, because the
      artifact download was not reachable from the environment that diagnosed this and the
      cause was invisible without them. The job stays enabled; nothing here disables a check to
      hide the failure.

- Removal cleanup and installed-app update verification (unreleased, 2026-09-17):
  - Claude callbacks are now removed with the installation. `InstalledApp` registers Velopack's
    `OnBeforeUninstallFastCallback`, which runs `ClaudeUninstallCleanup` from the installed
    `current\CycleArc.exe`. Verified against Velopack 1.2.0: the uninstall command stops the app,
    runs `--veloapp-uninstall <version>` with a 60-second timeout, discards the result and then
    deletes the installation root, so the cleanup is time-boxed (20 seconds), never throws and
    never tries to cancel or retry removal.
  - Ownership for removal is stricter than for disconnection: `RestoreOwnedAsync` restores a
    statusLine wrapper or StopFailure hook only when the decoded wrapper matches byte for byte,
    names the profile, names the same configuration directory **and** names an executable inside
    the installation being removed. Disconnection keeps its existing profile-scoped behavior.
  - Accounts, settings, quota caches, connection bindings and Claude/Codex credentials are read
    but never rewritten or deleted, and the data root is not created when it does not exist.
    A receipt at `%LOCALAPPDATA%\ProMeter\claude-uninstall-cleanup.json` records per-profile
    outcomes; an incomplete or failed run is never recorded as completed, and it stores no
    command text, payload or address.
  - `ClaudeUninstallCleanupTests` covers an absent and a present previous statusLine, a statusLine
    the person replaced after connecting, another tool's hooks and unrelated properties, several
    profiles with a second installation's connection, a wrapper already migrated to a newer
    installation, damaged settings, a settings file held by another process, an unreadable
    connection record, repeated cleanup, installation-root ownership including a similarly named
    neighbour, and receipt privacy.
  - `scripts/Verify-InstalledUpdate.ps1` adds the missing installed-app end-to-end verification:
    three test builds with different versions and executable hashes (not an `sq.version` rewrite),
    a real `Setup.exe` installation, an update driven through the production update window,
    coordinator, Velopack client, supervisor and `Update.exe`, readiness confirmed over the
    desktop IPC status, a deliberate build that quits before readiness to exercise supervisor
    recovery, the installed Claude callback executed with synthetic input after each phase, and a
    real removal followed by the restoration and preservation checks. The failure injection and
    the isolated local feed exist only in the `CYCLEARC_TEST_E2E` / `CYCLEARC_TEST_FAIL_STARTUP`
    build flavours and are never compiled into a shipped build; HTTPS enforcement and package
    verification are unchanged.
  - Nothing was built or run in the environment that produced this change: a Linux container with
    no .NET SDK, whose egress policy blocks the SDK download host. Windows CI ran it instead.
    Run 162 on `76a5a24` passed every step: Release guard tests, `dotnet build CycleArc.sln -c
    Release`, the full `dotnet test` suite including the new `ClaudeUninstallCleanupTests`,
    production WPF checks, installer recovery tests, the single-file publish and its artifact
    assertion, the published Claude statusLine receiver, Velopack packaging and the packaged
    update validation. The first attempt (run 160) failed to compile, which is how the UiSmoke
    name collision and the blocking test call were found and fixed.
  - Review follow-up, two defects found by reading the added branches rather than by running
    Windows removal. First, the new profile projection returned an empty list both when a data
    root held no Claude profiles and when the account registry and its backup could not be read,
    so an unreadable registry was cleaned as "nothing to do", left the dead callback in place and
    wrote no receipt at all. `CodexAccountStore.ReadClaudeProfiles` now reports whether the
    registry could be read — absent files mean no accounts, unreadable files mean unknown — and
    the report carries an `IncompleteReason` (`InstallationRootUnusable`, `AccountsUnavailable`,
    `Interrupted`), so an unconfirmed list is recorded as incomplete with its cause. Receipt
    schema version 2. Regression tests cover a damaged registry and backup, a registry held open
    by another process, and an absent registry as a genuine empty list.
  - Second, the test-only update driver was read from an environment variable that the recovery
    helper and the desktop it restarts inherit, so a restored desktop would immediately apply the
    same failing update again and the recovery assertions would race that retry. The instruction
    is now consumed once, before anything can inherit it. Test-only build flavour; no shipped
    build contains that code, and no production update happens without a person approving it.
  - CI now compiles `CYCLEARC_TEST_E2E` and `CYCLEARC_TEST_FAIL_STARTUP` into a throwaway output,
    so the test-only code is type-checked on every push instead of first failing when someone runs
    the installed-app verification. It paid for itself immediately: both the new step and
    `Verify-InstalledUpdate.ps1` passed the two constants separated by a bare semicolon, which
    MSBuild reads as the end of the property (`MSB1006: Property is not valid`), so the failure
    build could never have been produced. Both now use the escaped `%3B`. Compilation is still not
    execution: it proves those branches build, not that the installed update, recovery or removal
    works.
  - Executed end to end on a GitHub-hosted `windows-latest` runner by the `Windows installed E2E`
    workflow, first passing on 2026-09-17 at 06:47 UTC (`8a37d22`, run 14). It runs on request
    only, from the Actions tab against a chosen branch, because it installs and removes a real
    installation, and passed again that way on the merged tree (`4208e6e`, run 16). The check that
    runs the installed callback now waits two minutes rather than twenty seconds: that callback is
    the first launch of a freshly installed self-contained single-file build, which unpacks its
    native libraries before reading input, and on a cold runner that outran the old budget. What that run actually
    did, in order: packaged three genuinely different builds (9.9.1 / 9.9.2 / 9.9.3, distinct file
    versions and distinct executable hashes, not an `sq.version` rewrite); installed 9.9.1 with its
    real `Setup.exe --silent`, which registered the `CycleArc` uninstall entry and both shortcuts;
    seeded a synthetic Claude connection through the production stores and ran the installed
    callback; drove the in-app update to 9.9.2 through the production coordinator, client and
    supervisor (pid 6852 → pid 2936, installed hash matching build B); applied 9.9.3, a build that
    quits before readiness, and watched the supervisor detect it and restore 9.9.2 (marker
    `completed: restored`, one desktop running the installed executable afterwards); then removed
    the installation with `Update.exe --uninstall --silent` and confirmed the Claude cleanup
    receipt `Completed: true, Inspected 1, Restored 1, Failed 0`, the accounts registry and Claude
    connection record unchanged, the data root still present and the shortcuts gone.
  - The failed-start scenario found a real defect before it passed: recovery abandoned the restore
    on Windows' first refusal to rename `current`, leaving an applied update with no working
    installation. `UpdateRecoverySnapshot` now retries those renames and the root-file copies for
    up to thirty seconds, and the supervisor records why a recovery failed beside the retained copy.
  - What this does not establish: installer packages remain unsigned; the update feed is a local
    directory, so no production release or GitHub feed was exercised; the Claude account is
    synthetic, so nothing here is evidence of live subscription usage; and the runner is a fresh
    throwaway user, so it says nothing about an installation that shares a machine with someone's
    real profile. For that, run
    `pwsh -NoProfile -File ./scripts/Verify-InstalledUpdate.ps1 -ConfirmDisposableEnvironment`
    on a disposable Windows VM or throwaway user.
  - Known limits of the new script: it cannot isolate the data root, the single-instance mutex,
    the desktop IPC pipe, the recovery root, the uninstall registry entry or shortcuts, so it
    refuses to run without `-ConfirmDisposableEnvironment` and when it finds an existing
    installation, data root or running CycleArc. Shortcut checks report what the current packaging
    configuration actually created rather than assuming one exists.

- Windows installer and in-app updates (0.6.0, 2026-09-17):
  - The final `dev-run.ps1 -NoLaunch` passed: 1,436 unit tests, all production WPF
    checks, 15 legacy installer scenarios, process/lease/shutdown checks, built and
    published Claude receivers, self-contained publish and Velopack packaging.
    Release build warnings/errors: zero. The actual running 0.5.9 installation was
    preserved; no real account or credential was used for these checks.
  - The production adapter discovered the generated stable feed, repaired a damaged
    cached full package, verified SHA-256 and size, rejected a same-size modification
    made after download, and recovered through explicit retry. Separate synthetic
    ProMeter settings/account sentinels remained unchanged.
  - Real Velopack `Update.exe` applied the full package in a disposable `.portable`
    installation. The prior manifest version was synthetic 0.5.9 with the current
    production binary; this is a filesystem upgrade fixture, not an installed 0.5.9
    to 0.6.0 end-to-end desktop session. The resulting manifest and executable hash
    matched the package. The external recovery snapshot restored the prior tree,
    marker, updater and launcher while its executable was held open for reading.
  - Recovery regressions cover missing/partial current, tampered/extra snapshot files,
    path redirects, size/count/depth limits, activation failure, root-file rollback,
    initial readiness failure, inability to stop the failed desktop, and failure to
    restart the previous desktop. Failed recovery retains the external snapshot.
  - Update views passed EN/KO and Dark/Light layout checks; four production previews
    were visually inspected. Opening/checking never downloads or applies implicitly;
    download requires a separate restart action. Cancellation waits for cleanup even
    after the window closes, and the app includes that operation in shutdown waiting.
  - Exact owned Claude wrappers migrate only after matching authenticated bindings,
    including missing old binaries; unrelated settings, prior statusLine output,
    binding generation and profile data are preserved. Malformed recovery-helper
    invocations exit before normal desktop or account initialization.
  - `Setup.exe` was packaged, not executed against the real Windows profile. Its
    registry/shortcut installation needs a disposable Windows profile for independent
    end-to-end install testing. Packages are unsigned; Velopack also emits an expected
    entry-point heuristic warning because its startup is delegated after the bounded
    headless routes. Actual Velopack before/after-update hooks passed the isolated apply.

- README and Claude preview refresh (2026-09-16):
  - Aligned both READMEs and the production connection/waiting/recovery guidance with
    Desktop-authenticated server refresh. Initial official CLI connection and Desktop
    quota authentication are separate steps; identity mismatch hides all profile quota.
    Removed the obsolete claim that the connection never reads Desktop credentials.
  - Replaced and visually inspected 20 affected PNGs in English/Korean and dark/light:
    server quota, Desktop history, waiting, connection and account management. All use
    production WPF views with synthetic profiles at 2x resolution. Server examples show
    Research at 46% / 11% with known resets; history examples show 91% / 47% with unknown
    resets and original observation time. No real account screenshot or request was used.
  - Moved the server previews into the documentation exporter. The full command produced
    36 images; the new `--claude-live-screenshots` command produced four. Full and focused
    exports now use the same Desktop-history source and unknown reset metadata.
  - Release build passed with zero warnings/errors, 88 related Claude tests passed,
    and all 246 mixed-provider WPF checks passed after updating existing copy assertions.
    This follow-up changes guidance and preview generation, not quota collection logic;
    no local publish, installation or credential access was performed.

- Claude Desktop live quota refresh (0.5.9, 2026-09-16):
  - Reproduced why the previous Desktop history fix was incomplete: the installed
    Desktop 2.110.0.0 normally polled at 15-minute intervals (5 minutes after recent
    usage-tray interaction), and its history writer also enforces a 4.5-minute minimum
    append spacing. CycleArc's local reread could not make manual or 5-minute refresh
    retrieve a newer server sample. The user's 27% / 9% versus cached 23% / 8% was a
    collection delay, not a model filter or percentage conversion error.
  - With explicit user approval, verified a read-only Desktop OAuth access credential
    in memory through the first-party profile endpoint, matched its email/organization
    fingerprint to the existing binding, then fetched quota. The profile response uses
    `account.email` and `organization.uuid`. The live endpoints are observed internal
    interfaces, not an Anthropic public compatibility contract. No Desktop credential
    was changed or refreshed, no cookies were read, and no model request was created.
  - The production C# reader/client independently returned 50% / 12% at
    `2026-09-16T22:09:22.310+09:00`, with reset timestamps, without writing the app's
    usage cache. This tests the real transport used by manual synchronization;
    deterministic manager/WPF tests verify manual button routing and single-flight behavior.
  - The final executable passed 1,388 unit tests, 246 mixed-provider WPF renders,
    all other WPF checks, 15 installer scenarios and the remaining installer/process
    checks, built/published Claude receiver checks, and a single-file publish. Build
    warnings/errors: zero. The first gate found and fixed malformed-response exception
    handling and binding-rotation stale data; the corrected unit suite passed in full.
    UI-only harness fixes were then verified with the affected WPF checks, and delivery
    resumed with `dev-run.ps1 -Fast` because production/unit code was unchanged.
  - Installed 0.5.9.0 through the transactional installer at
    `%LOCALAPPDATA%\Programs\CycleArc\CycleArc.exe`, SHA-256
    `21A0FAEA260B1FF323926FD65C9EE155BA7ECFF9252E709F554AB5396C146AD3`.
    The actual installed process fetched 50% / 12% at
    `2026-09-16T22:06:20.797+09:00`, with resets at 22:30 September 16 and
    02:00 September 18. Computer Use observed the live Claude usage page and the
    installed CycleArc card together, both showing 50% / 12%, with CycleArc marked
    Updated and last checked 22:06.
  - Without restarting or externally writing its quota cache, the same installed
    process automatically fetched again at `2026-09-16T22:11:23.824+09:00`:
    five-hour usage increased to 52%, weekly stayed 12%, and failure was null. The
    saved automatic interval was 5 minutes. This establishes real unattended automatic
    server refresh, not a repeated read of Desktop's older history timestamp.
  - The UI tool still cannot target CycleArc's taskbar-hidden tray window for a direct
    installed-button click; that exact UI gesture is not claimed as tested. Real source
    requests, installed startup/automatic refresh, and the production WPF button/manager
    dispatch are separately verified. Four new live UI documentation previews use
    synthetic accounts, never real account screenshots or authentication information.

- Claude Desktop quota receipt (2026-09-16):
  - Reproduced the delivery gap: the connected profile's statusLine inbox still held
    5-hour 4% / weekly 3% from September 12, while the actual Claude Desktop Code usage
    popup showed 15% / 7% with Opus 5 selected. Refresh previously only reread that inbox.
    Historical terminal tests did not establish Desktop Code receipt.
  - Claude Desktop 1.52386.3.0 wrote those actual percentages into its app-owned
    `plan-usage-history.json` version 2. Its organization matched the connected Pro
    identity through official `auth status --json`. No credential, cookie, conversation
    or transcript file was read. No CLI or Desktop model request was created for this test.
  - Added bounded Desktop history parsing, verified personal-subscription identity,
    generation-bound atomic caching and source selection by original observation time.
    Desktop history contains no reset timestamps; they remain unknown, including when
    the older CLI record has resets. Missing or invalid source data retains last-good values.
  - The final `dev-run.ps1 -NoLaunch` passed with zero build warnings/errors, 1,352 unit
    tests, 15 installer scenarios and the remaining installation/process checks,
    222 mixed-provider WPF renders, all other WPF checks, built/published Claude receiver
    checks and one self-contained executable. Replaced and visually inspected only the
    12 affected Claude overview/waiting/connection documentation images in EN/KO and
    light/dark. Those images deliberately use synthetic accounts and values.
  - Installed that validated executable through the existing transactional installer.
    The installed SHA-256 matched staging:
    `C5EA8CE4EF2CFE1DBFC7CECCDD4530F239E1548C379555AF059FDE3B84BA4D8C`.
    The actual running app created its own `claude-desktop-usage.json` with 15% / 7%
    and the exact Desktop observation timestamp `2026-09-16T11:18:44.627Z`;
    the old statusLine inbox remained untouched. Subsequent reads preserved that timestamp.
    This establishes receipt of real Desktop data by the installed app. Computer Use
    could read Claude's Code usage popup, but did not expose a targetable CycleArc tray
    window even after activation. Therefore an actual installed-flyout refresh click
    and a new Desktop conversation followed by UI refresh were not verified here;
    production WPF presentation and refresh transitions were checked with isolated fixtures.
  - This source is an observed Desktop-owned file schema, not a public quota API.
    CycleArc cannot force Desktop to fetch/write a new sample; manual refresh may wait
    for that write. Pro/Max identity is required because an organization-only history
    sample cannot safely identify an arbitrary Team member.

- Unified desktop installation and first-instance policy (0.5.8, 2026-09-16):
  - `dev-run.ps1 -NoLaunch` passed: zero build warnings/errors, 1,318 unit tests,
    installer recovery checks, production WPF checks, one self-contained executable,
    and built/published Claude statusLine/StopFailure receiver checks.
  - Added real child-process races with isolated mutex/pipe names. One owner remains;
    status, concurrent clients, activation, identity-bound shutdown and replacement pass.
    No production account data or tray registration is used by these fixtures.
  - Installer fixtures cover first install, missing directories, pre-stop hash validation,
    rollback, failed cleanup, exclusive leases, reparse rejection and interrupted journals.
    A failed cleanup preserves the pending journal and both executables. File retries span
    the bounded headless receiver lifetime; callbacks are never terminated for replacement.
  - Cross-process testing exposed Windows pipe authorization failures. The implementation
    now explicitly grants the current user's SID and checks that same SID on the client;
    it avoids the token-owner mismatch in [.NET CurrentUserOnly](https://github.com/dotnet/runtime/issues/123903).
    Unhandled diagnostic exceptions initially caused Windows error dialogs. The smoke
    harness now reports these failures through stderr/exit codes; temporary probes were removed.
  - Rendered and inspected the version-install action in Korean/English and light/dark,
    including complete button bounds and the running version in the flyout.
  - Actual authorized migration replaced desktop PID 51664 at the former primary worktree
    path with PID 25648 at `%LOCALAPPDATA%\Programs\CycleArc\CycleArc.exe`.
    Three later ordinary/autorun launches preserved its PID and instance nonce. Confirmed
    one desktop process, one CycleArc tray row and canonical `--autorun` registration.
    The obsolete tray row was backed up before removal. Installed SHA-256 matched staging:
    `354B7D0FC326165CEFA88955062577FB12E0EED748E2F1561F35AC3F81E4C886`.
    The old external executable remains available for existing absolute Claude callbacks.

- Existing desktop instance during installation (2026-09-15):
  - Reproduced a second launch of the installed executable while the first desktop process
    was running: the second process exited with code 0 and the original remained alive.
    `App.OnStartup` rejected the existing named mutex before application logging initialized;
    the installer's two-second startup check reported this as a generic startup failure.
  - The earlier installer stopped only the current and primary `publish/local` executable
    paths. An existing CycleArc desktop launched from another location could therefore
    survive replacement and reject the new executable. Passing the synthetic process tests
    did not establish that this installation scenario was covered.
  - Preflight now reports the existing desktop PID/path, and final installation rediscovers
    current-session desktops after validation. Verified CycleArc PE metadata covers other
    installation paths; exact legacy paths remain supported. The first argument separates
    headless receivers from desktops. Termination requires the captured process object and
    a matching executable path, then waits for exit and the disappearance of the named mutex
    before installation-directory changes. `-NoLaunch` still does not stop an app.
  - Regression checks cover outside-install identification, unrelated products, other sessions,
    all three quoted/unquoted callback arguments, misleading path/later-argument text, an
    unowned existing mutex, and actual test-owned process exit. PID-only and mismatched-path
    termination requests are rejected. Existing rollback and installation-layout checks passed.
  - Ran the full `dev-run.ps1` with the existing primary desktop already running. Preflight
    identified PID 26708; all 1,286 unit tests, WPF/receiver checks and single-file publish
    passed. The installer then stopped PID 26708 and launched PID 51664 from the same primary
    path. Confirmed one desktop process and one tray registration. The installed SHA-256
    `E9DEC5A366BB5EFFEC8AA480E3D27399F084ED8CE841A25C6FF8117E7C77F910` matched validated staging.

- Single development tray installation (2026-09-15):
  - The duplicate Windows tray-settings rows came from the primary checkout and a linked
    worktree's separate `publish/local/CycleArc.exe` paths. Only one desktop process was
    running; the existing mutex and NotifyIcon lifetime were working as intended.
  - `dev-run.ps1` now resolves the primary Git worktree and shares its existing installation.
    Build staging stays in the current checkout. After validation, a hash-checked copy to
    primary staging keeps replacement and rollback on the same volume. A shared lease
    prevents concurrent installs; only exact current/primary installation paths are stopped.
    Existing linked-worktree executable files remain available for headless callbacks.
  - Added isolated primary/linked Git, source-export fallback, malformed metadata, path-boundary,
    copy, nonempty staging, and lease checks alongside the 15 existing rollback scenarios.
    The full development run passed with zero build warnings/errors, 1,286 unit tests, all
    WPF/receiver checks and single-file publish, then launched the primary installation.
  - Backed up and removed only the obsolete linked-worktree tray registry entry. Confirmed
    one CycleArc registration and one running primary executable, preserved the primary
    visibility preference, and compared all unrelated tray entries before/after cleanup.
  - At `6e01466`, main CI passed but branch run
    [34970808842](https://github.com/frozenvoice/cyclearc/actions/runs/34970808842) failed
    the cancellation fixture's immediate directory deletion. Disposal masked the original
    assertion, so the log does not establish whether a child survived or filesystem teardown
    was still finishing. The fixture now captures a synthetic descendant's process handle
    before cancellation and requires exit within two seconds, followed by directory release
    within two seconds. Its normal lifetime is 30 seconds, so a leaked child still fails.
    Failure cleanup preserves the original assertion. Application process-launch code is unchanged.
    The corrected cancellation test passed 20 consecutive runs, followed by all 1,286 unit tests.
  - At `6883b7d`, main CI passed all stages and the branch's cancellation test passed, but
    [34972951115](https://github.com/frozenvoice/cyclearc/actions/runs/34972951115) exposed
    the same immediate-disposal assumption in `SuccessfulCommandAdapterLeavesDetachedChildRunning`.
    Its done-marker preceded actual process exit and its timed wait result was ignored.
    Teardown now retains the child's process handle, signals stop, awaits exit with a bounded
    kill fallback, and uses the same bounded directory release check. Its synthetic loop also
    expires after 60 seconds if setup fails before cleanup can take ownership.
    Both process fixtures then passed 20 consecutive runs each, followed by all 1,286 unit tests.

- CycleArc 0.5.7 release recovery (2026-09-15):
  - Compared all three failed Windows runs before retrying. Runs `34949182820` and
    `34949944075` reached the existing-statusLine output assertion; the second captured
    PowerShell's first-use module preparation. The Console-only fixture correction in
    `80e34d0` preserves the production four-second forwarding limit.
  - Run `34950974899` failed earlier, in
    `OfficialCommandAdapterHandlesWindowsBatchStatusAndCancellation`: the cancelled
    batch command's descendant still held the temporary working directory during disposal.
    `Process.Kill(entireProcessTree: true)` followed by the parent's `WaitForExit` does
    not establish descendant exit, as documented by
    [Microsoft](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-8.0).
  - Added bounded Windows job accounting for cancellation/error cleanup, preserving the
    existing process-tree kill fallback. Failed native cleanup is reported rather than
    silently accepted. Normal successful completion does not terminate detached children
    such as a login browser. Synthetic regressions cover a ready nested command's
    directory cleanup and a successful command's surviving detached child; the latter is
    explicitly stopped and reaped by the test.
  - Release publication now has a dedicated `scripts/Release.ps1` entry point. It requires
    successful results across every Windows push CI run at the intended commit, consumes a run's single-file
    artifact, and validates executable version, tag target and uploaded digests before
    publishing a draft. Release guards run locally and in CI; failed unit-test results
    are retained as a CI artifact.
  - Follow-up run `34965154816` passed all 1,284 unit tests, including the process cleanup
    regressions, but failed the generated manual PowerShell receiver assertion. A parallel
    run of the same commit passed; that success was not used to bypass the failed run.
    The assertion previously omitted exit/stdout/stderr, so this run alone does not prove
    the underlying cause. A local check with module auto-loading disabled then exposed
    PowerShell's internal Out-String dependency when piping stdin to a native process.
    Generated wrappers explicitly prepare Microsoft.PowerShell.Utility before starting
    the receiver, keeping module preparation outside its stdin deadline. They write UTF-8
    through Console directly, avoiding implicit output formatting while retaining the
    pipeline that waits for the GUI executable. Frozen old manual/v2 commands remain recognizable,
    including an old v2 command near the installation length limit. Process smoke checks
    cover disabled module auto-loading, Korean forwarded output, bounded stream draining
    and diagnostic output on failure. Production receiver deadlines are unchanged.
  - The final `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` passed with zero build
    warnings/errors, 1,286 unit tests, 15 installer scenarios, all production WPF/widget/DPI
    checks, single-file publish, and built/published Claude receivers in PowerShell and
    Git Bash. All receiver fixtures used isolated synthetic data.

- CycleArc 0.5.7 release preparation (2026-09-15):
  - Updated application/assembly/file versions, EN/KO period-selection controls and the
    91% five-hour / 47% weekly screenshot example. The 14 current detail previews from the
    shared-period change remain valid. Verified 39 local README document/image targets and
    corrected the full screenshot export count to 32.
  - Final versioned `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` passed: zero build warnings
    or errors, 1,283 unit tests, 15 installer recovery scenarios, 108 period-selection renders,
    existing WPF/widget/DPI checks, 99 tray renders, win-x64 single-file publish and
    built/published Claude receivers. This release preparation did not replace the running app.
  - The first release CI (34949182820, commit 60522ba) passed unit tests but failed the
    synthetic existing-statusLine output check. Its old assertion omitted the child exit
    code/output/error, so the cause was not established. Added those synthetic diagnostics
    and the projected failure kind; the affected local production-receiver check passed.
    Receiver deadlines and runtime behavior were not changed to bypass the failed check.
  - The diagnostic CI (34949944075) reproduced exit 0 with empty stdout, no projected auth
    failure and PowerShell's first-use module preparation message. The synthetic previous
    command used Out-String/ConvertFrom-Json/Write-Output inside the four-second forwarding
    budget. Replaced those cmdlets with Console stdin/stdout methods and an exact fixture
    five-hour marker check, retaining EOF/input/output and quota assertions without module
    initialization. Production receiver behavior and its limits remain unchanged.

- Shared usage-period selection (2026-09-15):
  - Added persisted Auto / 5 hours / Weekly selection shared by the detail ring, native
    tray and widget. Auto prefers a finite five-hour value, including zero, then weekly.
    Explicit selection falls back to a known window with its actual label and a visible
    explanation. Unknown values stay unknown; account data and receipt times are untouched.
  - Detail radio controls and the ring shortcut support native keyboard/accessibility
    actions. Selection only saves settings and rebinds the three surfaces. A failed save
    restores the previous selection; isolated tests cover this failure and backup recovery.
  - Final `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` passed: zero build warnings/errors,
    1,283 unit tests, 15 installer scenarios, 108 period-selection WPF renders, existing
    account/Claude/widget recovery/DPI checks, 99 tray renders, single-file win-x64 publish
    and built/published Claude receivers. Synthetic tests did not access live accounts.
  - Inspected EN/KO dark/light detail layouts and replaced the 14 affected documentation
    previews. Installed using the guarded rollback path; SHA-256:
    `33B2D71DECFA500165203E4FCF793B6701E7F5D793154A4C8F919FC01086E07F`.
    Read-only native captures confirmed that the currently selected weekly-only Codex
    account displayed 78% consistently in details, tray digits and widget. The saved widget
    position (8, 1321), topmost visibility and zero ordinary windows above it were preserved.
    Period switching was verified with synthetic Codex/Claude accounts, not by changing the
    user's selected account. Live captures remain ignored local artifacts.

- Maximum-size digits-only tray follow-up (2026-09-15):
  - Removed the percent suffix and its layout path. Number-style icons now fit the
    original Segoe UI Bold glyph proportions to size minus one pixel in both axes,
    leaving a half-pixel antialiasing inset. Background and taskbar-aware monochrome
    contrast remain unchanged, as does the optional ring rendering.
  - Updated settings and EN/KO guidance: 67 means 67% used. Unknown usage remains ?.
    Raster regressions cover maximum occupied extent and natural proportions for
    0/9/70/99/100, and reject solid strokes clipped by an icon edge.
  - Final `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` passed: zero build warnings/errors,
    1,272 unit tests, 15 installer scenarios, 99 tray renders, production WPF/widget/DPI
    checks, single-file win-x64 publish and built/published Claude receivers.
    Synthetic checks did not access live accounts or make model requests.
  - Visually inspected 16/24/32px dark/light icons and the affected EN/KO settings at
    normal/compact sizes. Installed through the guarded rollback path; SHA-256:
    `BAECDC63A37BA765F854E8E973F8ED14ED97D0D7A8F10F85DBA7D8E446DE0766`.
    Read-only native capture confirmed large white 73 digits with no suffix on the
    reporting PC's 24px taskbar. The widget stayed visible at (8, 1321), with no ordinary
    windows above it. Captures remain ignored local artifacts.

- Natural tray font proportions follow-up (2026-09-15):
  - Removed independent horizontal/vertical scaling from the percentage renderer.
    Segoe UI Bold digits and the smaller baseline-aligned % now keep their original
    proportions; the number is no longer stretched vertically to fill the icon slot.
  - Added raster aspect checks for 70% and 100% to catch a return to squeezed text.
    The existing 99 icon renders still cover both taskbar themes, 16/24/32px, unknown
    values, transparent monochrome text and the optional ring.
  - Visually compared the compressed, natural inline and lower-right-unit layouts,
    then confirmed the selected inline output in production renders. At 100%, the fixed
    native slot requires smaller text; preserving font shape takes priority over height.
  - Final `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` passed: zero build warnings/errors,
    1,272 unit tests, 15 installer scenarios, production WPF/widget/DPI checks, single-file
    win-x64 publish and built/published Claude receivers, using isolated synthetic data.
  - Installed through the guarded rollback path; SHA-256:
    `C4CBA8122E4CD1B82E2F271AFAEF2203E0BF1038E0347F0C18BA8C14610D1837`.
    Read-only capture of the reporting PC's 24px taskbar confirmed natural-proportioned
    71% text. The widget stayed visible at its saved (8, 1321) position with no ordinary
    windows above it. Screenshots remain ignored local artifacts.

- Monochrome percentage tray follow-up (2026-09-15):
  - Replaced the default number icon's colored backing with transparent, monochrome
    digits and a smaller baseline-aligned percent sign. Percentage glyphs preserve their
    height while fitting horizontally, including 100%, within the native notification slot.
    Unknown usage remains ?. The optional progress ring retains its existing rendering.
  - Text follows Windows SystemUsesLightTheme independently of the app's theme.
    System preference changes rerender the tray even when the app theme is fixed.
    The existing Dispatcher routing and icon disposal behavior remain unchanged.
  - Visually reviewed 16/24/32px number and ring renders on dark/light backgrounds.
    The initial uniform fit made 100% too small; height-preserving horizontal fitting
    corrected it. The native 16px slot still limits four-character spacing.
  - Final `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` passed: zero build warnings/errors,
    1,272 unit tests, 15 installer scenarios, production WPF/shutdown and widget/DPI checks,
    99 tray renders, single-file win-x64 publish, and built/published Claude receivers.
    EN/KO settings were visually checked in light/dark, normal/compact layouts. Tests used
    isolated synthetic accounts without model calls, live login or reset-credit consumption.
  - Installed with the guarded rollback path at `publish/local/CycleArc.exe`; SHA-256:
    `15A74610C426021DCD3F1EC47C6921BB4FAAE6DAA9B01424B7E3C20BB55DC613`.
    A read-only native screen capture confirmed white 70% text with no colored backing
    on the reporting PC's dark 24px taskbar. The widget remained visible at its user-saved
    physical position (8, 1321), with zero ordinary windows above it. Local diagnostic
    images remain ignored artifacts.

- Widget visibility and tray readability follow-up (2026-09-15):
  - On the reporting PC, the widget was enabled at 92% opacity, at physical position
    (60, 60), with native visible/topmost flags set, no minimization and no DWM cloaking.
    A read-only capture of its screen rectangle showed the underlying application instead.
    Walking the actual native window order found nine visible ordinary windows above it.
    The previous check trusted the topmost style bit and missed this disagreement.
  - Visibility recovery now also checks native stacking order. Explicit position reset
    replaces the widget window on save, preserving visibility preferences and account selection.
    This incident establishes a stacking-order failure; it does not establish a GPU defect.
  - The native regression moves a synthetic widget below an ordinary window, checks the
    stacking-order detector independently of the topmost bit, then verifies repair without
    activation. A healthy topmost sibling remains above on later checks. Windows rejected
    directly forcing the stale style bit with SetWindowLongPtr, so the test does not claim
    to recreate that OS corruption; the combined state was observed in the live diagnosis.
    Explicit reset checks verify HWND replacement, display preferences and input subscriptions.
  - Number-style tray icons fit bold glyph ink into an opaque rounded square at the Windows
    small-icon size. Normal refreshes with retained values keep the normal color; stale/error
    states stay amber, valid 100% is red, and connected Claude awaiting usage is neutral gray.
    The existing dark-centered ring option remains available. Settings and both guides explain
    the numeric basis and colors without describing Claude samples as live.
  - Final `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` passed: Release build with zero
    warnings/errors, 1,272 unit tests, 15 installer scenarios, production WPF/shutdown checks,
    native widget lifecycle/DPI checks on two monitors, 66 tray icon renders, win-x64 single-file
    publish, and built/published Claude receivers. The checks use isolated synthetic accounts;
    no model turn, live login or reset-credit consumption was used for verification.
  - Visually reviewed number/ring icons at 16/24/32px on light/dark backgrounds and the
    affected settings in English/Korean at normal/compact sizes, including the scrolled legend.
    On the reporting PC, the 144-DPI taskbar uses the expected 24px icon metric.
  - Installed through the guarded rollback path at `publish/local/CycleArc.exe`; the installed
    SHA-256 matches the validated artifact (`BDB0394B16C5D2CC9145D2E9C0CD385822333D06685DF94E3AFE27575DFCB0EC`).
    Read-only screen captures confirmed the installed widget and larger tray digits. Native
    diagnostics found zero ordinary visible windows above the widget; enabled/opacity/topmost/
    click-through preferences remained intact. Diagnostic images stay in ignored local artifacts.

- Follow-up review against `a87309c` (2026-09-15):
  - The original [Windows #123 attempt 1](https://github.com/frozenvoice/cyclearc/actions/runs/34856508275/attempts/1)
    failed `RealStderrContinuesDrainingAfterUtf8DiagnosticBudget` (Available vs TimedOut)
    and `ImportedRecoveryCancellationPreservesRegistryProfileSelectionAndCache` (Cancelled vs
    TimedOut): 1,256 passed and two failed. Its later green attempt did not itself fix either issue.
  - Shutdown now always reaches tray disposal and WPF shutdown after bounded work waits,
    even when work, cancellation callbacks, cleanup or logging throws. Isolated WPF child
    checks exercise the production exit path and verify mutex release before process exit.
  - Claude disconnection commits revocation before settings/inbox cleanup. Incomplete cleanup
    has a distinct warning; a failed binding save remains an unsuccessful disconnect.
    Overlong generated statusLine commands have a separate error with script-file guidance.
  - Codex JSONL reads buffer bytes and preserve read-ahead across lines. Positive whole-minute
    duration validation rejects fractional and overflowing values before conversion. Existing
    percentage clamping and unknown/optional-window behavior remain unchanged.
  - Codex login now maps cancellation/timeout results using the original caller token at each
    response stage. Cancellation just before a response wait cannot become a timeout, while
    the login deadline still reports TimedOut. The imported-profile regression preserves
    registry, selection, identity binding and last-good cache.
  - The large-stderr fixture exits after its final response is flushed, so production draining
    observes EOF before diagnostics are read. It still blocks protocol progress on writing
    more stderr than the pipe/diagnostic budget, exercising continued drainage. This is
    synthetic subprocess coverage, not a measurement of live account latency.
  - Final `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` passed on .NET SDK 8.0.424:
    Release build (zero warnings/errors), all 1,272 unit tests, 15 isolated installer scenarios,
    the five shutdown child scenarios, production WPF checks in both languages/all themes,
    native widget lifecycle/DPI checks on two monitors, single-file win-x64 publish, and
    built/published Claude statusLine/StopFailure receivers in PowerShell and Git Bash.
    The artifact contains only `CycleArc.exe`; the installed application was not replaced.
  - New connection errors were rendered and visually reviewed in English/Korean, Dark/Light/System,
    at 470x400 and 610x580. The footer remains readable; cleanup warning does not trigger auth,
    and failed binding persistence never appears as a successful disconnect. Checks use synthetic
    accounts and isolated data; no live login, model request or credit consumption was performed.
  - Connection-file backup recovery and legacy dependency separation remain separate design
    work. A stale identity backup must not revive a revoked binding. Per-callback identity
    verification and explicit recovery after authentication failure retain their existing policy.

- Release 0.5.6 audit corrections against `756d946` (2026-09-14):
  - CA-01: present malformed Codex windows fail as protocol mismatches, preserving the last
    verified sample and success time. Null/absent optional windows and unknown percentages
    retain their existing behavior.
  - CA-02: Claude v2 identity excludes plan metadata. Tests cover Pro-to-Max changes,
    deployed v1 files without a Plan field, verified migration, different email/organization
    rejection, and exact legacy rollback when duplicate-profile setup fails.
  - CA-03: obsolete automatic/manual callbacks cannot change the current quota receipt.
    Quota/failure commits share the connection mutation lease; tests cover lock waits,
    generation rotation, byte-identical quota/failure preservation and previous-command output.
  - Executable checks exposed an additional Git Bash limit: the old wrapper duplicated its
    encoded options and could lose the command tail above roughly 8 KiB. The compact wrapper
    carries options once, bounds generated command length, and still recognizes exact older
    wrappers for upgrade/restoration. Tests cover the generated command with connection generation
    and an existing statusLine command in PowerShell and Git Bash.
  - CA-04: Codex snapshot writes flush a same-directory temporary file before atomic replacement,
    retaining a valid backup. Tests exercise corruption, bounded/schema-checked loading,
    injected flush/replace failures and temporary-file cleanup with isolated data.
  - CA-05: account projection reads only in-memory identity state. File-lock contention does
    not delay projection; refresh still detects changed bindings. Durable conflict writes run
    outside the shared projection lock and remain separate from rendering.
  - The NuGet audit initially found `SQLitePCLRaw.lib.e_sqlite3 2.1.6` affected by
    [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q).
    Pinning the compatible native bundle to `2.1.13` removes that resolution. A repeated
    `dotnet list CycleArc.sln package --vulnerable --include-transitive` reports no known
    vulnerable packages in all five projects using the configured NuGet source.
  - Windows CI now uses Node 24 actions: [checkout v5](https://github.com/actions/checkout/tree/v5),
    [setup-dotnet v5](https://github.com/actions/setup-dotnet/tree/v5), and
    [upload-artifact v6](https://github.com/actions/upload-artifact/tree/v6).
    The repository description now reflects Codex/Claude support and reported windows.
  - Release build passed with 0 warnings/errors; all 1,258 unit tests and 15 isolated installer
    scenarios passed. These regressions use synthetic identities, callbacks and filesystem
    failures; no live login, quota request or reset-credit consumption was performed.
  - The final `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` gate also passed production WPF
    checks in Korean/English across all themes, native widget/popup interaction and lifecycle/DPI
    checks on three monitors, and built/published Claude statusLine and StopFailure receiver
    checks in PowerShell and Git Bash. The self-contained win-x64 artifact contains only
    `CycleArc.exe`.

- Release 0.5.5 Codex account identity isolation (2026-09-14):
  - On the reporting PC, separate official App Server reads for the imported default home and
    the managed second profile returned the same projected identity, weekly usage and zero
    reset credits. Removing inherited Codex host environment variables did not change that result.
    The prior service accepted a changed login while retaining the old nickname.
  - A separate hash-only binding now rejects passive account changes, validates cached identity
    after restart, preserves last-good data on cancellation/sign-out, and prevents cross-account
    stale fallback after an explicit login. Binding transactions are locked; verified login repairs
    damaged binding metadata without reading credentials. Startup failures before account/read retain
    already verified data as stale; missing or malformed account identity still hides quota.
  - Imported/managed identity conflicts remain visible with unknown quota and blocked reset-credit
    actions. Reconnection verifies official login and quota in an isolated home before replacing
    the registered link; failed, cancelled and duplicate-login attempts preserve it.
  - Full local gate passed: 1,210 unit tests, 15 installer scenarios, production WPF checks across
    Korean/English and all themes, native widget/popup checks, self-contained win-x64 single EXE,
    and built/published Claude receiver checks. New coverage includes 22 unit cases and 18 WPF
    identity/reconnection scenarios. Inspected the affected account-management and popup renders.
  - Live recovery through the production account manager and official browser login succeeded:
    the main profile reported 39% used / 61% remaining and three reset credits; the two Codex
    profiles had distinct projected identities. Nickname, list position, logical selection and all
    other profiles were preserved. No authentication file, token, prompt or conversation was inspected.

- Release 0.5.4 Claude authentication recovery (2026-09-14):
  - On the reporting PC, official local authentication status reported signed in with the
    connected identity, while actual requests from the installed npm 2.1.105 and Desktop-bundled
    native 2.1.270 CLI failed with OAuth expiry. The user completed official browser login;
    a subsequent native CLI response delivered real five-hour 1% and weekly 4% quota.
    This verifies that runtime in an interactive terminal, not Desktop's embedded Code transport.
  - Added the official StopFailure receiver in the same executable. It records only a
    bounded error category and binding metadata in a separate health file. The existing quota
    cache, last-good values and receipt timestamps are preserved. Signed-in local metadata
    cannot override an actual authentication failure.
  - Authentication and identity errors require explicit recovery; a later statusLine callback
    can repeat cached percentages and is not proof of repaired authentication. Generic request
    failures have separate retry guidance. No quota is inferred from an error or missing input.
  - Same-profile reauthentication uses the existing CLI/configuration mode, rejects a different
    identity, preserves profile metadata and rotates a binding generation. Existing owned
    settings are upgraded without replacing another tool's hooks or user-edited statusLine.
  - Final `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` passed restore, Release build
    (**0 warnings/errors**), **1,188 unit tests** (including **120 Claude tests**), **15 installer
    scenarios**, **786 WPF renders**, **4 widget/popup interaction scenarios**, native widget
    lifecycle/DPI checks on **2 monitors**, and built/published statusLine and StopFailure
    receiver checks. The win-x64 self-contained artifact contains only `CycleArc.exe`.
  - Synthetic regressions cover signed-in metadata plus actual authentication failure,
    cached callback persistence, generation/lock races, old-account quota isolation, corrupt
    failure-cache recovery, exact command ownership, frozen v0.5.3 setup migration, same-profile
    login/cancellation/rollback, and preservation of user-edited statusLine and original empty hooks.
  - Visually inspected authentication-failure main and connection views in English/Korean and
    Dark/Light themes. The login action and preserved/unknown usage are readable without clipping.

- Release 0.5.3 popup activation (2026-09-14):
  - Widget clicks now explicitly open/activate the popup. An unpinned window covered by
    another app remains visible to WPF, so the former toggle hid it on the first click.
    Minimized popups restore before activation. Explicit close and tray toggle still close.
  - Four production-event scenarios cover English/Korean and Dark/Light, including the
    active native HWND, minimized restoration, repeated/pinned clicks, close/reopen and
    tray toggling. Fixtures use synthetic accounts and verify no quota request or selection
    change. The old binding failed the covered-popup assertion; all corrected cases pass.
  - The linked Windows run failed its first attempt in the large-stderr test and passed
    a retry. Thirty isolated old-code runs also passed locally, so the exact CI timeout
    cause was not reproduced. Replaced per-chunk character decoding/recounting with bounded
    byte capture while continuing to drain the pipe. Startup/request deadlines are unchanged.
  - Added deterministic completion-order and delayed-drain regressions: the old code returned
    before capture finished; the new client waits within a fixed bound and omits diagnostics
    if capture is still pending, avoiding a mutable-buffer race. The existing real-process
    Korean flood test now checks that its bounded text contains no split UTF-8 replacement.
    All **13 App Server client tests** passed, including the **2-second** delayed-drain case.
  - Final `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` passed restore, Release build
    (**0 warnings/errors**), **1,160 unit tests**, **15 installer scenarios**, **762 WPF
    renders**, the **4 popup interaction scenarios**, native widget/DPI checks on **2 monitors**,
    and built/published Claude bridge/receiver checks. Win-x64 self-contained publish contains
    only `CycleArc.exe`.
  - Visually inspected four reopened-popup previews. Existing documentation images remain
    representative because the layout is unchanged.

- Release 0.5.2 tooltip and Claude connection guidance (2026-09-14):
  - Reproduced the account tooltip failure in an open WPF popup: the system light background
    (`#F1F2F7`) and app dark-theme text (`#EEF1F6`) had insufficient contrast.
    Tooltips now use the app's foreground/background/border resources, wrap plain strings,
    cap dimensions at 360 × 300 DIPs, and scroll exceptionally long content. Existing rich
    content and click-open reset-credit help retain their behavior.
  - Added **36 open-popup checks** across English/Korean and Dark/Light/System, covering
    real account/avatar tooltip text, long email/message wrapping, visible rich content,
    contrast and resource changes while open. The pre-fix Codex tooltip failed the
    contrast assertion; the corrected template passes.
  - Claude waiting/setup text and the launch button explicitly identify the connected
    terminal CLI. Desktop Code is outside CycleArc's supported receipt path. Corrected
    the old setup-only five-minute stale claim to match existing idle receipt persistence;
    existing provider behavior and tests remain intact.
  - The reporting PC had a connected binding and statusLine command but no projected receipt.
    Only binding/setup and receipt-presence metadata were inspected. No credential files,
    conversations or model requests were used, and no live Claude quota receipt is claimed.
  - `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` passed restore, Release build with
    **0 warnings/errors**, **1,158 unit tests**, **15 installer scenarios**, **762 WPF renders**,
    native widget lifecycle/DPI checks on **2 monitors**, and built/published Claude bridge
    and headless receiver checks. Self-contained win-x64 publish contains only `CycleArc.exe`.
  - Inspected English/Korean dark/light Claude waiting/connection views and tooltip previews.
    Updated the eight affected documentation images only; other previews remain unchanged.

- Release 0.5.1 integration and widget fixes (2026-09-14): the Claude-enabled 0.5.0
  source and the two fixes previously delivered on the Codex-only main line are merged.
  Main and the new release now share the Codex/Claude account-management implementation.
  The widget always displays the selected account's nickname, reported email or existing
  provider/profile fallback, including when only one profile exists.
  - Added **60 identity renders** across both languages/all themes, including Claude email
    and profile fallbacks, blank names, quota states, long-email ellipsis/tooltips and removal.
    The retained history fixtures share a deterministic clock across parsing, scans and restart.
  - Mixed-DPI native testing exposed WPF activating a widget during its nested DPI resize.
    A synchronous, current-UI-thread activation guard covers settings/position updates,
    minimized restoration and controller-owned closure. Native regression checks use a
    separate active-window fixture, reproduce the nested resize on single-monitor desktops,
    and verify foreground/thread activation, zero transient widget activations and guard
    cleanup after successful and exceptional updates.
  - `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` passed restore, Release build with
    **0 warnings/errors**, **1,158 unit tests**, **15 installer scenarios**, **726 WPF renders**,
    native widget lifecycle/DPI checks on **2 monitors**, and built/published Claude bridge
    and headless receiver checks. The win-x64 self-contained output contains only
    `CycleArc.exe`.
  - Six widget previews and English/Korean dark/light account-management/guide previews
    were visually inspected using synthetic accounts. Existing documentation images still
    represent the layout. No real login, quota query or model request was performed.
    Installation/connection on the reporting PC remains a live-environment check.

- Release 0.5.0 fixture clock correction (2026-09-13): 23 retained history tests failed
  when their fixed September 6 samples aged out of a scan using the real system clock.
  The affected schema-health, primary-index and isolated-failure harnesses now inject
  the existing `MutableClock` at their snapshot reference time, including restarts.
  All 56 tests in those three classes passed with the original assertions preserved.
  This changes synthetic test setup only; runtime collection and real data are untouched.

- Widget visibility recovery (2026-09-13): `FloatingWidgetController` keeps the enabled
  widget's native window synchronized with its saved preferences and selected account.
  Native hide/minimize/topmost loss is checked by the existing two-second timer. Closed
  windows recover with fresh event subscriptions; coalesced resume/unlock/display callbacks
  replace the transparent window without losing position, opacity or interaction settings.
  - `./dev-run.ps1 -NoLaunch` passed restore, Release build (0 warnings/errors), **1,158 unit
    tests**, 15 installer scenarios, **666 WPF renders**, built/published Claude receiver
    checks and the new widget lifecycle checks in both languages/dark/light on **3 monitors**.
    The single-file win-x64 executable SHA-256 is
    `6BAE3471E5CC2C874D506985FAD5184403890661B9E4FBE2230ED631878153C3`.
  - Native checks cover hidden and minimized HWNDs, topmost loss, unexpected closure,
    repeated recovery, per-window click handlers, account disappearance/reappearance,
    disable during queued recovery, disposal and keyboard focus preservation. Minimized
    rectangles do not replace saved coordinates. Desktop callbacks are dispatched and
    disposed safely. Four recovery previews were visually inspected; existing documentation
    images still represent the unchanged layout.
  - The reported widget retained on-screen bounds and native visible/topmost flags at
    inspection; its original disappearance trigger was not captured. Tests use synthetic
    windows and simulated callbacks, without changing the user's power/session state.

- Claude elapsed-reset receipts (2026-09-13): a passed reset no longer marks a received
  sample stale or raises attention. This supersedes the elapsed-reset warning retained in
  the 2026-09-12 idle-receipt change below. Original percentages, reset times and receipt
  timestamps remain visible; real input/cache/identity failures and invalid receipt times
  still warn.
  - `./dev-run.ps1 -NoLaunch` passed restore, Release build (0 warnings/errors), **1,158 unit
    tests**, 15 installer scenarios, all **666 WPF renders**, and built/published headless
    receiver checks. The single-file win-x64 executable SHA-256 is
    `A19C9E28D9F1D3D3665972070F99E40332F2F64EE8536DC4C186F44964D088C5`.
  - Regression checks cover five-hour/weekly reset boundaries, 400-day receipts, unchanged
    polling/cache/restart state, weekly-only samples, tooltip limits and real input-failure
    recovery. The 186 mixed-provider renders include neutral elapsed-reset cards/widgets,
    zero attention for time alone, and retained warnings for actual malformed input.
  - English/Korean dark/light popup previews and two widget previews were visually inspected.
    Read-only projection of the existing Claude receipt returned Available after its reset,
    preserving both quotas and the original receipt with an unchanged cache hash. No model
    response was requested, and existing documentation images remain representative.

- Claude idle receipts (2026-09-12): elapsed idle time no longer marks a valid sample stale
  or raises attention. This supersedes the five-minute cutoff in the earlier checks below.
  Last-good percentages and original receipt timestamps remain unchanged; elapsed resets,
  malformed/missing input and invalid receipt/cache/identity data retain their warning paths.
  - Regression checks cover 4:59, 5:00, 12 minutes, one/four hours and a weekly-only sample
    after six days, repeated polls, manual reads and restart. Actual receipt failures still
    retain saved values as stale until valid input arrives; reset and clock rollback boundaries
    remain covered, including stable account visibility and original timestamps.
  - `./dev-run.ps1 -NoLaunch` passed restore, Release build (0 warnings/errors), **1,151 unit
    tests**, 15 installer scenarios, all **654 WPF renders**, and built/published headless
    receiver checks. The single-file win-x64 executable SHA-256 is
    `7F782F1BA5CB127611F6A586CB6BAFE2126BC7428C4C0CB3956BFA814DF553B8`.
  - The 174 mixed-provider renders include neutral idle receipts without attention, retained
    receipt times on cards/widgets, explicit elapsed-reset reasons, and recovery after a new
    sample in both languages/all themes. Normal and elapsed-reset previews were visually
    inspected; only the four affected Claude documentation PNGs were replaced, with matching
    hashes. All fixtures are synthetic; these checks did not request model responses.

- Conditional Codex five-hour display (2026-09-12): the seven added unit cases cover
  unknown weekly values with known five-hour values in available/stale/refreshing states,
  unknown/non-finite percentages and windows arriving/disappearing. **144 Codex-window WPF
  renders** cover five-hour-only, both windows, reversed slots, weekly-only and partial
  percentages across English/Korean, all themes and 80/100/150% popup zoom. These use
  synthetic [official App Server](https://learn.chatgpt.com/docs/app-server) response shapes
  with Plus, Pro and absent plan metadata; **no live Plus account was available**.
  - Eight popup/widget preview images were visually inspected. Run the focused exporter with
    `dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release -- --codex-windows artifacts/codex-windows`.
  - `./dev-run.ps1 -NoLaunch` passed restore, Release build (0 warnings/errors),
    **1,150 unit tests**, 15 installer scenarios, all 630 WPF renders and built/published
    headless receiver checks. The single-file win-x64 executable SHA-256 is
    `E6BA517313530E28D73DE6E7DCB45B43EF80E5718E3F014B92B413D108CE1204`.
  - The existing Claude five-minute stale threshold and original receipt timestamps remain
    unchanged. These checks did not request model responses or modify real account data.

- Explicit Claude stale display (2026-09-12): `./dev-run.ps1 -NoLaunch` passed restore,
  Release build (0 warnings/errors), **1,143 unit tests**, 15 installer scenarios,
  120 account, 150 mixed-provider, 180 widget and 36 resource WPF renders, plus built
  and published headless receiver checks. The single-file win-x64 executable SHA-256 is
  `8C2BA8F6A5A446970194F0D973ED5722A58A4EA26D4CFA26C1C15A86261DB84F`.
  - Regression checks cover the 4:59/5:00 stale boundary, unchanged values/timestamps
    during repeated reads, restart and new-sample recovery. Two new language cases
    retain an old receipt's full date/time and shared scope inside the 127-character
    native tooltip even with a very long nickname. Codex labels remain unchanged.
  - WPF checks verify explicit stale text, warning colors with at least 4.5:1 contrast
    against actual card/hover backgrounds, dated receipts, ring color and removal of
    emphasis after a new sample. Four changed documentation previews and six widget
    renders were visually inspected; only the four affected documentation PNGs were
    replaced, with matching hashes. The guides passed 41 local link/anchor checks.
  - The validated executable was installed locally and restarted; the running process
    was responsive and its hash matched. Read-only connection verification still found
    3 visible profiles, 1 connected Claude account awaiting its first actual quota,
    and 0 attention items. UI fixtures were synthetic. No new query, model request,
    scraping, login change or receipt-renewing refresh was introduced.

- Claude shared subscription meaning (2026-09-12): reviewed official help, statusLine,
  CLI, Agent SDK and organization API documentation; the installed CLI was 2.1.233.
  No supported independent personal-subscription quota query was identified. The
  [research record](CLAUDE-USAGE-RESEARCH.md) separates shared quota from its Code
  delivery source. The app retains the passive receiver and adds manual usage-page access.
  - `./dev-run.ps1 -NoLaunch` passed restore, Release build (0 warnings/errors),
    **1,141 unit tests**, 15 installer scenarios, 120 account, 138 mixed-provider,
    180 widget and 36 resource WPF renders, plus both built and published headless
    receiver checks. The single-file win-x64 executable SHA-256 is
    `E9389D5D8CCAF19BC9925CD3F57789202F24FEF8DAFE3B5CC54B1A73D4EF9B32`.
  - Two added language cases verify that recent Claude samples say Received and
    describe shared subscription scope without changing Codex semantics. WPF checks
    exercise source guidance for recent/stale/waiting values, provider-scoped buttons,
    browser-account guidance, and a usage-page click that leaves displayed usage intact
    and invokes neither refresh nor Claude login. Existing tests cover unchanged
    receipt timestamps during polling, resets, waiting connections and disconnection.
  - All 24 production documentation previews were generated with synthetic data,
    visually inspected and copied with matching hashes. The five user/research/image
    guides passed 41 local link/anchor checks and balanced table/details checks.
  - The validated executable was installed locally and restarted with a matching hash
    and responsive process. Read-only official login inspection confirmed 3 profiles,
    1 connected Claude account, 3 visible accounts and 0 attention items. Claude still
    has no real received quota sample and correctly remains Awaiting usage. No model
    request, browser scraping, credential extraction or account-setting change was used
    to obtain a value. The usage-page action was exercised through an offline opener;
    no authenticated browser quota reading is claimed.

- Claude connected visibility and duplicate drafts (2026-09-12): `./dev-run.ps1 -NoLaunch`
  passed restore, Release build (0 warnings/errors), **1,139 unit tests** (9 added cases),
  15 installer scenarios, 120 account, 138 mixed-provider, 180 widget and 36 resource WPF
  renders. Built and published automatic/legacy headless process checks passed with
  isolated synthetic input. The win-x64 self-contained single-file SHA-256 is
  `BE3AC06EE60E2AAB66B75E9B37F7D40A0D7C0FC88A5BD17FE0A00BEB81F933DC`.
  - Connected Claude profiles now remain visible before the first sample, with unknown
    limits and Awaiting usage, excluded from attention totals. Tests cover restart,
    disconnection, duplicate current-login reuse, distinct configuration folders, draft
    cancellation/failure and preservation of existing or unreadable connection/usage data.
    WPF checks also cover reentrant Add and actions redirected to a reused profile.
  - English/Korean documentation and **24 production WPF previews** were updated and
    visually checked, including the connected-waiting state and the web/desktop limitation.
    All preview data is synthetic; generating previews does not access a real account.
  - Local installation matched the validated hash and the restarted process was responsive.
    Two verified empty draft references were removed after a registry-only backup, preserving
    the existing Claude connection and both Codex profiles. Read-only official CLI inspection
    confirmed one matching connected Claude profile, three visible accounts, zero empty draft
    references and zero attention items. No authentication settings or quota files were changed
    during cleanup. Actual Claude usage remains absent because web-only chats do not feed this
    statusLine integration; no model turn was launched or live quota sample fabricated.

- README and screenshot refresh (2026-09-12): English/Korean guides now describe automatic
  Claude login/connection, hidden pending profiles, first-sample appearance, stale usage,
  disconnection and Release single-file installation. Obsolete manual-JSON steps and old
  account-menu labels were removed. The documentation exporter generated **20 production
  WPF PNGs**, all visually checked, including the two-ready/three-registered comparison,
  mixed Claude usage and automatic connection in both languages and Dark/Light themes.
  Synthetic identities and an in-memory connection adapter were used; no live account,
  browser login, Claude settings or credentials were accessed for the previews.
  - All 36 local links/anchors in the two READMEs, Claude guide and image guide resolved;
    HTML table/details tags were balanced and all 20 PNGs matched the reviewed exports.
  - `./dev-run.ps1 -NoLaunch` passed restore, Release build (0 warnings/errors), 1,130 unit
    tests, 15 installer scenarios, 120 account, 132 mixed-provider, 180 widget and 36 resource
    WPF renders, plus built/published automatic and legacy headless process checks.
    Single-file publish SHA-256:
    `CD3F21DE042D0D0C3DB717A03E4D2425E414609AE3A51AD742AC5257851ACDD4`.
    This change affects documentation and its preview exporter; production behavior is unchanged.

- Connected-account visibility (2026-09-12): `./dev-run.ps1 -NoLaunch` passed restore,
  Release build (0 warnings/errors), **1,130 unit tests** (11 added regression cases),
  15 installer scenarios, 120 account WPF renders, 132 mixed-provider WPF renders,
  180 widget DPI/layout renders and 36 resource/layout renders. Both built and published
  automatic/legacy headless entry points passed the isolated process checks.
  The single-file win-x64 `CycleArc.exe` SHA-256 is
  `19B23C169183BD0611C34C37ACC2648D041CC78FE8337FE9E7C3EEDD48797C64`.
  - Tests cover hidden pending profiles, valid zero and stale usage, selection fallback,
    first-sample appearance, disconnected Codex retries and Claude disconnection across
    restart/reconnection while retaining the last good cache. Account management retains
    pending profiles and their connection actions. Empty popup and tray state is generic,
    with the widget hidden until a usable account becomes available.
  - All WPF/process checks passed again after correcting the screenshot helper's pending
    layout measurement. Korean-dark connected and English-light empty previews were
    visually checked with synthetic data.
  - A local projection of existing registry/quota caches found 3 registered profiles and
    2 visible Codex accounts, with the pending Claude profile excluded and no attention
    items. This check did not invoke authentication or launch a model turn.

- Claude automatic login/connection (2026-09-12): `./dev-run.ps1 -NoLaunch` passed restore,
  Release build (0 warnings/errors), **1,119 unit tests** (25 connection regression cases),
  15 installer scenarios, 120 account WPF renders, 108 mixed-provider WPF renders,
  180 widget DPI/layout renders and 36 resource/layout renders. Built and published
  automatic/legacy headless entry points passed their isolated process checks.
  The single-file win-x64 `CycleArc.exe` SHA-256 is
  `C956359DBBD2DBE6B21B5B441D8DCE84DCFB2031AF232D3209F369915CEA98F8`.
  - Tests cover official auth metadata parsing, Windows npm CLI invocation/cancellation,
    single-flight login/inspection, isolated login folders, setting preservation/rollback,
    idempotent installation, existing output/stdin forwarding, owned-command restoration,
    concurrent settings edits, legacy-command upgrade and identity/configuration changes.
    UI checks exercise current-login connection, verified email, progress/cancellation,
    duplicate-login suppression and 470x400 layouts in both languages and all themes.
  - Live existing-login connection succeeded through the installed official Claude CLI:
    signed-in Pro status, matching local binding and automatic statusLine installation
    were verified. Unrelated Claude settings matched the preserved backup. The installed
    executable hash matched the validated artifact and the restarted app was responsive.
  - The live check caught an official CLI distinction between an unset `CLAUDE_CONFIG_DIR`
    and explicitly assigning the usual `.claude` folder. The connection now preserves that
    mode in metadata; a regression checks the child environment for both cases.
  - No credentials were inspected, browser reauthentication forced or model turn launched.
    Actual quota receipt remains pending the next normal Claude response. New browser-login
    completion was exercised with synthetic adapters, not a second live OAuth sign-in.
    The six connection previews were visually checked with synthetic identity data.

- CycleArc source/project naming (2026-09-12): `./dev-run.ps1 -NoLaunch` passed restore
  of all five renamed projects, Release build (0 warnings/errors), **1,094 unit tests**,
  15 installer recovery scenarios, 120 account WPF renders, 108 mixed-provider WPF renders,
  180 widget DPI/layout renders and 36 production resource/layout renders. Both the built
  and published Claude statusLine entry points passed the isolated process checks.
  The win-x64 self-contained publish contains exactly `CycleArc.exe`, SHA-256:
  `980B96796D45BA0075B46F75F508D40993A6128E52C693B7A501E0B8C3F6C6A5`.
  - The solution, five project directories/files, C#/XAML namespaces, core/test assemblies,
    retained host, development launcher, Windows CI paths and documentation use CycleArc.
    Existing identity assertions now check `CycleArc.Core`; legacy startup/rollback cases
    continue checking the exact previous executable and registry names.
  - A case-insensitive source audit found no old-name filenames or documentation references.
    Remaining old-name literals are limited to five compatibility/installer/test files.
    Existing settings, profile/cache paths, mutex and previous executable recognition remain.
  - All 24 local Markdown/HTML documentation links resolve. The 12 documentation PNGs were
    regenerated from the renamed production views using synthetic accounts and visually checked.
    No interactive sign-in, credit redemption or live Claude session was performed for this rename.

- Claude statusLine provider (2026-09-12): `./dev-run.ps1 -NoLaunch` passed restore,
  Release build (0 warnings/errors), **1,094 unit tests** (47 added cases), 15 installer
  scenarios, 120 existing account WPF renders, **108 mixed-provider WPF renders**,
  180 widget DPI/layout renders and 36 existing production renders. Single-file,
  self-contained win-x64 publish contains exactly `CycleArc.exe`. Validated SHA-256:
  `6AA22206E90816B7C01E00D06EEF83075AFE22D11C805315986D51751B66CC3F`.
  - Provider adapters preserve the Codex client/service and caches. Regression cases cover
    mixed values, aliases, order/selection/restart, passive refresh without Codex calls,
    Claude rejection of Codex login/credit actions and removal disabling its collector.
  - Official synthetic stdin fixtures cover fractional/zero/full usage, independently
    absent windows, malformed/duplicate fields, integer Unix seconds, bounded input,
    retained last-good data, fresh/stale recovery, elapsed resets and clock rollback.
    Storage checks cover privacy projection, atomic backup recovery and out-of-order
    concurrent callbacks. Version-2 migration updates the previous-good backup as well
    as the primary, preventing older builds from falling back to a v1 registry.
  - The actual production entry point was tested in both the built executable and the
    published single file, with a held desktop mutex and an isolated synthetic registry.
    Direct stdin and the generated PowerShell/Git Bash command passed, including paths
    with spaces and shell metacharacters, malformed input and a five-second stdin deadline.
    The collector did not initialize desktop settings or persist unrelated JSON fields.
  - Mixed WPF checks exercise both languages, all themes, 80/100/150% popup zoom, Claude
    badges/aliases on cards and widgets, receipt/stale messaging, hidden Codex credit
    controls and 470x400 connection/management layouts. Final Korean-dark and English-light
    mixed views, the setup guide and widget renders were visually checked with synthetic data.
  - No real Claude session, account authentication or reset-credit redemption was performed.
    A user must connect each profile's generated statusLine command in the corresponding
    Claude Code settings and use Claude Code to receive live data. Official statusLine has
    no verified account identity; profile attribution is explicitly local and user-configured.

- Earlier Codex-only main: single-account widget identity (2026-09-14): the widget used to hide the account name
  whenever only one profile existed, regardless of its nickname. `FloatingWidget.BindAccount`
  now shows any selected account's existing `DisplayName`; `App.xaml.cs` no longer passes an
  account-count visibility flag. The shared nickname/email/localized fallback rule is reused.
  `AccountUiChecks` reproduces the missing single-account email before the fix and adds
  48 offline WPF renders across English/Korean and Dark/Light/System. Cases cover empty and
  whitespace nicknames, configured nicknames, refreshing/stale/signed-out states, long-email
  ellipsis/full tooltips and clearing the identity after account removal. Existing 0/1/3/8
  account and long-name checks remain. Synthetic dark/light previews were visually inspected.
  Full `./dev-run.ps1` passed restore, Release build (0 warnings/errors), all 1,047 unit
  tests, 15 installer scenarios, 48 new identity renders, 120 existing account renders,
  180 widget DPI/layout renders and 36 production renders. Self-contained win-x64 publish
  contains exactly `CycleArc.exe`; local replacement/startup succeeded. Verification of the
  updated executable on the other PC remains a user-side check; no live-account quota read
  or authentication change was needed to reproduce this presentation issue.

- Earlier Codex-only main: development launcher date regression (2026-09-14): reproduced the reported 23 failures
  in `PrimaryIndexSchemaSignalTests`, `ConversationSchemaHealthTests` and
  `IsolatedConversationFailureTests`. Their fixed September 6 fixtures were filtered out
  when the system clock moved into a later quota period. The retained legacy tests now use
  a fixture-aligned `MutableClock` in both `SyncEngine` and `ConversationParser`, including
  restarted engines and presentation snapshots. Existing cases remain; added assertions
  verify body fetches, completion timestamps and deferred failures after restart. Fatal-auth
  and paused-run checks explicitly advance the clock to keep timestamp-preservation checks meaningful.
  All 56 affected-class cases passed. The complete `./dev-run.ps1` run passed restore,
  Release build (0 warnings/errors), all 1,047 unit tests, 15 installer scenarios,
  120 account WPF renders, 180 widget DPI/layout renders and 36 production WPF renders.
  Self-contained win-x64 publish produced exactly `CycleArc.exe`; local replacement and
  startup succeeded. This changes test setup only; live-account quota behavior was not
  separately revalidated.

- Repository rename and default-branch screenshots (2026-09-12): repository, badge,
  download/issue links, clone instructions and the app's About link use `frozenvoice/cyclearc`.
  The default-branch update includes all 12 previously verified CycleArc production-view
  PNGs with synthetic data. Historical releases retain their original executable names;
  current source builds produce `CycleArc.exe`.
  `./dev-run.ps1 -NoLaunch` passed restore, Release build (0 warnings/errors), 1,047 unit
  tests, 15 installer scenarios, 120 account WPF renders, 180 widget DPI/layout renders,
  36 production WPF renders and one-file self-contained win-x64 publish. Local replacement
  and startup passed; the installed SHA-256 matches the validated artifact:
  `8FD53A931C652402A2709C2C51A198C0BF85493476E70048B8DF333956A09E2E`.
  This link/documentation update adds no behavior or regression-test cases and needs no
  additional live-account verification.

- CycleArc branding and provider labels (2026-09-12): `./dev-run.ps1 -NoLaunch` passed
  restore, Release build (0 warnings/errors), all 1,047 unit tests, 15 installer scenarios,
  120 account WPF renders, 180 widget DPI/layout renders and 36 existing production renders.
  The self-contained win-x64 publish contains exactly `CycleArc.exe` (product metadata: CycleArc).
  Local replacement/startup passed; installed SHA-256 matches the tested artifact:
  `808044C391F889F7B78B8F6021D4FDA43F5BCEDB23A6F8530A6D088A9289B6BA`.
  - Changed presentation files: `UiText`, `AccountSummary`, shared `CodexProviderBadge`,
    Flyout, FloatingWidget, About, Accounts/Settings and TrayController; application theme
    resources provide explicit provider-badge colors. Project/manifest metadata, App Server
    client identity, the launcher/installer and Windows artifact names use CycleArc.
  - Product-identity tests cover both languages, retained storage/mutex identifiers and the
    renamed handshake. Two new startup cases cover owned commands from both earlier product names and
    reject unrelated installations or commands. Installer tests cover all five deployment/
    retry/rollback outcomes for CycleArc and both earlier executable names.
  - WPF checks verify visible Codex badges in 0/1/3/8-account views, selected details and
    widgets, including long Korean/English names, every quota status, 80/100/150% zoom,
    all three themes and a minimum text contrast of 4.5:1. Widget alignment now checks the
    complete title/provider/usage stack; baseline and pixel-formatting checks remain.
  - All 12 documentation PNGs were regenerated from production views with synthetic data
    and visually inspected. Codex remains the only supported provider; no Claude/Gemini
    integration or data-format migration was added. No new browser login or reset-credit
    redemption was exercised, and live-account quota refresh was not independently verified.

- Multi-account README examples (2026-09-11): eight new 2x PNGs show the production
  popup and account manager in English/Korean and Dark/Light themes. All use fictional
  Personal/Work/Research profiles, `example.invalid` addresses and sample 18/64/91% usage;
  Work is selected and Research is stale. The manager is scrolled to show every order button.
  `--screenshots artifacts/readme-multi-account` exported 12 views; only the eight new
  account images were copied to the docs, and all eight were visually inspected. The
  exporter uses `OfflineApp` and `RenderTargetBitmap`, with no user account/settings access,
  Codex home inspection, live requests or desktop capture. README explanations match the
  displayed used/remaining values, selected-account scope, saved-data label and controls.
  All 21 local documentation links resolve. `./dev-run.ps1 -NoLaunch` passed restore,
  Release build (0 warnings/errors), 1,045 unit tests, five installer scenarios, 120 account
  WPF renders, 180 widget renders, 36 existing WPF renders and single-file win-x64 publish.
  This documentation/exporter change required no new live-account verification.

- Connection guidance, local nickname icons and account order (2026-09-11):
  `./dev-run.ps1 -NoLaunch` passed restore, Release build (0 warnings/errors),
  1,045 unit tests, five installer scenarios, 120 account WPF renders, 180 widget
  DPI/layout renders, 36 existing WPF renders and one-file self-contained win-x64 publish.
  Two new core tests verify saved account order after restart, unchanged selected snapshot
  and cache files, no refresh request, and boundary/invalid moves. The WPF checks additionally
  verify popup ordering, movement-button targets/boundaries, visible first-use instructions,
  all expanded guidance in a 470x400 window with the last account reachable, local initials
  (Latin, Korean and combined emoji), stable icon color/contrast and retained editing state.
  Korean dark and English light account management, expanded guidance, usage overview and
  Connection settings were rendered and visually checked with synthetic data.
  Official account documentation and the installed 0.147.0 generated schema expose email/plan,
  not a ChatGPT web nickname or avatar; no web profile scraping was added. Authentication and
  quota request paths are unchanged. This change did not require another live login or a real
  reset-credit use; order/persistence checks used isolated synthetic profiles.
  Local replacement/startup passed with the account-management entry point, and the
  installed executable's SHA-256 matched the verified staging artifact.

- Multi-account support (2026-09-11): `./dev-run.ps1 -NoLaunch` passed restore,
  Release build (0 warnings/errors), 1,043 unit tests (50 added cases), five installer
  recovery scenarios, 120 multi-account WPF renders, 180 widget DPI/layout renders and
  36 existing production WPF renders. win-x64 self-contained publish contained one executable
  under the then-current product name. Local replacement/startup passed and its SHA-256
  matches the validated staging artifact. An earlier restore reported NU1900 feed warnings;
  the final complete run had no warnings.
  - Core cases cover generated account/login shapes, child-only home isolation, legacy
    settings/cache compatibility, 13-profile registry round-trip/backup recovery, malformed
    registries, known-home discovery/deduplication, removal without data deletion, identity
    changes, stale cache isolation, selection persistence and account-bound credit actions.
  - Deterministic gated-process tests cover six-account refresh with two concurrent reads,
    shared-refresh ownership, cancelled waiters, independent account failures and one
    interactive login alongside another account's read. Login tests cover early notifications,
    wrong login IDs, invalid URLs/handshakes, success followed by signed-out verification,
    cancellation, timeout and cleanup after a late process start. A real Node fixture verifies
    that a large UTF-8 stderr stream continues draining beyond the diagnostic budget.
  - WPF cases cover 0/1/3/8 accounts, both languages, all three themes and 80/100/150% popup
    zoom. Account selection does not initiate refresh, busy/cancel progress remains visible
    until cleanup, and a credit action retains its confirmed account through a nested UI
    selection change. A display-timer update interrupting account-name editing was reproduced;
    the new regression passes after the fix. Korean dark overview and English light management
    renders were visually inspected using synthetic metadata.
  - Live verification used installed Codex 0.147.0 and the production login/manager/client
    paths. The user completed official browser login with a second distinct email account.
    Both homes returned available quota metadata (2% and 99% used at the login check), and a
    fresh verifier process loaded both persisted profiles and returned 3% and 99%. The original
    Codex identity remained unchanged, home paths were separate and the checks reported process
    cleanup. An initial same-account login also exercised the matching-identity indication.
    No model turn, real reset-credit consumption or direct credential-file access was performed.
  - Live scope: one linked default home plus one app-owned home. Additional custom-home
    discovery, more than two accounts, cancellation/offline failures and first-use selection
    were covered with synthetic tests. Same-email workspace separation cannot be established
    from this protocol's email/plan projection; no such claim is made.
  - Opt-in diagnostic commands: `dotnet run --project tests/CycleArc.UiSmoke -c Release
    --no-build -- --live-accounts read` checks existing profiles; use `login` to explicitly
    add a managed profile or `relogin` to sign into the last managed profile again. These
    diagnostics are excluded from ordinary tests/CI and print only projected status and
    percentages. Browser login requires the user.

- Selectable automatic refresh: defaults to five minutes and persists 1/2/5/10/30/60-minute
  choices independently of retired history-sync settings. Restore, Release build (0 warnings/errors),
  993 unit tests (21 new schedule cases), 36 production WPF renders, 180 widget DPI renders,
  five installer scenarios and win-x64 single-file publish passed. WPF checks cover all choices,
  Save/Cancel, live timer updates and compact-window scroll access in both languages/all themes.
  Korean dark and English light connection-tab renders were visually checked. Successful refresh
  completion restarts the timer; failed automatic attempts retain a two-minute minimum cooldown.
  Live-account polling over real minute intervals was not exercised for this change.

- Widget native-height alignment: reproduced on the home PC's three 4K monitors at 100% DPI:
  the shown window/content was 39 DIP high despite a 33 DIP content request, leaving 9 DIP
  above the status row and 15 below. Centering the status stack fixes the allocated-height
  case. The new regression failed before the fix and passed after it. 180 offline renders
  cover 100/125/150/175/200% layout DPI, both languages, all three themes, extra allocated
  height and available/stale/recovered states; native window checks pass on all three monitors.
  Korean dark previews at 100% and 150% were visually checked. This is layout validation
  with synthetic metadata; no real reset credit was consumed. Restore, Release build
  (0 warnings/errors), 972 unit tests, 36 existing WPF renders, five installer scenarios,
  single-file win-x64 publish and local replacement/startup also passed.

- Widget restart/credit actions: restore and Release build passed (0 warnings/errors), 972 unit
  tests passed, 36 WPF renders plus offline confirmation/cancellation/single-flight checks passed,
  five installer scenarios passed, and publish produced one self-contained win-x64 executable.
  The WPF harness suppresses production startup and uses the production DPI manifest.
  An actual secondary-monitor edge-position regression was reproduced; after correction the
  installed widget retained its original physical position across replacement and another process
  restart. Reset redemption was tested only with synthetic processes/delegates: selected ID,
  idempotency, malformed outcomes, failed handshake, cancellation, refresh/use serialization,
  stale blocking and exclusion of credit IDs from cache. No real reset credit was consumed.

- Widget alignment/DPI: removed the first row's asymmetric 6 DIP top margin and reused
  AppWindow pixel rounding, device snapping and Display text formatting. WPF startup now
  declares PerMonitorV2 in the executable manifest. Regression checks cover centered text
  and inherited rendering options in both languages/three themes, plus manifest wiring.
  Restore, Release build (0 warnings/errors), 956 tests, 36 WPF renders and win-x64
  self-contained single-file publish passed. Synthetic widget preview was visually checked.
  Physical mixed-DPI monitor transitions and transparent-window text clarity remain manual checks;
  no live-account access was needed.

- Public documentation: English README, Korean companion guide and four production WPF
  previews were prepared with synthetic quota data. The PNGs were visually inspected in
  dark/light themes; README relative links resolve. The exporter does not start the app
  or access accounts/settings. Restore, Release build (0 warnings/errors), 955 tests,
  36 WPF renders, five installer scenarios and the single-file publish passed.
  A targeted scan of 1,424 Git-history blobs found no matches for the checked private-key,
  GitHub/OpenAI/AWS-key or JWT patterns and no tracked sensitive data-file paths.
  This is a targeted check, not a guarantee that arbitrary secrets cannot exist.

- Reliability improvements: restore, Release build (0 warnings/errors), 955 tests,
  36 WPF renders and five isolated PowerShell installer scenarios passed. Completion timestamps,
  recent-failure cooldown boundaries, invalid/off-screen widget positions, atomic settings writes,
  corrupt/missing primary recovery and locked replacement are covered. WPF checks also cover
  dispatcher ownership/disposal, fixed versus System themes, widget recovery persistence and
  reset-on-save intent without mutating settings on Cancel. Installer tests cover transient/permanent
  locks, staging failure and failed startup with previous-version restart. Single-file publish and
  actual local replacement/startup passed in one launcher invocation.
  Physical monitor removal/DPI changes and live Windows theme toggles remain manual checks;
  account access uses the existing protocol and was not separately revalidated for these changes.

- Popup zoom: restore, Release build (0 warnings/errors), 934 tests and 36 WPF renders
  passed. Shortcut logic covers Ctrl +/-/0, shifted plus and keypad keys; normal keys
  and Ctrl+Alt are ignored. Settings default/round-trip/bounds and refresh preservation
  are checked. Popup rows render without overlap at 80/100/150% in both languages and
  three themes. Single-file publish and local replacement/startup passed after retrying
  the known transient directory lock. Physical keyboard input was not automated.

- Widget normal-state notice is collapsed, including its margin. All quota statuses
  and recovery to normal are checked in the WPF smoke suite across both languages and
  three themes. Restore, Release build (0 warnings/errors), 925 unit tests and 24 renders passed.

- Combined used/remaining row: restore, Release build (0 warnings/errors), 925 tests,
  24 WPF renders with row-overlap checks, single-file publish and local app startup passed.
  Korean/English weekly and five-hour labels, 0/100% and unknown percentages are covered.

- Last-checked elapsed time: Release build (0 warnings/errors), 918 tests and 24 WPF
  renders passed. Unit tests cover minute/hour/day boundaries, Korean/English, future
  timestamps and last-success vs failed-attempt timing. WPF checks use the real card
  width and reject overlapping last-checked text. Single-file publish and local app
  replacement/startup passed (the first replacement hit a transient folder lock; retry succeeded).

- Extension cleanup: restore, Release build (0 warnings/errors), all 903 remaining tests, and win-x64 self-contained single-file publish passed. Two obsolete extension-file consistency tests were removed; retained .NET regression tests remain.

- Product rename: clean restore and Release build passed with 0 warnings/errors;
  907 tests passed. New checks cover App Server client identity, both localized product
  labels and unchanged legacy settings/cache/registry/mutex identifiers.
- `CycleArc.UiSmoke` loads production App/XAML without starting the app or accessing
  an account. All 24 window renders passed across Korean/English and Dark/Light/System.
  This smoke check also runs in Windows CI and the development launcher.
- The earlier project rename produced one win-x64 self-contained executable under its then-current name.
  No live-account refresh was performed for this source/project rename.
- Release build: 0 warnings/errors; 907 tests passed, including shared-refresh ownership,
  waiter cancellation, retry after failure, unknown-vs-zero and cached identity-state tests.
- CI artifact-path parsing covers LF, CRLF, tab and space delimiters.
- Single-file Windows x64 self-contained publish verified: one executable under its then-current name,
  with no browser extension, native host, WebView2 or external .NET runtime files.
- Header logo scales a 24 DIP vector canvas into 20 DIP; WPF stroke bounds fit completely inside the canvas.
- WPF flyout/settings rendered in dark and light themes using stored Codex quota metadata.
- Live standalone executable: refreshed successfully at 14:48:28 KST on 2026-09-07; server weekly usage 8%, remaining 92%. Chrome/Edge/Whale old native registrations absent; only the desktop app process remained.
- Notification-icon replacement: production source/project guards pass and assembly reflection
  confirms retired taskbar strip/Win32 types are absent. No overlay is instantiated.
- Settings General/Widget/Connection pages rendered in Korean/English, dark/light, and compact
  sizing. Save/Cancel and disabled widget options checked through the WPF probe.
- Reset countdown boundaries, unknown/partial expiries, duplicate credit IDs, malformed
  counts, root-container precedence and old-cache loading covered by regression tests.
- Read-only live App Server check returned three available credit expiry timestamps;
  the bounded client reported child-process cleanup. Only projected quota metadata was used.
- Flyout renders the supplied two-card layout in dark/light and Korean/English. Quota labels
  and values pass overlap checks; eight expiry groups scroll without truncation. Multiple
  windows, stale, signed-out and unavailable states were rendered.
- Widget pure tests cover click jitter, successive screen deltas, negative coordinates and
  returning to the start after a drag. WPF gesture-handler tests with injected coordinates
  verify 80x40 DIP movement, one save, no accidental open, click opening and capture-loss handling.
  Physical mouse automation was unavailable: its kernel failed at sandbox ACL initialization.
- Compact flyout renders at 440x436 DIP for the three-credit snapshot. Widget-menu callback
  test confirms close fires once without exit; persistence/hide wiring has a regression test.
- Accordion WPF checks pass: 440x436 expanded, 440x317.33 collapsed, refresh preserves collapse,
  and 1/3/8 credit date groups keep the same expanded height. Dark/light and Korean/English render.
- Credit help WPF button-event regression: open, refresh while open, second-click close,
  third-click reopen, and parent-hide close all pass.
- Sliding refresh bar removed. WPF probe confirms identical 436 DIP height before, during
  and after refresh for the same snapshot in dark/light and Korean/English; busy button stays disabled.
- Runtime checks: verify actual Codex refresh, no ChatGPT HTTP/SQLite collector startup,
  no native host registration, saved window position and tray/refresh behavior.
- A fresh PC requires installed Codex CLI; official browser sign-in can be started from
  **Manage accounts**. No browser-extension pairing is part of setup.

## Historical ChatGPT checks (retired; do not use as CycleArc setup)

The extension source and its CI checks have been removed. Commands and extension setup
below document historical validation only and require an old checkout from Git history.

# Manual validation

ProMeter reconstructs usage from ChatGPT account history. These checks require a real signed-in Pro account. Live ChatGPT compatibility is not claimed until they pass.

## Companion pairing

1. Load the unpacked `extension/` in Chrome or Edge.
2. Register the Chrome/Edge native host from Settings with that extension ID.
3. Sign in to ChatGPT in that normal browser. Do not use WebView2 for Google/Microsoft/Apple.
4. Click **Connect to ProMeter** in the extension popup.
5. Run the first manual sync. Sign-in alone must not scan history.

## Multi-device

1. On this PC, open the tray flyout and note the GPT Pro count.
2. On the Android ChatGPT app, send one GPT-5.6 Sol Pro or GPT-6 Pro message.
3. On this PC, choose **Sync now**.
4. Confirm the Pro counter increases by 1.

## Another PC

1. Use ChatGPT on a second Windows PC with the same account.
2. Sync on the machine running ProMeter.
3. Confirm the remote usage appears.

## Reasoning

1. In ChatGPT, send one GPT-5.6 Sol message with Extra High (`매우 높음`).
2. Sync.
3. Confirm **SOL REASONING → Extra High** increases by 1.
4. Confirm the Pro meter does **not** increase for non-Pro Sol reasoning.

## Regenerate

1. Send one Pro message.
2. Regenerate the response.
3. Sync.
4. If the conversation endpoint returns every branch `request_id`, the Pro count should increase by 1 for the regenerate.
5. If only the current branch is returned, Coverage should stay Estimated/Good and this limitation should remain visible.

## Archive

1. Use Pro in a chat, then archive that chat.
2. Sync.
3. Confirm the current-period Pro count still includes that usage.

## Project

1. Send a Pro message inside a ChatGPT Project.
2. Sync.
3. Confirm the event appears and Coverage lists Projects as available.

## Temporary / deleted

1. Use Temporary Chat or delete a conversation.
2. Confirm those turns do **not** appear after sync.
3. Coverage should continue to list Temporary and Deleted as unavailable.

## Official export

1. From ChatGPT Settings → Data Controls, download `conversations.json` if available.
2. Import it from ProMeter Settings.
3. Import the same file again.
4. Confirm usage counts do not double.

## Theme readability (Dark and Light)

Check each screen in Dark, then repeat in Light (Settings → Theme):

- Main
- Settings
- Flyout
- Coverage
- Welcome
- About

Confirm labels, text boxes, combo boxes and dropdown items, checkboxes, tab headers, DataGrid text/headers, and flyout values stay readable. No black/default text on a dark background, and no washed-out light text on a light background.

## Browser companion with VPN left on

Do not disable the browser VPN or proxy, switch browsers, or weaken browser security.

1. Keep the VPN/proxy enabled in Edge or Chrome.
2. Confirm chatgpt.com itself loads and is signed in in a normal tab.
3. Confirm the companion popup shows connected to ProMeter.
4. Leave that ChatGPT tab open (or let ProMeter open https://chatgpt.com/ and sign in there, then retry).
5. Run a manual sync.
6. Confirm ProMeter does **not** tell you to disable VPN.
7. If ChatGPT returns 403, the UI/log should say `ChatGPT rejected the page request (403)`, not `ChatGPT session expired`, unless the ChatGPT tab is independently signed out.
8. If no ChatGPT tab exists, the status should be `Open/sign in to ChatGPT, then retry`.
9. If MAIN-world page execution is blocked, the status should be `ChatGPT page bridge unavailable` rather than a silent service-worker fetch.


## Browser page timeout recovery

`node extension/test-companion.js` includes synthetic regressions for a hung session fetch,
response body, backend body, and 401 refresh; shared authentication recovery; late session
completion; injection before document idle; frozen/discarded tab preference; and late Chrome callbacks.
No real account content or credentials are used in these tests.

Live check after reloading the unpacked Edge extension:

1. Activate an already signed-in ChatGPT tab, then run ProMeter's full manual history sync.
2. Confirm account/index requests resume and the completed reconstruction is displayed.
3. Repeat with a background ChatGPT tab and, separately, an Edge sleeping tab. The companion
   should activate the same sleeping tab once and resume. It must not focus the browser window,
   reload the user's conversation, disable VPN, or reset stored usage.
4. Confirm stalled requests fail within the documented deadline and a later retry is not held by
   the previous session promise. `Connection required` can also reflect a previous BridgeTimeout;
   inspect the safe sync failure category rather than treating that label as proof of logout.

Live result on 2026-09-07: after the user reloaded/reconnected the fixed-ID Edge companion,
manual history sync completed at 13:28:36 +09:00 with `UpToDate`, `coverage=Estimated`,
and zero failed conversations. This verifies history transport recovery, not an authoritative
remaining-quota count. Subsequent 13:37–13:38 retries failed at page preparation, so this earlier success alone did not establish a complete fix. Version 5 adds bounded same-tab wake-up recovery; verify this with the deployed version.


## Unknown-reset Monday regression

- With no configured/confirmed reset, weekend Pro observations must remain in **Last 7 days reconstructed**
  after Monday midnight. Never turn the default Monday preference into a quota reset.
- No next-reset date is displayed until there is an applicable anchor. Server counts and configured
  reset periods still follow their original rules; local-calendar Sol analytics are unchanged.
- `RollingProHistoryTests` uses synthetic weekend, too-old, and future requests to verify the boundary.

- Reset lookup fallback regression: empty init followed by model-limit metadata must recover reset timing; an ordinary model catalog must never manufacture a reset. Bridge version 6 requires extension Reload; live reset retrieval remains unverified.

## Cross-PC verification, 2026-09-07

- After extension Reload, live sync completed at 14:17:43 KST: UpToDate / Estimated,
  34 loaded conversations, 0 failed; archived and 9 project indexes were checked.
- Current local server-status metadata still has no model limits or retained reset.
  Provider fallback did not recover a reset from this live response.
- The user-referenced home conversation records a prior reset around
  2026-09-06 14:20:14 KST and a reconstructed count of 12. The attached log examined
  here did not independently establish that reset timestamp.
- A read-only production QuotaEngine calculation over the freshly synchronized local
  metadata produces 12 with that historical-chat anchor, versus 72 in the rolling
  seven-day window. No server-status/settings values were overwritten from chat text.
- Account-history collection supports cross-device reconstruction; identical current-cycle
  displays on fresh PCs remain unverified until the same evidenced reset boundary is
  available on each PC. A successful scan does not establish authoritative billed usage.

## Multi-account floating widget, 2026-09-17

- The widget now projects `UsageAccountOverview.Accounts` through `WidgetAccountModel`, which
  reuses `CodexRingPresentation`, `CodexDisplayFormatting` and `ClaudeUsagePresentation`. It adds
  no collector, request or timer: the existing one-minute display timer and the two-second passive
  read already rebind it, so a countdown tick costs no network call.
- `WidgetGridLayoutTests` covers wrapping in DIP against the work area of the monitor the widget
  sits on: 1/3/5 accounts on one row when it fits, 3+2 at 760 DIP, 2+2+1 at 520 DIP, a whole module
  on a work area narrower than one module, and bounded vertical scrolling for 24 accounts on a
  300 DIP-tall work area. Column count is never capped at five.
- `WidgetAccountModelTests` covers every reported period being shown with its own remaining
  percentage and its own reset, the Auto/5-hour/Weekly preference choosing only the ring, unknown
  percentages staying `?`, a missing reset staying **Reset not provided**, a passed reset staying
  **Awaiting refresh** rather than restarting a cycle locally, and identity mismatch, sign-out and
  awaiting-usage showing no cached numbers.
- WPF evidence is produced by `CycleArc.UiSmoke --widget-accounts`: 1/3/5 accounts, the wrapped
  5-account row, mixed one-period and two-period accounts, and long names, missing resets, stale
  data and authentication failure, each in EN/KO and Dark/Light. `WidgetDpiChecks` runs the same
  three-account content at 100–200% in both languages and all themes.
- Not verified here: this change was authored on Linux, where the WPF projects cannot build, so
  the build, the unit tests and every UiSmoke render listed above were left to the Windows CI job
  rather than run locally. Multi-monitor recovery is exercised only through the synthetic
  `ScreenRect` work areas and whatever monitors the CI runner reports; it is not evidence of a real
  multi-monitor desktop. `docs/images/widget.png` is produced from the production widget with three
  synthetic accounts (`DocumentationScreenshots`); it is not a live-account capture.

## Widget relayout display and recovery, 2026-09-18

- Relayout now applies the arranged DIP size to the HWND (`SizeToContent` can keep a previous
  wrapped width, and WPF Width can follow `SM_CXMAXTRACK`) before clamping into the same target
  work area. Tests inject monitor rectangles only; `RecoverTo` uses `RecoverInto` on that path
  whether the list is injected or live.
- `WidgetLayoutChecks.RelayoutOrder` records injected vs host work areas, LastLayout, DesiredSize,
  RenderSize, ActualWidth/Height and `GetWindowRect`, requires a `Moved` event when the origin
  must change, and round-trips DIP plus `WidgetPixelLeft/Top` through an isolated `SettingsStore`.
  It does not read or write `%LOCALAPPDATA%\ProMeter`.
- Capture of a shown widget uses the live `RenderSize`. A five-column primary render that uses the
  injected 1920×1040 DIP work area is not a claim that the CI host monitor is that wide.
- Not run: installed-app update/removal, real-account quota requests, or changing this PC's
  monitor layout. Dual-monitor wrap/recovery is the injected `ScreenRect` path plus whatever
  monitors the host already has.

## Independent widget and detail-card size, 2026-09-19

- The reported symptom, "`Ctrl` +/- at the widget resizes the detail card", had two causes and
  neither was a shared zoom value. `FinishDrag` raised `FlyoutRequested` for every click,
  including the header and empty chrome, so `App.ShowMain` activated the detail card and the
  keyboard went with it; and the widget had no zoom of its own, so the only handler that existed
  belonged to `FlyoutWindow`. A press on an account still opens and activates the card; a press
  on the header or empty chrome now only focuses the widget.
- `AppSettings.WidgetZoomPercent` is stored and normalised separately from `FlyoutZoomPercent`.
  `FlyoutZoom` is shared arithmetic only: neither window reads or writes the other's value, and
  restoring a saved value is silent, so a clamped value is not written back over what was chosen.
- `WidgetZoomChecks` (`CycleArc.UiSmoke --widget-zoom`) is 531 checks against real shown windows
  and the real click gesture, not the zoom helpers alone. Focus separation is asserted through
  `GetActiveWindow` and `Keyboard.FocusedElement`: with the detail card active, a widget header
  click returns the keyboard to the widget and does not re-activate the card. It also covers
  account click vs header click, drag vs button click, button/keyboard parity, one step per
  keystroke, the 80% and 150% limits with `Ctrl` `0` escaping either end, `Ctrl`-less and
  `Ctrl+Alt`/`Ctrl+Win` combinations being declined by both windows, header fit and order at
  1/3/5 accounts and 80/100/150% in EN/KO and Dark/Light, and a saved size surviving reload and
  recreation through a real `SettingsStore`.
- `FlyoutActivationChecks` gained the complementary case: a header click changes neither whether
  the popup is shown nor which window is active. Its existing click helper now presses an account
  module, which is the gesture that opens the popup.
- `ArrangedSize` mixed two coordinate spaces once the panel could scale: the grid wraps in
  unscaled child DIP while the panel's `DesiredSize` is already transformed, so a zoomed widget
  would have been given an HWND sized to the unscaled grid. At 100% the two are equal, which is
  why nothing caught it before. The capture helpers had the same mistake and cropped the picture;
  `WidgetFixture.RenderWidget` and `AccountUiChecks.RenderCurrent` now take the applied scale.
- `IndependentWindowZoomTests` pins the settings contract: separate values, per-window
  normalisation, an absent widget value opening unscaled, and an explicitly null one leaving the
  file unreadable so the store keeps the last good copy - the same rule as every other number in
  that file, which this change does not alter.
- Previews affected by the two new header buttons were regenerated from the production views with
  synthetic accounts (`CycleArc.UiSmoke --screenshots`): `widget.png` and the detail-card images.
  `settings.png` also differs from its checked-in copy, but for an unrelated earlier drift in the
  tray-icon explanation text, so it was left alone. Fourteen of the exported previews are not
  byte-stable between two runs of identical code because they contain relative times; a byte
  difference in those is not evidence that a change affected them.
- Run locally on Windows: `dev-run.ps1 -NoLaunch` (exit 0, 1574 unit tests, full UiSmoke).
- Not run: any live account request, and any installed-app update or removal. `package-verify`
  exercises an isolated portable root, which is not an installed-app update. The multi-account
  and zoomed layouts use injected `ScreenRect` work areas and synthetic accounts throughout.

## Widget period alignment, 2026-09-19

- Five-hour and weekly labels reserve the same marker gutter and share the remaining value's
  font size and baseline. Each period uses a 16-DIP primary line and a 14-DIP reset line;
  the two periods and their 4-DIP gap match the 64-DIP ring. Single-period and two-period
  accounts therefore keep their rings aligned across the row.
- An immediate remeasure after binding includes a newly visible status line, before WPF's
  deferred parent invalidation. The existing shown-window status/rebind regression caught
  this as a clipped module and now passes without changing its bounds assertions.
- `WidgetMultiAccountChecks.CheckAlignment` compares label starts, text baselines, right
  edges, period-block/ring centers and rings within each account row. It also runs in the
  zoom and DPI suites. The missing-reset fixture reproduces 100% left for five hours and
  77% left for the week using an invented Claude account.
- The DPI fixture applies its scale at the window root so a synchronous module remeasure
  inherits the simulated DPI throughout the visual tree. Text-DPI equality remains asserted.
- Focused checks passed locally: 24 multi-account renders, 151 layout checks, 531 zoom checks,
  and 180 DPI/layout renders plus native windows on three monitors. Inspected EN/KO, Dark/Light,
  80/150% widget sizes and 125% DPI; updated only the affected `docs/images/widget.png` preview.
- Final local gate: `dev-run.ps1 -NoLaunch` passed in 4m20s, including 1,574 unit tests,
  full UiSmoke, single-file publish and package/rollback component checks. No live account
  request or installed-app replacement was performed.

## Build-local previous-statusLine fixture, 2026-09-20

- A normal `build-local.cmd` run passed 1,574 unit tests and the built-executable WPF checks,
  then stopped in `publish`: the single-file receiver's Git Bash bridge check reported
  exit 0 with empty stdout/stderr. The same published executable subsequently passed the
  focused check with the normal Windows PATH. The original log did not retain the inner
  forwarding failure, so it does not establish a particular OS error or timeout.
- The synthetic previous statusLine no longer starts another PowerShell inside the
  production four-second forwarding budget. A UiSmoke child reads stdin through EOF,
  requires the same exact five-hour marker, and emits the same Korean/ASCII line. Missing
  quota input is explicitly rejected. The generated production wrapper still runs through
  both PowerShell and Git Bash, with authentication, receipt and output-preservation checks.
- Process failures now include elapsed time. Production forwarding behavior and the
  four-/ten-second deadlines are unchanged; this removes nested fixture startup variance,
  not a claim that every external statusLine command can finish within the deadline.
- The repaired checkout passed `build-local.cmd -Fast` with the persisted Windows PATH,
  including the Store-installed PowerShell 7 entry point. `-Fast` reused the 1,574 passing
  unit tests for unchanged application/unit-test code; all WPF/process checks, single-file
  publish, packaging and package verification reran and passed in 2m54s. The generated
  installer's live `CycleArc Setup` window and `awaiting-approval` state were verified;
  installation was left at its confirmation screen for the user.


## Screen-edge snapping, 2026-09-22

- `WindowEdgeSnap` is shared DIP arithmetic: detection is inclusive at 12 DIP from the
  8-DIP inset target, with independent axes, closest-candidate selection and left/top ties.
  Each production window detects only after an actual drag, after choosing its monitor and
  measuring its final content. Shift skips detection, while safety recovery remains active.
- `FloatingWidget` and `FlyoutWindow` keep their own horizontal/vertical anchors. Relayout
  reuses these anchors after zoom, wrapping, content-height and DPI/work-area changes; it
  never infers an attachment from a refresh or a legacy saved position. App callbacks save
  final positions and anchors into the existing settings store, including physical pixel
  positions for mixed-DPI recreation. Disabling clears both windows' anchors; widget position
  reset clears only the widget's. Re-enabling waits for a new drag.
- Focused verification passed: Release build (0 warnings/errors), deterministic production
  `--edge-snap` checks, and 45 `--settings-window` checks. The latter drives Save, checks the
  localized accessible name, both languages/themes, the declared size and minimum-size
  scrolling. Only `settings.png` and the new snap comparison captures were updated.
- `--edge-snap-native artifacts/window-edge-snap/native` passed on the actual connected
  monitors: primary work area `(0,0,2560,1528)` at 150% OS DPI and secondary
  `(-1920,0,1920,1032)` at 100%. For **both** production windows it used native mouse
  down/move/up, header clicks and zoom buttons at 100/150/80%, Shift release near the target,
  passive content/zoom updates with a separate foreground window, and pixel/anchor recreation.
  The negative-coordinate monitor cases also crossed the physical monitor/DPI boundary with
  a native drag. No monitor geometry or DPI was injected in this opt-in run.
- The default `--edge-snap` suite simulates production gesture state and uses synthetic
  coordinates; it is not evidence of OS mouse delivery. Core tests additionally cover
  thresholds, corners, portrait/taskbar rectangles, negative origins, removed-monitor
  recovery, oversize/invalid inputs and scaling. Existing WPF DPI fixtures inject
  100/125/150/175/200% layout DPI; they are separate from the two hardware DPIs above.
- The disable -> re-enable -> same-coordinate reattachment regression caught a stale
  flyout persistence-deduplication tuple. Applying changed anchors now invalidates that tuple,
  so the new attachment is saved even if the final coordinates match the earlier drop.
- Final local gate: `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch
  -TestResultsDirectory artifacts/window-edge-snap/test-results` passed in 5m17s:
  1,796 unit tests, complete UiSmoke (including 151 layout, 531 zoom, 45 settings and the new
  edge-snap checks), test-flavour compile, single-file publish, built/published receiver
  checks, Velopack packaging and isolated portable apply/rollback verification. The build
  reported only the two existing xUnit1031 warnings in `ChildReportFileTests` and
  `UpdateRecoverySnapshotTests`. The log and TRX are in `artifacts/window-edge-snap`.
- Native screenshots use `PrintWindow` on the synthetic HWND only, positioned at its measured
  coordinates on a neutral work-area canvas. Other apps and actual account data are never
  captured. Checked-in `edge-snap-{widget,flyout}-{100,150}.png` show the primary monitor;
  80% and secondary-monitor captures plus `native-monitors.txt` remain in the local artifacts.
- Not run: changing this PC's monitor configuration, physical monitor removal, a physical
  portrait monitor, or taskbars placed on other edges. Those geometry cases use injected
  tests. No actual account login/quota/model request, installation/update/removal, remote CI,
  push, version change, tag or release was used for this work.


## Screen-edge inset reduced to 2 DIP, 2026-09-22

- Baseline: `c08558d675b3cc5120c6be9bb492930bef5d0783`. `WindowEdgeSnap.MarginDip`
  is now 2, while `ThresholdDip` remains 12. Both windows already apply anchored placement
  after generic recovery, so their WPF drag, zoom, sizing and restore code needs no change.
  Internal layout, fonts, rings, account sizing, settings and persisted anchor formats are unchanged.
- General recovery remains 8 DIP. In particular, `ClampRecoveredAxis` previously reused the
  snap constant for non-finite input; it now has a separate private 8-DIP recovery constant.
  `FlyoutPlacement` safety clamping and `WidgetGridLayout` wrapping clearance are unchanged.
- Focused tests passed: 38 coordinate/settings/placement tests, then production `--edge-snap`
  checks for both windows. Literal 2-DIP expectations independently pin every corner and
  supplied work-area inset; both directions around each edge test 11.999/12/12.001 DIP.
  Old 8-DIP attached coordinates restore at 2 DIP, while the same unanchored position stays
  unchanged. Invalid/oversized safety recovery, independent free axes, Shift and disabled
  snapping retain their previous behavior.
- Native `--edge-snap-native` passed on actual primary `(0,0,2560,1528)` work area at
  150% OS DPI and secondary `(-1920,0,1920,1032)` at 100%. It covers native drop/release,
  header clicks, 80/100/150% app zoom, passive height/status changes without focus theft,
  and recreation from saved pixel/DIP coordinates still carrying the old 8-DIP inset.
  Both windows retain their anchors and the new inset; Shift detaches as before.
- Twelve before/after native capture pairs use identical fixed synthetic snapshots and
  matching app zoom. Every HWND width/height is unchanged; each moves exactly 6px at
  100% OS DPI or 9px at 150%. Measured gaps are 1-2px at 100% and 2-4px at 150%, within
  the existing 1px native-coordinate rounding of the 2/3px targets. After aligning window
  origins for pixel comparison, only the reset-countdown hour digit differs (the real clock
  crossed an hour boundary); other internal pixels are identical. Geometry and full-size
  captures are under `artifacts/snap-margin-2dip`, including `comparison.json`.
- The first pre-change native attempt did not reach the requested drop coordinates and
  failed its anchor assertion. A rerun with start/target/DPI diagnostics passed, as did the
  post-change run. Its cause was not established; no input logic or timeout was changed.
- Only the four current edge-snap previews were refreshed; two 100% baseline captures were
  added. The previous 8-DIP validation record above remains historical evidence.
- Final local gate passed in 5m10s: `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch
  -TestResultsDirectory artifacts/snap-margin-2dip/test-results`. All 1,800 unit tests,
  complete WPF checks (including input/focus, 151 layout, 531 zoom and 180 DPI/layout cases),
  test-flavour build, single-file publish, built/published receiver checks, installer packaging
  and isolated portable apply/rollback verification passed. Log: `artifacts/snap-margin-2dip/full-gate.log`.
- Not run: physical 200% OS DPI, monitor removal/rotation, or taskbar relocation, because
  the connected desktop remains at its existing 100/150% configuration. Other DPI/work-area
  cases use injected arithmetic/layout checks. No user installation, account or developer-tool
  configuration was changed; no push, CI run, merge, version change, tag or release was performed.
