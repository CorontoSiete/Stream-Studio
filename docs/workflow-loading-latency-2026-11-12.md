# Browse and broadcast loading

Twitch VOD access checks now overlap up to four requests. Each request retains
the existing 15-video limit, and results are written to their original positions
so a slow batch cannot reorder videos. A 100-video page still makes seven access
requests, but they can overlap instead of waiting for seven sequential responses.
Failed batches keep their existing unknown-access labels; successful batches
retain their labels. Cancellation stops queued requests and drains active ones.

Kick offline artwork and broadcast history now start independently. The two
operations share a limit of four active website requests across the refresh.
An artwork deadline can therefore retain successfully loaded broadcast history,
just as a history deadline retains successfully loaded artwork. Partial results
keep the existing retry interval.

Selecting the currently displayed category again retains stream cards and
pagination, including a page already loading. Explicit Refresh still reloads
the first page, and returning to the category directory keeps its existing
navigation behavior.

## Verification

- All six behavior regressions failed against the previous implementation and
  passed after the changes. An additional guard checks the shared Kick request
  limit and cancellation: seven new checks passed.
- Release solution build with warnings treated as errors: zero warnings/errors.
- Related filtered runs covered browsing, pagination, refresh, VOD access,
  progressive followed-channel loading, optional artwork/history, language
  selection, startup loading, and Home navigation. They recorded 122 passes,
  seven interactive skips, and one category-preferences persistence failure.
  One desktop language-change check appears in two of these filters.
- The persistence failure was traced to Windows DPAPI being unavailable in the
  restricted execution context. A separate settings-save check reproduced the
  same encryption error. The original category-preferences persistence check
  passed when rerun with access to the Windows user profile.
- Whitespace formatting verification for changed C# files and `git diff --check`
  passed. Compiler/build analyzers passed; a separate full formatter/analyzer
  pass was stopped after reporting whitespace issues, which were corrected and
  verified with the focused whitespace check.

The full application suite and authenticated streaming were not run. These
checks establish overlapping work, retained navigation state, request limits,
and cancellation behavior; they do not measure installed-app startup or a
percentage improvement in live network or playback latency.

Reproduce the new checks with:

```powershell
.\scripts\dev.ps1 Test -NoRestore -Filter 'workflow loading latency:'
```

Logs are in `.tmp/workflow-loading-latency-*.log`,
`.tmp/workflow-loading-related-*.log`, and
`.tmp/workflow-loading-settings-persistence-verified.log`.
