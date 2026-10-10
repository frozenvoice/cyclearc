# CycleArc promo video

A 20-second highlight cut: 2560×1440, 60fps, H.264 + AAC stereo (−14 LUFS target).

- `cyclearc-promo.mp4` / `cyclearc-promo-poster.jpg` are written by `render.cjs` (git-ignored here; the published copies live in `frozenvoice/frozenvoice-assets` under `cyclearc/video/`).
- `cyclearc-promo.html` is the composition, cut on a 120 BPM grid: intro (0–2s), four footage beats (2–16s: open the popup from the tray and refresh, switch between Codex/Claude/Cursor accounts, refresh the desktop widget, the usage colors), manifesto (16–18s), end card (18–20s). Open it with `?play` to preview in a browser, or `?t=7.2` for one moment (needs a capture).
- `capture/` is a small WPF host (`CycleArc.PromoCapture`) that loads the production `FlyoutWindow` and `FloatingWidget` with startup overridden, binds four fixed sample accounts and records timed PNG frames with `RenderTargetBitmap`, plus click marks, element boxes and production tray icon renders. It never reads accounts, settings, caches or credentials, registers a tray icon or starts a refresh. It is not part of `CycleArc.sln`, the test suites or any release.
- `render.cjs` renders the HTML frame by frame (Playwright + ffmpeg) and muxes the soundtrack; `--preview` writes a 1280×720 / 15fps check to `.capture/preview/`, `--stills 2.4,7.2` writes PNGs.
- `soundtrack.cjs` synthesizes the music and UI sounds in code from the composition's cue sheet. There are no third-party audio assets.

## Rebuild

```powershell
cd demo/promo
npm install
npm run capture
npm run render
```

The capture takes about 20 seconds and writes roughly 60 MB to `.capture/session/` (git-ignored). The render takes a few minutes and needs `ffmpeg` with libx264 on PATH. Playwright falls back to installed Chrome or Edge when its bundled Chromium is not downloaded.

## What is real, what is added

- Every CycleArc window in the video is the production WPF view from this source (0.10.1), rendered as it runs: the refresh spinner, the header status, account selection through the real row buttons, the widget refresh and the ring colors. Selection, refresh and rebinding follow the app controller's path; the values are fixed samples.
- The accounts, percentages, reset times and credits are synthetic. They illustrate the layout and do not describe any plan's entitlements.
- Added in post: the desktop backdrop and taskbar strip, the popup and widget open/close motion, the pointer drawn at the recorded click positions, captions, camera moves, the enlarged tray icon card, light sweeps and the soundtrack. Waits between actions are shortened; nothing is reordered.

Claims stay within the README: separate limits per account, unknown values stay unknown, ring colors by actual usage (amber from 70%, orange from 85%), and no forecasts.
