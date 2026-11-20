function Invoke-NativeTestCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $command = Get-Command -Name $FilePath -CommandType Application -ErrorAction Stop
    # Windows PowerShell wraps redirected stderr in error records, even when
    # the executable succeeded. Keep diagnostics and judge native tests by their
    # exit status. These preferences are local to this function.
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    & $command @Arguments 2>&1 | ForEach-Object {
        if ($_ -is [Management.Automation.ErrorRecord]) {
            if ($_.FullyQualifiedErrorId -notin @('NativeCommandError', 'NativeCommandErrorMessage')) {
                throw $_
            }
            $_.Exception.Message
        } else {
            $_.ToString()
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "$FailureMessage ($LASTEXITCODE)." }
}

function Build-ChatOverlayNativeTest {
    param(
        [Parameter(Mandatory = $true)][string]$Gcc,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [string[]]$CompilerOptions = @()
    )

    $sourceRoot = Join-Path $PSScriptRoot '../../native/chat-overlay'
    $sources = @((Join-Path $sourceRoot "tests/$Name.c"))
    if ($Name -ne 'tls-handshake') { $sources += Join-Path $sourceRoot 'tls.c' }
    Invoke-NativeTestCommand -FilePath $Gcc -FailureMessage "Native $Name test build failed" -Arguments (
        @('-O2', '-Wall', '-Wextra', '-Werror', '-static', '-static-libgcc') + $CompilerOptions + $sources +
        @('-o', $OutputPath, '-lws2_32', '-lsecur32', '-lcrypt32', '-lgdi32', '-luser32', '-lwinhttp',
            '-lole32', '-lgdiplus', '-ld2d1', '-ldwrite', '-luuid', '-lpsapi')
    )
}

function Invoke-VlcNativeTest {
    param(
        [Parameter(Mandatory = $true)][string]$Gcc,
        [Parameter(Mandatory = $true)][string]$VlcIncludeDirectory,
        [Parameter(Mandatory = $true)][string]$VlcLibraryDirectory,
        [Parameter(Mandatory = $true)][string]$VlcDirectory,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Libraries
    )

    $sourceRoot = Join-Path $PSScriptRoot '../../native/chat-overlay'
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $binary = Join-Path $OutputDirectory "$Name.exe"
    Invoke-NativeTestCommand -FilePath $Gcc -FailureMessage "Native $Name test build failed" -Arguments (
        @('-O2', '-Wall', '-Wextra', '-Werror', '-static-libgcc', '-D__PLUGIN__', '-DWIN32', '-D_WIN32_WINNT=0x0601',
            '-include', (Join-Path $sourceRoot 'quality-gdi/build-config.h'),
            '-I', $VlcIncludeDirectory, '-L', $VlcLibraryDirectory, '-L', $VlcDirectory,
            (Join-Path $sourceRoot "tests/$Name.c"), '-o', $binary) + $Libraries
    )

    $previousPath = $env:PATH
    try {
        $env:PATH = $VlcDirectory + ';' + $previousPath
        Invoke-NativeTestCommand -FilePath $binary -FailureMessage "Native $Name tests failed"
    } finally { $env:PATH = $previousPath }
}
