# Live web seekbar repair — November 8, 2026

The web player explicitly hid `.video-seek` for every live stream. Its input
handler also required a finite media duration. Native live HLS commonly reports
an infinite duration, and hls.js's default finite MediaSource duration can include
expired segments. Simply showing the existing VOD slider would therefore leave
seeking broken or offer unavailable video.

The seekbar now uses the current hls.js playlist window and retained browser
buffers, or `video.seekable` for native HLS. Expired media and gaps between
available ranges are excluded from seek targets. A drag keeps the displayed
range steady while clamping its target to currently available media. Playlist,
buffer and media events refresh the range, including while paused. The 30-second
skip controls use the same bounds, and LIVE uses hls.js's synchronized position
or the existing native-HLS fallback near the seekable end. Finite VODs keep
their full duration and end-of-video behavior.

hls.js now reports an infinite live duration and updates its live seekable range;
VOD duration behavior is preserved. Playback settings, stream resolution, the
existing 90-second back-buffer limit and server media authorization are retained.
Earlier broadcast history remains subject to the existing Replay resolver and
provider availability. This repair does not record broadcasts on the server.

Browser verification also exposed a click failure caused by replacing a button's
SVG during frequent media updates. Play and mute icons now change only when
their displayed state changes, keeping pressed targets in place.

## Verification

Evidence is in `.artifacts/web-live-seek/` and `.artifacts/web-seek-*.log`.

- All eight initial regression cases failed against the unchanged player. The
  final nine seek regressions and all **80 Node web tests pass**, with no skips.
  Cases include Twitch/Kick, native/hls.js adapters, infinite and missing
  durations, expired media, buffer gaps, moving windows, dragging, stopped
  players, VOD seeking and stable button contents.
- The Release solution builds with **zero warnings and zero errors**, after a
  locked dependency restore. No dependency versions were changed.
- A real HTTPS/WebView2 regression passes using decoded HLS video for both
  provider identities. Trusted pointer and keyboard input exercise scrubbing,
  rewind, forward, LIVE, playlist advancement, old-buffer removal and playback
  after seeking. Phone-width screenshots were inspected.
- The full web suite reports **47 passed, one failed, zero skipped**. The overlay
  fullscreen test receives the browser's `not granted` denial. It also fails at
  the same assertion using the unchanged release's embedded web player in an
  isolated test host. Embedded asset hashes confirm the baseline and repaired
  hosts contain their respective sources. This pre-existing fullscreen test
  failure was not suppressed or counted as a pass.
- Actual live Twitch **KaiCenat** and Kick **SXB** media were resolved after
  confirming both broadcasts in their live directories. The production embedded
  web assets and authenticated HTTPS media proxy decoded **1920×1080** video.
  Trusted slider input rewound Twitch from **10.04 to 3.83 seconds** and Kick
  from **10.05 to 2.33 seconds**; both resumed and returned to live. There were
  no fatal playback errors. Kick continued playing through multiple playlist
  updates; the later sample recorded 4,784 frames with four dropped frames.
  Rapid dragging produced nonfatal aborted requests as superseded seeks were
  cancelled. Exact observations are in `live-verification.json` and
  `kick-sustained-final.json`.
- The standalone repair uses the normal self-contained release packaging flags.
  Its dependency verification exits zero for Streamlink, VLC, WebView2, Skia and
  HarfBuzz. The extracted web asset's SHA-256 matches the validated source.
  The live-seek browser regression also passes against libraries extracted from
  that exact executable (`exact-candidate-browser.log`).

The live probe uses temporary test accounts and certificates and real provider
media; it does not establish provider-chat behavior or physical iOS/TV device
compatibility. Native HLS range logic is covered by deterministic adapters.

## Installed repair

The repair is based on installed release **1.8.11**, commit
`c43e58e4385f2db2a855cfb3798c234c16e1ac58`. Its informational version is
`1.8.11+web-live-seek-fix.20261108`; its release/update version remains 1.8.11.
The executable SHA-256 is
`E2ACF7BDF650435CE8D96D1876CA7F3475411D644EC8154BE1FFD1CBED3DD0B1`.

The installation script verifies the release, validated candidate hash, original
executable hash and ownership record. It requires the application to be closed,
backs up the executable and ownership files, replaces the executable atomically
and refreshes ownership. Any replacement failure restores the backup. Installation
and final verification are recorded in `installation-result.json` and the
installed health-check logs; the backup is in `installed-backup/`.

Installation completed. The installed executable matches the candidate hash and
informational version, and its dependency verification exits zero
(`installed-verification.json`).

Open Stream Studio and start the LAN browser server, then reload the browser page
to load the repaired embedded assets.
