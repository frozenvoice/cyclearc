# CycleArc validation

## Current release — Codex and Claude Code

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
