#!/usr/bin/env pwsh
# Publishes the Native AOT mt binary and boots it once against a throwaway settings
# directory. Catches AOT-only startup crashes (trimming, reflection init, missing
# native libraries) that dotnet build and JIT test runs cannot see.

param(
    [string]$Configuration = "Release",
    [string]$Rid = "win-x64",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $RepoRoot "src/Ai.Tlbx.MidTerm/Ai.Tlbx.MidTerm.csproj"
$wwwrootProbe = Join-Path $RepoRoot "src/Ai.Tlbx.MidTerm/wwwroot/js/terminal.min.js.br"
$exeName = if ($Rid.StartsWith("win")) { "mt.exe" } else { "mt" }
$publishDir = Join-Path $RepoRoot "src/Ai.Tlbx.MidTerm/bin/$Configuration/net10.0/$Rid/publish"
$exePath = Join-Path $publishDir $exeName

if (-not $SkipPublish) {
    # Standalone probes may start without publish-mode frontend assets.
    # Build them when the release preflight has not already prepared wwwroot.
    if (-not (Test-Path $wwwrootProbe)) {
        $version = (Get-Content (Join-Path $RepoRoot 'src/version.json') -Raw | ConvertFrom-Json).web
        Write-Host "Building publish-mode frontend ($version) for smoke probe..." -ForegroundColor Cyan
        Push-Location (Join-Path $RepoRoot 'src/Ai.Tlbx.MidTerm')
        try {
            & pwsh -NoProfile -ExecutionPolicy Bypass -File frontend-build.ps1 -Version $version -Publish
            if ($LASTEXITCODE -ne 0) {
                throw "frontend publish build failed for smoke probe"
            }
        }
        finally {
            Pop-Location
        }
    }
    if (-not (Test-Path $wwwrootProbe)) {
        throw "AOT smoke probe still misses the publish frontend artifact: $wwwrootProbe"
    }
    Write-Host "Publishing Native AOT $Rid binary for smoke probe..." -ForegroundColor Cyan
    & dotnet publish $projectPath -c $Configuration -r $Rid -p:IsPublishing=true -p:SkipFrontendBuild=true -p:ContinuousIntegrationBuild=true --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "AOT publish failed for $Rid"
    }
}

if (-not (Test-Path $exePath)) {
    throw "Published binary not found: $exePath"
}

$settingsDir = Join-Path ([System.IO.Path]::GetTempPath()) ("mt-aot-probe-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
# Exercise exactly what the release archive/updater installs: mt without native sidecars.
$standaloneDir = Join-Path $settingsDir 'standalone'
New-Item -ItemType Directory -Path $standaloneDir | Out-Null
Copy-Item -LiteralPath $exePath -Destination (Join-Path $standaloneDir $exeName)
$exePath = Join-Path $standaloneDir $exeName
$stdoutLog = Join-Path $settingsDir "probe-stdout.log"
$stderrLog = Join-Path $settingsDir "probe-stderr.log"
$port = Get-Random -Minimum 21000 -Maximum 29000

Write-Host "Booting $exeName on port $port (settings: $settingsDir)..." -ForegroundColor Cyan
$env:MIDTERM_SETTINGS_DIR = $settingsDir
$proc = $null
try {
    $probePassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $probePassword | & $exePath --set-password *> (Join-Path $settingsDir "password-setup.log")
    if ($LASTEXITCODE -ne 0) { throw "AOT smoke probe password setup failed" }
    $backgroundDir = Join-Path $settingsDir 'backgrounds'
    New-Item -ItemType Directory -Path $backgroundDir | Out-Null
    # Tiny RGB PNG, two pixels. Real codec execution catches AOT/native loading failures.
    $pngBytes = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAADUlEQVR4nGP4zwAE/wEHAAH/4iOeWQAAAABJRU5ErkJggg==')
    $oldBackground = Join-Path $backgroundDir 'app-background.png'
    [IO.File]::WriteAllBytes($oldBackground, $pngBytes)
    $settingsPath = Join-Path $settingsDir 'settings.json'
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
    $settings.backgroundImageFileName = 'app-background.png'
    $settings.backgroundImageRevision = 123
    $settings.backgroundImageEnabled = $false
    $settings.uiTransparency = 10
    $settings.terminalTransparency = 35
    $settings | ConvertTo-Json -Depth 64 | Set-Content $settingsPath
    $proc = Start-Process -FilePath $exePath -ArgumentList "--port $port --bind 127.0.0.1" `
        -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru -WindowStyle Hidden

    $version = $null
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
            break
        }
        try {
            $bootstrap = Invoke-RestMethod "https://127.0.0.1:$port/api/bootstrap/login" -SkipCertificateCheck -TimeoutSec 3
            if (-not $bootstrap.certificate.fingerprint) { throw "Certificate bootstrap missing" }
            $loginBody = @{ password = $probePassword } | ConvertTo-Json -Compress
            $login = Invoke-RestMethod "https://127.0.0.1:$port/api/auth/login" -Method Post -ContentType 'application/json' -Body $loginBody -SessionVariable probeSession -SkipCertificateCheck -TimeoutSec 5
            if (-not $login.success) { throw "Probe authentication failed" }
            $version = Invoke-RestMethod "https://127.0.0.1:$port/api/version" -WebSession $probeSession -SkipCertificateCheck -TimeoutSec 3
            break
        } catch {
            Start-Sleep -Milliseconds 750
        }
    }

    if ($proc.HasExited) {
        Get-Content $stdoutLog -ErrorAction SilentlyContinue | Select-Object -Last 20 | Write-Host
        Get-Content $stderrLog -ErrorAction SilentlyContinue | Select-Object -Last 20 | Write-Host
        throw "AOT smoke probe FAILED: $exeName exited with code $($proc.ExitCode) during startup. See $stdoutLog"
    }
    if (-not $version) {
        throw "AOT smoke probe FAILED: $exeName did not answer /api/version within 45s. See $stdoutLog"
    }
    Write-Host "  /api/version -> $version" -ForegroundColor Green

    $anonymous = Invoke-WebRequest "https://127.0.0.1:$port/api/version" -SkipCertificateCheck -SkipHttpErrorCheck -TimeoutSec 5
    if ([int]$anonymous.StatusCode -ne 401) { throw "AOT smoke probe FAILED: anonymous API access was not denied" }
    $graphsStatus = 0
    try {
        $response = Invoke-WebRequest "https://127.0.0.1:$port/api/graphs" -WebSession $probeSession -SkipCertificateCheck -TimeoutSec 5
        $graphsStatus = [int]$response.StatusCode
    } catch {
        $graphsStatus = [int]$_.Exception.Response.StatusCode
    }
    if ($graphsStatus -ne 200) {
        throw "AOT smoke probe FAILED: /api/graphs returned $graphsStatus"
    }
    Write-Host "  /api/graphs -> $graphsStatus" -ForegroundColor Green

    $dbPath = Join-Path $settingsDir "action-graphs.db"
    if (-not (Test-Path $dbPath)) {
        throw "AOT smoke probe FAILED: SQLite database was not created at $dbPath"
    }
    Write-Host "  action-graphs.db created ($((Get-Item $dbPath).Length) bytes)" -ForegroundColor Green

    $backgroundUrl = "https://127.0.0.1:$port/api/settings/background-image"
    $webpPath = Join-Path $settingsDir 'migrated.webp'
    $background = Invoke-WebRequest "$($backgroundUrl)?v=123&encoding=2" -WebSession $probeSession -SkipCertificateCheck -TimeoutSec 10 -OutFile $webpPath -PassThru
    $webp = [IO.File]::ReadAllBytes($webpPath)
    if ($background.Headers['Content-Type'] -ne 'image/webp' -or $webp.Length -lt 16 -or
        [Text.Encoding]::ASCII.GetString($webp, 0, 4) -ne 'RIFF' -or
        [Text.Encoding]::ASCII.GetString($webp, 8, 4) -ne 'WEBP') {
        throw 'AOT smoke probe FAILED: stored background was not converted to WebP'
    }
    $after = Invoke-RestMethod "https://127.0.0.1:$port/api/settings" -WebSession $probeSession -SkipCertificateCheck -TimeoutSec 5
    if ($after.backgroundImageFileName -ne 'app-background-v2.webp' -or (Test-Path $oldBackground) -or
        $after.backgroundImageRevision -le 123 -or $after.backgroundImageEnabled -or
        $after.uiTransparency -ne 10 -or $after.terminalTransparency -ne 35) {
        throw 'AOT smoke probe FAILED: background migration did not preserve preferences or remove the old file'
    }
    $form = [Net.Http.MultipartFormDataContent]::new()
    try {
        $content = [Net.Http.ByteArrayContent]::new($webp)
        $content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('image/webp')
        $form.Add($content, 'file', 'upload.webp')
        $body = $form.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $upload = Invoke-RestMethod $backgroundUrl -Method Post -Body $body -ContentType $form.Headers.ContentType.ToString() -WebSession $probeSession -SkipCertificateCheck -TimeoutSec 10
    } finally {
        $form.Dispose()
    }
    if ($upload.fileName -ne 'app-background-v2.webp') { throw 'AOT smoke probe FAILED: WebP upload failed' }
    Write-Host '  background migration and upload -> WebP (bundled native codec)' -ForegroundColor Green
    Write-Host "AOT smoke probe PASSED." -ForegroundColor Green
}
finally {
    Remove-Item Env:\MIDTERM_SETTINGS_DIR -ErrorAction SilentlyContinue
    if ($proc -and -not $proc.HasExited) {
        try { $proc.Kill($true) } catch {}
        $proc.WaitForExit(5000) | Out-Null
    }
    Start-Sleep -Milliseconds 500
    $artifactDir = Join-Path $RepoRoot '.dev/aot-smoke'
    New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
    Get-ChildItem -LiteralPath $settingsDir -Filter '*.log' | Copy-Item -Destination $artifactDir -Force
    try { Remove-Item -LiteralPath $settingsDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}
}
