param(
    [Parameter(Mandatory)][string]$SbomPath,
    [string]$RuntimeIdentifier,
    [string]$BinaryPath,
    [string]$BuildInfoPath,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
if (-not $RuntimeIdentifier -and (Split-Path $SbomPath -Leaf) -match '^mt-(win-x64|win-x86|linux-x64|linux-arm64|osx-x64|osx-arm64)\.spdx\.json$') {
    $RuntimeIdentifier = $Matches[1]
}
if ($RuntimeIdentifier -notin @('win-x64','win-x86','linux-x64','linux-arm64','osx-x64','osx-arm64')) { throw 'A supported release RID is required.' }
if (-not $BinaryPath) {
    $filename = if ($RuntimeIdentifier.StartsWith('win-')) { 'mtconnect.exe' } else { 'mtconnect' }
    $BinaryPath = Join-Path $PSScriptRoot "bin/$RuntimeIdentifier/$filename"
}
if (-not $BuildInfoPath) { $BuildInfoPath = Join-Path (Split-Path $BinaryPath -Parent) 'build-info.json' }
if (-not $Version) { $Version = (Get-Content (Join-Path $PSScriptRoot '../version.json') -Raw | ConvertFrom-Json).web }
$metadata = Get-Content -LiteralPath $BuildInfoPath -Raw | ConvertFrom-Json
$binaryHash = (Get-FileHash -LiteralPath $BinaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
$platform = @{
    'win-x64'='windows/amd64'; 'win-x86'='windows/386'; 'linux-x64'='linux/amd64'
    'linux-arm64'='linux/arm64'; 'osx-x64'='darwin/amd64'; 'osx-arm64'='darwin/arm64'
}[$RuntimeIdentifier]
if ($metadata.schema -ne 1 -or $metadata.runtimeIdentifier -ne $RuntimeIdentifier -or
    $metadata.binarySha256 -ne $binaryHash -or $metadata.module -ne 'github.com/tlbx-ai/tlbx/connecttransport' -or
    "$($metadata.goos)/$($metadata.goarch)" -ne $platform -or $metadata.goVersion -notmatch '^go\d+\.\d+\.\d+$' -or
    $metadata.cgo -ne '0' -or -not $metadata.dependencies.Count) {
    throw 'Connect SBOM metadata does not match the actual compiled release companion.'
}
$document = Get-Content -LiteralPath $SbomPath -Raw | ConvertFrom-Json -AsHashtable
if ($document.spdxVersion -ne 'SPDX-2.3' -or -not $document.SPDXID) { throw 'A valid SPDX 2.3 document is required.' }
if (-not $document.ContainsKey('packages')) { $document.packages = @() }
if (-not $document.ContainsKey('relationships')) { $document.relationships = @() }
if (-not $document.ContainsKey('hasExtractedLicensingInfos')) { $document.hasExtractedLicensingInfos = @() }

function Get-ConnectSpdxId([string]$Identity) {
    return 'SPDXRef-Connect-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Identity))).Substring(0, 24).ToLowerInvariant()
}
function Add-ConnectRelationship([string]$From, [string]$Relationship, [string]$To) {
    if (-not @($document.relationships | Where-Object { $_.spdxElementId -eq $From -and $_.relationshipType -eq $Relationship -and $_.relatedSpdxElement -eq $To }).Count) {
        $document.relationships += @{ spdxElementId = $From; relationshipType = $Relationship; relatedSpdxElement = $To }
    }
}
function Add-ConnectPackage {
    param([string]$Name,[string]$PackageVersion,[string]$Purl,[string]$Download,[string]$License,[string]$LicenseText,[string]$SourceInfo,[string]$Checksum)
    if (-not $License -or -not $LicenseText) { throw "Missing Go package licensing: $Name" }
    if ($License.StartsWith('LicenseRef-') -and -not @($document.hasExtractedLicensingInfos | Where-Object licenseId -EQ $License).Count) {
        $document.hasExtractedLicensingInfos += @{ licenseId = $License; extractedText = $LicenseText; name = "$Name license" }
    }
    $existing = @($document.packages | Where-Object { @($_.externalRefs | Where-Object referenceLocator -EQ $Purl).Count })
    if ($existing.Count -gt 1) { throw "Duplicate Go package PURL in SPDX: $Purl" }
    if ($existing.Count -eq 1) {
        $existing[0].licenseDeclared = $License
        $existing[0].sourceInfo = $SourceInfo
        if ($Checksum) { $existing[0].checksums = @(@{ algorithm = 'SHA256'; checksumValue = $Checksum }) }
        return $existing[0].SPDXID
    }
    $id = Get-ConnectSpdxId $Purl
    if (@($document.packages | Where-Object SPDXID -EQ $id).Count) { throw 'Conflicting Connect SPDX package identifier.' }
    $package = @{
        name = $Name; SPDXID = $id; versionInfo = $PackageVersion
        downloadLocation = $Download; filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'; licenseDeclared = $License
        copyrightText = 'NOASSERTION'; sourceInfo = $SourceInfo
        externalRefs = @(@{ referenceCategory = 'PACKAGE-MANAGER'; referenceType = 'purl'; referenceLocator = $Purl })
    }
    if ($Checksum) { $package.checksums = @(@{ algorithm = 'SHA256'; checksumValue = $Checksum }) }
    $document.packages += $package
    return $id
}

$companionId = Add-ConnectPackage -Name 'tlbx Connect transport' -PackageVersion $Version `
    -Purl "pkg:generic/tlbx-connect-transport@${Version}?arch=$RuntimeIdentifier" -Download 'https://github.com/tlbx-ai/tlbx' `
    -License 'AGPL-3.0-only' -LicenseText 'See the tlbx repository LICENSE.' `
    -SourceInfo "Native Go companion embedded in mt as manifest resource mtconnect.runtime; RID $RuntimeIdentifier." -Checksum $binaryHash
Add-ConnectRelationship $document.SPDXID 'DESCRIBES' $companionId
$goVersion = $metadata.goVersion -replace '^go',''
$runtimeId = Add-ConnectPackage -Name 'Go runtime and standard library' -PackageVersion $goVersion `
    -Purl "pkg:generic/golang/go@$goVersion" -Download "https://go.dev/dl/go$goVersion.src.tar.gz" `
    -License $metadata.runtimeLicense -LicenseText $metadata.runtimeLicenseText `
    -SourceInfo "Go runtime statically linked into the embedded Connect companion; compiler $($metadata.goVersion)."
Add-ConnectRelationship $companionId 'STATIC_LINK' $runtimeId
foreach ($dependency in $metadata.dependencies) {
    if ($dependency.sum -notmatch '^h1:[A-Za-z0-9+/]+=*$' -or -not $dependency.path -or -not $dependency.version) { throw 'Incomplete compiled Go module metadata.' }
    $versionPart = [Uri]::EscapeDataString($dependency.version)
    $id = Add-ConnectPackage -Name $dependency.path -PackageVersion $dependency.version `
        -Purl "pkg:golang/$($dependency.path)@$($dependency.version)" `
        -Download "https://proxy.golang.org/$($dependency.path)/@v/$versionPart.zip" `
        -License $dependency.license -LicenseText $dependency.licenseText `
        -SourceInfo "Compiled Go module; Go content checksum $($dependency.sum). This h1 checksum identifies module content, not the archive's raw SHA256."
    Add-ConnectRelationship $companionId 'STATIC_LINK' $id
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($SbomPath), ($document | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
Write-Host "SPDX completed with Connect runtime and $($metadata.dependencies.Count) compiled Go dependencies for $RuntimeIdentifier."
