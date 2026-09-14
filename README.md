# CycleArc

**Your Codex and Claude limits, one click away.**

A native Windows tray app for checking multiple Codex and Claude profiles, remaining percentages and reset times. Codex accounts also show reset credits when available.

The **Codex** or **Claude** label on account cards, selected details, tray tooltips and the widget identifies the usage provider. Connect the current Claude login or sign in through the official browser flow; CycleArc sets up its **statusLine** connection automatically. See [Claude Code connection](#claude-code-connection). Gemini is not supported.

> **Codex:** five-hour and weekly limits appear when the official App Server reports them, including on Plus; display is not restricted by plan name. Missing windows are omitted and unknown percentages stay unknown. **Claude:** the official statusLine must supply the requested rate-limit fields; these can be absent before the first response or on unsupported plans.

[![Windows build](https://github.com/frozenvoice/cyclearc/actions/workflows/windows.yml/badge.svg)](https://github.com/frozenvoice/cyclearc/actions/workflows/windows.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows x64](https://img.shields.io/badge/Platform-Windows_x64-0078D4.svg)](#get-started)

[Download for Windows](https://github.com/frozenvoice/cyclearc/releases/latest) · [Multi-account examples](#accounts) · [한국어](docs/README.ko.md) · [Report an issue](https://github.com/frozenvoice/cyclearc/issues)

<table>
  <tr>
    <td align="center"><strong>Dark</strong></td>
    <td align="center"><strong>Light</strong></td>
  </tr>
  <tr>
    <td><img src="docs/images/overview-dark.png" alt="English dark view showing weekly usage, remaining percentage, reset countdown, and reset credits" width="440"></td>
    <td><img src="docs/images/overview-light.png" alt="The same Codex usage view in the light theme" width="440"></td>
  </tr>
</table>

*English previews rendered from the production WPF views using sample quota data. Available windows and reset-credit details depend on what your account reports.*

## At a glance

- **Codex and Claude together.** Connect an existing CLI login or sign in through the official browser flow. Connected Claude accounts appear immediately, with **Awaiting usage** until the first sample; choose which account appears in the tray and widget.
- **Usage in the tray.** A Windows notification-area icon keeps the meter within reach. Click for the detailed card; pin it to keep it visible.
- **Clear quota windows.** See usage and remaining percentages, reset times and countdowns for each reported five-hour or weekly window. The ring, tray and widget use the weekly percentage when known, otherwise the five-hour percentage.
- **Reset credits.** View the available count and expiry times when the server supplies them. Use an individual reset after confirmation. Missing expiry information stays explicitly unknown.
- **Optional desktop widget.** A compact, draggable meter with adjustable opacity, always-on-top, and click-through options. Off-screen positions recover automatically.
- **Your preferred appearance.** Dark, Light, or live System theme; English and Korean; keyboard zoom from 80% to 150%.
- **Honest refresh states.** Check Codex at a selectable 1, 2, 5, 10, 30 or 60-minute interval (default: five minutes). Claude shows the last received shared subscription quota, delivered through Claude Code. Previously received values remain visibly stale during a temporary update failure; unconnected profiles stay in account management.

## Get started

**Requirements:** Windows 10/11 on x64. Codex monitoring requires an installed Codex CLI signed into a ChatGPT account that reports subscription limits, and network access. Claude monitoring requires the Claude Code terminal CLI with official statusLine rate-limit support and Windows PowerShell; it does not require Codex sign-in.

1. Download **`CycleArc.exe`** from the [latest release](https://github.com/frozenvoice/cyclearc/releases/latest).
2. Put it in a folder you want to keep and run it. The .NET runtime is bundled; there is no separate runtime installer.
3. Open the tray icon and **Manage accounts → Add an account**. For Codex, existing CLI sign-ins are discovered automatically; choose **New account sign-in** to add another account. For Claude, choose **Connect Claude**, then connect the current login or sign in through your browser.
4. Codex appears after a successful quota check; Claude appears after verified connection, with unknown limits until usage arrives. If Codex cannot be found, install the [Codex CLI](https://developers.openai.com/codex/cli/) or open **Settings → Connection** and select its executable path. For Claude setup, follow the [connection steps below](#claude-code-connection).

CycleArc discovers `codex.exe` or `codex.cmd` through PATH and supported installation locations. The Codex CLI itself is not bundled. Sign-in remains managed by Codex.

### Accounts

**Manage accounts** is available in the popup and **Settings → Connection → Manage Codex and Claude accounts**. **Add an account** offers Codex login/discovery and Claude connection. It opens automatically when there is no previously checked account; existing users see their account list first. Accounts retain separate percentages, reset windows and refresh states; values are never added together. Reset credits belong only to the selected Codex account.

**Connected Claude accounts remain visible while awaiting usage.** They show **Awaiting usage** and unknown limits, without increasing the attention count. Unconnected profiles stay in **Manage accounts** and do not increase the main account count. Previously received values remain visible as stale during a temporary update failure. Popup, tray and widget share the same visible selection; if none is available, the widget stays hidden and the popup shows a connection hint.

<table>
  <tr>
    <td align="center"><strong>Two ready accounts · Dark</strong></td>
    <td align="center"><strong>Two ready accounts · Light</strong></td>
  </tr>
  <tr>
    <td><img src="docs/images/accounts-overview-en-dark.png" alt="Dark popup with Personal and Work Codex accounts, Work selected, count two and all updated; the unconnected Claude profile is hidden" width="440"></td>
    <td><img src="docs/images/accounts-overview-en-light.png" alt="The same two ready Codex accounts in the light theme, without a card for the unconnected Claude profile" width="440"></td>
  </tr>
</table>

*These are production UI renders with entirely fictional accounts and quota data, not captures of a user's account. Names, `example.invalid` email addresses, percentages, reset times and credits are samples.*

Read the example from the account list down to the detail card:

1. **Compare accounts separately.** Personal has used 18% and Work 64% of their own Codex weekly windows. These percentages are not combined into one allowance.
2. **Choose the detail account.** The blue border and dot mark Work as selected, so the ring shows Work's **64% used**, with **36% left** in the quota row. The reset-credit card also belongs to Work. Clicking another account changes the detail card, tray and widget; it does not switch the login used by your other Codex apps or start a refresh.
3. **Keep unconnected profiles out of the overview.** The previously registered Research Claude profile has not been connected. It remains in account management, but the main view shows **2 accounts** and **All updated**. Connecting it makes it visible immediately; see the [Claude example](#claude-code-connection).

To connect an account, choose the option that matches your setup:

| Your situation | Choose | What happens |
| --- | --- | --- |
| First Codex use, or adding another Codex account | **Add an account → New account sign-in** | Complete the official browser login for that account; CycleArc checks its usage and keeps its Codex home separate. |
| Already signed into Codex CLI on this PC | **Find accounts on this PC** | Connect the existing login from a known Codex home. A ChatGPT website-only login is not enough. |
| Already use a custom `CODEX_HOME` | **Advanced · Connect a specific Codex folder → Choose Codex home folder** | Select that home folder, not the Codex executable or a project folder. |
| Already signed into Claude Code | **Add an account → Connect Claude → Connect current login** | Verify the current official CLI login and configure usage updates automatically. |
| Adding a separate Claude login | **Add an account → Connect Claude → Sign in to Claude** | Complete official browser login in a separate Claude configuration. When already signed in, this button reads **Sign in to another account**. |

Set a **Nickname in CycleArc** to recognize an account; leaving it empty shows the reported email, or a provider/profile label when email is unavailable. Codex supplies email/plan through its account API; Claude supplies login metadata through its official CLI. Circular icons are generated locally from the displayed name and a stable account color. They are not synced web avatars; saving a nickname does not change the provider profile. The widget shows this name even when only one account is connected. Expand **Nicknames, icons and account actions** for details.

Use **Order ↑ / ↓** beside each account's nickname to move it. The popup follows the same order among visible accounts, which is saved immediately and retained after restart. Reordering keeps the selected tray/widget account and does not initiate a quota refresh. Select a card with usage to change which account drives the tray/widget. **Sign in again** changes the login for a Codex profile added in CycleArc; **Connect** opens a Claude profile's connection window. **Remove** forgets the profile from this list.

<details>
<summary><strong>Account manager · Dark preview</strong></summary>

<p>The account manager retains all three profiles, including the unconnected Research profile. Its summary cannot be selected yet, while Connect, nickname and order actions remain available. The main popup above counts only the two ready accounts. Work stays selected while you arrange the list.</p>
<p><img src="docs/images/accounts-manage-en-dark.png" alt="Dark account manager with two synthetic Codex accounts and an unconnected Research Claude profile whose Connect button remains available" width="700"></p>

</details>

<details>
<summary><strong>Account manager · Light preview</strong></summary>

<p><img src="docs/images/accounts-manage-en-light.png" alt="Light account manager retaining all three synthetic profiles, selected Work and the unconnected Claude profile" width="700"></p>

</details>

**Find accounts on this PC** checks `CODEX_HOME` from the process/user/machine environment and the default `~/.codex` directory through `account/read`. It cannot discover an account signed in only on the ChatGPT website. **Advanced · Connect a specific Codex folder → Choose Codex home folder** connects another known home; choose the home, not the executable or a project folder. It does not search the disk for credentials or copy an existing login. Imported homes stay linked to their original Codex installation; reauthenticate those in Codex itself.

New Codex profiles receive separate homes under `%LOCALAPPDATA%\ProMeter\accounts\<local-id>\codex-home`. Only the installed Codex process stores and renews credentials there. Browser sign-in has a five-minute deadline and can be cancelled. Choose the intended email account in the browser; if two profiles report the same email and plan, both display a matching-login notice. The protocol does not expose a stable workspace identifier, so that notice does not establish whether workspaces are identical.

Removing a profile forgets its reference without logging out or deleting its Codex home. It will not be automatically re-added. A custom home can be connected again with the folder picker.

The first launch opens the detail card. Later launches start in the tray; `CycleArc.exe --show` opens the card at startup. If Windows hides the tray icon, move it out of the notification-area overflow. Starting with Windows and showing the desktop widget are optional settings.

### Claude Code connection

**Claude subscription usage is shared across Web, Desktop and Code.** CycleArc displays the last shared-quota sample received through Claude Code. Web/Desktop activity also consumes that allowance, so the current account usage may have changed since the sample. [Official usage-limit explanation](https://support.claude.com/en/articles/11647753-how-do-usage-and-length-limits-work).

To check current usage without using Code, choose **Open usage page** on the Claude detail card or connection window. It opens [Claude Settings → Usage](https://claude.ai/settings/usage) in your browser; check that the intended account is signed in. Opening the page does not refresh CycleArc. A review of official APIs, CLI and SDK documentation found no supported independent query for a personal subscription's current shared quota. See the [dated research and decision](docs/CLAUDE-USAGE-RESEARCH.md).

1. Open **Manage accounts → Add an account → Connect Claude**. Set an optional nickname.
2. Choose **Connect current login** to use the signed-in Claude CLI, or **Sign in to Claude** to complete the official browser login. CycleArc verifies `claude auth status --json` and configures the connection automatically. Other settings and the existing status line are preserved; no JSON copying is required.
3. During normal **Claude Code terminal (CLI)** use, responses can supply shared subscription usage. **Open Claude Code terminal…** launches it with that profile's configuration and your chosen working folder. Use it when awaiting the first sample, especially for a separate login created by CycleArc. Only the official `rate_limits.five_hour` / `rate_limits.seven_day` → `used_percentage` and `resets_at` fields supply usage; absent values stay unknown.

**CycleArc supports usage receipt from the connected Claude Code terminal, not Web or the Desktop Code tab.** Connection and usage receipt are separate: a connected account stays visible with **Awaiting usage** until the connected Claude Code terminal sends a sample. Repeating **Connect current login** reuses the same verified configuration binding and preserves its name and usage history. Closing or cancelling a new connection before it succeeds removes its empty draft from the list.

<details>
<summary><strong>Connected, awaiting usage · Dark and Light</strong></summary>

<p><img src="docs/images/claude-waiting-en-dark.png" alt="Connected Claude awaiting shared subscription usage, with unknown limits and an Open usage page action" width="440"> <img src="docs/images/claude-waiting-en-light.png" alt="The same connected waiting state in the light theme" width="440"></p>

</details>

<table>
  <tr><td align="center"><strong>Automatic connection · Dark</strong></td><td align="center"><strong>Automatic connection · Light</strong></td></tr>
  <tr>
    <td><img src="docs/images/claude-connection-en-dark.png" alt="Production Claude connection window with synthetic signed-in identity, current-login connection, another-account login and Open Claude Code terminal buttons" width="530"></td>
    <td><img src="docs/images/claude-connection-en-light.png" alt="The same automatic Claude connection window in the light theme; no manual JSON entry" width="530"></td>
  </tr>
</table>

*The connection previews use a synthetic login adapter and a reserved example address. No real login or account settings are accessed when these images are generated.*

The official statusLine JSON has **no account email or account ID**. The official CLI supplies the current login's email and subscription metadata; CycleArc derives an identity fingerprint for the local binding. Email stays in memory. Each callback checks that the current login still matches; a different login requires reconnecting. This checks the configured login, not the identity of an already-running session, so restart existing Claude sessions after switching their login outside CycleArc. Browser logins started by CycleArc use separate configuration folders. A nickname takes precedence over the verified login email, with `Claude · <local profile ID>` as the fallback. Account selection, order and aliases share the existing UI; profiles are never added together.

The last valid sample stays **Received** while idle, including after a reported reset time passes, with its original **Last received** date and time. Elapsed time does not raise a warning or reset the saved percentage to zero. **Stale data** appears only when input is missing/malformed or receipt/cache/identity checks fail. Those warnings use amber text on the detail card, account cards and widget, and an amber detail ring. Percentages and receipt timestamps remain visible through restarts and in the tray tooltip. The shared Web·Desktop·Code scope stays visible because a received sample does not establish current server usage. Manual refresh and passive inbox reads do not renew receipt time, query Claude, or synthesize a new quota period. Claude has no Codex reset-credit controls.

<details>
<summary><strong>Claude usage after the first sample · Dark and Light</strong></summary>

<p>Research now has received usage, so the same account list includes three accounts. Research is selected: its five-hour usage is 91% and seven-day usage is 47%, with separate reset times. The sample was received 12 minutes ago and remains Received without an idle-time warning; the original receipt time stays visible. The two Codex accounts still show current data.</p>
<table>
  <tr>
    <td><img src="docs/images/claude-overview-en-dark.png" alt="Dark mixed-provider popup with Research Claude selected, separate five-hour and weekly limits, its original receipt time and no idle-time warning or Codex reset-credit card" width="440"></td>
    <td><img src="docs/images/claude-overview-en-light.png" alt="Light mixed-provider popup showing the same saved Claude sample and provider badges" width="440"></td>
  </tr>
</table>

</details>

**Connection details → Disconnect** restores the previous status line and hides the profile from the main view across restarts. The profile stays in account management, with its last good cache and configuration location preserved. Reconnecting shows the account with **Awaiting usage** until a new official sample arrives; it does not log out of Claude.

Only the official statusLine input is used for usage. Authentication uses the official `claude auth login --claudeai` and `claude auth status --json` commands. CycleArc does not parse `/usage`, read Claude auth/token files, launch a model turn, inspect transcripts or call an undocumented usage endpoint. It saves projected quota fields, receipt/status metadata and local connection paths/fingerprint, never the full stdin JSON. See [integration details](docs/CLAUDE.md) and the [official statusLine documentation](https://code.claude.com/docs/en/statusline).

### Controls

| Action | Result |
| --- | --- |
| Click the tray icon | Open or hide the detail card |
| Refresh button | Check account limits immediately |
| Pin button | Keep the detail card on top |
| `Ctrl` + `+` / `Ctrl` + `-` | Enlarge or reduce the detail card |
| `Ctrl` + `0` | Restore 100% zoom |
| Drag the widget | Move it and save its position |
| Right-click the widget | Open its menu, including Close widget |

The zoom shortcuts also support the numeric keypad. Widget position can be reset from **Settings → Widget**; the reset takes effect when you save.

With **Show widget** enabled and a displayable account, CycleArc checks the native window every two seconds and restores unexpected hiding, minimization or a lost always-on-top setting without taking keyboard focus. After sleep, unlock or display changes it recreates the widget with the saved position, opacity and interaction settings. Turning the widget off still keeps it hidden. Recovery events are recorded in the local app log.

<details>
<summary><strong>Settings and compact widget</strong></summary>

<p><img src="docs/images/settings.png" alt="English settings with theme, language, tray style, and optional Windows startup" width="640"></p>
<p><img src="docs/images/widget.png" alt="Compact desktop widget showing sample weekly usage" width="220"></p>

</details>

## How it works

CycleArc starts a bounded, short-lived **Codex App Server** process per account and requests account/rate-limit metadata. Every UI entry point shares the same refresh batch, with at most two simultaneous reads. One interactive login may run alongside reads for other accounts. It does not run a model turn to measure usage.

Percentages come from the reported limit windows. CycleArc does **not** turn them into invented request counts or combine unrelated reset periods. When a refresh fails, the last valid snapshot may remain visible with a stale label. Opening the card immediately after a failed check does not trigger repeated automatic retries; manual refresh remains available. Claude uses an independent passive provider and receives stdin through a headless mode of the same executable. Its projected inbox is checked every two seconds without starting Codex or Claude.

Countdowns and “last checked” ages update locally once a minute without another server request.

### Privacy

- No prompt, response, conversation, project, or rollout collection.
- No direct reading or copying of Codex or Claude authentication files, tokens, browser cookies, or credentials.
- No external telemetry or analytics.
- Only local preferences, profile references/labels, projected quota metadata, connection paths/identity fingerprints and safe diagnostic logs are retained. Reported emails stay in memory for display. Authentication is handled by the installed Codex or Claude CLI.

Settings and quota cache remain under `%LOCALAPPDATA%\ProMeter` for upgrade compatibility. Settings use atomic replacement with a previous-good backup and recovery if the primary file is damaged.
The account registry (`codex-accounts.json`) also uses atomic writes and a previous-good backup. The original account continues using `codex-snapshot.json`; additional profiles have separate quota caches. Existing preferences and historical files are preserved.

CycleArc is an independent project and is not affiliated with or endorsed by OpenAI or Anthropic. Compatibility depends on the installed Codex App Server or Claude CLI/statusLine protocol and the metadata available to your account.

## Build from source

Requires Windows, PowerShell 7, and the .NET 8 SDK.

```powershell
git clone https://github.com/frozenvoice/cyclearc.git
cd cyclearc
.\dev-run.ps1
```

The launcher restores dependencies, builds **Release**, runs the tests and WPF checks, then publishes and launches **one self-contained Windows x64 `CycleArc.exe`**, without debug symbols. The installed file is placed in `publish/local`. It retries transient deployment locks and restores the previous local build if replacement or startup fails.

- `-NoLaunch`: validate the staged executable without replacing the running local app.
- `-Fast`: skip the unit suite only when it has already passed for the same changes.
- CI also publishes the single executable as the `CycleArc-win-x64` artifact.

| Path | Purpose |
| --- | --- |
| `src/CycleArc` | Active WPF tray application |
| `src/CycleArc.Core` | Quota protocol, presentation logic, persistence, and retained legacy logic |
| `tests/CycleArc.Tests` | Unit and regression tests |
| `tests/CycleArc.UiSmoke` | Production WPF layout checks and documentation previews |

See [Architecture](docs/ARCHITECTURE.md), [Validation](docs/VALIDATION.md), and [preview generation](docs/images/README.md) for implementation and verification details.

## Upgrading from earlier releases

Run the new `CycleArc.exe`. Existing preferences and quota-cache paths remain compatible. The former ChatGPT history reconstruction, browser companion, and WebView2 features are retired; old history data is neither read nor deleted by the active app. The old browser extension can be removed through your browser's extension manager.

Retained legacy source and tests are identified in the architecture document. The companion host and retired screens are excluded from the shipped executable.

## License

[MIT](LICENSE). Contributions and reproducible bug reports are welcome. Please omit account credentials and conversation content from issues and attachments.
