param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [string]$OutputDirectory = '',
    [string]$BaselineSource = '',
    [ValidateSet('', 'quiet', 'busy', 'animated')][string]$Benchmark = '',
    [ValidateRange(1, 100000)][int]$Frames = 400,
    [ValidateRange(0, 32767)][int]$CatalogEntries = 4000,
    [ValidateSet('', 'references', 'capacity', 'pipe')][string]$Filter = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/native-test.ps1')
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot '.tmp/chat-render-resources' }
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$binary = Join-Path $OutputDirectory 'render-resources.exe'
$compilerOptions = @('-fmax-errors=4')
if ($BaselineSource) {
    $baseline = (Resolve-Path -LiteralPath $BaselineSource).Path.Replace('\', '/')
    $baselineHeader = Join-Path $OutputDirectory 'baseline-source.h'
    [IO.File]::WriteAllText($baselineHeader,
        "#define RESOURCE_BASELINE`n" + '#define OVERLAY_RENDERER_SOURCE "' + $baseline + "`"`n",
        [Text.UTF8Encoding]::new($false))
    $compilerOptions += @('-include', $baselineHeader)
}
Build-ChatOverlayNativeTest -Gcc $Gcc -Name 'render-resources' -OutputPath $binary -CompilerOptions $compilerOptions
$testArguments = @()
if ($Benchmark) { $testArguments = @('--benchmark', $Benchmark, $Frames, $CatalogEntries) }
elseif ($Filter) { $testArguments = @("--$Filter") }
Invoke-NativeTestCommand -FilePath $binary -Arguments $testArguments -FailureMessage 'Native render resource tests failed'
