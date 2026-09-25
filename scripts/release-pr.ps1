# GitHub workflow for this repository only. Dot-source; no work on import.
$script:TlbxReleaseRepo = 'tlbx-ai/tlbx'

function Write-ReleaseProgress {
    param([string]$Message)
    $line = "[$([DateTime]::UtcNow.ToString('HH:mm:ss')) UTC] $Message"
    # A separate flushed journal stays readable even when a parent buffers stdout.
    $dir = Invoke-ReleaseGit rev-parse --absolute-git-dir
    [IO.File]::AppendAllText((Join-Path $dir 'tlbx-release-progress.log'), "$line`n")
    [Console]::Error.WriteLine($line)
    [Console]::Error.Flush()
}

function Invoke-ReleaseGit {
    $result = & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed." }
    return $result
}

function Invoke-ReleaseGh {
    # gh can inherit forced ANSI output from an interactive terminal.
    $oldForce = $env:GH_FORCE_TTY
    $env:GH_FORCE_TTY = ''
    try {
        $result = & gh @args
        if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' ') failed. Re-run the same release command to resume." }
        return ($result | ForEach-Object { [regex]::Replace([string]$_, '\x1b\[[0-?]*[ -/]*[@-~]', '') })
    } finally { $env:GH_FORCE_TTY = $oldForce }
}

function Assert-ReleaseTaskBranch {
    $branch = Invoke-ReleaseGit branch --show-current
    if ($branch -notmatch '^(feat|fix|chore)/[a-z0-9]+(?:-[a-z0-9]+)*$') {
        throw 'Start a task branch first: feat/<short-kebab-description>, fix/<short-kebab-description>, or chore/<short-kebab-description>. dev and main only receive PRs.'
    }
    return $branch
}

function Assert-ReleaseClean {
    if (Invoke-ReleaseGit status --porcelain) { throw 'Commit intended changes first. Release preparation requires a clean checkout.' }
}

function Get-ReleaseStatePath {
    param([string]$Branch)
    $dir = Invoke-ReleaseGit rev-parse --absolute-git-dir
    return Join-Path $dir ('tlbx-release-' + $Branch.Replace('/', '-') + '.json')
}

function Save-ReleaseState {
    param($State)
    $path = Get-ReleaseStatePath $State.Branch
    $State | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$path.tmp" -Encoding utf8
    Move-Item -LiteralPath "$path.tmp" -Destination $path -Force
}

function Get-ReleaseState {
    param([string]$Branch)
    $path = Get-ReleaseStatePath $Branch
    if (Test-Path -LiteralPath $path) { return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
}

function Reset-ReleasePreparation {
    param($State)
    if ($State.Pr) {
        $pr = Invoke-ReleaseGh pr view $State.Pr --repo $script:TlbxReleaseRepo --json state | ConvertFrom-Json
        if ($pr.state -ne 'OPEN') { throw 'Only an open, unmerged release may be re-prepared.' }
    }
    Assert-ReleaseClean
    # Keep the prior evidence for diagnosis; the next preparation runs every selected check.
    $path = Get-ReleaseStatePath $State.Branch
    Copy-Item -LiteralPath $path -Destination "$path.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')).previous"
}

function Start-ReleasePr {
    param([string]$Branch, [string]$Base, [string]$Version, [string]$Title,
          [string]$Message, [string]$Body, [string]$ExpectedBase)
    Invoke-ReleaseGit add -A | Out-Host
    $state = [pscustomobject]@{
        Branch=$Branch; Base=$Base; Version=$Version; Title=$Title; Message=$Message; Body=$Body
        ExpectedBase=$ExpectedBase; StartHead=(Invoke-ReleaseGit rev-parse HEAD)
        Tree=(Invoke-ReleaseGit write-tree); Head=''; Pr=0; Merge=''; SyncPr=0; SyncBranch=''
    }
    # Save before committing so interruption cannot silently allocate a second version.
    Save-ReleaseState $state
    & git diff --cached --quiet
    if ($LASTEXITCODE -eq 1) {
        $Message | & git commit -F - | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Release commit failed. Prepared state was retained.' }
    } elseif ($LASTEXITCODE -ne 0) { throw 'Could not inspect prepared release changes.' }
    $state.Head = Invoke-ReleaseGit rev-parse HEAD
    Save-ReleaseState $state
    return $state
}

function Get-OrCreateReleasePr {
    param([string]$Branch, [string]$Base, [string]$Title, [string]$Body)
    $prs = @(Invoke-ReleaseGh pr list --repo $script:TlbxReleaseRepo --head $Branch --base $Base --state all --json 'number,state,headRefOid' | ConvertFrom-Json)
    $head = Invoke-ReleaseGit rev-parse HEAD
    $match = @($prs | Where-Object { $_.state -eq 'OPEN' -or ($_.state -eq 'MERGED' -and $_.headRefOid -eq $head) })
    if ($match.Count -gt 1) { throw "Multiple PRs match $Branch -> $Base; inspect them before continuing." }
    if ($match.Count -eq 1 -and $match[0].state -eq 'MERGED') { return $match[0].number }
    $bodyPath = [IO.Path]::GetTempFileName()
    try {
        Set-Content -LiteralPath $bodyPath -Value $Body -Encoding utf8
        if ($match.Count -eq 1) {
            Invoke-ReleaseGh pr edit $match[0].number --repo $script:TlbxReleaseRepo --title $Title --body-file $bodyPath | Out-Host
            return $match[0].number
        }
        Invoke-ReleaseGh pr create --repo $script:TlbxReleaseRepo --head $Branch --base $Base --title $Title --body-file $bodyPath | Out-Host
    } finally { Remove-Item -LiteralPath $bodyPath -ErrorAction SilentlyContinue }
    $prs = @(Invoke-ReleaseGh pr list --repo $script:TlbxReleaseRepo --head $Branch --base $Base --state open --json number | ConvertFrom-Json)
    if ($prs.Count -ne 1) { throw 'Could not identify the newly created release PR.' }
    return $prs[0].number
}

function Complete-ReleasePrMerge {
    param([int]$Number, [string]$Head, [string]$Base, [string]$ExpectedBase)
    $deadline = [DateTime]::UtcNow.AddMinutes(45)
    while ($true) {
        $pr = Invoke-ReleaseGh pr view $Number --repo $script:TlbxReleaseRepo --json 'state,isDraft,headRefOid,baseRefName,mergeCommit,url' | ConvertFrom-Json
        if ($pr.headRefOid -ne $Head -or $pr.baseRefName -ne $Base) { throw "PR #$Number changed head or target. Refusing to merge unverified work." }
        if ($pr.state -eq 'MERGED') { return $pr.mergeCommit.oid }
        if ($pr.state -ne 'OPEN' -or $pr.isDraft) { throw "PR #$Number is closed or draft; resolve it before resuming." }
        $checksText = & gh pr checks $Number --repo $script:TlbxReleaseRepo --required --json 'name,bucket'
        $checksExit = $LASTEXITCODE
        if ($checksExit -notin @(0,1,8)) { throw "Could not read required checks for PR #$Number." }
        $checks = @(([regex]::Replace(($checksText -join "`n"), '\x1b\[[0-?]*[ -/]*[@-~]', '')) | ConvertFrom-Json)
        if (@($checks | Where-Object bucket -In @('fail','cancel')).Count) { throw "Required checks failed for $($pr.url). Fix them, then resume." }
        # An empty set is not proof that protection/check registration is ready.
        if ($checks.Count -gt 0 -and @($checks | Where-Object bucket -NE 'pass').Count -eq 0) { break }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Checks are still pending: $($pr.url). Re-run this release command later." }
        Write-ReleaseProgress "Waiting for required checks: $($pr.url)"
        Start-Sleep -Seconds 30
    }
    Invoke-ReleaseGit fetch origin "refs/heads/${Base}:refs/remotes/origin/$Base" | Out-Host
    if ($ExpectedBase -and (Invoke-ReleaseGit rev-parse "origin/$Base") -ne $ExpectedBase) {
        throw "origin/$Base advanced after preparation. Merge the new base into the task branch, then prepare again; never publish an unreviewed combination. See docs/BUILD-RELEASE.md."
    }
    Invoke-ReleaseGh pr merge $Number --repo $script:TlbxReleaseRepo --merge --match-head-commit $Head | Out-Host
    $pr = Invoke-ReleaseGh pr view $Number --repo $script:TlbxReleaseRepo --json 'state,mergeCommit' | ConvertFrom-Json
    if ($pr.state -ne 'MERGED' -or -not $pr.mergeCommit.oid) { throw "PR #$Number has not merged; no tag was created." }
    return $pr.mergeCommit.oid
}

function Publish-MergedReleaseTag {
    param($State)
    Invoke-ReleaseGit fetch origin "refs/heads/$($State.Base):refs/remotes/origin/$($State.Base)" | Out-Host
    Invoke-ReleaseGit merge-base --is-ancestor $State.Merge "origin/$($State.Base)" | Out-Null
    Invoke-ReleaseGit merge-base --is-ancestor $State.Head $State.Merge | Out-Null
    $version = Invoke-ReleaseGit show "$($State.Merge):src/version.json" | ConvertFrom-Json
    if ($version.web -ne $State.Version) { throw 'Merged version does not match the prepared release.' }
    # With an unchanged target, the merge must contain exactly the prepared tree.
    if ((Invoke-ReleaseGit rev-parse "$($State.Merge)^{tree}") -ne $State.Tree) { throw 'Merged content differs from the verified release tree. No tag was published.' }
    $tag = "v$($State.Version)"
    $remote = @(Invoke-ReleaseGit ls-remote --tags origin "refs/tags/$tag" "refs/tags/$tag^{}")
    if ($remote.Count) {
        $peeled = @($remote | Where-Object { $_ -match '\^\{\}$' })
        if ($peeled.Count -ne 1 -or ($peeled[0] -split '\s+')[0] -ne $State.Merge) { throw "$tag already exists at another commit or is not annotated." }
        Write-Host "$tag already submitted at $($State.Merge); no duplicate publication."
        return
    }
    $existing = & git rev-parse -q --verify "refs/tags/$tag" 2>$null
    if ($LASTEXITCODE -eq 0) {
        if ((Invoke-ReleaseGit rev-parse "$tag^{}") -ne $State.Merge) { throw "Local $tag belongs to another commit." }
    } else {
        $State.Message | & git tag -a $tag $State.Merge -F -
        if ($LASTEXITCODE -ne 0) { throw 'Annotated release tag creation failed.' }
    }
    Invoke-ReleaseGit push origin "refs/tags/$tag" | Out-Host
    Write-Host "Submitted $tag at merged commit $($State.Merge). Publication is complete only when Release CI and assets succeed."
}

function Sync-StableRelease {
    param($State)
    Invoke-ReleaseGit fetch origin main dev | Out-Host
    & git merge-base --is-ancestor origin/main origin/dev
    if ($LASTEXITCODE -eq 0) { Write-Host 'dev already contains main.'; return }
    $original = Invoke-ReleaseGit branch --show-current
    Assert-ReleaseClean
    $syncBranch = 'chore/sync-main-' + $State.Version.Replace('.', '-')
    $State.SyncBranch = $syncBranch
    Save-ReleaseState $State
    try {
        $null = & git rev-parse -q --verify "refs/heads/$syncBranch" 2>$null
        if ($LASTEXITCODE -eq 0) {
            Invoke-ReleaseGit switch $syncBranch | Out-Host
        } else {
            Invoke-ReleaseGit switch -c $syncBranch origin/dev | Out-Host
        }
        # A separate branch can incorporate new dev commits without changing main.
        Invoke-ReleaseGit merge --no-edit origin/dev | Out-Host
        Invoke-ReleaseGit merge --no-edit origin/main | Out-Host
        $head = Invoke-ReleaseGit rev-parse HEAD
        Invoke-ReleaseGit push --set-upstream origin "HEAD:refs/heads/$syncBranch" | Out-Host
        $body = "Bring stable $($State.Version) metadata and ancestry back into dev, preserving newer development. Required PR checks verify the combined result."
        $State.SyncPr = Get-OrCreateReleasePr -Branch $syncBranch -Base dev -Title "Sync stable $($State.Version) into dev" -Body $body
        Save-ReleaseState $State
        $null = Complete-ReleasePrMerge -Number $State.SyncPr -Head $head -Base dev
        Invoke-ReleaseGit fetch origin dev | Out-Host
        Invoke-ReleaseGit merge-base --is-ancestor origin/main origin/dev | Out-Null
        Write-Host "Synchronization PR #$($State.SyncPr) merged. dev contains stable main."
    } finally {
        if (-not (Invoke-ReleaseGit status --porcelain)) {
            Invoke-ReleaseGit switch $original | Out-Host
        } else {
            Write-Warning "Synchronization needs conflict resolution on $syncBranch. Commit the resolution, return to $original and resume promotion."
        }
    }
    # Do not force-delete: a failed or changed synchronization remains recoverable.
    Invoke-ReleaseGit branch -d $syncBranch | Out-Host
}

function Complete-TlbxRelease {
    param($State, [switch]$PrepareOnly)
    # Recovery after a commit completed but its state write was interrupted.
    if (-not $State.Head) {
        $head = Invoke-ReleaseGit rev-parse HEAD
        if ($head -eq $State.StartHead -and (Invoke-ReleaseGit write-tree) -eq $State.Tree) {
            & git diff --cached --quiet
            if ($LASTEXITCODE -eq 1) {
                $State.Message | & git commit -F - | Out-Host
                if ($LASTEXITCODE -ne 0) { throw 'Could not finish the prepared release commit.' }
            } elseif ($LASTEXITCODE -ne 0) { throw 'Could not inspect interrupted release changes.' }
        } elseif ((Invoke-ReleaseGit rev-parse 'HEAD^') -ne $State.StartHead -or (Invoke-ReleaseGit rev-parse 'HEAD^{tree}') -ne $State.Tree) {
            throw 'Interrupted preparation no longer matches this checkout. Inspect the retained release state.'
        }
        $State.Head = Invoke-ReleaseGit rev-parse HEAD
        Save-ReleaseState $State
    }
    Assert-ReleaseClean
    if ((Invoke-ReleaseGit branch --show-current) -ne $State.Branch -or (Invoke-ReleaseGit rev-parse HEAD) -ne $State.Head) {
        throw 'The prepared branch changed. Re-prepare after verifying the changes; see docs/BUILD-RELEASE.md.'
    }
    if (-not $State.Pr) {
        Invoke-ReleaseGit push --set-upstream origin "HEAD:refs/heads/$($State.Branch)" | Out-Host
        $State.Pr = Get-OrCreateReleasePr -Branch $State.Branch -Base $State.Base -Title $State.Title -Body $State.Body
        Save-ReleaseState $State
    }
    Write-ReleaseProgress "Release PR: https://github.com/$script:TlbxReleaseRepo/pull/$($State.Pr)"
    if ($PrepareOnly) { Write-Host 'Prepared only. Re-run without -PrepareOnly to merge and tag after checks pass.'; return }
    $State.Merge = Complete-ReleasePrMerge -Number $State.Pr -Head $State.Head -Base $State.Base -ExpectedBase $State.ExpectedBase
    Save-ReleaseState $State
    Publish-MergedReleaseTag $State
    if ($State.Base -eq 'main') { Sync-StableRelease $State }
    Write-Host "Retire task after Release CI succeeds: ./scripts/finish-task.ps1 (clean, exclusively owned checkout only)."
    Write-Host $State.Message
}
