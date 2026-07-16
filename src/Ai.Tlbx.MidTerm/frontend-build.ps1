#!/usr/bin/env pwsh
# Frontend build script - handles TypeScript, bundling, asset copying, and compression
# Cross-platform: Windows, macOS, Linux
#
# IMPORTANT FOR PUBLISH BUILDS:
#   This script MUST run BEFORE 'dotnet publish' starts!
#   The csproj uses static ItemGroups for EmbeddedResource which are evaluated
#   when MSBuild loads the project. If files don't exist at that moment,
#   they won't be embedded and you'll get 404 errors.
#
#   Correct order in release scripts:
#     1. frontend-build.ps1 -Publish    <-- Creates wwwroot with compressed files
#     2. dotnet publish                  <-- Embeds existing files
#
# Usage:
#   ./frontend-build.ps1                    # Debug build (TypeScript + assets)
#   ./frontend-build.ps1 -Publish           # Publish build (+ Brotli compression)
#   ./frontend-build.ps1 -Version "1.2.3"   # With version injection

param(
    [switch]$Publish,        # Enable Brotli compression for publish builds
    [switch]$DevRelease,     # Include source maps in publish (for dev/prerelease builds)
    [switch]$SkipVerify,     # Skip npm verify/lint/typecheck gates when another job already owns them
    [string]$Version = "dev" # Version to inject into BUILD_VERSION
)

$ErrorActionPreference = "Stop"
$WwwRoot = Join-Path $PSScriptRoot "wwwroot"
$TsSource = Join-Path $PSScriptRoot "src/ts"
$StaticSource = Join-Path $PSScriptRoot "src/static"
$MobileDeviceBridgeSource = Join-Path $PSScriptRoot "src/mobile-device-bridge"
$OutFile = Join-Path $WwwRoot "js/terminal.min.js"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$NodeModulesRoot = Join-Path $PSScriptRoot "node_modules"
$AssetVersionPlaceholder = "__MIDTERM_ASSET_VERSION__"

if (Test-Path $WwwRoot) {
    Get-ChildItem -Path $WwwRoot -Filter "*.br" -File -Recurse | Remove-Item -Force
}

# ===========================================
# PRECHECK: Required toolchain/dependencies
# ===========================================
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Error "Node.js is required but was not found in PATH. Install Node.js 24.x and run npm ci."
    exit 1
}

if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
    Write-Error "npx is required but was not found in PATH. Install Node.js/npm and run npm ci."
    exit 1
}

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Error "npm is required but was not found in PATH. Install Node.js/npm and run npm ci."
    exit 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error ".NET SDK is required but was not found in PATH."
    exit 1
}

$requiredNodeDeps = @(
    @{ Name = "typescript"; Path = Join-Path $NodeModulesRoot "typescript/lib/tsc.js" },
    @{ Name = "eslint"; Path = Join-Path $NodeModulesRoot "eslint" },
    @{ Name = "esbuild"; Path = Join-Path $NodeModulesRoot "esbuild" },
    @{ Name = "prettier"; Path = Join-Path $NodeModulesRoot "prettier" },
    @{ Name = "openapi-typescript"; Path = Join-Path $NodeModulesRoot "openapi-typescript" },
    @{ Name = "swagger-ui-dist"; Path = Join-Path $NodeModulesRoot "swagger-ui-dist/package.json" }
)

function Get-MissingFrontendDeps {
    param([array]$Deps)

    $missing = @()
    foreach ($dep in $Deps) {
        if (-not (Test-Path $dep.Path)) {
            $missing += $dep.Name
        }
    }
    return @($missing)
}

function Get-AssetFingerprint {
    param([string[]]$Paths)

    $files = foreach ($path in $Paths) {
        if (Test-Path $path -PathType Container) {
            Get-ChildItem -Path $path -File -Recurse
        }
        elseif (Test-Path $path -PathType Leaf) {
            Get-Item $path
        }
    }

    $entries = $files |
        Sort-Object FullName -Unique |
        ForEach-Object {
            $relativePath = [System.IO.Path]::GetRelativePath($RepoRoot, $_.FullName).Replace('\', '/')
            $fileHash = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$relativePath|$fileHash"
        }

    if (-not $entries) {
        return "dev"
    }

    $manifest = [string]::Join("`n", $entries)
    $hash = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($manifest))
    return [Convert]::ToHexString($hash).ToLowerInvariant().Substring(0, 12)
}

function Invoke-NpmScript {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $false)][string]$Label = $Name
    )

    Write-Host $Label -ForegroundColor Cyan
    Push-Location $PSScriptRoot
    try {
        & npm run $Name
        if ($LASTEXITCODE -ne 0) {
            Write-Error ("npm run {0} failed" -f $Name)
            exit $LASTEXITCODE
        }
    }
    finally {
        Pop-Location
    }
}

$missingDeps = @(Get-MissingFrontendDeps -Deps $requiredNodeDeps)
if ($missingDeps.Count -gt 0) {
    Write-Host ("Missing frontend npm dependencies: {0}" -f ($missingDeps -join ", ")) -ForegroundColor Yellow
    Write-Host ("Attempting automatic install: npm ci --include=dev (project dir: {0})" -f $PSScriptRoot) -ForegroundColor Cyan

    Push-Location $PSScriptRoot
    try {
        & npm ci --include=dev
    }
    finally {
        Pop-Location
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error ("Automatic dependency install failed (npm ci exit code {0})." -f $LASTEXITCODE)
        exit $LASTEXITCODE
    }

    $missingAfterInstall = @(Get-MissingFrontendDeps -Deps $requiredNodeDeps)
    if ($missingAfterInstall.Count -gt 0) {
        Write-Error ("Frontend dependencies still missing after npm ci: {0}" -f ($missingAfterInstall -join ", "))
        exit 1
    }
}

# ===========================================
# PHASE 0: Prepare wwwroot output directory
# ===========================================
if ($Publish) {
    Write-Host "Cleaning wwwroot for fresh publish build..." -ForegroundColor Cyan
    Remove-Item -Path $WwwRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# Create output directories
@('', 'js', 'css', 'fonts', 'img', 'openapi', 'swagger') | ForEach-Object {
    $dir = Join-Path $WwwRoot $_
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

# ===========================================
# PHASE 0.5: Generate API types from OpenAPI spec
# ===========================================
$OpenApiProject = Join-Path $PSScriptRoot "..\Ai.Tlbx.MidTerm.OpenApi\Ai.Tlbx.MidTerm.OpenApi.csproj"
$OpenApiSpec = Join-Path $PSScriptRoot "openapi\openapi.json"
$GeneratedTypes = Join-Path $TsSource "api.generated.ts"
$SwaggerSource = Join-Path $StaticSource "swagger"
$SwaggerUiRoot = Join-Path $NodeModulesRoot "swagger-ui-dist"

Write-Host "Generating API types from OpenAPI spec..." -ForegroundColor Cyan

# Build OpenAPI project to regenerate spec
Write-Host "  Building OpenAPI project..." -ForegroundColor DarkGray
$buildOutput = & dotnet build $OpenApiProject --verbosity quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host $buildOutput -ForegroundColor Red
    Write-Error "OpenAPI project build failed"
    exit $LASTEXITCODE
}

if (-not (Test-Path $OpenApiSpec)) {
    Write-Error "OpenAPI spec not generated at $OpenApiSpec"
    exit 1
}

Write-Host "  Generating TypeScript types..." -ForegroundColor DarkGray
# Run the project-local binaries directly: npx resolves by current directory and
# silently falls back to an arbitrary cached version when this script is invoked
# from outside the web project (e.g. scripts/dev.ps1), which yields generated
# output that disagrees with the lint gate.
& node (Join-Path $NodeModulesRoot "openapi-typescript/bin/cli.js") $OpenApiSpec -o $GeneratedTypes
if ($LASTEXITCODE -ne 0) { Write-Error "openapi-typescript failed"; exit $LASTEXITCODE }

& node (Join-Path $NodeModulesRoot "prettier/bin/prettier.cjs") --write $GeneratedTypes 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "prettier formatting failed"; exit $LASTEXITCODE }

Write-Host "  api.generated.ts updated" -ForegroundColor DarkGray

$AssetVersion = Get-AssetFingerprint -Paths @(
    $TsSource,
    $StaticSource,
    $MobileDeviceBridgeSource,
    $OpenApiSpec,
    (Join-Path $PSScriptRoot "package.json"),
    (Join-Path $PSScriptRoot "package-lock.json")
)

Write-Host "Asset fingerprint: $AssetVersion" -ForegroundColor DarkGray

# ===========================================
# PHASE 1+2: Static verification
# ===========================================
if ($Publish) {
    if ($SkipVerify) {
        Write-Host "Skipping publish TypeScript/lint/test gate..." -ForegroundColor Yellow
    }
    else {
        Invoke-NpmScript -Name "verify" -Label "Running publish TypeScript/lint/test gate..."
    }
}
else {
    Invoke-NpmScript -Name "typecheck" -Label "Running production TypeScript type-check..."
    Invoke-NpmScript -Name "lint" -Label "Running production lint..."
}

# ===========================================
# PHASE 3: Bundle with esbuild
# ===========================================
Write-Host "Bundling with esbuild (version: $Version, assets: $AssetVersion)..." -ForegroundColor Cyan

$mainTs = Join-Path $TsSource "main.ts"
$includeSourceMap = -not $Publish -or $DevRelease
$sourcemapArg = if ($includeSourceMap) { "--sourcemap=linked" } else { $null }
# esbuild --define requires a valid JS expression. Use single quotes for the string
# literal to avoid double-quote escaping issues across PowerShell + Windows cmd.exe.
$defineVersionArg = "--define:BUILD_VERSION='$Version'"
$defineAssetVersionArg = "--define:BUILD_ASSET_VERSION='$AssetVersion'"
$esbuildArgs = @($mainTs, "--bundle", "--minify", "--outfile=$OutFile", "--target=es2020", $defineVersionArg, $defineAssetVersionArg)
if ($sourcemapArg) { $esbuildArgs += $sourcemapArg }
& npx esbuild @esbuildArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "esbuild failed"
    exit $LASTEXITCODE
}

$jsSize = (Get-Item $OutFile).Length
Write-Host "  terminal.min.js ($([math]::Round($jsSize/1KB, 1)) KB)" -ForegroundColor DarkGray

# ===========================================
# PHASE 4: Copy binary assets
# ===========================================
# Binary asset compression notes (tested 2025-01):
#   - woff2 files are already Brotli-compressed internally, so most don't benefit
#   - EXCEPT Terminus.woff2 which has 62% reduction (unoptimized metadata?)
#   - woff (older format, zlib) benefits ~49% from Brotli
#   - ico files benefit ~49% (contains BMP data, not PNG)
#   - png files don't benefit (already DEFLATE compressed)
#   - Properly optimized woff2 (CascadiaCode, JetBrains) show 0-1% reduction
#
# Files worth compressing for publish (saves ~195 KB total):
#   - Terminus.woff2: 297 KB -> 112 KB (62% reduction, 185 KB saved)
#   - midFont.woff:    15 KB ->   8 KB (49% reduction, 7 KB saved)
#   - favicon.ico:     15 KB ->   8 KB (49% reduction, 7 KB saved)

Write-Host "Copying static assets..." -ForegroundColor Cyan
Write-Host "::group::Copying static assets"

# Chrome runs the bridge locally on the user's machine even when MidTerm is remote.
# Ship it as an unpacked MV3 extension archive; Chrome itself is the only runtime dependency.
$mobileDeviceBridgeArchive = Join-Path $WwwRoot "midterm-mobile-device-bridge.zip"
if (Test-Path $MobileDeviceBridgeSource) {
    Remove-Item $mobileDeviceBridgeArchive -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $MobileDeviceBridgeSource "*") -DestinationPath $mobileDeviceBridgeArchive -CompressionLevel Optimal
    Write-Host "  midterm-mobile-device-bridge.zip" -ForegroundColor DarkGray
}

# Binary files that benefit from Brotli compression (publish only)
# These get both the original (debug) and .br version (publish)
$compressibleBinaries = @(
    @{ Src = "fonts/Terminus.woff2"; Dst = "fonts/Terminus.woff2" },
    @{ Src = "fonts/midFont.woff"; Dst = "fonts/midFont.woff" },
    @{ Src = "favicon/favicon.ico"; Dst = "favicon.ico" }
)

# Binary files that don't benefit from compression (already optimized)
# woff2 uses Brotli internally, png uses DEFLATE
$nonCompressibleBinaries = @(
    @{ Pattern = "fonts/*.woff2"; Dst = "fonts"; Exclude = @("Terminus.woff2") },
    @{ Pattern = "img/*.png"; Dst = "img" },
    @{ Pattern = "favicon/*.png"; Dst = "" }
)

# Copy compressible binaries (always copy original, compress for publish)
foreach ($file in $compressibleBinaries) {
    $srcPath = Join-Path $StaticSource $file.Src
    $dstPath = Join-Path $WwwRoot $file.Dst
    $dstDir = Split-Path $dstPath -Parent
    if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }

    Copy-Item $srcPath -Destination $dstPath -Force
    Write-Host "  $($file.Dst)" -ForegroundColor DarkGray
}

# Copy non-compressible binaries
foreach ($spec in $nonCompressibleBinaries) {
    $pattern = Join-Path $StaticSource $spec.Pattern
    $exclude = if ($spec.ContainsKey('Exclude')) { @($spec['Exclude']) } else { @() }

    Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin $exclude } | ForEach-Object {
        $dstDir = if ($spec['Dst']) { Join-Path $WwwRoot $spec['Dst'] } else { $WwwRoot }
        Copy-Item $_.FullName -Destination $dstDir -Force
        $relPath = if ($spec['Dst']) { "$($spec['Dst'])/$($_.Name)" } else { $_.Name }
        Write-Host "  $relPath" -ForegroundColor DarkGray
    }
}
Write-Host "::endgroup::"

# Compress select binary files for publish (see notes above for rationale)
if ($Publish) {
    Write-Host "Compressing select binary assets..." -ForegroundColor Cyan
    Write-Host "::group::Compressing binary assets"
    foreach ($file in $compressibleBinaries) {
        $srcPath = Join-Path $WwwRoot $file.Dst
        $dstPath = "$srcPath.br"

        $bytes = [System.IO.File]::ReadAllBytes($srcPath)
        $memStream = [System.IO.MemoryStream]::new()
        $brotli = [System.IO.Compression.BrotliStream]::new($memStream, [System.IO.Compression.CompressionLevel]::SmallestSize)
        $brotli.Write($bytes, 0, $bytes.Length)
        $brotli.Close()
        [System.IO.File]::WriteAllBytes($dstPath, $memStream.ToArray())

        $srcSize = $bytes.Length
        $dstSize = $memStream.ToArray().Length
        $reduction = [math]::Round((1 - $dstSize / $srcSize) * 100)

        Write-Host "  $($file.Dst) -> $($file.Dst).br ($srcSize -> $dstSize bytes, $reduction% reduction)" -ForegroundColor DarkGray

        # Remove original for publish (only .br embedded)
        Remove-Item $srcPath -Force
    }
    Write-Host "::endgroup::"
}

# ===========================================
# PHASE 5: Process text assets
# ===========================================
# Text files to process (compress for publish, copy for debug)
$textExtensions = @('*.html', '*.css', '*.js', '*.txt', '*.json', '*.webmanifest', '*.svg')
$totalSaved = 0

function Process-TextFile {
    param([string]$Source, [string]$Destination, [bool]$Compress)

    $content = [System.IO.File]::ReadAllText($Source)
    $processedContent = $content.Replace($AssetVersionPlaceholder, $AssetVersion)
    $tempPath = $null

    if ($Compress) {
        $dstPath = "$Destination.br"
        $srcStream = $null
        $dstStream = $null
        $brotli = $null

        try {
            $tempPath = [System.IO.Path]::GetTempFileName()
            [System.IO.File]::WriteAllText($tempPath, $processedContent, [System.Text.Encoding]::UTF8)
            $srcStream = [System.IO.File]::OpenRead($tempPath)
            $dstStream = [System.IO.File]::Create($dstPath)
            $brotli = [System.IO.Compression.BrotliStream]::new(
                $dstStream,
                [System.IO.Compression.CompressionLevel]::SmallestSize
            )
            $srcStream.CopyTo($brotli)
            $brotli.Flush()

            $srcSize = [System.Text.Encoding]::UTF8.GetByteCount($processedContent)
            $dstSize = (Get-Item $dstPath).Length
            $reduction = [math]::Round((1 - $dstSize / $srcSize) * 100)

            return @{ Saved = ($srcSize - $dstSize); Reduction = $reduction }
        }
        finally {
            if ($null -ne $brotli) { $brotli.Dispose() }
            if ($null -ne $dstStream) { $dstStream.Dispose() }
            if ($null -ne $srcStream) { $srcStream.Dispose() }
            if ($null -ne $tempPath -and (Test-Path $tempPath)) { Remove-Item $tempPath -Force }
        }
    }
    else {
        [System.IO.File]::WriteAllText($Destination, $processedContent, [System.Text.Encoding]::UTF8)
        return @{ Saved = 0; Reduction = 0 }
    }
}

if ($Publish) {
    Write-Host "Compressing text assets with Brotli..." -ForegroundColor Cyan
    Write-Host "::group::Compressing text assets"
}

# Root-level text files (HTML, manifest, etc.) -> wwwroot/
Get-ChildItem -Path "$StaticSource\*" -Include $textExtensions | ForEach-Object {
    $dstName = $_.Name
    $dstPath = Join-Path $WwwRoot $dstName
    $result = Process-TextFile -Source $_.FullName -Destination $dstPath -Compress $Publish

    if ($Publish) {
        $totalSaved += $result.Saved
        Write-Host "  $dstName -> $dstName.br ($($result.Reduction)% reduction)" -ForegroundColor DarkGray
    }
    else {
        Write-Host "  $dstName" -ForegroundColor DarkGray
    }
}

# CSS files -> wwwroot/css/ (minified)
$cssSource = Join-Path $StaticSource "css"
Get-ChildItem -Path "$cssSource\*" -Include @('*.css') | ForEach-Object {
    $dstPath = Join-Path $WwwRoot "css/$($_.Name)"
    $srcSize = $_.Length

    # Minify with esbuild
    $null = & npx esbuild $_.FullName --minify --outfile=$dstPath 2>&1
    $minifiedContent = [System.IO.File]::ReadAllText($dstPath).Replace($AssetVersionPlaceholder, $AssetVersion)
    [System.IO.File]::WriteAllText($dstPath, $minifiedContent, [System.Text.Encoding]::UTF8)
    $minSize = [System.Text.Encoding]::UTF8.GetByteCount($minifiedContent)

    if ($Publish) {
        # Brotli compress the minified file
        $brPath = "$dstPath.br"
        $srcStream = $null
        $dstStream = $null
        $brotli = $null
        try {
            $srcStream = [System.IO.File]::OpenRead($dstPath)
            $dstStream = [System.IO.File]::Create($brPath)
            $brotli = [System.IO.Compression.BrotliStream]::new($dstStream, [System.IO.Compression.CompressionLevel]::SmallestSize)
            $srcStream.CopyTo($brotli)
            $brotli.Flush()
        }
        finally {
            if ($null -ne $brotli) { $brotli.Dispose() }
            if ($null -ne $dstStream) { $dstStream.Dispose() }
            if ($null -ne $srcStream) { $srcStream.Dispose() }
        }
        Remove-Item $dstPath -Force
        $brSize = (Get-Item $brPath).Length
        $totalSaved += ($srcSize - $brSize)
        $reduction = [math]::Round((1 - $brSize / $srcSize) * 100)
        Write-Host "  css/$($_.Name) -> css/$($_.Name).br ($reduction% reduction)" -ForegroundColor DarkGray
    }
    else {
        Write-Host "  css/$($_.Name)" -ForegroundColor DarkGray
    }
}

# Fonts text files (OFL.txt license) -> wwwroot/fonts/
$fontsSource = Join-Path $StaticSource "fonts"
Get-ChildItem -Path "$fontsSource\*" -Include @('*.txt') | ForEach-Object {
    $dstPath = Join-Path $WwwRoot "fonts/$($_.Name)"
    $result = Process-TextFile -Source $_.FullName -Destination $dstPath -Compress $Publish

    if ($Publish) {
        $totalSaved += $result.Saved
        Write-Host "  fonts/$($_.Name) -> fonts/$($_.Name).br ($($result.Reduction)% reduction)" -ForegroundColor DarkGray
    }
    else {
        Write-Host "  fonts/$($_.Name)" -ForegroundColor DarkGray
    }
}

# Locale translation files -> wwwroot/locales/
$localesSource = Join-Path $StaticSource "locales"
if (Test-Path $localesSource) {
    $localesDir = Join-Path $WwwRoot "locales"
    if (-not (Test-Path $localesDir)) {
        New-Item -ItemType Directory -Path $localesDir -Force | Out-Null
    }
    Get-ChildItem -Path "$localesSource\*.json" | ForEach-Object {
        $dstPath = Join-Path $localesDir $_.Name
        $result = Process-TextFile -Source $_.FullName -Destination $dstPath -Compress $Publish

        if ($Publish) {
            $totalSaved += $result.Saved
            Write-Host "  locales/$($_.Name) -> locales/$($_.Name).br ($($result.Reduction)% reduction)" -ForegroundColor DarkGray
        }
        else {
            Write-Host "  locales/$($_.Name)" -ForegroundColor DarkGray
        }
    }
}

# SVG image assets -> wwwroot/img/
$imageSource = Join-Path $StaticSource "img"
if (Test-Path $imageSource) {
    $imageDir = Join-Path $WwwRoot "img"
    if (-not (Test-Path $imageDir)) {
        New-Item -ItemType Directory -Path $imageDir -Force | Out-Null
    }
    else {
        # Keep generated public SVGs as an exact mirror of src/static/img so
        # retired brand assets cannot survive indefinitely in wwwroot.
        Get-ChildItem -Path $imageDir -Filter '*.svg' -File | Remove-Item -Force
    }
    Get-ChildItem -Path "$imageSource\*.svg" | ForEach-Object {
        $dstPath = Join-Path $imageDir $_.Name
        $result = Process-TextFile -Source $_.FullName -Destination $dstPath -Compress $Publish

        if ($Publish) {
            $totalSaved += $result.Saved
            Write-Host "  img/$($_.Name) -> img/$($_.Name).br ($($result.Reduction)% reduction)" -ForegroundColor DarkGray
        }
        else {
            Write-Host "  img/$($_.Name)" -ForegroundColor DarkGray
        }
    }
}

# Additional JS files (not bundled, e.g. audio worklets) -> wwwroot/js/
$jsSource = Join-Path $StaticSource "js"
if (Test-Path $jsSource) {
    Get-ChildItem -Path "$jsSource\*" -Include @('*.js') | ForEach-Object {
        $dstPath = Join-Path $WwwRoot "js/$($_.Name)"
        $result = Process-TextFile -Source $_.FullName -Destination $dstPath -Compress $Publish

        if ($Publish) {
            $totalSaved += $result.Saved
            Write-Host "  js/$($_.Name) -> js/$($_.Name).br ($($result.Reduction)% reduction)" -ForegroundColor DarkGray
        }
        else {
            Write-Host "  js/$($_.Name)" -ForegroundColor DarkGray
        }
    }
}

# OpenAPI spec -> wwwroot/openapi/
$openApiDst = Join-Path $WwwRoot "openapi/openapi.json"
$openApiResult = Process-TextFile -Source $OpenApiSpec -Destination $openApiDst -Compress $Publish
if ($Publish) {
    $totalSaved += $openApiResult.Saved
    Write-Host "  openapi/openapi.json -> openapi/openapi.json.br ($($openApiResult.Reduction)% reduction)" -ForegroundColor DarkGray
}
else {
    Write-Host "  openapi/openapi.json" -ForegroundColor DarkGray
}

# Swagger UI assets -> wwwroot/swagger/
$swaggerTextAssets = @(
    @{ Src = Join-Path $SwaggerUiRoot "swagger-ui.css"; Dst = Join-Path $WwwRoot "swagger/swagger-ui.css"; Label = "swagger/swagger-ui.css" },
    @{ Src = Join-Path $SwaggerUiRoot "swagger-ui-bundle.js"; Dst = Join-Path $WwwRoot "swagger/swagger-ui-bundle.js"; Label = "swagger/swagger-ui-bundle.js" },
    @{ Src = Join-Path $SwaggerUiRoot "swagger-ui-standalone-preset.js"; Dst = Join-Path $WwwRoot "swagger/swagger-ui-standalone-preset.js"; Label = "swagger/swagger-ui-standalone-preset.js" },
    @{ Src = Join-Path $SwaggerSource "index.html"; Dst = Join-Path $WwwRoot "swagger/index.html"; Label = "swagger/index.html" },
    @{ Src = Join-Path $SwaggerSource "swagger-initializer.js"; Dst = Join-Path $WwwRoot "swagger/swagger-initializer.js"; Label = "swagger/swagger-initializer.js" }
)

foreach ($asset in $swaggerTextAssets) {
    $result = Process-TextFile -Source $asset.Src -Destination $asset.Dst -Compress $Publish
    if ($Publish) {
        $totalSaved += $result.Saved
        Write-Host "  $($asset.Label) -> $($asset.Label).br ($($result.Reduction)% reduction)" -ForegroundColor DarkGray
    }
    else {
        Write-Host "  $($asset.Label)" -ForegroundColor DarkGray
    }
}

# html2canvas vendor library — lazy-loaded by web preview screenshot feature
$h2cSrc = Join-Path $NodeModulesRoot "html2canvas/dist/html2canvas.min.js"
if (Test-Path $h2cSrc) {
    $dstPath = Join-Path $WwwRoot "js/html2canvas.min.js"
    $result = Process-TextFile -Source $h2cSrc -Destination $dstPath -Compress $Publish
    if ($Publish) {
        $totalSaved += $result.Saved
        Write-Host "  html2canvas.min.js -> html2canvas.min.js.br ($($result.Reduction)% reduction)" -ForegroundColor DarkGray
    } else {
        Write-Host "  html2canvas.min.js" -ForegroundColor DarkGray
    }
}

if ($Publish) {
    Write-Host "::endgroup::"
}

# ===========================================
# PHASE 6: Compress generated JS (publish only)
# ===========================================
if ($Publish) {
    Write-Host "Compressing generated JavaScript..." -ForegroundColor Cyan
    Write-Host "::group::Compressing generated JavaScript"

    @($OutFile, "$OutFile.map") | Where-Object { Test-Path $_ } | ForEach-Object {
        $src = $_
        $dst = "$src.br"
        $srcStream = $null
        $dstStream = $null
        $brotli = $null

        try {
            $srcStream = [System.IO.File]::OpenRead($src)
            $dstStream = [System.IO.File]::Create($dst)
            $brotli = [System.IO.Compression.BrotliStream]::new(
                $dstStream,
                [System.IO.Compression.CompressionLevel]::SmallestSize
            )
            $srcStream.CopyTo($brotli)
            $brotli.Flush()

            $srcSize = (Get-Item $src).Length
            $dstSize = (Get-Item $dst).Length
            $reduction = [math]::Round((1 - $dstSize / $srcSize) * 100)
            $totalSaved += ($srcSize - $dstSize)

            $fileName = Split-Path $src -Leaf
            Write-Host "  js/$fileName -> js/$fileName.br ($srcSize -> $dstSize bytes, $reduction% reduction)" -ForegroundColor DarkGray
        }
        finally {
            if ($null -ne $brotli) { $brotli.Dispose() }
            if ($null -ne $dstStream) { $dstStream.Dispose() }
            if ($null -ne $srcStream) { $srcStream.Dispose() }
        }
    }

    # Remove uncompressed JS files for publish (only .br embedded)
    Remove-Item $OutFile -Force -ErrorAction SilentlyContinue
    Remove-Item "$OutFile.map" -Force -ErrorAction SilentlyContinue
    Write-Host "::endgroup::"

    Write-Host "  Total saved: $([math]::Round($totalSaved/1KB, 1)) KB" -ForegroundColor Green
}

Write-Host "Frontend build complete" -ForegroundColor Green
