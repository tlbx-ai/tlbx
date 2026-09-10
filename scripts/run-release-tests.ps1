#!/usr/bin/env pwsh
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()]
    [ValidateSet('assets','frontend','server','runtime','installers','dependencies','build','all')]
    [string[]]$TestCategories,
    [switch]$FrontendInstalled,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-test-clusters.ps1"
$categories = @(Resolve-ReleaseTestClusters -TestCategories $TestCategories)
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    Write-Host "Running release clusters: $($categories -join ', ')" -ForegroundColor Cyan
    if ($categories -contains 'frontend' -or $categories -contains 'assets') {
        Push-Location src/Ai.Tlbx.MidTerm
        try {
            if (-not $FrontendInstalled) {
                & npm ci --include=dev --prefer-offline --no-audit --no-fund
                if ($LASTEXITCODE -ne 0) { throw 'Frontend install failed.' }
                $FrontendInstalled = $true
            }
            if ($categories -contains 'frontend') {
                & npm run verify
                if ($LASTEXITCODE -ne 0) { throw 'Frontend tests failed.' }
            } else {
                & npm run typecheck
                if ($LASTEXITCODE -ne 0) { throw 'Frontend typecheck failed.' }
                & npm run lint
                if ($LASTEXITCODE -ne 0) { throw 'Frontend lint failed.' }
            }
        } finally { Pop-Location }
    }
    if ($categories -contains 'runtime') {
        & "$PSScriptRoot/run-dotnet-test-suite.ps1" -Configuration $Configuration -WarnAsError
    } elseif ($categories -contains 'server') {
        & "$PSScriptRoot/run-dotnet-test-suite.ps1" -Configuration $Configuration -WarnAsError -Suite server
    }
    if ($categories -contains 'installers') { & "$PSScriptRoot/verify-installers.ps1" }
    if ($categories -contains 'dependencies') {
        & "$PSScriptRoot/audit-supply-chain.ps1" -Scope server -FrontendInstalled:$FrontendInstalled
    }
    if ($categories -contains 'build') {
        & "$PSScriptRoot/test-release-build-system.ps1"
        & "$PSScriptRoot/run-runtime-build-verification.ps1" -Configuration $Configuration -WarnAsError
        if ($IsWindows) {
            & "$PSScriptRoot/run-aot-smoke-probe.ps1" -Configuration $Configuration -Rid win-x64
        } else {
            Write-Host 'Windows AOT execution probe is owned by the Windows release environment.'
        }
    }
} finally { Pop-Location }
$global:LASTEXITCODE = 0
