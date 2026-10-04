# .NET 10 LTS migration

CycleArc 0.10.0 moves the desktop, Core, retained companion test target, unit tests,
WPF smoke runner and Native AOT Setup from .NET 8 to .NET 10. This is a source and
packaging change; publication still follows the existing release approval procedure.

## Runtime and build dependencies

| Component | Migration decision |
| --- | --- |
| WPF desktop and UiSmoke | `net10.0-windows10.0.17763.0`; WindowsDesktop supplies WPF, WinForms tray support and System.Drawing. Remove the redundant System.Drawing.Common 8.0.11 reference. |
| Core, tests, retained companion host | `net10.0`; no storage-schema migration. The companion remains excluded from distributed app output. |
| SQLite | Microsoft.Data.Sqlite 8.0.11 → 10.0.12. Retain SQLitePCLRaw.bundle_e_sqlite3 2.1.13 and the existing native SQLite security floor. Cursor's exact-row, read-only credential query is unchanged. |
| Setup | `net10.0-windows`, `win-x64`, Native AOT; the embedded Velopack engine still owns installation. |
| Velopack / vpk | Keep the matched 1.2.0 library/tool versions. vpk 1.2.0 includes a `net10.0` tool asset, selected by .NET 10 tool restore; no tool major-version migration is needed. |
| SDK | `global.json` selects stable 10.0, minimum 10.0.100, with `latestFeature` roll-forward within 10.0 and previews disabled. CI installs 10.0.x. Keep SDK/runtime servicing current and record the selected SDK in verification output. |
| Native toolchain | Retain the tested VS2022 C++ toolchain contract and windows-2022 source-build image. A Windows SDK with x64 import libraries is required. Native toolchain version and the application's Windows API minimum are different settings. |
| WebView2 | No active WebView2 NuGet dependency or shipped browser profile consumer. `WebView/**` and desktop `Companion/**` are excluded by the app project. This migration does not reactivate them or claim live WebView2 compatibility. |

The [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
lists .NET 8 support ending **2026-11-10** and .NET 10 LTS ending **2028-11-14**.
Self-contained application updates carry runtime servicing; installing a new machine-wide
runtime alone does not update an already shipped CycleArc executable.

## Operating systems

The Windows API target remains **10.0.17763.0 (Windows 10 1809)** and the shipped
architecture remains **x64**. No higher Windows API floor was introduced by this change.
The previous README's unqualified “Windows 10/11” was broader than Microsoft's support
matrix: use an in-support Windows 11 version or supported Windows 10 LTSC edition at
or above that API floor. At review time the relevant Windows 10 entries include
Enterprise 1809 and Enterprise/IoT Enterprise 21H2. Windows 10 1607 in the .NET matrix
is below CycleArc's existing API floor. Ordinary Windows 10 editions are not covered
by the current .NET 10 support list. This is an explicit support qualification, not
evidence that every older Windows installation fails to start.
The current [.NET 8 Windows support matrix](https://github.com/dotnet/core/blob/main/release-notes/8.0/supported-os.md)
lists the same Windows versions as .NET 10 at this review date: this migration does
not remove an additional currently supported Windows version.

See the official [.NET 10 supported OS matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
and each Windows edition's lifecycle. Passing Windows 11 or Windows Server CI does
not establish Windows 10 LTSC, every Windows 11 release, ARM emulation or Server desktop
compatibility. CycleArc does not add those as newly tested product targets here.

## Compatibility review

Both the [.NET 9](https://learn.microsoft.com/en-us/dotnet/core/compatibility/9.0)
and [.NET 10](https://learn.microsoft.com/en-us/dotnet/core/compatibility/10.0)
breaking-change catalogs apply when moving from .NET 8.

- WPF 10 rejects empty Grid definition shorthand and invalid `DynamicResource`
  resource types. Production XAML compilation and EN/KO theme/layout UiSmoke exercise
  the actual dictionaries and windows. The app does not use WPF's changed
  `GetXmlNamespaceMaps` API. Keep WPF/WinForms menu types unambiguous.
  Preserve the WPF DPI manifest: the existing intentional WinForms warning suppression
  is updated from WFAC010 to [WFO0003](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/compiler-messages/wfo0003).
  WinForms' suggested `ApplicationConfiguration.Initialize` is not CycleArc's startup path.
- .NET 10 changes [single-file native-library probing](https://learn.microsoft.com/en-us/dotnet/core/compatibility/interop/10.0/native-library-search).
  Keep `IncludeNativeLibrariesForSelfExtract=true`. The published test-flavour smoke
  opens a synthetic Cursor SQLite fixture through the real reader in a bundled
  executable, independently of unbundled unit tests. It must not read a real login.
  No global DLL search-path workaround or weaker security flag is needed.
- System.Text.Json has stricter member-conflict checks. Existing settings, account,
  quota, binding and failure persistence tests cover reading, round trips, backups
  and malformed documents. Serialization options and model versions are unchanged.
  CycleArc does not use BinaryFormatter (which .NET 9 removed).
- The application stays self-contained, untrimmed WPF. Only the small Win32 Setup
  wrapper uses AOT. Microsoft's [Native AOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
  support VS2022 or later with the default Desktop development with C++ components.
- Velopack's [WPF integration and self-contained packaging guidance](https://docs.velopack.io/getting-started/csharp)
  matches the existing early `Main` bootstrap and single-file publish. Keep the
  bounded headless Claude callback routes before WPF and Velopack initialization.

## Installation, state and security

New managed installations still use `%LOCALAPPDATA%\Programs\CycleArc`; existing
installations retain the uninstall registry's resolved `InstallLocation`, including
the former `%LOCALAPPDATA%\CycleArc`. `Get-ManagedInstallRoot` remains authoritative.
`current\CycleArc.exe`, the stable root launcher and the separate
`%LOCALAPPDATA%\Programs\CycleArc-dev` layout are unchanged.

Persisted application state stays under **`%LOCALAPPDATA%\ProMeter`**:

- `settings.json` keeps schema version 2 and its valid backup/atomic-write behavior.
- `codex-accounts.json` retains registry version 3 for Cursor support, selected IDs,
  nicknames, order, provider bindings and profile paths; existing compatible registry
  versions remain readable.
- Per-account `quota.json`, identity bindings and Claude connection/status/failure
  records retain their formats. Legacy `codex-snapshot.json`, `prometer.db`,
  `webview` and companion-pairing files are not moved or deleted.
- `logs` stays in the same root. There is no separate `AppState` class or file in
  this revision; application state is represented by these settings and stores.

Authentication remains owned by the existing provider applications. There is no
credential migration, new sign-in, token renewal or changed encryption setting.
The existing bounded Desktop OAuth read and Cursor exact-token-row read stay in
memory; identity checks, redirect rejection and secret-redaction rules remain.

Legacy companion pipes retain `PipeOptions.CurrentUserOnly`. Desktop IPC already
uses a Windows ACL restricted to the current user SID and validates pipe ownership
to handle elevated token-owner behavior; it must not be mechanically replaced with
`CurrentUserOnly`. Origin allowlists, native messaging frame bounds and pairing-token
checks remain intact in retained code and regression tests.

Update verification still checks SHA-256, advertised size, package identity/version,
safe paths and reparse points before apply, with independent verification of the
recovery copy. No verification was relaxed to accommodate .NET 10. Setup/update/removal
must be exercised only in a disposable Windows VM or throwaway user because their
registry, data root, shortcuts, mutex and desktop IPC cannot be isolated by a temp path.

## Code signing

No approved CycleArc certificate, signing identity or signing procedure is configured
in the tracked pipeline. Artifacts therefore remain unsigned. This migration does
not buy certificates, self-sign, export keys or change any trust store.

When an approved signing identity and runner access are supplied:

1. Wire the approved signer into `scripts/Package.ps1`'s `Invoke-VpkPack` using
   Velopack's supported signing options so application/Update/engine binaries are
   signed at the required packaging stages. See [Velopack signing](https://docs.velopack.io/packaging/signing).
2. Sign the final AOT wrapper after `New-SetupUiInstaller` replaces the engine under
   `CycleArc-Setup.exe`, **before** `Assert-PackageOutput` writes `SHA256SUMS.txt`.
3. Validate the approved signer identity, chain and timestamp on all required final
   PE assets. Recompute/check feed size and hashes and the uploaded manifest only
   after all signing is complete. Never mutate an asset after final hash verification.
4. Keep keys/credentials in the approved secret or hardware/service boundary and
   follow the existing release approval and `Release.ps1` preflight process. Signing
   alone does not guarantee a particular SmartScreen outcome.

## Verification evidence

See [Validation](VALIDATION.md) for exact executed commands, results, tool versions,
CI commit/run links and remaining platform conditions. The shared local gate is:

```powershell
pwsh -NoProfile -File ./dev-run.ps1 -NoLaunch
```

The disposable installed E2E can build a pinned prior .NET 8 checkout as its baseline,
then install, reject a failed .NET 10 start and restore .NET 8, update successfully
to .NET 10, verify subsequent failed-start recovery and remove the test installation.
The historical app is packaged with the current .NET 10 Setup wrapper. Such builds
contain the existing synthetic E2E feed hooks: they are
source-baseline compatibility evidence, not a claim that the public release binary
was modified or that a public release was published.
