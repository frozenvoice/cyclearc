# Documentation previews

These PNGs render the actual production WPF views and native tray icons with synthetic profiles and quota metadata.
They show the CycleArc product name, Codex/Claude/Cursor provider labels and current connection controls.
They are not captures of a user's account or fabricated UI mockups. The sample percentages,
reset times and credits illustrate the layout; they do not promise specific plan entitlements.

| Files | Contents |
| --- | --- |
| `updates-{en,ko}-{dark,light}.png` | Production update window showing a sample 0.6.0 → 0.6.1 upgrade with the release's widget and installer highlights; download and restart require separate approval |
| `overview-dark.png`, `overview-light.png`, `settings.png`, `widget.png` | English single-account popup, settings, and the multi-account widget (three synthetic accounts in one row) |
| `accounts-overview-{en,ko}-{dark,light}.png` | Two ready Codex accounts, with Work / 업무용 selected; the unconnected Claude profile is absent from the cards and counts |
| `accounts-manage-{en,ko}-{dark,light}.png` | All three registered profiles, including unconnected Research / 실험용 Claude, with connection, nickname and saved-order controls |
| `claude-waiting-{en,ko}-{dark,light}.png` | Connected Research before its first sample, with unknown shared subscription limits, Awaiting usage and Open usage page |
| `claude-live-{en,ko}-{dark,light}.png` | Research selected with a synthetic server response: 46% five-hour / 11% weekly, known resets, Updated and Last checked |
| `claude-overview-{en,ko}-{dark,light}.png` | Research selected with a Desktop history fallback: 91% five-hour / 47% weekly, unknown resets, Received and original observation time |
| `claude-connection-{en,ko}-{dark,light}.png` | Official CLI connection choices, existing Desktop login for server checks and manual usage-page access; advanced settings collapsed |
| `cursor-popup-{en,ko}-{dark,light}.png` | Cursor Models and Other Models monthly allowances, Grok Bot weekly allowance, named usage ring, disabled on-demand and successful update time |
| `cursor-widget-{en,ko}-{dark,light}.png` | Cursor Models / Other Models monthly and Grok Bot weekly summary, named ring and short update age; exact times and omitted budgets remain in tooltips/detail |
| `cursor-widget-summary-{before,after}-{en,ko}-{dark,light}.png` | Fixed synthetic Codex / Cursor / Claude accounts in the actual WPF widget, before and after the Cursor summary change at 100% zoom |
| `cursor-widget-compact-{before,after}-{en,ko}-{dark,light}.png` | The follow-up comparison from `f0a9964`: the same synthetic mixed accounts, with the repeated ring target moved inside the ring at unchanged width/font sizes |
| `cursor-accounts-{en,ko}-{dark,light}.png` | Current Windows Cursor connection, reconnect/disconnect, nickname and saved-order controls |
| `cursor-tray-icons.png` | Cursor fractional inputs shown as whole tray digits in number/ring styles at 16/24/32 pixels on dark/light taskbars; zero, full usage and unknown included |

The multi-account fixtures live in `DocumentationScreenshots.SampleAccounts`. They use the
names Personal / Work / Research (개인용 / 업무용 / 실험용), reserved `example.invalid` email
addresses and display-only paths under `C:\CycleArc-Samples`. Personal and Work have Codex
weekly usage of 18% and 64%; Work is selected in the account overview, so its detail ring shows
64% used and its quota row includes 36% remaining. Research is unconnected in the main/manager
comparison: the main popup counts two ready accounts, while management retains all three profiles.
Account-management previews scroll to the bottom so all three sets of actions are visible.

The widget has its own fixture in `DocumentationScreenshots.Export`: Personal (Codex, 28%
weekly), Lab (Codex, 62% five-hour), and selected Work Claude (85% five-hour / 23% weekly).
Its header shows independent size controls, shared refresh, Settings and Close widget.
Period labels share a baseline with the remaining values and a fixed marker gutter;
the two-period block and the rings across accounts share the same vertical alignment.
The settings preview uses the production window's declared size so the General tab is fully visible.

The Claude previews use those same profiles with Research connected and selected, so all three
accounts appear. The waiting view has no quota yet. The server preview supplies synthetic
46% / 11% usage and reset timestamps to show Updated / Last checked. The separate history
preview supplies 91% / 47% observed 12 minutes ago. Desktop history has no reset timestamps,
so those remain unknown, and the original Last received time stays visible. Exporting through
any supported command uses the same source and reset metadata for each image name.

Auto selects the known five-hour value for the ring; the display-period selector applies to
the ring, tray and widget. Selecting Claude removes the Codex reset-credit card. These previews
illustrate display states, not real-account compatibility. Connection previews drive the
production window through `IClaudeConnectionActions` using `PreviewClaudeConnection`; the adapter
returns fictional login metadata and only changes in-memory state. It never runs a CLI,
starts a browser, creates a home, opens a session or changes settings. These Claude fixture dates
are generated relative to export time; the Cursor summary comparison below uses a fixed time.

Generate on Windows after building the solution. Export to `artifacts/` for visual review
before copying affected images into `docs/images`.

Cursor checks and previews (also exports the current System theme):

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build -- --cursor-ui artifacts/cursor-ui
```

Cursor summary checks and previews (standalone/mixed accounts, EN/KO, Dark/Light,
80/100/150% zoom, plus unknown/stale/authentication states, long names and a budget-first response):

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build -- --cursor-widget-summary-previews artifacts/cursor-widget-summary-after
```

`CursorWidgetSummaryChecks` uses a fixed observation time with Cursor Models 76.9%,
Other Models 41.2%, Grok Bot 12.5%, disabled on-demand and a separate team pool.
The mixed fixture places Cursor between Codex (63%) and Claude (34%) to expose vertical
alignment. The before set was exported from `012d514` with the export harness added before
production edits; the after set uses the same accounts and times. The checked-in comparison
files are those actual renders, not generated mockups. Layout DPI checks separately include
Cursor at 100/125/150/175/200% through `--widget-dpi`.

The compact follow-up uses the same fixed accounts and timestamps. Before images were
exported from `f0a9964` before production edits into `artifacts/cursor-compact-before`;
after images use `artifacts/cursor-compact-after`. The short ring labels Cursor / Other /
Grok Bot sit above the used percentage; complete names and Monthly / Weekly headings
remain in the adjacent list, with the represented row emphasized. Full ring targets remain
in tooltips and accessibility text. Standard standalone and mixed content is 192 DIP high
instead of 206, with the same 232-DIP modules and original font sizes. Large money amounts
and failure messages can use additional lines to preserve their complete information.
The earlier `cursor-widget-summary-*` comparison remains historical evidence of the first
summary change. All comparisons are actual WPF renders, with no image generation.

Native tray pixel checks and contact sheets (including `cursor-tray-icons.png`):

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build -- --tray-icons artifacts/tray-icons
```

`CursorUiChecks` uses reserved `example.invalid` identity and synthetic Cursor Models 76.9%,
Other Models 0%, Grok Bot 12.5% used values. Separate assertions cover unknown and monetary
allowances. The explicit `--cursor-live-read` command is a separate, opt-in real
Windows account check; it emits only redacted result counts and never exports images.

Update approval and layout checks (exports four screenshots to `artifacts/update-ui`):

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build -- --updates
```

Full export (36 PNGs, including additional authentication-failure previews):

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build -- --screenshots artifacts/docs-previews
```

Only the four server quota previews:

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build -- --claude-live-screenshots artifacts/claude-live-docs
```

Only the four Desktop history fallback previews:

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build -- --claude-usage-screenshots artifacts/claude-history-docs
```

All 16 Claude server, history, waiting and connection previews:

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build -- --claude-desktop-screenshots artifacts/claude-docs
```

Only the 18 detail previews affected by the shared display-period selector or the version
shown in the popup header (regenerate these after changing `Directory.Build.props`):

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build -- --usage-period-screenshots artifacts/period-docs
```

The exporter never runs production startup, requests account data, or reads/writes user settings.
View previews use WPF `RenderTargetBitmap` at 2x resolution under the smoke harness's
`OfflineApp`; tray contact sheets use the production GDI icon renderer at native sizes.
Neither takes a desktop screenshot. Each image must be visually inspected before
replacing the checked-in file. Keep English and Korean captions consistent with the account counts,
selected profile, data source and separate Claude periods.
