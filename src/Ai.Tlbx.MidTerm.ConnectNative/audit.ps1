#!/usr/bin/env pwsh
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RuntimeIdentifier)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$pin = Get-Content (Join-Path $PSScriptRoot 'native-stack.json') -Raw | ConvertFrom-Json
$security = Join-Path $repoRoot '.artifacts/connect-dotnet-transport/security'
$arm = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64'
$platform = if ($IsWindows) { 'windows_amd64.zip' } elseif ($IsMacOS) { if ($arm) { 'darwin_arm64.tar.gz' } else { 'darwin_amd64.tar.gz' } } else { if ($arm) { 'linux_arm64.tar.gz' } else { 'linux_amd64.tar.gz' } }
$toolRoot = Join-Path $security "grype-$($pin.grype.version)-$($platform -replace '\.(zip|tar.gz)$','')"
$tool = Join-Path $toolRoot $(if ($IsWindows) { 'grype.exe' } else { 'grype' })
$archive = Join-Path $security "grype_$($pin.grype.version)_$platform"
New-Item -ItemType Directory -Force -Path $security, $toolRoot | Out-Null
if (-not (Test-Path $archive)) {
    Invoke-WebRequest "https://github.com/anchore/grype/releases/download/v$($pin.grype.version)/grype_$($pin.grype.version)_$platform" -OutFile $archive
}
if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pin.grype.archives.$platform) {
    throw 'Pinned Grype archive integrity check failed.'
}
# Re-extract the authenticated tool rather than trusting an unrelated cached executable.
if ($IsWindows) { Expand-Archive -LiteralPath $archive -DestinationPath $toolRoot -Force }
else {
    & tar -xzf $archive -C $toolRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract pinned Grype.' }
}
$previous = @{}
foreach ($name in @('GRYPE_DB_CACHE_DIR','GRYPE_CHECK_FOR_APP_UPDATE','GRYPE_DB_VALIDATE_AGE','GRYPE_DB_MAX_ALLOWED_BUILT_AGE','GRYPE_DB_AUTO_UPDATE')) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name)
}
try {
    $env:GRYPE_DB_CACHE_DIR = Join-Path $security 'grype/db'
    $env:GRYPE_CHECK_FOR_APP_UPDATE = 'false'
    $env:GRYPE_DB_VALIDATE_AGE = 'true'
    $env:GRYPE_DB_MAX_ALLOWED_BUILT_AGE = '72h'
    $env:GRYPE_DB_AUTO_UPDATE = 'true'
    & $tool db update *> (Join-Path $security "db-update-$RuntimeIdentifier.log")
    if ($LASTEXITCODE -ne 0) { throw 'Native vulnerability database update failed.' }
    $status = & $tool db status -o json
    if ($LASTEXITCODE -ne 0) { throw 'Native vulnerability database status failed.' }
    $database = $status | ConvertFrom-Json
    if (-not $database.valid -or [DateTimeOffset]::UtcNow - [DateTimeOffset]$database.built -gt [TimeSpan]::FromHours(72)) {
        throw 'Native vulnerability database is invalid or too old.'
    }
    $status | Set-Content (Join-Path $security "db-status-$RuntimeIdentifier.json")
    $sbomPath = Join-Path $PSScriptRoot "bin/$RuntimeIdentifier/native-dependencies.spdx.json"
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $PSScriptRoot "bin/$RuntimeIdentifier/bundle.zip"))
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry('manifest.json').Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        $record = @($manifest.files | Where-Object name -eq 'native-dependencies.spdx.json')
        $graph = $zip.GetEntry('native-dependencies.spdx.json').Open()
        try { $embeddedHash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($graph)) } finally { $graph.Dispose() }
        if ($manifest.runtimeIdentifier -ne $RuntimeIdentifier -or $manifest.versions.vcpkgCommit -ne $pin.vcpkgCommit -or
            $record.Count -ne 1 -or $embeddedHash -ne $record[0].sha256 -or
            (Get-FileHash $sbomPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $embeddedHash) {
            throw 'Native audit SBOM does not match the exact embedded bundle.'
        }
    } finally { $zip.Dispose() }
    $candidate = Get-Content $sbomPath -Raw
    $coverage = @((($candidate | ConvertFrom-Json).packages | Where-Object SPDXID -like '*-port') | ForEach-Object {
        $cpes = @($_.externalRefs | Where-Object referenceType -eq cpe23Type | Select-Object -ExpandProperty referenceLocator)
        [pscustomobject]@{ name=$_.name; version=$_.versionInfo; explicitCpes=$cpes; upstreamReviewRequired=($cpes.Count -eq 0 -and $_.name -ne 'openssl') }
    })
    $coverage | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $security "native-$RuntimeIdentifier-coverage.json")
    # Both controls must work: inferred OpenSSL CPE and explicitly mapped GLib CPE.
    foreach ($control in @(@{Name='openssl';Version='3.5.0';Cve='CVE-2025-15467'}, @{Name='glib';Version='2.80.0';Cve='CVE-2024-52533'})) {
        $document = $candidate | ConvertFrom-Json
        $component = @($document.packages | Where-Object { $_.name -eq $control.Name -and $_.SPDXID -like '*-port' })
        if ($component.Count -ne 1) { throw "Missing native audit control component: $($control.Name)." }
        $component[0].versionInfo = $control.Version
        if ($control.Name -eq 'glib') {
            $cpe = @($component[0].externalRefs | Where-Object referenceType -eq cpe23Type)
            if ($cpe.Count -ne 1) { throw 'GLib vulnerability identity is missing or ambiguous.' }
            $cpe[0].referenceLocator = $pin.cpeProducts.glib.Replace('{version}', $control.Version)
        }
        $controlPath = Join-Path $security "positive-$($control.Name)-$RuntimeIdentifier.spdx.json"
        $reportPath = Join-Path $security "positive-$($control.Name)-$RuntimeIdentifier.json"
        $document | ConvertTo-Json -Depth 100 | Set-Content $controlPath
        & $tool "sbom:$controlPath" --add-cpes-if-none --fail-on high -o json 1> $reportPath 2> (Join-Path $security "positive-$($control.Name)-$RuntimeIdentifier.stderr.log")
        if ($LASTEXITCODE -ne 2) { throw "Native audit positive control did not fail on known High/Critical vulnerabilities: $($control.Name)." }
        $report = Get-Content $reportPath -Raw | ConvertFrom-Json
        if (-not @($report.matches | Where-Object { $_.vulnerability.id -eq $control.Cve -and $_.vulnerability.severity -eq 'Critical' }).Count) {
            throw "Native audit failed to detect known Critical $($control.Cve)."
        }
    }
    $reportPath = Join-Path $security "native-$RuntimeIdentifier-grype.json"
    & $tool "sbom:$sbomPath" --add-cpes-if-none --fail-on high -o json 1> $reportPath 2> (Join-Path $security "native-$RuntimeIdentifier-grype.stderr.log")
    if ($LASTEXITCODE -ne 0) { throw "Native dependency audit failed; inspect $reportPath. No severity waiver is applied." }
    Write-Host "Native $RuntimeIdentifier Grype audit passed with fresh database and both known-Critical controls. Review unsupported source identities separately."
}
finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
}
