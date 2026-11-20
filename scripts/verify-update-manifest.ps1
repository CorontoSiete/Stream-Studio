[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$SignaturePath,
    [string]$ContractPath,
    [string]$PublicKeyPath,
    [string]$SetupPath,
    [string]$ZipPath,
    [string]$ExpectedVersion,
    [string]$ExpectedTag,
    [string]$ExpectedCommit,
    [string]$ExpectedRepository = 'CorontoSiete/streamlink-vlc-studio',
    [string]$DependencyManifestPath,
    [switch]$PassThru
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

$PublicKeyPath = Resolve-ReleasePath $PublicKeyPath (Get-ReleaseContractPublicKeyPath $contractFile $contract)
foreach ($path in @($ManifestPath, $SignaturePath, $PublicKeyPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Update-manifest verification input is missing: $path"
    }
}
if ([string]::IsNullOrWhiteSpace($SetupPath) -xor [string]::IsNullOrWhiteSpace($ZipPath)) {
    throw 'SetupPath and ZipPath must either both be supplied or both be omitted.'
}
$expectedKeyId = [string]$contract.release.manifestSignature.keyId

$manifestBytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ManifestPath))
if ($manifestBytes.Length -eq 0 -or
    ($manifestBytes.Length -ge 3 -and $manifestBytes[0] -eq 0xEF -and $manifestBytes[1] -eq 0xBB -and $manifestBytes[2] -eq 0xBF)) {
    throw 'The update manifest must be non-empty UTF-8 without a byte-order mark.'
}
$signatureBytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $SignaturePath))
$expectedSignatureLength = [int]$contract.release.manifestSignature.keyBits / 8
if ($signatureBytes.Length -ne $expectedSignatureLength) {
    throw "Update-manifest signature must be exactly $expectedSignatureLength bytes; found $($signatureBytes.Length)."
}

$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText((Resolve-Path -LiteralPath $PublicKeyPath)))
    if ($rsa.KeySize -ne [int]$contract.release.manifestSignature.keyBits) {
        throw "Release verification key must be exactly $($contract.release.manifestSignature.keyBits) bits; found $($rsa.KeySize)."
    }
    $verificationKeyId = Get-ReleaseKeyFingerprint ($rsa.ExportSubjectPublicKeyInfo())
    if ($verificationKeyId -cne $expectedKeyId) {
        throw 'Release verification key does not match the update trust root.'
    }
    if (-not $rsa.VerifyData(
            $manifestBytes,
            $signatureBytes,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)) {
        throw 'The RSA-PSS/SHA-256 update-manifest signature is invalid.'
    }
} finally {
    $rsa.Dispose()
}

$manifestText = [Text.UTF8Encoding]::new($false, $true).GetString($manifestBytes)
$manifest = $manifestText | ConvertFrom-Json
$identity = Get-StableReleaseIdentity -Tag ([string]$manifest.tag) -MinimumVersion ([string]$contract.release.minimumVersion)
if ($manifest.schemaVersion -ne 1 -or
    [int]$manifest.protocolVersion -ne [int]$contract.release.updaterProtocolVersion -or
    [string]$manifest.channel -cne 'stable' -or
    [bool]$manifest.prerelease -ne $false -or
    [string]$manifest.version -cne $identity.VersionText -or
    [string]$manifest.keyId -cne $expectedKeyId -or
    [string]$manifest.repository -cne $ExpectedRepository -or
    [string]$manifest.commit -cnotmatch '^[0-9a-f]{40}\z' -or
    [string]$manifest.releasePage -cne "https://github.com/$ExpectedRepository/releases/tag/$($identity.Tag)") {
    throw 'The signed update manifest contains inconsistent release metadata.'
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and [string]$manifest.version -cne $ExpectedVersion) {
    throw "Update-manifest version mismatch. Expected $ExpectedVersion; found $($manifest.version)."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedTag) -and [string]$manifest.tag -cne $ExpectedTag) {
    throw "Update-manifest tag mismatch. Expected $ExpectedTag; found $($manifest.tag)."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and [string]$manifest.commit -cne $ExpectedCommit.ToLowerInvariant()) {
    throw "Update-manifest commit mismatch. Expected $ExpectedCommit; found $($manifest.commit)."
}

foreach ($dependency in @('streamlink', 'vlc')) {
    $property = $manifest.dependencyMinimums.PSObject.Properties[$dependency]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "Update manifest omits the $dependency dependency minimum."
    }
}
foreach ($property in $manifest.dependencyMinimums.PSObject.Properties) {
    $parsedMinimum = ConvertTo-DependencyVersion ([string]$property.Value)
    if ([string]$property.Value -notmatch '^\d+(?:\.\d+){1,3}(?:-\d+)?$' -or
        $null -eq $parsedMinimum -or $parsedMinimum -le [version]'0.0.0.0') {
        throw "Update manifest has an invalid dependency minimum for '$($property.Name)'."
    }
}
if (-not [string]::IsNullOrWhiteSpace($DependencyManifestPath)) {
    Assert-DependencyMinimums $manifest.dependencyMinimums (Read-WindowsDependencyManifest $DependencyManifestPath)
}

function Assert-ManifestAsset {
    param($Asset, [string]$ExpectedName, [AllowEmptyString()][string]$LocalPath)

    [long]$length = 0
    if ([string]$Asset.name -cne $ExpectedName -or
        -not [long]::TryParse(
            [Convert]::ToString($Asset.length, [Globalization.CultureInfo]::InvariantCulture),
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$length) -or
        $length -le 0 -or $length -gt 1GB -or
        [string]$Asset.sha256 -cnotmatch '^[0-9a-f]{64}\z') {
        throw "Signed update asset metadata is invalid for $ExpectedName."
    }
    if (-not [string]::IsNullOrWhiteSpace($LocalPath)) {
        $local = Get-ArtifactRecord $LocalPath $ExpectedName
        if ($local.length -ne $length -or $local.sha256 -cne [string]$Asset.sha256) {
            throw "Signed update asset does not match its exact name, length, and SHA-256: $LocalPath"
        }
    }
}

Assert-ManifestAsset $manifest.setup 'StreamlinkVlcStudio-Setup.exe' $SetupPath
Assert-ManifestAsset $manifest.zip 'StreamlinkVlcStudio-release.zip' $ZipPath

if ($PassThru) {
    $manifest
} else {
    Write-Host "Verified signed update manifest for $($identity.Tag) with key $expectedKeyId."
}
