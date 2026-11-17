# Disk footprint: eliminate duplicate runtime files

The release now installs native runtime libraries beside the managed executable.
The runtime loads those files in place, so launching a new build no longer creates
a second native-runtime copy in a .NET extraction cache. Managed assemblies still
load directly from the uncompressed bundle. Nothing was trimmed or recompressed
at runtime, and no runtime binaries, codecs, languages, or application resources
were removed.

The publish also omits three WebView2 reference XML files (797,549 bytes). These
are developer IntelliSense documentation; the app does not read them. Third-party
notices, native source/build tools, and all runtime DLLs remain in the payload.

Project defaults control local publishing and both CI publication paths. The
packaged-app CI check starts each executable with a fresh extraction destination,
renders WPF controls, checks bundled resources, and rejects any runtime files
created in that destination. The previous native-extracting layout was run as a
negative control and was rejected.

The updater copies only the executable into its operation directory. Testing that
copy through its real entry point exposed a dependency on WPF initialization that
the startup-hook probe could not see. A dedicated entry point now dispatches
maintenance/update commands before constructing `Application` or loading XAML.
The packaged-app check stages the executable alone, removes the startup hook,
and verifies that the real helper rejects invalid arguments normally, with no
native sidecars or extraction cache. It also rejects the unfixed executable as a
negative control. Normal UI startup still constructs the same App, loads the same
XAML, and runs the same dispatcher.

Microsoft documents the native-library deployment behavior and the startup cost
of bundle compression in its [single-file deployment reference](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview#native-libraries).

## Measured footprint

The same checkout, .NET SDK 10.0.403 and Release/win-x64 self-contained publish
were used. Values count every published file plus files extracted by that build
after launch. They are file lengths, not filesystem allocation-unit accounting.
They exclude separately installed prerequisites, user downloads, browser profiles,
installer-maintenance files, and historical builds' caches.

| Layout | Published bytes | Extracted bytes | Combined |
| --- | ---: | ---: | ---: |
| Original full extraction, from the earlier measurement | 108,935,244 | 246,905,820 | 339.36 MiB |
| Intermediate: only native extraction | 260,022,645 | 22,380,600 | 269.32 MiB |
| Final: installed native libraries, no reference XML | 259,220,573 | 0 | 247.21 MiB |

The additional reduction is **23,182,672 bytes / 22.11 MiB**. Relative to the
original recorded layout, the combined reduction is **96,620,491 bytes / 92.14 MiB
/ 27.15%**. The new layout creates **zero .NET runtime-extraction files**.
The [earlier report](disk-space-usage-2026-11-12.md) preserves the original
comparison and the rejected runtime-compression alternative.

Every one of the ten native files extracted by the intermediate layout matches
the final installed library's SHA-256. Every embedded-resource digest also matches
the original full-extraction package, including all 440 WPF resources, web assets,
release notes, badges, and custom playback binaries.

## Performance

Each run used a new extraction destination, then 40 paired warm launches,
alternating the order. Measurements are process elapsed time. No build or test
suite ran alongside these timing probes.

| Probe | Intermediate median / p95 | Candidate median / p95 |
| --- | ---: | ---: |
| Initial updater startup hook | 128.35 / 172.06 ms | 128.42 / 135.13 ms |
| Initial resources and WPF rendering | 1,137.11 / 1,141.10 ms | 1,125.48 / 1,177.10 ms |
| Repeated rendering before the helper fix | 1,126.78 / 1,141.41 ms | 1,127.67 / 1,145.35 ms |
| Final updater startup hook | 124.04 / 126.17 ms | 124.27 / 128.28 ms |
| Final resources and WPF rendering | 1,124.45 / 1,138.66 ms | 1,123.70 / 1,138.63 ms |

The final baseline and candidate include the same helper-entry-point fix. The
rendering probes hash all application resources, load all eight palettes, and
render VolumeOverlay, ReplaySeekOverlay, and VodDownloadsView offscreen.

The paired medians are effectively unchanged. Tail timings varied between runs;
the final rendering median and p95 are both slightly lower for the candidate.
These observations do not show a consistent slowdown. They do not prove identical
timing for every workload or measure post-reboot cold startup, network streaming,
or every user interaction.

## Verification and deliverable

- The Release solution build and self-contained publish succeeded with zero
  warnings/errors. Whitespace verification passed for the changed C# files.
- All 124 update tests, 16 changelog tests, two release-package tests, and the app
  lifecycle test passed. The final update run included the desktop checks; no
  tests were skipped.
- Four workflow, six development, 11 install-lifecycle, and 66 tooling checks
  passed against temporary fixtures.
- The final executable passed the real dependency verifier: Streamlink 8.5.0,
  VLC 3.0.23 with the verified custom core, WebView2 157.0.4341.46 executing local
  JavaScript, Skia bitmap access, and HarfBuzz buffer creation.
- The complete staged payload passed the bundled-resource, extraction, standalone
  helper, and executable-directory checks and was correctly detected as a ZIP
  installation. Both negative controls were rejected.
- All 89 files in the final ZIP were read back and SHA-256 checked against staging,
  including ownership metadata, native sidecars, and the uninstaller. Its executable
  hash matches the candidate in the final timing measurements.

The verified local ZIP is
`.artifacts/disk-minimum/release/StreamlinkVlcStudio-release.zip`, 102,796,471 bytes
(SHA-256 `696C43F458C3FA9834E0138B07FEDA5F9E08CE4804B9C72BB8E6CD5EFE10EB8E`).
Its full uncompressed distribution is 263,713,655 bytes, including the additional
installer/uninstaller files excluded from the publish comparison above. It has
not been installed over the user's app or published.

Temporary comparison builds, staged payloads, and their extraction trees were
removed after verification (1,930,304,677 bytes). The final ZIP and small measurement
records remain; user caches and saved media were not part of that cleanup.

## App data and retention

The read-only audit found browser profiles of about 291.31 MiB for Twitch bonuses,
18.23 MiB for Kick follows, and 9.05 MiB for Twitch follows. Removing HTTP/code
caches would require later network downloads and compilation. These profiles,
account state, saved media, and historical runtime caches were preserved.

Existing code already bounds logs to five 10 MiB files, removes temporary replay
playlists when their lease ends, protects offline media used by players, retries
obsolete-download cleanup, and expires staged update packages while retaining
failed/canceled operations for retry. Those contracts were preserved.

The app's custom embedded VLC components still use their existing verified,
content-addressed app-data files. This report's zero-extraction result refers to
the .NET bundle cache, not to those separate playback components. No claim is made
that the final byte count is an absolute lower bound across all possible designs.

## Reproduction

Publish the intermediate baseline with
`IncludeNativeLibrariesForSelfExtract=true` and
`PublishReferencesDocumentationFiles=true`; publish the final layout using project
defaults. Both must use Release/win-x64, self-contained single-file deployment,
`PublishTrimmed=false`, `DebugType=none`, and `DebugSymbols=false`.

Run `scripts/measure-package-footprint.ps1` with those executables, the built
UpdateProbe DLL, a new output directory, and `-Iterations 40`. Repeat with
`-VerifyResources` for WPF/resource verification. The script now correctly records
zero bytes when a build does not create an extraction directory.

Raw timing samples and verification evidence are retained in
`docs/measurements/disk-space-usage-2026-11-12/native-libraries/`.
