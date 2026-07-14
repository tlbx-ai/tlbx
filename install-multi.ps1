#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Installs multiple isolated MidTerm Windows service instances on one machine.

.DESCRIPTION
    This installer is intentionally separate from install.ps1. The normal install
    path keeps the historical MidTerm service, paths, and port. Multi-instance
    installs use one install directory, settings directory, service name, and
    update scope per instance.
#>

[CmdletBinding()]
param(
    [ValidateSet("install", "plan", "list", "update", "update-all", "remove")]
    [string]$Mode = "install",

    [string[]]$Names = @(),
    [int]$Count = 0,
    [int]$BasePort = 2000,
    [int[]]$Ports = @(),
    [string]$BindAddress = "0.0.0.0",
    [string]$RootDir = "$env:ProgramData\MidTerm\instances",
    [string]$InstallRoot = "$env:ProgramFiles\MidTerm\instances",
    [string]$VersionTag = "latest",
    [string]$AssetPath = "",
    [string]$PasswordHash = "",
    [string]$Password = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$repo = "tlbx-ai/tlbx"

function Assert-Windows {
    if (-not $IsWindows) {
        throw "install-multi.ps1 is the Windows multi-instance installer. Use install-multi.sh on macOS/Linux."
    }
}

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session."
    }
}

function Normalize-InstanceName([string]$value) {
    $normalized = ($value.Trim() -replace '[^A-Za-z0-9_-]', '-').Trim('-_')
    if ([string]::IsNullOrWhiteSpace($normalized)) { throw "Invalid instance name '$value'." }
    return $normalized
}

function Resolve-InstanceNames {
    if ($Names.Count -gt 0) {
        $resolved = @()
        foreach ($entry in $Names) {
            foreach ($name in ([string]$entry -split ",")) {
                if (-not [string]::IsNullOrWhiteSpace($name)) {
                    $resolved += Normalize-InstanceName $name
                }
            }
        }
        return $resolved
    }

    if ($Count -le 0) {
        $script:Count = 1
    }

    return @(1..$Count | ForEach-Object { "user$_" })
}

function Test-PortFree([int]$port) {
    $listener = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    return $null -eq $listener
}

function Resolve-Ports([string[]]$instanceNames) {
    if ($Ports.Count -gt 0 -and $Ports.Count -ne $instanceNames.Count) {
        throw "-Ports must contain exactly one port per instance."
    }

    if ($Ports.Count -gt 0) {
        return @($Ports)
    }

    $resolved = @()
    $candidate = $BasePort
    foreach ($name in $instanceNames) {
        while (-not (Test-PortFree $candidate) -or $resolved -contains $candidate) {
            $candidate++
        }
        $resolved += $candidate
        $candidate++
    }
    return $resolved
}

function Get-InstanceLayout([string]$name, [int]$port) {
    $safe = Normalize-InstanceName $name
    [pscustomobject]@{
        Name = $safe
        Port = $port
        ServiceName = "MidTerm-$safe"
        InstallDir = Join-Path $InstallRoot $safe
        SettingsDir = Join-Path $RootDir $safe
        ManifestPath = Join-Path (Join-Path $RootDir $safe) "instance.json"
    }
}

function Resolve-AssetName {
    switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        "X86" { return "mt-win-x86.zip" }
        "X64" { return "mt-win-x64.zip" }
        "Arm64" { return "mt-win-x64.zip" }
        default { throw "Unsupported Windows architecture: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" }
    }
}

function Download-ReleaseAsset([string]$destinationDir) {
    if ($AssetPath) {
        return (Resolve-Path $AssetPath).Path
    }

    $assetName = Resolve-AssetName
    $releaseUri = if ($VersionTag -eq "latest") {
        "https://api.github.com/repos/$repo/releases/latest"
    } else {
        "https://api.github.com/repos/$repo/releases/tags/$VersionTag"
    }

    $release = Invoke-RestMethod -Headers @{ "User-Agent" = "MidTerm multi-instance installer" } -Uri $releaseUri
    $asset = @($release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1)
    if (-not $asset) {
        $available = ($release.assets | ForEach-Object { $_.name }) -join ", "
        throw "Release '$($release.tag_name)' does not contain required asset '$assetName'. Available: $available"
    }

    New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
    $destination = Join-Path $destinationDir $assetName
    Invoke-WebRequest -Headers @{ "User-Agent" = "MidTerm multi-instance installer" } -Uri $asset.browser_download_url -OutFile $destination
    return $destination
}

function Expand-Asset([string]$assetPath) {
    $extractDir = Join-Path ([IO.Path]::GetTempPath()) ("midterm-multi-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
    Expand-Archive -Path $assetPath -DestinationPath $extractDir -Force
    return $extractDir
}

function Resolve-PasswordHash([string]$mtPath) {
    if ($PasswordHash) {
        return $PasswordHash
    }

    $plain = $Password
    if (-not $plain) {
        $secure = Read-Host "Password for new MidTerm instances" -AsSecureString
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try {
            $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
        } finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
        }
    }

    if (-not $plain) {
        throw "A password or -PasswordHash is required."
    }

    $hash = $plain | & $mtPath --hash-password
    if ($LASTEXITCODE -ne 0 -or -not ($hash -match '^\$PBKDF2\$')) {
        throw "Could not hash password with $mtPath."
    }
    return $hash.Trim()
}

function Copy-Payload([string]$payloadDir, [string]$installDir) {
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Copy-Item -Path (Join-Path $payloadDir "*") -Destination $installDir -Recurse -Force
}

function Write-InstanceManifest($layout) {
    $manifest = [ordered]@{
        name = $layout.Name
        port = $layout.Port
        bindAddress = $BindAddress
        serviceName = $layout.ServiceName
        installDir = $layout.InstallDir
        settingsDir = $layout.SettingsDir
        updatedAt = (Get-Date).ToUniversalTime().ToString("o")
    }
    New-Item -ItemType Directory -Force -Path $layout.SettingsDir | Out-Null
    $manifest | ConvertTo-Json -Depth 3 | Set-Content -Path $layout.ManifestPath -Encoding UTF8
}

function Install-Instance($layout, [string]$payloadDir, [string]$passwordHash) {
    if ((Get-Service -Name $layout.ServiceName -ErrorAction SilentlyContinue) -and -not $Force) {
        throw "Service '$($layout.ServiceName)' already exists. Use -Force to replace it."
    }

    if (Get-Service -Name $layout.ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $layout.ServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $layout.ServiceName | Out-Null
        Start-Sleep -Seconds 1
    }

    Copy-Payload $payloadDir $layout.InstallDir
    New-Item -ItemType Directory -Force -Path $layout.SettingsDir | Out-Null

    $mtPath = Join-Path $layout.InstallDir "mt.exe"
    $passwordHash | & $mtPath --write-secret password_hash --settings-dir $layout.SettingsDir --service-mode | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not write password secret for '$($layout.Name)'."
    }

    $settings = [ordered]@{
        authenticationEnabled = $true
        isServiceInstall = $true
    }
    $settings | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $layout.SettingsDir "settings.json") -Encoding UTF8
    Write-InstanceManifest $layout

    $args = @(
        "--port", $layout.Port,
        "--bind", $BindAddress,
        "--settings-dir", "`"$($layout.SettingsDir)`"",
        "--service-mode",
        "--service-name", "`"$($layout.ServiceName)`""
    )
    $binPath = "`"$mtPath`" $($args -join ' ')"
    sc.exe create $layout.ServiceName binPath= $binPath start= auto DisplayName= "MidTerm ($($layout.Name))" | Out-Null
    sc.exe description $layout.ServiceName "MidTerm isolated instance '$($layout.Name)' on port $($layout.Port)" | Out-Null
    sc.exe failure $layout.ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
    Start-Service -Name $layout.ServiceName
}

function Update-Instance($layout, [string]$payloadDir) {
    if (-not (Get-Service -Name $layout.ServiceName -ErrorAction SilentlyContinue)) {
        throw "Service '$($layout.ServiceName)' does not exist."
    }

    Stop-Service -Name $layout.ServiceName -Force -ErrorAction SilentlyContinue
    Copy-Payload $payloadDir $layout.InstallDir
    Write-InstanceManifest $layout
    Start-Service -Name $layout.ServiceName
}

function Remove-Instance($layout) {
    if (Get-Service -Name $layout.ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $layout.ServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $layout.ServiceName | Out-Null
    }

    if ($Force) {
        Remove-Item -LiteralPath $layout.InstallDir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $layout.SettingsDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Assert-Windows
$instanceNames = Resolve-InstanceNames
$resolvedPorts = Resolve-Ports $instanceNames
$layouts = for ($i = 0; $i -lt $instanceNames.Count; $i++) {
    Get-InstanceLayout $instanceNames[$i] $resolvedPorts[$i]
}

if ($Mode -eq "plan") {
    $layouts | Format-Table Name, Port, ServiceName, InstallDir, SettingsDir
    return
}

if ($Mode -eq "list") {
    Get-ChildItem -Path $RootDir -Filter instance.json -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json } |
        Format-Table name, port, serviceName, installDir, settingsDir
    return
}

Assert-Admin

if ($Mode -eq "remove") {
    foreach ($layout in $layouts) {
        Remove-Instance $layout
    }
    return
}

$downloadDir = Join-Path ([IO.Path]::GetTempPath()) "midterm-multi-download"
$asset = Download-ReleaseAsset $downloadDir
$payloadDir = Expand-Asset $asset
try {
    if ($Mode -eq "install") {
        $firstMt = Join-Path $payloadDir "mt.exe"
        $hash = Resolve-PasswordHash $firstMt
        foreach ($layout in $layouts) {
            Install-Instance $layout $payloadDir $hash
            Write-Host "Installed $($layout.Name): https://localhost:$($layout.Port)"
        }
    } elseif ($Mode -eq "update") {
        foreach ($layout in $layouts) {
            Update-Instance $layout $payloadDir
            Write-Host "Updated $($layout.Name): https://localhost:$($layout.Port)"
        }
    } elseif ($Mode -eq "update-all") {
        $manifests = Get-ChildItem -Path $RootDir -Filter instance.json -Recurse -ErrorAction SilentlyContinue
        foreach ($manifestFile in $manifests) {
            $manifest = Get-Content $manifestFile.FullName -Raw | ConvertFrom-Json
            $layout = Get-InstanceLayout $manifest.name ([int]$manifest.port)
            Update-Instance $layout $payloadDir
            Write-Host "Updated $($layout.Name): https://localhost:$($layout.Port)"
        }
    }
} finally {
    Remove-Item -LiteralPath $payloadDir -Recurse -Force -ErrorAction SilentlyContinue
}
