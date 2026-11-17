# Full broadcast history in the web seekbar

The earlier live seekbar used the current provider playlist and retained browser
buffers. Neither contains footage that expired before the viewer opened the
stream. Enlarging the slider or keeping more buffered video cannot recover it.

Live playback now automatically asks the existing replay resolver for the VOD or
DVR belonging to the current broadcast. A bounded, player-owned background task
resolves the selected media quality and reads the actual playlist. Full history
is advertised only when that playlist includes the beginning (media sequence zero)
and complete segment metadata. Its duration is the sum of segment durations;
provider duration estimates and wall-clock uptime do not establish seek bounds.
Master playlists are followed with bounded nesting. Archive playback pins the
measured rendition while preserving referenced audio/subtitle groups and keys.
Missing archives retry every
30 seconds without blocking live playback, chat or other commands. Closing the
player cancels and drains its lookup.

When full history is available, the live slider spans the verified archive and
rests at its right edge while playing live. A completed scrub opens that VOD at
the chosen archive time, including an explicit zero that overrides a bookmark.
Dragging does not repeatedly reopen the player. Rewind enters the archive thirty
seconds before its published end. LIVE reopens the original live source. No
offset between separate live and archive timelines is estimated. Pause, playback
speed, volume and quality are preserved, and quality changes/reloads retain the
archive position. Replay chat follows the VOD timeline through the existing chat
provider.

Only a verified current-broadcast archive opened in this mode is served as a
growing EVENT playlist. Snapshot VOD/ENDLIST markers are removed so native HLS and
hls.js refresh for appended segments. Ordinary VODs and offline downloads retain
their existing semantics. The Twitch subscriber fallback also exposes its
validated remote playlist to this path, applying its existing segment sanitizer
on every refresh instead of replaying a fixed local snapshot. Every manifest,
segment, key and initialization resource still uses authenticated opaque media
routes and the existing public HTTPS/DNS restrictions.

Full rewind requires an archive the provider actually makes available and the
existing Replay settings/account requirements. Missing history is not recorded
or reconstructed. In that case the original live-window controls remain usable,
and the seekbar tooltip explains lookup status. Provider archive publication may
lag direct live playback. An archive is shown as elapsed time / available archive
duration; LIVE returns to the direct source.

## Verification

- Node web tests cover both provider identities and native/hls.js adapters,
  history predating opening, explicit zero, drag cancellation, late responses,
  retry, LIVE and preserving pause/speed/volume/quality.
- Workspace and proxy tests measure playlist history rather than provider
  estimates, reject incomplete/sliding/unsafe manifests, check variant selection,
  scope growing playback, retain normal VOD behavior and exercise cancellation.
- A real HTTPS/WebView2 test uses decoded HLS video, trusted keyboard/pointer
  input and the production media proxy. The direct live source contains only
  seconds 40–60, while the archive contains seconds 0–50. The test loads and
  decodes the first archived segment, seeks to second 25, verifies growth to
  second 60 while paused, and returns to decoded direct live playback for both
  provider identities. Phone-width screenshots are in
  `.artifacts/local-web-design/full-archive-{twitch,kick}.png`.

These deterministic media/provider fixtures test the full playback integration;
they do not establish availability of a particular channel's current VOD or
physical Safari/phone compatibility. Native HLS control behavior is covered by
the deterministic native adapter.

Build and test logs: `.artifacts/web-archive-build.log`,
`.artifacts/web-archive-node-tests.log`, `.artifacts/web-archive-regressions.log`.

Final checks: the Release build has zero warnings/errors; all 88 Node web tests,
all five archive integration tests (including the decoded-video browser test),
and all eight subscriber resolver tests pass, with zero skips. The broader web
suite reports 53 passed, one failed, zero skipped. Its failure is the browser's
`not granted` response to the existing overlay fullscreen test, also documented
against the unchanged baseline in `web-live-seekbar-2026-11-08.md`. It was not
suppressed or counted as a pass.

The self-contained app is `.artifacts/web-full-live-archive/app/StreamStudio.exe`,
informational version `1.8.11+web-full-archive.20261111`. Run this executable,
start the LAN browser server and reload the browser. Packaging uses the normal
release single-file flags. Its hidden dependency check verifies Streamlink, VLC,
WebView2, Skia and HarfBuzz. Its extracted web asset is compared with the tested
source; the executable hash and comparison are recorded in
`.artifacts/web-full-live-archive/verification.json`.
