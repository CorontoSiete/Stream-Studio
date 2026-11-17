# Web seekbar times and previews — November 11, 2026

The WebUI player displays elapsed VOD time and total duration, including hours.
Before media metadata arrives it uses the server's known duration; an unknown
length displays `--:--:--`. Growing replay totals use the available archive range.
The slider also shows the played portion in the accent color.

Hovering shows the target timestamp immediately. After a 160 ms pause, a separate
muted, paused video decodes the matching frame through the player's existing
authenticated media URL. Native HLS and MP4 use native decoding; hls.js uses a
small buffer and the first available video rendition, skipping audio-only levels.
Loading stops after the frame is drawn. Leaving the slider, cancelling a drag,
stopping playback, closing, replacing, or hiding a player releases preview media.
Hover does not seek, play, change audio selection, create another server player,
or write watch progress. Keyboard focus and scrubbing also expose the card;
Escape dismisses it. Cards remain inside the video at narrow widths.

Hover targets share the same clamping as seeks, including expired live media,
gaps, and the safe live edge. Live timestamps show distance behind live. The live
player's separately resolved full broadcast archive has a different timeline.
Its hover decoder uses a separate, authenticated archive media URL owned by the
live player, so earlier history is available without opening the replay. Decode
failures keep the timestamp and show **Preview
unavailable**, without affecting main playback. No provider storyboard API or
additional dependency is required.

## Live-history preview failure and fix

The previous preview loader explicitly returned **Preview unavailable** whenever
a live player had a full archive. It never requested or decoded that archive.
The server measured its duration with a temporary `archive-probe` media object,
then disposed it without returning a usable preview URL. Ordinary VOD fixtures
did not exercise that branch.

Archive lookup now retains the validated media under the live player's existing
ID and returns its opaque URL. It pins the measured rendition, reuses the media
while checking for new history, and resolves a replacement if the provider URL
expires. Preview requests use that media's own timeline and resource inventory.
Other players and sessions cannot use its IDs. Closing the player cancels the
lookup and media requests and disposes both media objects. No playback slot,
extra chat connection, recent-stream entry, or watch-progress write is created.
If the preview URL changes, the browser releases and dismisses its old decoder.

## Verification

- `node --test scripts/tests/*.tests.cjs`: 100 tests pass, zero failed or skipped.
  New cases cover unknown/known duration, multi-hour times, hover endpoints,
  independent decoding, audio-only renditions, cancellation, native/hls.js errors,
  retries, live gaps, Twitch/Kick live-history URLs with native/HLS adapters,
  source renewal, and missing archive URLs without incorrect live-frame fallback.
- `node scripts/tests/web-navigation.browser.cjs --seek-preview-only`: passes
  in Chromium with the production HTML/CSS/JS and generated media fixtures.
  Both HLS and MP4 decode real preview frames. The HLS check verifies green
  content at 35 seconds after red preroll, rather than relying on the clock alone.
  Start, middle and end hover cards fit at 1440, 390 and 320 px; main playback and
  pause state are preserved. Keyboard seeking, Escape, closing during preview,
  and a `12:34:56 / 17:59:59` phone readout are checked. Screenshots are in
  `.artifacts/web-seek-preview/` and were visually inspected.
- The browser check also uses a live manifest containing only segments 4–5 and
  an archive containing segments 0–5. Twitch and Kick hovers decode red at six
  seconds, green at 35 seconds, and blue at the end while the original live video
  stays paused at its existing position. Open, started, and progress request
  counts remain unchanged throughout hover.
- Release app and test build: zero warnings and zero errors; embedded assets
  are included. A separate `bin/SeekPreviewVerified/` output avoids files locked
  by the running desktop app, using the existing locked dependency restore.
- Server regression checks cover retained preview manifests and segments, player
  and workspace isolation, archive growth with stable media URLs, expired-URL
  renewal, media disposal, and restoration of all playback slots on close.
  The HTTPS/WebView2 full-archive browser check also decodes red and green history
  previews before rewinding, following archive growth, and returning live on both
  platforms. Its expanded decoding sequence uses the same 90-second test budget
  as the other multi-step browser checks.
- The broader local-web run reports 54 passed and one failed. The failure is
  the existing overlay-chat fullscreen check: Windows denies fullscreen with
  **not granted**. All archive and media checks in that run pass.
- The separate existing mobile-controls browser test passes its live layout
  measurements, then fails when the browser denies fullscreen with **not
  granted**. It is not counted as a pass. The same denial was documented in the
  earlier [live seekbar verification](web-live-seekbar-2026-11-08.md).

These checks use deterministic media, not live provider sessions or physical
iOS/TV devices. The source must be built into the desktop app; restart the desktop
app and reload the WebUI to serve the updated embedded assets.
