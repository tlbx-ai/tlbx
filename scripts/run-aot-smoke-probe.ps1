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
$publishDir = Join-Path $RepoRoot "src/Ai.Tlbx.MidTerm/bin/$Configuration/net11.0/$Rid/publish"
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
# Ask the OS for an available port instead of guessing inside a busy dev range.
$portReservation = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$portReservation.Start()
try { $port = ([Net.IPEndPoint]$portReservation.LocalEndpoint).Port }
finally { $portReservation.Stop() }

Write-Host "Booting $exeName on port $port (settings: $settingsDir)..." -ForegroundColor Cyan
$previousSettingsDir = $env:MIDTERM_SETTINGS_DIR
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

    # Execute the vendor-neutral bootstrap in the actual NativeAOT binary.
    $mcpProject = Join-Path $settingsDir 'mcp-client'
    New-Item -ItemType Directory -Path $mcpProject | Out-Null
    $mcpOrigin = "https://127.0.0.1:$port"
    $mcpToken = $probeSession.Cookies.GetCookies([Uri]$mcpOrigin)['mm-session'].Value
    $mcpInfo = [Diagnostics.ProcessStartInfo]::new($exePath)
    $mcpInfo.WorkingDirectory = $mcpProject
    $mcpInfo.UseShellExecute = $false
    $mcpInfo.CreateNoWindow = $true
    $mcpInfo.RedirectStandardInput = $true
    $mcpInfo.RedirectStandardOutput = $true
    $mcpInfo.RedirectStandardError = $true
    $mcpInfo.ArgumentList.Add('mcp')
    $mcpInfo.ArgumentList.Add('bootstrap')
    $mcpInfo.Environment['MT_BASE_URL'] = $mcpOrigin
    $mcpInfo.Environment['MT_TOKEN'] = $mcpToken
    $mcpInfo.Environment['TLBX_MCP_CERT_SHA256'] = $bootstrap.certificate.fingerprint
    $null = $mcpInfo.Environment.Remove('MT_API_KEY')
    $null = $mcpInfo.Environment.Remove('TLBX_MCP_BOOTSTRAP')
    $mcpProcess = $null
    $mcpCredentials = $null
    try {
        $mcpProcess = [Diagnostics.Process]::Start($mcpInfo)
        $mcpProcess.StandardInput.Close()
        $mcpOutput = $mcpProcess.StandardOutput.ReadToEndAsync()
        $mcpErrors = $mcpProcess.StandardError.ReadToEndAsync()
        if (-not $mcpProcess.WaitForExit(15000)) { throw 'AOT MCP bootstrap did not exit within 15 seconds.' }
        $mcpJson = $mcpOutput.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $settingsDir 'mcp-bootstrap.stderr.log'), $mcpErrors.GetAwaiter().GetResult())
        if ($mcpProcess.ExitCode -ne 0) { throw 'AOT MCP bootstrap failed; inspect mcp-bootstrap.stderr.log.' }
        $mcpDescriptor = $mcpJson | ConvertFrom-Json
        if ($mcpDescriptor.name -ne 'tlbx' -or $mcpDescriptor.transport -ne 'stdio' -or $mcpDescriptor.command -ne $exePath -or
            $mcpDescriptor.args[0] -ne 'mcp' -or $mcpDescriptor.args[1] -ne '--stdio' -or $mcpDescriptor.args[2] -ne '--credentials-file' -or
            $mcpDescriptor.startupTimeoutSeconds -ne 30 -or $mcpDescriptor.toolTimeoutSeconds -ne 660 -or $mcpJson.Contains($mcpToken)) {
            throw 'AOT MCP bootstrap returned an invalid or secret-bearing descriptor.'
        }
        $expectedProfile = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($mcpProject + "`n" + $mcpOrigin))).ToLowerInvariant()
        $candidate = [string]$mcpDescriptor.args[-1]
        if ((Split-Path $candidate -Leaf) -ne 'credentials.json' -or (Split-Path (Split-Path $candidate) -Leaf) -ne $expectedProfile) {
            throw 'Unexpected AOT MCP credential path.'
        }
        $mcpCredentials = $candidate
        $privateValues = Get-Content -LiteralPath $mcpCredentials -Raw | ConvertFrom-Json
        if ($privateValues.MT_TOKEN -ne $mcpToken -or $privateValues.MT_BASE_URL -ne $mcpOrigin) { throw 'AOT MCP credential authority mismatch.' }
        [IO.File]::WriteAllText((Join-Path $settingsDir 'mcp-bootstrap.json'), $mcpJson)
        Write-Host '  mcp bootstrap -> vendor-neutral stdio JSON / private authentication' -ForegroundColor Green
    } finally {
        if ($mcpProcess) {
            if (-not $mcpProcess.HasExited) { $mcpProcess.Kill($true); $mcpProcess.WaitForExit(3000) | Out-Null }
            $mcpProcess.Dispose()
        }
        if ($mcpCredentials) {
            Remove-Item -LiteralPath $mcpCredentials
            Remove-Item -LiteralPath (Split-Path $mcpCredentials)
        }
    }

    # Exercise the real published worker command and embedded native closure.
    # Kill only a probe-owned worker, then require HTTP survival and a fresh worker.
    foreach ($attempt in 1..2) {
        $worker = $null
        $workerInfo = [Diagnostics.ProcessStartInfo]::new($exePath)
        $workerInfo.UseShellExecute = $false
        $workerInfo.CreateNoWindow = $true
        $workerInfo.RedirectStandardInput = $true
        $workerInfo.RedirectStandardOutput = $true
        $workerInfo.RedirectStandardError = $true
        foreach ($argument in @('connect-worker', "$($proc.Id)", "$($proc.StartTime.ToUniversalTime().Ticks)")) {
            $workerInfo.ArgumentList.Add($argument)
        }
        try {
            $worker = [Diagnostics.Process]::Start($workerInfo)
            $workerErrors = $worker.StandardError.ReadToEndAsync()
            $initialization = [Text.Encoding]::UTF8.GetBytes((@{
                settingsDirectory = $settingsDir
                configuration = @{iceServers = @()}
            } | ConvertTo-Json -Compress -Depth 4))
            $header = [byte[]]::new(5)
            $header[0] = 1
            [BitConverter]::GetBytes([int]$initialization.Length).CopyTo($header, 1)
            $worker.StandardInput.BaseStream.Write($header, 0, $header.Length)
            $worker.StandardInput.BaseStream.Write($initialization, 0, $initialization.Length)
            $worker.StandardInput.BaseStream.Flush()
            $ready = [byte[]]::new(5)
            $worker.StandardOutput.BaseStream.ReadExactlyAsync([Memory[byte]]::new($ready), [Threading.CancellationToken]::None).AsTask().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
            if ($ready[0] -ne 4 -or [BitConverter]::ToInt32($ready, 1) -ne 0) { throw 'Invalid AOT worker readiness frame.' }
            $workerOutput = $worker.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null)
            if ($attempt -eq 1) { $worker.Kill() } else { $worker.StandardInput.Close() }
            if (-not $worker.WaitForExit(5000)) { throw 'AOT worker failed to exit within five seconds.' }
            $workerOutput.WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult()
            [IO.File]::WriteAllText((Join-Path $settingsDir "worker-$attempt.log"), $workerErrors.GetAwaiter().GetResult())
            $afterWorker = Invoke-RestMethod "https://127.0.0.1:$port/api/version" -WebSession $probeSession -SkipCertificateCheck -TimeoutSec 3
            if ($proc.HasExited -or $afterWorker -ne $version) { throw 'Worker failure interrupted the AOT control plane.' }
        } finally {
            if ($worker) {
                if (-not $worker.HasExited) { $worker.Kill($true); $worker.WaitForExit(3000) | Out-Null }
                $worker.Dispose()
            }
        }
    }
    if ($IsWindows -and @($proc.Modules | Where-Object ModuleName -Like '*datachannel*').Count -ne 0) {
        throw 'Native WebRTC was loaded into the main AOT server.'
    }
    Write-Host '  Link worker crash / replacement / graceful shutdown -> HTTP survives, native code stays outside mt' -ForegroundColor Green


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

    # MCP tool catalogs are constructed during AOT startup. Verify that the new
    # endpoint still requires both ordinary authentication and verified context.
    $mcpUrl = "https://127.0.0.1:$port/api/mcp"
    $mcpAnonymous = Invoke-WebRequest $mcpUrl -Method Post -ContentType 'application/json' -Body '{}' -SkipCertificateCheck -SkipHttpErrorCheck -TimeoutSec 5
    $mcpUnbound = Invoke-WebRequest $mcpUrl -Method Post -ContentType 'application/json' -Body '{}' -WebSession $probeSession -SkipCertificateCheck -SkipHttpErrorCheck -TimeoutSec 5
    $mcpUnboundText = if ($mcpUnbound.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($mcpUnbound.Content) } else { [string]$mcpUnbound.Content }
    if ([int]$mcpAnonymous.StatusCode -ne 401 -or [int]$mcpUnbound.StatusCode -ne 409 -or
        $mcpUnbound.Headers['Content-Type'] -notlike 'text/plain*' -or
        $mcpUnboundText -notlike '*TLBX_MCP_CONTEXT_REQUIRED*') {
        throw "AOT smoke probe FAILED: MCP access gate: anonymous=$($mcpAnonymous.StatusCode); unbound=$($mcpUnbound.StatusCode); contentType=$($mcpUnbound.Headers['Content-Type']); body=$mcpUnboundText"
    }
    Write-Host '  /api/mcp -> anonymous401 / authenticated-unbound409' -ForegroundColor Green

    # Read the embedded PDF image from the standalone published executable, never source wwwroot.
    $pdfLoadingPath = Join-Path $settingsDir 'pdf-loading-icon.gif'
    $pdfLoading = Invoke-WebRequest "https://127.0.0.1:$port/pdf/images/loading-icon.gif" -WebSession $probeSession -SkipCertificateCheck -SkipHttpErrorCheck -TimeoutSec 5 -OutFile $pdfLoadingPath -PassThru
    $pdfLoadingBytes = [IO.File]::ReadAllBytes($pdfLoadingPath)
    if ([int]$pdfLoading.StatusCode -ne 200 -or $pdfLoading.Headers['Content-Type'] -ne 'image/gif' -or
        $pdfLoadingBytes.Length -lt 6 -or
        [Text.Encoding]::ASCII.GetString($pdfLoadingBytes, 0, 6) -notin @('GIF87a', 'GIF89a')) {
        throw 'AOT smoke probe FAILED: embedded /pdf/images/loading-icon.gif was not served as a valid GIF'
    }
    Write-Host '  /pdf/images/loading-icon.gif -> 200 image/gif (embedded GIF signature)' -ForegroundColor Green

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
    $env:MIDTERM_SETTINGS_DIR = $previousSettingsDir
    if ($proc -and -not $proc.HasExited) {
        try { $proc.Kill($true) } catch {}
        $proc.WaitForExit(5000) | Out-Null
    }
    Start-Sleep -Milliseconds 500
    $artifactDir = Join-Path $RepoRoot ('.artifacts/aot-smoke/' + (Split-Path $settingsDir -Leaf))
    New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
    Get-ChildItem -LiteralPath $settingsDir -Filter '*.log' | Copy-Item -Destination $artifactDir -Force
    Write-Host "AOT smoke logs: $artifactDir"
    $resolvedSettingsDir = [IO.Path]::GetFullPath($settingsDir)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedSettingsDir.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolvedSettingsDir -Leaf) -notlike 'mt-aot-probe-*') { throw 'Unsafe AOT probe cleanup path.' }
    try { Remove-Item -LiteralPath $resolvedSettingsDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}
}
