# Documentation previews

These PNGs render the actual production WPF views with synthetic profiles and quota metadata.
They show the CycleArc product name, Codex/Claude provider labels and current connection controls.
They are not captures of a user's account or fabricated UI mockups. The sample percentages,
reset times and credits illustrate the layout; they do not promise specific plan entitlements.

| Files | Contents |
| --- | --- |
| `updates-{en,ko}-{dark,light}.png` | Production update window with synthetic 0.6.1 release notes; download and restart require separate approval |
| `overview-dark.png`, `overview-light.png`, `settings.png`, `widget.png` | English single-account, settings and widget previews |
| `accounts-overview-{en,ko}-{dark,light}.png` | Two ready Codex accounts, with Work / 업무용 selected; the unconnected Claude profile is absent from the cards and counts |
| `accounts-manage-{en,ko}-{dark,light}.png` | All three registered profiles, including unconnected Research / 실험용 Claude, with connection, nickname and saved-order controls |
| `claude-waiting-{en,ko}-{dark,light}.png` | Connected Research before its first sample, with unknown shared subscription limits, Awaiting usage and Open usage page |
| `claude-live-{en,ko}-{dark,light}.png` | Research selected with a synthetic server response: 46% five-hour / 11% weekly, known resets, Updated and Last checked |
| `claude-overview-{en,ko}-{dark,light}.png` | Research selected with a Desktop history fallback: 91% five-hour / 47% weekly, unknown resets, Received and original observation time |
| `claude-connection-{en,ko}-{dark,light}.png` | Official CLI connection choices, existing Desktop login for server checks and manual usage-page access; advanced settings collapsed |

The multi-account fixtures live in `DocumentationScreenshots.SampleAccounts`. They use the
names Personal / Work / Research (개인용 / 업무용 / 실험용), reserved `example.invalid` email
addresses and display-only paths under `C:\CycleArc-Samples`. Personal and Work have Codex
weekly usage of 18% and 64%; Work is selected in the account overview, so its detail ring shows
64% used and its quota row includes 36% remaining. Research is unconnected in the main/manager
comparison: the main popup counts two ready accounts, while management retains all three profiles.
Account-management previews scroll to the bottom so all three sets of actions are visible.

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
starts a browser, creates a home, opens a session or changes settings. All dates are generated
relative to export time.

Generate on Windows after building the solution. Export to `artifacts/` for visual review
before copying affected images into `docs/images`.

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

Only the 18 detail previews affected by the shared display-period selector:

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release --no-build -- --usage-period-screenshots artifacts/period-docs
```

The exporter never runs production startup, requests account data, or reads/writes user settings.
All documentation previews use WPF `RenderTargetBitmap` at 2x resolution under the smoke harness's
`OfflineApp`, without taking a desktop screenshot. Each image must be visually inspected before
replacing the checked-in file. Keep English and Korean captions consistent with the account counts,
selected profile, data source and separate Claude periods.
