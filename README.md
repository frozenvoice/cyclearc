# CycleArc

**Your Codex, Claude and Cursor limits, one click away.**

A native Windows tray app for checking each account's usage, remaining allowance and reset times. An optional desktop widget compares accounts side by side. Accounts keep separate limits and refresh states; unknown values stay unknown. English/Korean and Dark/Light/System themes are included.

[![Windows build](https://github.com/frozenvoice/cyclearc/actions/workflows/windows.yml/badge.svg)](https://github.com/frozenvoice/cyclearc/actions/workflows/windows.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[Download for Windows](https://github.com/frozenvoice/cyclearc/releases/latest) · [한국어](docs/README.ko.md) · [Detailed user guide](docs/README.en.md) · [Report an issue](https://github.com/frozenvoice/cyclearc/issues)

## Get started

The .NET 10 requirements below describe the 0.10.0 source build. The latest-release link serves the published version; its release notes identify the bundled runtime.

**Windows x64:** use a supported Windows 11 release or Windows 10 Enterprise LTSC 1809 or Enterprise/IoT Enterprise LTSC 21H2 edition. The Windows API minimum remains 1809; ordinary Windows 10 editions are outside .NET 10 vendor support. See [OS compatibility](docs/DOTNET10-MIGRATION.md#operating-systems). The 0.10.0 source build bundles the .NET 10 runtime; no separate .NET installation is needed.

1. Download **`CycleArc-Setup.exe`** from the [latest release](https://github.com/frozenvoice/cyclearc/releases/latest).
2. Run it, review the installation location, optionally select **Create a desktop shortcut**, then choose **Install → Run CycleArc → Finish**. New installations use `%LOCALAPPDATA%\Programs\CycleArc`; existing installations keep their registered location, including `%LOCALAPPDATA%\CycleArc`.
3. Open the tray icon and **Manage accounts → Add an account**. Connect an existing Codex login or sign in to another account; choose **Connect Claude** or **Connect Cursor** for those providers.
4. Click the tray icon for details, refresh immediately, or enable the widget in Settings. Windows startup is opt-in. An ordinary launch opens the first running instance and shows its version.

Codex requires the installed [Codex CLI](https://developers.openai.com/codex/cli/) and network access; the CLI is not bundled. Claude requires the official Claude CLI for connection verification and the same signed-in Claude Desktop account for live checks (Pro/Max). Claude statusLine and Desktop history remain fallback sources. Cursor uses the current login in the Windows Cursor app; custom `--user-data-dir` locations are not searched.

## Running and updating versions

CycleArc checks GitHub Releases 20 seconds after startup and every six hours. Review the release notes, choose **Download update**, then **Restart & update** when ready, or choose **Later**. Updates require these actions. Packages are checked for SHA-256, size and path before they can be applied.

If replacement or initial desktop readiness fails, a separate recovery process restores and restarts the verified previous app. If recovery is blocked, CycleArc retains the backup and reports its location. Settings, accounts and quota caches stay in `%LOCALAPPDATA%\ProMeter` across installation, update, recovery and removal. [Installation, callbacks and removal details](docs/README.en.md#running-and-updating-versions).

Release artifacts are unsigned unless an approved code-signing pipeline is configured; an installer alone does not remove Windows SmartScreen warnings. [.NET migration, compatibility and signing conditions](docs/DOTNET10-MIGRATION.md).

## Troubleshooting

- **Codex not found:** install the CLI, or select its executable in **Settings → Connection**. **Login changed / Check connection:** [reconnect the affected profile](docs/README.en.md#codex-connection-recovery); account selection does not change another app's login.
- **Claude awaiting usage or stale data:** sign into the same Claude Desktop account and refresh. Local receipts keep their original time; unknown reset times stay unknown. Repeated server throttling can require a longer interval in **Settings → Connection**. [Claude connection and recovery](docs/README.en.md#claude-code-connection).
- **Cursor login changed:** sign back into the original account in Cursor to reconnect the profile. [Cursor connection and quota details](docs/CURSOR.md).
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

The [detailed user guide](docs/README.en.md) preserves account examples, production-view previews, controls and earlier-release guidance. See [Claude integration](docs/CLAUDE-INTEGRATION.md), [Cursor integration](docs/CURSOR.md), [architecture](docs/ARCHITECTURE.md) and [validation evidence](docs/VALIDATION.md) for implementation details.

CycleArc makes no model requests to measure usage and collects no prompts, conversations or telemetry. Credentials used for read-only provider checks stay in memory; projected quotas and local preferences remain local. [Data and privacy details](docs/README.en.md#privacy).

## Build from source

Use Windows, PowerShell 7 and the .NET 10 SDK selected by [`global.json`](global.json). Building the Native AOT Setup window also requires Visual Studio 2022 / Build Tools with **Desktop development with C++**, MSVC x64/x86 tools and a Windows SDK.

```powershell
git clone https://github.com/frozenvoice/cyclearc.git
cd cyclearc
pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch
```

This runs the shared build, tests, WPF smoke and packaging gate without replacing the installed app. Drop `-NoLaunch` to publish and run the separate development installation. Double-click `build-local.cmd` to build and install the stable package from the current checkout. See [developer workflow and disposable installer checks](docs/README.en.md#build-from-source) and [agent instructions](AGENTS.md).

## License

[MIT](LICENSE). CycleArc is an independent project and is not affiliated with or endorsed by OpenAI or Anthropic.
