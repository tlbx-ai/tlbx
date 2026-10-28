$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot '.artifacts/sbom-test'
New-Item -ItemType Directory -Force $fixture | Out-Null
$binary = Join-Path $fixture 'mtconnect.exe'
$metadataPath = Join-Path $fixture 'build-info.json'
$sbom = Join-Path $fixture 'mt-win-x64.spdx.json'
[IO.File]::WriteAllBytes($binary, [byte[]]@(1,2,3,4))
$metadata = @{
    schema=1; runtimeIdentifier='win-x64'; binarySha256=(Get-FileHash $binary).Hash.ToLowerInvariant()
    module='github.com/tlbx-ai/tlbx/connecttransport'; goVersion='go1.27.3'; cgo='0'
    goos='windows'; goarch='amd64'
    runtimeLicense='BSD-3-Clause'; runtimeLicenseText='Go license fixture'
    dependencies=@(@{path='github.com/pion/webrtc/v4'; version='v4.2.25'; sum='h1:YWJjZA=='; license='MIT'; licenseText='MIT fixture'})
}
$metadata | ConvertTo-Json -Depth 8 | Set-Content $metadataPath -Encoding utf8
@{spdxVersion='SPDX-2.3'; SPDXID='SPDXRef-DOCUMENT'; packages=@(); relationships=@()} |
    ConvertTo-Json -Depth 8 | Set-Content $sbom -Encoding utf8
& "$PSScriptRoot/complete-sbom.ps1" -SbomPath $sbom -BinaryPath $binary -BuildInfoPath $metadataPath -Version '1.2.3-dev'
$first=Get-Content $sbom -Raw | ConvertFrom-Json
if($first.packages.Count -ne 3 -or $first.relationships.Count -ne 3){throw 'Go packages or static-link relationships missing'}
$pion=@($first.packages | Where-Object name -EQ 'github.com/pion/webrtc/v4')[0]
if($pion.licenseDeclared -ne 'MIT' -or $pion.sourceInfo -notlike '*h1:YWJjZA==*' -or
    $pion.externalRefs[0].referenceLocator -ne 'pkg:golang/github.com/pion/webrtc/v4@v4.2.25'){throw 'Go identity/license/checksum information lost'}
$companion=@($first.packages | Where-Object name -EQ 'tlbx Connect transport')[0]
if($companion.externalRefs[0].referenceLocator -ne 'pkg:generic/tlbx-connect-transport@1.2.3-dev?arch=win-x64'){throw 'Companion purl malformed'}
& "$PSScriptRoot/complete-sbom.ps1" -SbomPath $sbom -BinaryPath $binary -BuildInfoPath $metadataPath -Version '1.2.3-dev'
$second=Get-Content $sbom -Raw | ConvertFrom-Json
if($second.packages.Count -ne 3 -or $second.relationships.Count -ne 3){throw 'SBOM completion is not idempotent'}
$metadata.runtimeIdentifier='linux-x64'
$metadata | ConvertTo-Json -Depth 8 | Set-Content $metadataPath -Encoding utf8
$rejected=$false
try { & "$PSScriptRoot/complete-sbom.ps1" -SbomPath $sbom -BinaryPath $binary -BuildInfoPath $metadataPath -Version '1.2.3-dev' } catch { $rejected=$true }
if(-not $rejected){throw 'Wrong-RID metadata accepted'}
$metadata.runtimeIdentifier='win-x64'
$metadata | ConvertTo-Json -Depth 8 | Set-Content $metadataPath -Encoding utf8
[IO.File]::WriteAllBytes($binary, [byte[]]@(9,8,7,6))
$rejected=$false
try { & "$PSScriptRoot/complete-sbom.ps1" -SbomPath $sbom -BinaryPath $binary -BuildInfoPath $metadataPath -Version '1.2.3-dev' } catch { $rejected=$true }
if(-not $rejected){throw 'Tampered Go binary accepted'}
Write-Host 'Connect SPDX: identity/license/checksum, idempotence, RID and binary-integrity checks passed.'
