# Workflow and loading improvements

Desktop live-search results become usable as soon as channel discovery or
playability checks finish. Optional titles, categories, avatars and thumbnails
arrive afterward, updating existing rows and retaining their commands and focus.
Twitch VOD links still wait for their availability check before becoming playable.
Viewer totals update and reorder rows as each channel responds, with at most four
requests in flight. Old queries cannot publish late metadata or viewer totals.

Browser directory loads now belong to the selected page. Navigation, refresh,
Preferences and disconnect cancel the previous load. Canceled reads still waiting
in the command queue are skipped before being sent; active fetches propagate
cancellation to the existing server request lifetime. Generation and session
checks also reject providers that return late. Playback and settings writes keep
their existing ordering.

Selecting the current directory again preserves its cards, query, filters and
scroll position. Refresh still explicitly reloads it. Changing pages, returning
from playback and opening a category retain their normal navigation behavior.

## Verification

- Release solution build with warnings treated as errors: zero warnings/errors.
- Formatting/analyzer verification for the changed C# files and diff whitespace
  validation passed.
- Desktop workflow, Home search, stream-opening and existing loading checks:
  66 passed, one interactive-layout check skipped.
- JavaScript regression suite: 113 passed, including six new cancellation and
  navigation cases.
- Real Chrome navigation checks passed at 1440px and 390px, including canceled
  slow requests, preserved controls/filters, explicit refresh and Preferences.
  No JavaScript errors or horizontal overflow; screenshots visually checked.
- Existing real Chrome search checks passed across nine widths from 320–1920px.

Reproduce with:

```powershell
.\scripts\dev.ps1 Test -NoRestore -Filter 'workflow resources:'
.\scripts\dev.ps1 Test -NoBuild -Filter 'home search'
.\scripts\dev.ps1 Test -NoBuild -Filter 'stream open workflow:'
.\scripts\dev.ps1 Test -NoBuild -Filter 'loading responsiveness:'
node --test scripts/tests/*.tests.cjs
node scripts/tests/web-navigation.browser.cjs --loading-only
```

These checks use controlled providers. They establish earlier usability and
cancellation behavior, not a measured percentage reduction in installed-app
startup, platform/network latency or playback buffering. Live authenticated
streaming and the full application suite were not rerun for this change.

## Independent search details and retained browser cards

Desktop live-search metadata now publishes per channel as soon as it arrives.
Viewer-count requests start alongside metadata, so a slow artwork lookup cannot
delay another channel's artwork or any viewer totals. The search continues to own
and drain both operations, cancels them together when the query changes, and
rejects late results. Explicit Twitch VOD links still complete their availability
lookup before becoming playable.

The browser reconciles cards by identity and content instead of rebuilding the
entire grid. Load more appends new cards, an unchanged refresh retains existing
controls and decoded artwork, and sorting moves the existing cards while keeping
keyboard focus. Changed metadata rebuilds only its affected card. Optional
category totals and offline-history updates keep the retained card's content
snapshot current. The snapshots use weak references so removed cards are not
kept in a separate cache.

The controlled Chrome pagination check loads 100 categories and then another
25: the second page adds exactly 25 card elements and removes zero. The previous
renderer removed all 100 and created 125 replacements. This is a reduction in
rendering work; it is not a measured percentage improvement in network latency,
installed-app startup, or playback buffering.

Follow-up verification:

- Both new desktop regressions failed before the change because viewer-count
  requests had not started while artwork was pending. They now pass, including
  fast artwork arriving before the slow channel and cancellation of providers
  that ignore the cancellation token.
- Three new browser regressions failed against the previous renderer and pass
  with card reuse. A fourth checks that changing pages rebuilds the appropriate
  controls even if the card ID is the same.
- Release solution build with warnings treated as errors: zero warnings/errors.
- Focused desktop workflow, Home search, stream opening, and VOD-link metadata:
  75 passed, one interactive-layout check skipped.
- JavaScript regression suite: 117 passed, zero skipped.
- Formatting/analyzer verification for the changed C# files and diff whitespace
  validation passed.
- Real Chrome loading, category totals, VOD-library and download-delete checks
  passed. These cover pagination, unchanged refreshes, reorder focus, enriched
  card identity, cancellation, publication sorting, and actual confirmation
  dialogs. Desktop and phone screenshots were visually checked.

Reproduce the added coverage with:

```powershell
.\scripts\dev.ps1 Test -NoRestore -Filter 'workflow resources:'
.\scripts\dev.ps1 Test -NoBuild -Filter 'home search'
.\scripts\dev.ps1 Test -NoBuild -Filter 'stream open workflow:'
.\scripts\dev.ps1 Test -NoBuild -Filter 'VOD link metadata:'
node --test scripts/tests/*.tests.cjs
node scripts/tests/web-navigation.browser.cjs --loading-only
node scripts/tests/web-navigation.browser.cjs --category-viewers-only
node scripts/tests/web-navigation.browser.cjs --vod-library-only
node scripts/tests/web-navigation.browser.cjs --downloads-delete-only
```

Follow-up logs are in `.tmp/workflow-loading-followup/`. Live authenticated
streaming and the full application suite were not rerun for this follow-up.
