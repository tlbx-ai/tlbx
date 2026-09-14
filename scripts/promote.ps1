#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Promotes the current dev version to a stable release on main.

.DESCRIPTION
    This script automates the promotion of a dev release to stable:
    1. Verifies we're on dev branch with a -dev version
    2. Auto-gathers changelog from all dev tag annotations since the last stable release
    3. Freezes the candidate on chore/promote-X-Y-Z and prepares stable metadata
    4. Verifies and merges its PR into main, then tags the exact merge commit
    5. Synchronizes main back into dev through a PR

.PARAMETER ReleaseTitle
    Optional. A concise title for this release (one line, no version number).
    If omitted, uses the most recent dev release title.

.PARAMETER TestCategories
    REQUIRED: assets, frontend, server, runtime, installers, dependencies, build; or all alone.
    Stable releases require all. Mobile apps are verified and released independently.

.PARAMETER ReleaseNotes
    Optional. Array of detailed changelog entries. If omitted, automatically
    gathered from all dev tag annotations since the last stable release.

.EXAMPLE
    # Auto-gather all changelog items (recommended)
    .\promote.ps1 -TestCategories all

.EXAMPLE
    # Override title, still auto-gather notes
    .\promote.ps1 -TestCategories all -ReleaseTitle "Major UI overhaul"

.EXAMPLE
    # Fully manual (legacy behavior)
    .\promote.ps1 -TestCategories all -ReleaseTitle "Version management improvements" -ReleaseNotes @(
        "Centralized version management: src/version.json is now single source of truth",
        "Fixed update failures where wrong version was baked into binaries"
    )
#>

param(
    [Parameter(Mandatory=$true, HelpMessage="Choose the affected test clusters explicitly, or all.")]
    [ValidateNotNullOrEmpty()]
    [ValidateSet('assets','frontend','server','runtime','installers','dependencies','build','all')]
    [string[]]$TestCategories,

    [string]$ReleaseTitle,
    [string[]]$ReleaseNotes,
    [switch]$PrepareOnly,
    [switch]$Reprepare
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
. "$PSScriptRoot/release-pr.ps1"
. "$PSScriptRoot/release-test-clusters.ps1"
$selectedCategories = @(Resolve-ReleaseTestClusters -TestCategories $TestCategories -Stable)
Show-ReleaseTestPlan -Categories $selectedCategories
$currentBranch = Invoke-ReleaseGit branch --show-current
$pending = Get-ReleaseState $currentBranch
if ($pending) {
    if ($pending.Base -ne 'main') { throw 'This branch has a dev release in progress; finish and retire it first.' }
    if (-not $Reprepare) { Complete-TlbxRelease $pending -PrepareOnly:$PrepareOnly; return }
    Reset-ReleasePreparation $pending
}
Assert-ReleaseClean
Invoke-ReleaseGit fetch origin --tags | Out-Host
if ($currentBranch -eq 'dev') {
    Invoke-ReleaseGit merge --ff-only origin/dev | Out-Host
} elseif ($currentBranch -notmatch '^chore/promote-[0-9]+-[0-9]+-[0-9]+$') {
    throw 'Run promotion from clean updated dev, or resume its chore/promote-X-Y-Z branch.'
}
$promotionBase = Invoke-ReleaseGit rev-parse origin/main
$candidate = if ($currentBranch -eq 'dev') { Invoke-ReleaseGit rev-parse origin/dev } else { Invoke-ReleaseGit merge-base HEAD origin/dev }
Invoke-ReleaseGit merge-base --is-ancestor origin/main HEAD | Out-Null
$githubPrBodyMaxChars = 65536
$githubReleaseNotesMaxChars = 125000
$githubBodySafetyMarginChars = 512
$githubReleaseHeading = "## What's Changed`n"

function Get-ChangelogMarkdownBlock {
    param(
        [Parameter(Mandatory=$true)]
        [psobject]$Entry
    )

    $block = "`n### $($Entry.Tag) - $($Entry.Title)`n"
    foreach ($note in $Entry.Notes) {
        $block += "$note`n"
    }

    return $block
}

function Get-ChangelogPlainTextBlock {
    param(
        [Parameter(Mandatory=$true)]
        [psobject]$Entry
    )

    $block = "$($Entry.Tag): $($Entry.Title)`n"
    foreach ($note in $Entry.Notes) {
        $block += "$note`n"
    }

    return $block + "`n"
}

function Join-RecentBlocksWithinLimit {
    param(
        [string]$Prefix = "",

        [Parameter(Mandatory=$true)]
        [string[]]$Blocks,

        [Parameter(Mandatory=$true)]
        [int]$MaxLength,

        [string]$TruncationNoticeFormat = ""
    )

    $keptBlocks = [System.Collections.Generic.List[string]]::new()
    $keptLength = 0

    for ($i = $Blocks.Count - 1; $i -ge 0; $i--) {
        $block = [string]$Blocks[$i]
        $candidateKeptCount = $keptBlocks.Count + 1
        $omittedCount = $Blocks.Count - $candidateKeptCount
        $notice = ""
        if ($omittedCount -gt 0 -and -not [string]::IsNullOrEmpty($TruncationNoticeFormat)) {
            $notice = [string]::Format($TruncationNoticeFormat, $omittedCount)
        }

        $candidateLength = $Prefix.Length + $notice.Length + $keptLength + $block.Length
        if ($candidateLength -le $MaxLength) {
            $keptBlocks.Insert(0, $block)
            $keptLength += $block.Length
            continue
        }

        if ($keptBlocks.Count -eq 0) {
            throw "The newest release-note block exceeds the configured GitHub size limit by itself."
        }

        break
    }

    $omittedCount = $Blocks.Count - $keptBlocks.Count
    $notice = ""
    if ($omittedCount -gt 0 -and -not [string]::IsNullOrEmpty($TruncationNoticeFormat)) {
        $notice = [string]::Format($TruncationNoticeFormat, $omittedCount)
    }

    $text = $Prefix + $notice + ($keptBlocks -join "")
    if ($text.Length -gt $MaxLength) {
        throw "Trimmed release notes still exceed the configured GitHub size limit."
    }

    return [pscustomobject]@{
        Text         = $text
        OmittedCount = $omittedCount
        KeptCount    = $keptBlocks.Count
        TotalCount   = $Blocks.Count
    }
}

# Read current version
$versionJsonPath = "$PSScriptRoot\..\src\version.json"
$versionJson = Get-Content $versionJsonPath | ConvertFrom-Json
$devVersion = $versionJson.web
if ($Reprepare -and $pending) { $devVersion = "$($pending.Version)-dev" }

# Verify it's a dev version
if ($devVersion -notmatch '-dev$') {
    Write-Host ""
    Write-Host "ERROR: Current version '$devVersion' is not a dev version." -ForegroundColor Red
    Write-Host "Only versions ending in -dev can be promoted." -ForegroundColor Yellow
    Write-Host ""
    throw 'Release preparation failed; see details above.'
}

# Calculate stable version
$stableVersion = $devVersion -replace '-dev$', ''

# Find last stable tag (non-dev, sorted by version descending)
$lastStableTag = git tag --sort=-v:refname | Where-Object { $_ -notmatch '-dev' } | Select-Object -First 1
$lastStableVersion = [version]($lastStableTag -replace '^v', '')

Write-Host ""
Write-Host "  tlbx Promotion" -ForegroundColor Cyan
Write-Host "  =================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Dev version:    $devVersion" -ForegroundColor Gray
Write-Host "  Stable version: $stableVersion" -ForegroundColor Green
Write-Host "  Last stable:    $lastStableTag" -ForegroundColor Gray
Write-Host ""

# Freeze the promotion on a short-lived branch; later dev merges cannot enter it.
if ($currentBranch -eq 'dev') {
    if ((Invoke-ReleaseGit rev-parse HEAD) -ne $candidate) { throw 'dev has unpublished local commits; integrate them through a task PR first.' }
    $currentBranch = 'chore/promote-' + $stableVersion.Replace('.', '-')
    Invoke-ReleaseGit switch -c $currentBranch | Out-Host
}

# --- Auto-gather changelog from dev tags since last stable release ---

Write-Host "Gathering changelog from dev releases since $lastStableTag..." -ForegroundColor Gray

# Get all dev tags sorted by version, filter to those newer than last stable
$allDevTags = git tag --merged HEAD --sort=version:refname | Where-Object { $_ -match '-dev$' }
$devTagsInRange = @()
foreach ($tag in $allDevTags) {
    $baseVer = $tag -replace '^v', '' -replace '-dev(\.\d+)?$', ''
    try {
        if ([version]$baseVer -gt $lastStableVersion) {
            $devTagsInRange += $tag
        }
    } catch {
        # Skip tags with unparseable versions
    }
}

if ($devTagsInRange.Count -eq 0) {
    Write-Host ""
    Write-Host "ERROR: No dev tags found since $lastStableTag. Nothing to promote." -ForegroundColor Red
    Write-Host ""
    throw 'Release preparation failed; see details above.'
}

# Parse each tag's annotation
$changelog = @()
foreach ($tag in $devTagsInRange) {
    $annotation = git tag -l --format='%(contents)' $tag
    if (-not $annotation) { continue }
    $lines = $annotation -split "`n"
    $title = $lines[0].Trim()
    $bullets = @($lines | Where-Object { $_ -match '^\s*-\s+' } | ForEach-Object { $_.Trim() })
    $changelog += [PSCustomObject]@{
        Tag    = $tag
        Title  = $title
        Notes  = $bullets
    }
}

Write-Host "  Found $($changelog.Count) dev releases since ${lastStableTag}:" -ForegroundColor Gray
foreach ($entry in $changelog) {
    $noteCount = $entry.Notes.Count
    Write-Host "    $($entry.Tag): $($entry.Title) ($noteCount notes)" -ForegroundColor DarkGray
}
Write-Host ""

# Use auto-gathered data if parameters not provided
if (-not $ReleaseTitle) {
    $ReleaseTitle = $changelog[-1].Title
    if (-not $ReleaseTitle) { $ReleaseTitle = "Stable release $stableVersion" }
    Write-Host "  Title (from latest dev): $ReleaseTitle" -ForegroundColor Gray
}

$autoGathered = $false
if (-not $ReleaseNotes) {
    $autoGathered = $true
    $ReleaseNotes = @()
    foreach ($entry in $changelog) {
        foreach ($note in $entry.Notes) {
            $ReleaseNotes += $note -replace '^\s*-\s+', ''
        }
    }
    Write-Host "  Auto-gathered $($ReleaseNotes.Count) changelog entries" -ForegroundColor Gray
}

if ($ReleaseNotes.Count -eq 0) {
    Write-Host ""
    Write-Host "ERROR: No changelog entries found. Dev tags may have empty annotations." -ForegroundColor Red
    Write-Host "Provide -ReleaseNotes manually." -ForegroundColor Yellow
    Write-Host ""
    throw 'Release preparation failed; see details above.'
}

# --- Build PR body (markdown, grouped by dev release) ---

$prBodyPrefix = "## Summary`n"
$prBodyPrefix += "Promoting ``$devVersion`` to stable ``$stableVersion`` - includes $($changelog.Count) dev releases since $lastStableTag.`n`n"
$prBodyPrefix += "## Changelog`n"
$prBlocks = @($changelog | ForEach-Object { Get-ChangelogMarkdownBlock -Entry $_ })
$prBodyResult = Join-RecentBlocksWithinLimit `
    -Prefix $prBodyPrefix `
    -Blocks $prBlocks `
    -MaxLength ($githubPrBodyMaxChars - $githubBodySafetyMarginChars) `
    -TruncationNoticeFormat "> Older prerelease entries omitted to stay within GitHub PR body limits ({0} older releases omitted).`n`n"
$prBody = $prBodyResult.Text
if ($prBodyResult.OmittedCount -gt 0) {
    Write-Host "  Truncated PR body to newest $($prBodyResult.KeptCount) of $($prBodyResult.TotalCount) prerelease sections." -ForegroundColor Yellow
}

# --- Build commit/tag message (plain text, keeping newest release blocks when needed) ---

$tagBodyLimit = $githubReleaseNotesMaxChars - $githubReleaseHeading.Length - $githubBodySafetyMarginChars
$commitMsg = "$ReleaseTitle`n`n"
if ($autoGathered) {
    $tagBodyPrefix = "All changes since $($lastStableTag):`n`n"
    $tagBlocks = @($changelog | ForEach-Object { Get-ChangelogPlainTextBlock -Entry $_ })
    $tagBodyResult = Join-RecentBlocksWithinLimit `
        -Prefix $tagBodyPrefix `
        -Blocks $tagBlocks `
        -MaxLength $tagBodyLimit `
        -TruncationNoticeFormat "[Older prerelease entries omitted to stay within GitHub release note limits: {0} older releases omitted.]`n`n"
} else {
    $manualBlocks = @($ReleaseNotes | ForEach-Object { "- $_`n" })
    $tagBodyResult = Join-RecentBlocksWithinLimit `
        -Prefix "" `
        -Blocks $manualBlocks `
        -MaxLength $tagBodyLimit `
        -TruncationNoticeFormat "[Older release notes omitted to stay within GitHub release note limits: {0} older entries omitted.]`n`n"
}

if ($tagBodyResult.OmittedCount -gt 0) {
    Write-Host "  Truncated stable tag notes to newest $($tagBodyResult.KeptCount) of $($tagBodyResult.TotalCount) blocks." -ForegroundColor Yellow
}

$commitMsg += $tagBodyResult.Text

# Stable metadata and generated assets belong in the promotion PR itself.
$versionJson.web = $stableVersion
$versionJson.pty = $versionJson.pty -replace '-dev(\.\d+)?$', ''
$versionJson | ConvertTo-Json | Set-Content $versionJsonPath
node (Join-Path $PSScriptRoot 'sync-npx-launcher-version.mjs') $stableVersion
if ($LASTEXITCODE -ne 0) { throw 'Failed to sync launcher version.' }
& "$PSScriptRoot/release-frontend-preflight.ps1" -Version $stableVersion -SkipVerify
& "$PSScriptRoot/run-release-tests.ps1" -TestCategories $selectedCategories -FrontendInstalled
Invoke-ReleaseGit fetch origin main | Out-Host
if ((Invoke-ReleaseGit rev-parse origin/main) -ne $promotionBase) { throw 'main changed during preparation; synchronize before retrying.' }
$prBody += "`n`nCandidate dev commit: $candidate. Verified locally: all server release clusters."
$state = Start-ReleasePr -Branch $currentBranch -Base main -Version $stableVersion -Title $ReleaseTitle -Message $commitMsg -Body $prBody -ExpectedBase $promotionBase
Complete-TlbxRelease $state -PrepareOnly:$PrepareOnly
