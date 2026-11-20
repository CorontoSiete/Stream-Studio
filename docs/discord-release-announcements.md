# Discord release announcements

The **Announce Discord release** workflow posts published Stream Studio releases
to the server's #announcements channel. Each post includes the release notes,
Windows installer link, full release link, publication time, and Stream Studio's
mint accent. The first post for each published release pings `@everyone`.
Changelog continuation posts and announcement edits are silent. Only release
publication triggers automatic announcements.

Include a populated **Improvements and bug fixes** section under the Markdown
heading `## Improvements and bug fixes` in each new versioned release note.
The stable release build checks this section before it builds the packages.

The workflow needs the repository Actions secret DISCORD_RELEASE_WEBHOOK_URL.
Use a Discord incoming webhook that can post only to #announcements. Never put
its URL in source files, release notes, issue comments, or workflow logs.

Both the protected build and the artifact recovery workflow call the announcement
job after successful publication. This covers releases published with `GITHUB_TOKEN`,
which GitHub excludes from triggering new `release` workflow runs. Releases
published through the GitHub UI still use the `release` event. The announcement
job has read-only repository access. If a notification fails, use the manual
workflow below to retry that release. [GitHub workflow trigger rules](https://docs.github.com/en/actions/concepts/security/github_token#when-github_token-triggers-workflow-runs)

## Verify or backfill a release

Open **Actions → Announce Discord release → Run workflow**. Enter a published
release tag, or leave it blank for the latest release. Leave **Validate without
posting to Discord** enabled to check the payload and verify the webhook with
Discord.
To correct an existing announcement, update the GitHub release notes and supply
the Discord message ID alongside the release tag. Run with validation disabled
to apply the edit. The workflow checks that the message links to that release
before editing it.

With validation disabled and the message ID blank, the workflow posts an
announcement and pings `@everyone`. Check the channel before retrying a failed
send to avoid a duplicate ping.

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
