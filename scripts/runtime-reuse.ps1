# Host reuse is scoped to an attested release archive, never an untrusted build cache.
function Get-RuntimeInputFingerprint {
    param([string]$RepoRoot, [string]$Rid, [string]$Configuration, [string]$PtyVersion, [string]$SdkVersion)
    $paths = @(& git -C $RepoRoot ls-files -- 'src/Ai.Tlbx.MidTerm.TtyHost' 'src/Ai.Tlbx.MidTerm.AgentHost' 'src/Ai.Tlbx.MidTerm.Common' 'src/Ai.Tlbx.MidTerm.TmuxShim' 'src/Directory.*' 'Directory.*' 'global.json' '*NuGet.Config' '*nuget.config' '.github/workflows/release.yml' 'scripts/publish-runtime-set.ps1' 'scripts/runtime-reuse.ps1' 'scripts/copy-windows-conpty-runtime.ps1' 'src/Ai.Tlbx.MidTerm/src/static/favicon/favicon.ico')
    if ($LASTEXITCODE -ne 0 -or $paths.Count -eq 0) { throw 'Cannot enumerate runtime build inputs.' }
    $rows = @("runtime-inputs-v1", $Rid, $Configuration, $PtyVersion, $SdkVersion)
    $paths = @($paths | Sort-Object -CaseSensitive)
    # Hash canonical Git bytes in one process; include local edits, avoid CRLF differences.
    $hashes = @($paths | & git -C $RepoRoot hash-object --stdin-paths)
    if ($LASTEXITCODE -ne 0 -or $hashes.Count -ne $paths.Count) { throw 'Cannot hash runtime inputs.' }
    for ($index = 0; $index -lt $paths.Count; $index++) {
        $rows += "$($paths[$index])=$($hashes[$index])"
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($rows -join "`n")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-RuntimePublishDirectories {
    param([string]$RepoRoot, [string]$Rid, [string]$Configuration)
    $tfm = if ($Rid.StartsWith('win-')) { 'net10.0-windows10.0.19041.0' } else { 'net10.0' }
    return @{
        mthost = Join-Path $RepoRoot "src/Ai.Tlbx.MidTerm.TtyHost/bin/$Configuration/$tfm/$Rid/publish"
        mtagenthost = Join-Path $RepoRoot "src/Ai.Tlbx.MidTerm.AgentHost/bin/$Configuration/net10.0/$Rid/publish"
        mttmux = Join-Path $RepoRoot "src/Ai.Tlbx.MidTerm.TmuxShim/bin/$Configuration/net10.0/$Rid/publish"
    }
}

function Test-RuntimeReuseMetadata {
    param($Metadata, [string]$Fingerprint, [string]$Rid, [string]$PtyVersion)
    return $null -ne $Metadata -and $Metadata.schema -eq 1 -and
        $Metadata.fingerprint -eq $Fingerprint -and $Metadata.rid -eq $Rid -and $Metadata.pty -eq $PtyVersion
}

function Assert-RuntimeArchivePaths {
    param([string[]]$Paths)
    foreach ($path in $Paths) {
        $normalized = $path.Replace('\', '/')
        if ($normalized.StartsWith('/') -or $normalized -match '(^|/)\.\.(/|$)|:' ) {
            throw "Unsafe runtime archive entry: $path"
        }
    }
}

function Restore-ReleasedHostRuntimes {
    param([string]$RepoRoot, [string]$Rid, [string]$Configuration, $Version, [string]$Fingerprint)
    # Stable builds always rebuild for their own version/signing policy.
    if (-not $Version.webOnly -or $Version.web -notmatch '-dev(?:\.|$)' -or $Version.pty -eq $Version.web) { return $false }
    if ($Version.pty -notmatch '^\d+\.\d+\.\d+(?:-dev(?:\.\d+)?)?$') { throw 'Invalid runtime source version.' }
    $tag = "v$($Version.pty)"
    $extension = if ($Rid.StartsWith('win-')) { 'zip' } else { 'tar.gz' }
    $asset = "mt-$Rid.$extension"
    $work = Join-Path $RepoRoot ".artifacts/runtime-reuse/$Rid/$tag"
    New-Item -ItemType Directory -Force $work | Out-Null
    $archive = Join-Path $work $asset
    # A missing baseline is a normal cold build. Verification failures are not.
    & gh release download $tag --repo tlbx-ai/tlbx --pattern $asset --dir $work --clobber *> (Join-Path $work 'download.log')
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Runtime baseline $tag/$asset unavailable; building hosts. Details: $work/download.log"
        return $false
    }
    & gh attestation verify $archive --repo tlbx-ai/tlbx --signer-workflow tlbx-ai/tlbx/.github/workflows/release.yml --source-ref "refs/tags/$tag" *> (Join-Path $work 'attestation.log')
    if ($LASTEXITCODE -ne 0) { throw "Runtime baseline provenance verification failed: $work/attestation.log" }
    $unpack = Join-Path $work ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $unpack | Out-Null
    if ($extension -eq 'zip') {
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try { Assert-RuntimeArchivePaths -Paths @($zip.Entries.FullName) } finally { $zip.Dispose() }
        Expand-Archive -LiteralPath $archive -DestinationPath $unpack
    } else {
        $entries = @(& tar -tzf $archive)
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect runtime archive.' }
        Assert-RuntimeArchivePaths -Paths $entries
        & tar -xzf $archive -C $unpack
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract runtime archive.' }
    }
    $metadataPath = Join-Path $unpack 'runtime-inputs.json'
    if (-not (Test-Path $metadataPath)) {
        Write-Host 'Runtime baseline predates input metadata; building hosts.'
        return $false
    }
    $metadata = Get-Content $metadataPath -Raw | ConvertFrom-Json
    if (-not (Test-RuntimeReuseMetadata $metadata $Fingerprint $Rid $Version.pty)) {
        Write-Host 'Runtime inputs, SDK or configuration changed; building hosts.'
        return $false
    }
    $manifest = Get-Content (Join-Path $unpack 'version.json') -Raw | ConvertFrom-Json
    if ($manifest.pty -ne $Version.pty -or $manifest.platform -ne $Rid) { throw 'Runtime baseline manifest mismatch.' }
    $dirs = Get-RuntimePublishDirectories $RepoRoot $Rid $Configuration
    $ext = if ($Rid.StartsWith('win-')) { '.exe' } else { '' }
    $names = @('mthost','mtagenthost')
    if ($Rid.StartsWith('win-')) { $names += 'mttmux' }
    foreach ($name in $names) {
        $file = "$name$ext"
        $source = Join-Path $unpack $file
        if ((Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.checksums.$file) {
            throw "Runtime baseline checksum mismatch: $file"
        }
        New-Item -ItemType Directory -Force $dirs[$name] | Out-Null
        Copy-Item -LiteralPath $source -Destination $dirs[$name] -Force
    }
    if ($Rid.StartsWith('win-')) {
        & (Join-Path $RepoRoot 'scripts/copy-windows-conpty-runtime.ps1') -SourceDir $unpack -DestinationDir $dirs.mthost -Rid $Rid
    }
    Write-Host "Reused verified host runtimes from $tag ($Rid)." -ForegroundColor Green
    return $true
}
