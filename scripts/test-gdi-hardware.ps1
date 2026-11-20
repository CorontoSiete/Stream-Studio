param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [Parameter(Mandatory = $true)][string]$VlcIncludeDirectory,
    [Parameter(Mandatory = $true)][string]$VlcLibraryDirectory,
    [Parameter(Mandatory = $true)][string]$VlcDirectory,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/native-test.ps1')
. (Join-Path $PSScriptRoot 'lib/vlc-compiler.ps1')
$Gcc = Resolve-VlcCompiler -Gcc $Gcc
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '../.tmp/gdi-hardware-tests' }
Invoke-VlcNativeTest -Gcc $Gcc -VlcIncludeDirectory $VlcIncludeDirectory -VlcLibraryDirectory $VlcLibraryDirectory `
    -VlcDirectory $VlcDirectory -OutputDirectory $OutputDirectory -Name 'hardware' `
    -Libraries @('-l:libvlc.dll', '-lvlccore', '-lgdi32', '-lmsimg32', '-ld3d9', '-ldxva2', '-ldxguid', '-luuid')
