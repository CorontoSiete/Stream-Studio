# Code review — November 9–11, 2026

This pass preserved the existing working changes. Repository-wide build, analyzer, syntax, test, duplication, and member-reference checks covered a source inventory of 589 files across the application, core, infrastructure, services, native code, hosting, shared code, and scripts. Manual inspection focused on VOD downloads, playback metadata, persistence, asynchronous cleanup, HTTP helpers, and native chat boundaries.

## Fixes

- **Automatic Kick VOD checks accepted refreshed sources before validating them.** Completion checks and downloads now share the same source validation and resolution path. An invalid refreshed source falls back to the canonical provider page without being passed to Streamlink.
- **A failed Kick metadata lookup was immediately repeated by the automatic completion check.** Shared resolution reads metadata once before trying the plugin fallback. Caller cancellation still stops resolution and does not trigger fallback.
- **Web and desktop Kick playback could use different IDs for the same VOD.** Both now use the same model-to-target conversion, including the UUID preference, title normalization, category, broadcast clock, artwork, and publication metadata. The web library migrates an existing bookmark stored under the old numeric ID when no canonical bookmark exists, preserving progress across restart. An existing canonical bookmark takes precedence.

## Reuse and cleanup

- `VodTargets` owns VOD-to-playback mapping for desktop cards, the web library, and automatic downloads. Automatic checks no longer construct UI view models just to obtain playback targets.
- `VodDownloadService.Resolution.cs` owns provider resolution for completion checks and downloads. The old duplicate implementations and an unused import were removed.
- Corrected formatting in the automatic-download tests.
- Corrected the download-settings UI test to count the toolbar action across the window instead of searching only the Home panel. It still requires exactly one action and continues to verify compact layout, keyboard accessibility, validation, folder changes, and persisted settings.
- Member-reference checks did not establish additional unused production code that could safely be deleted. Native structure fields, XAML callbacks, attached-property accessors, and test hooks remain because their consumers are outside ordinary direct method calls.

## Verification

- Solution build: zero warnings and zero errors.
- Formatting and configured analyzers: passed.
- JavaScript account-service and web-player tests: 96 passed.
- Tooling and packaging contract tests: passed.
- PowerShell syntax checks: passed; seven Python files parsed; 31 JavaScript files passed syntax checks.
- Native chat-overlay tests: passed with `-Wall -Wextra -Werror`, including network framing, reconnects, rendering, animation, caches, and resource cleanup.
- Targeted VOD and settings regression reruns: 23 passed, covering automatic-download validation and fallback, legacy watch-progress migration and persistence, desktop Kick playback compatibility, and dark/light download settings.
- Final full application suite on November 11: **2,161 passed, 1 failed, 0 timed out, 276 skipped, 0 not run**, out of 2,438 selected tests. All skipped tests require an interactive desktop. The one failure is the browser fullscreen case described below, so the full-suite command returns a nonzero exit status. Evidence is retained locally in `.tmp/review-2026-11-11-final-suite.log`.

The NuGet vulnerability-data endpoint was unavailable during the initial build. Dependencies were restored from the existing cache with locked versions and the remote audit disabled for that invocation only; repository audit settings and package versions were not changed.

## Fullscreen verification limitation

The remaining browser fullscreen failure reproduces outside the application. The overlay test reaches the real fullscreen request with an active user gesture, a focused document, and `document.fullscreenEnabled === true`, but the browser rejects it with `TypeError: not granted`. A separate headless Chrome test also rejects fullscreen, and an isolated blank-page probe returns the same error without loading any application code. Chromium reports this error when the browser rejects the fullscreen transition; see its [fullscreen request completion implementation](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/core/fullscreen/fullscreen.cc).

These observations indicate a browser or execution-environment restriction, but do not establish its exact cause. The fullscreen assertions remain enabled. Temporary diagnostic hooks and the unsuccessful focus-emulation experiment were removed. Fullscreen still needs verification in a desktop session that permits the transition.
