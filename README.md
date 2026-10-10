# CycleArc

**Your Codex, Claude and Cursor limits, one click away.**

A native Windows tray app for checking each account's usage, remaining allowance and reset times. An optional desktop widget compares accounts side by side. Accounts keep separate limits and refresh states; unknown values stay unknown. English/Korean and Dark/Light/System themes are included.

[![Windows build](https://github.com/frozenvoice/cyclearc/actions/workflows/windows.yml/badge.svg)](https://github.com/frozenvoice/cyclearc/actions/workflows/windows.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[Download for Windows](https://github.com/frozenvoice/cyclearc/releases/latest) · [한국어](docs/README.ko.md) · [Detailed user guide](docs/README.en.md) · [Report an issue](https://github.com/frozenvoice/cyclearc/issues)

## Get started

The .NET 10 requirements below describe the 0.10.0 source build. As of 2026-10-09, the published stable release is 0.10.0. The latest-release link serves the published version; its release notes identify the bundled runtime.

**Windows x64:** use a supported Windows 11 release or Windows 10 Enterprise LTSC 1809 or Enterprise/IoT Enterprise LTSC 21H2 edition. The Windows API minimum remains 1809; ordinary Windows 10 editions are outside .NET 10 vendor support. See [OS compatibility](docs/DOTNET10-MIGRATION.md#operating-systems). The 0.10.0 source build bundles the .NET 10 runtime; no separate .NET installation is needed.

1. Download **`CycleArc-Setup.exe`** from the [latest release](https://github.com/frozenvoice/cyclearc/releases/latest).
   Starting with the next release produced by this source, the same GitHub Release also offers **`CycleArc-<version>-win-x64-portable.zip`**. Extract it to a writable folder and run its single self-contained `CycleArc.exe` in place; no separate .NET installation is needed. Portable runs do not install themselves, register Windows startup, change installation/tray registry entries, or use the managed in-app updater. The existing 0.10.0 release and its tag remain unchanged.
2. Run it, review the installation location, optionally select **Create a desktop shortcut**, then choose **Install → Run CycleArc → Finish**. New installations use `%LOCALAPPDATA%\Programs\CycleArc`; existing installations keep their registered location, including `%LOCALAPPDATA%\CycleArc`.
3. Open the tray icon and **Manage accounts → Add an account**. Connect an existing Codex login or sign in to another account; choose **Connect Claude** or **Connect Cursor** for those providers.
4. Click the tray icon for details, refresh immediately, or enable the widget in Settings. Installed Windows startup is opt-in; portable startup registration is unavailable. An ordinary launch opens the first running instance and shows its version.

Codex requires the installed [Codex CLI](https://developers.openai.com/codex/cli/) and network access; the CLI is not bundled. Claude requires the official Claude CLI for connection verification and the same signed-in Claude Desktop account for live checks (Pro/Max). Claude statusLine and Desktop history remain fallback sources. Cursor uses the current login in the Windows Cursor app; custom `--user-data-dir` locations are not searched.

## Running and updating versions

Installed CycleArc checks GitHub Releases 20 seconds after startup and every six hours. Review the release notes, choose **Download update**, then **Restart & update** when ready, or choose **Later**. Updates require these actions. Packages are checked for SHA-256, size and path before they can be applied.

For portable updates, exit CycleArc, download the new ZIP from Releases, replace `CycleArc.exe` in the same folder, and run it again. Portable and installed copies share `%LOCALAPPDATA%\ProMeter` accounts, settings and cache. In the same Windows session, the first desktop instance wins; exit it before switching distributions. New builds use a shared data lock across sessions of the same Windows account; older releases do not participate, so exit older desktops in every session first. Keep the portable folder path when updating so configured callbacks can still find the EXE; reconnect those integrations after moving it. See the [portable and callback details](docs/README.en.md#running-and-updating-versions).

If replacement or initial desktop readiness fails, a separate recovery process restores and restarts the verified previous app. If recovery is blocked, CycleArc retains the backup and reports its location. Settings, accounts and quota caches stay in `%LOCALAPPDATA%\ProMeter` across installation, update, recovery and removal. [Installation, callbacks and removal details](docs/README.en.md#running-and-updating-versions).

Release artifacts are unsigned unless an approved code-signing pipeline is configured; an installer alone does not remove Windows SmartScreen warnings. [.NET migration, compatibility and signing conditions](docs/DOTNET10-MIGRATION.md).

## Troubleshooting

- **Codex not found:** install the CLI, or select its executable in **Settings → Connection**. **Login changed / Check connection:** [reconnect the affected profile](docs/README.en.md#codex-connection-recovery); account selection does not change another app's login.
- **Claude awaiting usage or stale data:** sign into the same Claude Desktop account and refresh. Local receipts keep their original time; unknown reset times stay unknown. Repeated server throttling can require a longer interval in **Settings → Connection**. [Claude connection and recovery](docs/README.en.md#claude-code-connection).
- **Cursor login changed:** sign back into the original account in Cursor to reconnect the profile. [Cursor connection and quota details](docs/CURSOR.md).
- **Cursor recent request shows Disconnected:** CycleArc's entries are no longer in Cursor's `hooks.json`. Turn **Settings → Connection → Cursor recent request** off, apply, then turn it on again to reconnect; remaining usage is unaffected. [What the recent request means](docs/README.en.md#cursor-connection).
- **Widget missing:** enable **Show widget**, check that an account is displayable, then reset its position in **Settings → Widget**. [Controls and widget recovery](docs/README.en.md#controls).
- **Report a failure:** include the running version, error and reproduction steps in an [issue](https://github.com/frozenvoice/cyclearc/issues). Omit credentials and conversation content.

## User guide and implementation

<a id="at-a-glance"></a>
<a id="accounts"></a>
<a id="claude-code-connection"></a>
<a id="cursor-connection"></a>
<a id="controls"></a>
<a id="codex-connection-recovery"></a>
<a id="how-it-works"></a>
<a id="privacy"></a>
<a id="upgrading-from-earlier-releases"></a>

The [detailed user guide](docs/README.en.md) includes account examples, production-view previews, controls and earlier-release guidance. Current popup, widget and settings previews use the 0.10.0 development source and synthetic accounts. The development popup header uses a green dot for confirmed current usage, a warning icon for accounts needing attention and a neutral icon for pending or local receipts. Hover for the full status; the refresh button spins during a check. Header actions are grouped as zoom, refresh/settings and pin/close. The pin button toggles keeping the popup above other windows; an upright filled pin with selected chrome means pinned, and an angled outline means unpinned. Tab to it and press Space or Enter to toggle. The rightmost **×** hides the popup while CycleArc keeps running, including when pinned. The account dropdown selects the account displayed in CycleArc. The account cards below retain their errors and connection-recovery notices; selection changes the CycleArc display only.

The popup and widget keep separate saved zoom levels. A small percentage between each header's magnifying-glass −/+ buttons shows the current size relative to the original 100%, and updates with buttons or shortcuts. Localized tooltips and accessible names match `Ctrl + +` / `Ctrl + -` (80–150%); `Ctrl + 0` restores 100%. The popup briefly identifies the actual display period and any fallback. Full shared-period behavior is explained once in **Settings → General → Display period & tray guide**. Healthy, unprovided credit details use a small folded row; zero, unknown, old data, failures and reconnection requirements remain distinct.

The popup and widget focus on current quotas, remaining allowance, reset times and check/receipt status. Their current source has no usage-history graph or selected-account sparkline. Current provider source times, last checked/received times and cached last-good values remain available. CycleArc does not provide depletion forecasts or consumption-rate predictions. [Architecture](docs/ARCHITECTURE.md#current-quota-and-retired-observation-history) and [validation evidence](docs/VALIDATION.md) describe the removal; [Claude](docs/CLAUDE-INTEGRATION.md) and [Cursor](docs/CURSOR.md) retain their provider contracts.

CycleArc makes no model requests to measure usage and collects no prompts, conversations or telemetry. Credentials used for read-only provider checks stay in memory; projected quotas and local preferences remain local. [Data and privacy details](docs/README.en.md#privacy).

## Build from source

Use Windows, PowerShell 7 and the .NET 10 SDK selected by [`global.json`](global.json). Building the Native AOT Setup window also requires Visual Studio 2022 / Build Tools with **Desktop development with C++**, MSVC x64/x86 tools and a Windows SDK.

After you approve the `build-local.cmd` installer, it waits for verified desktop IPC shutdown, including an existing `CycleArc-dev` instance, before starting the installation engine. Cancellation or failed shutdown prevents installation. The completion page's Run choice is preserved, and a running app must match the installed path, build version and published SHA-256; an older development instance cannot count as this build running.

`build-local.cmd` can start with only Windows PowerShell: it surveys PowerShell 7, the selected stable .NET 10 SDK and the VS2022 C++ toolchain together. Direct interactive execution automatically prepares missing official Microsoft tools without a prerequisite selection menu, verifies signatures and actual installed commands/files after each installation, then continues the build in the same run. Windows UAC and company policy still apply; a required reboot stops the run. Use `build-local.cmd -ManualPrerequisites` to show the environment state and official manual instructions without preparing tools or building. CI, redirected/noninteractive consoles, `-NoPrerequisitePrompt` and `-SilentInstall` fail without installing prerequisites. The shipped self-contained `CycleArc-Setup.exe` needs none of these development tools.

The SDK signature check accepts Microsoft's documented `.NET` certificate subject as well as its `Microsoft Corporation` signer, and still requires a valid Windows Authenticode result and the official SHA-512. Policy blocks and required reboots from an interactive `build-local.cmd` show a Windows error acknowledgement with the cause and log location before the console closes. Other prerequisite failures record their cause in the console and failure log without requesting input.

```powershell
git clone https://github.com/frozenvoice/cyclearc.git
cd cyclearc
pwsh -NoProfile -File ./dev-run.ps1 -DevelopmentOnly
```

This runs the shared Release build and unit tests without publishing, packaging, installing or launching the app. Add `-TestFilter "<matching-filter>"` for relevant tests or `-BuildOnly` for compile only. Run affected WPF checks explicitly. `dev-run.ps1 -NoLaunch` remains the full publish/package gate; dropping `-NoLaunch` also installs/runs the separate development installation. Double-click `build-local.cmd` to build and install the stable package from the current checkout. All Windows Actions workflows are manual-only; ordinary push/PR work requires relevant local checks and no remote CI wait. Releases select an explicit successful full run with `Release.ps1 -FullRunId <id>`. See [developer workflow and disposable installer checks](docs/README.en.md#build-from-source) and [agent instructions](AGENTS.md).

## License

[MIT](LICENSE). CycleArc is an independent project and is not affiliated with or endorsed by OpenAI or Anthropic.
