# Discord release announcements

The **Announce Discord release** workflow posts published Stream Studio releases
to the server's #announcements channel. Each post includes the release notes,
Windows installer link, full release link, publication time, and Stream Studio's
mint accent. It sends no mass mentions. Draft changes, commits, and builds do not
trigger announcements.

The workflow needs the repository Actions secret DISCORD_RELEASE_WEBHOOK_URL.
Use a Discord incoming webhook that can post only to #announcements. Never put
its URL in source files, release notes, issue comments, or workflow logs.

This is independent of the protected release build and runs after publication.
A notification failure does not change the published release or in-app updater.
The workflow has read-only repository access and no signing credentials.

## Verify or backfill a release

Open **Actions → Announce Discord release → Run workflow**. Enter a published
release tag, or leave it blank for the latest release. Leave **Validate without
posting to Discord** enabled to check the payload and secret configuration.
Disable that option only when a real announcement is wanted. Before rerunning a
failed send, inspect the channel so an uncertain response does not duplicate it.

Ordinary release notes are included in full, split into separate posts when
needed to respect Discord's limits. Exceptionally long notes beyond ten posts
link to the remaining changelog. Internal HTML comments are removed.

If replacing Discord's GitHub-compatible webhook, disable that repository
webhook after this workflow is active; the built-in formatter sends release
links without the changelog and would produce duplicate notices.

## Local validation

Run:

    python -m unittest discover -s scripts/tests -p test_discord_release.py

The tests make no network requests. The announcement script also accepts
--release-json <file> --dry-run for a saved public GitHub release object.
