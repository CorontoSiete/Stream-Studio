[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BaselineExecutable,
    [Parameter(Mandatory = $true)][string]$CandidateExecutable,
    [Parameter(Mandatory = $true)][string]$ProbePath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateRange(6, 200)][int]$Iterations = 24,
    [switch]$VerifyResources
)

$ErrorActionPreference = 'Stop'
$executables = @{
    baseline = (Resolve-Path -LiteralPath $BaselineExecutable).Path
    candidate = (Resolve-Path -LiteralPath $CandidateExecutable).Path
}
$probe = (Resolve-Path -LiteralPath $ProbePath).Path
$root = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($executable in $executables.Values) {
    $publishedRoot = [IO.Path]::TrimEndingDirectorySeparator((Split-Path $executable -Parent))
    if ($root.Equals($publishedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $root.StartsWith($publishedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Measurement output must be outside both published application directories.'
    }
}
if (Test-Path -LiteralPath $root) { throw 'Use a new output directory so the first launch has a fresh extraction cache.' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$samples = [Collections.Generic.List[object]]::new()
$reports = @{}

function Invoke-PackageProbe([string]$Variant, [bool]$FreshExtraction) {
    $info = [Diagnostics.ProcessStartInfo]::new($executables[$Variant])
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Environment['DOTNET_STARTUP_HOOKS'] = $probe
    $info.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $root "$Variant-extracted"
    $info.Environment['SVS_UPDATE_PROBE_PACKAGE_RESOURCES'] = $VerifyResources.IsPresent.ToString().ToLowerInvariant()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Packaged startup probe timed out.' }
        $watch.Stop()
        $errorText = $errors.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "$Variant probe failed: $errorText" }
        $report = $output.GetAwaiter().GetResult() | ConvertFrom-Json
        if ($report.ProcessPath -ine $info.FileName -or $report.ConfiguredDirectory -ine (Split-Path $info.FileName -Parent)) {
            throw 'The packaged updater resolved the wrong application directory.'
        }
        if ($FreshExtraction) { $reports[$Variant] = $report }
        $samples.Add([pscustomobject]@{Variant=$Variant; FreshExtraction=$FreshExtraction; ElapsedMs=$watch.Elapsed.TotalMilliseconds})
    } finally { $process.Dispose() }
}

foreach ($variant in @('baseline', 'candidate')) { Invoke-PackageProbe $variant $true }
for ($index = 0; $index -lt $Iterations; $index++) {
    # Alternate which package runs first to reduce ordering bias.
    $order = if ($index % 2 -eq 0) { @('baseline','candidate') } else { @('candidate','baseline') }
    foreach ($variant in $order) { Invoke-PackageProbe $variant $false }
}
if ($VerifyResources -and
    ($reports.baseline.Package.ResourceDigests | ConvertTo-Json -Compress) -cne
    ($reports.candidate.Package.ResourceDigests | ConvertTo-Json -Compress)) {
    throw 'Embedded resources differ between the two packages.'
}
$sizes = foreach ($variant in @('baseline', 'candidate')) {
    $published = @(Get-ChildItem -LiteralPath (Split-Path $executables[$variant] -Parent) -File -Recurse -ErrorAction Stop)
    $extractionRoot = Join-Path $root "$variant-extracted"
    $extracted = @(if (Test-Path -LiteralPath $extractionRoot) {
        Get-ChildItem -LiteralPath $extractionRoot -File -Recurse -ErrorAction Stop
    })
    $warm = @($samples | Where-Object { $_.Variant -eq $variant -and -not $_.FreshExtraction } | ForEach-Object ElapsedMs | Sort-Object)
    $publishedBytes = ($published | Measure-Object Length -Sum).Sum
    $extractedBytes = if ($extracted.Count -eq 0) { 0L } else { ($extracted | Measure-Object Length -Sum).Sum }
    [pscustomobject]@{
        Variant = $variant
        ExecutableSha256 = (Get-FileHash -LiteralPath $executables[$variant] -Algorithm SHA256).Hash
        PublishedBytes = $publishedBytes
        ExtractedBytes = $extractedBytes
        ExtractedFiles = $extracted.Count
        TotalBytes = $publishedBytes + $extractedBytes
        FreshExtractionMs = ($samples | Where-Object { $_.Variant -eq $variant -and $_.FreshExtraction }).ElapsedMs
        MedianWarmMs = ($warm[[math]::Floor(($warm.Count-1)/2)] + $warm[[math]::Floor($warm.Count/2)]) / 2
        P95WarmMs = $warm[[math]::Ceiling($warm.Count*0.95)-1]
    }
}
[pscustomobject]@{
    MeasuredAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    OperatingSystem = [Environment]::OSVersion.VersionString
    Probe = $(if ($VerifyResources) { 'updater, all embedded resources, eight palettes and three rendered WPF controls' } else { 'updater startup hook' })
    Sizes = @($sizes)
    Samples = @($samples.ToArray())
    Reports = $reports
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'measurement.json') -Encoding utf8
$sizes | Format-List
