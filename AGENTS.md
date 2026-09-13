# CycleArc agent instructions

**CycleArc** is a Windows-only .NET 8 WPF tray app for Codex and Claude subscription limits.
Repository: `cyclearc`; solution: `CycleArc.sln`; distribution: one self-contained `CycleArc.exe`.
`src/CycleArc` contains the desktop app; `src/CycleArc.Core` contains provider/shared logic;
`tests/CycleArc.Tests` and `tests/CycleArc.UiSmoke` contain unit and production WPF checks.

## Scope and workflow

- Check the repository root, branch/upstream and working-tree changes before editing. Preserve unrelated work.
- Follow the user's current request and earlier approvals. Resolve routine reversible choices without asking again;
  clarify only a missing decision that materially affects the result. Respect host permissions.
- Inspect the affected implementation and relevant recent history, then implement, run the appropriate check,
  fix failures and verify the result. A plan, first implementation or passing compile alone is not completion.
- Keep changes focused. Do not add collectors, dependencies, features or cleanup merely to make a check pass.
- Read only the context needed below. Select skills for the actual operation, not incidental keywords.
  A wording change does not require another protocol investigation or every project document.
- Keep this file focused on current product contracts and recurring failures. Put detailed explanations in
  the relevant document; consolidate an existing rule before adding another blanket requirement.

## Read on demand

| When the task involves | Read the relevant section of |
| --- | --- |
| Product behavior, setup or user-facing wording | [README.md](README.md) and the corresponding [Korean guide](docs/README.ko.md) |
| Provider boundaries, account state, cache compatibility or startup | The active-product section of [ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Claude login, bindings, statusLine or receipt handling | [Claude integration](docs/CLAUDE.md) |
| Changing Claude's data source or assessing a current-quota query | [Official-interface research](docs/CLAUDE-USAGE-RESEARCH.md); recheck official sources if the decision may have changed |
| Prior regressions, test coverage or release evidence | Matching entries in [VALIDATION.md](docs/VALIDATION.md), not its entire history |
| Creating or replacing UI documentation images | [Image guide](docs/images/README.md) |
| Retained ChatGPT history, reconstruction, WebView or companion code/tests | [Legacy maintenance](docs/LEGACY-MAINTENANCE.md); those rules do not apply to ordinary Codex/Claude work |

## Product contracts

- Keep providers behind `IUsageProvider` / `IUsageAccountService`. Never combine accounts' percentages,
  windows, caches or reset credits. Selection changes the detail/tray/widget account, not another app's login.
  Preserve nicknames, order, selected IDs, provider badges and existing settings/cache compatibility.
- Codex data comes from the installed, signed-in App Server. Classify windows by `windowDurationMins`,
  not primary/secondary position. Keep root metadata separate from selected-bucket metadata.
  Missing optional windows are allowed; malformed protocol input is a failure, not successful empty data.
  Unknown percentages/credit expiries stay unknown. Reset-credit IDs stay in memory; persist expiry timestamps only.
- Claude values describe the **shared Web·Desktop·Code subscription quota**, last delivered via Code's
  official statusLine `rate_limits.five_hour` / `seven_day` (`used_percentage`, `resets_at`).
  Preserve fractional percentages. Code is the delivery source, not the whole scope of account usage.
- Label Claude samples as received, not live/current. Keep idle samples in Received with last-good values
  and their original receipt time, including after reported reset times pass. Reserve stale warnings for
  missing/malformed input, cache/identity failures or invalid receipt metadata. Elapsed time or a passed
  reset alone must not raise attention.
  Keep the last receipt date/time and shared scope in the UI.
  Polling, manual refresh and opening the usage page must not renew a receipt or invent zero after reset.
- Do not force a Claude quota refresh without a supported, safe official query. The current refresh reads
  the local inbox only; the usage-page button opens the normal browser without collecting its contents.
  Never scrape usage pages, parse `/usage`, call undocumented quota endpoints, or run a model turn to measure limits.
- Connection and usage receipt are separate. Connected Claude accounts remain visible with unknown limits
  and **Awaiting usage**, without increasing attention totals. Unconnected/disconnected profiles stay in management;
  temporary failures retain usable stale data. Popup, tray, widget, counts and fallback selection use one projection.
- Reconnecting the same verified Claude binding reuses the existing profile. Remove only empty drafts created by
  the cancelled/failed connection flow; preserve connected profiles and saved data. Disconnection survives restart.
- Claude authentication uses official `auth login --claudeai` / `auth status --json`, with bounded, cancellable,
  single-flight operations. StatusLine does not verify identity: preserve configuration/account binding checks,
  reject old or mismatched callbacks, and preserve unrelated settings and any existing statusLine command/output.
- Persist only projected quota, receipt and binding metadata. Never read/copy/monitor CLI credential files,
  tokens, cookies, prompts, responses or conversation files; never log secrets or add telemetry.
  Keep Claude email in memory and login with the official flow. Diagnostics/tests must not change real accounts/settings.
- Unknown usage is never zero or a fabricated request count. Failed attempts must not advance the last success.
  Preserve the last valid snapshot on transient failure and across restart. Cache/settings writes remain atomic
  with a valid backup; branding changes must preserve `LegacyInstallation` data/mutex/registry identifiers.
- Marshal background and system-event callbacks to the WPF Dispatcher. Bound subprocess startup, requests,
  cancellation and shutdown; do not leak child processes. Shared refresh is single-flight across entry points:
  only its owner clears busy state, with visible progress until the operation actually finishes.
- Keep enabled widget visibility separate from WPF's cached `IsVisible` state. Preserve native
  hide/minimize/topmost and resume/unlock/display recovery, saved position and per-window event
  subscriptions. Recovery must not steal focus, revive disabled/accountless widgets or run after exit.
- Use the existing Korean/English localization and Dark/Light/System resources. State must be clear without
  relying only on color or hover. Preserve readable controls, scaling, scrolling and widget recovery after DPI changes.
  Keep native tray text within 127 characters, retaining Claude freshness/receipt/scope before long nicknames.
- Windows owns the native notification-icon slot. Do not restore taskbar overlays, Explorer hooks, browser
  extensions, WebView2, conversation synchronization or active SQLite history collection. Retained legacy code
  is not a supported runtime path. Starting with Windows requires opt-in; preserve saved widget preferences.
- The bounded headless Claude receiver runs before WPF/mutex/account startup in the same executable.
  Ship no companion host or extension. Codex reset-credit actions remain Codex-only.

## Verification matched to the change

| Change | Required local checks |
| --- | --- |
| Markdown, instructions, comments or Git tracking only | Review the diff; check affected links, paths and command accuracy. No app build, publish, reinstall or model call solely for this change. |
| Core/provider behavior or a correctness bug | Relevant regression tests, including malformed/unknown input and state transitions where applicable. Synthetic fixtures must match the official protocol shape. |
| WPF state, layout, localization, theme or interaction | Release build and affected `CycleArc.UiSmoke` checks; visually inspect affected views in English/Korean and relevant themes/sizes. |
| Login, cache, concurrency or account lifecycle | Deterministic failure/cancellation/restart tests; isolated data roots and fake provider/process adapters. Live checks only when required by the task. |
| Installer, startup, executable routing or distribution | Installer rollback scenarios, single-file publish and built/published Claude receiver checks via the full gate below. |

- Before delivering a changed executable, run `pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch` once on the final
  executable-producing changes. It restores, builds/tests Release, runs WPF/installer checks and validates the
  win-x64 single-file artifact and headless receiver. `-NoLaunch` leaves the installed app untouched.
- For targeted unit checks use `dotnet test tests/CycleArc.Tests/CycleArc.Tests.csproj -c Release --filter ...`;
  use a real matching test filter. Use `--no-build` only after building the code being checked.
- After checks pass, broaden or repeat them only for a new change, failure or unresolved concern.
  Do not add tests that merely restate a text edit. Preserve meaningful regression coverage.
- Use production views with synthetic accounts for screenshots. Inspect and replace only affected previews;
  do not regenerate unrelated images just to change dates. Update relevant EN/KO guidance when behavior changes.
- Claim only checks actually run. Distinguish fixture coverage from real-account verification; an awaiting
  Claude sample is not evidence of received usage. Do not create a model request to satisfy a test.

## Git and delivery

- Review the final diff and commit the intended changes. Push to the configured origin unless the user says
  otherwise; preserve existing authorization and respect any host approval block. Never force-push.
- On a branch's first push use `git push -u origin <branch>` with its actual name. If already published without
  tracking, verify the remote branch, then use `git branch --set-upstream-to=origin/<branch> <branch>`.
- Verify upstream, ahead/behind state and the actual remote SHA against local HEAD. A successful push alone
  does not prove tracking is configured; this prevents GitHub Desktop showing **Publish branch** after upload.
- Check the CI run for the pushed SHA. Fix relevant failures and verify the corrected run; report pending,
  passed or failed accurately. If publication is blocked, state the exact blocker and that the commit is local.
- Install/restart only when the task calls for a working local executable. Use the validated artifact and
  existing guarded installer/rollback path; confirm the target checkout, running path and file hash.
  Documentation-only changes do not require replacing the user's running app.
- Finish with what changed, verification results and material limitations, using concise Korean for this user.
  Do not stop at a local commit when push/CI or an authorized installation still remains.
