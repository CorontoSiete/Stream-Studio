# Release publication and retry

The protected stable-release job builds and validates the installers, signs the
update manifest, runs the installation/upgrade smoke test, and attests the
reviewed files before calling `scripts/publish-release.ps1`. That script keeps
the release as a draft until all seven uploaded assets match the local release
set. Normal users and the in-app updater receive the release only after this
verification succeeds.

The repository is now named `CorontoSiete/Stream-Studio`. Protocol 1 installed
updaters pin `CorontoSiete/streamlink-vlc-studio` in the signed manifest, so the
stable release job retains that release identity. Installed clients use GitHub's
redirect to reach the renamed repository. Do not replace `RELEASE_REPOSITORY` with the current
workflow repository name: existing clients would reject the signed manifest.
The disposable installer verification accepts both names, while still requiring
Windows GitHub Actions. Workflow regressions enforce both compatibility rules.

The stable-release smoke test upgrades the published 1.8.0 installer using that
release's own update helper. It verifies the new installed version, retained user
data, the restarted application's managed updater, and quiet repair before the
release is published. The manual upgrade workflow can still select older source
versions and the target helper for clients predating the packaged-updater fix.

For the latest published target and the source helper, the manual workflow runs the
installed source application's real signed release check and download against
GitHub. It installs the exact package and helper staged by that updater, then
checks that the upgraded application reports the latest version. Targets that
declare dependency verification also undergo passive same-version repair with
an intentionally removed application plugin and exact-byte restoration checks.

With the source helper selected, the manual workflow also extracts the signed
source ZIP on that disposable runner and calls its real `CheckAsync`,
`DownloadAsync`, and `ApplyAndRestartAsync` through the packaged probe. It checks
the restarted target's version, ZIP ownership, retained user file, and completed
update state. The helper verifies installed runtime dependencies before restart.
This ZIP test requires the target to be the current published release.

`publish-release.ps1 -Repository` validates the signed release identity;
`-GitHubRepository` selects the current GitHub destination for all API calls,
uploads, and draft markers. The workflow passes `GITHUB_REPOSITORY` for that
destination. The optional destination defaults to `-Repository` for repositories
without a rename.

Creation uses `--verify-tag` and independently resolves that existing tag to the
reviewed commit before and after uploading. It omits the unused `--target` field:
[GitHub checks workflow-edit permission for an explicit target commit](https://docs.github.com/en/rest/releases/releases#create-a-release)
that differs in workflow files from the current default branch, even when the
release tag already exists. The default branch may have advanced during a long
release build. A draft can therefore retain the default branch's informational
target label; its exact marker and the resolved tag remain authoritative. An
explicitly different target SHA is still rejected.

## Retry an interrupted release

If publication tooling needs a fix after the protected run produced reviewed,
signed artifacts, run **Publish verified release artifacts** on the current
default branch with the original build run ID and stable tag. This recovery job
uses the protected release environment, requires successful Windows validation,
installation/upgrade/repair, artifact upload, and provenance/SBOM attestation,
then independently checks the downloaded signatures, checksums, attested source,
and every artifact digest. It publishes the original files with the original
tagged notes, without rebuilding the app or accessing the signing key.

Rerun the failed protected workflow for the same stable tag and source commit.
The publication script recognizes its draft by an exact repository/tag/commit
marker in the release notes and the verified remote tag commit. It reuses files with
matching names, upload states, sizes, and SHA-256 digests, and replaces missing,
unfinished, or changed uploads. After creating a draft, it briefly retries an
absent list entry to accommodate GitHub propagation. API errors and mismatched
release state still stop immediately. A rebuilt file can have different bytes,
so a full job rerun may need to replace more files than an interrupted upload alone.
Validation artifacts, reviewed release artifacts, and installer logs include the
workflow attempt number in their names, so reruns retain previous evidence and
avoid colliding with an earlier attempt's uploads.

Failures before publication leave the draft available for another attempt.
An absent or mismatched GitHub digest blocks publication. Unexpected or duplicate
asset names require inspection; the script does not silently delete them.
Manually created drafts, drafts for another commit, prereleases, and already
published releases are rejected without modification. If GitHub published the
release but the final response was lost, inspect the existing public release;
a rerun intentionally refuses to replace it.

Release jobs share a concurrency group. Preflight checks all pages of existing
releases, rejects an equal or newer final version, and verifies the remote tag's
commit. The same checks run again after uploading. A failed GitHub request or
malformed response stops the operation rather than implying a release is absent.

## Local verification

Run these from the repository with PowerShell 7:

```powershell
pwsh -NoProfile -File .\scripts\tests\release-publication.tests.ps1
pwsh -NoProfile -File .\scripts\tests\tooling.tests.ps1
```

The publication tests generate a temporary RSA key and signed fixture assets,
then simulate the GitHub CLI. They exercise publication, partial-upload and
publication retries, asset reuse/replacement, changed tags, newer releases,
foreign drafts, pagination, network failures, corrupt assets, and invalid
signatures. They make no network requests and do not install or publish anything.
CI runs them in the validation job before the protected release job.

The preflight command is read-only and needs GitHub CLI authentication:

```powershell
.\scripts\publish-release.ps1 -Preflight -Tag v1.7.7 `
    -Commit '<40-character source commit>' `
    -Repository CorontoSiete/streamlink-vlc-studio
```

Running without `-Preflight` publishes a release and belongs in the protected
workflow after its build, signature, installer, and provenance checks.

GitHub documents [draft creation](https://cli.github.com/manual/gh_release_create),
[asset replacement](https://cli.github.com/manual/gh_release_upload),
[draft publication](https://cli.github.com/manual/gh_release_edit), and
[release asset digests](https://docs.github.com/en/rest/releases/assets).
