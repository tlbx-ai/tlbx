#!/usr/bin/env pwsh
# Offline behavioral checks for release security regression policy.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/advisory-regression.ps1"
$checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Reject([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Check $rejected $Message
}

$existing = New-AdvisoryFinding braces 'https://example.test/advisory' high development
Assert-NoAdvisoryRegression @($existing) @($existing) 'unchanged graph / newly reported advisory'
Assert-NoAdvisoryRegression @() @($existing) 'removed vulnerability'
$lower = New-AdvisoryFinding braces $existing.Advisory moderate development
Assert-NoAdvisoryRegression @($lower) @($existing) 'reduced severity'
Reject { Assert-NoAdvisoryRegression @($existing) @() 'introduced vulnerability' } 'New vulnerability passed.'
Reject { Assert-NoAdvisoryRegression @($existing) @($lower) 'increased severity' } 'Higher severity passed.'
Reject { Assert-NoAdvisoryRegression @((New-AdvisoryFinding other $existing.Advisory high development)) @($existing) 'new package' } 'Switch to a newly vulnerable library passed.'
Reject { Assert-NoAdvisoryRegression @((New-AdvisoryFinding braces $existing.Advisory high runtime)) @($existing) 'runtime promotion' } 'Development vulnerability promoted to runtime passed.'
Reject { Assert-NoAdvisoryRegression @((New-AdvisoryFinding braces $existing.Advisory high development 2)) @($existing) 'additional installation' } 'Expanded vulnerable exposure passed.'
Reject { New-AdvisoryFinding braces '' high } 'Missing advisory identity passed.'

$releases = @(
    [pscustomobject]@{ tag_name='v1.0.3-dev'; draft=$true; published_at='2026-10-04T00:00:00Z' },
    [pscustomobject]@{ tag_name='v1.0.2-dev'; draft=$false; published_at='2026-10-03T00:00:00Z' },
    [pscustomobject]@{ tag_name='v1.0.4'; draft=$false; published_at='2026-10-04T00:00:00Z' }
)
Check ((Select-AdvisoryBaselineRelease $releases $true).tag_name -eq 'v1.0.2-dev') 'Draft or wrong channel became baseline.'
Check ((Select-AdvisoryBaselineRelease $releases $false).tag_name -eq 'v1.0.4') 'Stable baseline selection failed.'
Reject { Select-AdvisoryBaselineRelease @() $true } 'Missing published baseline passed.'

$report = @'
{"auditReportVersion":2,"vulnerabilities":{"braces":{"name":"braces","nodes":["node_modules/braces"],"via":[{"url":"https://example.test/advisory","severity":"high"}]},"parent":{"name":"parent","nodes":["node_modules/parent"],"via":["braces"]}}}
'@ | ConvertFrom-Json
$lock = '{"packages":{"node_modules/braces":{"version":"3.0.3","dev":true},"node_modules/parent":{"version":"1.0.0","dev":true}}}' | ConvertFrom-Json -AsHashtable
$findings = @(Convert-NpmAdvisoryFindings $report $lock)
Check ($findings.Count -eq 1 -and $findings[0].Count -eq 1) 'npm meta-vulnerabilities were counted as new advisories.'
$lock.packages.'node_modules/braces'.version = '3.0.2'
Assert-NoAdvisoryRegression @(Convert-NpmAdvisoryFindings $report $lock) $findings 'another affected version'
$lock.packages.'node_modules/braces'.dev = $false
Reject { Assert-NoAdvisoryRegression @(Convert-NpmAdvisoryFindings $report $lock) $findings 'npm runtime promotion' } 'Lockfile runtime promotion passed.'
Reject { Convert-NpmAdvisoryFindings $report ('{"packages":{}}' | ConvertFrom-Json -AsHashtable) } 'npm finding absent from lock passed.'

$nuget = '{"version":1,"projects":[{"frameworks":[{"framework":"net10.0","transitivePackages":[{"id":"Fixture","resolvedVersion":"1.0.0","vulnerabilities":[{"severity":"High","advisoryurl":"https://example.test/nuget"}]}]}]}]}' | ConvertFrom-Json
$nugetFindings = @(Convert-NuGetAdvisoryFindings $nuget)
Check ($nugetFindings.Count -eq 1 -and $nugetFindings[0].Rank -eq 3) 'NuGet advisory normalization failed.'
Reject { Convert-NuGetAdvisoryFindings ('{"version":1,"projects":[],"problems":["source unavailable"]}' | ConvertFrom-Json) } 'Incomplete NuGet audit passed.'

$android = Get-AndroidAdvisoryPackages @('example:library:1.0=releaseRuntimeClasspath', 'example:test:1.0=debugRuntimeClasspath')
$androidOld = Get-AndroidAdvisoryPackages @('example:library:0.9=releaseRuntimeClasspath')
$osvReports = @{'example:library:1.0'=@{vulns=@(@{id='OSV-fixture'})}; 'example:library:0.9'=@{vulns=@(@{id='OSV-fixture'})}}
Check ($android.Count -eq 1) 'Non-release Android dependencies entered the audit.'
Assert-NoAdvisoryRegression @(Convert-AndroidAdvisoryFindings $android $osvReports) @(Convert-AndroidAdvisoryFindings $androidOld $osvReports) 'another affected Android version'
$androidExpanded = $android.Clone()
$androidExpanded['example:library:0.9'] = $androidOld['example:library:0.9']
Reject { Assert-NoAdvisoryRegression @(Convert-AndroidAdvisoryFindings $androidExpanded $osvReports) @(Convert-AndroidAdvisoryFindings $androidOld $osvReports) 'expanded Android exposure' } 'Additional vulnerable Android version passed.'
Reject { Assert-NoAdvisoryRegression @(Convert-AndroidAdvisoryFindings $android $osvReports) @() 'introduced Android vulnerability' } 'New Android vulnerability passed.'

# Exercise the npm process boundary: exit 1 with a valid advisory report succeeds,
# while an error object or invalid JSON still fails the audit.
$global:TlbxAuditFixtureOutput = $report | ConvertTo-Json -Depth 10 -Compress
function global:npm { $global:LASTEXITCODE = 1; $global:TlbxAuditFixtureOutput }
try {
    Check ((Invoke-NpmAdvisoryReport $PSScriptRoot -MaxAttempts 1).auditReportVersion -eq 2) 'npm findings exit code was rejected.'
    $global:TlbxAuditFixtureOutput = '{"error":{"code":"E503"}}'
    Reject { Invoke-NpmAdvisoryReport $PSScriptRoot -MaxAttempts 1 } 'npm registry error passed.'
    $global:TlbxAuditFixtureOutput = 'truncated JSON'
    Reject { Invoke-NpmAdvisoryReport $PSScriptRoot -MaxAttempts 1 } 'Malformed npm response passed.'
} finally {
    Remove-Item Function:\npm -ErrorAction SilentlyContinue
    Remove-Variable TlbxAuditFixtureOutput -Scope Global -ErrorAction SilentlyContinue
}
Write-Host "Advisory regression policy: $checks checks passed." -ForegroundColor Green
$global:LASTEXITCODE = 0
