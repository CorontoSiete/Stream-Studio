# Web seek-preview startup — November 11, 2026

Reported environment: Brave on Windows 10, Twitch live streams. The first hover
sometimes shows black; moving to another timestamp sometimes recovers it.

## Diagnosis and evidence

The preview loader treated a successful `drawImage` call as a completed capture.
It exposed the canvas, cleared its eight-second timeout, and stopped HLS loading
without establishing that any video pixels had been copied. A new canvas is
transparent and its CSS background is black, so an empty copy becomes a black
preview. Moving to another timestamp repeats this same capture path.

Event probes with real HLS and MP4 fixtures confirmed that media readiness and
seek events can run before the first frame-presentation callback. Their normal
captures produced pixels; the spontaneous black live-provider failure was not
reproduced on demand. The application defect was reproduced deterministically
by withholding the canvas copy after the real decoder became ready. This is
fault injection, not evidence that a particular GPU or Brave version is faulty.

The regression runs the production HTML, CSS, JavaScript, and bundled hls.js in
Brave. Against the saved original application it fails because an empty first
capture is exposed. With the fix, loading remains visible, then the correct
scene appears without another pointer event. A second blocked capture verifies
that a previous successful image cannot be mistaken for the new timestamp.

## Change

- Register a video-frame callback before loading a new preview decoder and wait
  for first presentation on browsers supporting that API.
- Clear the previous canvas image before copying, then check for any nonzero
  alpha. An opaque black scene remains valid; RGB brightness is never a filter.
- Retry an empty copy on frame presentation or a bounded 50 ms polling interval.
  The existing eight-second failure deadline remains active until actual success.
- Cancel retries and frame callbacks on timestamp changes and media release.
  Obsolete callbacks cannot clear a replacement request.
- Preserve a verified capture against subsequent readiness events. Main playback,
  audio, archive resolution, and progress reporting use their existing paths.

## Verification

- `node --test scripts/tests/*.tests.cjs`: 107 passed, zero failed or skipped.
  Seven new startup tests cover delayed pixels with native/HLS adapters, genuine
  black scenes, presentation ordering, dismissal, failure timeout, and a stale
  callback after moving to another timestamp.
- Brave real-browser regressions pass with graphics enabled and disabled;
  Chrome passes with graphics enabled. Both decode HLS/MP4 and Twitch/Kick history fixtures, including known
  scene colors, first/second delayed captures, desktop/phone bounds, keyboard
  interaction, and unchanged main playback. To repeat in Brave:

  ```powershell
  $env:SVS_WEB_TEST_BROWSER = 'C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe'
  node scripts/tests/web-navigation.browser.cjs --seek-preview-only --enable-gpu
  ```

- The solution builds in Release with zero warnings or errors. Verification
  output uses `bin/SeekPreviewStartup/` to avoid files locked by the running app.
- The rebuilt application's HTTPS/WebView2 full-history regression passes for
  Twitch and Kick, including decoded previews, rewinding, growth, and returning
  live. This uses deterministic media rather than a live provider session.

Logs are under `.artifacts/seek-preview-first-hover-*`; browser screenshots are
under `.artifacts/web-seek-preview/`. The fix must be served by the rebuilt desktop
app, followed by a WebUI reload; an already running older binary is unchanged.
