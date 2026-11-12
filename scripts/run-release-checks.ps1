#!/usr/bin/env pwsh
# Release validation never dispatches unit, integration or behavioral test suites.
# Run run-release-tests.ps1 explicitly, or use Tests (on demand), for those.
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
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    Write-Host "Release checks (no test suites): $($categories -join ', ')" -ForegroundColor Cyan
    if ($categories -contains 'frontend' -or $categories -contains 'assets') {
        Push-Location src/Ai.Tlbx.MidTerm
        try {
            if (-not $FrontendInstalled) {
                & npm ci --include=dev --prefer-offline --no-audit --no-fund
                if ($LASTEXITCODE) { throw 'Frontend install failed.' }
                $FrontendInstalled = $true
            }
            & npm run typecheck
            if ($LASTEXITCODE) { throw 'Frontend typecheck failed.' }
            & npm run lint
            if ($LASTEXITCODE) { throw 'Frontend lint failed.' }
        } finally { Pop-Location }
    }
    if ($categories -contains 'runtime' -or $categories -contains 'build') {
        & "$PSScriptRoot/run-runtime-build-verification.ps1" -Configuration $Configuration -WarnAsError
    } elseif ($categories -contains 'server') {
        & dotnet build src/Ai.Tlbx.MidTerm/Ai.Tlbx.MidTerm.csproj -c $Configuration --nologo -warnaserror -p:SkipFrontendBuild=true -p:ContinuousIntegrationBuild=true
        if ($LASTEXITCODE) { throw 'Server build failed.' }
    }
    if ($categories -contains 'dependencies') {
        & "$PSScriptRoot/audit-supply-chain.ps1" -Scope server -FrontendInstalled:$FrontendInstalled
    }
    # Installer behavior is exercised on demand. Actual release packages retain
    # their mandatory signed manifests, hashes, SBOMs and Native AOT publication.
} finally { Pop-Location }
$global:LASTEXITCODE = 0
