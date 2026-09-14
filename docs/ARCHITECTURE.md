# CycleArc architecture

## Active product — Codex and Claude Code (2026-09-12)

- Codex displays the five-hour (300 minutes) and weekly (10,080 minutes) windows supplied by
  the official App Server, regardless of plan name or primary/secondary position. Account
  cards and details retain all reported windows. Compact surfaces prefer a known weekly
  percentage, then a known five-hour percentage; an unknown weekly value cannot hide usable
  five-hour data. Missing windows are omitted, and unknown percentages are never filled with zero.
- `IUsageProvider` creates isolated `IUsageAccountService` instances for the shared account
  manager. `CodexUsageProvider` adapts the existing Codex service without changing its
  protocol/client, identity verification, concurrency or cache format. `ClaudeUsageProvider`
  reads only a local inbox populated by Claude Code's official statusLine stdin.
- Claude's projected windows describe shared Web/Desktop/Code subscription quota. Code is
  the delivery source; receipt recency does not prove current server usage. Claude UI labels
  even recent samples as Received, with shared scope and last-sample guidance. A user action
  opens the official usage page in the default browser for manual inspection; no page or
  credential collection occurs. See [the official-interface review](CLAUDE-USAGE-RESEARCH.md).
- Claude stale presentation uses explicit localized warning text and a theme-aware amber
  resource on details, account cards and widget, with an amber detail ring. These surfaces
  retain the receipt date/time. A separate bounded tray formatter places freshness,
  receipt and shared scope ahead of a potentially long nickname; full tooltips also retain
  the Code delivery context. Presentation changes never refresh provider metadata.
- Public `CodexAccount*` / `CodexQuota*` type names remain as shared presentation records for
  caller/cache compatibility; their default provider is Codex. Claude accounts carry
  `Provider = Claude`, an empty shared home path and no Codex authentication/credit capabilities.
  `ClaudeConnectionService` separately owns official Claude CLI login and connection settings.
  The account registry advances to version 2 only when a Claude profile is added, so older
  Codex-only builds fail closed. Existing homes, aliases, order, selection and Codex caches remain.
- `Program.Main` routes `--claude-statusline-bridge <encoded-options>` and the legacy
  `--claude-statusline <profile-id>` before WPF, the desktop mutex,
  settings, tray and account startup. It accepts only an existing Claude profile, reads bounded
  stdin and stores the two projected rate-limit windows with local receipt/status metadata.
  No raw JSON, session/project/transcript metadata, auth/token file or `/usage` parsing is used.
  This headless entry point is part of the one self-contained `CycleArc.exe`.
- Claude callbacks merge under a bounded exclusive lock and atomically replace a per-profile
  cache with a previous-good backup. Earlier receipt times cannot overwrite newer data.
  Missing/malformed input retains the last good values as stale. Optional absent windows are
  not filled from another sample. Idle samples stay Received with their original receipt;
  elapsed time and passed reset timestamps do not raise attention or invent zero usage.
  Invalid receipt metadata still makes the saved values stale.
- A separate two-second passive check reads Claude inboxes off the UI thread and emits changes
  only for new data/freshness transitions, preserving nickname editor focus. It does not start
  Codex, renew receipt timestamps or alter manual-refresh ownership. Normal manual refresh
  also reads Claude's inbox. The existing Codex interval and bounded batch remain unchanged.
- The connection window offers current-login connection and official browser login. A single-flight
  cancellable `ClaudeConnectionService` invokes `claude auth login --claudeai` / `auth status --json`.
  Browser logins use new per-profile `CLAUDE_CONFIG_DIR` folders; current-login connection uses the
  effective existing folder and preserves whether that environment variable was originally unset.
  CLI-owned credentials are never opened by CycleArc. Email is held only
  in memory; paths, identity fingerprint and connection time use a separate provider metadata file.
- `ClaudeStatusLineInstaller` updates statusLine and one owned `hooks.StopFailure` command, with validation, a local backup,
  an exclusive lock and atomic replacement that checks for concurrent changes. The encoded wrapper
  preserves an existing command's stdin/output; reconnect is idempotent and disconnect restores the
  previous entry only while the active command is still owned by this profile.
- StatusLine has no identity fields. Each automatic callback verifies the current CLI login against
  its binding before accepting usage; a changed login marks the last sample stale. Old configuration
  callbacks and quota samples predating a new binding cannot populate the new account. A current-login
  check cannot authenticate an already-running session's emitter; users must restart sessions after
  external login changes. The old manual receiver cannot bypass an automatic binding's checks.
  Passive polls never invoke authentication. Details are in [CLAUDE.md](CLAUDE.md).
- Official StopFailure events carry request-error classifications through a separate bounded headless
  receiver in the same executable. Only projected failure kind, binding generation and observation
  time are persisted in `claude-failure.json`; no raw hook metadata is stored. Local signed-in
  metadata does not prove remote authentication validity. A matching authentication failure shows
  Sign-in required, preserving any quota sample as stale with its original receipt. Authentication
  failure persists until explicit sign-in recovery; repeated cached quota is not proof of repair. Existing-profile reauthentication uses the same CLI/configuration,
  preserves identity and quota history, and rotates a generation to reject earlier failure callbacks.
  Connection inspection upgrades exact owned settings only; user replacements remain untouched.
- A new Claude connection uses a draft profile and removes it on modal completion only if no
  connection or usage record exists. Repeated current-login setup resolves the existing profile
  for the same verified identity, configuration path and implicit/explicit directory mode.
- `UsageAccountOverview` shows verified connected Claude profiles before their first sample,
  with unknown limits and **Awaiting usage**, excluded from attention totals. It also retains usable
  stale samples during temporary failures. Unconnected profiles remain in account management.
  Account totals, attention totals, detail selection, tray
  tooltip and widget all use this same projection, with an ordered fallback when the saved selection
  is hidden. The projection never deletes profiles or rewrites the user's saved selection.
  Explicit Claude disconnection is persisted separately from its preserved quota cache, so a restart
  cannot resurrect the disconnected card. Reconnection shows **Awaiting usage** until a new official
  sample arrives. Terminal Claude Code responses supply quota fields. Web/Desktop activity,
  including the Desktop Code tab, consumes the same allowance; this integration supports receipt
  from the connected terminal CLI. The waiting text and launch action identify that requirement.

- The repository is `frozenvoice/cyclearc`; clone instructions use the folder `cyclearc`.
  Clone instructions, documentation badges/download links and the app's repository link use
  the same repository. The solution is `CycleArc.sln`; source/test projects use CycleArc names.

- Tooltips share theme-owned foreground/background/chrome. Plain-string content wraps at a
  bounded width; oversized content can scroll within a bounded height. Rich UI content remains
  intact, and live placement targets propagate theme changes to open popups.

- Widget clicks use the explicit popup-open path. An unpinned popup covered by another
  window can still have `IsVisible == true`; inspecting it activates the existing window
  instead of toggling it hidden. A minimized popup is restored before activation. Explicit
  close and the tray toggle retain their behavior.

- Product branding is **CycleArc**. The solution, project paths, namespaces, assemblies,
  shipped executable, window titles, menus, startup entry and App Server identity use CycleArc.
  Saved account/cache data and the shared single-instance mutex retain their existing
  identifiers. Startup cleanup recognizes both previous product
  entries only when their command points to this installation; existing opt-in still gates it.
- A shared `UsageProviderBadge` identifies Codex or Claude on account cards, selected quota details and
  the widget. Its explicit text/background/border colors follow Dark, Light and System themes.
  Account usage rows and tray tooltips also identify the selected provider. The widget shows
  the CycleArc title. Provider selection follows the selected account, never a login switch.

- Account ordering swaps profile references atomically in the existing registry; service/cache
  identity and selected account remain keyed by local ID. Reordering raises a presentation change
  without starting a refresh, and every account list follows the saved profile order.
- Connection settings and account management include contextual Korean/English setup guidance.
  Connection options initially expand for accounts with no successful check; existing users start
  with their list. The complete account body scrolls, including expanded advanced/action guidance.
- The official account projection has no ChatGPT web nickname/avatar. Existing local labels remain
  the nickname source; WPF generates a two-grapheme badge and stable profile-ID color without
  another network request, credential access or new persistence format. These icons are explicitly
  described as local presentation, and label edits do not change the ChatGPT profile.

- Multi-account support uses one `CodexAccountManager` with a registry of local profile IDs,
  labels and absolute Codex homes. `CodexRefreshCoordinator` owns the shared batch; account
  services own independent snapshots and read/redemption gates. At most two reads run at once.
  A single browser login may run alongside other accounts' reads; the logging-in profile is skipped.
- `CODEX_HOME` is set only in the child's `ProcessStartInfo.Environment`. Managed profiles use
  `cli_auth_credentials_store=file` through an invocation override; Codex owns persistence and renewal.
  Linked profiles retain the original credential-store configuration. API-key environment overrides
  are removed from account-scoped children. No authentication file is inspected, copied or migrated.
- Discovery probes only known environment/default homes through `account/read`; custom homes use a
  folder picker. It neither enumerates conversation files nor scans credential files. Removing a profile
  forgets its reference and suppresses automatic rediscovery without deleting or logging out its home.
- Login uses `account/login/start` with `type: chatgpt`, an exact official HTTPS browser origin allowlist,
  matching `account/login/completed` notifications, and a final `account/read`. An early completion
  notification is retained. Cancellation sends `account/login/cancel`, then the existing bounded
  shutdown reaps the process. Login has a five-minute ceiling; browser completion never implies quota
  success. UI progress stays active until cleanup finishes, including when the window closes.
- `codex-accounts.json` is atomic with backup recovery and fails closed on unrecoverable/unknown
  registry shapes. Existing settings remain separate; `default` retains the legacy quota-cache path.
  Each new profile receives its own home and cache. Emails are protocol-projected and memory-only;
  an identity hash binds quota caches and detects reported identity changes before stale fallback.
  Unavailable/signed-out identities never inherit another account's cached percentages.
- Codex profiles keep a separate, atomic, hash-only account binding. Legacy cached email/plan hashes
  must match the first official account read before migration to a normalized email hash; cached quota
  stays hidden until that verification. Plan changes after migration do not change the binding.
  Passive refresh never replaces a bound identity. A changed/missing identity hides quota and credits
  while preserving the binding; only explicit successful login may replace it.
- An imported Codex profile that matches a managed profile is projected as a connection conflict
  without percentages or credits. This also gates credit actions. Matching metadata does not prove
  workspace identity: account/read does not expose a stable workspace identifier.
  A data-free conflict marker keeps recovery required across restarts and removal of the matching
  managed profile; changes to another profile cannot make ambiguous cached quota trustworthy.
- Reconnecting an imported profile signs in to an unregistered, isolated managed home and verifies
  quota before atomically replacing the old reference at its current list position. The replacement
  has a new local ID required by the managed-home path contract; the same logical selection and
  current nickname are preserved, and the old home is ignored by discovery. Cancellation, duplicate
  login, failed quota and registry-write failure preserve the old registered profile and its data.
- Generated protocol 0.147.0 `Account` exposes email/plan, not a stable workspace ID. Matching
  reported identities are indicated without merging profiles or claiming distinct workspaces.
  Reset actions capture the selected local profile before confirmation and re-check reported identity
  inside the consuming App Server process. Credit IDs remain memory-only.
- Flyout overview cards show all accounts; the selected account reuses the established ring/credit
  detail layout and drives tray/widget output. The widget always identifies the selected account,
  including a single profile: its nickname, otherwise email or the existing provider/profile fallback.
  Tray account labels identify the selection when multiple profiles exist.
  `AccountsWindow` shares the existing Korean/English strings and themed controls.
- Official references: [Codex state locations](https://learn.chatgpt.com/docs/config-file/config-advanced#config-and-state-locations),
  [credential ownership](https://learn.chatgpt.com/docs/auth#credential-storage),
  [App Server account/login protocol](https://learn.chatgpt.com/docs/app-server#authentication-endpoints).

- The widget centers its complete status stack within the actual native window height.
  `SizeToContent` does not guarantee that the allocated height equals the requested content
  height; extra native-window space must be shared above and below the text.
- Widget positions persist physical screen pixels alongside legacy DIP coordinates. Initial
  restoration waits for target-monitor DPI/layout to settle before recovery or persistence.
  A fully visible widget keeps its exact position, including within 8 pixels of an edge.
- Each live reset-credit row has an explicit, confirmed Use reset action. The service serializes
  redemption with reads, sends the selected opaque ID and a per-attempt idempotency key through
  the installed App Server, then the app reads fresh limits. Uncertain retries reuse the same
  key in memory; no automatic redemption/retry occurs. Cached/stale rows cannot redeem.
  App exit cancels and waits for the bounded redemption process as well as active refreshes.

`CycleArc.exe` → `CodexAccountManager` / shared `CodexRefreshCoordinator` → per-account `CodexQuotaService` →
installed, signed-in Codex CLI (`app-server --stdio`) → account/rate-limit metadata.

- Tray, flyout, widget and timer all share one bounded refresh. Cancelling a
  waiting caller does not cancel the active owner's work. File/process work runs off the UI thread.
  Successful-check timestamps use completion time, while attempt timestamps retain start time.
  Automatic checks use the newest attempt/completion and the saved interval, with a minimum two-minute cooldown after failures; manual checks remain available.
- Both providers show actual provided periods, used/remaining percentages and reset times.
  Codex additionally shows reset-credit metadata; Claude shows last receipt and passive-data
  guidance and hides credit controls. Signed-out/missing Codex CLI states do not display cached
  percentages as current; stale data is explicitly marked.
- The desktop never constructs ChatGPT transports, collectors, SQLite stores, pairing servers
  or Pro reset services. Retired WPF views and WebView2 are excluded from its build.
- Publish bundles the .NET runtime and produces exactly one `CycleArc.exe`; Codex CLI itself
  remains an external installed/sign-in prerequisite. No extension or companion host is shipped.
- Existing settings/cache paths are retained; history is neither read nor deleted.
  Startup removes only known native-host registrations matching the old owned manifest path.
  The browser extension must be removed via the browser's extension manager.
- The solution is `CycleArc.sln`; all source/test projects and namespaces use CycleArc.
  `src/CycleArc` is the WPF app, `src/CycleArc.Core` holds active and retained legacy logic,
  `tests/CycleArc.Tests` holds regression tests, and `src/CycleArc.CompanionHost` is the
  retired host, still built for legacy checks but never published with the app.
- `LegacyInstallation` centralizes persisted data, registry, mutex, pipe and backup identifiers.
  These retain their old values so upgrades continue finding settings/cache and safely removing
  previous registrations. The shared mutex also prevents old and new builds running together.
  The development launcher and installer retain previous executable names only for scoped
  replacement and rollback; their regression fixtures exercise those exact legacy names.
  New diagnostic log files use the `cyclearc-` prefix; previous log files remain in place.
- The retired `extension/` source and its CI validation step have been removed. Extension-only
  file consistency tests were removed; retained .NET transport and manifest fixture tests remain.
  Previous extension sources are available in Git history.
- Windows owns the notification icon's allocated slot through NotifyIcon. The retired taskbar
  overlay and TaskbarWin32 are excluded from the desktop build, with no geometry/fullscreen
  polling. Its old JSON settings remain compatible but startup/save disable the overlay.
- Settings has General, Widget and Connection tabs with themed switches, sliders and explicit
  Save/Cancel behavior. Korean/English and dark/light themes use the existing resources.
  Writes flush a same-directory temporary file before atomic replacement, retaining the previous
  valid primary as `.bak`. Missing or corrupt primary files recover from backup; corrupt data never replaces it.
  System theme/display, power-resume and session-unlock/connect events marshal through the
  owning Dispatcher and unsubscribe on exit.
  Fixed themes ignore system theme changes. Widgets recover into connected work areas after
  startup, size/DPI or monitor changes; position reset is applied only on Settings Save.
  `FloatingWidgetController` owns each widget window and its subscriptions. The existing
  two-second passive timer also checks native visibility, minimized state and topmost flags
  independently of quota changes. A closed window is replaced while the widget remains enabled
  with a displayable account. Coalesced resume/unlock/display recovery replaces the transparent
  HWND and restores saved preferences without activation; queued callbacks cannot resurrect a
  disabled widget or run after disposal. Minimized rectangles never overwrite saved positions.
  WPF's DPI resize can request activation even inside a non-activating native move. Widget
  settings/position updates, minimized restores and controller-owned closure use a synchronous
  activation guard on the current UI thread; it is released on success or exception and does
  not change DPI scaling, pointer interaction or other processes.
  Local installation retries bounded file operations and restores/restarts the old executable
  when deployment or startup fails. Failed pre-backup moves never restore a stale backup.
- Reset rows show server reset time plus remaining days/hours. A one-minute UI-only timer
  refreshes countdowns; account refresh uses the saved 1/2/5/10/30/60-minute schedule (default five minutes).
- Last checked combines the local successful-refresh time with elapsed minutes/hours/days.
  The same display timer updates it without a request. Failed attempts do not reset its age;
  missing or future timestamps never produce an invented elapsed value.
- The detail popup supports Ctrl +/- and Ctrl 0, including keypad keys. A persisted
  80–150% scale in 10% steps scales the complete layout so fixed-size text does not overlap.
  Work-area bounds cap effective width, and the body scroll limit accounts for the scale.
  Loading settings and repeated keys at a limit do not trigger redundant saves.
- Root reset-credit metadata takes precedence as one container. Available Codex reset credits
  are deduplicated by ID. IDs for explicit redemption live only in memory; the cache contains nullable expiry timestamps only.
  The additive cache field preserves older snapshots; missing/partial expiries stay explicit.
  The separate reset-credit card shows each credit in a bounded scrolling list, with local expiry date and HH:mm always visible.
- Flyout follows the supplied two-card layout: large usage ring, separated quota rows, and a
  reset-credit list below. Its 440 DIP width and scrollable body fit the current work area.
  Fresh/refreshing/stale/identity-failure states stay distinct; credit unknown is never zero.
- The widget context menu includes Close widget, which saves its disabled preference and hides
  only the widget. Settings can re-enable it; the tray and app remain running.
- Widget drag uses captured screen-coordinate deltas, independent of the moving window.
  Crossing the movement threshold suppresses click-to-open; release or capture loss emits
  one position-save event. Click-through remains an explicit setting and disables input.
- The final supplied sample removes the redundant ring legend and places the credit count in
  a badge beside its title. The badge opens help; the right chevron toggles the expiry list.
  Expansion defaults on, survives refresh, and keeps a 108 DIP list viewport for 1+ credits.
- Reset-credit help is click-toggled on one reused tooltip instance. Refresh keeps its state;
  hiding the detail window closes it. Automatic hover opening is disabled for this button.
- Flyout refresh uses only the header status and fixed-size spinning button; no sliding progress
  bar or duplicate in-card refreshing message changes the card height.
- Flyout header exposes refresh, settings, pin and close. Settings is owned by the visible
  flyout so it stays above a pinned card. Ring captions omit the product prefix; the old
  accuracy badge is removed. Widget-only options are disabled when the widget is off.
- Cleanup audit: old WPF views/WebView2/companion are excluded from desktop build, but legacy
  Core logic, SQLite package, compatibility settings fields and regression tests remain.
  Removing that shared legacy layer requires a separate source/project split; none runs as
  a CycleArc history collector.

## Historical ProMeter architecture (retired)

The following describes retained legacy code and prior investigations. It is not the
runtime or setup contract for CycleArc.
# ProMeter architecture

ProMeter reconstructs ChatGPT Pro usage from **account conversation history**, not from local request interception. History is the reconstruction input. Only a matching server quota counter is authoritative. That is what allows company PC, home PC, and mobile usage to share one meter.

```text
Browser companion (recommended)
  Chrome/Edge tab
    → MV3 extension (operation allowlist, page-local auth, endpoint projection)
    → Native Messaging full-duplex (stdin↔pipe and pipe↔stdout pumps)
    → prometer-companion-host.exe
    → named pipe ProMeterCompanion (CurrentUserOnly, serialized writer)
    → CompanionRequestHub (timeout, generation, FailAllPending)
    → BrowserCompanionTransport (logical operations only)

WebView2 fallback (only when that sign-in works)
  WebView2 session → SessionAuthCoordinator → WebViewTransport

Data Export (non-real-time)
  conversations.json  →  ConversationExportImporter

All three keep the same ChatGptProvider → SyncEngine → SQLite → QuotaEngine path.
Transports are never swapped silently.
```

## Layers

- `ProMeter.Core` — models, ChatGPT provider abstraction, parsers, SQLite, quota, import/export
- `ProMeter` — WPF tray app, WebView2 fallback login, companion pipe server, notifications, startup
- `ProMeter.CompanionHost` — Chrome/Edge native messaging host (`prometer-companion-host.exe`)
- Former `extension/` (Git history only) — Manifest V3 companion. `https://chatgpt.com/*` only. No `cookies` permission.

## Provider abstraction

`IChatGptProvider` is the only business-facing ChatGPT API. Endpoint strings live in `ChatGptEndpoints`. If ChatGPT changes paths, only the provider/endpoints change.

Observed 2026 web paths (same-origin from chatgpt.com):

- `GET /api/auth/session`
- `GET /backend-api/me`
- `GET /backend-api/accounts/check/v4-2023-04-27`
- `GET /backend-api/models`
- `GET /backend-api/conversations`
- `GET /backend-api/conversation/{id}`
- `POST /backend-api/conversation/init` (quota metadata, if present)
- `GET /backend-api/gizmos/snorlax/sidebar`
- `GET /backend-api/gizmos/{id}/conversations`

These are unofficial internal endpoints the ChatGPT website itself uses. They can change without notice. Programmatic history access is unsupported and may carry account or terms risk. Official ChatGPT Data Export remains the lower-risk, non-real-time fallback.

## Authentication

Social login must use a normal Chrome or Edge session plus the browser companion. Embedded WebView OAuth for Google, Microsoft, and Apple is unsupported. ProMeter does not spoof a user agent.

The companion security model:

- Native Messaging is initiated by the unpacked extension (`com.prometer.bridge`).
- The host binds only to the current-user named pipe `ProMeterCompanion` on loopback.
- A local pairing token authenticates host messages. It is not a ChatGPT secret.
- Native Messaging is two concurrent pumps. Application-initiated invoke messages do not wait for another extension message.
- The extension constructs method/path from a closed operation enum. Arbitrary `/backend-api` fetch is forbidden.
- Responses are projected through endpoint-specific metadata allowlists before leaving the browser. Access tokens stay in authenticated chatgpt.com page-local memory only.
- Cookie databases are never read. Cookies and access tokens are never sent to the Windows app.

WebView2 remains an optional fallback for authentication methods that actually work there. Its profile lives under `%LOCALAPPDATA%\ProMeter\webview`. Access tokens stay in process memory only.

Backend fetches validate both the current HTTPS ChatGPT origin and the requested relative target before credentials or JavaScript are attached.

## Deduping

One `request_id` is one usage event, even when hidden reasoning, tool calls, and the final assistant message share that id. If `request_id` is missing, ProMeter falls back to conversation + message id.

All mapping nodes are scanned, so regenerate branches are counted when the server returns them.

## Privacy

Usage events store model, timestamps, request/message ids, and source metadata only. Prompt and response text are discarded.


## Browser page request deadlines

- The page bridge runs immediately rather than waiting for `document_idle`; its modules do not
  depend on DOM readiness. Exact HTTPS origin and operation allowlists still apply.
- Runnable ChatGPT tabs take priority over frozen/discarded tabs. A suspended tab, or a tab
  whose bridge probe times out, is activated once in its own window before retrying preparation.
  This can select the ChatGPT tab but does not focus the browser window, explicitly reload/navigate
  a conversation, change VPN settings, switch accounts/tabs on fetch failure, or change transports.
- Tab query, bridge probe, and injection have bounded setup waits. The extension invocation has
  a 55-second ceiling, below the native hub's existing 60-second deadline.
- Page fetches and response-body reads share a 40-second operation deadline and AbortSignal.
  A stalled session probe releases single-flight waiters so retry can authenticate again;
  late responses from timed-out probes cannot overwrite recovered page-local authentication.
- PAGE_BRIDGE_VERSION 5 replaces the prior unbounded page executor after extension reload.
  Conversation endpoint/whole-conversation budgets are unchanged.

## Unknown Pro reset

When neither an applicable server/retained anchor nor a user-configured anchor exists, Pro
reconstruction and history scanning use the rolling interval (now - 7 days, now]. It is labelled
"Last 7 days reconstructed" and has no next-reset timestamp. It must never be described as an
actual quota cycle, exact remaining amount, or lower bound. Confirmed quota periods and Sol
local-calendar day/week analytics retain their existing rules.

- When init has no usable Pro metadata, quota lookup also checks models metadata. The models projection preserves only the existing scalar quota allowlist. Confirmed-reset cache is local to each PC; conversation sync does not transfer it.
