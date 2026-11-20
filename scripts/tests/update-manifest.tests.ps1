[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'scripts\lib\common.ps1')
. (Join-Path $repoRoot 'scripts\lib\dependency-manifest.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('StreamStudio-signing-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$key = [Security.Cryptography.RSA]::Create(3072)
function Assert-SigningFailure([scriptblock]$Action, [string]$Pattern) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw "Unexpected signing failure: $($_.Exception.Message)" }
        return
    }
    throw "Expected signing failure matching '$Pattern'."
}
try {
    # Use an isolated ephemeral key and contract. Production signing keys and the
    # repository's trust root are never read as private keys or modified.
    $contract = Get-Content -LiteralPath (Join-Path $repoRoot 'shared\release-contract.json') -Raw | ConvertFrom-Json
    $contract.release.manifestSignature.keyId = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($key.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    $contract.release.manifestSignature.publicKey = 'fixture-public.pem'
    $contractPath = Join-Path $testRoot 'release-contract.json'
    $contract | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $contractPath -Encoding utf8NoBOM
    $privatePath = Join-Path $testRoot 'fixture-private.pem'
    $publicPath = Join-Path $testRoot 'fixture-public.pem'
    [IO.File]::WriteAllText($privatePath, $key.ExportPkcs8PrivateKeyPem())
    [IO.File]::WriteAllText($publicPath, $key.ExportSubjectPublicKeyInfoPem())
    $setup = Join-Path $testRoot 'StreamlinkVlcStudio-Setup.exe'
    $zip = Join-Path $testRoot 'StreamlinkVlcStudio-release.zip'
    [IO.File]::WriteAllText($setup, 'fixture installer')
    [IO.File]::WriteAllText($zip, 'fixture ZIP')
    $generator = Join-Path $repoRoot 'scripts\new-update-manifest.ps1'
    $generation = @{
        Version = '1.8.3'; Tag = 'v1.8.3'; Commit = ('a' * 40); SetupPath = $setup; ZipPath = $zip
        PrivateKeyPath = $privatePath; ContractPath = $contractPath; PublicKeyPath = $publicPath; OutputDirectory = $testRoot
    }
    Push-Location -LiteralPath $testRoot
    try {
        $generation.ContractPath = 'release-contract.json'
        $generation.OutputDirectory = '.'
        $generation.Remove('PublicKeyPath')
        & $generator @generation | Out-Null
    } finally {
        Pop-Location
        $generation.ContractPath = $contractPath
        $generation.OutputDirectory = $testRoot
    }
    $manifestPath = Join-Path $testRoot 'UPDATE-MANIFEST.json'
    $signaturePath = Join-Path $testRoot 'UPDATE-MANIFEST.sig'
    $original = [IO.File]::ReadAllText($manifestPath)
    $manifest = $original | ConvertFrom-Json
    if ($manifest.dependencyMinimums.webview2 -cne '152.0.4191.53' -or
        @($manifest.dependencyMinimums.PSObject.Properties).Count -ne 3) {
        throw 'The signed release did not include the SDK-required WebView2 minimum.'
    }
    $verification = @{
        ManifestPath = $manifestPath; SignaturePath = $signaturePath; ContractPath = $contractPath
        PublicKeyPath = $publicPath; SetupPath = $setup; ZipPath = $zip
        DependencyManifestPath = Join-Path $repoRoot 'dependencies\windows-installers.json'
    }
    $verifier = Join-Path $repoRoot 'scripts\verify-update-manifest.ps1'
    Push-Location -LiteralPath $testRoot
    try {
        $verification.ContractPath = 'release-contract.json'
        $verification.PublicKeyPath = 'fixture-public.pem'
        & $verifier @verification
    } finally {
        Pop-Location
        $verification.ContractPath = $contractPath
        $verification.PublicKeyPath = $publicPath
    }
    Write-Host 'PASS update signing: relative paths follow the current PowerShell directory'
    Write-Host 'PASS update signing: generated signature authenticates all three runtime minima and exact package bytes'
    $verification.PublicKeyPath = Join-Path $testRoot 'untrusted-public.pem'
    $untrustedKey = [Security.Cryptography.RSA]::Create(3072)
    try {
        [IO.File]::WriteAllText($verification.PublicKeyPath, $untrustedKey.ExportSubjectPublicKeyInfoPem())
        Assert-SigningFailure { & $verifier @verification } 'verification key does not match the update trust root'
    } finally {
        $untrustedKey.Dispose()
        $verification.PublicKeyPath = $publicPath
    }
    Write-Host 'PASS update signing: a verification key override must match the contract fingerprint'
    $generation.OutputDirectory = ''
    Assert-SigningFailure { & $generator @generation } 'A release path is required'
    $generation.OutputDirectory = $testRoot
    $generation.Commit = 'b' * 40
    $generation.PrivateKeyPath = Join-Path $testRoot 'wrong-private.pem'
    $wrongKey = [Security.Cryptography.RSA]::Create(2048)
    try { [IO.File]::WriteAllText($generation.PrivateKeyPath, $wrongKey.ExportPkcs8PrivateKeyPem()) } finally { $wrongKey.Dispose() }
    Assert-SigningFailure { & $generator @generation } 'signing key must be exactly'
    $generation.PrivateKeyPath = $privatePath
    [IO.File]::WriteAllText($zip, '')
    Assert-SigningFailure { & $generator @generation } 'non-empty'
    [IO.File]::WriteAllText($zip, 'fixture ZIP')
    if ([IO.File]::ReadAllText($manifestPath) -cne $original) { throw 'Failed signing replaced the previous manifest.' }
    & $verifier @verification
    Write-Host 'PASS update signing: signing and package failures preserve the previous verified manifest and signature'
    foreach ($mutation in @(
            { param($data) $data.dependencyMinimums.PSObject.Properties.Remove('webview2') },
            { param($data) $data.dependencyMinimums.webview2 = '140.0.0.0' },
            { param($data) $data.dependencyMinimums | Add-Member -NotePropertyName undeclared -NotePropertyValue '1.0.0' })) {
        $manifest = $original | ConvertFrom-Json
        & $mutation $manifest
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 10 -Compress))
        [IO.File]::WriteAllBytes($manifestPath, $bytes)
        [IO.File]::WriteAllBytes($signaturePath, $key.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss))
        Assert-SigningFailure { & $verifier @verification } 'dependency.*does not match|every dependency'
    }
    Write-Host 'PASS update signing: even valid signatures cannot omit lower or add undeclared payload requirements'
    # Legacy published releases remain verifiable without the new-build expected
    # manifest. Their downloaded ZIP is still bound to its signature and hash.
    $manifest = $original | ConvertFrom-Json
    $manifest.dependencyMinimums.PSObject.Properties.Remove('webview2')
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 10 -Compress))
    [IO.File]::WriteAllBytes($manifestPath, $bytes)
    [IO.File]::WriteAllBytes($signaturePath, $key.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss))
    $verification.Remove('DependencyManifestPath')
    & $verifier @verification
    $bytes[0] = $bytes[0] -bxor 1
    [IO.File]::WriteAllBytes($manifestPath, $bytes)
    Assert-SigningFailure { & $verifier @verification } 'signature is invalid'
    Write-Host 'PASS update signing: legacy two-dependency releases are supported and unsigned tampering is rejected'
} finally {
    $key.Dispose()
    Assert-UnderDirectory -ChildPath $testRoot -ParentPath ([IO.Path]::GetTempPath())
    if (-not [IO.Path]::GetFileName($testRoot).StartsWith('StreamStudio-signing-tests-')) { throw 'Unsafe signing test cleanup path.' }
    Remove-DirectoryTreeSafely $testRoot
}
