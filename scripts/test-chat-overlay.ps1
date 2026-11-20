param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\native-test.ps1')
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot '.tmp/chat-overlay-tests' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
foreach ($name in @('tls-handshake', 'network', 'readability', 'render-resources')) {
    $binary = Join-Path $OutputDirectory ($name + '.exe')
    Build-ChatOverlayNativeTest -Gcc $Gcc -Name $name -OutputPath $binary
    Invoke-NativeTestCommand -FilePath $binary -Arguments @($OutputDirectory) -FailureMessage "Native $name tests failed"
}
