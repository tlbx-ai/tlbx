param(
    [Parameter(Mandatory = $true)]
    [string]$RunDir,

    [switch]$AuditFrames
)

$ErrorActionPreference = "Stop"

$resolvedRunDir = Resolve-Path -LiteralPath $RunDir
$videos = Get-ChildItem -LiteralPath $resolvedRunDir -Filter "*.webm" -File
if ($videos.Count -eq 0) {
    throw "No .webm recordings found in $($resolvedRunDir.Path)"
}

$results = @()
foreach ($video in $videos) {
    $outputPath = Join-Path $resolvedRunDir ("{0}-final.mp4" -f $video.BaseName)

    & ffmpeg -y -i $video.FullName `
        -vf "scale=1920:1080:flags=lanczos,setsar=1" `
        -c:v libx264 -pix_fmt yuv420p -movflags +faststart $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg export failed for $($video.FullName)"
    }

    $probeJson = & ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of json $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "ffprobe failed for $outputPath"
    }

    $probe = $probeJson | ConvertFrom-Json
    $width = [int]$probe.streams[0].width
    $height = [int]$probe.streams[0].height
    if ($width -ne 1920 -or $height -ne 1080) {
        throw "Final export has wrong dimensions: ${width}x${height}"
    }

    $frames = @()
    if ($AuditFrames) {
        foreach ($second in @(1, 4, 8)) {
            $stamp = "{0:00}" -f $second
            $framePath = Join-Path $resolvedRunDir ("{0}-audit-{1}.png" -f $video.BaseName, $stamp)
            & ffmpeg -y -ss "00:00:$stamp" -i $outputPath -frames:v 1 -update 1 $framePath | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to extract audit frame at 00:00:$stamp from $outputPath"
            }
            $frames += $framePath
        }
    }

    $results += [pscustomobject]@{
        Input = $video.FullName
        Output = $outputPath
        Size = "${width}x${height}"
        AuditFrames = $frames
    }
}

$results | ConvertTo-Json -Depth 4
