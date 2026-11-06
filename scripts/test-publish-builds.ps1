#!/usr/bin/env pwsh
# Offline protocol fixtures: no GitHub/API calls, credentials or publication.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/publish-builds.ps1" -LibraryOnly
$script:checks=0
function Test-BuildCheck([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Test-BuildReject([scriptblock]$Action,[string]$Message) {
    $failed=$false
    try { & $Action | Out-Null } catch { $failed=$true }
    Test-BuildCheck $failed $Message
}
function New-TestRelease([string]$ReleaseTag='v1.2.3-dev',[string]$Rid='win-x64') {
    $extension=if ($Rid.StartsWith('win-')) { 'zip' } else { 'tar.gz' }
    return [pscustomobject]@{tag_name=$ReleaseTag;draft=$false;published_at='2026-11-01T00:00:00Z';prerelease=$ReleaseTag.EndsWith('-dev');body='Release notes';assets=@(
        [pscustomobject]@{name="mt-$Rid.$extension";size=100L;digest=('sha256:'+('a'*64))},
        [pscustomobject]@{name="mt-$Rid.spdx.json";size=50L;digest=('sha256:'+('b'*64))}
    )}
}
$root=Join-Path ([IO.Path]::GetTempPath()) ('tlbx-build-tests-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$savedSha=$env:GITHUB_SHA
try {
    $env:GITHUB_SHA=$null
    foreach ($rid in $script:BuildPlatforms) {
        foreach ($tag in @('v1.2.3-dev','v1.2.3')) { Test-BuildCheck (@(Get-BuildPairs (New-TestRelease $tag $rid)).Count -eq 1) "Complete pair rejected: $rid." }
    }
    foreach ($mutation in @(
        {param($r) $r.draft=$true},
        {param($r) $r.published_at=$null},
        {param($r) $r.prerelease=$false},
        {param($r) $r.tag_name='../v1.2.3-dev'},
        {param($r) $r.assets=@($r.assets[0])},
        {param($r) $r.assets=@()},
        {param($r) $r.assets[0].size=0},
        {param($r) $r.assets[0].size=1GB+1},
        {param($r) $r.assets[0].digest='sha256:bad'},
        {param($r) $r.assets+= $r.assets[0]},
        {param($r) $r.assets[0].name='unexpected.zip'}
    )) {
        $release=New-TestRelease; & $mutation $release
        Test-BuildReject { Get-BuildPairs $release } 'Invalid/incomplete public release was accepted.'
    }
    $releases=@(foreach($version in @('1.2.1','1.2.3','1.2.9','1.2.10')){New-TestRelease "v$version-dev";New-TestRelease "v$version"})
    $draft=New-TestRelease 'v9.0.0-dev';$draft.draft=$true;$releases+=$draft
    $selection=@(Select-BuildReleases $releases)
    Test-BuildCheck (($selection.tag_name -join ',') -ceq 'v1.2.10-dev,v1.2.9-dev,v1.2.3-dev,v1.2.10,v1.2.9,v1.2.3') 'Backfill did not select the latest three public semantic versions per channel.'

    $assetPath=Join-Path $root 'asset.zip';[IO.File]::WriteAllText($assetPath,'same bytes')
    $asset=[pscustomobject]@{name='asset.zip';size=(Get-Item $assetPath).Length;digest=('sha256:'+(Get-FileHash $assetPath).Hash.ToLowerInvariant())}
    Assert-BuildAsset $assetPath $asset; $script:checks++
    [IO.File]::WriteAllText($assetPath,'changed bytes')
    Test-BuildReject {Assert-BuildAsset $assetPath $asset} 'Changed asset bytes were accepted.'
    $sbomPath=Join-Path $root 'sbom.json'
    [IO.File]::WriteAllText($sbomPath,'{"spdxVersion":"SPDX-2.3","SPDXID":"SPDXRef-DOCUMENT","packages":[{"name":"fixture"}]}')
    $null=Assert-BuildSbom $sbomPath;$script:checks++
    [IO.File]::WriteAllText($sbomPath,'{"spdxVersion":"SPDX-2.2","SPDXID":"SPDXRef-DOCUMENT","packages":[]}')
    Test-BuildReject {Assert-BuildSbom $sbomPath} 'Invalid SPDX document was accepted.'
    [IO.File]::WriteAllText($sbomPath,'{"spdxVersion":"SPDX-2.3","SPDXID":"SPDXRef-DOCUMENT","packages":null}')
    Test-BuildReject {Assert-BuildSbom $sbomPath} 'SPDX without package inventory was accepted.'
    & {
        $script:tagCalls=0
        function Invoke-BuildGhJson { param($Arguments) $script:tagCalls++; if($script:tagCalls -eq 1){return @{object=@{type='tag';sha=('a'*40)}}};return @{object=@{type='commit';sha=('b'*40)}} }
        Test-BuildCheck ((Get-BuildSourceCommit 'v1.2.3-dev') -ceq ('b'*40)) 'Annotated tag was not dereferenced to the exact source commit.'
        function Invoke-BuildGhJson { param($Arguments) return @{object=@{type='tag';sha=('a'*40)}} }
        Test-BuildReject {Get-BuildSourceCommit 'v1.2.3-dev'} 'Unbounded annotated tag recursion.'
    }
    & {
        $script:requests=[Collections.Generic.List[object]]::new()
        $script:tokens=0;$script:failures=2;$script:status=503
        function Get-BuildOidcToken { $script:tokens++;return "fixture-$script:tokens" }
        function Start-Sleep { param($Seconds) }
        function Invoke-RestMethod {
            param($Uri,$Method,$Headers,$TimeoutSec,$MaximumRedirection,$InFile,$ContentType,$Body)
            $script:requests.Add(@{Token=$Headers.Authorization;Path=$Uri.AbsolutePath;File=$InFile;Body=$Body;Redirects=$MaximumRedirection})
            if($script:failures -gt 0){$script:failures--; $fixtureError=[Exception]::new('fixture transport failure');$fixtureError|Add-Member Response ([pscustomobject]@{StatusCode=$script:status});throw $fixtureError}
        }
        Invoke-BuildRequest -Base 'https://builds.tlbx.ai' -Method PUT -RelativePath 'api/v1/releases/v1.2.3-dev/assets/mt-win-x64.zip' -FilePath $assetPath
        Test-BuildCheck ($script:requests.Count -eq 3 -and $script:tokens -eq 3 -and ($script:requests.Token -join ',') -ceq 'Bearer fixture-1,Bearer fixture-2,Bearer fixture-3') 'Retry did not obtain a fresh OIDC token for every HTTP attempt.'
        Test-BuildCheck (@($script:requests|Where-Object {$_.File -cne $assetPath -or $_.Redirects -ne 0}).Count -eq 0) 'Retry changed raw upload bytes or permitted credential redirects.'
        $script:requests.Clear();$script:failures=5;$script:status=409
        Test-BuildReject {Invoke-BuildRequest -Base 'https://builds.tlbx.ai' -Method POST -RelativePath 'api/v1/releases/v1.2.3-dev/publish' -Json '{}'} 'Conflicting publication was accepted.'
        Test-BuildCheck ($script:requests.Count -eq 1) 'Permanent conflict was retried.'
        $script:requests.Clear();$script:failures=5;$script:status=503
        Test-BuildReject {Invoke-BuildRequest -Base 'https://builds.tlbx.ai' -Method POST -RelativePath 'api/v1/releases/v1.2.3-dev/publish' -Json '{}'} 'Permanent server failure was hidden.'
        Test-BuildCheck ($script:requests.Count -eq 3) 'Server failure exceeded three attempts.'
        $before=$script:tokens
        Test-BuildReject {Invoke-BuildRequest -Base 'http://builds.tlbx.ai' -Method POST -RelativePath 'publish' -Json '{}'} 'HTTP endpoint was accepted.'
        Test-BuildReject {Invoke-BuildRequest -Base 'https://builds.tlbx.ai:444' -Method POST -RelativePath 'publish' -Json '{}'} 'Non-443 endpoint was accepted.'
        Test-BuildReject {Invoke-BuildRequest -Base 'https://other.example' -Method POST -RelativePath 'publish' -Json '{}'} 'OIDC token could be sent to another HTTPS host.'
        Test-BuildCheck ($script:tokens -eq $before) 'Invalid endpoint requested credentials.'
    }
    & {
        $script:events=[Collections.Generic.List[string]]::new();$script:sent=[Collections.Generic.List[object]]::new();$script:rejectProvenance=$false
        function Get-BuildSourceCommit {param($ReleaseTag) return ('c'*40)}
        function Save-BuildAsset {param($Asset,$Path) [IO.File]::WriteAllText($Path,'fixture');$script:events.Add("download:$($Asset.name)")}
        function Assert-BuildSbom {param($Path) $script:events.Add('spdx')}
        function Assert-BuildArchive {param($Path,$ReleaseTag,$Rid,$ExtractDir) $null=New-Item -ItemType Directory -Path $ExtractDir;$script:events.Add('signature')}
        function Assert-BuildProvenance {param($ArchivePath,$SbomPath,$ReleaseTag,$SourceCommit) if($script:rejectProvenance){throw 'fixture provenance mismatch'};$script:events.Add('provenance')}
        function Invoke-BuildRequest {param($Base,$Method,$RelativePath,$FilePath,$Json) $script:events.Add($Method);$script:sent.Add(@{Method=$Method;Path=$RelativePath;Json=$Json;Bytes=$(if($FilePath){[IO.File]::ReadAllText($FilePath)}else{''})})}
        $release=New-TestRelease
        Publish-BuildRelease $release 'https://builds.tlbx.ai'
        Test-BuildCheck (($script:events -join ',') -ceq 'download:mt-win-x64.zip,download:mt-win-x64.spdx.json,spdx,signature,provenance,PUT,PUT,POST') 'Publish occurred before complete verification/uploads.'
        $first=$script:sent.ToArray();$script:events.Clear();$script:sent.Clear()
        Publish-BuildRelease $release 'https://builds.tlbx.ai'
        Test-BuildCheck (($first|ConvertTo-Json -Depth 5 -Compress) -ceq ($script:sent.ToArray()|ConvertTo-Json -Depth 5 -Compress)) 'Resuming changed the bytes or publication payload.'
        $payload=$script:sent[2].Json|ConvertFrom-Json
        Test-BuildCheck ($payload.sourceCommit -ceq ('c'*40) -and $payload.releaseNotes -ceq 'Release notes' -and @($payload.assets).Count -eq 2) 'Publication metadata changed.'
        $script:sent.Clear();$script:rejectProvenance=$true
        Test-BuildReject {Publish-BuildRelease $release 'https://builds.tlbx.ai'} 'Invalid provenance was accepted.'
        Test-BuildCheck ($script:sent.Count -eq 0) 'Failed verification uploaded or published assets.'
        $script:rejectProvenance=$false;$env:GITHUB_SHA=('d'*40)
        Test-BuildReject {Publish-BuildRelease $release 'https://builds.tlbx.ai'} 'Wrong workflow source commit was accepted.'
        $env:GITHUB_SHA=$null
    }
    & {
        [IO.File]::WriteAllText($sbomPath,'{"spdxVersion":"SPDX-2.3","SPDXID":"SPDXRef-DOCUMENT","packages":[{"name":"fixture"}]}')
        $script:provenanceMismatch=$false
        function Invoke-BuildGhJson {
            param($Arguments)
            if($Arguments -contains '--predicate-type'){
                $predicate=Get-Content $sbomPath -Raw|ConvertFrom-Json
                if($script:provenanceMismatch){$predicate.packages[0].name='other'}
                return @{verificationResult=@{statement=@{predicate=$predicate}}}
            }
            return @{verificationResult=@{statement=@{}}}
        }
        Assert-BuildProvenance $assetPath $sbomPath 'v1.2.3-dev' ('c'*40);$script:checks++
        $script:provenanceMismatch=$true
        Test-BuildReject {Assert-BuildProvenance $assetPath $sbomPath 'v1.2.3-dev' ('c'*40)} 'Downloaded SPDX did not match its signed attestation.'
    }
    & {
        $savedUrl=$env:ACTIONS_ID_TOKEN_REQUEST_URL;$savedRuntime=$env:ACTIONS_ID_TOKEN_REQUEST_TOKEN
        try {
            $env:ACTIONS_ID_TOKEN_REQUEST_URL='https://oidc.example/token?api-version=2';$env:ACTIONS_ID_TOKEN_REQUEST_TOKEN='synthetic-runtime'
            $script:oidcUri=$null;$script:oidcAuthorization=$null;$script:maskCommand=$null
            function Invoke-RestMethod {param($Uri,$Headers,$TimeoutSec) $script:oidcUri=$Uri;$script:oidcAuthorization=$Headers.Authorization;return @{value='synthetic.oidc.fixture'}}
            function Write-Host {param($Object) $script:maskCommand=$Object}
            $token=Get-BuildOidcToken
            Test-BuildCheck ($token -ceq 'synthetic.oidc.fixture' -and $script:oidcUri.EndsWith('&audience=https%3A%2F%2Fbuilds.tlbx.ai') -and $script:oidcAuthorization -ceq 'Bearer synthetic-runtime') 'OIDC request did not bind the build audience.'
            Test-BuildCheck ($script:maskCommand -ceq '::add-mask::synthetic.oidc.fixture') 'OIDC token was not masked before use.'
        } finally {$env:ACTIONS_ID_TOKEN_REQUEST_URL=$savedUrl;$env:ACTIONS_ID_TOKEN_REQUEST_TOKEN=$savedRuntime}
    }
    # Malicious archive entries must fail before extraction; no native executable runs.
    $unsafe=Join-Path $root 'unsafe.zip';$zip=[IO.Compression.ZipFile]::Open($unsafe,[IO.Compression.ZipArchiveMode]::Create)
    try {$entry=$zip.CreateEntry('../outside');$stream=$entry.Open();try{$stream.WriteByte(1)}finally{$stream.Dispose()}}finally{$zip.Dispose()}
    Test-BuildReject {Assert-BuildArchive $unsafe 'v1.2.3-dev' 'win-x64' (Join-Path $root 'unsafe-extract')} 'Traversal archive was accepted.'
    $invalidRoot=Join-Path $root 'invalid-source';$null=New-Item -ItemType Directory -Path $invalidRoot
    foreach($name in @('mt.exe','mthost.exe','mtagenthost.exe','mttmux.exe')){[IO.File]::WriteAllText((Join-Path $invalidRoot $name),'not executable')}
    [IO.File]::WriteAllText((Join-Path $invalidRoot 'version.json'),'{"signatureVersion":2,"signedPayload":"e30=","metadataSignature":"AA=="}')
    $invalid=Join-Path $root 'invalid-signature.zip';Compress-Archive -Path "$invalidRoot/*" -DestinationPath $invalid
    Test-BuildReject {Assert-BuildArchive $invalid 'v1.2.3-dev' 'win-x64' (Join-Path $root 'invalid-extract')} 'Invalid manifest signature was accepted.'
} finally {
    $env:GITHUB_SHA=$savedSha
    $expected=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar+'tlbx-build-tests-'
    if(-not [IO.Path]::GetFullPath($root).StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup path.'}
    Remove-Item -LiteralPath $root -Recurse -Force
}
Write-Host "HTTPS build publisher: $script:checks offline protocol checks passed."
$global:LASTEXITCODE=0
