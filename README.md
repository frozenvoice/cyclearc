# CycleArc

**Your Codex and Claude limits, one click away.**

A native Windows tray app for checking multiple Codex and Claude profiles, remaining percentages and reset times. Codex accounts also show reset credits when available.

The **Codex** or **Claude** label on account cards, selected details, tray tooltips and the widget identifies the usage provider. Connect the current Claude login or sign in through the official browser flow; CycleArc can actively check the shared Claude quota through the connected Desktop login and keeps **statusLine** and Desktop history as fallback sources. See [Claude Code connection](#claude-code-connection). Gemini is not supported.

> **Codex:** five-hour and weekly limits appear when the official App Server reports them, including on Plus; display is not restricted by plan name. Missing windows are omitted and unknown percentages stay unknown. **Claude:** manual and scheduled refreshes first try a read-only quota check through the connected Desktop login; statusLine and Claude Desktop subscription history remain fallback sources. Missing windows stay unknown, and history samples have no reset timestamps.

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
- **Clear quota windows.** See usage and remaining percentages, reset times and countdowns for each reported five-hour or weekly window. The ring, tray and widget share one display period: **Auto** uses a known five-hour percentage first, then weekly. Choose **Auto / 5 hours / Weekly** above the detail ring, or click the ring to switch when both values are known. The selection applies immediately to all three views and is saved across restarts. If the selected period has no known value, a known available period is shown with an explanation in the detail card.
- **Reset credits.** View the available count and expiry times when the server supplies them. Use an individual reset after confirmation. Missing expiry information stays explicitly unknown.
- **Optional desktop widget.** A compact, draggable summary that compares every displayable Codex and Claude account side by side: one small module each, with the account name, its provider badge, a usage ring for the shared display period, and what is left plus the countdown to the next reset for **every** period that account reports. Accounts keep their managed order and are never combined. One row holds as many modules as the widget's own monitor allows and the rest wrap to the next row; a list taller than the work area scrolls inside it. Opacity, always-on-top and click-through are unchanged. Click a module to select that account and open the usage popup; the thin header carries settings and a hide button. Off-screen positions recover automatically.
- **Your preferred appearance.** Dark, Light, or live System theme; English and Korean; keyboard zoom from 80% to 150%.
- **Honest refresh states.** Codex refreshes on its selectable 1, 2, 5, 10, 30 or 60-minute interval (default: five minutes). Claude actively checks the shared quota through the connected Desktop login during manual and configured scheduled refresh. Successful live data is **Updated** with a last-checked time; statusLine and Desktop history fallbacks are **Received** with source time. Failed checks keep previous values visibly stale.

## Get started

**Requirements:** Windows 10/11 on x64. Codex monitoring requires an installed Codex CLI signed into a ChatGPT account that reports subscription limits, and network access. Claude monitoring requires the official Claude CLI for connection verification, Windows PowerShell, a signed-in Claude Desktop account for live checks, and a signed-in Claude Pro or Max account. StatusLine and Desktop subscription history remain fallbacks; Codex sign-in is not required.

1. Download **`CycleArc-Setup.exe`** from the [latest release](https://github.com/frozenvoice/cyclearc/releases/latest).
2. Run the setup program. CycleArc installs under `%LOCALAPPDATA%\CycleArc`; the .NET runtime is bundled. Your settings, accounts and quota cache remain under `%LOCALAPPDATA%\ProMeter`.
3. Open the tray icon and **Manage accounts → Add an account**. For Codex, existing CLI sign-ins are discovered automatically; choose **New account sign-in** to add another account. For Claude, choose **Connect Claude**, then connect the current login or sign in through your browser.
4. Codex appears after a successful quota check; Claude appears after verified connection and attempts a Desktop live check, with statusLine/history fallback when needed. If Codex cannot be found, install the [Codex CLI](https://developers.openai.com/codex/cli/) or open **Settings → Connection** and select its executable path. For Claude setup, follow the [connection steps below](#claude-code-connection).

CycleArc discovers `codex.exe` or `codex.cmd` through PATH and supported installation locations. The Codex CLI itself is not bundled. Sign-in remains managed by Codex.

### Running and updating versions

An ordinary launch keeps the first running instance and opens its popup, which shows the running version. Development builds and releases have equal priority. Windows startup uses the installed launcher and does not bring an already running window forward.

The setup installs the stable channel and keeps it current through GitHub Releases. CycleArc checks 20 seconds after startup and then every six hours. A stable release is offered with English/Korean release notes; choose **Download**, then **Restart & update** to apply it, or choose **Later**. Updates are not applied automatically at startup. The complete `.nupkg` is SHA-256 checked after download and again before Velopack applies it.

Release artifacts are unsigned unless the release pipeline is configured for code signing. Installing with `CycleArc-Setup.exe` alone does not remove Windows SmartScreen warnings.

Before replacement, CycleArc keeps a verified copy of the previous app outside its installation. If replacement fails or the new desktop does not become ready, a separate helper restores and restarts the previous version. Accounts and settings stay outside both app versions. If recovery itself is blocked, the backup is retained and its location is shown; this check covers startup, not failures later in a session.

The installed desktop runs from `%LOCALAPPDATA%\CycleArc\current\CycleArc.exe`. `%LOCALAPPDATA%\CycleArc\CycleArc.exe` is the stable launcher used for desktop and Windows-startup entry points; Claude statusLine and failure callbacks use the current executable directly so stdin/stdout forwarding remains synchronous. Existing exact CycleArc-owned Claude callbacks migrate after a matching CLI identity check, including when their old executable has already been removed. Unrelated settings, the previous statusLine command, account bindings and nicknames remain intact.

The first setup launch can close an older canonical legacy desktop after verifying its current-session IPC identity. A development build running from another path keeps the normal first-instance policy. The former `%LOCALAPPDATA%\Programs\CycleArc` installation is retained for development compatibility and is not updated by the GitHub release checker.

Removing CycleArc through **Settings → Apps** (or `Update.exe --uninstall`) restores the Claude settings this installation changed before its files are deleted. Only callbacks that belong to the installation being removed are touched: the previous statusLine comes back exactly as it was, the CycleArc failure hook is removed, and a statusLine you replaced yourself, another tool's hooks and any wrapper belonging to a different CycleArc installation stay as they are. Accounts, preferences, quota history and Claude connection records under `%LOCALAPPDATA%\ProMeter` are never deleted or signed out, so reinstalling finds them again. The cleanup is time-boxed and cannot delay removal; when settings are locked, damaged or edited at the same moment, the file is left untouched and the outcome is recorded in `%LOCALAPPDATA%\ProMeter\claude-uninstall-cleanup.json`.

<details>
<summary><strong>Update preview</strong></summary>

<p><img src="docs/images/updates-en-light.png" alt="CycleArc update window with current and new versions, release notes, Later and Download update" width="480"></p>
<p>Production WPF view with synthetic 0.6.1 release notes. Downloading does not restart the app; a separate Restart &amp; update action appears after verification.</p>

</details>

### Accounts

**Manage accounts** is available in the popup and **Settings → Connection → Manage Codex and Claude accounts**. **Add an account** offers Codex login/discovery and Claude connection. It opens automatically when there is no previously checked account; existing users see their account list first. Accounts retain separate percentages, reset windows and refresh states; values are never added together. Reset credits belong only to the selected Codex account.

**Codex profiles keep their verified login.** Profiles marked **Login changed** or **Check connection** stay visible with quota and reset credits hidden. If the affected profile is selected, the detail card, tray and widget retain that selection. Follow [Codex connection recovery](#codex-connection-recovery) to restore it.

**Connected Claude accounts remain visible while awaiting usage.** They show **Awaiting usage** and unknown limits, without increasing the attention count. Unconnected profiles stay in **Manage accounts** and do not increase the main account count. Previously received values remain visible as stale during a temporary update failure. Popup and tray show the selected account; the widget lists every visible account and marks the selected one. If none is available, the widget stays hidden and the popup shows a connection hint.

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

Use **Order ↑ / ↓** beside each account's nickname to move it. The popup follows the same order among visible accounts, which is saved immediately and retained after restart. Reordering keeps the selected tray/widget account and does not initiate a quota refresh. Select a card with usage to change which account drives the tray/widget. **Reconnect** gives a linked Codex profile an independent login; **Sign in again** renews or changes a Codex login already managed by CycleArc. **Connect** opens a Claude profile's connection window. **Remove** forgets the profile from this list.

<details>
<summary><strong>Account manager · Dark preview</strong></summary>

<p>The account manager retains all three profiles, including the unconnected Research profile. Its summary cannot be selected yet, while Connect, nickname and order actions remain available. The main popup above counts only the two ready accounts. Work stays selected while you arrange the list.</p>
<p><img src="docs/images/accounts-manage-en-dark.png" alt="Dark account manager with two synthetic Codex accounts and an unconnected Research Claude profile whose Connect button remains available" width="700"></p>

</details>

<details>
<summary><strong>Account manager · Light preview</strong></summary>

<p><img src="docs/images/accounts-manage-en-light.png" alt="Light account manager retaining all three synthetic profiles, selected Work and the unconnected Claude profile" width="700"></p>

</details>

**Find accounts on this PC** checks `CODEX_HOME` from the process/user/machine environment and the default `~/.codex` directory through `account/read`. It cannot discover an account signed in only on the ChatGPT website. **Advanced · Connect a specific Codex folder → Choose Codex home folder** connects another known home; choose the home, not the executable or a project folder. It does not search the disk for credentials or copy an existing login. Imported homes stay linked to their original Codex installation until **Reconnect** creates an independent login for that CycleArc profile.

New and reconnected Codex profiles receive separate homes under `%LOCALAPPDATA%\ProMeter\accounts\<local-id>\codex-home`. Only the installed Codex process stores and renews credentials there. One browser login can run alongside usage checks for other accounts.

Removing a profile forgets its reference without logging out or deleting its Codex home. It will not be automatically re-added. A custom home can be connected again with the folder picker.

The first launch opens the detail card. Later launches start in the tray; `CycleArc.exe --show` opens the card at startup. If Windows hides the tray icon, move it out of the notification-area overflow. Starting with Windows and showing the desktop widget are optional settings.

#### Codex connection recovery

Nicknames are local display names. **Same reported login email as another profile** means that the profiles report the same login email; the official protocol cannot distinguish workspaces sharing that email. When a profile linked from an existing Codex home matches a profile signed in separately through CycleArc, the linked profile instead shows **Check connection** and hides its quota and reset credits. That conflict remains until reconnection, including after restart or removal of the matching profile. **Check connection** also appears when the saved account binding cannot be verified.

1. Open **Manage accounts** and find the affected Codex profile. Choose **Reconnect** for a linked profile, or **Sign in again** for a login managed by CycleArc.
2. Complete the official browser login with the intended account.
3. **Reconnect** verifies login and usage before replacing the old link. It preserves the nickname, list position and selection. Cancelling, choosing an already connected account or failing the usage check keeps the existing link.

Browser sign-in has a five-minute deadline and can be cancelled. If it times out and the browser later cannot connect to the localhost callback, start sign-in again from the same profile button to open a new session.

### Claude Code connection

**Claude subscription usage is shared across Web, Desktop and Code.** CycleArc actively checks the shared quota during manual refresh and the configured automatic interval through the connected Claude Desktop login. A successful server check is labeled **Updated** with its last checked time. Claude Code statusLine and Claude Desktop's local subscription usage history remain fallback receipts, labeled **Received** with their source time; Web/Desktop activity also consumes the same allowance. [Official usage-limit explanation](https://support.claude.com/en/articles/11647753-how-do-usage-and-length-limits-work).

To check current usage independently, choose **Open usage page** on the Claude detail card or connection window. It opens [Claude Settings → Usage](https://claude.ai/settings/usage) in your browser; check that the intended account is signed in. Opening the page does not refresh CycleArc. Public API, CLI and SDK documentation still provide no supported stable personal-subscription quota query. CycleArc 0.6.0 uses a narrow private first-party Desktop profile/usage path for its live check; it may change without notice. See the [dated research and decision](docs/CLAUDE-USAGE-RESEARCH.md).

1. Open **Manage accounts → Add an account → Connect Claude**. Set an optional nickname.
2. Choose **Connect current login** to verify the existing Claude CLI login, or **Sign in to Claude** to complete the official browser flow. CLI verification establishes the CycleArc connection; live quota requests use the connected Claude Desktop login. Other settings and the existing status line are preserved; no JSON copying is required.
3. Sign in to the **same account in Claude Desktop**, then choose **Refresh** in CycleArc. Manual and automatic refresh query the shared quota directly, without a model request. The interval in **Settings → Connection** applies to both Codex and Claude (default: five minutes).

Claude Code statusLine and Desktop subscription history also provide local fallback samples. CycleArc preserves their observation times; rereading a file does not count as a successful server check. Desktop history may be delayed and has no reset timestamps.

If authentication fails, the profile shows **Claude Desktop login required** and keeps the last valid values marked as stale. If the Desktop identity does not match the connected account, CycleArc hides all Claude quota for that profile. Sign in to the intended account in Desktop, then refresh. Rate-limit and request failures also retain previous values and show the reason the check failed.

A Claude plan change keeps the same account when its email and organization match. After sign-in recovery, callbacks from an older connection generation cannot change the latest sample or its receipt time. Older saved bindings are upgraded only after their identity is verified; see [connection compatibility](docs/CLAUDE.md#account-identity-compatibility).

**CycleArc actively checks the shared quota through the connected Claude Desktop login.** StatusLine and Claude Desktop history remain fallback receipts. A connected account can show **Awaiting usage** until the live or fallback source returns a valid sample. When live data succeeds it is labeled **Updated** with its server-fetched time; fallback data is **Received** with its source time. When sources are available together, the newer observation wins. Repeating **Connect current login** reuses the same verified binding and preserves its name and usage history. Closing or cancelling a new connection before it succeeds removes its empty draft.

<details>
<summary><strong>Connected, awaiting usage · Dark and Light</strong></summary>

<p><img src="docs/images/claude-waiting-en-dark.png" alt="Connected Claude awaiting shared subscription usage, with unknown limits and an Open usage page action" width="440"> <img src="docs/images/claude-waiting-en-light.png" alt="The same connected waiting state in the light theme" width="440"></p>

</details>

<table>
  <tr><td align="center"><strong>Automatic connection · Dark</strong></td><td align="center"><strong>Automatic connection · Light</strong></td></tr>
  <tr>
    <td><img src="docs/images/claude-connection-en-dark.png" alt="Production Claude connection window with synthetic signed-in identity, current-login connection, another-account connection and Open Claude Code terminal buttons" width="530"></td>
    <td><img src="docs/images/claude-connection-en-light.png" alt="The same automatic Claude connection window in the light theme; no manual JSON entry" width="530"></td>
  </tr>
</table>

*Research is selected below, alongside two Codex accounts. Synthetic server data (46% five-hour, 11% weekly, with reset times) shows the **Updated** state. These previews do not access a real login.*

<table>
  <tr><td align="center"><strong>Desktop live quota · Dark</strong></td><td align="center"><strong>Desktop live quota · Light</strong></td></tr>
  <tr>
    <td><img src="docs/images/claude-live-en-dark.png" alt="Synthetic Claude Desktop live quota with Updated status, 46% five-hour usage, 11% weekly usage and reset times" width="440"></td>
    <td><img src="docs/images/claude-live-en-light.png" alt="Synthetic Claude Desktop live quota in the light theme with Updated status and reset times" width="440"></td>
  </tr>
</table>

The official statusLine JSON has **no account email or account ID**. Desktop history carries an organization ID, not an email. The live Desktop reader reads only the app-owned config.json and Local State, decrypts the protected OAuth access token in memory with Windows DPAPI/AES-GCM, verifies the profile response email and organization against the bound identity, then requests usage. It never writes or refreshes Desktop credentials, reads cookies, or stores the token. A mismatched identity hides all Claude quota for that profile and cannot replace the last-good projection. Email stays in memory for display; a nickname takes precedence.

A successful live response is **Updated** and shows its server-fetched time. StatusLine and Desktop history fallbacks stay **Received** with their original source time. Authentication, rate-limit and ordinary request failures preserve the last-good values as **stale**. A Desktop profile identity mismatch hides all Claude quota for that profile and never replaces the last-good projection. Manual and scheduled refreshes attempt the live check, while the two-second passive loop reads only local sources. No reset is inferred when a source omits it, and percentages never roll back to zero locally. Claude has no Codex reset-credit controls.

<details>
<summary><strong>Claude Desktop history fallback · Dark and Light</strong></summary>

<p>This separate preview shows a Claude Desktop history sample: 91% five-hour and 47% weekly usage, with unknown reset times. It is labeled <strong>Received</strong> with its original observation time. A server-check failure additionally marks retained values as stale. The two Codex accounts still show current data.</p>
<table>
  <tr>
    <td><img src="docs/images/claude-overview-en-dark.png" alt="Dark mixed-provider popup with Research Claude selected, 91% five-hour and 47% weekly Desktop history fallback, unknown reset timestamps and its original source time" width="440"></td>
    <td><img src="docs/images/claude-overview-en-light.png" alt="Light mixed-provider popup showing the same Claude Desktop history fallback and provider badges" width="440"></td>
  </tr>
</table>

</details>

**Connection details → Disconnect** first saves the disconnected state, then restores the previous status line and removes CycleArc's own failure hook. The profile stays hidden from the main view across restarts while its last-good cache and configuration remain in account management. Reconnecting shows **Awaiting usage** until a live or fallback source succeeds; it does not log out of Claude.

If setup reports that the statusLine command is too long, move the existing inline statusLine into a script file and use a short command to call it, then connect again. The limit applies to the full generated command, including paths and encoded options; failed setup preserves the existing settings.

Usage comes from the Desktop live profile/usage check when available, then the official statusLine input or Claude Desktop app-owned history fallback. The live reader reads only config.json and Local State, decrypts the OAuth access token in memory with Windows DPAPI/AES-GCM, verifies the bound profile, requests quota and discards the token. It never writes or refreshes Desktop credentials, reads cookies, launches a model turn, inspects transcripts or stores raw history. The private endpoint is an internal compatibility dependency and is not claimed as an official public API. It saves projected quota fields, source/receipt/status metadata and local connection paths/fingerprint.

### Controls

| Action | Result |
| --- | --- |
| Click the tray icon | Open or hide the detail card |
| Refresh button | Check account limits immediately |
| Auto / 5 hours / Weekly | Choose the period shared by the detail ring, tray and widget; saved across restarts |
| Click the usage ring | Switch between five-hour and weekly usage when both values are known |
| Pin button | Keep the detail card on top |
| `Ctrl` + `+` / `Ctrl` + `-` | Enlarge or reduce the detail card |
| `Ctrl` + `0` | Restore 100% zoom |
| Drag the widget | Move it and save its position; a finished drag never opens the popup |
| Click a widget account | Select that account everywhere and open the usage popup |
| Widget header buttons | Open Settings, or hide the widget without exiting CycleArc |
| Right-click the widget | Open its menu, including Close widget |

The zoom shortcuts also support the numeric keypad. Widget position can be reset from **Settings → Widget**; saving moves it to the primary screen and recreates its window so a missing widget can recover even when Windows reports it as visible.

With **Show widget** enabled and a displayable account, CycleArc checks the native window every two seconds and restores unexpected hiding, minimization or displacement behind ordinary windows despite an enabled always-on-top setting, without taking keyboard focus. After sleep, unlock or display changes it recreates the widget with the saved position, opacity and interaction settings. Turning the widget off still keeps it hidden. Recovery events are recorded in the local app log.

The **Usage number** tray style shows bold digits as large as the native icon slot allows, keeping their original font proportions on a transparent background. The **%** sign is omitted: **67** means **67% used**. Text is white on a dark Windows taskbar and dark on a light taskbar, independently of the app theme. The number is the selected account's percentage used for the period chosen in the detail card. **Auto** prefers a known five-hour value, then weekly. The tooltip identifies the period being shown; switching periods only changes the display of the received data. Unknown usage is **?**. Check the tooltip or detail card for status and receipt time. The optional **Usage ring** retains its status colors: blue for a valid sample, red for a valid sample at 100%, and amber for stale data or a lookup/connection problem. Without a known value, the ring is gray with **?**.

<details>
<summary><strong>Settings and compact widget</strong></summary>

<p><img src="docs/images/settings.png" alt="English settings with theme, language, tray style, and optional Windows startup" width="640"></p>
<p><img src="docs/images/widget.png" alt="Compact desktop widget comparing three sample accounts side by side" width="740"></p>

</details>

## How it works

CycleArc starts a bounded, short-lived **Codex App Server** process per account and requests account/rate-limit metadata. Every UI entry point shares the same refresh batch, with at most two simultaneous reads. One interactive login may run alongside reads for other accounts. It does not run a model turn to measure usage.

Percentages come from the reported limit windows. CycleArc does not turn them into invented request counts or combine unrelated reset periods. A successful Claude Desktop live check is labeled **Updated** with its fetched time; statusLine and Desktop history fallback values are **Received** with source time. A temporary failure leaves last-good values visible as stale. Claude manual and scheduled refreshes use the active Desktop check; the two-second passive loop reads only local projections. The newer fallback observation is displayed when live data is unavailable.

Countdowns and “last checked” ages update locally once a minute without another server request.

### Privacy

- No prompt, response, conversation, project, or rollout collection.
- Claude live refresh reads only the app-owned Desktop config.json and Local State needed to obtain its protected OAuth token; DPAPI/AES-GCM decryption is memory-only. It never writes or refreshes Desktop credentials, reads cookies, or persists the token. StatusLine and Desktop history are projected fallbacks.
- No external telemetry or analytics.
- Only local preferences, profile references/labels, projected quota metadata, source/receipt metadata, connection paths/identity fingerprints and safe diagnostic logs are retained. Reported emails stay in memory for display. The Desktop token is discarded after the read; raw history, credentials, cookies, prompts, responses and transcripts are never copied into CycleArc.

Settings and quota cache remain under `%LOCALAPPDATA%\ProMeter` for upgrade compatibility. Settings use atomic replacement with a previous-good backup and recovery if the primary file is damaged.
The account registry (`codex-accounts.json`) also uses atomic writes and a previous-good backup. The legacy default profile uses `codex-snapshot.json`; new profiles and profiles replaced through **Reconnect** have separate quota caches. Reconnection preserves the old home and cache. Existing preferences and historical files are preserved.

CycleArc is an independent project and is not affiliated with or endorsed by OpenAI or Anthropic. Compatibility depends on the installed Codex App Server, Claude CLI/statusLine protocol, Desktop OAuth/history formats, first-party quota responses and the metadata available to your account.

## Build from source

Requires Windows, PowerShell 7, and the .NET 8 SDK.

```powershell
git clone https://github.com/frozenvoice/cyclearc.git
cd cyclearc
.\dev-run.ps1
```

Double-click **`build-local.cmd`** in the repository root to build the current checkout, package `CycleArc-Setup.exe`, install that package into the managed Velopack location (`%LOCALAPPDATA%\CycleArc`, or the existing InstallLocation), and start `%LOCALAPPDATA%\CycleArc\CycleArc.exe`. It runs `dev-run.ps1 -NoLaunch` in a separate process, then uses Setup.exe. It does not copy the development EXE to `%LOCALAPPDATA%\Programs\CycleArc`.

`.\dev-run.ps1` remains the development publish path: it restores dependencies, builds **Release**, runs the tests and WPF checks, then publishes the development **single-file Windows x64 `CycleArc.exe`**, without debug symbols. Development output uses `%LOCALAPPDATA%\Programs\CycleArc\CycleArc.exe` for compatibility with existing source workflows; it is separate from the stable Velopack installation under `%LOCALAPPDATA%\CycleArc` and is not selected by the GitHub update checker. Build staging stays in the current checkout; `-NoLaunch` does not stop or replace the installed app. Filesystem deployment can restore the previous build when replacement fails; a successful file rollback does not guarantee that a newly started app passed a health check.

Preflight reports existing desktop PIDs and executable paths. After validation, the installer stops verified CycleArc desktops in the current Windows session and checks that the single-instance lock is gone before replacing files. If a desktop is running directly from build output, preflight identifies it before cleanup; exit that instance and rerun the script.

- `-NoLaunch`: validate the staged executable without replacing the running local app.
- `-Fast`: skip the unit suite only when it has already passed for the same changes.
- CI builds and checks the development single-file executable and packages the Velopack installer assets for the stable release workflow.

`scripts/Verify-InstalledUpdate.ps1` verifies the installed application end to end: it builds three test executables with different versions and hashes, installs the first with its real `Setup.exe`, updates to the second through the production update window, coordinator, updater and recovery supervisor, forces a build that fails to start so the supervisor restores and restarts the previous version, and finally removes the installation and checks the Claude cleanup and data preservation. It installs, updates and removes CycleArc for the current Windows user and cannot isolate the data root, uninstall registry entry, shortcuts, single-instance mutex or desktop IPC, so run it only on a disposable Windows VM or a throwaway user account, with `-ConfirmDisposableEnvironment`. Its update feed is a local directory read by a test-only build flavour; HTTPS enforcement and package verification are unchanged, and its Claude accounts are synthetic rather than a live subscription check.

For a GitHub release, commit and push the versioned changes, pass the local `-NoLaunch` gate and Windows CI, then run `pwsh -NoProfile -File ./scripts/Release.ps1 -Version 0.6.0 -NotesPath "./release-notes/0.6.0.md"` (replace the notes path with your prepared file).

The script downloads that commit's tested executable and installer assets, checks their
versions and uploaded SHA-256 values, and publishes the draft only after verification.
Existing draft notes are preserved; a new release requires `-NotesPath "./release-notes/0.6.0.md"` (replace the notes path with your prepared file). Public assets and existing tag targets
are never overwritten.

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
