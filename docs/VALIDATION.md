# CycleArc validation

## Current release — Codex only

- Development launcher date regression (2026-09-14): reproduced the reported 23 failures
  in `PrimaryIndexSchemaSignalTests`, `ConversationSchemaHealthTests` and
  `IsolatedConversationFailureTests`. Their fixed September 6 fixtures were filtered out
  when the system clock moved into a later quota period. The retained legacy tests now use
  a fixture-aligned `MutableClock` in both `SyncEngine` and `ConversationParser`, including
  restarted engines and presentation snapshots. Existing cases remain; added assertions
  verify body fetches, completion timestamps and deferred failures after restart. Fatal-auth
  and paused-run checks explicitly advance the clock to keep timestamp-preservation checks meaningful.
  All 56 affected-class cases passed. The complete `./dev-run.ps1` run passed restore,
  Release build (0 warnings/errors), all 1,047 unit tests, 15 installer scenarios,
  120 account WPF renders, 180 widget DPI/layout renders and 36 production WPF renders.
  Self-contained win-x64 publish produced exactly `CycleArc.exe`; local replacement and
  startup succeeded. This changes test setup only; live-account quota behavior was not
  separately revalidated.

- Repository rename and default-branch screenshots (2026-09-12): repository, badge,
  download/issue links, clone instructions and the app's About link use `frozenvoice/cyclearc`.
  The default-branch update includes all 12 previously verified CycleArc production-view
  PNGs with synthetic data. Historical releases retain their original executable names;
  current source builds produce `CycleArc.exe`.
  `./dev-run.ps1 -NoLaunch` passed restore, Release build (0 warnings/errors), 1,047 unit
  tests, 15 installer scenarios, 120 account WPF renders, 180 widget DPI/layout renders,
  36 production WPF renders and one-file self-contained win-x64 publish. Local replacement
  and startup passed; the installed SHA-256 matches the validated artifact:
  `8FD53A931C652402A2709C2C51A198C0BF85493476E70048B8DF333956A09E2E`.
  This link/documentation update adds no behavior or regression-test cases and needs no
  additional live-account verification.

- CycleArc branding and provider labels (2026-09-12): `./dev-run.ps1 -NoLaunch` passed
  restore, Release build (0 warnings/errors), all 1,047 unit tests, 15 installer scenarios,
  120 account WPF renders, 180 widget DPI/layout renders and 36 existing production renders.
  The self-contained win-x64 publish contains exactly `CycleArc.exe` (product metadata: CycleArc).
  Local replacement/startup passed; installed SHA-256 matches the tested artifact:
  `808044C391F889F7B78B8F6021D4FDA43F5BCEDB23A6F8530A6D088A9289B6BA`.
  - Changed presentation files: `UiText`, `AccountSummary`, shared `CodexProviderBadge`,
    Flyout, FloatingWidget, About, Accounts/Settings and TrayController; application theme
    resources provide explicit provider-badge colors. Project/manifest metadata, App Server
    client identity, the launcher/installer and Windows artifact names use CycleArc.
  - Product-identity tests cover both languages, retained storage/mutex identifiers and the
    renamed handshake. Two new startup cases cover owned CodexMeter/ProMeter commands and
    reject unrelated installations or commands. Installer tests cover all five deployment/
    retry/rollback outcomes for each of CycleArc, CodexMeter and ProMeter.
  - WPF checks verify visible Codex badges in 0/1/3/8-account views, selected details and
    widgets, including long Korean/English names, every quota status, 80/100/150% zoom,
    all three themes and a minimum text contrast of 4.5:1. Widget alignment now checks the
    complete title/provider/usage stack; baseline and pixel-formatting checks remain.
  - All 12 documentation PNGs were regenerated from production views with synthetic data
    and visually inspected. Codex remains the only supported provider; no Claude/Gemini
    integration or data-format migration was added. No new browser login or reset-credit
    redemption was exercised, and live-account quota refresh was not independently verified.

- Multi-account README examples (2026-09-11): eight new 2x PNGs show the production
  popup and account manager in English/Korean and Dark/Light themes. All use fictional
  Personal/Work/Research profiles, `example.invalid` addresses and sample 18/64/91% usage;
  Work is selected and Research is stale. The manager is scrolled to show every order button.
  `--screenshots artifacts/readme-multi-account` exported 12 views; only the eight new
  account images were copied to the docs, and all eight were visually inspected. The
  exporter uses `OfflineApp` and `RenderTargetBitmap`, with no user account/settings access,
  Codex home inspection, live requests or desktop capture. README explanations match the
  displayed used/remaining values, selected-account scope, saved-data label and controls.
  All 21 local documentation links resolve. `./dev-run.ps1 -NoLaunch` passed restore,
  Release build (0 warnings/errors), 1,045 unit tests, five installer scenarios, 120 account
  WPF renders, 180 widget renders, 36 existing WPF renders and single-file win-x64 publish.
  This documentation/exporter change required no new live-account verification.

- Connection guidance, local nickname icons and account order (2026-09-11):
  `./dev-run.ps1 -NoLaunch` passed restore, Release build (0 warnings/errors),
  1,045 unit tests, five installer scenarios, 120 account WPF renders, 180 widget
  DPI/layout renders, 36 existing WPF renders and one-file self-contained win-x64 publish.
  Two new core tests verify saved account order after restart, unchanged selected snapshot
  and cache files, no refresh request, and boundary/invalid moves. The WPF checks additionally
  verify popup ordering, movement-button targets/boundaries, visible first-use instructions,
  all expanded guidance in a 470x400 window with the last account reachable, local initials
  (Latin, Korean and combined emoji), stable icon color/contrast and retained editing state.
  Korean dark and English light account management, expanded guidance, usage overview and
  Connection settings were rendered and visually checked with synthetic data.
  Official account documentation and the installed 0.147.0 generated schema expose email/plan,
  not a ChatGPT web nickname or avatar; no web profile scraping was added. Authentication and
  quota request paths are unchanged. This change did not require another live login or a real
  reset-credit use; order/persistence checks used isolated synthetic profiles.
  Local replacement/startup passed with the account-management entry point, and the
  installed executable's SHA-256 matched the verified staging artifact.

- Multi-account support (2026-09-11): `./dev-run.ps1 -NoLaunch` passed restore,
  Release build (0 warnings/errors), 1,043 unit tests (50 added cases), five installer
  recovery scenarios, 120 multi-account WPF renders, 180 widget DPI/layout renders and
  36 existing production WPF renders. win-x64 self-contained publish contains exactly
  `CodexMeter.exe`. Local replacement/startup passed and the installed executable's SHA-256
  matches the validated staging artifact. An earlier restore reported NU1900 feed warnings;
  the final complete run had no warnings.
  - Core cases cover generated account/login shapes, child-only home isolation, legacy
    settings/cache compatibility, 13-profile registry round-trip/backup recovery, malformed
    registries, known-home discovery/deduplication, removal without data deletion, identity
    changes, stale cache isolation, selection persistence and account-bound credit actions.
  - Deterministic gated-process tests cover six-account refresh with two concurrent reads,
    shared-refresh ownership, cancelled waiters, independent account failures and one
    interactive login alongside another account's read. Login tests cover early notifications,
    wrong login IDs, invalid URLs/handshakes, success followed by signed-out verification,
    cancellation, timeout and cleanup after a late process start. A real Node fixture verifies
    that a large UTF-8 stderr stream continues draining beyond the diagnostic budget.
  - WPF cases cover 0/1/3/8 accounts, both languages, all three themes and 80/100/150% popup
    zoom. Account selection does not initiate refresh, busy/cancel progress remains visible
    until cleanup, and a credit action retains its confirmed account through a nested UI
    selection change. A display-timer update interrupting account-name editing was reproduced;
    the new regression passes after the fix. Korean dark overview and English light management
    renders were visually inspected using synthetic metadata.
  - Live verification used installed Codex 0.147.0 and the production login/manager/client
    paths. The user completed official browser login with a second distinct email account.
    Both homes returned available quota metadata (2% and 99% used at the login check), and a
    fresh verifier process loaded both persisted profiles and returned 3% and 99%. The original
    Codex identity remained unchanged, home paths were separate and the checks reported process
    cleanup. An initial same-account login also exercised the matching-identity indication.
    No model turn, real reset-credit consumption or direct credential-file access was performed.
  - Live scope: one linked default home plus one app-owned home. Additional custom-home
    discovery, more than two accounts, cancellation/offline failures and first-use selection
    were covered with synthetic tests. Same-email workspace separation cannot be established
    from this protocol's email/plan projection; no such claim is made.
  - Opt-in diagnostic commands: `dotnet run --project tests/CodexMeter.UiSmoke -c Release
    --no-build -- --live-accounts read` checks existing profiles; use `login` to explicitly
    add a managed profile or `relogin` to sign into the last managed profile again. These
    diagnostics are excluded from ordinary tests/CI and print only projected status and
    percentages. Browser login requires the user.

- Selectable automatic refresh: defaults to five minutes and persists 1/2/5/10/30/60-minute
  choices independently of retired history-sync settings. Restore, Release build (0 warnings/errors),
  993 unit tests (21 new schedule cases), 36 production WPF renders, 180 widget DPI renders,
  five installer scenarios and win-x64 single-file publish passed. WPF checks cover all choices,
  Save/Cancel, live timer updates and compact-window scroll access in both languages/all themes.
  Korean dark and English light connection-tab renders were visually checked. Successful refresh
  completion restarts the timer; failed automatic attempts retain a two-minute minimum cooldown.
  Live-account polling over real minute intervals was not exercised for this change.

- Widget native-height alignment: reproduced on the home PC's three 4K monitors at 100% DPI:
  the shown window/content was 39 DIP high despite a 33 DIP content request, leaving 9 DIP
  above the status row and 15 below. Centering the status stack fixes the allocated-height
  case. The new regression failed before the fix and passed after it. 180 offline renders
  cover 100/125/150/175/200% layout DPI, both languages, all three themes, extra allocated
  height and available/stale/recovered states; native window checks pass on all three monitors.
  Korean dark previews at 100% and 150% were visually checked. This is layout validation
  with synthetic metadata; no real reset credit was consumed. Restore, Release build
  (0 warnings/errors), 972 unit tests, 36 existing WPF renders, five installer scenarios,
  single-file win-x64 publish and local replacement/startup also passed.

- Widget restart/credit actions: restore and Release build passed (0 warnings/errors), 972 unit
  tests passed, 36 WPF renders plus offline confirmation/cancellation/single-flight checks passed,
  five installer scenarios passed, and publish produced one self-contained win-x64 executable.
  The WPF harness suppresses production startup and uses the production DPI manifest.
  An actual secondary-monitor edge-position regression was reproduced; after correction the
  installed widget retained its original physical position across replacement and another process
  restart. Reset redemption was tested only with synthetic processes/delegates: selected ID,
  idempotency, malformed outcomes, failed handshake, cancellation, refresh/use serialization,
  stale blocking and exclusion of credit IDs from cache. No real reset credit was consumed.

- Widget alignment/DPI: removed the first row's asymmetric 6 DIP top margin and reused
  AppWindow pixel rounding, device snapping and Display text formatting. WPF startup now
  declares PerMonitorV2 in the executable manifest. Regression checks cover centered text
  and inherited rendering options in both languages/three themes, plus manifest wiring.
  Restore, Release build (0 warnings/errors), 956 tests, 36 WPF renders and win-x64
  self-contained single-file publish passed. Synthetic widget preview was visually checked.
  Physical mixed-DPI monitor transitions and transparent-window text clarity remain manual checks;
  no live-account access was needed.

- Public documentation: English README, Korean companion guide and four production WPF
  previews were prepared with synthetic quota data. The PNGs were visually inspected in
  dark/light themes; README relative links resolve. The exporter does not start the app
  or access accounts/settings. Restore, Release build (0 warnings/errors), 955 tests,
  36 WPF renders, five installer scenarios and the single-file publish passed.
  A targeted scan of 1,424 Git-history blobs found no matches for the checked private-key,
  GitHub/OpenAI/AWS-key or JWT patterns and no tracked sensitive data-file paths.
  This is a targeted check, not a guarantee that arbitrary secrets cannot exist.

- Reliability improvements: restore, Release build (0 warnings/errors), 955 tests,
  36 WPF renders and five isolated PowerShell installer scenarios passed. Completion timestamps,
  recent-failure cooldown boundaries, invalid/off-screen widget positions, atomic settings writes,
  corrupt/missing primary recovery and locked replacement are covered. WPF checks also cover
  dispatcher ownership/disposal, fixed versus System themes, widget recovery persistence and
  reset-on-save intent without mutating settings on Cancel. Installer tests cover transient/permanent
  locks, staging failure and failed startup with previous-version restart. Single-file publish and
  actual local replacement/startup passed in one launcher invocation.
  Physical monitor removal/DPI changes and live Windows theme toggles remain manual checks;
  account access uses the existing protocol and was not separately revalidated for these changes.

- Popup zoom: restore, Release build (0 warnings/errors), 934 tests and 36 WPF renders
  passed. Shortcut logic covers Ctrl +/-/0, shifted plus and keypad keys; normal keys
  and Ctrl+Alt are ignored. Settings default/round-trip/bounds and refresh preservation
  are checked. Popup rows render without overlap at 80/100/150% in both languages and
  three themes. Single-file publish and local replacement/startup passed after retrying
  the known transient directory lock. Physical keyboard input was not automated.

- Widget normal-state notice is collapsed, including its margin. All quota statuses
  and recovery to normal are checked in the WPF smoke suite across both languages and
  three themes. Restore, Release build (0 warnings/errors), 925 unit tests and 24 renders passed.

- Combined used/remaining row: restore, Release build (0 warnings/errors), 925 tests,
  24 WPF renders with row-overlap checks, single-file publish and local app startup passed.
  Korean/English weekly and five-hour labels, 0/100% and unknown percentages are covered.

- Last-checked elapsed time: Release build (0 warnings/errors), 918 tests and 24 WPF
  renders passed. Unit tests cover minute/hour/day boundaries, Korean/English, future
  timestamps and last-success vs failed-attempt timing. WPF checks use the real card
  width and reject overlapping last-checked text. Single-file publish and local app
  replacement/startup passed (the first replacement hit a transient folder lock; retry succeeded).

- Extension cleanup: restore, Release build (0 warnings/errors), all 903 remaining tests, and win-x64 self-contained single-file publish passed. Two obsolete extension-file consistency tests were removed; retained .NET regression tests remain.

- Product rename: clean restore and Release build passed with 0 warnings/errors;
  907 tests passed. New checks cover App Server client identity, both localized product
  labels and unchanged legacy settings/cache/registry/mutex identifiers.
- `CodexMeter.UiSmoke` loads production App/XAML without starting the app or accessing
  an account. All 24 window renders passed across Korean/English and Dark/Light/System.
  This smoke check also runs in Windows CI and the development launcher.
- Renamed project win-x64 self-contained publish contains exactly `CodexMeter.exe`.
  No live-account refresh was performed for this source/project rename.
- Release build: 0 warnings/errors; 907 tests passed, including shared-refresh ownership,
  waiter cancellation, retry after failure, unknown-vs-zero and cached identity-state tests.
- CI artifact-path parsing covers LF, CRLF, tab and space delimiters.
- Single-file Windows x64 self-contained publish verified: exactly `CodexMeter.exe`,
  with no browser extension, native host, WebView2 or external .NET runtime files.
- Header logo scales a 24 DIP vector canvas into 20 DIP; WPF stroke bounds fit completely inside the canvas.
- WPF flyout/settings rendered in dark and light themes using stored Codex quota metadata.
- Live standalone executable: refreshed successfully at 14:48:28 KST on 2026-09-07; server weekly usage 8%, remaining 92%. Chrome/Edge/Whale old native registrations absent; only CodexMeter process remained.
- Notification-icon replacement: production source/project guards pass and assembly reflection
  confirms retired taskbar strip/Win32 types are absent. No overlay is instantiated.
- Settings General/Widget/Connection pages rendered in Korean/English, dark/light, and compact
  sizing. Save/Cancel and disabled widget options checked through the WPF probe.
- Reset countdown boundaries, unknown/partial expiries, duplicate credit IDs, malformed
  counts, root-container precedence and old-cache loading covered by regression tests.
- Read-only live App Server check returned three available credit expiry timestamps;
  the bounded client reported child-process cleanup. Only projected quota metadata was used.
- Flyout renders the supplied two-card layout in dark/light and Korean/English. Quota labels
  and values pass overlap checks; eight expiry groups scroll without truncation. Multiple
  windows, stale, signed-out and unavailable states were rendered.
- Widget pure tests cover click jitter, successive screen deltas, negative coordinates and
  returning to the start after a drag. WPF gesture-handler tests with injected coordinates
  verify 80x40 DIP movement, one save, no accidental open, click opening and capture-loss handling.
  Physical mouse automation was unavailable: its kernel failed at sandbox ACL initialization.
- Compact flyout renders at 440x436 DIP for the three-credit snapshot. Widget-menu callback
  test confirms close fires once without exit; persistence/hide wiring has a regression test.
- Accordion WPF checks pass: 440x436 expanded, 440x317.33 collapsed, refresh preserves collapse,
  and 1/3/8 credit date groups keep the same expanded height. Dark/light and Korean/English render.
- Credit help WPF button-event regression: open, refresh while open, second-click close,
  third-click reopen, and parent-hide close all pass.
- Sliding refresh bar removed. WPF probe confirms identical 436 DIP height before, during
  and after refresh for the same snapshot in dark/light and Korean/English; busy button stays disabled.
- Runtime checks: verify actual Codex refresh, no ChatGPT HTTP/SQLite collector startup,
  no native host registration, saved window position and tray/refresh behavior.
- A fresh PC requires installed Codex CLI; official browser sign-in can be started from
  **Manage accounts**. No browser-extension pairing is part of setup.

## Historical ChatGPT checks (retired; do not use as CodexMeter setup)

The extension source and its CI checks have been removed. Commands and extension setup
below document historical validation only and require an old checkout from Git history.

# Manual validation

ProMeter reconstructs usage from ChatGPT account history. These checks require a real signed-in Pro account. Live ChatGPT compatibility is not claimed until they pass.

## Companion pairing

1. Load the unpacked `extension/` in Chrome or Edge.
2. Register the Chrome/Edge native host from Settings with that extension ID.
3. Sign in to ChatGPT in that normal browser. Do not use WebView2 for Google/Microsoft/Apple.
4. Click **Connect to ProMeter** in the extension popup.
5. Run the first manual sync. Sign-in alone must not scan history.

## Multi-device

1. On this PC, open the tray flyout and note the GPT Pro count.
2. On the Android ChatGPT app, send one GPT-5.6 Sol Pro or GPT-6 Pro message.
3. On this PC, choose **Sync now**.
4. Confirm the Pro counter increases by 1.

## Another PC

1. Use ChatGPT on a second Windows PC with the same account.
2. Sync on the machine running ProMeter.
3. Confirm the remote usage appears.

## Reasoning

1. In ChatGPT, send one GPT-5.6 Sol message with Extra High (`매우 높음`).
2. Sync.
3. Confirm **SOL REASONING → Extra High** increases by 1.
4. Confirm the Pro meter does **not** increase for non-Pro Sol reasoning.

## Regenerate

1. Send one Pro message.
2. Regenerate the response.
3. Sync.
4. If the conversation endpoint returns every branch `request_id`, the Pro count should increase by 1 for the regenerate.
5. If only the current branch is returned, Coverage should stay Estimated/Good and this limitation should remain visible.

## Archive

1. Use Pro in a chat, then archive that chat.
2. Sync.
3. Confirm the current-period Pro count still includes that usage.

## Project

1. Send a Pro message inside a ChatGPT Project.
2. Sync.
3. Confirm the event appears and Coverage lists Projects as available.

## Temporary / deleted

1. Use Temporary Chat or delete a conversation.
2. Confirm those turns do **not** appear after sync.
3. Coverage should continue to list Temporary and Deleted as unavailable.

## Official export

1. From ChatGPT Settings → Data Controls, download `conversations.json` if available.
2. Import it from ProMeter Settings.
3. Import the same file again.
4. Confirm usage counts do not double.

## Theme readability (Dark and Light)

Check each screen in Dark, then repeat in Light (Settings → Theme):

- Main
- Settings
- Flyout
- Coverage
- Welcome
- About

Confirm labels, text boxes, combo boxes and dropdown items, checkboxes, tab headers, DataGrid text/headers, and flyout values stay readable. No black/default text on a dark background, and no washed-out light text on a light background.

## Browser companion with VPN left on

Do not disable the browser VPN or proxy, switch browsers, or weaken browser security.

1. Keep the VPN/proxy enabled in Edge or Chrome.
2. Confirm chatgpt.com itself loads and is signed in in a normal tab.
3. Confirm the companion popup shows connected to ProMeter.
4. Leave that ChatGPT tab open (or let ProMeter open https://chatgpt.com/ and sign in there, then retry).
5. Run a manual sync.
6. Confirm ProMeter does **not** tell you to disable VPN.
7. If ChatGPT returns 403, the UI/log should say `ChatGPT rejected the page request (403)`, not `ChatGPT session expired`, unless the ChatGPT tab is independently signed out.
8. If no ChatGPT tab exists, the status should be `Open/sign in to ChatGPT, then retry`.
9. If MAIN-world page execution is blocked, the status should be `ChatGPT page bridge unavailable` rather than a silent service-worker fetch.


## Browser page timeout recovery

`node extension/test-companion.js` includes synthetic regressions for a hung session fetch,
response body, backend body, and 401 refresh; shared authentication recovery; late session
completion; injection before document idle; frozen/discarded tab preference; and late Chrome callbacks.
No real account content or credentials are used in these tests.

Live check after reloading the unpacked Edge extension:

1. Activate an already signed-in ChatGPT tab, then run ProMeter's full manual history sync.
2. Confirm account/index requests resume and the completed reconstruction is displayed.
3. Repeat with a background ChatGPT tab and, separately, an Edge sleeping tab. The companion
   should activate the same sleeping tab once and resume. It must not focus the browser window,
   reload the user's conversation, disable VPN, or reset stored usage.
4. Confirm stalled requests fail within the documented deadline and a later retry is not held by
   the previous session promise. `Connection required` can also reflect a previous BridgeTimeout;
   inspect the safe sync failure category rather than treating that label as proof of logout.

Live result on 2026-09-07: after the user reloaded/reconnected the fixed-ID Edge companion,
manual history sync completed at 13:28:36 +09:00 with `UpToDate`, `coverage=Estimated`,
and zero failed conversations. This verifies history transport recovery, not an authoritative
remaining-quota count. Subsequent 13:37–13:38 retries failed at page preparation, so this earlier success alone did not establish a complete fix. Version 5 adds bounded same-tab wake-up recovery; verify this with the deployed version.


## Unknown-reset Monday regression

- With no configured/confirmed reset, weekend Pro observations must remain in **Last 7 days reconstructed**
  after Monday midnight. Never turn the default Monday preference into a quota reset.
- No next-reset date is displayed until there is an applicable anchor. Server counts and configured
  reset periods still follow their original rules; local-calendar Sol analytics are unchanged.
- `RollingProHistoryTests` uses synthetic weekend, too-old, and future requests to verify the boundary.

- Reset lookup fallback regression: empty init followed by model-limit metadata must recover reset timing; an ordinary model catalog must never manufacture a reset. Bridge version 6 requires extension Reload; live reset retrieval remains unverified.

## Cross-PC verification, 2026-09-07

- After extension Reload, live sync completed at 14:17:43 KST: UpToDate / Estimated,
  34 loaded conversations, 0 failed; archived and 9 project indexes were checked.
- Current local server-status metadata still has no model limits or retained reset.
  Provider fallback did not recover a reset from this live response.
- The user-referenced home conversation records a prior reset around
  2026-09-06 14:20:14 KST and a reconstructed count of 12. The attached log examined
  here did not independently establish that reset timestamp.
- A read-only production QuotaEngine calculation over the freshly synchronized local
  metadata produces 12 with that historical-chat anchor, versus 72 in the rolling
  seven-day window. No server-status/settings values were overwritten from chat text.
- Account-history collection supports cross-device reconstruction; identical current-cycle
  displays on fresh PCs remain unverified until the same evidenced reset boundary is
  available on each PC. A successful scan does not establish authoritative billed usage.
