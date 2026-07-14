#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Prunes GitHub Actions artifacts outside the retained release lines.

.DESCRIPTION
    Keeps artifacts for the current stable major.minor line and the immediately
    previous major.minor line. Deletes unexpired artifacts attached to older
    release lines.

    This only touches workflow artifacts. GitHub Releases, release assets, and
    tags are unaffected.

.PARAMETER Repository
    GitHub repository in owner/name form. Defaults to GITHUB_REPOSITORY or the
    current gh repo context.

.PARAMETER CurrentTag
    Stable tag that anchors retention, for example v8.11.24.

.EXAMPLE
    ./scripts/prune-release-artifacts.ps1 -Repository tlbx-ai/tlbx -CurrentTag v8.11.24

.EXAMPLE
    ./scripts/prune-release-artifacts.ps1 -CurrentTag v8.11.24 -WhatIf
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Repository,
    [string]$CurrentTag
)

$ErrorActionPreference = "Stop"

function Resolve-Repository {
    param([string]$Value)

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        return $Value.Trim()
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) {
        return $env:GITHUB_REPOSITORY.Trim()
    }

    $repo = gh repo view --json nameWithOwner --jq '.nameWithOwner' 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repo)) {
        throw "Could not resolve GitHub repository. Pass -Repository explicitly."
    }

    return $repo.Trim()
}

function Resolve-CurrentTag {
    param([string]$Value)

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        return $Value.Trim()
    }

    $tag = git tag --sort=-v:refname | Where-Object { $_ -notmatch '-dev' } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($tag)) {
        throw "Could not resolve a stable tag. Pass -CurrentTag explicitly."
    }

    return $tag.Trim()
}

function Parse-ReleaseLine {
    param([string]$RefName)

    if ([string]::IsNullOrWhiteSpace($RefName)) {
        return $null
    }

    $match = [regex]::Match($RefName, '^v(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-dev(?:\.\d+)?)?$')
    if (-not $match.Success) {
        return $null
    }

    $major = [int]$match.Groups['major'].Value
    $minor = [int]$match.Groups['minor'].Value

    return [pscustomobject]@{
        Major = $major
        Minor = $minor
        Key   = "$major.$minor"
    }
}

function Get-LineSortValue {
    param($Line)

    return [version]"$($Line.Major).$($Line.Minor).0"
}

$resolvedRepository = Resolve-Repository -Value $Repository
$resolvedCurrentTag = Resolve-CurrentTag -Value $CurrentTag

if ($resolvedCurrentTag -match '-dev(?:\.\d+)?$') {
    throw "CurrentTag must be a stable tag. Received '$resolvedCurrentTag'."
}

$currentLine = Parse-ReleaseLine -RefName $resolvedCurrentTag
if ($null -eq $currentLine) {
    throw "CurrentTag '$resolvedCurrentTag' is not a supported release tag."
}

Write-Host ""
Write-Host "  Release Artifact Prune" -ForegroundColor Cyan
Write-Host "  ======================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Repository:  $resolvedRepository" -ForegroundColor Gray
Write-Host "  Current tag: $resolvedCurrentTag" -ForegroundColor Gray
Write-Host "  Keep line:   $($currentLine.Key)" -ForegroundColor Green
Write-Host ""

$firstPage = gh api "/repos/$resolvedRepository/actions/artifacts?per_page=1" | ConvertFrom-Json
$pageCount = [math]::Ceiling($firstPage.total_count / 100)
$activeArtifacts = New-Object System.Collections.Generic.List[object]

for ($page = 1; $page -le $pageCount; $page++) {
    $response = gh api "/repos/$resolvedRepository/actions/artifacts?per_page=100&page=$page" | ConvertFrom-Json
    foreach ($artifact in $response.artifacts) {
        if ($artifact.expired) {
            continue
        }

        $line = Parse-ReleaseLine -RefName $artifact.workflow_run.head_branch
        if ($null -eq $line) {
            continue
        }

        $activeArtifacts.Add([pscustomobject]@{
            Id       = [int64]$artifact.id
            Name     = [string]$artifact.name
            Size     = [int64]$artifact.size_in_bytes
            Branch   = [string]$artifact.workflow_run.head_branch
            Created  = [datetime]$artifact.created_at
            Expires  = [datetime]$artifact.expires_at
            LineKey  = [string]$line.Key
            Major    = [int]$line.Major
            Minor    = [int]$line.Minor
        })
    }
}

if ($activeArtifacts.Count -eq 0) {
    Write-Host "No active release-tag artifacts found. Nothing to prune." -ForegroundColor Yellow
    exit 0
}

$allLines =
    $activeArtifacts |
    Group-Object LineKey |
    ForEach-Object {
        [pscustomobject]@{
            Key   = $_.Name
            Major = $_.Group[0].Major
            Minor = $_.Group[0].Minor
        }
    }

$currentSort = Get-LineSortValue -Line $currentLine
$previousLine =
    $allLines |
    Where-Object { (Get-LineSortValue -Line $_) -lt $currentSort } |
    Sort-Object { Get-LineSortValue -Line $_ } -Descending |
    Select-Object -First 1

$keepLineKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
[void]$keepLineKeys.Add($currentLine.Key)
if ($null -ne $previousLine) {
    [void]$keepLineKeys.Add($previousLine.Key)
}

Write-Host "  Previous line: $($(if ($previousLine) { $previousLine.Key } else { '<none>' }))" -ForegroundColor Green
Write-Host "  Active release artifacts: $($activeArtifacts.Count)" -ForegroundColor Gray
Write-Host ""

$artifactsToDelete =
    $activeArtifacts |
    Where-Object { -not $keepLineKeys.Contains($_.LineKey) } |
    Sort-Object Created, Id

$artifactsToKeep =
    $activeArtifacts |
    Where-Object { $keepLineKeys.Contains($_.LineKey) }

$keepSize = ($artifactsToKeep | Measure-Object Size -Sum).Sum
$deleteSize = ($artifactsToDelete | Measure-Object Size -Sum).Sum

Write-Host "  Keep summary:" -ForegroundColor Cyan
foreach ($lineKey in $keepLineKeys | Sort-Object { [version]($_ + '.0') } -Descending) {
    $lineArtifacts = $artifactsToKeep | Where-Object { $_.LineKey -eq $lineKey }
    $lineSize = ($lineArtifacts | Measure-Object Size -Sum).Sum
    Write-Host ("    {0}: {1} artifacts, {2:N1} MB" -f $lineKey, $lineArtifacts.Count, ($lineSize / 1MB)) -ForegroundColor DarkGray
}
Write-Host ("    total: {0} artifacts, {1:N1} MB" -f $artifactsToKeep.Count, ($keepSize / 1MB)) -ForegroundColor DarkGray
Write-Host ""

Write-Host "  Delete summary:" -ForegroundColor Cyan
foreach ($group in ($artifactsToDelete | Group-Object LineKey | Sort-Object { [version]($_.Name + '.0') } -Descending)) {
    $lineSize = ($group.Group | Measure-Object Size -Sum).Sum
    Write-Host ("    {0}: {1} artifacts, {2:N1} MB" -f $group.Name, $group.Count, ($lineSize / 1MB)) -ForegroundColor DarkGray
}
Write-Host ("    total: {0} artifacts, {1:N1} MB" -f $artifactsToDelete.Count, ($deleteSize / 1MB)) -ForegroundColor DarkGray
Write-Host ""

if ($artifactsToDelete.Count -eq 0) {
    Write-Host "Nothing falls outside the retained release lines." -ForegroundColor Green
    exit 0
}

if ($WhatIfPreference) {
    Write-Host "WhatIf mode is enabled. GitHub will report each artifact that would be deleted." -ForegroundColor Yellow
    Write-Host ""
}

$deletedCount = 0
foreach ($artifact in $artifactsToDelete) {
    $target = "$($artifact.Id) $($artifact.Name) $($artifact.Branch)"
    if ($PSCmdlet.ShouldProcess($target, "Delete GitHub Actions artifact")) {
        gh api --method DELETE "/repos/$resolvedRepository/actions/artifacts/$($artifact.Id)" | Out-Null
        $deletedCount++
        if (($deletedCount % 100) -eq 0) {
            Write-Host ("  Progress: deleted {0}/{1} artifacts..." -f $deletedCount, $artifactsToDelete.Count) -ForegroundColor DarkGray
        }
    }
}

Write-Host ""
Write-Host ("Pruned {0} artifacts ({1:N1} MB)." -f $artifactsToDelete.Count, ($deleteSize / 1MB)) -ForegroundColor Green
