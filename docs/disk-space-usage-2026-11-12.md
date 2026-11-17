# Disk footprint without a startup regression

This records the first reduction. The [follow-up measurement](disk-space-minimum-2026-11-12.md)
removes native-library extraction too and reduces the final measured footprint to
247.21 MiB. The intermediate layout and its original measurements remain below.

Single-file releases now load managed assemblies directly from an uncompressed
bundle. Only native libraries are extracted. The old compatibility mode extracted
all managed assemblies, resources and content alongside the already installed
bundle. No assemblies, languages, codecs, features or ReadyToRun code were removed.

The settings live in the WPF project so local packaging and both CI publication
paths agree. CI also starts the actual published executable, verifies that its
managed assemblies have no extracted file location, reads its bundled resources,
loads all eight color palettes and renders three production WPF controls offscreen.
The same check deliberately rejects the old package layout.

Microsoft documents both the unnecessary full-extraction compatibility mode and
the startup cost of bundle compression in its
[single-file deployment reference](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview#other-considerations).

## Measured footprint

These are actual file byte counts from the same checkout and SDK 10.0.403,
including the complete publish directory and its extraction cache after launch.
They exclude separately installed prerequisites, browser profiles, saved VODs and
historical releases. MiB means 1,048,576 bytes; these are file lengths rather than
filesystem allocation-unit accounting.

| Files | Previous layout | Retained layout |
| --- | ---: | ---: |
| Published app and support files | 108,935,244 bytes | 260,022,645 bytes |
| Runtime extraction | 246,905,820 bytes / 599 files | 22,380,600 bytes / 10 files |
| Combined | 355,841,064 bytes (339.36 MiB) | 282,403,245 bytes (269.32 MiB) |

The combined saving is **73,437,819 bytes / 70.04 MiB / 20.64%**. Extraction per
build drops **90.94%**. The executable itself grows; measuring it alone would
miss the larger saving in the extracted copy.

Release ZIPs use `SmallestSize` compression when the packaging host supports it,
with `Optimal` retained for Windows PowerShell compatibility. Compression happens
once during packaging. The complete new ZIP is **102,937,743 bytes**; substituting
the previous executable into the same support-file set and using the previous
`Optimal` level produced **103,704,397 bytes**. This controlled comparison isolates
the packaging change; it is not a comparison against a downloaded public release.
All **82** files in the new ZIP were read back and SHA-256 checked against staging.

## Performance and rejected alternative

Each comparison used a new extraction directory and 24 warm launches per variant,
alternating which variant ran first. The startup hook verifies the shipped updater
without opening the application or touching account settings.

| Startup-hook process time | Previous | Retained |
| --- | ---: | ---: |
| First launch into a fresh extraction cache | 1,371.48 ms | 148.91 ms |
| Warm median | 157.90 ms | 130.28 ms |
| Warm p95 | 165.91 ms | 139.90 ms |

A second 24-pair run additionally hashed every application resource, loaded eight
palettes and rendered VolumeOverlay, ReplaySeekOverlay and VodDownloadsView.
Warm medians were **1,164.81 ms** and **1,133.41 ms**; p95 was **1,185.06 ms** and
**1,150.76 ms**. These are bounded startup/rendering probes on this machine, not
whole-application startup, network-playback latency or post-reboot cold benchmarks.

Keeping compression while disabling full extraction was tested separately. It
reduced the combined footprint to 131,980,580 bytes, but warm startup median rose
from **155.20 ms to 291.39 ms** and p95 from **162.77 ms to 317.05 ms**. That option
was rejected because it violates the performance requirement.

## Verification

- Release test-project build and final self-contained publish succeeded with zero
  warnings/errors; the package-probe project also builds with warnings as errors.
- All embedded resource SHA-256 values match between the previous and retained
  packages: WPF markup, 440 WPF resources, badges, web assets, release notes and
  embedded native playback binaries.
- Both packages pass the actual hidden dependency verifier: Streamlink 8.5.0,
  VLC 3.0.23 with the pinned core, WebView2 157.0.4341.46 executing local JavaScript,
  Skia bitmap access and HarfBuzz buffer creation. The final executable was checked.
- All 124 tests selected by `update`, all 16 `changelog:` tests, both release-package
  tests and the four workflow checks passed, with zero skips. Initial restricted
  runs blocked Windows encryption, certificates and desktop input; rerunning those
  checks with normal access passed. These were not counted as product failures.
- The packaged updater recognizes both an unmanaged publish and the final owned
  ZIP payload and resolves the actual executable directory.

The installation audit found 3,871.47 MiB of historical runtime extraction under
the legacy `StreamlinkVlcStudio` bundle name, plus smaller extraction trees from
other named builds. It also found 331.01 MiB of current app data, mostly WebView2
HTTP/code caches. Existing caches were preserved: earlier runnable builds and the
currently running installation can still use them, and deleting browser caches
would trade disk space for repeated downloads and compilation. This change reduces
new build footprints; it does not claim to reclaim every historical cache.

## Reproduction and artifacts

Run `scripts/measure-package-footprint.ps1` with `-BaselineExecutable`,
`-CandidateExecutable`, `-ProbePath` and a new `-OutputDirectory`. Add
`-VerifyResources` for the resource and WPF rendering comparison. The baseline
publish uses `IncludeAllContentForSelfExtract=true` and
`EnableCompressionInSingleFile=true`; the retained publish uses project defaults.
Both use Release/win-x64, self-contained single-file deployment, no trimming and no
debug symbols. Raw measurements and archive verification are in
`docs/measurements/disk-space-usage-2026-11-12/`.

The complete local build is
`.artifacts/disk-usage/release/StreamlinkVlcStudio-release.zip` (SHA-256
`8B92AE0F80634932B5043E2A288510AAE15500066A7766B15F61D70517D1F99D`).
It has not been installed over the running application or publicly released.
