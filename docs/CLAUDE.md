# Claude subscription usage via statusLine and Desktop history

The five-hour and seven-day percentages describe the subscription allowance shared
across Web, Desktop and Code. CycleArc receives observations from Claude Code's
statusLine and, when available, Claude Desktop's subscription usage history. It displays
the **last received/read sample**, which may differ from current account usage. The
[official usage-limit explanation](https://support.claude.com/en/articles/11647753-how-do-usage-and-length-limits-work)
confirms the shared scope. The [2026-09-12 interface review](CLAUDE-USAGE-RESEARCH.md)
found no supported public personal-subscription quota query; the Desktop history source
below is an app-owned local schema rather than a public API.

CycleArc reads the [official Claude Code statusLine JSON](https://code.claude.com/docs/en/statusline), delivered to a configured command on stdin. It also reads Claude Desktop's local `plan-usage-history.json` when the installed app has written a recognized sample. It projects only:

| Input field | CycleArc meaning |
| --- | --- |
| `rate_limits.five_hour.used_percentage` | Five-hour usage, 0–100, retaining fractional values |
| `rate_limits.five_hour.resets_at` | Five-hour reset as Unix epoch seconds |
| `rate_limits.seven_day.used_percentage` | Weekly usage, 0–100, retaining fractional values |
| `rate_limits.seven_day.resets_at` | Weekly reset as Unix epoch seconds |
| Desktop history `samples[].t` | Observation time in Unix milliseconds, used to select the newer source sample |
| Desktop history `samples[].org` | Organization identity used during binding verification; not displayed |
| Desktop history `samples[].u.fh` / `samples[].u.sd` | Five-hour / weekly usage percentages; Desktop history has no reset timestamps |

Claude Code may omit each window independently, including before a first response or for an unsupported plan. Missing data stays unknown; a malformed present window is a schema failure rather than a successful partial reading. The model/context-token fields are not used as account quota.

## Connect a profile

In **Manage accounts → Add an account → Connect Claude**, set an optional nickname and open the connection window.

- **Connect current login** checks the installed Claude CLI's existing sign-in and automatically connects its effective `CLAUDE_CONFIG_DIR` (or the usual user `.claude` folder).
- **Sign in to Claude / Sign in to another account** starts the official `claude auth login --claudeai` browser flow in a new per-profile configuration folder. CycleArc waits for `claude auth status --json` to verify completion, then installs the connection. Login can be cancelled; closing the window or exiting the app cancels and reaps the child process.
- **Open Claude Code terminal…** lets you choose a working folder and opens the terminal CLI with the connected configuration. Use it when awaiting a first statusLine sample, especially for a login created by CycleArc, so the correct account and settings are active. Claude Desktop Code can provide a sample through its local history when that file is available.
- **Connection details → Disconnect** restores the previous status line. It disconnects usage collection, without logging out of Claude or deleting CLI-owned credentials.
- **Open usage page**, also available on the detail card, opens `https://claude.ai/settings/usage`
  in the user's default browser. Check the intended browser account; CycleArc neither
  switches that account nor reads the page or imports its values.

Verified connected profiles appear in the main view even before the first sample, with **Awaiting usage** and unknown limits. Waiting does not increase the attention count. Unconnected profiles appear only in **Manage accounts**. An explicit disconnection hides the account across restarts while preserving its cache and configuration; reconnecting shows it awaiting a new sample. Idle time preserves the received state; a receipt failure keeps the last valid values visible as stale.

**Web, Claude Desktop and Claude Code use the shared quota.** CycleArc accepts a statusLine sample from the connected terminal and a recognized sample from Claude Desktop's local history. After Desktop Code use, the app can take a moment to write `plan-usage-history.json`; refresh reads the file passively and does not start Desktop or a model request. If both sources are present, the sample with the newer observation time is displayed. Repeated current-login connection reuses an existing binding when its configuration directory, implicit/explicit directory mode and verified identity all match. It preserves that profile's name, order and quota history. Closing or cancelling a new connection removes only that flow's empty, never-connected draft; existing profiles and any saved connection/usage data are retained.

An installed Claude CLI is required for official login verification; CycleArc recognizes the native Windows executable and npm `claude.cmd`. Claude Desktop can additionally provide local usage history. Authentication uses only [official CLI commands](https://code.claude.com/docs/en/cli-reference); it never reads auth/token files or retains login URLs, codes or secrets. The binding preserves whether `CLAUDE_CONFIG_DIR` is unset or explicit: the CLI can resolve login metadata differently even when the directory path looks the same. Status checks have an eight-second bound; browser login has a five-minute bound. A missing, signed-out, unsupported or malformed login response cannot be shown as a connected account.

StatusLine usage arrives through the [official statusLine](https://code.claude.com/docs/en/statusline); Desktop usage arrives only when Claude Desktop has written a recognized local history sample. No model turn is launched to measure quota. Claude may omit statusLine quota fields until a response or on unsupported plans, and Desktop may not have written a sample yet; connection success is distinct from receipt of usable quota data. Restart a running Claude terminal session if it has not picked up the new settings, or after changing its login externally. The statusLine command also requires accepting Claude Code's workspace trust dialog; CycleArc does not accept that dialog or launch a model request for the user.

**Account attribution:** the statusLine JSON has no email or account identifier, and Desktop history carries an organization identifier rather than an email. CycleArc obtains current-login metadata through `auth status --json`, keeps email in memory for display, and persists only a hash of identity metadata together with the profile/configuration binding. Desktop samples are accepted only for a matching verified organization and a Claude Pro or Max account. Each automatic callback and Desktop read verifies the current binding before accepting quota data. A changed or unavailable identity cannot replace the last good sample. This checks the configured login; it cannot authenticate the emitter of an already-running Claude session. New browser logins use separate configuration folders, and callbacks from an old folder cannot write into a new binding. Separate profiles retain separate selection, nickname and quota histories.

## Request failures and sign-in recovery

Official `auth status --json` reports local login metadata; it is not a remote session-validity probe. A request can fail with expired OAuth while that command still reports signed in. CycleArc also installs the official [StopFailure hook](https://code.claude.com/docs/en/hooks#stopfailure) and accepts only its documented error classification. Authentication failures show **Sign-in required**, other request failures show a separate failure state, and any last-good quota remains visible with its original receipt time. Missing usage fields alone do not prove authentication failure.

**Sign in again** uses the existing profile's CLI and exact configuration mode for the official browser login. It preserves the nickname, ordering, selected account, connection time and quota history. A different authenticated identity is rejected rather than silently replacing the account. Successful reauthentication rotates a nonsecret binding generation so earlier failure callbacks cannot override recovery; it does not create a new quota receipt. Restart the affected Claude Code session to use the repaired login and receive a new sample.

Failure diagnostics live separately in `claude-failure.json`, with only profile ID, binding generation, observation time and a small failure enum. Raw hook JSON, error details, messages, transcript paths and session IDs are never stored or logged. Only failure data matching the current connected generation applies. Authentication and identity failures remain until successful sign-in recovery: statusLine can repeat cached quota after a failed request, so a later callback alone is not authentication proof. A later valid quota receipt can supersede a generic request failure. Existing exact CycleArc-owned setups are upgraded during connection inspection; user-replaced statusLine commands are preserved.

## Account identity compatibility

Current v2 bindings identify an account by its normalized email and exact organization ID, independently of its subscription plan. A different email or organization still requires a different account connection. Legacy v1 hashes are verified against the current CLI email/organization with the current plan or a bounded set of known historical plan labels, including Pro and Max. This supports deployed v1 files that did not save a plan. Migration preserves the profile and quota history, and setup failure restores the original binding. An unrecognized historical plan that cannot reproduce the saved hash remains unverified; reconnect that profile through the official connection flow. Only recognized plan labels are persisted; email remains in memory.

Only a callback or Desktop read belonging to the current configuration and connection generation can record quota or failure state. A rejected older callback still forwards the previous statusLine command and its output. Quota and failure writes hold the connection mutation lock through the cache commit, so reauthentication cannot rotate the generation between the check and the write. The legacy manual receiver also leaves an automatically connected profile unchanged. Authentication failures use the separate failure record and preserve the quota receipt timestamp.

## Automatic statusLine setup

CycleArc updates the `statusLine` property and one exact owned `hooks.StopFailure` command in the connected folder's `settings.json`. It preserves unrelated JSON settings and makes a local `settings.json.cyclearc.bak` backup before replacement. Invalid/ambiguous settings, active bindings owned by another profile, or concurrent edits are reported without replacing that file. Reconnecting updates the executable path without nesting wrappers or resetting status-line presentation properties.

The generated shell-neutral encoded PowerShell command invokes the headless `--claude-statusline-bridge` mode of the same `CycleArc.exe`. It forwards the same stdin to an existing statusLine command, preserving its stdout and settings such as padding. The old command and its restoration data remain in the Claude settings, not in CycleArc's usage cache. Disconnect restores the previous statusLine entry only if the active command is still CycleArc's exact owned wrapper, and removes only its own StopFailure hook; it never overwrites a replacement command or removes another tool's hooks. An old wrapper that cannot be removed after changing configuration folders can still display its previous command, but can no longer collect for the moved profile.

Disconnect saves the revoked binding before touching the Claude settings or usage inbox. If cleanup fails, callbacks remain rejected and the profile remains disconnected after restart; the UI reports incomplete cleanup. Failure to save the binding is an unsuccessful disconnect. Existing user edits remain preserved.

An overlong generated statusLine command has its own setup error. Move an existing inline statusLine into a script file with a short invocation before reconnecting. The limit includes executable/configuration paths and encoded options; rejected setup leaves the original settings intact.

The headless bridge has a ten-second total deadline and a four-second bound for the previous command. It uses Git Bash when available and Windows PowerShell otherwise. No additional executable is distributed. Reconnect if you move `CycleArc.exe`.

The previous manual `--claude-statusline <profile-id>` receiver remains for existing configurations. Automatic setup recognizes its exact generated command and upgrades it without recursively wrapping it. Once a profile has an automatic binding, the old receiver cannot bypass its login checks.

## Freshness and persistence

- A valid statusLine callback or Desktop history sample stays **Received** while idle and after included reset timestamps pass, provided no receipt/cache/identity failure has occurred. There is no age or elapsed-reset attention warning. The original date/time remains visible: CycleArc's callback receipt time for statusLine, or Desktop's recorded `t` for history. Neither establishes a current server query; reading a file does not stamp it with a new time.
- Missing/malformed input preserves the last valid sample as stale. A later valid callback recovers the profile. A valid one-window callback replaces the previous sample; it does not carry an absent older window into fresh data.
- The provider checks its small projected statusLine inbox and Claude Desktop history every two seconds. The Codex refresh schedule remains independent. Neither this poll nor manual refresh renews receipt time, invokes Claude or Desktop, reads authentication data or generates a model response.
- When both sources are available, the provider chooses the newer observation timestamp. Desktop history can lag behind a Code response and does not include reset times; the last-good projection remains visible when the file is missing, delayed or changed. Desktop source snapshots use `claude-desktop-history`; unavailable or identity-rejected reads use `claude-desktop-unavailable` or `claude-desktop-identity-unverified`.
- An elapsed reset preserves the last values and **Received** status; CycleArc never rolls percentages back to zero locally. Clock rollback that makes a sample future-dated still warns about invalid receipt time. This behavior and the original receipt survive application restarts.
- Detail, account cards and widget explicitly emphasize **Stale data / 오래된 데이터** with theme-aware amber text. The detail ring changes color as well. Last receipt date/time remains visible; the detail row also shows elapsed time. The bounded native tray tooltip prioritizes stale status, receipt time and shared Web·Desktop·Code scope before an optional nickname. A new valid sample clears the warning; rereading the cache does not.
- Each profile stores separate `accounts/<local-id>/claude-statusline.json` and `claude-desktop-usage.json` caches under the existing `%LOCALAPPDATA%/ProMeter` directory. Only provider/profile ID, projected windows, receipt/source timestamps and a bounded status enum or binding hash are serialized. The original Desktop history file stays owned by Claude Desktop and is never copied into CycleArc. No transcript path, session ID, prompt, response, email or token is retained.
- Independent callbacks use a bounded exclusive lock, monotonic receipt ordering, an atomic replace and a previous-good backup. Interrupted writes leave the previous cache intact. Corrupt primary data can use the backup with a stale label.
- The input limit is 256 KiB with a five-second receiver deadline. Present percentages must be finite numbers in 0–100 and reset timestamps must be representable positive integer Unix seconds. Unknown unrelated JSON fields are ignored and never copied to storage or output.

Account registry version 1 continues to load as Codex. Adding a Claude profile upgrades the registry and its previous-good backup to version 2; older builds refuse both instead of falling back to a v1 account list or interpreting Claude profiles as Codex credentials. The backup retains its previous profiles. Codex home references, selected IDs, aliases, order, ignored homes and quota-cache paths are preserved.

## Validation

Unit checks exercise synthetic official shapes, missing windows, malformed fields, fractional usage, stale/recovery/reset boundaries, cache backup and concurrent receipt ordering, provider separation, Desktop history version-2 samples and legacy registry compatibility. Production WPF checks cover mixed accounts in both languages and all themes, provider labels and aliases, stale values, compact connection guidance and Codex-only credit controls.

Both `dev-run.ps1` and Windows CI validate the receiver in the built application and again in the single-file published executable. A held desktop mutex ensures the headless path works beside the tray application. Synthetic checks use an explicit collector-only `--data-root <absolute-temporary-directory>` and registered fixture profiles, so they never touch actual account settings or Claude Desktop history. Direct stdin, generated PowerShell, Git Bash when installed, malformed input and missing-stdin deadlines are checked. The Desktop file path, version-2 shape and source ordering are covered by synthetic fixtures; the 2026-09-16 Desktop 1.52386.3.0 comparison is a separate real-app observation, not a live service guarantee.
