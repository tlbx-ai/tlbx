#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64','win-x86','linux-x64','linux-arm64','osx-x64','osx-arm64')]
    [string]$RuntimeIdentifier
)
$ErrorActionPreference = 'Stop'
& python (Join-Path $PSScriptRoot 'prebuilt.py') --prepare $RuntimeIdentifier
if ($LASTEXITCODE -ne 0) {
    throw "Pinned native bundle validation failed for $RuntimeIdentifier. Deliberately rebuild/review and seal the matching prebuilt; automatic source compilation is disabled."
}