#!/usr/bin/env pwsh
# Run only in the task's exclusively owned checkout after its release has succeeded.
param()
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
. "$PSScriptRoot/release-pr.ps1"
$branch = Assert-ReleaseTaskBranch
Assert-ReleaseClean
$prs = @(Invoke-ReleaseGh pr list --repo $script:TlbxReleaseRepo --head $branch --state merged --json 'number,headRefOid' | ConvertFrom-Json)
$head = Invoke-ReleaseGit rev-parse HEAD
if (-not @($prs | Where-Object headRefOid -EQ $head).Count) { throw 'No merged PR matches this exact branch tip. Task was not retired.' }
$state = Get-ReleaseState $branch
if ($state) {
    $release = Invoke-ReleaseGh release view "v$($state.Version)" --repo $script:TlbxReleaseRepo --json 'isDraft,assets' | ConvertFrom-Json
    if ($release.isDraft -or $release.assets.Count -lt 12) { throw 'Release assets are incomplete; keep the task available for recovery.' }
    $runs = @(Invoke-ReleaseGh run list --repo $script:TlbxReleaseRepo --workflow release.yml --commit $state.Merge --json 'status,conclusion' | ConvertFrom-Json)
    if (-not @($runs | Where-Object { $_.status -eq 'completed' -and $_.conclusion -eq 'success' }).Count) { throw 'Release CI has not succeeded; task was not retired.' }
}
Invoke-ReleaseGit fetch origin --prune | Out-Host
Invoke-ReleaseGit merge-base --is-ancestor $head origin/dev | Out-Null
Invoke-ReleaseGit switch dev | Out-Host
Invoke-ReleaseGit merge --ff-only origin/dev | Out-Host
Invoke-ReleaseGit branch -d $branch | Out-Host
Write-Host "Retired $branch; checkout is back on updated dev."
