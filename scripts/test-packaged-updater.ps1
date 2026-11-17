[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$ProbePath,
    [ValidateSet('', 'Managed', 'Zip', 'Unmanaged')][string]$ExpectedInstallKind = '',
    [switch]$VerifyBundleLayout
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$info = [Diagnostics.ProcessStartInfo]::new($executable)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
# Scope the startup hook to this one child. It exits before app startup or any installation.
$info.Environment['DOTNET_STARTUP_HOOKS'] = (Resolve-Path -LiteralPath $ProbePath).Path
$extractionRoot = $null
$probeRoot = $null
$process = $null
if ($VerifyBundleLayout) {
    $info.Environment['SVS_UPDATE_PROBE_PACKAGE_RESOURCES'] = 'true'
    # A fresh, isolated destination proves this build doesn't create runtime copies,
    # even when the machine already has an extraction cache from an earlier launch.
    $probeRoot = Join-Path ([IO.Path]::GetTempPath()) ('StreamStudio-bundle-probe-' + [Guid]::NewGuid().ToString('N'))
    $extractionRoot = Join-Path $probeRoot 'bundle'
    $info.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $extractionRoot
}
try {
    $process = [Diagnostics.Process]::Start($info)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Packaged updater probe timed out.' }
    $reportText = $output.GetAwaiter().GetResult()
    $errorText = $errors.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "Packaged updater probe failed: $errorText" }
    $report = $reportText | ConvertFrom-Json
    if ($report.ProcessPath -ine $executable -or
        [IO.Path]::TrimEndingDirectorySeparator($report.ConfiguredDirectory) -ine (Split-Path $executable -Parent)) {
        throw "The packaged updater is using the extraction cache instead of its apphost directory: $reportText"
    }
    if ($ExpectedInstallKind -ne '' -and $report.InstallKind -cne $ExpectedInstallKind) {
        throw "Packaged updater ownership mismatch. Expected $ExpectedInstallKind; got $($report.InstallKind)."
    }
    if ($VerifyBundleLayout) {
        if (@($report.Package.ManagedAssemblyLocations.PSObject.Properties).Count -ne 3 -or
            @($report.Package.ManagedAssemblyLocations.PSObject.Properties | Where-Object { $_.Value -ne '' }).Count -ne 0) {
            throw 'The package extracts managed assemblies instead of loading them directly from its executable.'
        }
        if ((Test-Path -LiteralPath $extractionRoot) -and
            @(Get-ChildItem -LiteralPath $extractionRoot -File -Recurse -Force -ErrorAction Stop).Count -ne 0) {
            throw 'The package duplicates runtime files in an extraction cache instead of loading the installed libraries.'
        }
        # The real updater stages only the apphost. Exercise its actual entry point
        # without a startup hook or native sidecars, rather than bypassing it again.
        $helperDirectory = Join-Path $probeRoot 'helper'
        [IO.Directory]::CreateDirectory($helperDirectory) | Out-Null
        $helperPath = Join-Path $helperDirectory ([IO.Path]::GetFileName($executable))
        [IO.File]::Copy($executable, $helperPath)
        $helperInfo = [Diagnostics.ProcessStartInfo]::new($helperPath)
        $helperInfo.UseShellExecute = $false
        $helperInfo.CreateNoWindow = $true
        $helperInfo.WorkingDirectory = $helperDirectory
        $helperInfo.RedirectStandardOutput = $true
        $helperInfo.RedirectStandardError = $true
        $helperInfo.Environment.Remove('DOTNET_STARTUP_HOOKS') | Out-Null
        $helperInfo.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $extractionRoot
        $helperInfo.ArgumentList.Add('--update-helper')
        $helperInfo.ArgumentList.Add('invalid')
        $helper = [Diagnostics.Process]::Start($helperInfo)
        try {
            $helperOutput = $helper.StandardOutput.ReadToEndAsync()
            $helperErrors = $helper.StandardError.ReadToEndAsync()
            if (-not $helper.WaitForExit(60000)) { $helper.Kill(); throw 'Standalone update helper timed out.' }
            $helperErrorText = $helperErrors.GetAwaiter().GetResult()
            if ($helper.ExitCode -ne 1 -or $helperErrorText -ne '' -or $helperOutput.GetAwaiter().GetResult() -ne '') {
                throw "The standalone update helper could not reject invalid arguments without UI libraries: $helperErrorText"
            }
            if ((Test-Path -LiteralPath $extractionRoot) -and
                @(Get-ChildItem -LiteralPath $extractionRoot -File -Recurse -Force -ErrorAction Stop).Count -ne 0) {
                throw 'The standalone update helper created a runtime extraction cache.'
            }
        } finally { $helper.Dispose() }
        Write-Host 'PASS staged update helper runs without native sidecars or UI initialization.'
        Write-Host "PASS package uses no runtime extraction cache and reads $($report.Package.WpfResourceCount) WPF resources."
    }
    Write-Host "PASS packaged updater uses its executable directory; installation kind: $($report.InstallKind)."
    Write-Host $reportText
} finally {
    if ($null -ne $process) { $process.Dispose() }
    if ($null -ne $probeRoot -and (Test-Path -LiteralPath $probeRoot)) {
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedExtractionRoot = [IO.Path]::GetFullPath($probeRoot)
        if (-not [IO.Path]::TrimEndingDirectorySeparator((Split-Path $resolvedExtractionRoot -Parent)).Equals(
                [IO.Path]::TrimEndingDirectorySeparator($temporaryRoot), [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($resolvedExtractionRoot).StartsWith('StreamStudio-bundle-probe-', [StringComparison]::Ordinal)) {
            throw 'Refusing to remove an unexpected bundle-probe directory.'
        }
        Remove-Item -LiteralPath $resolvedExtractionRoot -Recurse -Force
    }
}
