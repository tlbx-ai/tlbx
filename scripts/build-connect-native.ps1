#!/usr/bin/env pwsh
# Explicit native-source rebuild only; normal builds keep using frozen prebuilts.
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('win-x64','win-x86','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$RuntimeIdentifier)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/build-storage.ps1"
$repoRoot = Split-Path $PSScriptRoot -Parent
$lock = Enter-TlbxBuildLock $repoRoot 'connect-native'
$run = $null
$succeeded = $false
try {
    Assert-TlbxNativeStorageIdle $repoRoot
    Move-TlbxNativeCache $repoRoot
    $run = Start-TlbxBuildRun $repoRoot "native-build-$RuntimeIdentifier"
    Move-TlbxNativeCache $repoRoot -IntoRecipe
    & "$repoRoot/src/Ai.Tlbx.MidTerm.ConnectNative/build.ps1" -RuntimeIdentifier $RuntimeIdentifier *> (Join-Path $run.Path 'build.log')
    if (-not $?) { throw "Native rebuild failed; inspect $($run.Path)/build.log" }
    $succeeded = $true
} finally {
    try {
        Move-TlbxNativeCache $repoRoot
        if ($succeeded) {
            foreach ($output in @('buildtrees', "load-smoke/$RuntimeIdentifier")) {
                try { Remove-TlbxBuildOutput $repoRoot ".artifacts/connect-dotnet-transport/$output" }
                catch { Write-Warning "Keeping native scratch: $($_.Exception.Message)" }
            }
        }
    } finally {
        try { if ($run) { Complete-TlbxBuildRun $run $succeeded } }
        finally { $lock.Dispose() }
    }
}
