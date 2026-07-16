#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Promotes the current dev version to a stable release on main.

.DESCRIPTION
    This script automates the promotion of a dev release to stable:
    1. Verifies we're on dev branch with a -dev version
    2. Auto-gathers changelog from all dev tag annotations since the last stable release
    3. Creates and merges a PR from dev to main
    4. Updates src/version.json to remove -dev suffix
    5. Creates a git tag and pushes to trigger GitHub Actions build

.PARAMETER ReleaseTitle
    Optional. A concise title for this release (one line, no version number).
    If omitted, uses the most recent dev release title.

.PARAMETER ReleaseNotes
    Optional. Array of detailed changelog entries. If omitted, automatically
    gathered from all dev tag annotations since the last stable release.

.EXAMPLE
    # Auto-gather all changelog items (recommended)
    .\promote.ps1

.EXAMPLE
    # Override title, still auto-gather notes
    .\promote.ps1 -ReleaseTitle "Major UI overhaul"

.EXAMPLE
    # Fully manual (legacy behavior)
    .\promote.ps1 -ReleaseTitle "Version management improvements" -ReleaseNotes @(
        "Centralized version management: src/version.json is now single source of truth",
        "Fixed update failures where wrong version was baked into binaries"
    )
#>

param(
    [string]$ReleaseTitle,
    [string[]]$ReleaseNotes
)

$ErrorActionPreference = "Stop"
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

# Ensure we're on dev branch
$currentBranch = git branch --show-current
if ($currentBranch -ne "dev") {
    Write-Host ""
    Write-Host "ERROR: promote.ps1 must be run from the dev branch." -ForegroundColor Red
    Write-Host "Current branch: $currentBranch" -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

# Read current version
$versionJsonPath = "$PSScriptRoot\..\src\version.json"
$versionJson = Get-Content $versionJsonPath | ConvertFrom-Json
$devVersion = $versionJson.web

# Verify it's a dev version
if ($devVersion -notmatch '-dev$') {
    Write-Host ""
    Write-Host "ERROR: Current version '$devVersion' is not a dev version." -ForegroundColor Red
    Write-Host "Only versions ending in -dev can be promoted." -ForegroundColor Yellow
    Write-Host ""
    exit 1
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

# Ensure dev is up to date
Write-Host "Syncing with remote..." -ForegroundColor Gray
git fetch origin 2>$null
git pull origin dev 2>&1 | Out-Null

# Check for uncommitted changes
$status = git status --porcelain
if ($status) {
    Write-Host ""
    Write-Host "ERROR: Uncommitted changes in working directory." -ForegroundColor Red
    Write-Host "Commit or stash changes before promoting." -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

# --- Auto-gather changelog from dev tags since last stable release ---

Write-Host "Gathering changelog from dev releases since $lastStableTag..." -ForegroundColor Gray

# Get all dev tags sorted by version, filter to those newer than last stable
$allDevTags = git tag --sort=version:refname | Where-Object { $_ -match '-dev$' }
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
    exit 1
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
    exit 1
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

# Create PR from dev to main
Write-Host "Creating PR from dev to main..." -ForegroundColor Gray

$prBodyPath = [System.IO.Path]::GetTempFileName()
$prOutputPath = [System.IO.Path]::GetTempFileName()
$prListOutputPath = [System.IO.Path]::GetTempFileName()
$prUrl = ""

try {
    Set-Content -LiteralPath $prBodyPath -Value $prBody -Encoding utf8
    gh pr create --base main --head dev --title $ReleaseTitle --body-file $prBodyPath *> $prOutputPath
    $prCreateOutput = (Get-Content -LiteralPath $prOutputPath -Raw).Trim()
    if ($LASTEXITCODE -ne 0) {
        # PR might already exist
        if ($prCreateOutput -match "already exists") {
            Write-Host "  PR already exists, finding it..." -ForegroundColor Yellow
            gh pr list --head dev --base main --json url --jq '.[0].url' *> $prListOutputPath
            if ($LASTEXITCODE -ne 0) {
                $prListOutput = (Get-Content -LiteralPath $prListOutputPath -Raw).Trim()
                Write-Host "ERROR: Failed to locate existing PR: $prListOutput" -ForegroundColor Red
                exit 1
            }

            $prUrl = (Get-Content -LiteralPath $prListOutputPath -Raw).Trim()
        } else {
            Write-Host "ERROR: Failed to create PR: $prCreateOutput" -ForegroundColor Red
            exit 1
        }
    } else {
        $prUrl = $prCreateOutput
    }
}
finally {
    Remove-Item -LiteralPath $prBodyPath, $prOutputPath, $prListOutputPath -ErrorAction SilentlyContinue
}

if ([string]::IsNullOrWhiteSpace($prUrl)) {
    Write-Host "ERROR: Failed to resolve promote PR URL." -ForegroundColor Red
    exit 1
}

Write-Host "  PR: $prUrl" -ForegroundColor Gray

# Merge the PR
Write-Host "Merging PR..." -ForegroundColor Gray
gh pr merge --merge --delete-branch=false 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to merge PR. Check GitHub for details." -ForegroundColor Red
    exit 1
}

# Switch to main and pull
Write-Host "Switching to main..." -ForegroundColor Gray
git checkout main 2>&1 | Out-Null
git pull origin main 2>&1 | Out-Null

# Update version.json to stable version
Write-Host "Updating version to $stableVersion..." -ForegroundColor Gray
$versionJson = Get-Content $versionJsonPath | ConvertFrom-Json
$versionJson.web = $stableVersion
$versionJson.pty = $versionJson.pty -replace '-dev(\.\d+)?$', ''
$versionJson | ConvertTo-Json | Set-Content $versionJsonPath
$syncNpxLauncherScript = Join-Path $PSScriptRoot "sync-npx-launcher-version.mjs"
node $syncNpxLauncherScript $stableVersion
if ($LASTEXITCODE -ne 0) { throw "Failed to sync npx launcher version" }

# Commit, tag, and push
Write-Host "Committing and tagging v$stableVersion..." -ForegroundColor Gray
git add -A
$commitMsg | git commit -F -
if ($LASTEXITCODE -ne 0) { throw "git commit failed" }

$commitMsg | git tag -a "v$stableVersion" -F -
if ($LASTEXITCODE -ne 0) { throw "git tag failed" }

git push origin main
if ($LASTEXITCODE -ne 0) { throw "git push main failed" }

git push origin "v$stableVersion"
if ($LASTEXITCODE -ne 0) { throw "git push tag failed" }

# Switch back to dev and sync
Write-Host "Syncing dev with main..." -ForegroundColor Gray
git checkout dev 2>&1 | Out-Null
git merge main -m "Merge main v$stableVersion into dev" 2>&1 | Out-Null
git push origin dev 2>&1 | Out-Null

Write-Host ""
Write-Host "Promoted v$stableVersion ($($changelog.Count) dev releases, $($ReleaseNotes.Count) changelog entries)" -ForegroundColor Green
Write-Host "Monitor build: https://github.com/tlbx-ai/tlbx/actions" -ForegroundColor Cyan
Write-Host ""
