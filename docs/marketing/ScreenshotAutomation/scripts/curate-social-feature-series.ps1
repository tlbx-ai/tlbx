param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRunDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

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

$clips = @(
    @{
        Slug = "01-adhoc-session"
        Source = Join-Path $resolvedSourceRunDir "01-adhoc-session-final.mp4"
        Start = "00:00:08.2"
        Duration = "00:00:08.4"
        Feature = "Ad-hoc session"
        Hook = "Need a shell now?"
    },
    @{
        Slug = "02-web-terminal"
        Source = Join-Path $resolvedSourceRunDir "02-web-terminal-final.mp4"
        Start = "00:00:12.4"
        Duration = "00:00:09.0"
        Feature = "Web terminal"
        Hook = "The terminal is real. The surface is the browser."
    },
    @{
        Slug = "03-real-copy-paste"
        Source = Join-Path $resolvedSourceRunDir "03-real-copy-paste-final.mp4"
        Start = "00:00:07.0"
        Duration = "00:00:07.6"
        Feature = "Real copy and paste"
        Hook = "Paste should stay exact."
    },
    @{
        Slug = "04-file-radar"
        Source = Join-Path $resolvedSourceRunDir "04-file-radar-final.mp4"
        Start = "00:00:10.0"
        Duration = "00:00:07.6"
        Feature = "File Radar"
        Hook = "Terminal output should be clickable context."
    },
    @{
        Slug = "05-bookmarks"
        Source = Join-Path $resolvedSourceRunDir "05-bookmarks-final.mp4"
        Start = "00:00:15.2"
        Duration = "00:00:07.6"
        Feature = "Bookmarks"
        Hook = "Some shells are worth coming back to."
    },
    @{
        Slug = "06-multi-agents"
        Source = Join-Path $resolvedSourceRunDir "06-multi-agents-final.mp4"
        Start = "00:00:11.5"
        Duration = "00:00:08.2"
        Feature = "Multi-agent supervision"
        Hook = "Agents need a control room."
    },
    @{
        Slug = "07-side-by-side-console"
        Source = Join-Path $resolvedSourceRunDir "07-side-by-side-console-final.mp4"
        Start = "00:00:16.0"
        Duration = "00:00:08.8"
        Feature = "Side-by-side console work"
        Hook = "Not every job belongs in one pane."
    },
    @{
        Slug = "08-dev-browser-validation"
        Source = Join-Path $resolvedSourceRunDir "08-dev-browser-validation-final.mp4"
        Start = "00:00:16.8"
        Duration = "00:00:10.5"
        Feature = "Dev Browser validation"
        Hook = "The preview belongs next to the command."
    },
    @{
        Slug = "09-desktop-control"
        Source = Join-Path $resolvedSourceRunDir "09-desktop-control-final.mp4"
        Start = "00:00:14.5"
        Duration = "00:00:09.0"
        Feature = "Desktop control"
        Hook = "Desktop mode keeps the whole workspace visible."
    },
    @{
        Slug = "10-files-git-context"
        Source = Join-Path $resolvedSourceRunDir "10-files-git-context-final.mp4"
        Start = "00:00:13.5"
        Duration = "00:00:08.0"
        Feature = "Files and Git context"
        Hook = "The shell needs surrounding context."
    }
)

$manifest = @()

foreach ($clip in $clips) {
    $sourcePath = Resolve-Path -LiteralPath $clip.Source
    $outputPath = Join-Path $resolvedOutputDir ("{0}.mp4" -f $clip.Slug)

    & ffmpeg -hide_banner -loglevel error -y `
        -ss $clip.Start -t $clip.Duration -i $sourcePath.Path `
        -vf "scale=1920:1080:flags=lanczos,setsar=1" `
        -c:v libx264 -pix_fmt yuv420p -movflags +faststart `
        -an $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg curate failed for $($clip.Slug)"
    }

    $probeJson = & ffprobe -v error -select_streams v:0 -show_entries stream=width,height -show_entries format=duration -of json $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "ffprobe failed for $outputPath"
    }
    $probe = $probeJson | ConvertFrom-Json
    $width = [int]$probe.streams[0].width
    $height = [int]$probe.streams[0].height
    $duration = [double]::Parse($probe.format.duration, [System.Globalization.CultureInfo]::InvariantCulture)
    if ($width -ne 1920 -or $height -ne 1080) {
        throw "Curated export has wrong dimensions for $($clip.Slug): ${width}x${height}"
    }

    $frames = @()
    if ($AuditFrames) {
        foreach ($second in @(1, [Math]::Max(1, [Math]::Floor($duration / 2)), [Math]::Max(1, [Math]::Floor($duration - 1)))) {
            $stamp = "{0:00}" -f [int]$second
            $framePath = Join-Path $resolvedOutputDir ("{0}-audit-{1}.png" -f $clip.Slug, $stamp)
            & ffmpeg -hide_banner -loglevel error -y -ss "00:00:$stamp" -i $outputPath -frames:v 1 -update 1 $framePath
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to extract audit frame at 00:00:$stamp from $outputPath"
            }
            $frames += (Split-Path -Leaf $framePath)
        }
    }

    $manifest += [pscustomobject]@{
        slug = $clip.Slug
        feature = $clip.Feature
        hook = $clip.Hook
        source = $sourcePath.Path
        start = $clip.Start
        durationRequested = $clip.Duration
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
    -vf "scale=1920:1080:flags=lanczos,setsar=1" `
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
if ($stitchWidth -ne 1920 -or $stitchHeight -ne 1080) {
    throw "Stitchup export has wrong dimensions: ${stitchWidth}x${stitchHeight}"
}

$stitchup = [pscustomobject]@{
    slug = "00-all-features-stitchup"
    feature = "All ten desktop feature clips"
    hook = "Ten MidTerm desktop-mode features in one pass."
    output = (Split-Path -Leaf $stitchupPath)
    durationSeconds = [Math]::Round($stitchDuration, 2)
    size = "${stitchWidth}x${stitchHeight}"
    sources = $manifest.output
}

$manifestPath = Join-Path $resolvedOutputDir "manifest.json"
@($stitchup) + $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
@($stitchup) + $manifest | ConvertTo-Json -Depth 5
