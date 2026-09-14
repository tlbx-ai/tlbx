#!/usr/bin/env pwsh
param([Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+(-dev)?$')][string]$Tag)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-pr.ps1"
$sha = Invoke-ReleaseGit rev-parse HEAD
$base = if ($Tag.EndsWith('-dev')) { 'dev' } else { 'main' }
$pages = Invoke-ReleaseGh api "repos/$script:TlbxReleaseRepo/commits/$sha/pulls" --paginate --slurp | ConvertFrom-Json
$prs = @($pages | ForEach-Object { $_ })
$matching = @($prs | Where-Object { $_.merged_at -and $_.merge_commit_sha -eq $sha -and $_.base.ref -eq $base })
if ($matching.Count -eq 0) { throw "Release $Tag is not the exact merge commit of a merged PR into $base." }
$fetchArgs = @('fetch','origin',"refs/heads/${base}:refs/remotes/origin/$base")
if ((Invoke-ReleaseGit rev-parse --is-shallow-repository) -eq 'true') { $fetchArgs += '--unshallow' }
Invoke-ReleaseGit @fetchArgs | Out-Host
Invoke-ReleaseGit merge-base --is-ancestor $sha "origin/$base" | Out-Null
$version = Get-Content "$PSScriptRoot/../src/version.json" -Raw | ConvertFrom-Json
if ("v$($version.web)" -ne $Tag) { throw 'Tag and merged source version disagree.' }
Write-Host "Verified $Tag at $sha through merged PR #$($matching[0].number) into $base."
