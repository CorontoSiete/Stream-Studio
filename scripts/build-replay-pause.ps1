param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [Parameter(Mandatory = $true)][string]$VlcIncludeDirectory,
    [Parameter(Mandatory = $true)][string]$VlcLibraryDirectory,
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/vlc-compiler.ps1')
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) {
    $OutputPath = Join-Path $repositoryRoot 'src\StreamlinkVlcStudio.Infrastructure\Vlc\BundledReplayPause\libstudio_replay_pause_plugin.dll'
}
$OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$Gcc = Resolve-VlcCompiler -Gcc $Gcc
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
& $Gcc -shared -O2 -Wall -Wextra -Werror -fno-strict-aliasing -static-libgcc `
    -D__PLUGIN__ -DPIC -DWIN32 -D_WIN32_WINNT=0x0601 `
    -I $VlcIncludeDirectory -L $VlcLibraryDirectory `
    (Join-Path $repositoryRoot 'native\replay-pause\replay_pause.c') `
    -o $OutputPath -lvlccore '-Wl,--no-insert-timestamp,--image-base,0x180000000,--strip-all'
if ($LASTEXITCODE -ne 0) { throw "Replay pause plugin build failed ($LASTEXITCODE)." }
Assert-VlcBinaryRuntime -Gcc $Gcc -Path $OutputPath
Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256
