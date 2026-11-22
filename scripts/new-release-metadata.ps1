[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$SourceRef,
    [Parameter(Mandatory = $true)][string]$WorkflowRef,
    [Parameter(Mandatory = $true)][long]$WorkflowRunId,
    [Parameter(Mandatory = $true)][long]$WorkflowRunNumber,
    [Parameter(Mandatory = $true)][long]$WorkflowRunAttempt,
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [Parameter(Mandatory = $true)][string]$InternalMsiPath,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$SbomPath,
    [Parameter(Mandatory = $true)][string]$UpdateManifestPath,
    [Parameter(Mandatory = $true)][string]$UpdateSignaturePath,
    [string]$OutputPath = 'artifacts\RELEASE-METADATA.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
. (Join-Path $scriptRoot 'lib\common.ps1')
. (Join-Path $scriptRoot 'lib\release-contract.ps1')
. (Join-Path $scriptRoot 'lib\authenticode.ps1')
$contract = Read-ReleaseContract (Join-Path $repoRoot 'shared\release-contract.json')
$identity = Get-StableReleaseIdentity -Tag $Tag -MinimumVersion ([string]$contract.release.minimumVersion)
if ($identity.VersionText -cne $Version) {
    throw "Release metadata version $Version does not match tag $Tag."
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Repository must be an owner/name pair: $Repository"
}

$authenticode = Get-AuthenticodeSigningConfiguration
$msiVersion = Get-MsiPropertyValue -Path $InternalMsiPath -Property 'ProductVersion'
$metadata = [ordered]@{
    schemaVersion = 2
    version = $Version
    tag = $Tag
    repository = $Repository
    commit = $Commit
    sourceRef = $SourceRef
    workflowRef = $WorkflowRef
    workflowRunId = $WorkflowRunId
    workflowRunNumber = $WorkflowRunNumber
    workflowRunAttempt = $WorkflowRunAttempt
    msiProductVersion = $msiVersion
    bundleVersion = $identity.FourPartVersion
    dotnetSdkVersion = [string](Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    selfContainedRuntimeVersion = [string](ConvertTo-StableReleaseVersion ([string]$contract.release.dotnetRuntimeVersion))
    updaterProtocolVersion = [int]$contract.release.updaterProtocolVersion
    updateSigning = [ordered]@{
        algorithm = [string]$contract.release.manifestSignature.algorithm
        keyId = [string]$contract.release.manifestSignature.keyId
    }
    authenticode = [ordered]@{
        enabled = [bool]$authenticode.Enabled
        timestampProtocol = if ($authenticode.Enabled) { 'RFC3161' } else { $null }
        timestampUrl = if ($authenticode.Enabled) { [string]$authenticode.TimestampUrl } else { $null }
        signerThumbprint = if ($authenticode.Enabled) { [string]$authenticode.CertificateThumbprint } else { $null }
    }
    artifacts = (Get-ReleaseMetadataArtifacts $contract $PSBoundParameters)
    internalMsi = [ordered]@{
        public = $false
        productVersion = $msiVersion
    }
    nativeInputManifestSha256 = Get-FileSha256 (Join-Path $repoRoot 'dependencies\native-overlay.json')
    createdUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
}

$fullOutputPath = Resolve-ReleasePath $OutputPath
$parent = Split-Path -Parent $fullOutputPath
[IO.Directory]::CreateDirectory($parent) | Out-Null
$temporary = "$fullOutputPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText(
        $temporary,
        ($metadata | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $fullOutputPath -Force
} finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) {
        Remove-Item -LiteralPath $temporary -Force
    }
}
Write-Output $fullOutputPath
