# Kick follow detection

**Settings > General > Followed channels > Detect Kick follows** uses the Kick website session saved in Stream Studio. When that session works, detection imports live and offline follows in the background and refreshes the live cards after saving. If Kick requires sign-in or a browser check, complete it in the window that opens and select **Import follows**. Use **Manage Kick sign-in** to change accounts or clear the session. Run detection again after following or unfollowing channels. One-minute polling refreshes live status for saved channels.

Kick API credentials are still needed for the existing public-API live-status checks. Website sign-in and Kick OAuth are separate sessions. Browser session data lives in `%LOCALAPPDATA%\StreamStudio\KickFollowsWebView2`; Clear Kick sign-in clears that profile. No website access token is copied into settings, returned through the browser message bridge, or logged.

## Verified website contract

An unauthenticated request to `https://kick.com/api/v2/channels/followed` returned HTTP 401 on 2026-11-18. Kick treats the channels an account follows as private. The current public API schema documents channel and livestream lookups. [Kick viewer privacy settings](https://help.kick.com/en/articles/14994505-managing-your-viewer-profile-and-privacy-settings-on-kick) · [Kick public API schema](https://api.kick.com/swagger/doc.json)

Website implementation checked on 2026-09-25.

- [Kick website](https://kick.com/) loaded [channel queries](https://assets.kick.com/main/_next/static/chunks/3f4a4e01ll_c0.js). `followedChannelsForSidebar` calls `/api/v2/channels/followed` with a `cursor` query parameter. Its infinite query reads `nextCursor` until it is absent or falsey.
- The website's [sidebar](https://assets.kick.com/main/_next/static/chunks/2hjoqzpwzl__6.js) flattens `pages[].channels`, deduplicates by `channel_slug`, and renders both `is_live` and offline entries. Detection uses this list, not recommendations, search results, or a guessed public endpoint.
- The site's [fetch implementation](https://assets.kick.com/main/_next/static/chunks/1zjh0c747t7_m.js) uses the `session_token` cookie for the Bearer header, `credentials: include`, and `x-app-platform: web`. The [cookie registry and environment](https://assets.kick.com/main/_next/static/chunks/24vax2xd1yhg9.js) define `AUTH_TOKEN` as `session_token` and the base URL as `https://kick.com`.

The asset filenames are deployment-specific references to the inspected source, not runtime dependencies. The app executes a bounded GET inside the signed-in Kick origin and receives only the response body/status. A per-import session fingerprint prevents pages from different browser sessions being combined. Each response is limited to 2 MiB, with 1,000-page and 50,000-channel limits, repeated-cursor rejection, strict channel validation, and cancellation. A partial list is never saved.

Background detection starts from the Kick homepage. In live WebView2 testing, Kick's Following page returned HTTP 200 and then started a separate navigation to the homepage for a signed-out session. WebView2 reported the superseded load as aborted. Detection waits for the latest allowed navigation to finish before reading follows. [WebView2 navigation events](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/navigation-events)

## Validation

`KickFollowImportTestCatalog` covers pagination, offline follows, deduplication, malformed and looping pages, limits, settings round trips, legacy settings, batching, empty lists, failed saves, cancellation, and shutdown. WebView2 integration tests cover saved-session reuse, sign-in after expiry during pagination, browser checks, rate limiting, explicit session clearing, and missing sessions. The tests intercept all network requests and use synthetic credentials in temporary profiles.

Authenticated end-to-end validation against a real Kick account requires signing in through the detection window. The automated fixtures validate the implementation, not the current response for a particular user's account.
