[CmdletBinding()]
param(
    [ValidatePattern('^v\d+\.\d+\.\d+$')][string]$SourceTag = 'v1.8.12',
    [ValidatePattern('^v\d+\.\d+\.\d+$')][string]$TargetTag = 'v1.8.14'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' -or
    $env:GITHUB_REPOSITORY -notin @('CorontoSiete/streamlink-vlc-studio', 'CorontoSiete/Stream-Studio')) {
    throw 'This installation test is restricted to the repository disposable Windows Actions runner.'
}
. "$PSScriptRoot/lib/common.ps1"
. "$PSScriptRoot/lib/release-contract.ps1"
$sourceIdentity = Get-StableReleaseIdentity $SourceTag
$targetIdentity = Get-StableReleaseIdentity $TargetTag
if ($targetIdentity.Version -le $sourceIdentity.Version) { throw 'The target must be newer than the source.' }
$latestTag = & gh api "repos/$env:GITHUB_REPOSITORY/releases/latest" --jq '.tag_name'
if ($LASTEXITCODE -ne 0 -or $latestTag.Trim() -cne $TargetTag) {
    throw 'The ZIP smoke test requires the target to be the current published release.'
}
$root = Join-Path $env:RUNNER_TEMP ('published-zip-upgrade-' + [Guid]::NewGuid().ToString('N'))
$logs = [IO.Path]::GetFullPath('artifacts/upgrade-smoke/zip')
$download = Join-Path $root 'source'
New-Item -ItemType Directory -Path $download, $logs -Force | Out-Null
$updateRoot = Join-Path $env:LOCALAPPDATA 'StreamlinkVlcStudio/Updates'
$operationId = $null

try {
    & gh release download $SourceTag --repo $env:GITHUB_REPOSITORY --dir $download `
        --pattern StreamlinkVlcStudio-release.zip --pattern UPDATE-MANIFEST.json --pattern UPDATE-MANIFEST.sig
    if ($LASTEXITCODE -ne 0) { throw "Could not download $SourceTag ZIP assets." }
    $manifest = & "$PSScriptRoot/verify-update-manifest.ps1" `
        -ManifestPath (Join-Path $download 'UPDATE-MANIFEST.json') `
        -SignaturePath (Join-Path $download 'UPDATE-MANIFEST.sig') `
        -ExpectedTag $SourceTag -ExpectedVersion $sourceIdentity.VersionText -PassThru
    $zip = Get-Item -LiteralPath (Join-Path $download 'StreamlinkVlcStudio-release.zip')
    if ($zip.Length -ne $manifest.zip.length -or
        (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.zip.sha256) {
        throw 'The source ZIP does not match its signed update manifest.'
    }
    $payload = Join-Path $root 'application'
    Expand-ValidatedZipArchive -ArchivePath $zip.FullName -DestinationDirectory $payload
    $contract = Read-ReleaseContract (Join-Path $PSScriptRoot '../shared/release-contract.json')
    $applicationDirectory = Resolve-ReleasePayloadRoot -ExtractedRoot $payload -Contract $contract
    $executable = Join-Path $applicationDirectory 'StreamStudio.exe'
    Assert-WindowsFileVersion $executable 'Source ZIP application' $sourceIdentity.VersionText
    $sentinel = Join-Path $applicationDirectory 'upgrade-smoke-user-file.txt'
    $sentinelValue = [Guid]::NewGuid().ToString('D')
    [IO.File]::WriteAllText($sentinel, $sentinelValue)
    $reportPath = Join-Path $logs 'live-updater.json'
    $probeEnvironment = @{
        DOTNET_STARTUP_HOOKS = (Resolve-Path -LiteralPath 'tests/StreamlinkVlcStudio.UpdateProbe/bin/Release/net10.0/StreamlinkVlcStudio.UpdateProbe.dll').Path
        DOTNET_BUNDLE_EXTRACT_BASE_DIR = (Join-Path $root 'bundle')
        SVS_UPDATE_PROBE_RESTART_EXECUTABLE = $executable
        SVS_UPDATE_PROBE_RESTART_REPORT = $reportPath
        SVS_UPDATE_PROBE_EXPECTED_TAG = $TargetTag
        SVS_UPDATE_PROBE_EXPECTED_INSTALL_KIND = 'Zip'
        SVS_UPDATE_PROBE_DOWNLOAD = 'true'
        SVS_UPDATE_PROBE_APPLY = 'true'
    }
    $source = Start-Process -FilePath $executable -WorkingDirectory $applicationDirectory `
        -WindowStyle Hidden -PassThru -Environment $probeEnvironment `
        -RedirectStandardError (Join-Path $logs 'source-stderr.txt') `
        -RedirectStandardOutput (Join-Path $logs 'source-stdout.txt')
    try {
        if (-not $source.WaitForExit(660000)) { $source.Kill(); throw 'The source ZIP updater timed out.' }
        if ($source.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $reportPath)) {
            throw "The source ZIP updater failed with exit code $($source.ExitCode). See source-stderr.txt."
        }
    } finally { $source.Dispose() }
    $sourceReport = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($sourceReport.InstallKind -cne 'Zip' -or $sourceReport.LiveUpdate.Phase -cne 'Ready' -or
        $sourceReport.LiveUpdate.Launch.Started -ne $true) {
        throw 'The source ZIP updater did not verify its download and start its real update helper.'
    }
    $sourceReport | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $logs 'source-updater.json')
    $operationId = [Guid]::Parse([string]$sourceReport.LiveUpdate.PreparedUpdate.OperationId)
    Write-Host "PASS published ZIP $SourceTag authenticated $TargetTag, downloaded its ZIP, and launched its own helper."

    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    do {
        Start-Sleep -Milliseconds 250
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if ($report.LiveUpdate.Phase -ceq 'Completed') { break }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'The upgraded ZIP did not restart and confirm the installed release.' }
    } while ($true)
    if ($report.ProcessPath -ine $executable -or $report.ConfiguredDirectory -ine $applicationDirectory -or
        $report.InstallKind -cne 'Zip' -or $report.LiveUpdate.Check.Release.Tag -cne $TargetTag -or
        $report.LiveUpdate.Check.IsUpdateAvailable -ne $false) {
        throw 'The restarted ZIP application did not recognize its installed version and ownership.'
    }
    Assert-WindowsFileVersion $executable 'Upgraded ZIP application' $targetIdentity.VersionText
    if (-not (Test-Path -LiteralPath $sentinel) -or [IO.File]::ReadAllText($sentinel) -cne $sentinelValue) {
        throw 'The ZIP upgrade did not preserve the existing user file.'
    }
    Write-Host "PASS real ZIP upgrade $SourceTag -> $TargetTag preserved user files, verified runtime dependencies, restarted, and reported no pending update."
} finally {
    if ($null -ne $operationId) {
        foreach ($path in @(
                (Join-Path $updateRoot ('logs/' + $operationId.ToString('N') + '.log')),
                (Join-Path $updateRoot ('results/' + $operationId.ToString('N') + '.json')))) {
            if (Test-Path -LiteralPath $path -PathType Leaf) { Copy-Item -LiteralPath $path -Destination $logs -Force }
        }
    }
}
