param(
    [Parameter(Mandatory = $true)]
    [string]$RunDir,

    [string]$InputName = "mobile-vertical-demo.webm",
    [string]$OutputName = "mobile-vertical-demo-phone-dpi.mp4"
)

$ErrorActionPreference = "Stop"

$resolvedRunDir = Resolve-Path -LiteralPath $RunDir
$inputPath = Join-Path $resolvedRunDir $InputName
$outputPath = Join-Path $resolvedRunDir $OutputName

if (-not (Test-Path -LiteralPath $inputPath)) {
    throw "Input video not found: $inputPath"
}

$ffprobeJson = & ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of json $inputPath
if ($LASTEXITCODE -ne 0) {
    throw "ffprobe failed for $inputPath"
}

$probe = $ffprobeJson | ConvertFrom-Json
$width = [int]$probe.streams[0].width
$height = [int]$probe.streams[0].height

if ($width -gt 430 -or $height -gt 760) {
    throw "Raw capture is too large for phone-DPI export: ${width}x${height}. Record the narrow viewport, then scale."
}

& ffmpeg -y -i $inputPath `
    -vf "scale=1080:1920:flags=lanczos,setsar=1" `
    -c:v libx264 -pix_fmt yuv420p -movflags +faststart $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "ffmpeg export failed for $inputPath"
}

$auditSeconds = @(3, 9, 16, 24)
foreach ($second in $auditSeconds) {
    $stamp = "{0:00}" -f $second
    $framePath = Join-Path $resolvedRunDir "audit-phone-dpi-final-$stamp.png"
    & ffmpeg -y -ss "00:00:$stamp" -i $outputPath -frames:v 1 -update 1 $framePath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to extract audit frame at 00:00:$stamp"
    }
}

$finalProbeJson = & ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of json $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "ffprobe failed for $outputPath"
}

$finalProbe = $finalProbeJson | ConvertFrom-Json
$finalWidth = [int]$finalProbe.streams[0].width
$finalHeight = [int]$finalProbe.streams[0].height
if ($finalWidth -ne 1080 -or $finalHeight -ne 1920) {
    throw "Final export has wrong dimensions: ${finalWidth}x${finalHeight}"
}

[pscustomobject]@{
    RunDir = $resolvedRunDir.Path
    RawVideo = $inputPath
    RawSize = "${width}x${height}"
    FinalVideo = $outputPath
    FinalSize = "${finalWidth}x${finalHeight}"
    AuditFrames = $auditSeconds | ForEach-Object {
        Join-Path $resolvedRunDir ("audit-phone-dpi-final-{0:00}.png" -f $_)
    }
} | ConvertTo-Json -Depth 3
