#!/usr/bin/env pwsh
# Sign MidTerm release artifacts using openssl
# Updates version.json files with checksums and ECDSA P-256 signatures

param(
    [Parameter(Mandatory=$true)]
    [string]$ArtifactsPath
)

$ErrorActionPreference = "Stop"

# Check for signing key (base64-encoded PKCS#8 PEM)
$privateKeyB64 = $env:SIGNING_PRIVATE_KEY
if (-not $privateKeyB64) {
    Write-Host "Warning: SIGNING_PRIVATE_KEY not set, releases will be unsigned" -ForegroundColor Yellow
    exit 0
}

Write-Host "Signing release artifacts..."

function Get-ChecksumsFromManifest {
    param(
        [Parameter(Mandatory=$true)]
        [string]$ManifestPath,

        [Parameter(Mandatory=$true)]
        [string[]]$ExpectedFiles
    )

    $checksums = @{}

    foreach ($line in Get-Content $ManifestPath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') {
            $hash = $matches[1].ToLowerInvariant()
            $fileName = [System.IO.Path]::GetFileName($matches[2].Trim())
            $checksums[$fileName] = $hash
        }
    }

    $filteredChecksums = @{}
    foreach ($expectedFile in $ExpectedFiles) {
        if ($checksums.ContainsKey($expectedFile)) {
            $filteredChecksums[$expectedFile] = $checksums[$expectedFile]
        }
    }

    return $filteredChecksums
}

# Write private key to temp file
$keyFile = [System.IO.Path]::GetTempFileName()
try {
    [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($privateKeyB64)) | Set-Content $keyFile -NoNewline

    # Process each platform
    $platforms = @("win-x64", "win-x86", "osx-arm64", "osx-x64", "linux-x64", "linux-arm64")

    foreach ($platform in $platforms) {
        $platformDir = Join-Path $ArtifactsPath $platform
        if (-not (Test-Path $platformDir)) {
            Write-Host "  Skipping $platform (not found)"
            continue
        }

        Write-Host "  Processing $platform..."

        $versionJsonPath = Join-Path $platformDir "version.json"
        if (-not (Test-Path $versionJsonPath)) {
            Write-Host "    Warning: version.json not found" -ForegroundColor Yellow
            continue
        }

        # Read version.json to check for web-only release
        $versionJson = Get-Content $versionJsonPath -Raw | ConvertFrom-Json
        $isWebOnly = $versionJson.webOnly -eq $true

        # Compute checksums for binaries. Web-only releases remain a single release mode:
        # running installs preserve their current mthost + mtagenthost, but release archives
        # may still include host binaries for fresh installs and offline/manual flows.
        # The signed manifest intentionally omits mthost for web-only releases so the PTY host
        # preservation contract stays explicit. mtagenthost stays signed when present because
        # it still ships in the archive set even though durable self-updaters preserve the
        # installed runtime on web-only updates.
        $checksums = @{}
        $binaries = if ($isWebOnly) { @("mt", "mtagenthost") } else { @("mt", "mthost", "mtagenthost") }
        if ($platform.StartsWith("win-")) {
            $binaries += "mttmux"
        }
        $ext = if ($platform.StartsWith("win-")) { ".exe" } else { "" }
        $expectedFiles = $binaries | ForEach-Object { "$_$ext" }
        $checksumManifestPath = Join-Path $platformDir "SHA256SUMS.txt"

        if ($isWebOnly) {
            Write-Host "    Web-only release: signing mt + mtagenthost; running installs still preserve mthost + mtagenthost" -ForegroundColor Cyan
        }

        if (Test-Path $checksumManifestPath) {
            $checksums = Get-ChecksumsFromManifest -ManifestPath $checksumManifestPath -ExpectedFiles $expectedFiles

            if ($checksums.Count -gt 0) {
                Write-Host "    Reusing checksums from SHA256SUMS.txt" -ForegroundColor DarkGray
                foreach ($fileName in $checksums.Keys | Sort-Object) {
                    Write-Host "    $fileName = $($checksums[$fileName])"
                }
            }
        }

        if ($checksums.Count -eq 0) {
            foreach ($binary in $binaries) {
                $binaryPath = Join-Path $platformDir "$binary$ext"
                if (Test-Path $binaryPath) {
                    $hash = (Get-FileHash -Path $binaryPath -Algorithm SHA256).Hash.ToLower()
                    $checksums["$binary$ext"] = $hash
                    Write-Host "    $binary$ext = $hash"
                }
            }
        }

        if ($checksums.Count -eq 0) {
            Write-Host "    Warning: No binaries found" -ForegroundColor Yellow
            continue
        }

        # Create sorted JSON of checksums (deterministic for signing)
        $sortedChecksums = [ordered]@{}
        foreach ($key in $checksums.Keys | Sort-Object) {
            $sortedChecksums[$key] = $checksums[$key]
        }
        $checksumJson = $sortedChecksums | ConvertTo-Json -Compress

        # Sign with openssl
        $msgFile = [System.IO.Path]::GetTempFileName()
        $sigFile = [System.IO.Path]::GetTempFileName()
        try {
            $checksumJson | Set-Content $msgFile -NoNewline -Encoding UTF8
            $opensslCmd = if (Get-Command openssl -ErrorAction SilentlyContinue) { 'openssl' }
                          elseif (Test-Path 'C:\Program Files\Git\usr\bin\openssl.exe') { 'C:\Program Files\Git\usr\bin\openssl.exe' }
                          else { throw 'openssl not found' }
            & $opensslCmd dgst -sha256 -sign $keyFile -out $sigFile $msgFile
            if ($LASTEXITCODE -ne 0) { throw "openssl signing failed" }
            $signature = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($sigFile))
        } finally {
            Remove-Item $msgFile -ErrorAction SilentlyContinue
            Remove-Item $sigFile -ErrorAction SilentlyContinue
        }

        # Update version.json with checksums and signature
        $versionJson | Add-Member -NotePropertyName "checksums" -NotePropertyValue $checksums -Force
        $versionJson | Add-Member -NotePropertyName "signature" -NotePropertyValue $signature -Force

        # Write updated version.json
        $versionJson | ConvertTo-Json -Depth 10 | Set-Content $versionJsonPath -Encoding UTF8
        Write-Host "    Signed version.json"
    }
} finally {
    Remove-Item $keyFile -ErrorAction SilentlyContinue
}

Write-Host "Release signing complete" -ForegroundColor Green
