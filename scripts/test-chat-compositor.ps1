param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [Parameter(Mandatory = $true)][string]$VlcIncludeDirectory,
    [Parameter(Mandatory = $true)][string]$VlcLibraryDirectory,
    [Parameter(Mandatory = $true)][string]$VlcDirectory,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/native-test.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '../.tmp/chat-compositor-tests' }
Invoke-VlcNativeTest -Gcc $Gcc -VlcIncludeDirectory $VlcIncludeDirectory -VlcLibraryDirectory $VlcLibraryDirectory `
    -VlcDirectory $VlcDirectory -OutputDirectory $OutputDirectory -Name 'compositor' `
    -Libraries @('-l:libvlc.dll', '-lvlccore', '-lgdi32', '-lmsimg32')
