param(
    [string]$ChromePath = "C:\Program Files\Google\Chrome\Application\chrome.exe",
    [string]$WebsiteRoot,
    [switch]$ProductAssets
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$compactSource = Join-Path $repoRoot 'src\Ai.Tlbx.MidTerm\src\static\favicon.svg'
$largeSource = Join-Path $repoRoot 'src\Ai.Tlbx.MidTerm\src\static\favicon-large.svg'
$foregroundSource = Join-Path $repoRoot 'src\Ai.Tlbx.MidTerm\src\static\img\tlbx-toolbox-foreground.svg'
$faviconRoot = Join-Path $repoRoot 'src\Ai.Tlbx.MidTerm\src\static\favicon'
$staticRoot = Join-Path $repoRoot 'src\Ai.Tlbx.MidTerm\src\static'

if (-not (Test-Path -LiteralPath $ChromePath)) {
    throw "Chrome not found at $ChromePath"
}

function Render-SvgPngDirect {
    param(
        [string]$SourcePath,
        [int]$Width,
        [int]$Height,
        [string]$OutputPath,
        [switch]$Transparent
    )

    $profile = Join-Path $env:TEMP "tlbx-brand-$Width-$Height-$PID"
    $uri = [uri]$SourcePath
    Remove-Item -LiteralPath $OutputPath -Force -ErrorAction SilentlyContinue
    $chromeArguments = @(
        '--headless=new'
        '--disable-gpu'
        '--hide-scrollbars'
        '--force-device-scale-factor=1'
        '--run-all-compositor-stages-before-draw'
        '--virtual-time-budget=1000'
        "--window-size=$Width,$Height"
        "--user-data-dir=$profile"
        "--screenshot=$OutputPath"
    )
    if ($Transparent) {
        $chromeArguments += '--default-background-color=00000000'
    }
    $chromeArguments += $uri.AbsoluteUri
    & $ChromePath @chromeArguments | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutputPath)) {
        throw "Failed to render $OutputPath"
    }
    Remove-Item -LiteralPath $profile -Recurse -Force -ErrorAction SilentlyContinue
}

$renderMasterCache = @{}

function Get-SvgRenderMaster {
    param([string]$SourcePath, [switch]$Transparent)

    $key = "$SourcePath|$($Transparent.IsPresent)"
    if ($renderMasterCache.ContainsKey($key)) {
        return $renderMasterCache[$key]
    }

    $masterPath = Join-Path $env:TEMP "tlbx-brand-master-$($renderMasterCache.Count)-$PID.png"
    Render-SvgPngDirect -SourcePath $SourcePath -Width 1024 -Height 1024 -OutputPath $masterPath -Transparent:$Transparent
    $renderMasterCache[$key] = $masterPath
    return $masterPath
}

function Render-SvgPng {
    param(
        [string]$SourcePath,
        [int]$Width,
        [int]$Height,
        [string]$OutputPath,
        [switch]$Transparent
    )

    $masterPath = Get-SvgRenderMaster -SourcePath $SourcePath -Transparent:$Transparent
    $filter = if ($Width -eq $Height) {
        "scale=$Width`:$Height`:flags=lanczos"
    }
    else {
        "scale=$Width`:$Height`:force_original_aspect_ratio=decrease:flags=lanczos,pad=$Width`:$Height`:(ow-iw)/2:(oh-ih)/2:color=0x00000000,format=rgba"
    }
    & ffmpeg -hide_banner -loglevel error -y -i $masterPath -vf $filter -frames:v 1 $OutputPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutputPath)) {
        throw "Failed to resize $OutputPath"
    }
}

function Render-SquareSvgPng {
    param([string]$SourcePath, [int]$Size, [string]$OutputPath, [switch]$Transparent)
    Render-SvgPng -SourcePath $SourcePath -Width $Size -Height $Size -OutputPath $OutputPath -Transparent:$Transparent
}

New-Item -ItemType Directory -Force -Path $faviconRoot | Out-Null
Render-SquareSvgPng $compactSource 16 (Join-Path $faviconRoot 'favicon-16x16.png')
Render-SquareSvgPng $compactSource 32 (Join-Path $faviconRoot 'favicon-32x32.png')
Render-SquareSvgPng $compactSource 32 (Join-Path $faviconRoot 'favicon-32.png')
Render-SquareSvgPng $largeSource 192 (Join-Path $faviconRoot 'android-chrome-192x192.png')
Render-SquareSvgPng $largeSource 512 (Join-Path $faviconRoot 'android-chrome-512x512.png')

$favicon32 = Join-Path $faviconRoot 'favicon-32.png'
$faviconIco = Join-Path $faviconRoot 'favicon.ico'
& ffmpeg -hide_banner -loglevel error -y -i $favicon32 -frames:v 1 $faviconIco
if ($LASTEXITCODE -ne 0) { throw 'Failed to encode favicon.ico' }
Remove-Item -LiteralPath $favicon32

if ($WebsiteRoot) {
    $resolvedWebsiteRoot = (Resolve-Path -LiteralPath $WebsiteRoot).Path
    Render-SquareSvgPng $compactSource 64 (Join-Path $resolvedWebsiteRoot 'favicon.png')
    Render-SquareSvgPng $largeSource 512 (Join-Path $resolvedWebsiteRoot 'tlbx-icon.png')
}

if ($ProductAssets) {
    $staticImageRoot = Join-Path $staticRoot 'img'
    $bridgeIconRoot = Join-Path $repoRoot 'src\Ai.Tlbx.MidTerm\src\mobile-device-bridge\icons'
    $storeAssetRoot = Join-Path $repoRoot 'docs\chrome-web-store\mobile-device-bridge\assets'
    $sharedAssetRoot = Join-Path $repoRoot 'src\connectors\shared-assets'
    $androidRoot = Join-Path $repoRoot 'src\connectors\android\app\src\main\res'
    $iosIconRoot = Join-Path $repoRoot 'src\connectors\ios\MidTermConnector\Assets.xcassets\AppIcon.appiconset'

    Render-SquareSvgPng $largeSource 400 (Join-Path $staticImageRoot 'logo.png')
    Render-SquareSvgPng $compactSource 16 (Join-Path $bridgeIconRoot 'icon16.png')
    Render-SquareSvgPng $largeSource 48 (Join-Path $bridgeIconRoot 'icon48.png')
    Render-SquareSvgPng $largeSource 128 (Join-Path $bridgeIconRoot 'icon128.png')
    Copy-Item -LiteralPath (Join-Path $bridgeIconRoot 'icon128.png') -Destination (Join-Path $storeAssetRoot 'icon128.png') -Force

    Render-SvgPng -SourcePath $foregroundSource -Width 364 -Height 330 -OutputPath (Join-Path $sharedAssetRoot 'icon-source.png') -Transparent
    Render-SquareSvgPng $foregroundSource 432 (Join-Path $androidRoot 'drawable\ic_launcher_foreground.png') -Transparent

    foreach ($item in @(
        @{ Directory = 'drawable-mdpi'; Size = 120 },
        @{ Directory = 'drawable-hdpi'; Size = 180 },
        @{ Directory = 'drawable-xhdpi'; Size = 240 },
        @{ Directory = 'drawable-xxhdpi'; Size = 360 },
        @{ Directory = 'drawable-xxxhdpi'; Size = 480 }
    )) {
        Render-SquareSvgPng $foregroundSource $item.Size (Join-Path $androidRoot "$($item.Directory)\splash_logo.png") -Transparent
    }

    foreach ($item in @(
        @{ Directory = 'mipmap-mdpi'; Size = 48 },
        @{ Directory = 'mipmap-hdpi'; Size = 72 },
        @{ Directory = 'mipmap-xhdpi'; Size = 96 },
        @{ Directory = 'mipmap-xxhdpi'; Size = 144 },
        @{ Directory = 'mipmap-xxxhdpi'; Size = 192 }
    )) {
        $source = if ($item.Size -le 72) { $compactSource } else { $largeSource }
        foreach ($name in @('ic_launcher.png', 'ic_launcher_round.png')) {
            Render-SquareSvgPng $source $item.Size (Join-Path $androidRoot "$($item.Directory)\$name")
        }
    }

    foreach ($icon in Get-ChildItem -LiteralPath $iosIconRoot -Filter '*.png') {
        $dimensions = (& ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of csv=s=x:p=0 $icon.FullName).Trim().Split('x')
        if ($dimensions.Count -ne 2 -or $dimensions[0] -ne $dimensions[1]) {
            throw "Expected a square iOS app icon: $($icon.FullName)"
        }
        $size = [int]$dimensions[0]
        $source = if ($size -le 40) { $compactSource } else { $largeSource }
        Render-SquareSvgPng $source $size $icon.FullName
    }
}

foreach ($masterPath in $renderMasterCache.Values) {
    Remove-Item -LiteralPath $masterPath -Force -ErrorAction SilentlyContinue
}

Write-Host 'Rendered tlbx favicon assets.'
