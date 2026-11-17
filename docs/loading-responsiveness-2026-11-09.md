# Startup layout and download-library responsiveness

## Changes

Before the settings view model arrives, unresolved visibility bindings use WPF's
default `Visible` value. The first window layout therefore measured settings,
workspace content, playback controls, and an empty update banner. Startup now
shows the title bar and a small loading message until the view model is available.
The normal workspace and settings bindings take over as soon as it arrives.
The loading message does not wait for providers or suppress their loading states.

Download-library initialization previously recalculated library totals, rebuilt
per-channel statistics, and normalized and published saved automatic-download
records for every restored card. Restoring 1,000 automatic downloads published
1,000 settings changes. The snapshot now reconciles all cards first, merges
automatic-download records by ID, and publishes the completed aggregates once.
Insertion uses a binary search with the same descending date and equal-date
behavior as before. Individual download events still update immediately.

Media validation, recovery, conversion, and directory discovery retain their
existing service path. The batch uses each card's accepted revision, so an older
snapshot cannot replace newer events or revive a deleted card. Completion history
remains cumulative after deletion, and reloading an unchanged snapshot does not
publish another automatic-download settings change.

## Measurements

These are controlled Release measurements on the same machine using the pinned
.NET SDK. Each scenario runs sequentially in a fresh test process. The first trial
is retained in the data but excluded from the warm medians. The shell fixture
constructs the actual window, lays it out before binding data, then binds six
fixture cards and visits Settings, Accounts, and Hotkeys. The library fixture
restores 1,000 completed automatic downloads across 25 channels.

| Measured work | Before median | After median |
| --- | ---: | ---: |
| Initial window-content layout before settings arrive | 291.95 ms | 11.98 ms |
| Model/window construction through populated Home layout | 585.52 ms | 304.45 ms |
| Same sequence plus first Settings, Accounts, and Hotkeys visits | 625.96 ms | 414.41 ms |
| In-memory download snapshot reconciliation | 784.15 ms | 5.69 ms |
| Allocations on the library's calling thread | 344,677,584 bytes | 3,333,176 bytes |
| Automatic-download settings publications per 1,000-video snapshot | 1,000 | 1 |

The first visits to settings pages now perform their own required layout instead
of paying for every page before settings are loaded. The entire measured
navigation sequence is faster, as shown above. Window construction itself remains
similar; this change does not remove the cost of constructing the XAML tree.

These figures exclude process launch, application-resource initialization, actual
settings reads, provider/network waits, disk/media validation, and playback.
They are not measurements of complete installed-app startup or promises about
live-provider latency. Full samples and calculation boundaries are in
[summary.json](measurements/loading-responsiveness-2026-11-09/summary.json).

## Reproduction and verification

```powershell
.\scripts\dev.ps1 Test -NoRestore -Filter 'loading responsiveness:'
.\scripts\dev.ps1 Test -NoBuild -Filter 'loading performance: shell'
.\scripts\dev.ps1 Test -NoBuild -Filter 'loading performance: downloaded'
```

The two original regressions failed before the changes: startup settings were
visible without data, and a 100-video migration published 100 settings changes
instead of one. The revised regressions also cover loading-message visibility,
the populated workspace and settings transition, unchanged reloads, descending
card order, newer revisions, and deletion while a snapshot is pending.

Before/after timing logs are in `.artifacts/loading-unbound-before.log`,
`.artifacts/loading-shell-final.log`, `.artifacts/loading-library-before.log`,
and `.artifacts/loading-library-final.log`. Offscreen startup and ready-state
images are in `.artifacts/loading-responsiveness/`.

Final checks:

- Release solution build with warnings treated as errors: zero warnings and errors.
- Formatting verification for the changed C# sources: passed.
- `git diff --check`: passed.
- Loading checks: **40 passed, zero skipped**. This includes the three new
  regressions and existing provider, cancellation, and library-validation checks.
- Workflow checks: **98 passed, zero skipped**, including stream opening,
  playback-start cancellation, paging, refresh, and resource ownership.
- Offscreen startup and populated Home images were rendered and visually checked.
- The full headless suite was attempted: **1,216 passed, 50 failed, one timed out,
  235 skipped, and 933 not run** out of 2,435 entries. The maintenance test
  `Maintenance damaged executable cleanup failures are reported without crashing`
  exceeded its existing timeout and drain period, so the runner stopped.

The full suite is not clean. Failures include Windows DPAPI protection, denied
AppData native-cache/timeline directories, persistence assertions, and UI
assertions expecting a Home VOD-download-settings shortcut absent from the
current page. A standalone `ProtectedData.Protect` call also failed independently
of application code. The full run passed the automatic-download statistics
lifecycle, migration, dark/light layouts, recovery, and offscreen shell tests.
The loading and workflow suites above were then run to completion separately.
The full log and failure headers are in `.artifacts/loading-full-suite.log` and
`.artifacts/loading-full-suite-failures.txt`; final focused results are in
`.artifacts/loading-focused-final.log` and `.artifacts/loading-workflow-final.log`.
Interactive desktop and authenticated live playback were not exercised.
