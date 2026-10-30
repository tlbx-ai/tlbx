#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64','win-x86','linux-x64','linux-arm64','osx-x64','osx-arm64')]
    [string]$RuntimeIdentifier
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$scratch = Join-Path $repoRoot '.artifacts/connect-dotnet-transport'
$pin = Get-Content (Join-Path $PSScriptRoot 'native-stack.json') -Raw | ConvertFrom-Json
$vcpkgRoot = Join-Path $scratch 'vcpkg'
$sourcePin = Join-Path $vcpkgRoot '.tlbx-source-pin'
$vcpkg = Join-Path $vcpkgRoot $(if ($IsWindows) { 'vcpkg.exe' } else { 'vcpkg' })
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
if (-not (Test-Path (Join-Path $vcpkgRoot 'scripts/vcpkg-tool-metadata.txt'))) {
    $archive = Join-Path $scratch "vcpkg-$($pin.vcpkgCommit).tar.gz"
    if (-not (Test-Path $archive)) {
        Invoke-WebRequest "https://codeload.github.com/microsoft/vcpkg/tar.gz/$($pin.vcpkgCommit)" -OutFile $archive
    }
    if ((Get-FileHash $archive -Algorithm SHA512).Hash.ToLowerInvariant() -ne $pin.vcpkgArchiveSha512) {
        throw 'Pinned vcpkg source archive hash mismatch.'
    }
    & tar -xzf $archive -C $scratch
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract pinned vcpkg.' }
    Move-Item -LiteralPath (Join-Path $scratch "vcpkg-$($pin.vcpkgCommit)") -Destination $vcpkgRoot
    Set-Content -LiteralPath $sourcePin -Value $pin.vcpkgArchiveSha512 -NoNewline
}
if (-not (Test-Path $sourcePin) -or (Get-Content -LiteralPath $sourcePin -Raw).Trim() -ne $pin.vcpkgArchiveSha512) {
    throw 'Existing vcpkg scratch is not the pinned source snapshot; use a fresh task-owned scratch directory.'
}
if (-not (Test-Path $vcpkg)) {
    if ($IsWindows) { & (Join-Path $vcpkgRoot 'bootstrap-vcpkg.bat') -disableMetrics }
    else { & sh (Join-Path $vcpkgRoot 'bootstrap-vcpkg.sh') -disableMetrics }
    if ($LASTEXITCODE -ne 0) { throw 'Pinned vcpkg bootstrap failed.' }
}

# Bound compilation on developer machines as well as CI. No global installation.
$previousConcurrency = $env:VCPKG_MAX_CONCURRENCY
$previousMetrics = $env:VCPKG_DISABLE_METRICS
$previousVisualStudio = $env:VCPKG_VISUAL_STUDIO_PATH
$previousBinaryCache = $env:VCPKG_DEFAULT_BINARY_CACHE
$previousSigner = $env:TLBX_CONNECT_SIGN_IDENTITY
try {
    $env:VCPKG_MAX_CONCURRENCY = '2'
    $env:VCPKG_DISABLE_METRICS = '1'
    if (-not $env:VCPKG_DEFAULT_BINARY_CACHE) { $env:VCPKG_DEFAULT_BINARY_CACHE = Join-Path $scratch 'binary-cache' }
    New-Item -ItemType Directory -Force -Path $env:VCPKG_DEFAULT_BINARY_CACHE | Out-Null
    if ($IsMacOS) {
        $env:TLBX_CONNECT_SIGN_IDENTITY = if ($env:GITHUB_ACTIONS -eq 'true') { 'Developer ID Application: Johannes Schmidt (FK7G5C74WH)' } else { '-' }
    }
    if ($IsWindows -and -not $env:VCPKG_VISUAL_STUDIO_PATH) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (Test-Path $vswhere) {
            $env:VCPKG_VISUAL_STUDIO_PATH = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        }
    }
    $installRoot = Join-Path $scratch 'installed'
    # Seed the exact public gitlink response already pinned by the vcpkg PCRE2
    # port. Its unchanged SHA512 is rechecked there; source archives still use
    # their own pins. This avoids a rate-limited GitHub API metadata request.
    $metadata = Join-Path (Join-Path $PSScriptRoot 'source-metadata') $pin.sourceMetadata.file
    if ((Get-FileHash -LiteralPath $metadata -Algorithm SHA512).Hash.ToLowerInvariant() -ne $pin.sourceMetadata.sha512) {
        throw 'Pinned PCRE2 source metadata integrity check failed.'
    }
    $downloads = Join-Path $scratch 'downloads'
    New-Item -ItemType Directory -Force -Path $downloads | Out-Null
    Copy-Item -LiteralPath $metadata -Destination (Join-Path $downloads $pin.sourceMetadata.file) -Force
    $hostTriplet = if ($IsWindows) { 'x64-windows' }
        elseif ($IsMacOS) { if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64-osx' } else { 'x64-osx' } }
        else { if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64-linux' } else { 'x64-linux' } }
    & $vcpkg install "--triplet=$RuntimeIdentifier" "--host-triplet=$hostTriplet" `
        "--overlay-ports=$(Join-Path $PSScriptRoot 'ports')" "--overlay-triplets=$(Join-Path $PSScriptRoot 'triplets')" `
        "--x-manifest-root=$PSScriptRoot" "--x-install-root=$installRoot" `
        "--x-buildtrees-root=$(Join-Path $scratch 'buildtrees')" "--downloads-root=$(Join-Path $scratch 'downloads')"
    if ($LASTEXITCODE -ne 0) { throw "Native Connect build failed for $RuntimeIdentifier." }
    & python (Join-Path $PSScriptRoot 'package.py') --rid $RuntimeIdentifier --installed $installRoot
    if ($LASTEXITCODE -ne 0) { throw "Native Connect packaging failed for $RuntimeIdentifier." }
}
finally {
    $env:VCPKG_MAX_CONCURRENCY = $previousConcurrency
    $env:VCPKG_DISABLE_METRICS = $previousMetrics
    $env:VCPKG_VISUAL_STUDIO_PATH = $previousVisualStudio
    $env:VCPKG_DEFAULT_BINARY_CACHE = $previousBinaryCache
    $env:TLBX_CONNECT_SIGN_IDENTITY = $previousSigner
}
