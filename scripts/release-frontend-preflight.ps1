#!/usr/bin/env pwsh

param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Version,

    [switch]$DevRelease
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$frontendRoot = Join-Path $RepoRoot "src/Ai.Tlbx.MidTerm"
$frontendBuildScript = Join-Path $frontendRoot "frontend-build.ps1"
if (-not (Test-Path $frontendRoot -PathType Container)) {
    throw "Frontend root not found: $frontendRoot"
}
if (-not (Test-Path $frontendBuildScript -PathType Leaf)) {
    throw "frontend-build.ps1 not found: $frontendBuildScript"
}

$buildArgs = @{
    Publish = $true
    Version = $Version
}
if ($DevRelease) {
    $buildArgs.DevRelease = $true
}

# The selected checkout is the release workspace; keep all verification in it.
Write-Host "Running frontend preflight in $frontendRoot..." -ForegroundColor Cyan
Push-Location $frontendRoot
try {
    & npm ci --include=dev --prefer-offline --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) {
        throw "npm ci failed in $frontendRoot"
    }

    & $frontendBuildScript @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "frontend-build.ps1 failed in $frontendRoot"
    }
}
finally {
    Pop-Location
}

$global:LASTEXITCODE = 0
