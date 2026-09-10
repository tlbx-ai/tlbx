#!/usr/bin/env pwsh
<#[
.SYNOPSIS
    Fails when tlbx's locked release dependency graph contains known vulnerabilities
    or mutable GitHub Action references.
#>

[CmdletBinding()]
param(
    [ValidateSet('server','android','tooling','all')][string]$Scope = 'server',
    [switch]$FrontendInstalled
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

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

function Invoke-NpmAdvisoryAudit {
    param(
        [Parameter(Mandatory=$true)][string]$WorkingDirectory,
        [int]$MaxAttempts = 3
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Invoke-Checked -FilePath "npm" -ArgumentList @(
                "audit",
                "--audit-level=low",
                "--fetch-timeout=600000"
            ) -WorkingDirectory $WorkingDirectory
            return
        }
        catch {
            if ($attempt -eq $MaxAttempts) {
                throw
            }

            $retryDelaySeconds = 5 * $attempt
            Write-Warning "npm advisory audit attempt $attempt of $MaxAttempts failed; retrying in $retryDelaySeconds seconds."
            Start-Sleep -Seconds $retryDelaySeconds
        }
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
        # The registry's bulk advisory endpoint can be slow or transiently return
        # 503 for the Claude SDK graph. Keep the audit fail-closed while allowing a
        # complete response and a bounded retry of this idempotent remote check.
        Invoke-NpmAdvisoryAudit -WorkingDirectory $workspace
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
    $nugetFindings = @()
    foreach ($relativeProject in $projectFiles) {
        $projectPath = Join-Path $repoRoot $relativeProject
        Invoke-Checked -FilePath "dotnet" -ArgumentList @("restore", $projectPath, "--locked-mode") -WorkingDirectory $repoRoot

        $auditOutput = & dotnet list $projectPath package --vulnerable --include-transitive --format json --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "NuGet vulnerability audit failed for $relativeProject."
        }
        $audit = $auditOutput | ConvertFrom-Json
        foreach ($project in @($audit.projects)) {
            foreach ($framework in @($project.frameworks)) {
                $packages = @()
                if ($null -ne $framework.topLevelPackages) {
                    $packages += @($framework.topLevelPackages)
                }
                if ($null -ne $framework.transitivePackages) {
                    $packages += @($framework.transitivePackages)
                }

                foreach ($package in $packages) {
                    if ($null -eq $package) {
                        continue
                    }
                    foreach ($vulnerability in @($package.vulnerabilities | Where-Object { $null -ne $_ })) {
                        $nugetFindings += "$relativeProject $($package.id) $($package.resolvedVersion) $($vulnerability.severity) $($vulnerability.advisoryurl)"
                    }
                }
            }
        }
    }
    if ($nugetFindings.Count -gt 0) {
        throw "Known NuGet vulnerabilities found:`n$($nugetFindings -join "`n")"
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

    $releasePackages = @{}
    foreach ($line in Get-Content -LiteralPath $lockFile) {
        if ($line.StartsWith("#") -or $line -eq "empty=" -or $line -notmatch '=') {
            continue
        }
        $coordinate, $configurations = $line -split '=', 2
        if (($configurations -split ',') -notcontains "releaseRuntimeClasspath") {
            continue
        }
        $parts = $coordinate -split ':'
        if ($parts.Count -lt 3) {
            continue
        }
        $name = "$($parts[0]):$($parts[1])"
        $version = $parts[2..($parts.Count - 1)] -join ':'
        $releasePackages["$name@$version"] = @{ package = @{ ecosystem = "Maven"; name = $name }; version = $version }
    }
    if ($releasePackages.Count -eq 0) {
        throw "Android releaseRuntimeClasspath was not represented in the lock file."
    }

    $queryBody = @{ queries = @($releasePackages.Values) } | ConvertTo-Json -Depth 8
    $osv = Invoke-RestMethod -Method Post -Uri "https://api.osv.dev/v1/querybatch" -ContentType "application/json" -Body $queryBody
    $gradleFindings = @()
    $keys = @($releasePackages.Keys)
    for ($index = 0; $index -lt $keys.Count; $index++) {
        foreach ($vulnerability in @($osv.results[$index].vulns | Where-Object { $null -ne $_ })) {
            $gradleFindings += "$($keys[$index]) $($vulnerability.id)"
        }
    }
    if ($gradleFindings.Count -gt 0) {
        throw "Known Android runtime vulnerabilities found:`n$($gradleFindings -join "`n")"
    }
}

Write-Host "Supply-chain gate passed." -ForegroundColor Green
