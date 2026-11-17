[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'scripts\lib\common.ps1')

function Assert-Workflow([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-WorkflowRunScripts([string]$Path) {
    $lines = [IO.File]::ReadAllLines($Path)
    $stepName = ''
    for ($index = 0; $index -lt $lines.Length; $index++) {
        if ($lines[$index] -match '^      - name: (.+)$') { $stepName = $Matches[1] }
        if ($lines[$index] -notmatch '^        run:\s*(.*)$') { continue }
        $value = $Matches[1]
        if ($value -match '^[|>]') {
            $folded = $value.StartsWith('>')
            $scriptLines = [Collections.Generic.List[string]]::new()
            for ($index++; $index -lt $lines.Length; $index++) {
                if ([string]::IsNullOrWhiteSpace($lines[$index])) { $scriptLines.Add(''); continue }
                if (-not $lines[$index].StartsWith('          ')) { break }
                $scriptLines.Add($lines[$index].Substring(10))
            }
            $index--
            $value = $scriptLines -join $(if ($folded) { ' ' } else { "`n" })
        }
        [pscustomobject]@{ Name = $stepName; Script = $value }
    }
}

$workflowPath = Join-Path $repoRoot '.github\workflows\build.yml'
$versionSteps = @(Get-WorkflowRunScripts $workflowPath | Where-Object { $_.Name -ceq 'Resolve one build-wide version' })
Assert-Workflow ($versionSteps.Count -eq 1) 'The build workflow must resolve one version before packaging.'
$versionScript = [string]$versionSteps[0].Script
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('StreamStudio-workflow-tests-' + [Guid]::NewGuid().ToString('N'))
$environmentPath = Join-Path $testRoot 'github-env.txt'
$savedEnvironment = @{}
foreach ($name in @('GITHUB_REF', 'GITHUB_REF_NAME', 'GITHUB_ENV', 'GITHUB_ACTIONS', 'RUNNER_OS', 'GITHUB_REPOSITORY')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$previousExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$hadExitCode = $null -ne $previousExitCode
$savedExitCode = if ($hadExitCode) { $previousExitCode.Value } else { $null }

function Invoke-WorkflowVersion([string]$Ref, [string]$RefName) {
    $env:GITHUB_REF = $Ref
    $env:GITHUB_REF_NAME = $RefName
    $env:GITHUB_ENV = $environmentPath
    [IO.File]::WriteAllText($environmentPath, '')
    # Render the original expression-based workflow too, so the regression reproduces
    # interpretation of a tag before its release-contract validation can reject it.
    $rendered = $versionScript.Replace('${{ github.ref }}', $Ref).Replace('${{ github.ref_name }}', $RefName)
    $workflowProbeRan = $false
    $failure = $null
    try { . ([scriptblock]::Create($rendered)) *> $null }
    catch { $failure = $_.Exception }
    $metadata = @{}
    foreach ($line in [IO.File]::ReadAllLines($environmentPath)) {
        $parts = $line -split '=', 2
        if ($parts.Length -eq 2) { $metadata[$parts[0]] = $parts[1] }
    }
    [pscustomobject]@{ ProbeRan = $workflowProbeRan; Failure = $failure; Metadata = $metadata }
}

New-Item -ItemType Directory -Path $testRoot | Out-Null
Push-Location $repoRoot
try {
    $probeTag = 'v9.8.7''+$($workflowProbeRan=$true)+'''
    git check-ref-format "refs/tags/$probeTag"
    Assert-Workflow ($LASTEXITCODE -eq 0) 'The expression fixture must be a valid Git tag name.'
    $result = Invoke-WorkflowVersion "refs/tags/$probeTag" $probeTag
    Assert-Workflow (-not $result.ProbeRan) 'A Git tag was interpreted as PowerShell code before release validation.'
    Assert-Workflow ($null -ne $result.Failure) 'An invalid stable-release tag was accepted.'
    Assert-Workflow ($result.Metadata.Count -eq 0) 'Rejected release input wrote build metadata.'
    Write-Host 'PASS workflow: invalid tags remain data and fail before expression evaluation'

    $result = Invoke-WorkflowVersion "refs/heads/$probeTag" $probeTag
    Assert-Workflow (-not $result.ProbeRan -and $null -eq $result.Failure) 'A quoted branch changed the workflow script.'
    [xml]$properties = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
    $expectedVersion = $properties.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText
    Assert-Workflow ($result.Metadata['RELEASE_VERSION'] -ceq $expectedVersion -and
        $result.Metadata['RELEASE_TAG'] -ceq '' -and $result.Metadata['IS_STABLE_TAG'] -ceq 'false') `
        'Branch validation did not retain the configured build version.'
    Write-Host 'PASS workflow: quoted branches remain data and preserve the build version'

    $result = Invoke-WorkflowVersion 'refs/tags/v9.8.7' 'v9.8.7'
    Assert-Workflow ($null -eq $result.Failure -and $result.Metadata['RELEASE_VERSION'] -ceq '9.8.7' -and
        $result.Metadata['RELEASE_TAG'] -ceq 'v9.8.7' -and $result.Metadata['IS_STABLE_TAG'] -ceq 'true') `
        'Stable release metadata no longer preserves the exact tag version.'
    Write-Host 'PASS workflow: stable tags produce exact versioned build metadata'

    $workflowText = [IO.File]::ReadAllText($workflowPath)
    $updaterText = [IO.File]::ReadAllText((Join-Path $repoRoot 'src\StreamlinkVlcStudio.Infrastructure\Updates\StagedAppUpdateService.cs'))
    $trustedRepository = [regex]::Match($updaterText, 'private const string Repository = "([^"]+)";').Groups[1].Value
    $releaseRepository = [regex]::Match($workflowText, '(?m)^      RELEASE_REPOSITORY: ([^\r\n]+)$').Groups[1].Value
    Assert-Workflow ($trustedRepository -ne '' -and $releaseRepository -ceq $trustedRepository) `
        'Signed releases must retain the repository identity trusted by installed updaters after a GitHub rename.'
    Write-Host 'PASS workflow: signed release identity stays compatible with installed updaters'

    foreach ($upgradeScriptName in @('test-published-upgrade.ps1', 'test-published-zip-upgrade.ps1')) {
        $tokens = $null
        $parseErrors = $null
        $upgradeScript = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $repoRoot "scripts/$upgradeScriptName"), [ref]$tokens, [ref]$parseErrors)
        $runnerGuard = $upgradeScript.EndBlock.Statements | Where-Object {
            $_ -is [Management.Automation.Language.IfStatementAst]
        } | Select-Object -First 1
        Assert-Workflow ($null -ne $runnerGuard) 'The installer verification runner restriction is missing.'
        foreach ($fixture in @(
                @{ Actions = 'true'; OS = 'Windows'; Repository = 'CorontoSiete/streamlink-vlc-studio'; Allowed = $true },
                @{ Actions = 'true'; OS = 'Windows'; Repository = 'CorontoSiete/Stream-Studio'; Allowed = $true },
                @{ Actions = 'true'; OS = 'Windows'; Repository = 'Other/Stream-Studio'; Allowed = $false },
                @{ Actions = 'false'; OS = 'Windows'; Repository = 'CorontoSiete/Stream-Studio'; Allowed = $false },
                @{ Actions = 'true'; OS = 'Linux'; Repository = 'CorontoSiete/Stream-Studio'; Allowed = $false })) {
            $env:GITHUB_ACTIONS = $fixture.Actions
            $env:RUNNER_OS = $fixture.OS
            $env:GITHUB_REPOSITORY = $fixture.Repository
            $allowed = $true
            try { & ([scriptblock]::Create($runnerGuard.Extent.Text)) }
            catch { $allowed = $false }
            Assert-Workflow ($allowed -eq $fixture.Allowed) 'Installer verification accepted an unsafe runner or rejected the renamed repository.'
        }
    }
    Write-Host 'PASS workflow: installation verification allows both repository names only on disposable Windows Actions'

    foreach ($workflow in Get-ChildItem -LiteralPath (Join-Path $repoRoot '.github\workflows') -Filter '*.yml' -File) {
        foreach ($step in Get-WorkflowRunScripts $workflow.FullName) {
            Assert-Workflow (-not $step.Script.Contains('${{')) "GitHub context is embedded in script source: $($workflow.Name) / $($step.Name)."
            $tokens = $null
            $parseErrors = $null
            [Management.Automation.Language.Parser]::ParseInput($step.Script, [ref]$tokens, [ref]$parseErrors) | Out-Null
            Assert-Workflow ($parseErrors.Count -eq 0) "Workflow PowerShell does not parse: $($workflow.Name) / $($step.Name)."
        }
    }
    Write-Host 'PASS workflow: embedded PowerShell parses and receives GitHub metadata through environment variables'
} finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    if ($hadExitCode) { Set-Variable -Name LASTEXITCODE -Scope Global -Value $savedExitCode }
    else { Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
    Pop-Location
    Remove-DirectoryTreeSafely $testRoot
}
