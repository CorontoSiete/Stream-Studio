[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$ContractPath,
    [string]$Destination,
    [string]$CheckRoot,
    [switch]$Check,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptRoot 'lib\common.ps1')
. (Join-Path $scriptRoot 'lib\release-contract.ps1')
$repoRoot = Resolve-ReleasePath $RepositoryRoot (Join-Path $scriptRoot '..')
$contractFile = Resolve-ReleasePath $ContractPath (Join-Path $repoRoot 'shared\release-contract.json')
$contract = Read-ReleaseContract $contractFile

function Assert-SignedReleaseSet([object[]]$Files) {
    $byName = @{}
    foreach ($item in $Files) { $byName[[string]$item.Entry.name] = $item.File.FullName }
    $metadata = Get-Content -LiteralPath $byName['RELEASE-METADATA.json'] -Raw | ConvertFrom-Json
    & (Join-Path $scriptRoot 'verify-update-manifest.ps1') `
        -ContractPath $contractFile `
        -ManifestPath $byName['UPDATE-MANIFEST.json'] `
        -SignaturePath $byName['UPDATE-MANIFEST.sig'] `
        -SetupPath $byName['StreamlinkVlcStudio-Setup.exe'] `
        -ZipPath $byName['StreamlinkVlcStudio-release.zip'] `
        -ExpectedVersion ([string]$metadata.version) `
        -ExpectedTag ([string]$metadata.tag) `
        -ExpectedCommit ([string]$metadata.commit) `
        -ExpectedRepository ([string]$metadata.repository)
}

if ($Check) {
    $root = Resolve-ReleasePath $CheckRoot (Join-Path $repoRoot 'artifacts\release-set')
    $files = @(Test-VerifiedReleaseSet -Contract $contract -Root $root)
    Assert-SignedReleaseSet $files
    Write-Info "Verified closed release set with $($files.Count) assets: $root"
    return
}

$destinationRoot = Resolve-ReleasePath $Destination (Join-Path $repoRoot 'artifacts\release-set')
$files = @(New-VerifiedReleaseSet -Contract $contract -RepositoryRoot $repoRoot -Destination $destinationRoot `
    -ValidateStagedSet ${function:Assert-SignedReleaseSet})
Write-Info "Promoted closed release set with $($files.Count) assets: $destinationRoot"
