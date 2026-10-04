#!/usr/bin/env pwsh
<#[
.SYNOPSIS
    Fails on dependency advisory regressions against the last published release,
    or on failed lock/signature checks and mutable GitHub Action references.
#>

[CmdletBinding()]
param(
    [ValidateSet('server','android','tooling','all')][string]$Scope = 'server',
    [switch]$FrontendInstalled
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. "$PSScriptRoot/advisory-regression.ps1"

function Invoke-Checked {
    param(
        [Parameter(Mandatory=$true)][string]$FilePath,
        [Parameter(Mandatory=$true)][string[]]$ArgumentList,
        [Parameter(Mandatory=$true)][string]$WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try {
        & $FilePath @ArgumentList
        if ($LASTEXITCODE -ne 0) {
            throw "$FilePath exited with code $LASTEXITCODE in $WorkingDirectory"
        }
    }
    finally {
        Pop-Location
    }
}

Write-Host "Supply-chain gate: immutable GitHub Actions" -ForegroundColor Cyan
$workflowFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot ".github") -Recurse -File |
    Where-Object { $_.Extension -in ".yml", ".yaml" })
$mutableActions = foreach ($workflowFile in $workflowFiles) {
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $workflowFile.FullName) {
        $lineNumber++
        if ($line -match '^\s*uses:\s*([^\s#]+)') {
            $reference = $matches[1]
            if (-not $reference.StartsWith("./") -and $reference -notmatch '@[0-9a-fA-F]{40}$') {
                "$($workflowFile.FullName):$lineNumber $reference"
            }
        }
    }
}
if (@($mutableActions).Count -gt 0) {
    throw "Mutable GitHub Action references found:`n$($mutableActions -join "`n")"
}
$baseline = Get-AdvisoryBaseline $repoRoot

if ($Scope -in @('server','tooling','all')) {
    Write-Host "Supply-chain gate: npm advisories and registry signatures" -ForegroundColor Cyan
    $requiredNpmVersion = "11.18.0"
    $actualNpmVersion = (& npm --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualNpmVersion -ne $requiredNpmVersion) {
        throw "npm $requiredNpmVersion is required for strict lifecycle-script allowlisting; found $actualNpmVersion."
    }
    $npmWorkspaces = @()
    if ($Scope -in @('server','all')) {
        $npmWorkspaces += Join-Path $repoRoot 'src/Ai.Tlbx.MidTerm'
        $npmWorkspaces += Join-Path $repoRoot 'src/Ai.Tlbx.MidTerm.AgentHost/ClaudeBridge'
    }
    if ($Scope -in @('tooling','all')) {
        $npmWorkspaces += Join-Path $repoRoot 'docs/marketing/ScreenshotAutomation'
    }
    foreach ($workspace in $npmWorkspaces) {
        $ciArguments = if ($workspace.EndsWith("ClaudeBridge", [StringComparison]::OrdinalIgnoreCase)) {
            @("ci", "--omit=optional")
        } else {
            @("ci")
        }
        if (-not ($FrontendInstalled -and $workspace -eq (Join-Path $repoRoot 'src/Ai.Tlbx.MidTerm'))) {
            Invoke-Checked -FilePath "npm" -ArgumentList $ciArguments -WorkingDirectory $workspace
        }
        $relativeWorkspace = [IO.Path]::GetRelativePath($repoRoot, $workspace).Replace('\', '/')
        $currentLockText = Get-Content (Join-Path $workspace 'package-lock.json') -Raw
        $currentLock = $currentLockText | ConvertFrom-Json -AsHashtable
        $report = Invoke-NpmAdvisoryReport $workspace
        $currentFindings = @(Convert-NpmAdvisoryFindings $report $currentLock)
        $baselineFindings = @()
        if ($currentFindings.Count -gt 0 -and (Export-AdvisoryBaselineFile $repoRoot $baseline "$relativeWorkspace/package-lock.json")) {
            if (-not (Export-AdvisoryBaselineFile $repoRoot $baseline "$relativeWorkspace/package.json")) {
                throw "Baseline npm manifest is missing for $relativeWorkspace."
            }
            $baselineWorkspace = Join-Path $baseline.Root $relativeWorkspace
            $oldLock = Get-Content (Join-Path $baselineWorkspace 'package-lock.json') -Raw | ConvertFrom-Json -AsHashtable
            # Identical inputs need only one query, avoiding advisory-feed races.
            if (($oldLock | ConvertTo-Json -Depth 100 -Compress) -eq ($currentLock | ConvertTo-Json -Depth 100 -Compress)) {
                $baselineFindings = $currentFindings
            } else {
                $oldReport = Invoke-NpmAdvisoryReport $baselineWorkspace
                $baselineFindings = @(Convert-NpmAdvisoryFindings $oldReport $oldLock)
            }
        }
        Assert-NoAdvisoryRegression $currentFindings $baselineFindings $relativeWorkspace
        Invoke-Checked -FilePath "npm" -ArgumentList @("audit", "signatures") -WorkingDirectory $workspace
    }
}
if ($Scope -in @('server','all')) {
    Invoke-Checked -FilePath "npm" -ArgumentList @("run", "build") -WorkingDirectory (Join-Path $repoRoot "src/Ai.Tlbx.MidTerm.AgentHost/ClaudeBridge")
    Invoke-Checked -FilePath "git" -ArgumentList @("diff", "--exit-code", "--", "src/Ai.Tlbx.MidTerm.AgentHost/ClaudeBridge/dist/claude-agent-sdk-bridge.mjs") -WorkingDirectory $repoRoot
}
if ($Scope -in @('tooling','all')) {
    Invoke-Checked -FilePath "npm" -ArgumentList @("test") -WorkingDirectory (Join-Path $repoRoot "src/npx-launcher")
}
if ($Scope -in @('server','all')) {

    Write-Host "Supply-chain gate: locked NuGet projects and advisories" -ForegroundColor Cyan
    $projectFiles = @(& git -C $repoRoot ls-files "*.csproj")
    if ($LASTEXITCODE -ne 0 -or $projectFiles.Count -eq 0) {
        throw "Could not enumerate tracked .NET projects."
    }
    $baselineNuGetExported = $false
    foreach ($relativeProject in $projectFiles) {
        $projectPath = Join-Path $repoRoot $relativeProject
        Invoke-Checked -FilePath "dotnet" -ArgumentList @("restore", $projectPath, "--locked-mode") -WorkingDirectory $repoRoot

        $auditOutput = & dotnet list $projectPath package --vulnerable --include-transitive --format json --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "NuGet vulnerability audit failed for $relativeProject."
        }
        $audit = $auditOutput | ConvertFrom-Json
        $currentFindings = @(Convert-NuGetAdvisoryFindings $audit)
        $baselineFindings = @()
        if ($currentFindings.Count -gt 0) {
            if (-not $baselineNuGetExported) {
                $inputs = @(& git -C $repoRoot ls-tree -r --name-only $baseline.Commit)
                if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate baseline NuGet restore inputs.' }
                foreach ($inputPath in $inputs | Where-Object { $_ -match '(?i)(\.csproj|\.props|\.targets|/packages\.lock\.json|(^|/)nuget\.config|(^|/)global\.json|(^|/)version\.json)$' }) {
                    if (-not (Export-AdvisoryBaselineFile $repoRoot $baseline $inputPath)) { throw "Could not export $inputPath." }
                }
                $baselineNuGetExported = $true
            }
            $oldProject = Join-Path $baseline.Root $relativeProject
            if (Test-Path -LiteralPath $oldProject) {
                Invoke-Checked 'dotnet' @('restore', $oldProject, '--locked-mode') $baseline.Root
                $oldOutput = & dotnet list $oldProject package --vulnerable --include-transitive --format json --no-restore
                if ($LASTEXITCODE -ne 0) { throw "Baseline NuGet audit failed for $relativeProject." }
                $baselineFindings = @(Convert-NuGetAdvisoryFindings ($oldOutput | ConvertFrom-Json))
            }
        }
        Assert-NoAdvisoryRegression $currentFindings $baselineFindings $relativeProject
    }
}
if ($Scope -in @('android','all')) {
    Write-Host "Supply-chain gate: locked and verified Android release graph" -ForegroundColor Cyan
    $androidRoot = Join-Path $repoRoot "src/connectors/android"
    $wrapperProperties = Get-Content -LiteralPath (Join-Path $androidRoot "gradle/wrapper/gradle-wrapper.properties") -Raw
    if ($wrapperProperties -notmatch '(?m)^distributionSha256Sum=[0-9a-f]{64}$') {
        throw "Gradle wrapper distributionSha256Sum is missing."
    }
    $verificationMetadata = Join-Path $androidRoot "gradle/verification-metadata.xml"
    $lockFile = Join-Path $androidRoot "app/gradle.lockfile"
    if (-not (Test-Path -LiteralPath $verificationMetadata) -or -not (Test-Path -LiteralPath $lockFile)) {
        throw "Gradle dependency verification metadata or lock state is missing."
    }

    $gradleExecutable = if ($IsWindows) { ".\gradlew.bat" } else { "bash" }
    $gradleArguments = @(
        ":app:dependencies",
        "--configuration", "releaseRuntimeClasspath",
        "--no-daemon",
        "--console", "plain"
    )
    if (-not $IsWindows) { $gradleArguments = @('./gradlew') + $gradleArguments }
    Invoke-Checked -FilePath $gradleExecutable -ArgumentList $gradleArguments -WorkingDirectory $androidRoot

    $releasePackages = Get-AndroidAdvisoryPackages (Get-Content -LiteralPath $lockFile)
    if ($releasePackages.Count -eq 0) {
        throw "Android releaseRuntimeClasspath was not represented in the lock file."
    }

    $oldPackages = @{}
    $relativeLock = 'src/connectors/android/app/gradle.lockfile'
    if (Export-AdvisoryBaselineFile $repoRoot $baseline $relativeLock) {
        $oldPackages = Get-AndroidAdvisoryPackages (Get-Content (Join-Path $baseline.Root $relativeLock))
    }
    $queries = $oldPackages.Clone()
    foreach ($coordinate in $releasePackages.Keys) { $queries[$coordinate] = $releasePackages[$coordinate] }
    $keys = @($queries.Keys | Sort-Object)
    $queryBody = @{ queries = @($keys | ForEach-Object { $queries[$_] }) } | ConvertTo-Json -Depth 8
    $osv = Invoke-RestMethod -Method Post -Uri "https://api.osv.dev/v1/querybatch" -ContentType "application/json" -Body $queryBody
    if ($null -eq $osv.results -or $osv.results.Count -ne $keys.Count) { throw 'OSV returned an incomplete Android audit.' }
    $reports = @{}
    for ($index = 0; $index -lt $keys.Count; $index++) {
        $reports[$keys[$index]] = $osv.results[$index]
    }
    Assert-NoAdvisoryRegression @(Convert-AndroidAdvisoryFindings $releasePackages $reports) @(Convert-AndroidAdvisoryFindings $oldPackages $reports) 'Android releaseRuntimeClasspath'
}

Write-Host "Supply-chain gate passed." -ForegroundColor Green
