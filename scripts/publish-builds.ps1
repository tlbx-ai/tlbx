#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [ValidateSet('Release','Backfill')][string]$Mode = 'Release',
    [string]$Tag,
    [uri]$ApiBase = 'https://builds.tlbx.ai',
    [switch]$LibraryOnly
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$script:BuildRepository = 'tlbx-ai/tlbx'
$script:BuildAudience = 'https://builds.tlbx.ai'
$script:BuildPlatforms = @('linux-arm64','linux-x64','osx-arm64','osx-x64','win-x64','win-x86')

function Invoke-BuildGhJson([string[]]$Arguments) {
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'GitHub release/provenance verification failed.' }
    # gh may inherit color settings from an interactive caller.
    return ([regex]::Replace(($output -join "`n"), '\x1b\[[0-?]*[ -/]*[@-~]', '') | ConvertFrom-Json -Depth 100)
}

function Get-BuildPairs($Release) {
    $releaseTag = [string]$Release.tag_name
    if ($releaseTag -cnotmatch '^v\d+\.\d+\.\d+(-dev)?$' -or
        $Release.draft -isnot [bool] -or $Release.draft -or -not $Release.published_at -or
        $Release.prerelease -isnot [bool] -or $Release.prerelease -ne $releaseTag.EndsWith('-dev')) {
        throw 'Only a public, correctly tagged GitHub release can be mirrored.'
    }
    $assets = @{}
    foreach ($asset in $Release.assets) {
        $name = [string]$asset.name
        if ($assets.ContainsKey($name) -or $asset.size -le 0 -or $asset.size -gt 1GB -or
            [string]$asset.digest -cnotmatch '^sha256:[0-9a-f]{64}$') { throw 'Duplicate, empty, oversized or undigested release asset.' }
        $assets[$name] = $asset
    }
    $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pairs = @(
        foreach ($rid in $script:BuildPlatforms) {
            $extension = if ($rid.StartsWith('win-')) { 'zip' } else { 'tar.gz' }
            $archive = "mt-$rid.$extension"; $sbom = "mt-$rid.spdx.json"
            $null = $known.Add($archive); $null = $known.Add($sbom)
            if ($assets.ContainsKey($archive) -ne $assets.ContainsKey($sbom)) { throw "Incomplete archive/SPDX pair for $rid." }
            if ($assets.ContainsKey($archive)) { [pscustomobject]@{Rid=$rid; Archive=$assets[$archive]; Sbom=$assets[$sbom]} }
        }
    )
    if ($pairs.Count -eq 0 -or @($assets.Keys | Where-Object { -not $known.Contains($_) }).Count) { throw 'Release has no complete platforms or contains unexpected assets.' }
    return $pairs
}

function Select-BuildReleases([object[]]$Releases) {
    $public = @($Releases | Where-Object {
        -not $_.draft -and $_.published_at -and $_.tag_name -cmatch '^v\d+\.\d+\.\d+(-dev)?$' -and
        $_.prerelease -is [bool] -and $_.prerelease -eq $_.tag_name.EndsWith('-dev')
    })
    foreach ($dev in @($true,$false)) {
        $public | Where-Object prerelease -eq $dev |
            Sort-Object { [version]($_.tag_name -replace '^v|\-dev$','') } -Descending | Select-Object -First 3
    }
}

function Get-BuildSourceCommit([string]$ReleaseTag) {
    $reference = Invoke-BuildGhJson @('api', "repos/$script:BuildRepository/git/ref/tags/$ReleaseTag")
    $object = $reference.object
    for ($depth=0; $depth -lt 4; $depth++) {
        if ([string]$object.sha -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid release tag object.' }
        if ($object.type -eq 'commit') { return [string]$object.sha }
        if ($object.type -ne 'tag') { throw 'Release tag does not reference a commit.' }
        $reference = Invoke-BuildGhJson @('api', "repos/$script:BuildRepository/git/tags/$($object.sha)")
        $object = $reference.object
    }
    throw 'Release tag nesting exceeds the verification bound.'
}

function Assert-BuildAsset([string]$Path, $Asset) {
    if ((Get-Item -LiteralPath $Path).Length -ne $Asset.size -or
        ('sha256:' + (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()) -cne $Asset.digest) {
        throw "GitHub asset size/SHA256 mismatch: $($Asset.name)."
    }
}

function Save-BuildAsset($Asset, [string]$Path) {
    # The URL is constructed, never supplied by a release note or redirect config.
    $url = "https://github.com/$script:BuildRepository/releases/download/$script:CurrentBuildTag/$($Asset.name)"
    for ($attempt=1; $attempt -le 3; $attempt++) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $Path -TimeoutSec 300 | Out-Null
            Assert-BuildAsset $Path $Asset
            return
        } catch {
            if ($attempt -eq 3) { throw "Release download/size/SHA256 verification failed: $($Asset.name)." }
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

function Assert-BuildSbom([string]$Path) {
    if ((Get-Item -LiteralPath $Path).Length -gt 32MB) { throw 'SPDX asset exceeds its size limit.' }
    $sbom = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 100
    if ($sbom.spdxVersion -cne 'SPDX-2.3' -or $sbom.SPDXID -cne 'SPDXRef-DOCUMENT' -or $null -eq $sbom.packages -or @($sbom.packages).Count -eq 0) {
        throw 'Missing or invalid SPDX 2.3 document.'
    }
    return $sbom
}

function Assert-BuildArchive([string]$Path, [string]$ReleaseTag, [string]$Rid, [string]$ExtractDir) {
    # Reuse only the reviewed local installer verifier, never code from an archive.
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../install.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Local installer verifier has parser errors.' }
    foreach ($name in @('Convert-DerEcdsaSignatureToP1363','Test-ReleaseMetadataSignature','Assert-SignedRelease')) {
        $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name}, $true)
        if (-not $definition) { throw "Local release verifier is missing: $name." }
        Invoke-Expression $definition.Extent.Text
    }
    $windows=$Rid.StartsWith('win-')
    $allowed=@('version.json','runtime-inputs.json','SHA256SUMS.txt','THIRD-PARTY-LICENSES.txt')
    $allowed += if ($windows) { @('mt.exe','mthost.exe','mtagenthost.exe','mttmux.exe','conpty.dll','x64/OpenConsole.exe','arm64/OpenConsole.exe','x86/OpenConsole.exe') } else { @('mt','mthost','mtagenthost','mttmux') }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $null=New-Item -ItemType Directory -Path $ExtractDir
    if ($windows) {
        $archive=[IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $total=0L
            foreach ($entry in $archive.Entries) {
                $name=$entry.FullName
                if ($name.EndsWith('/')) {
                    if ($name -cnotin @('x64/','arm64/','x86/') -or $entry.Length -ne 0) { throw 'Unsafe archive directory.' }
                    continue
                }
                $type=($entry.ExternalAttributes -shr 16) -band 0xF000
                $total += $entry.Length
                if ($name -cnotin $allowed -or -not $seen.Add($name) -or $entry.Length -le 0 -or
                    $total -gt 2GB -or ($name -in @('version.json','runtime-inputs.json','SHA256SUMS.txt') -and $entry.Length -gt 1MB) -or ($type -ne 0 -and $type -ne 0x8000)) { throw 'Unsafe or oversized release archive.' }
            }
        } finally { $archive.Dispose() }
        Expand-Archive -LiteralPath $Path -DestinationPath $ExtractDir
    } else {
        $stream=[IO.File]::OpenRead($Path)
        $gzip=[IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionMode]::Decompress)
        $reader=[System.Formats.Tar.TarReader]::new($gzip)
        try {
            $total=0L
            while ($entry=$reader.GetNextEntry()) {
                $name=$entry.Name -creplace '^\./',''
                if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory -and $entry.Name -in @('.','./')) { continue }
                $total += $entry.Length
                if ($entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile,[System.Formats.Tar.TarEntryType]::V7RegularFile) -or
                    $name -cnotin $allowed -or -not $seen.Add($name) -or $entry.Length -le 0 -or $total -gt 2GB -or
                    ($name -in @('version.json','runtime-inputs.json','SHA256SUMS.txt') -and $entry.Length -gt 1MB)) { throw 'Unsafe, duplicate or oversized release archive entry.' }
                $destination=[IO.File]::Create((Join-Path $ExtractDir $name))
                try { $entry.DataStream.CopyTo($destination) } finally { $destination.Dispose() }
            }
        } finally { $reader.Dispose(); $gzip.Dispose(); $stream.Dispose() }
    }
    foreach ($binary in $(if ($windows) { @('mt.exe','mthost.exe','mtagenthost.exe','mttmux.exe') } else { @('mt','mthost','mtagenthost') })) {
        if (-not $seen.Contains($binary)) { throw "Release archive is missing $binary." }
    }
    $channel=if ($ReleaseTag.EndsWith('-dev')) { 'dev' } else { 'stable' }
    Assert-SignedRelease -ExtractDir $ExtractDir -ExpectedVersion $ReleaseTag -ExpectedPlatform $Rid -ExpectedChannel $channel
}

function Assert-BuildProvenance([string]$ArchivePath, [string]$SbomPath, [string]$ReleaseTag, [string]$SourceCommit) {
    $common=@('--repo',$script:BuildRepository,'--signer-workflow',"$script:BuildRepository/.github/workflows/release.yml",'--source-ref',"refs/tags/$ReleaseTag",'--source-digest',$SourceCommit,'--format','json')
    $null=Invoke-BuildGhJson (@('attestation','verify',$ArchivePath)+$common)
    $attestations=@(Invoke-BuildGhJson (@('attestation','verify',$ArchivePath)+$common+@('--predicate-type','https://spdx.dev/Document/v2.3')))
    $sbomJson=Get-Content -LiteralPath $SbomPath -Raw
    $matches=@($attestations | Where-Object {
        [System.Text.Json.Nodes.JsonNode]::DeepEquals(
            [System.Text.Json.Nodes.JsonNode]::Parse($sbomJson),
            [System.Text.Json.Nodes.JsonNode]::Parse(($_.verificationResult.statement.predicate | ConvertTo-Json -Depth 100 -Compress)))
    })
    if ($matches.Count -eq 0) { throw 'Downloaded SPDX document differs from the verified archive attestation.' }
}

function Get-BuildOidcToken {
    if (-not $env:ACTIONS_ID_TOKEN_REQUEST_URL -or -not $env:ACTIONS_ID_TOKEN_REQUEST_TOKEN) { throw 'GitHub Actions OIDC is required.' }
    $separator=if ($env:ACTIONS_ID_TOKEN_REQUEST_URL.Contains('?')) { '&' } else { '?' }
    $url=$env:ACTIONS_ID_TOKEN_REQUEST_URL+$separator+'audience='+[uri]::EscapeDataString($script:BuildAudience)
    $response=Invoke-RestMethod -Uri $url -Headers @{Authorization="Bearer $env:ACTIONS_ID_TOKEN_REQUEST_TOKEN"} -TimeoutSec 30
    if (-not $response.value) { throw 'GitHub returned no OIDC token.' }
    # GitHub consumes this control command; the token is masked before any use.
    Write-Host "::add-mask::$($response.value)"
    return [string]$response.value
}

function Invoke-BuildRequest([uri]$Base, [string]$Method, [string]$RelativePath, [string]$FilePath, [string]$Json) {
    if ($Base.DnsSafeHost -cne 'builds.tlbx.ai' -or $Base.Scheme -ne 'https' -or $Base.Port -ne 443 -or $Base.UserInfo -or $Base.Query -or $Base.Fragment -or $Base.AbsolutePath -ne '/') { throw 'Build API must use an HTTPS origin on port 443.' }
    for ($attempt=1; $attempt -le 3; $attempt++) {
        $token=$null
        try {
            $token=Get-BuildOidcToken
            $parameters=@{Uri=([uri]::new($Base,$RelativePath)); Method=$Method; Headers=@{Authorization="Bearer $token"}; TimeoutSec=300; MaximumRedirection=0}
            if ($FilePath) { $parameters.InFile=$FilePath; $parameters.ContentType='application/octet-stream' }
            else { $parameters.Body=[Text.Encoding]::UTF8.GetBytes($Json); $parameters.ContentType='application/json' }
            $null=Invoke-RestMethod @parameters
            return
        } catch {
            $status=0
            if ($_.Exception.Response -and $_.Exception.Response.StatusCode) { $status=[int]$_.Exception.Response.StatusCode }
            $retry=$status -eq 0 -or $status -in @(408,425,429) -or $status -ge 500
            if (-not $retry -or $attempt -eq 3) { throw "Build API $Method failed (HTTP $status), after $attempt attempt(s). No credentials were logged." }
            Write-Warning "Build API $Method attempt $attempt/3 failed (HTTP $status); retrying identical content."
            Start-Sleep -Seconds (2*$attempt)
        } finally { $token=$null }
    }
}

function Publish-BuildRelease($Release, [uri]$Base) {
    $pairs=@(Get-BuildPairs $Release)
    $releaseTag=[string]$Release.tag_name
    $source=Get-BuildSourceCommit $releaseTag
    if ($Mode -eq 'Release' -and $env:GITHUB_SHA -and $source -cne $env:GITHUB_SHA) { throw 'Release tag commit differs from the publishing workflow.' }
    $script:CurrentBuildTag=$releaseTag
    $root=Join-Path ([IO.Path]::GetTempPath()) ('tlbx-build-publish-'+[guid]::NewGuid().ToString('N'))
    $null=New-Item -ItemType Directory -Path $root
    try {
        $inventory=@(
            foreach ($pair in $pairs) {
                $archive=Join-Path $root $pair.Archive.name; $sbom=Join-Path $root $pair.Sbom.name
                Save-BuildAsset $pair.Archive $archive; Save-BuildAsset $pair.Sbom $sbom
                $null=Assert-BuildSbom $sbom
                $extract=Join-Path $root $pair.Rid
                Assert-BuildArchive $archive $releaseTag $pair.Rid $extract
                Remove-Item -LiteralPath $extract -Recurse -Force
                Assert-BuildProvenance $archive $sbom $releaseTag $source
                foreach ($asset in @($pair.Archive,$pair.Sbom)) { [ordered]@{name=$asset.name;sha256=$asset.digest.Substring(7);size=[long]$asset.size} }
            }
        )
        $inventory=@($inventory|Sort-Object { $_.name })
        # Publication is the final operation, after every included pair is verified.
        foreach ($asset in $inventory) {
            Invoke-BuildRequest -Base $Base -Method PUT -RelativePath "api/v1/releases/$releaseTag/assets/$($asset.name)" -FilePath (Join-Path $root $asset.name)
        }
        $payload=[ordered]@{sourceCommit=$source;releaseNotes=[string]$Release.body;assets=$inventory}|ConvertTo-Json -Depth 10 -Compress
        Invoke-BuildRequest -Base $Base -Method POST -RelativePath "api/v1/releases/$releaseTag/publish" -Json $payload
        Write-Host "Published $releaseTag to the HTTPS build catalog ($($pairs.Count) verified platform pairs)."
    } finally {
        # root is an unmodified, task-created child of the OS temporary directory.
        $expected=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar+'tlbx-build-publish-'
        if (-not [IO.Path]::GetFullPath($root).StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe build-publisher cleanup path.' }
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}

if ($LibraryOnly) { return }
if ($Mode -eq 'Release') {
    if ($Tag -cnotmatch '^v\d+\.\d+\.\d+(-dev)?$') { throw 'An exact release tag is required.' }
    $release=Invoke-BuildGhJson @('api',"repos/$script:BuildRepository/releases/tags/$Tag")
    if ($release.tag_name -cne $Tag) { throw 'GitHub release tag mismatch.' }
    Publish-BuildRelease $release $ApiBase
} else {
    $pages=Invoke-BuildGhJson @('api',"repos/$script:BuildRepository/releases?per_page=100",'--paginate','--slurp')
    $releases=@($pages|ForEach-Object { $_ })
    $selected=@(Select-BuildReleases $releases)
    if ($selected.Count -eq 0) { throw 'No public releases are available for backfill.' }
    $failures=@()
    foreach ($release in $selected) {
        try { Publish-BuildRelease $release $ApiBase }
        catch { $failures += $release.tag_name; Write-Warning "Backfill failed for $($release.tag_name): $($_.Exception.Message)" }
    }
    if ($failures.Count) { throw "Backfill incomplete: $($failures -join ', '). Rerun the manual repair workflow after fixing the failure." }
}
