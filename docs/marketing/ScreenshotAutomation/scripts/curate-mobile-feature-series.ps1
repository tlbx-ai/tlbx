param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRunDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [string]$TrimMapPath,

    [switch]$AuditFrames
)

$ErrorActionPreference = "Stop"

$resolvedSourceRunDir = Resolve-Path -LiteralPath $SourceRunDir
$resolvedOutputDir = if (Test-Path -LiteralPath $OutputDir) {
    Resolve-Path -LiteralPath $OutputDir
} else {
    New-Item -ItemType Directory -Path $OutputDir -Force
}

foreach ($pattern in @("*.mp4", "*-audit-*.png", "manifest.json", "stitchup-concat.txt", "audit-contact-sheet.png", "audit-frame-list.txt")) {
    Get-ChildItem -LiteralPath $resolvedOutputDir -Filter $pattern -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

if ([string]::IsNullOrWhiteSpace($TrimMapPath)) {
    $TrimMapPath = Join-Path $resolvedSourceRunDir "trim-map.json"
}
if (-not (Test-Path -LiteralPath $TrimMapPath)) {
    throw "Trim map not found: $TrimMapPath. Create a JSON array of { slug, start, duration, feature, hook }."
}

$clips = Get-Content -LiteralPath $TrimMapPath -Raw | ConvertFrom-Json

$manifest = @()
$auditFrameFiles = @()

foreach ($clip in $clips) {
    $sourcePath = Resolve-Path -LiteralPath (Join-Path $resolvedSourceRunDir ("{0}-final.mp4" -f $clip.slug))
    $outputPath = Join-Path $resolvedOutputDir ("{0}.mp4" -f $clip.slug)

    & ffmpeg -hide_banner -loglevel error -y `
        -ss $clip.start -t $clip.duration -i $sourcePath.Path `
        -vf "scale=1080:1920:flags=lanczos,setsar=1" `
        -c:v libx264 -pix_fmt yuv420p -movflags +faststart `
        -an $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg curate failed for $($clip.slug)"
    }

    $probeJson = & ffprobe -v error -select_streams v:0 -show_entries stream=width,height -show_entries format=duration -of json $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "ffprobe failed for $outputPath"
    }
    $probe = $probeJson | ConvertFrom-Json
    $width = [int]$probe.streams[0].width
    $height = [int]$probe.streams[0].height
    $duration = [double]::Parse($probe.format.duration, [System.Globalization.CultureInfo]::InvariantCulture)
    if ($width -ne 1080 -or $height -ne 1920) {
        throw "Curated export has wrong dimensions for $($clip.slug): ${width}x${height}"
    }

    $frames = @()
    if ($AuditFrames) {
        foreach ($second in @(1, [Math]::Max(1, [Math]::Floor($duration / 2)), [Math]::Max(1, [Math]::Floor($duration - 1)))) {
            $stamp = "{0:00}" -f [int]$second
            $framePath = Join-Path $resolvedOutputDir ("{0}-audit-{1}.png" -f $clip.slug, $stamp)
            & ffmpeg -hide_banner -loglevel error -y -ss "00:00:$stamp" -i $outputPath -frames:v 1 -update 1 $framePath
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to extract audit frame at 00:00:$stamp from $outputPath"
            }
            $frames += (Split-Path -Leaf $framePath)
            $auditFrameFiles += $framePath
        }
    }

    $manifest += [pscustomobject]@{
        slug = $clip.slug
        feature = $clip.feature
        hook = $clip.hook
        source = $sourcePath.Path
        start = $clip.start
        durationRequested = $clip.duration
        output = (Split-Path -Leaf $outputPath)
        durationSeconds = [Math]::Round($duration, 2)
        size = "${width}x${height}"
        auditFrames = $frames
    }
}

$concatListPath = Join-Path $resolvedOutputDir "stitchup-concat.txt"
$manifest |
    ForEach-Object {
        $clipPath = Join-Path $resolvedOutputDir $_.output
        "file '$($clipPath.Replace("'", "'\\''"))'"
    } |
    Set-Content -LiteralPath $concatListPath -Encoding UTF8

$stitchupPath = Join-Path $resolvedOutputDir "00-all-features-stitchup.mp4"
& ffmpeg -hide_banner -loglevel error -y `
    -f concat -safe 0 -i $concatListPath `
    -vf "scale=1080:1920:flags=lanczos,setsar=1" `
    -c:v libx264 -pix_fmt yuv420p -movflags +faststart `
    -an $stitchupPath
if ($LASTEXITCODE -ne 0) {
    throw "ffmpeg stitchup failed"
}

$stitchProbeJson = & ffprobe -v error -select_streams v:0 -show_entries stream=width,height -show_entries format=duration -of json $stitchupPath
if ($LASTEXITCODE -ne 0) {
    throw "ffprobe failed for $stitchupPath"
}
$stitchProbe = $stitchProbeJson | ConvertFrom-Json
$stitchWidth = [int]$stitchProbe.streams[0].width
$stitchHeight = [int]$stitchProbe.streams[0].height
$stitchDuration = [double]::Parse($stitchProbe.format.duration, [System.Globalization.CultureInfo]::InvariantCulture)
if ($stitchWidth -ne 1080 -or $stitchHeight -ne 1920) {
    throw "Stitchup export has wrong dimensions: ${stitchWidth}x${stitchHeight}"
}

if ($AuditFrames -and $auditFrameFiles.Count -gt 0) {
    $contactSheet = Join-Path $resolvedOutputDir "audit-contact-sheet.png"
    $listFile = Join-Path $resolvedOutputDir "audit-frame-list.txt"
    $auditFrameFiles | Set-Content -LiteralPath $listFile -Encoding UTF8
    $columns = 6
    $rows = [Math]::Ceiling($auditFrameFiles.Count / $columns)
    & ffmpeg -hide_banner -loglevel error -y `
        $(foreach ($frame in $auditFrameFiles) { @('-i', $frame) }) `
        -filter_complex "concat=n=$($auditFrameFiles.Count):v=1:a=0,scale=270:480,tile=${columns}x${rows}" `
        -frames:v 1 $contactSheet
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg contact sheet failed"
    }
}

$stitchup = [pscustomobject]@{
    slug = "00-all-features-stitchup"
    feature = "All mobile feature clips"
    hook = "tlbx mobile features in one pass."
    output = (Split-Path -Leaf $stitchupPath)
    durationSeconds = [Math]::Round($stitchDuration, 2)
    size = "${stitchWidth}x${stitchHeight}"
    sources = $manifest.output
}

$manifestPath = Join-Path $resolvedOutputDir "manifest.json"
@($stitchup) + $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
@($stitchup) + $manifest | ConvertTo-Json -Depth 5
