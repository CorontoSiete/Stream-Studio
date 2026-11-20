[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory = $true)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit,
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$PrivateKeyPath,
    [string]$ContractPath,
    [string]$PublicKeyPath,
    [string]$Repository = 'CorontoSiete/streamlink-vlc-studio',
    [string]$OutputDirectory = 'artifacts'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
. (Join-Path $scriptRoot 'lib\common.ps1')
. (Join-Path $scriptRoot 'lib\dependency-manifest.ps1')
. (Join-Path $scriptRoot 'lib\release-contract.ps1')
$contractFile = Resolve-ReleasePath $ContractPath (Join-Path $repoRoot 'shared\release-contract.json')
$contract = Read-ReleaseContract $contractFile
$identity = Get-StableReleaseIdentity -Tag $Tag -MinimumVersion ([string]$contract.release.minimumVersion)
if ($identity.VersionText -cne $Version) { throw "Tag must exactly match version: v$Version" }
if ($Commit -cne $Commit.ToLowerInvariant()) { throw 'Commit must use lowercase hexadecimal.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "Repository must be an owner/name pair: $Repository" }
$PublicKeyPath = Resolve-ReleasePath $PublicKeyPath (Get-ReleaseContractPublicKeyPath $contractFile $contract)
foreach ($path in @($PrivateKeyPath, $PublicKeyPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required update-manifest input is missing: $path" }
}
$dependencyManifestPath = Join-Path $repoRoot 'dependencies\windows-installers.json'
$dependencyManifest = Read-WindowsDependencyManifest $dependencyManifestPath
$dependencyMinimums = [ordered]@{}
foreach ($entry in $dependencyManifest.dependencies.PSObject.Properties) {
    $dependencyMinimums[$entry.Name] = Get-DependencyMinimumVersion $entry.Value
}

$manifest = [ordered]@{
    schemaVersion = 1
    protocolVersion = [int]$contract.release.updaterProtocolVersion
    channel = 'stable'
    prerelease = $false
    version = $Version
    tag = $Tag
    commit = $Commit
    repository = $Repository
    releasePage = "https://github.com/$Repository/releases/tag/$Tag"
    keyId = [string]$contract.release.manifestSignature.keyId
    dependencyMinimums = $dependencyMinimums
    setup = (Get-ArtifactRecord $SetupPath 'StreamlinkVlcStudio-Setup.exe')
    zip = (Get-ArtifactRecord $ZipPath 'StreamlinkVlcStudio-release.zip')
}

$output = Resolve-ReleasePath $OutputDirectory
[IO.Directory]::CreateDirectory($output) | Out-Null
$manifestPath = Join-Path $output 'UPDATE-MANIFEST.json'
$signaturePath = Join-Path $output 'UPDATE-MANIFEST.sig'
$utf8 = [Text.UTF8Encoding]::new($false)
$bytes = $utf8.GetBytes(($manifest | ConvertTo-Json -Depth 8 -Compress))

$rsa = [Security.Cryptography.RSA]::Create()
try {
    $pem = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $PrivateKeyPath))
    $rsa.ImportFromPem($pem)
    if ($rsa.KeySize -ne [int]$contract.release.manifestSignature.keyBits) { throw "Release signing key must be exactly $($contract.release.manifestSignature.keyBits) bits; found $($rsa.KeySize)." }
    $privateKeyId = Get-ReleaseKeyFingerprint ($rsa.ExportSubjectPublicKeyInfo())
    if ($privateKeyId -cne [string]$contract.release.manifestSignature.keyId -or
        $privateKeyId -cne (Get-ReleasePublicKeyId $PublicKeyPath)) {
        throw "Protected release private key does not match the committed update trust root. Expected $($contract.release.manifestSignature.keyId); found $privateKeyId."
    }
    $signature = $rsa.SignData(
        $bytes,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pss)
} finally {
    $rsa.Dispose()
}

$stage = Join-Path $output ('.update-manifest-' + [Guid]::NewGuid().ToString('N'))
$stagedManifest = "$stage.json"
$stagedSignature = "$stage.sig"
try {
    [IO.File]::WriteAllBytes($stagedManifest, $bytes)
    [IO.File]::WriteAllBytes($stagedSignature, $signature)
    & (Join-Path $scriptRoot 'verify-update-manifest.ps1') `
        -ManifestPath $stagedManifest `
        -SignaturePath $stagedSignature `
        -ContractPath $contractFile `
        -PublicKeyPath $PublicKeyPath `
        -SetupPath $SetupPath `
        -ZipPath $ZipPath `
        -ExpectedVersion $Version `
        -ExpectedTag $Tag `
        -ExpectedCommit $Commit `
        -ExpectedRepository $Repository `
        -DependencyManifestPath $dependencyManifestPath

    $promotionLock = [IO.FileStream]::new((Join-Path $output '.update-manifest.lock'),
        [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
    try {
        Promote-ValidatedFileSetAtomically @(
            @{ Source = $stagedManifest; Destination = $manifestPath }
            @{ Source = $stagedSignature; Destination = $signaturePath }
        )
    } finally { $promotionLock.Dispose() }
} finally {
    [IO.File]::Delete($stagedManifest)
    [IO.File]::Delete($stagedSignature)
}

Write-Output $manifestPath
Write-Output $signaturePath
