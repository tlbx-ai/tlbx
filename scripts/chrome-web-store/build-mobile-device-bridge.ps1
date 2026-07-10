param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$source = Join-Path $repoRoot "src/Ai.Tlbx.MidTerm/src/mobile-device-bridge"
$manifestPath = Join-Path $source "manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$version = [string]$manifest.version
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "Invalid Chrome extension version: $version"
}

$requiredFiles = @(
    "manifest.json",
    "service-worker.js",
    "page-bridge.js",
    "icons/icon16.png",
    "icons/icon48.png",
    "icons/icon128.png"
)
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path (Join-Path $source $relativePath))) {
        throw "Missing extension file: $relativePath"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/chrome-web-store"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$staging = Join-Path ([IO.Path]::GetTempPath()) ("midterm-mobile-device-bridge-" + [Guid]::NewGuid().ToString("N"))
$archive = Join-Path $OutputDirectory "midterm-mobile-device-bridge-v$version.zip"
try {
    New-Item -ItemType Directory -Path (Join-Path $staging "icons") -Force | Out-Null
    foreach ($relativePath in $requiredFiles) {
        $destination = Join-Path $staging $relativePath
        Copy-Item -LiteralPath (Join-Path $source $relativePath) -Destination $destination -Force
    }
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $archive -CompressionLevel Optimal
}
finally {
    if (Test-Path $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

Write-Output $archive
