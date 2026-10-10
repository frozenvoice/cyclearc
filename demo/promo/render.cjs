// Renders cyclearc-promo.html frame-by-frame, synthesizes the soundtrack, and muxes both into an MP4.
// Usage: node demo/promo/render.cjs [--preview] [--fps 60] [--width 2560] [--out demo/promo/cyclearc-promo.mp4] [--stills 1,8,12.8] [--audio-only]
//   --audio-only  reuse the last rendered picture (.capture/video.mp4) and only rebuild the audio.
// Needs demo/promo/.capture/session from the capture step (see README.md) and ffmpeg with libx264 on PATH.
const { chromium } = require('playwright');
const { spawn, spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const { renderSoundtrack } = require('./soundtrack.cjs');

const args = process.argv.slice(2);
const opt = (k, d) => { const i = args.indexOf(k); return i >= 0 ? args[i + 1] : d; };
const preview = args.includes('--preview');
const fps = Number(opt('--fps', preview ? 15 : 60));
const width = Number(opt('--width', preview ? 1280 : 2560));
const stills = opt('--stills', null);
const audioOnly = args.includes('--audio-only');
const work = path.join(__dirname, '.capture');
const previewDir = path.join(work, 'preview');
const out = path.resolve(opt('--out', path.join(preview ? previewDir : __dirname, preview ? 'cyclearc-promo-preview.mp4' : 'cyclearc-promo.mp4')));
const stillsDir = path.resolve(opt('--stills-dir', path.join(previewDir, 'stills')));
const videoOnly = path.join(work, 'video.mp4');
const wav = path.join(work, 'soundtrack.wav');

(async () => {
  if (!Number.isFinite(fps) || fps <= 0 || !Number.isInteger(width) || width <= 0 || width % 2 !== 0) {
    throw new Error('fps must be positive; width must be a positive even integer');
  }
  if (!fs.existsSync(path.join(work, 'session', 'clip.js'))) {
    throw new Error('Missing .capture/session — run the capture first (see README.md)');
  }
  fs.mkdirSync(path.dirname(out), { recursive: true });
  // Fall back to installed Chrome/Edge when Playwright's bundled Chromium isn't downloaded.
  const browser = await chromium.launch().catch(() => chromium.launch({ channel: 'chrome' })).catch(() => chromium.launch({ channel: 'msedge' }));
  // The composition is laid out at 1920×1080; larger outputs render at a higher device scale instead of upscaling.
  const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: Math.max(1, width / 1920) });
  await page.goto(require('url').pathToFileURL(path.join(__dirname, 'cyclearc-promo.html')).href);
  await page.evaluate(() => document.fonts.ready);

  if (stills) {
    fs.mkdirSync(stillsDir, { recursive: true });
    for (const t of stills.split(',').map(Number)) {
      await page.evaluate((t) => window.seek(t), t);
      await page.screenshot({ path: path.join(stillsDir, `still-${t}.png`) });
    }
    await browser.close();
    return;
  }

  const timeline = await page.evaluate(() => window.TIMELINE);
  if (!timeline) {
    const missing = await page.evaluate(() => window.CLIP ? MISSING : null);
    throw new Error(missing ? `Capture lacks marks: ${missing.join(', ')} — record a new session (see README.md)` : 'Missing .capture/session — run the capture first (see README.md)');
  }
  fs.writeFileSync(path.join(work, 'timeline.json'), JSON.stringify(timeline, null, 2));

  if (!audioOnly) {
    const total = Math.round(timeline.duration * fps);
    const ff = spawn('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'image2pipe', '-framerate', String(fps), '-i', '-',
      '-vf', `scale=${width}:-2:flags=lanczos`, '-c:v', 'libx264', '-preset', preview ? 'fast' : 'slow', '-crf', preview ? '22' : '18', '-pix_fmt', 'yuv420p', videoOnly],
      { stdio: ['pipe', 'inherit', 'inherit'] });
    for (let f = 0; f < total; f++) {
      await page.evaluate((t) => window.seek(t), f / fps);
      const buf = await page.screenshot({ type: 'png' });
      if (!ff.stdin.write(buf)) await new Promise((r) => ff.stdin.once('drain', r));
      if (f % (fps * 4) === 0) process.stdout.write(`frame ${f}/${total}\n`);
    }
    ff.stdin.end();
    await new Promise((resolve, reject) => {
      ff.on('error', reject);
      ff.on('close', (code) => code === 0 ? resolve() : reject(new Error(`ffmpeg render failed (${code})`)));
    });
  }
  await browser.close();

  renderSoundtrack(timeline, wav);
  // Normalize to streaming loudness (-14 LUFS, -1 dBTP) and mux.
  const mux = spawnSync('ffmpeg', ['-y', '-loglevel', 'error', '-i', videoOnly, '-i', wav,
    '-af', 'highpass=f=25,loudnorm=I=-14:TP=-1.0:LRA=11', '-ar', '48000',
    '-c:v', 'copy', '-c:a', 'aac', '-b:a', '192k', '-shortest', '-movflags', '+faststart', out], { stdio: 'inherit' });
  if (mux.status !== 0) throw new Error('ffmpeg mux failed');
  console.log('wrote', out);
  if (!preview) {
    // Poster for web embeds: the settled end card.
    const poster = out.replace(/\.mp4$/i, '') + '-poster.jpg';
    const still = spawnSync('ffmpeg', ['-y', '-loglevel', 'error', '-ss', String(timeline.end + 1.2), '-i', out,
      '-frames:v', '1', '-vf', 'scale=1920:-2:flags=lanczos', '-q:v', '3', poster], { stdio: 'inherit' });
    if (still.status !== 0) throw new Error('ffmpeg poster failed');
    console.log('wrote', poster);
  }
})();
