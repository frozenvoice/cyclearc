# Claude subscription usage via Desktop live quota, statusLine and history

The five-hour and seven-day percentages describe the subscription allowance shared
across Web, Desktop and Code. In 0.5.9 CycleArc actively checks the shared quota through
the connected Claude Desktop login during manual and configured scheduled refresh. A
successful check is **Updated** and includes its server-fetched time. Claude Code's
statusLine and Claude Desktop's subscription history remain fallback receipts, shown as
**Received** with their source time when a live check is unavailable. The
[official usage-limit explanation](https://support.claude.com/en/articles/11647753-how-do-usage-and-length-limits-work)
confirms the shared scope. The [2026-09-12 interface review](CLAUDE-USAGE-RESEARCH.md)
found no supported public personal-subscription quota query. The live path uses a known
private Anthropic OAuth endpoint reached with the Desktop session and is an internal compatibility path, not an
officially supported or stable public API. The Desktop history source below is an
app-owned local schema rather than a public API.

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
| Desktop OAuth live profile/usage | The Desktop OAuth access token is used in memory for GET https://api.anthropic.com/api/oauth/profile, identity verification, and GET https://api.anthropic.com/api/oauth/usage; the profile email is checked against the binding | Private/internal 0.5.9 compatibility path, not a stable public API; the separate Desktop Electron organization usage route is not used |

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

Verified connected profiles appear in the main view even before the first sample, with **Awaiting usage** and unknown limits. Waiting does not increase the attention count. Unconnected profiles appear only in **Manage accounts**. An explicit disconnection hides the account across restarts while preserving its cache and configuration; reconnecting shows it awaiting a new sample. A successful Desktop live check is **Updated** with its fetched time. If the live check is unavailable, idle time preserves the fallback receipt and a failure keeps the last valid values visible as stale.

**Web, Claude Desktop and Claude Code use the shared quota.** During manual and configured scheduled refresh, CycleArc first performs a read-only Desktop live check. It verifies the Desktop profile against the bound identity, then requests shared quota without launching a model turn. Claude Code statusLine and Desktop history remain fallback sources; when both are available, the newer observation wins. Repeating current-login connection reuses a matching binding and preserves its profile, order and quota history.

An installed Claude CLI remains required for official login verification and statusLine fallback. The Desktop live reader is a separate narrow path: it reads only the app-owned config.json and Local State, decrypts the protected OAuth access token in memory with Windows DPAPI/AES-GCM, verifies the profile identity, then requests the shared quota. It never writes or refreshes Desktop credentials, reads cookies, or retains the token. Authentication and connection setup remain owned by the official CLI/browser flow.

StatusLine usage arrives through the official statusLine; Desktop history arrives when Claude Desktop has written a recognized local sample. The live source uses the Desktop OAuth token in memory for Anthropic first-party profile and usage requests, then discards it. A live check launches no model turn. Its private/internal endpoints may change without notice. Claude may omit statusLine fields until a response or on unsupported plans, and Desktop may not have written a fallback sample yet; connection success is distinct from usable quota data. Restart a running Claude terminal session after changing its login or settings. CycleArc does not accept workspace trust dialogs or launch a model request for the user.

**Account attribution:** statusLine has no email or account identifier, and Desktop history carries an organization identifier rather than an email. CycleArc obtains current-login metadata through the official auth-status command, keeps email in memory for display, and persists only a hash of identity metadata with the profile binding. The live Desktop reader calls the Anthropic first-party profile endpoint with the Desktop OAuth token and verifies its email response against that bound fingerprint before requesting usage. History samples require a matching Pro/Max identity and organization. Live responses require a server-verified email and organization matching the saved binding. A changed or unavailable identity cannot replace the last good sample; an identity mismatch hides the live quota. Separate profiles retain separate selection, nickname and quota histories.

## Request failures and sign-in recovery

The Desktop live request can fail independently of local auth status. CycleArc classifies authentication, identity mismatch, rate limit, request and unavailable failures. Authentication and identity failures show **Sign-in required** or the corresponding connection state; a rate limit or transient request failure keeps the last-good values visible as stale. The live check never uses a model turn to test credentials.

**Sign in again** uses the existing profile's CLI and exact configuration mode for the official browser login. It preserves the nickname, ordering, selected account, connection time and quota history. A different authenticated identity is rejected rather than silently replacing the account. Successful reauthentication rotates a nonsecret binding generation so earlier failure callbacks cannot override recovery. Restart the affected Claude Code session after repairing its login; a Desktop live retry uses the connected Desktop account and does not create a model request.

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

- A successful Desktop live check is labeled **Updated** and records the server-fetched time. It uses the connected Desktop login and does not launch a model turn. A statusLine or Desktop history fallback is labeled **Received** with its source observation time; reading a local file does not stamp it with a new time.
- Manual refresh and the configured account interval actively attempt the live OAuth quota check through the connected Desktop session. The two-second passive loop reads only the local statusLine inbox and Desktop history cache; it never sends a quota request, starts Desktop or a model turn, or renews a receipt. History attribution may invoke the official CLI auth-status command.
- Live authentication, identity, rate-limit, request and unavailable failures preserve the last good projection as stale. A profile identity mismatch never replaces that projection and the mismatched live quota is hidden. Recovery asks the user to sign in to the intended Desktop account and retry.
- When live data is unavailable, the provider compares statusLine and Desktop history observation times and displays the newer fallback sample. Desktop history can lag behind a Code action and has no reset timestamps; unknown windows and reset fields remain unknown.
- A reset passing or elapsed idle time never rolls percentages back to zero. New valid live data clears stale state; a local fallback does not clear a failed live check. Receipt and projected quota data survive restarts, while the Desktop access token is never persisted.
- Each profile stores projected statusLine, Desktop history and live-usage metadata under the existing ProMeter directory. Only projected windows, source/receipt timestamps, bounded status data and binding hashes are serialized; no raw history, prompt, response, transcript, email or token is retained.
- The input limits and atomic previous-good cache rules apply to local receivers and projections. Unknown or malformed fields cannot create a successful sample or overwrite a previous-good value.

## Validation

Unit checks exercise synthetic official shapes, missing windows, malformed fields, fractional usage, stale/recovery/reset boundaries, cache backup and concurrent receipt ordering, provider separation, Desktop history version-2 samples and legacy registry compatibility. Production WPF checks cover mixed accounts in both languages and all themes, provider labels and aliases, stale values, compact connection guidance and Codex-only credit controls.

Both dev-run.ps1 and Windows CI validate the receiver in the built application and again in the single-file published executable. Synthetic checks cover the Desktop live projection and fallback ordering without touching real account settings or Desktop credentials. The real 0.5.9 Desktop check was validated against a bound profile and shared quota response; this proves the implemented compatibility path, not public API stability. Direct stdin, generated PowerShell, malformed input and missing-input deadlines remain covered. No Desktop token is persisted and no model request is used for measurement.
