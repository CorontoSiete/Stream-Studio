param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [Parameter(Mandatory = $true)][string]$VlcIncludeDirectory,
    [Parameter(Mandatory = $true)][string]$VlcLibraryDirectory,
    [Parameter(Mandatory = $true)][string]$VlcDirectory,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/native-test.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '../.tmp/chat-subpicture-tests' }
foreach ($name in @('subpictures', 'received-frames')) {
    Invoke-VlcNativeTest -Gcc $Gcc -VlcIncludeDirectory $VlcIncludeDirectory -VlcLibraryDirectory $VlcLibraryDirectory `
        -VlcDirectory $VlcDirectory -OutputDirectory $OutputDirectory -Name $name -Libraries @('-lvlccore', '-luser32')
}
