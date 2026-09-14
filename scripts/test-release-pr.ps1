#!/usr/bin/env pwsh
# Real local Git repositories, with only the GitHub boundary replaced. Never publishes externally.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/release-pr.ps1"
$root = Join-Path ([IO.Path]::GetTempPath()) ('tlbx-pr-tests-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $root 'work'
$remote = Join-Path $root 'remote.git'
$script:checks = 0
function Assert-Test([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }; $script:checks++
}
function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Assert-Test $failed $Message
}
$global:TlbxPrFixture = @{ Pr=$null; Prs=@(); Creates=0; Merges=0; FailChecks=$false; ChangeHead=$false; Remote=$remote }
function global:gh {
    $a = @($args); $f = $global:TlbxPrFixture; $global:LASTEXITCODE=0
    if (@($a | Where-Object { $_ -is [array] }).Count) { throw 'Nested command arguments would become System.Object[] at the native gh boundary.' }
    if ($a[0] -eq 'api') {
        $matches = @($f.Prs | Where-Object state -EQ 'MERGED' | ForEach-Object {
            @{number=$_.number;merged_at='2026-01-01';merge_commit_sha=$_.mergeCommit.oid;base=@{ref=$_.baseRefName}}
        })
        ConvertTo-Json -InputObject @(@($matches)) -Depth 8 -Compress
        return
    }
    if ($a[0] -ne 'pr') { throw "Unexpected gh command in fixture: $a" }
    switch ($a[1]) {
        'list' {
            $head = $a[[Array]::IndexOf($a, '--head')+1]
            $base = $a[[Array]::IndexOf($a, '--base')+1]
            $state = $a[[Array]::IndexOf($a, '--state')+1]
            $matches = @($f.Prs | Where-Object { $_.headRefName -eq $head -and $_.baseRefName -eq $base -and ($state -eq 'all' -or $_.state -eq $state) })
            ConvertTo-Json -InputObject $matches -Depth 6 -Compress
        }
        'create' {
            $f.Creates++
            $base = $a[[Array]::IndexOf($a, '--base')+1]
            $head = $a[[Array]::IndexOf($a, '--head')+1]
            $sha = git --git-dir=$($f.Remote) rev-parse "refs/heads/$head"
            $f.Pr = @{number=(41+$f.Creates);state='OPEN';isDraft=$false;headRefName=$head;headRefOid=$sha;baseRefName=$base;url='https://example.test/pull/42';mergeCommit=@{oid=''}}
            $f.Prs += $f.Pr
            $f.Pr.url
        }
        'view' {
            $f.Pr = @($f.Prs | Where-Object number -EQ ([int]$a[2]))[0]
            $copy = $f.Pr.Clone()
            if ($f.ChangeHead) { $copy.headRefOid='unverified' }
            ConvertTo-Json -InputObject $copy -Depth 6 -Compress
        }
        'checks' {
            if ($f.FailChecks) { '[{"name":"fixture","bucket":"fail"}]'; $global:LASTEXITCODE=1 }
            else { '[{"name":"fixture","bucket":"pass"}]' }
        }
        'merge' {
            $expected = $a[[Array]::IndexOf($a, '--match-head-commit')+1]
            if ($expected -ne $f.Pr.headRefOid -or $a -contains '--admin') { throw 'Merge bypassed the expected head.' }
            $base = $f.Pr.baseRefName
            $baseHead = git --git-dir=$($f.Remote) rev-parse "refs/heads/$base"
            $tree = git --git-dir=$($f.Remote) rev-parse "$expected^{tree}"
            $sha = 'Fixture PR merge' | git --git-dir=$($f.Remote) -c user.name=Test -c user.email=test@example.test commit-tree $tree -p $baseHead -p $expected
            if ($LASTEXITCODE -ne 0) { throw 'Fixture merge commit failed.' }
            git --git-dir=$($f.Remote) update-ref "refs/heads/$base" $sha $baseHead
            $f.Pr.state='MERGED';$f.Pr.mergeCommit=@{oid=$sha};$f.Merges++
        }
        default { throw "Unexpected gh operation: $a" }
    }
}
New-Item -ItemType Directory -Path $root | Out-Null
Push-Location $root
try {
    Invoke-ReleaseGit init --bare --initial-branch=dev $remote | Out-Null
    Invoke-ReleaseGit clone $remote $repo | Out-Null
    Set-Location $repo
    Invoke-ReleaseGit config user.name Test
    Invoke-ReleaseGit config user.email test@example.test
    New-Item -ItemType Directory src | Out-Null
    '{"web":"1.0.0-dev","pty":"1.0.0-dev"}' | Set-Content src/version.json
    Invoke-ReleaseGit add -A
    Invoke-ReleaseGit commit -m Initial | Out-Null
    Invoke-ReleaseGit push origin dev | Out-Null
    $base = Invoke-ReleaseGit rev-parse HEAD
    Assert-Rejected { Assert-ReleaseTaskBranch } 'Protected dev accepted as task branch.'
    Invoke-ReleaseGit switch -c fix/pr-fixture | Out-Null
    Assert-Test ((Assert-ReleaseTaskBranch) -eq 'fix/pr-fixture') 'Valid task branch rejected.'
    '{"web":"1.0.1-dev","pty":"1.0.0-dev"}' | Set-Content src/version.json
    Assert-Rejected { Assert-ReleaseClean } 'Dirty checkout accepted.'
    $state = Start-ReleasePr -Branch fix/pr-fixture -Base dev -Version 1.0.1-dev -Title 'Fixture release' -Message "Fixture release`n`n- Test PR release behavior." -Body 'Fixture' -ExpectedBase $base
    $head = Invoke-ReleaseGit rev-parse HEAD
    Complete-TlbxRelease $state -PrepareOnly
    Complete-TlbxRelease (Get-ReleaseState fix/pr-fixture) -PrepareOnly
    Assert-Test ($global:TlbxPrFixture.Creates -eq 1) 'Preparation duplicated the PR.'
    Assert-Test ((Invoke-ReleaseGit rev-parse HEAD) -eq $head) 'Retry created another release commit.'
    Assert-Test (@(Invoke-ReleaseGit ls-remote --tags origin).Count -eq 0) 'Preparation published an unmerged tag.'
    $global:TlbxPrFixture.FailChecks=$true
    Assert-Rejected { Complete-TlbxRelease $state } 'Failed checks allowed publication.'
    $global:TlbxPrFixture.FailChecks=$false
    $global:TlbxPrFixture.ChangeHead=$true
    Assert-Rejected { Complete-TlbxRelease $state } 'Changed PR head was accepted.'
    $global:TlbxPrFixture.ChangeHead=$false
    $global:TlbxPrFixture.Pr.isDraft=$true
    Assert-Rejected { Complete-TlbxRelease $state } 'Draft PR was merged.'
    $global:TlbxPrFixture.Pr.isDraft=$false
    $state.ExpectedBase='changed'
    Assert-Rejected { Complete-TlbxRelease $state } 'Changed integration base was accepted.'
    $state.ExpectedBase=$base
    Complete-TlbxRelease $state
    $merged = $state.Merge
    Assert-Test ($merged -ne $head) 'Fixture did not produce a real merge commit.'
    Assert-Test ((Invoke-ReleaseGit rev-parse 'v1.0.1-dev^{}') -eq $merged) 'Tag points at task tip instead of merged commit.'
    Complete-TlbxRelease (Get-ReleaseState fix/pr-fixture)
    Assert-Test ($global:TlbxPrFixture.Creates -eq 1 -and $global:TlbxPrFixture.Merges -eq 1) 'Retry duplicated PR or merge.'
    Assert-Test (@(Invoke-ReleaseGit ls-remote --tags --refs origin).Count -eq 1) 'Retry duplicated release tags.'
    $state.Version='9.9.9-dev'
    Assert-Rejected { Publish-MergedReleaseTag $state } 'Mismatched merged version was tagged.'
    $state.Version='1.0.1-dev';$state.Tree='changed'
    Assert-Rejected { Publish-MergedReleaseTag $state } 'Unverified merged tree was tagged.'
    $state.Tree=Invoke-ReleaseGit rev-parse 'HEAD^{tree}'
    Assert-Rejected { Reset-ReleasePreparation $state } 'Merged release was re-prepared.'
    # Resume commit recovery must retain the prepared version/head.
    $state.Head=''
    Complete-TlbxRelease $state -PrepareOnly
    Assert-Test ($state.Head -eq $head) 'Interrupted state write did not recover the original commit.'
    # Exercise the actual promotion entry point with only expensive build commands
    # stubbed. Git history, source metadata, PRs, merge/tag order and sync are real.
    $sourceScripts = $PSScriptRoot
    Invoke-ReleaseGit switch dev | Out-Null
    Invoke-ReleaseGit pull --ff-only origin dev | Out-Null
    New-Item -ItemType Directory scripts | Out-Null
    Copy-Item "$sourceScripts/promote.ps1", "$sourceScripts/release-pr.ps1", "$sourceScripts/release-test-clusters.ps1", "$sourceScripts/verify-release-merge.ps1" scripts/
    '$global:TlbxFixturePreflight = $args -join " "' | Set-Content scripts/release-frontend-preflight.ps1
    'param([string[]]$TestCategories, [switch]$FrontendInstalled) $global:TlbxFixtureTests = $TestCategories -join " "' | Set-Content scripts/run-release-tests.ps1
    function global:node { $global:LASTEXITCODE=0 }
    Invoke-ReleaseGit add -A
    Invoke-ReleaseGit commit -m 'Fixture build stubs' | Out-Null
    $fixtureHead = Invoke-ReleaseGit rev-parse HEAD
    Invoke-ReleaseGit push origin "${fixtureHead}:refs/heads/dev" | Out-Null
    Invoke-ReleaseGit push origin "${base}:refs/heads/main" | Out-Null
    Invoke-ReleaseGit tag -a v1.0.0 $base -m 'Initial stable'
    Invoke-ReleaseGit push origin refs/tags/v1.0.0 | Out-Null
    Invoke-ReleaseGit switch dev | Out-Null
    Invoke-ReleaseGit pull --ff-only origin dev | Out-Null
    & ./scripts/promote.ps1 -TestCategories all -PrepareOnly
    $promotion = Get-ReleaseState chore/promote-1-0-1
    Assert-Test ($promotion.Version -eq '1.0.1') 'Promotion prepared an incorrect stable version.'
    Assert-Test ($global:TlbxFixturePreflight -match '1.0.1' -and $global:TlbxFixtureTests -match 'runtime') 'Promotion skipped stable verification.'
    Assert-Test ((Get-Content src/version.json -Raw | ConvertFrom-Json).web -eq '1.0.1') 'Stable metadata was not in the PR.'
    # Development may advance after the promotion candidate has been frozen.
    Invoke-ReleaseGit switch dev | Out-Null
    'Later development stays on dev.' | Set-Content new-dev.txt
    Invoke-ReleaseGit add new-dev.txt
    Invoke-ReleaseGit commit -m 'Later dev work' | Out-Null
    Invoke-ReleaseGit push origin dev | Out-Null
    Invoke-ReleaseGit switch chore/promote-1-0-1 | Out-Null
    & ./scripts/promote.ps1 -TestCategories all
    $promotion = Get-ReleaseState chore/promote-1-0-1
    Assert-Test ((Invoke-ReleaseGit rev-parse 'v1.0.1^{}') -eq $promotion.Merge) 'Stable tag was not on the main merge commit.'
    Invoke-ReleaseGit fetch origin dev main | Out-Null
    Invoke-ReleaseGit merge-base --is-ancestor origin/main origin/dev | Out-Null
    Assert-Test ((Invoke-ReleaseGit show origin/dev:new-dev.txt) -eq 'Later development stays on dev.') 'Stable synchronization lost newer dev work.'
    $null = & git cat-file -e origin/main:new-dev.txt 2>$null
    Assert-Test ($LASTEXITCODE -ne 0) 'Promotion accidentally included later dev work.'
    $mergeCount = $global:TlbxPrFixture.Merges
    & ./scripts/promote.ps1 -TestCategories all
    Assert-Test ($global:TlbxPrFixture.Merges -eq $mergeCount) 'Stable retry duplicated a promotion or sync PR.'
    Invoke-ReleaseGit switch --detach $promotion.Merge | Out-Null
    & ./scripts/verify-release-merge.ps1 -Tag v1.0.1
    $script:checks++
    Assert-Rejected { & ./scripts/verify-release-merge.ps1 -Tag v1.0.2 } 'Release gate accepted a mismatched version.'
    Invoke-ReleaseGit switch --detach $promotion.Head | Out-Null
    Assert-Rejected { & ./scripts/verify-release-merge.ps1 -Tag v1.0.1 } 'Release gate accepted the unmerged task commit.'
    foreach ($name in @('release-pr','release-dev','release-local','promote','release','finish-task','verify-release-merge')) {
        $tokens=$null;$errors=$null
        [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot "$name.ps1"),[ref]$tokens,[ref]$errors) | Out-Null
        Assert-Test ($errors.Count -eq 0) "PowerShell parse errors in $name."
    }
    Write-Host "PR release behavior: $script:checks checks passed."
} finally {
    Pop-Location
    Remove-Item Function:\gh -ErrorAction SilentlyContinue
    Remove-Item Function:\node -ErrorAction SilentlyContinue
    Remove-Variable TlbxPrFixture -Scope Global -ErrorAction SilentlyContinue
    $resolved = [IO.Path]::GetFullPath($root)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'tlbx-pr-tests-*') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
