param(
    [Parameter(Mandatory = $true)]
    [string]$RunDir,

    [switch]$AuditFrames
)

$ErrorActionPreference = "Stop"

$resolvedRunDir = Resolve-Path -LiteralPath $RunDir
$rawClips = Get-ChildItem -LiteralPath $resolvedRunDir -Filter "*.webm" -File | Sort-Object Name

if ($rawClips.Count -eq 0) {
    throw "No .webm captures found in $resolvedRunDir"
}

$manifest = @()
$auditFrameFiles = @()

foreach ($clip in $rawClips) {
    $probeJson = & ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of json $clip.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "ffprobe failed for $($clip.FullName)"
    }
    $probe = $probeJson | ConvertFrom-Json
    $width = [int]$probe.streams[0].width
    $height = [int]$probe.streams[0].height
    if ($width -gt 430 -or $height -gt 760) {
        throw "Raw capture is too large for phone-DPI export: $($clip.Name) is ${width}x${height}. Record the narrow viewport, then scale."
    }

    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($clip.Name)
    $outputPath = Join-Path $resolvedRunDir "$baseName-final.mp4"

    & ffmpeg -hide_banner -loglevel error -y -i $clip.FullName `
        -vf "scale=1080:1920:flags=lanczos,setsar=1" `
        -c:v libx264 -pix_fmt yuv420p -movflags +faststart -an $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg export failed for $($clip.Name)"
    }

    $finalProbeJson = & ffprobe -v error -select_streams v:0 -show_entries stream=width,height -show_entries format=duration -of json $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "ffprobe failed for $outputPath"
    }
    $finalProbe = $finalProbeJson | ConvertFrom-Json
    $finalWidth = [int]$finalProbe.streams[0].width
    $finalHeight = [int]$finalProbe.streams[0].height
    $duration = [double]::Parse($finalProbe.format.duration, [System.Globalization.CultureInfo]::InvariantCulture)
    if ($finalWidth -ne 1080 -or $finalHeight -ne 1920) {
        throw "Final export has wrong dimensions for ${baseName}: ${finalWidth}x${finalHeight}"
    }

    $frames = @()
    if ($AuditFrames) {
        foreach ($second in @(1, [Math]::Max(1, [Math]::Floor($duration / 2)), [Math]::Max(1, [Math]::Floor($duration - 1)))) {
            $stamp = "{0:00}" -f [int]$second
            $framePath = Join-Path $resolvedRunDir ("{0}-audit-{1}.png" -f $baseName, $stamp)
            & ffmpeg -hide_banner -loglevel error -y -ss "00:00:$stamp" -i $outputPath -frames:v 1 -update 1 $framePath
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to extract audit frame at 00:00:$stamp from $outputPath"
            }
            $frames += (Split-Path -Leaf $framePath)
            $auditFrameFiles += $framePath
        }
    }

    $manifest += [pscustomobject]@{
        slug = $baseName
        raw = $clip.Name
        rawSize = "${width}x${height}"
        output = (Split-Path -Leaf $outputPath)
        size = "${finalWidth}x${finalHeight}"
        durationSeconds = [Math]::Round($duration, 2)
        auditFrames = $frames
    }
}

if ($AuditFrames -and $auditFrameFiles.Count -gt 0) {
    $contactSheet = Join-Path $resolvedRunDir "audit-contact-sheet.png"
    $listFile = Join-Path $resolvedRunDir "audit-frame-list.txt"
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

$manifestPath = Join-Path $resolvedRunDir "export-manifest.json"
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
$manifest | ConvertTo-Json -Depth 4
