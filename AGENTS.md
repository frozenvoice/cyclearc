# CycleArc agent instructions

CycleArc is a Windows-only .NET 8 WPF tray app for Codex and Claude subscription limits.
Use Windows, the .NET 8 SDK and PowerShell 7+. The solution is `CycleArc.sln`;
`src/CycleArc` is the desktop app, `src/CycleArc.Core` holds provider/shared logic,
and `tests/CycleArc.Tests` / `tests/CycleArc.UiSmoke` cover unit / production WPF checks.
Ship one self-contained Windows x64 `CycleArc.exe`.
Do not hand-edit or commit generated/local output in `bin/`, `obj/`, `publish/`, `artifacts/` or `.tmp/`.

## Read on demand

Read only the sections relevant to the change:

- Product/setup/wording: [README](README.md) and [Korean guide](docs/README.ko.md); update both for behavior changes.
- Provider boundaries, caches and startup: active-product sections of [Architecture](docs/ARCHITECTURE.md).
- Claude authentication/receipts: [Claude integration](docs/CLAUDE.md). Before changing its data source,
  check [official-interface research](docs/CLAUDE-USAGE-RESEARCH.md) and current official sources.
- Regression evidence: matching entries in [Validation](docs/VALIDATION.md).
- UI images: [image guide](docs/images/README.md); use production views with synthetic accounts and update affected previews only.
- Retired history/reconstruction, WebView or companion code: [legacy maintenance](docs/LEGACY-MAINTENANCE.md);
  its rules apply only to that work.

## Product contracts

- Keep providers behind `IUsageProvider` / `IUsageAccountService`. Never mix accounts' quotas, windows,
  caches or reset credits. Selection affects detail/tray/widget, not another app's login.
  Preserve nicknames, order, selected IDs, badges and settings/cache compatibility.
- Codex uses the installed, signed-in App Server. Classify windows by `windowDurationMins`, not slot order;
  separate root and selected-bucket metadata. Optional windows may be absent; malformed input is a failure.
  Keep unknown percentages/credit expiries unknown and reset-credit IDs in memory; persist expiry timestamps only.
  Identity mismatch/conflict hides cached quota and credits; never inherit another account's cache.
  Only explicit successful login may replace an established binding.
- Claude shows the shared Web·Desktop·Code subscription quota received through official Code statusLine
  `rate_limits.five_hour` / `seven_day` (`used_percentage`, `resets_at`); preserve fractional percentages.
  Label it Received, with original receipt time and shared scope, never live/current.
- Idle Claude samples retain last-good values and Received state even after reset times pass. Input/receipt,
  cache, identity, authentication, request or bridge failures may mark samples stale. Refresh reads the local inbox; refresh, polling
  or opening the usage page must not renew receipts or invent zero. Never scrape usage pages, parse `/usage`,
  call undocumented quota endpoints or run a model turn to measure limits.
- Connection and receipt are separate: connected Claude accounts with unknown limits show Awaiting usage
  without attention counts; disconnected profiles stay in management. Use one projection for popup/tray/widget,
  counts and fallback selection. Reuse verified bindings on reconnect, preserve saved data and disconnection
  across restart, and remove only empty drafts from cancelled/failed connection flows.
- Claude login uses official `auth login --claudeai` / `auth status --json`. StatusLine does not verify identity:
  retain configuration/binding checks, reject old/mismatched callbacks and preserve unrelated settings
  and existing statusLine command/output. Change/restore only exact CycleArc-owned hook entries.
  Keep request/authentication failures separate from receipts; callbacks alone do not clear sign-in recovery.
  Bound subprocess startup, requests, cancellation and shutdown; do not orphan children. Authentication is single-flight.
- Claude receiver storage is limited to projected quota, receipt/binding metadata and failure classifications;
  keep email in memory.
  Never read/copy/monitor CLI credentials, tokens, cookies, prompts, responses or conversation files;
  never log secrets or add telemetry. Tests/diagnostics use isolated roots and fake adapters, not real account/settings changes.
- Unknown usage is never zero. Failures retain the last valid snapshot and
  last-success time across restart. Keep atomic settings/cache writes with valid backups and preserve
  `LegacyInstallation` data/mutex/registry identifiers through branding changes.
- Dispatch background/system callbacks to WPF's Dispatcher. Shared refresh is single-flight across entry points;
  only its owner clears busy state after completion. Preserve widget enabled state separately from `IsVisible`,
  saved position and native hide/minimize/topmost/resume/unlock/display recovery without stealing focus.
  Never revive disabled/accountless widgets or recover after exit; preserve per-window event subscriptions.
- Use existing EN/KO and Dark/Light/System resources, accessible states and DPI-safe layout.
  Keep native tray text within 127 characters, prioritizing Claude receipt/freshness/scope over long nicknames.
- Windows owns tray placement. Do not restore overlays, Explorer hooks, extensions, WebView2,
  conversation synchronization or active SQLite history collection. Retained legacy code is not a supported runtime.
  Windows startup stays opt-in. Route the bounded headless Claude receiver before WPF/mutex/account startup
  in the same executable; ship no companion host. Reset-credit actions remain Codex-only.

## Verification

Run commands from the repository root. Use `--no-build` only for code already built.

| Change | Required checks |
| --- | --- |
| Documentation/instructions/comments/Git tracking only | Diff, affected links/paths and command accuracy; no local app build, publish, install or model call. |
| Core/provider correctness | Relevant regression tests, including malformed/unknown input and state transitions. Use official protocol-shaped fixtures. |
| Login/cache/concurrency/account lifecycle | Deterministic failure/cancellation/restart tests with isolated data and fake adapters. |
| WPF state/layout/localization/theme/interaction | Release build and affected UiSmoke checks; visually inspect affected production views in EN/KO and relevant themes/sizes. |
| Installer/startup/routing/distribution or executable delivery | Full gate below, including rollback, single-file and built/published receiver checks. |

- Targeted tests: `dotnet test tests/CycleArc.Tests/CycleArc.Tests.csproj -c Release --filter "<matching-filter>"`.
  Use an existing test name/category for the filter. WPF checks: `dotnet build CycleArc.sln -c Release`, then
  `dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build`.
- Full gate: `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` on final executable changes.
  It restores, builds/tests Release, checks WPF/installers and validates `publish/.dev-staging/CycleArc.exe`.
  If installation/run is requested, use `pwsh -NoProfile -File ./dev-run.ps1` instead: the same gate runs
  before installation. Choose the mode upfront to avoid repeating the gate.
  The full gate satisfies the build/test requirements above; `-Fast` skips only unit tests already passed for unchanged code.
- Synthetic fixtures do not establish real-account receipt/compatibility; Awaiting usage is not received usage.

## Delivery

- Commit and push intended changes to origin unless instructed otherwise; never force-push.
  Configure upstream (`git push -u origin <branch>` on first push), verify remote HEAD/tracking and check CI for that SHA.
- Publish with `pwsh -NoProfile -File ./scripts/Release.ps1 -Version <version> -NotesPath <file>` after
  the final local gate and successful Windows push CI; new drafts require the notes file.
  Use that CI run's executable; verify file version, commit/tag and uploaded SHA-256 before publication.
  Versions are centralized in `Directory.Build.props`.
- Install/restart only when requested. Use the executable's guarded transactional installer/rollback path,
  verify the target checkout, running path and artifact hash, and keep the shared per-PC installation at
  `%LOCALAPPDATA%\Programs\CycleArc\CycleArc.exe` across downloads and worktrees.
  Ordinary launches preserve/activate the first desktop; `--autorun` stays quiet. Preserve the legacy mutex.
