# Code review — November 16, 2026

This pass preserved the existing uncommitted work. The inventory covered 939 source, project, UI, script, and test files across the desktop application, Core, Infrastructure, account service, installer, maintenance application, native integrations, shared code, and hosting. Repository-wide build, analyzer, syntax, reference, and test checks were combined with manual inspection of playback, downloads, history, asynchronous cleanup, provider requests, HLS processing, and browser state. Bundled third-party sources were treated as dependencies. This is a review pass, not a guarantee that every possible defect has been found.

## Fixes

- **Seek previews reused an earlier frame's readiness.** Moving an already rendered preview to a new timestamp could publish stale pixels with the new timestamp. Each actual decoder seek now clears readiness and requests a fresh frame callback after changing the media clock. A callback that arrives before `seeked` is retained until the media is ready, which also supports paused WebView2 decoders. Existing cancellation checks continue to reject callbacks from retired captures. Native HLS and hls.js regressions both failed before the fix and pass afterward.
- **Live used archive coordinates when full history was available.** The Live action now uses the underlying live playlist and buffer ranges, clears a pending archive seek, and updates the history slider. It preserves pause state and does not open a replay when the slider later loses focus. Both native HLS and hls.js regressions failed before the fix.
- **Cancellation could admit queued Twitch VOD access requests.** A semaphore release could win the race with its queued waiter's cancellation notification. Access batches now use the existing `AsyncOperationGate`, which checks caller cancellation after taking a slot and releases leases reliably.
- **The same cancellation race affected Kick artwork/history requests.** Both kinds of lookup now share that cancellation-aware gate while retaining the four-request limit and draining active work. The initial complete suite exposed both provider cancellation failures. The strengthened Twitch regression also exercises active requests completing before queued cancellation callbacks run.

## Reuse and removal

- Moved published HLS duration parsing into `HlsPlaylistPolicy.ReadPublishedDuration`. Desktop replay and the web archive now share segment pairing, zero-based sequence checks, duration precision, delta rejection, header validation, and the supported fourteen-day limit. Removed both previous parsing loops and the web parser's unused globalization import. Archive tests cover the exact duration limit, over-limit input, and malformed headers.
- Removed the duplicate single-item automatic-download tracker. Individual updates and library loads now use the existing batch tracker.
- Reused the operation gate for both provider fixes instead of adding independent admission/cancellation implementations.
- Reference checks found no additional production methods or types that could safely be deleted. WPF attached-property setters, framework overrides, extension-method containers, native fields, and dynamically used test hooks retain consumers.

## Validation

- Final Debug and Release solution builds, plus the standalone Release packaging-probe build: zero warnings and zero errors.
- Solution-wide formatting/analyzer verification passed initially; final verification of all changed C# files also passed.
- All 139 JavaScript account-service and web-player tests passed. Both preview-reuse and Live-action regressions were reproduced before their fixes.
- The real Chrome preview integration passed for HLS, MP4, and Twitch/Kick full archive history, including decoded scene colors, paused playback, keyboard input, and desktop/phone layouts. The final application run also passed the real WebView2 full-archive browser test.
- All eight workflow-loading regressions passed in both Debug and Release, including concurrent batches, partial provider failures, both cancellation paths, and the forced admission race.
- All eight Python tooling tests passed. Python script compilation and syntax checks for all 38 authored JavaScript sources passed.
- PowerShell syntax, tooling/installer contracts, and pinned native dependency verification passed.
- Native overlay TLS-handshake, network, readability, and render-resource tests passed with `-Wall -Wextra -Werror`, including reconnects, framing, animation, rendering caches, and GDI resource cleanup.
- Git whitespace checks passed.

The final complete Debug application run selected **2,464 tests: 2,187 passed, 1 failed, 276 skipped, none timed out or left unrun**. The skips require an interactive desktop. All fixes and the new cancellation regression passed in this complete run.

The remaining failure is the existing browser fullscreen check: WebView2 rejects the transition with `not granted`. It matches the previously investigated denial, which was also reproduced outside the application; see the [earlier fullscreen investigation](code-review-2026-11-09.md#fullscreen-verification-limitation). The fullscreen assertion remains enabled, and this behavior still needs verification in a desktop environment that permits the transition. Consequently, the complete-suite command returns a nonzero exit status.

Local evidence is retained in `.tmp/review-2026-11-16-*`, including `final-tests.log`, `release-cancellation.log`, `javascript-final.log`, `preview-browser-final.log`, `format-final.log`, `probe-build.log`, and `native.log`. The initial `check.log` includes the repository-wide formatting, PowerShell/tooling, dependency, build, and earlier complete-suite results; the final complete run above supersedes its application-test results.
