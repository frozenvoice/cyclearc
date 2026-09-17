# CycleArc agent instructions

CycleArc is a Windows-only .NET 8 WPF tray app for Codex and Claude subscription limits.
Use Windows, the .NET 8 SDK and PowerShell 7+. The solution is `CycleArc.sln`;
`src/CycleArc` is the desktop app, `src/CycleArc.Core` holds provider/shared logic,
and `tests/CycleArc.Tests` / `tests/CycleArc.UiSmoke` cover unit / production WPF checks.
Ship the Windows x64 `CycleArc-Setup.exe` Velopack installer for the stable channel. Keep the
development publish check for one self-contained `CycleArc.exe`; the installed app runs from
`%LOCALAPPDATA%\CycleArc\current\CycleArc.exe` and the stable root launcher is used for desktop/
autorun forwarding. The former `%LOCALAPPDATA%\Programs\CycleArc` path remains development-only
compatibility and is excluded from GitHub update checks.
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
- Claude manual and configured automatic refresh query the shared Web·Desktop·Code quota using
  the connected account's existing Claude Desktop OAuth access credential. Read only the bounded
  Desktop `config.json` token cache and `Local State` protected encryption key; decrypt in memory,
  verify the server profile against the saved identity binding, then GET the first-party quota.
  Desktop owns credential renewal. Never write/refresh its credentials, read cookies or conversations,
  send model requests, follow redirects, or log/persist authentication material. These app-owned
  formats and OAuth endpoints are observed interfaces, not a public compatibility contract.
- Keep official Code statusLine `rate_limits.five_hour` / `seven_day` and Desktop
  `plan-usage-history.json` as passive fallbacks. Preserve fractional percentages and original
  observation times; Desktop history has no reset timestamps. The two-second passive loop reads
  local data only. Never represent cache rereads as successful server checks or invent zero.
- Successful server quota shows Updated/Last checked with its actual response time. Legacy samples
  show Received with original source time. Failures retain last-good data and success time across
  restart, while identity mismatch hides all quotas. Keep live failure state through passive polls,
  respect server retry delays, and guard in-flight responses/cache commits against binding changes.
- Connection and receipt are separate: connected Claude accounts with unknown limits show Awaiting usage
  without attention counts; disconnected profiles stay in management. Use one projection for popup/tray/widget,
  counts and fallback selection. Reuse verified bindings on reconnect, preserve saved data and disconnection
  across restart, and remove only empty drafts from cancelled/failed connection flows.
- Claude login uses official `auth login --claudeai` / `auth status --json`. StatusLine does not verify identity:
  retain configuration/binding checks, reject old/mismatched callbacks and preserve unrelated settings
  and existing statusLine command/output. Change/restore only exact CycleArc-owned hook entries.
  Keep request/authentication failures separate from receipts; callbacks alone do not clear sign-in recovery.
  Bound subprocess startup, requests, cancellation and shutdown; do not orphan children. Authentication is single-flight.
- Removal cleans up only what this installation owns. Velopack's uninstall hook stops the app, runs once with
  a bounded budget, ignores the result and then deletes the installation root, so cleanup is time-boxed, never
  throws and never tries to cancel or retry removal. Restore a statusLine or StopFailure hook only when the
  exact owned wrapper names this profile, the same configuration directory and an executable inside the
  installation being removed; never delete or rewrite accounts, settings, caches, bindings or credentials, and
  never record an incomplete cleanup as a successful one.
- Claude storage is limited to projected quota, receipt/source/binding metadata and failure classifications;
  keep email in memory. The history reader opens only bounded `plan-usage-history.json`; the separate
  live credential reader opens only Desktop OAuth config and its protected encryption key. Verify
  account identity before accepting any source. Never read CLI credentials, cookies, prompts, responses
  or conversation files, log secrets, or add telemetry. Tests use isolated roots and fake adapters.
  Real-account compatibility is verified separately with authorized read-only usage requests.
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
| Installer/startup/routing/distribution or executable delivery | Full gate below, including installer assets, rollback, single-file development publish and built/published receiver checks. Setup/update/removal behavior of the installed app also needs `scripts/Verify-InstalledUpdate.ps1` on a disposable Windows VM or throwaway user. |

- Targeted tests: `dotnet test tests/CycleArc.Tests/CycleArc.Tests.csproj -c Release --filter "<matching-filter>"`.
  Use an existing test name/category for the filter. WPF checks: `dotnet build CycleArc.sln -c Release`, then
  `dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build`.
- Full gate: `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` on final executable changes.
  It restores, builds/tests Release, checks WPF/installer assets and validates the development
  `publish/.dev-staging/CycleArc.exe` receiver.
  If installation/run is requested, use `pwsh -NoProfile -File ./dev-run.ps1` instead: the same gate runs
  before installation. Choose the mode upfront to avoid repeating the gate.
- Installed-app end to end: `pwsh -NoProfile -File ./scripts/Verify-InstalledUpdate.ps1 -ConfirmDisposableEnvironment`.
  It installs, updates, recovers and removes a real installation for the current Windows user and cannot
  isolate the data root, uninstall registry entry, shortcuts, mutex or desktop IPC, so never run it on a
  working profile. Packaging `Setup.exe`, rewriting a fixture's `sq.version` or calling `Update.exe` directly
  is not an installed-app update; report those as component checks.
  The full gate satisfies the build/test requirements above; `-Fast` skips only unit tests already passed for unchanged code.
- Synthetic fixtures do not establish real-account receipt/compatibility; Awaiting usage is not received usage.

## Delivery

- Commit and push intended changes to origin unless instructed otherwise; never force-push.
  Configure upstream (`git push -u origin <branch>` on first push), verify remote HEAD/tracking and check CI for that SHA.
- Publish with `pwsh -NoProfile -File ./scripts/Release.ps1 -Version <version> -NotesPath <file>` after
  the final local gate and successful Windows push CI; new drafts require the notes file.
  Verify all CI executable/installer assets, their versions, commit/tag and uploaded SHA-256 before publication.
  Versions are centralized in `Directory.Build.props`.
- Install/restart only when requested. Use the Velopack installer/update path, verify the target
  checkout, running path and artifact hash, and keep the stable per-PC installation at
  `%LOCALAPPDATA%\CycleArc` across releases. The external recovery supervisor restores failed
  replacement or initial desktop readiness; it does not monitor later session failures. Preserve the
  `%LOCALAPPDATA%\ProMeter` data path and legacy mutex. Ordinary launches preserve/activate the
  first desktop; `--autorun` stays quiet.
- Release artifacts are unsigned unless the release pipeline is configured for code signing;
  an installer by itself does not remove Windows SmartScreen warnings.
