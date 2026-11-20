function Resolve-VlcCompiler {
    param([Parameter(Mandatory = $true)][string]$Gcc)

    $compiler = Get-Command -Name $Gcc -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $message = 'Use an x64 MinGW-w64 GCC targeting MSVCRT for VLC 3.0.'
    $target = & $compiler -dumpmachine
    if ($LASTEXITCODE -ne 0 -or $target -ne 'x86_64-w64-mingw32') { throw $message }

    # UCRT and MSVCRT compilers report the same target. VLC 3.0 and code
    # passing allocations across its plugin boundary must use the same CRT.
    $macros = '' | & $compiler -dM -E -x c -include _mingw.h -
    if ($LASTEXITCODE -ne 0 -or $macros -cmatch '^#define _UCRT(?:\s|$)') { throw $message }
    return $compiler.Source
}

function Assert-VlcBinaryRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$Gcc,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $objdump = Join-Path (Split-Path -Parent $Gcc) 'objdump.exe'
    $imports = & $objdump -p $Path
    if ($LASTEXITCODE -ne 0 -or -not ($imports -match '^\s*DLL Name:\s+msvcrt\.dll\s*$') -or
        $imports -match '^\s*DLL Name:\s+(?:ucrtbase|api-ms-win-crt|libgcc|libstdc\+\+|libwinpthread)') {
        throw "VLC build output must use MSVCRT without an additional compiler runtime DLL: $Path"
    }
}
