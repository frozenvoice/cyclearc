# CycleArc — Claude / agent notes

This repository is **CycleArc**, a Windows tray app for Codex limits and Claude's shared subscription quota. Claude Code statusLine delivers the last received Claude sample. ChatGPT history collection is retired.

Follow [AGENTS.md](AGENTS.md) for current product contracts, scoped document reading, verification and delivery. Read only the documents relevant to the actual change; ordinary Codex/Claude presentation work does not require legacy reconstruction documents.

For retained ChatGPT history/reconstruction or browser/companion maintenance only, read [legacy maintenance](docs/LEGACY-MAINTENANCE.md) and its relevant references.

Keep this file a pointer; do not duplicate or broaden the instructions here. The facts below are
the minimum needed to work on installation, updates and Claude-owned settings without re-deriving
them; everything else stays in AGENTS.md and the scoped documents.

## Layout

| Path | Contents |
| --- | --- |
| `src/CycleArc` | WPF desktop, tray, installation bootstrap, Velopack client, update supervisor (`net8.0-windows`, Windows only) |
| `src/CycleArc.Core` | Providers, presentation, persistence, update coordinator/recovery, Claude connection and callbacks (`net8.0`, portable) |
| `tests/CycleArc.Tests` | xunit unit and regression tests (`net8.0`) |
| `tests/CycleArc.UiSmoke` | Production WPF checks, process checks, documentation previews (`net8.0-windows`) |
| `scripts`, `tests/*.ps1`, `dev-run.ps1` | Packaging, release, local installation and verification scripts (PowerShell 7) |

## Commands

Windows, PowerShell 7 and the .NET 8 SDK are required; the desktop and UiSmoke projects do not
build on other platforms.

```powershell
dotnet test tests/CycleArc.Tests/CycleArc.Tests.csproj -c Release --filter "<test-name>"
dotnet build CycleArc.sln -c Release
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build
pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch    # full gate; drop -NoLaunch to also install and run
pwsh -NoProfile -File ./scripts/Verify-InstalledUpdate.ps1 -ConfirmDisposableEnvironment
```

The last one installs, updates, recovers and removes a real installation for the current Windows
user. Run it only on a disposable VM or throwaway user; see its help for what it cannot isolate.

## Installation and update boundaries

- Velopack owns the packaged installation. A new install goes to `%LOCALAPPDATA%\Programs\CycleArc`;
  one that already exists keeps its location, including the former `%LOCALAPPDATA%\CycleArc`,
  so resolve it with `Get-ManagedInstallRoot` rather than assuming either. The desktop runs from
  `current\CycleArc.exe` under that root; the root `CycleArc.exe` is the launcher used for
  shortcuts and Windows startup. The development single-file build replaces itself in
  `%LOCALAPPDATA%\Programs\CycleArc-dev` and must never share a directory with a managed install.
- CycleArc owns approval, verification and recovery: the update window asks before downloading and
  before restarting, packages are SHA-256 checked after download and again before apply, and
  `ManagedUpdateSupervisor` runs outside the installation so it can restore and restart the previous
  version when apply or startup fails. Never let a caller reach `Update.exe` around that supervisor.
- Removal is Velopack's: it stops the app, runs the uninstall hook once with a bounded budget,
  ignores the result and deletes the installation root. Uninstall cleanup therefore has to be
  time-boxed, must never throw, and can be neither cancelled nor retried from app code.

## User data

`%LOCALAPPDATA%\ProMeter` holds accounts, settings, quota caches and Claude connection records and
is outside every installation. Installing, updating, recovering and removing must not delete,
rewrite or log out any of it, and removal must not even create it. Failure states keep the last
good data rather than inventing an empty one.

## Claude-owned settings

Claude's `settings.json` belongs to the person, not to CycleArc. Only an exact CycleArc-generated
wrapper is ours: it decodes to the same command byte for byte, names this profile, names the same
configuration directory, and — when an installation is being removed — names an executable inside
the installation being removed. Anything else (a replaced statusLine, another tool's hook, another
installation's wrapper, an unrelated property) stays untouched, and "absent" and "explicitly null"
remain different outcomes.

## Done means

- Focused tests first, then the affected gate; state what actually ran.
- Separate real verification from synthetic verification. A packaged `Setup.exe`, a fixture that
  rewrites `sq.version`, or a direct `Update.exe` call is not an installed-app update; synthetic
  Claude accounts are not evidence of live subscription usage; a file that did not change is not
  proof that the app can still read it. Say which one a result is.
- Record what was not run and why, rather than implying wider coverage.
