# Code review — November 12, 2026

This pass preserved the existing uncommitted work. The inventory covered 931 source, script, and test files across the desktop application, Core, Infrastructure, account service, installer, maintenance application, native overlay, hosting, and shared tooling. Repository-wide checks were combined with manual inspection of request handling, cancellation, caches, VOD downloads, history, playlist processing, and web rendering. Bundled third-party code was treated as a dependency. This is a review pass, not a guarantee that every possible defect has been found.

## Fixed behavior

- **Delayed VOD completion lost watched status.** History previously discarded an older sample before considering its completion information. It now retains the newest position, duration, timestamp, and current-pass completion flag while carrying watched status forward from either sample. A shared merge helper handles both live updates and disk-history merging. The regression checks notifications, stale-position suppression, card status, and persistence across reload.
- **Failed web artwork never recovered on an unchanged refresh.** Card reuse also retained the failed image state indefinitely. Refresh now retries only failed artwork through the same rendering callback used to create it. Controls, focus, media badges, pending images, and healthy decoded images remain attached. The new regression failed against the previous renderer and passes with the fix; the existing browser artwork test exercises recovery through the real HTTPS endpoint.

## Reuse and cleanup

- Removed three redundant special cases from grid layout calculation. Six-, eight-, and twelve-stream layouts use the existing calculation; the distinct ten-stream layout remains explicit.
- Removed the unused per-segment start index from replay timeline metadata. Segment pairing now uses a boolean instead of retaining an unused line number.
- Removed the web renderer's immediately replaced initial placeholder and its unused pending-placeholder branch.
- Updated the shared EOF persistence assertion to observe the queued disk save. Cached history reads deliberately return without waiting for disk work, so using a cached read as a write barrier caused three existing tests to race. The assertion still verifies immediate completion in memory and a persisted completion without triggering another save or closing the tab.
- Corrected anonymous-object formatting in the live-archive browser fixture.
- Reference checks found no further production members that could safely be removed. XAML callbacks, native interface fields, and test hooks retain real consumers.

## Verification

- Final Debug solution build with warnings treated as errors: **zero warnings, zero errors**.
- Solution-wide formatting and configured analyzer verification: passed. A final whitespace check of every changed C# file also passed.
- Final VOD regression run: **460 passed, 20 skipped**. This covers watch history, EOF persistence, downloads, replay timelines, and VOD UI behavior. The delayed-completion regression and all three previously failing EOF assertions passed.
- Final multi-stream regression run: **15 passed, 12 skipped**, including the requested six-, eight-, ten-, and twelve-stream layouts.
- Final artwork regression run: **5 passed, none skipped**, including recovery through the real browser and HTTPS server.
- JavaScript account-service and web-player tests: **139 passed**. The new failed-artwork regression was also run before the fix and failed as expected.
- Python tooling tests: **8 passed**. Seven Python sources parsed successfully.
- PowerShell syntax and tooling/packaging contract tests: passed. All 37 authored JavaScript files passed syntax checks.
- Native overlay tests: passed with `-Wall -Wextra -Werror`, including protocol framing, reconnects, rendering, animation, caches, and resource cleanup. Bundled native dependency verification passed.
- Whitespace error checks passed. Comparing source hashes against the initial inventory confirmed that existing work outside the eight intentionally edited source/test files remained unchanged.

The complete application run during this review selected **2,463 tests: 2,182 passed, 5 failed, 276 skipped, none timed out or left unrun**. That run preceded the final artwork fix, layout/timeline cleanup, and EOF test synchronization changes. Four failures were addressed and passed in the final focused runs above; the complete suite was not repeated after those last edits. The 276 skips require an interactive desktop.

The remaining failed check is browser fullscreen: the browser rejects the transition with `not granted`, and the fullscreen assertion stays enabled. The same rejection was previously reproduced outside the application; see the [earlier fullscreen investigation](code-review-2026-11-09.md#fullscreen-verification-limitation). Fullscreen still needs verification in a desktop environment that permits it.

Local evidence is retained in `.tmp/review-2026-11-12/`: `full-tests.log`, `final-build.log`, `vod-final.log`, `grid-final.log`, `artwork-final.log`, `javascript-final.log`, `format-final.log`, `format-touched.log`, `tooling.log`, `python-tests.log`, and `native-tests.log`. The initial restricted run could not access Windows credential protection, certificates, and some application caches, so application validation was repeated with normal Windows access.
