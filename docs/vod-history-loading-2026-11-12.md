# VOD history loading

Loading another page of past broadcasts now restores watch progress only for
newly appended cards. Existing cards continue receiving bookmark notifications,
overlapping provider pages keep their existing cards, and explicit refresh still
reloads progress for all cards on the refreshed page. This applies to both Twitch
and Kick. If a history read fails, the next page retries earlier cards as well,
preserving recovery without requiring a manual refresh.

Previously each page reread every displayed bookmark. For ten non-overlapping
pages of 100 videos, that requires 5,500 history lookups. The revised path requires
1,000. This is a count of work removed, not a measured reduction in network or
playback loading time.

After history has been initialized, bookmark reads use the current in-memory
snapshot without waiting for the disk-operation gate. Background saves can still
serialize and flush their snapshots, while resume and progress lookups see the
latest accepted bookmark. Initial history loading remains serialized, including
its existing merge, damaged-file backup, and retry behavior. Cancellation is
checked before returning a cached result.

## Regression checks

Before the changes, both pagination regressions read `1, 2, 3, 4` when only `4`
was appended. The loaded-history regression could not return a remembered resume
position while the file gate was held and reached its two-second cancellation
deadline. These three checks failed against the previous implementation.

The added checks cover overlapping pages, retained card identity, progress on
new cards, bookmark events on previous cards, explicit refresh, missing cached
bookmarks, persistence after the file gate is released, and cancellation of both
initial and cached reads.

Final verification:

- Release solution build with warnings treated as errors: zero warnings/errors.
- Watch-progress, resume, refresh, and pagination checks: 50 passed, zero skipped.
- Solution-based formatting/analyzer verification passed. A final whitespace
  verification of the changed C# files and `git diff --check` also passed.
- Recovery checks for both providers failed with the initial pagination
  optimization and pass with the retry fallback. Their failing output is in
  `.tmp/workflow-loading-bookmarks-retry-before.log`.

The full application suite and authenticated live streaming were not rerun.

Reproduce the focused checks with:

```powershell
.\scripts\dev.ps1 Test -NoRestore -Filter 'VOD watch progress:'
.\scripts\dev.ps1 Test -NoBuild -Filter 'VOD resume:'
.\scripts\dev.ps1 Test -NoBuild -Filter 'home refresh resources:'
.\scripts\dev.ps1 Test -NoBuild -Filter 'paging review:'
```

Before/after logs are in `.tmp/workflow-loading-bookmarks-before.log` and
`.tmp/workflow-loading-bookmarks-after.log`. These controlled checks do not measure
installed-app startup, provider response time, or live playback buffering.
