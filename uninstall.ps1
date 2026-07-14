# MidTerm Windows Uninstaller
# Usage: irm https://get.tlbx.ai/uninstall.ps1 | iex

param(
    [switch]$Elevated,
    [string]$OriginalUserProfile,
    [string]$OriginalLocalAppData,
    [string]$OriginalTempRoot
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Invoke-CompatibleWebRequest
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,
        [Parameter(Mandatory = $true)]
        [string]$OutFile
    )

    $params = @{
        Uri = $Uri
        OutFile = $OutFile
    }

    if ($PSVersionTable.PSVersion.Major -lt 6)
    {
        $params.UseBasicParsing = $true
    }

    return Invoke-WebRequest @params
}

$ServiceName = "MidTerm"
$OldHostServiceName = "MidTermHost"
$FirewallRuleName = "MidTerm HTTPS"
$CertificateSubject = "CN=ai.tlbx.midterm"

$WIN_SERVICE_SETTINGS_DIR = "$env:ProgramData\MidTerm"
$WIN_SERVICE_INSTALL_DIR = "$env:ProgramFiles\MidTerm"

function Write-Banner
{
    Write-Host ""
    Write-Host "            //   \\" -ForegroundColor White
    Write-Host "           //     \\         __  __ _     _ _____" -ForegroundColor White
    Write-Host "          //       \\       |  \/  (_) __| |_   _|__ _ __ _ __ ___" -ForegroundColor White
    Write-Host "         //  ( " -NoNewline -ForegroundColor White
    Write-Host "·" -NoNewline -ForegroundColor Cyan
    Write-Host " )  \\      | |\/| | |/ _` | | |/ _ \\ '__| '_ ` _ \\" -ForegroundColor White
    Write-Host "        //           \\     | |  | | | (_| | | |  __/ |  | | | | | |" -ForegroundColor White
    Write-Host "       //             \\    |_|  |_|_|\__,_| |_|\___|_|  |_| |_| |_|" -ForegroundColor White
    Write-Host "      //               \\   " -NoNewline -ForegroundColor White
    Write-Host "by J. Schmidt - https://github.com/tlbx-ai/MidTerm" -ForegroundColor Green
    Write-Host ""
}

function Write-Header
{
    Write-Banner
    Write-Host "  Uninstaller" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Step
{
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor Gray
}

function Write-WarnLine
{
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor Yellow
}

function Test-Administrator
{
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]$identity
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-WindowsPowerShellPath
{
    $systemRoot = $env:SystemRoot
    if ([string]::IsNullOrWhiteSpace($systemRoot))
    {
        $systemRoot = $env:windir
    }

    if (-not [string]::IsNullOrWhiteSpace($systemRoot))
    {
        $candidate = Join-Path $systemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
        if (Test-Path $candidate)
        {
            return $candidate
        }
    }

    $command = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if ($command -and $command.Source)
    {
        return $command.Source
    }

    return "powershell.exe"
}

function Resolve-OriginalContext
{
    if (-not $OriginalUserProfile)
    {
        $script:OriginalUserProfile = $env:USERPROFILE
    }

    if (-not $OriginalLocalAppData)
    {
        $script:OriginalLocalAppData = Join-Path $script:OriginalUserProfile "AppData\Local"
    }

    if (-not $OriginalTempRoot)
    {
        $script:OriginalTempRoot = $env:TEMP
    }
}

function Remove-PathIfExists
{
    param([string]$Path)

    if (-not $Path) { return }
    if (-not (Test-Path $Path)) { return }

    try
    {
        Remove-Item -Path $Path -Recurse -Force -ErrorAction Stop
        Write-Step "Removed: $Path"
    }
    catch
    {
        Write-WarnLine "Could not remove: $Path"
    }
}

function Remove-FileIfExists
{
    param([string]$Path)

    if (-not $Path) { return }
    if (-not (Test-Path $Path)) { return }

    try
    {
        Remove-Item -Path $Path -Force -ErrorAction Stop
        Write-Step "Removed: $Path"
    }
    catch
    {
        Write-WarnLine "Could not remove: $Path"
    }
}

function Remove-GlobMatches
{
    param(
        [string]$BasePath,
        [string]$Filter
    )

    if (-not $BasePath -or -not (Test-Path $BasePath)) { return }

    Get-ChildItem -Path $BasePath -Filter $Filter -Force -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.PSIsContainer)
        {
            Remove-PathIfExists -Path $_.FullName
        }
        else
        {
            Remove-FileIfExists -Path $_.FullName
        }
    }
}

function Test-GlobExists
{
    param(
        [string]$BasePath,
        [string]$Filter
    )

    if (-not $BasePath -or -not (Test-Path $BasePath)) { return $false }
    return [bool](Get-ChildItem -Path $BasePath -Filter $Filter -Force -ErrorAction SilentlyContinue | Select-Object -First 1)
}

function Stop-MidTermProcesses
{
    param([string[]]$KnownPaths)

    $normalized = @($KnownPaths | Where-Object { $_ } | ForEach-Object {
        try { [System.IO.Path]::GetFullPath($_).ToLowerInvariant() } catch { $null }
    } | Where-Object { $_ } | Select-Object -Unique)

    if ($normalized.Count -eq 0) { return }

    try
    {
        $processes = Get-CimInstance Win32_Process -Filter "Name='mt.exe' OR Name='mthost.exe' OR Name='mt-host.exe'" -ErrorAction SilentlyContinue
        foreach ($proc in $processes)
        {
            $exePath = $proc.ExecutablePath
            if (-not $exePath) { continue }

            $normalizedExe = try { [System.IO.Path]::GetFullPath($exePath).ToLowerInvariant() } catch { $null }
            if (-not $normalizedExe) { continue }

            if ($normalized -contains $normalizedExe)
            {
                Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch
    {
        Write-WarnLine "Could not stop one or more running MidTerm processes."
    }
}

function Remove-MidTermCertificates
{
    try
    {
        $rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
        $rootStore.Open("ReadWrite")
        $midTermCerts = @($rootStore.Certificates | Where-Object { $_.Subject -eq $CertificateSubject })
        foreach ($cert in $midTermCerts)
        {
            try
            {
                $rootStore.Remove($cert)
            }
            catch
            {
                Write-WarnLine "Could not remove trusted certificate $($cert.Thumbprint)."
            }
        }
        $rootStore.Close()

        if ($midTermCerts.Count -gt 0)
        {
            Write-Step "Removed $($midTermCerts.Count) trusted MidTerm certificate(s)."
        }
    }
    catch
    {
        Write-WarnLine "Could not clean up trusted MidTerm certificates."
    }
}

function Remove-UserPathEntry
{
    param([string]$PathToRemove)

    try
    {
        $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
        if (-not $userPath) { return }

        $newPath = ($userPath -split ";" | Where-Object { $_ -and $_ -ne $PathToRemove }) -join ";"
        [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
        Write-Step "Updated user PATH."
    }
    catch
    {
        Write-WarnLine "Could not update the user PATH."
    }
}

function Remove-MidTermTempArtifacts
{
    param([string[]]$Roots)

    foreach ($root in ($Roots | Where-Object { $_ } | Select-Object -Unique))
    {
        Remove-PathIfExists -Path (Join-Path $root "midterm-bin")
        Remove-PathIfExists -Path (Join-Path $root "mt-drops")
        Remove-PathIfExists -Path (Join-Path $root "mm-drops")
        Remove-PathIfExists -Path (Join-Path $root "MidTerm-Install")

        Remove-GlobMatches -BasePath $root -Filter "mt-update-*"
        Remove-GlobMatches -BasePath $root -Filter "mm-update-*"
        Remove-GlobMatches -BasePath $root -Filter "mt-browser-*.bin"
        Remove-GlobMatches -BasePath $root -Filter "mt-tmux-*.bin"

        Remove-FileIfExists -Path (Join-Path $root "mt-install-elevated.ps1")
        Remove-FileIfExists -Path (Join-Path $root "mt-install-log.txt")
    }
}

function Test-UserTraces
{
    param(
        [string]$UserInstallDir,
        [string]$UserSettingsDir,
        [string]$TempRoot
    )

    return (
        (Test-Path $UserInstallDir) -or
        (Test-Path $UserSettingsDir) -or
        (Test-Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MidTerm") -or
        (Test-Path (Join-Path $TempRoot "midterm-bin")) -or
        (Test-Path (Join-Path $TempRoot "mt-drops")) -or
        (Test-Path (Join-Path $TempRoot "mm-drops")) -or
        (Test-Path (Join-Path $TempRoot "MidTerm-Install")) -or
        (Test-GlobExists -BasePath $TempRoot -Filter "mt-update-*") -or
        (Test-GlobExists -BasePath $TempRoot -Filter "mm-update-*") -or
        (Test-GlobExists -BasePath $TempRoot -Filter "mt-browser-*.bin") -or
        (Test-GlobExists -BasePath $TempRoot -Filter "mt-tmux-*.bin")
    )
}

function Test-ServiceTraces
{
    $hasCerts = $false
    $hasFirewallRule = $false

    try
    {
        $rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
        $rootStore.Open("ReadOnly")
        $hasCerts = [bool]($rootStore.Certificates | Where-Object { $_.Subject -eq $CertificateSubject } | Select-Object -First 1)
        $rootStore.Close()
    }
    catch
    {
        $hasCerts = $false
    }

    if (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue)
    {
        $hasFirewallRule = [bool](Get-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue)
    }

    return (
        (Test-Path $WIN_SERVICE_INSTALL_DIR) -or
        (Test-Path $WIN_SERVICE_SETTINGS_DIR) -or
        (Test-Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MidTerm") -or
        (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -or
        (Get-Service -Name $OldHostServiceName -ErrorAction SilentlyContinue) -or
        $hasFirewallRule -or
        $hasCerts
    )
}

function Invoke-UserCleanup
{
    param(
        [string]$UserInstallDir,
        [string]$UserSettingsDir,
        [string]$TempRoot
    )

    Write-Step "Cleaning user install..."

    Stop-MidTermProcesses -KnownPaths @(
        (Join-Path $UserInstallDir "mt.exe"),
        (Join-Path $UserInstallDir "mthost.exe"),
        (Join-Path $UserInstallDir "mt-host.exe")
    )

    Remove-UserPathEntry -PathToRemove $UserInstallDir
    Remove-Item -Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MidTerm" -Force -Recurse -ErrorAction SilentlyContinue
    Remove-PathIfExists -Path $UserInstallDir
    Remove-PathIfExists -Path $UserSettingsDir
    Remove-MidTermTempArtifacts -Roots @($TempRoot, [System.IO.Path]::GetTempPath())
}

function Invoke-ServiceCleanup
{
    Write-Step "Cleaning system install..."

    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null 2>$null
    Stop-Service -Name $OldHostServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $OldHostServiceName | Out-Null 2>$null

    Stop-MidTermProcesses -KnownPaths @(
        (Join-Path $WIN_SERVICE_INSTALL_DIR "mt.exe"),
        (Join-Path $WIN_SERVICE_INSTALL_DIR "mthost.exe"),
        (Join-Path $WIN_SERVICE_INSTALL_DIR "mt-host.exe")
    )

    if ((Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue) -and
        (Get-Command Remove-NetFirewallRule -ErrorAction SilentlyContinue))
    {
        Get-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue | Out-Null
    }

    Remove-Item -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MidTerm" -Force -Recurse -ErrorAction SilentlyContinue
    Remove-MidTermCertificates
    Remove-PathIfExists -Path $WIN_SERVICE_SETTINGS_DIR
    Remove-PathIfExists -Path $WIN_SERVICE_INSTALL_DIR
}

Resolve-OriginalContext

$userInstallDir = Join-Path $OriginalLocalAppData "MidTerm"
$userSettingsDir = Join-Path $OriginalUserProfile ".midterm"

Write-Header
Write-Step "User profile: $OriginalUserProfile"
Write-Step "User install traces: $(Test-UserTraces -UserInstallDir $userInstallDir -UserSettingsDir $userSettingsDir -TempRoot $OriginalTempRoot)"
$serviceTraces = [bool](Test-ServiceTraces)
Write-Step "System install traces: $serviceTraces"

if (-not (Test-UserTraces -UserInstallDir $userInstallDir -UserSettingsDir $userSettingsDir -TempRoot $OriginalTempRoot) -and -not $serviceTraces)
{
    Write-Host "  No known MidTerm installation traces were found." -ForegroundColor Green
    return
}

Invoke-UserCleanup -UserInstallDir $userInstallDir -UserSettingsDir $userSettingsDir -TempRoot $OriginalTempRoot

if ($serviceTraces)
{
    if (-not (Test-Administrator))
    {
        Write-Host ""
        Write-Host "  Requesting administrator privileges to remove the MidTerm service, trusted certs, firewall rule, and system files..." -ForegroundColor Yellow

        $scriptUrl = "https://get.tlbx.ai/uninstall.ps1"
        $tempScript = Join-Path $env:TEMP "mt-uninstall-elevated.ps1"
        Invoke-CompatibleWebRequest -Uri $scriptUrl -OutFile $tempScript

        $psExe = Get-WindowsPowerShellPath
        $baseArguments = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $tempScript,
            "-Elevated",
            "-OriginalUserProfile", $OriginalUserProfile,
            "-OriginalLocalAppData", $OriginalLocalAppData,
            "-OriginalTempRoot", $OriginalTempRoot
        )

        $proc = Start-Process $psExe -ArgumentList $baseArguments -Verb RunAs -PassThru
        $proc.WaitForExit()
        if ($proc.ExitCode -ne 0)
        {
            exit $proc.ExitCode
        }
        return
    }

    Invoke-ServiceCleanup
}

Write-Host ""
Write-Host "  MidTerm uninstall complete." -ForegroundColor Green
