# Documentation previews

These PNGs render the actual production WPF views with synthetic profiles and quota metadata.
They show the CycleArc product name, Codex/Claude provider labels and current connection controls.
They are not captures of a user's account or fabricated UI mockups. The sample percentages,
reset times and credits illustrate the layout; they do not promise specific plan entitlements.

| Files | Contents |
| --- | --- |
| `overview-dark.png`, `overview-light.png`, `settings.png`, `widget.png` | English single-account, settings and widget previews |
| `accounts-overview-{en,ko}-{dark,light}.png` | Two ready Codex accounts, with Work / 업무용 selected; the unconnected Claude profile is absent from the cards and counts |
| `accounts-manage-{en,ko}-{dark,light}.png` | All three registered profiles, including unconnected Research / 실험용 Claude, with connection, nickname and saved-order controls |
| `claude-waiting-{en,ko}-{dark,light}.png` | Connected Research before its first sample, with unknown shared subscription limits, Awaiting usage and Open usage page |
| `claude-overview-{en,ko}-{dark,light}.png` | Research selected with shared subscription usage, separate five-hour/weekly windows, the original receipt without an idle-time warning and the manual usage-page action |
| `claude-connection-{en,ko}-{dark,light}.png` | Automatic Claude connection, terminal-specific launch/receipt guidance, official-login choices and manual usage-page access; advanced settings collapsed (scroll to advanced details) |

The multi-account fixtures live in `DocumentationScreenshots.SampleAccounts`. They use the
names Personal / Work / Research (개인용 / 업무용 / 실험용), reserved `example.invalid` email
addresses and display-only paths under `C:\CycleArc-Samples`. Personal and Work have Codex
weekly usage of 18% and 64%; Work is selected, so its detail ring shows 64% used and the
quota row includes 36% remaining. Research is unconnected in the main/manager comparison:
the main popup counts two ready accounts, while management retains all three profiles.
Account-management previews scroll to the bottom so all three sets of actions are visible.

The waiting view shows Research after connection but before any quota sample. The Claude
overview then supplies a synthetic sample with 91% five-hour usage and 47%
seven-day usage, received 12 minutes ago. It remains Received without an idle-time warning,
with Auto selecting the known five-hour value (91%) for the ring. The display-period selector
applies to the ring, tray and widget. The last receipt date/time and preserved values remain
visible, and selecting
Claude removes the Codex reset-credit card. Connection previews drive the production
window through `IClaudeConnectionActions` using `PreviewClaudeConnection`; the adapter
returns fictional login metadata and only changes in-memory state. It never runs a CLI,
starts a browser, creates a home, opens a session or changes settings. All dates are
generated relative to export time.

Generate on Windows after building the solution:

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release -- --screenshots docs/images
```

To export only the four Claude usage previews for review:

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release -- --claude-usage-screenshots artifacts/claude-usage
```

To export only the 14 detail previews affected by the shared display-period selector:

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release -- --usage-period-screenshots artifacts/period-docs
```

The exporter never runs production startup, requests account data, or reads/writes user settings.
The full export creates 32 PNGs at 2x resolution through WPF `RenderTargetBitmap`, without taking a desktop
screenshot. It runs under the smoke harness's `OfflineApp`; the live-account diagnostic path
is not used. Each image must be visually inspected before replacing the checked-in files.
Export to an `artifacts/` directory for visual review before copying the generated images
into this directory. Keep the English and Korean README captions consistent with the
two-ready/three-registered comparison and the separate Claude periods.
