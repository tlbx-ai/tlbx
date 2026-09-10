#!/usr/bin/env pwsh
param([string]$Base = $env:DIFF_BASE, [string]$Head = $env:DIFF_HEAD)
$ErrorActionPreference = 'Stop'
$android = $false
$ios = $false
if ($env:GITHUB_EVENT_NAME -in @('workflow_dispatch','schedule') -or [string]::IsNullOrEmpty($Base) -or $Base -match '^0+$') {
    $android = $ios = $true
} else {
    $files = @(& git diff --name-only $Base $Head --)
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine mobile changes.' }
    foreach ($file in $files) {
        if ($file -like 'src/connectors/android/*' -or $file -eq 'scripts/audit-supply-chain.ps1') { $android = $true }
        if ($file -like 'src/connectors/ios/*') { $ios = $true }
        if ($file -like 'src/connectors/shared-assets/*' -or $file -in @('src/connectors/build-number','.github/workflows/mobile-verify.yml','scripts/get-mobile-changes.ps1')) {
            $android = $ios = $true
        }
    }
}
$lines = @("android=$($android.ToString().ToLowerInvariant())", "ios=$($ios.ToString().ToLowerInvariant())")
if ($env:GITHUB_OUTPUT) { $lines | Add-Content $env:GITHUB_OUTPUT }
$lines
