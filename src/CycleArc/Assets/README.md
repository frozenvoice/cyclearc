# Application icon

`cyclearc.svg` is the editable source: a blue-to-mint C-shaped cycle and a light
orbit marker on a navy tile. Transparent corners keep it clean on light and dark
desktop backgrounds. The usage number/ring in the notification area remains dynamic.

Regenerate `cyclearc.ico` on Windows with PowerShell 7 from the repository root:

```powershell
pwsh -NoProfile -File scripts/Build-Icon.ps1
```

The script uses WPF to render 32-bit PNG frames at 16, 20, 24, 32, 40, 48, 64, 96,
128 and 256 pixels. Commit the SVG and regenerated ICO together. Both the desktop
executable and setup project embed this icon; Velopack uses it for its installer
and installed application branding.
