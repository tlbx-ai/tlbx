# tlbx Windows Installer (formerly MidTerm)
# Usage: irm https://get.tlbx.ai/install.ps1 | iex
# Dev:   & ([scriptblock]::Create((irm https://get.tlbx.ai/install.ps1))) -Dev
#
# Design goals:
# - install only official MidTerm release artifacts into known locations
# - collect interactive choices before elevation so the elevated leg can be replayed
# - preserve existing auth/settings unless the user explicitly replaces them
# - keep service-mode and user-mode installs mutually exclusive for predictable repair

param(
    [string]$RunAsUser,
    [string]$RunAsUserSid,
    [string]$PasswordHash,
    [int]$Port = 2000,
    [string]$BindAddress = "",
    [switch]$ServiceMode,
    [switch]$ConfigureFirewall,
    [switch]$TrustCert,
    [string]$LogFile,
    [string]$ReplayFile,
    [switch]$Dev
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$script:InstallerScriptPath = $PSCommandPath
$script:InstallerScriptDefinition = $MyInvocation.MyCommand.Definition

# Ensure TLS 1.2 for GitHub API/downloads (PS 5.1 defaults to TLS 1.0)
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Invoke-CompatibleRestMethod
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,
        [hashtable]$Headers,
        [int]$TimeoutSec,
        [switch]$SkipCertificateCheck
    )

    $params = @{ Uri = $Uri }
    if ($Headers) { $params.Headers = $Headers }
    if ($PSBoundParameters.ContainsKey("TimeoutSec")) { $params.TimeoutSec = $TimeoutSec }

    if ($PSVersionTable.PSVersion.Major -lt 6)
    {
        $params.UseBasicParsing = $true
    }
    elseif ($SkipCertificateCheck)
    {
        $params.SkipCertificateCheck = $true
    }

    return Invoke-RestMethod @params
}

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

function Unblock-DownloadedPath
{
    param(
        [string]$Path
    )

    if (-not (Test-Path $Path))
    {
        return
    }

    try
    {
        Get-Item -LiteralPath $Path -Force -ErrorAction Stop | Unblock-File -ErrorAction Stop
        Write-Log "Unblocked downloaded path: $Path"
    }
    catch
    {
        Write-Log "Could not unblock downloaded path '$Path': $_" "WARN"
    }
}

function Unblock-DownloadedTree
{
    param(
        [string]$Path
    )

    if (-not (Test-Path $Path))
    {
        return
    }

    try
    {
        Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction Stop |
            Unblock-File -ErrorAction Stop
        Write-Log "Unblocked downloaded tree: $Path"
    }
    catch
    {
        Write-Log "Could not unblock downloaded tree '$Path': $_" "WARN"
    }
}

# Logging
$script:UpdateLogFile = $null
$script:LogInitialized = $false

function Initialize-Log
{
    param(
        [string]$Mode  # "service" or "user"
    )

    if ($Mode -eq "service")
    {
        $logDir = "$env:ProgramData\MidTerm"
    }
    else
    {
        $logDir = "$env:USERPROFILE\.MidTerm"
    }

    if (-not (Test-Path $logDir))
    {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }

    $script:UpdateLogFile = Join-Path $logDir "update.log"

    # Clear previous log
    "" | Set-Content $script:UpdateLogFile -Force -ErrorAction SilentlyContinue

    $script:LogInitialized = $true

    $channelLabel = if ($Dev) { "dev" } else { "stable" }
    Write-Log "=========================================="
    Write-Log "MidTerm Install Script Starting"
    Write-Log "Mode: $Mode"
    Write-Log "Channel: $channelLabel"
    Write-Log "Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    Write-Log "Platform: Windows $([Environment]::OSVersion.Version)"
    Write-Log "User: $env:USERNAME"
    Write-Log "=========================================="
}

function Write-Log
{
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )

    if ($script:LogInitialized -and $script:UpdateLogFile)
    {
        $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
        $line = "[$timestamp] [$Level] $Message"
        Add-Content -Path $script:UpdateLogFile -Value $line -ErrorAction SilentlyContinue
    }
}

$ServiceName = "MidTerm"
$OldHostServiceName = "MidTermHost"
$DisplayName = "MidTerm"
$Publisher = "tlbx-ai"
$RepoOwner = "tlbx-ai"
$RepoName = "MidTerm"
$WebBinaryName = "mt.exe"
$TtyHostBinaryName = "mthost.exe"
$AgentHostBinaryName = "mtagenthost.exe"
$LegacyHostBinaryName = "mt-host.exe"
$AssetPattern = "mt-win-x64.zip"
# Certificate subject CN - must match CertificateGenerator.CertificateSubject in C#
$CertificateSubject = "CN=ai.tlbx.midterm"

try
{
    $repositoryCoordinate = (Invoke-CompatibleRestMethod -Uri "https://get.tlbx.ai/v1/repository" -TimeoutSec 3).Trim()
    if ($repositoryCoordinate -in @("tlbx-ai/MidTerm", "tlbx-ai/tlbx"))
    {
        $RepoOwner, $RepoName = $repositoryCoordinate.Split("/", 2)
    }
}
catch
{
    # Migration discovery is optional. Existing installs remain valid through the legacy coordinate.
}

# ============================================================================
# PATH CONSTANTS - SYNC: These paths MUST match:
#   - SettingsService.cs (GetSettingsPath method)
#   - LogPaths.cs (constants and GetSettingsDirectory method)
#   - UpdateScriptGenerator.cs (SettingsDir variable in generated scripts)
#   - install.sh (PATH_CONSTANTS section)
# ============================================================================
# Windows service mode: %ProgramData%\MidTerm (typically C:\ProgramData\MidTerm)
$WIN_SERVICE_SETTINGS_DIR = "$env:ProgramData\MidTerm"
$WIN_SERVICE_INSTALL_DIR = "$env:ProgramFiles\MidTerm"
# Windows user mode: %LOCALAPPDATA%\MidTerm and %USERPROFILE%\.midterm
$WIN_USER_INSTALL_DIR = "$env:LOCALAPPDATA\MidTerm"
$WIN_USER_SETTINGS_DIR = "$env:USERPROFILE\.midterm"
# Secrets file (secrets.bin on Windows, secrets.json on Unix)
$WIN_SECRETS_FILENAME = "secrets.bin"
# ============================================================================

$script:StatusLabelWidth = 12

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

function Write-Section
{
    param([string]$Title)

    $prefix = "  -- $Title "
    $padLength = [Math]::Max(2, 34 - $prefix.Length)
    Write-Host ""
    Write-Host ($prefix + ("-" * $padLength)) -ForegroundColor Cyan
}

function Write-StatusLine
{
    param(
        [string]$Label,
        [string]$Value,
        [ConsoleColor]$Color = [ConsoleColor]::Gray
    )

    $padded = $Label.PadRight($script:StatusLabelWidth)
    Write-Host ("  {0} : " -f $padded) -NoNewline
    Write-Host $Value -ForegroundColor $Color
}

function Write-Header
{
    Write-Banner
    Write-Host "  Installer" -ForegroundColor Cyan
    Write-Host ""
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

function Get-CurrentUserInfo
{
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $userName = $identity.Name.Split('\')[-1]
    $userSid = $identity.User.Value
    return @{
        Name = $userName
        Sid = $userSid
    }
}

function Get-CurrentInstallerScriptContent
{
    if ($script:InstallerScriptPath -and (Test-Path $script:InstallerScriptPath))
    {
        return Get-Content -Path $script:InstallerScriptPath -Raw
    }

    if (-not [string]::IsNullOrWhiteSpace($script:InstallerScriptDefinition) -and
        $script:InstallerScriptDefinition.Contains("# MidTerm Windows Installer"))
    {
        return $script:InstallerScriptDefinition
    }

    $branch = if ($Dev) { "dev" } else { "main" }
    $scriptUrl = "https://get.tlbx.ai/install.ps1"
    return Invoke-CompatibleRestMethod -Uri $scriptUrl -Headers @{ "User-Agent" = "MidTerm-Installer" }
}

function New-ElevationHandoffDirectory
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$UserSid
    )

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("MidTerm-Install-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $root -Force | Out-Null

    $grants = @()
    $grants += "*$UserSid`:(OI)(CI)F"
    $grants += "*S-1-5-32-544:(OI)(CI)F"
    $grants += "*S-1-5-18:(OI)(CI)F"

    $output = & icacls.exe $root /inheritance:r 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
        throw "Could not disable inherited ACLs on elevated installer handoff directory: $output"
    }

    foreach ($grant in $grants)
    {
        $output = & icacls.exe $root /grant:r $grant 2>&1
        if ($LASTEXITCODE -ne 0)
        {
            Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
            throw "Could not grant elevated installer handoff directory ACL '$grant': $output"
        }
    }

    return $root
}

function Join-ProcessArguments
{
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $quoted = foreach ($argument in $Arguments)
    {
        if ($null -eq $argument)
        {
            '""'
        }
        elseif ($argument -notmatch '[\s"]')
        {
            $argument
        }
        else
        {
            '"' + ($argument -replace '"', '\"') + '"'
        }
    }

    return ($quoted -join " ")
}

function Import-ElevatedReplayFile
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $Path))
    {
        throw "Elevated installer replay file not found: $Path"
    }

    $replay = Get-Content -Path $Path -Raw | ConvertFrom-Json
    $script:RunAsUser = [string]$replay.runAsUser
    $script:RunAsUserSid = [string]$replay.runAsUserSid
    $script:PasswordHash = if ($null -ne $replay.passwordHash) { [string]$replay.passwordHash } else { $null }
    $script:Port = [int]$replay.port
    $script:BindAddress = [string]$replay.bindAddress
    $script:ConfigureFirewall = [bool]$replay.configureFirewall
    $script:TrustCert = [bool]$replay.trustCert
    $script:Dev = [bool]$replay.dev
}

function Test-ExistingPassword
{
    # Check if password exists in secure storage (secrets.bin)
    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    $secretsPath = "$WIN_SERVICE_SETTINGS_DIR\$WIN_SECRETS_FILENAME"
    if (Test-Path $secretsPath)
    {
        try
        {
            $secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json
            if ($secrets.password_hash -and $secrets.password_hash.Length -gt 10)
            {
                return $true
            }
        }
        catch { }
    }

    # Legacy: check settings.json (old broken path - will be migrated)
    $settingsPath = "$WIN_SERVICE_SETTINGS_DIR\settings.json"
    if (Test-Path $settingsPath)
    {
        try
        {
            $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
            if ($settings.passwordHash -and $settings.passwordHash.Length -gt 10)
            {
                return $true
            }
        }
        catch { }
    }
    return $false
}

function Test-ExistingServiceInstall
{
    return (
        (Test-Path $WIN_SERVICE_INSTALL_DIR) -or
        (Test-Path $WIN_SERVICE_SETTINGS_DIR) -or
        (Test-Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MidTerm") -or
        (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -or
        (Get-Service -Name $OldHostServiceName -ErrorAction SilentlyContinue)
    )
}

function Test-ExistingUserInstall
{
    return (
        (Test-Path $WIN_USER_INSTALL_DIR) -or
        (Test-Path $WIN_USER_SETTINGS_DIR) -or
        (Test-Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MidTerm")
    )
}

function Assert-NoCrossModeConflict
{
    param([bool]$AsService)

    if ($AsService -and (Test-ExistingUserInstall))
    {
        Write-Host ""
        Write-Host "  Cannot install as a system service while a user install still exists." -ForegroundColor Red
        Write-Host "  Uninstall the user-mode copy first, then rerun the installer." -ForegroundColor Gray
        Write-Host "  User traces: $WIN_USER_INSTALL_DIR or $WIN_USER_SETTINGS_DIR" -ForegroundColor Gray
        exit 1
    }

    if (-not $AsService -and (Test-ExistingServiceInstall))
    {
        Write-Host ""
        Write-Host "  Cannot install in user mode while a system service install still exists." -ForegroundColor Red
        Write-Host "  Uninstall the service-mode copy first, then rerun the installer." -ForegroundColor Gray
        Write-Host "  Service traces: $WIN_SERVICE_INSTALL_DIR or $WIN_SERVICE_SETTINGS_DIR" -ForegroundColor Gray
        exit 1
    }
}

function Prompt-Password
{
    param(
        [string]$InstallDir
    )

    Write-Host ""
    Write-Host "  Security Notice:" -ForegroundColor Yellow
    Write-Host "  MidTerm exposes terminal access over the network." -ForegroundColor Gray
    Write-Host "  A password is required to prevent unauthorized access." -ForegroundColor Gray
    Write-Host ""

    $maxAttempts = 3
    for ($i = 0; $i -lt $maxAttempts; $i++)
    {
        $password = Read-Host "  Enter password" -AsSecureString
        $confirm = Read-Host "  Confirm password" -AsSecureString

        $pwPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($password))
        $confirmPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($confirm))

        if ($pwPlain -ne $confirmPlain)
        {
            Write-Host "  Passwords do not match. Try again." -ForegroundColor Red
            continue
        }

        if ($pwPlain.Length -lt 4)
        {
            Write-Host "  Password must be at least 4 characters." -ForegroundColor Red
            continue
        }

        # Hash the password using mt.exe --hash-password (password piped via stdin)
        $mmPath = Join-Path $InstallDir "mt.exe"
        if (Test-Path $mmPath)
        {
            try
            {
                $hash = $pwPlain | & $mmPath --hash-password 2>&1
                if ($hash -match '^\$PBKDF2\$')
                {
                    return $hash
                }
            }
            catch { }
        }

        # Fallback: Return plaintext marker (will be hashed on first run)
        Write-Host "  Warning: Could not hash password, will be set on first access." -ForegroundColor Yellow
        return "__PENDING__:$pwPlain"
    }

    Write-Host "  Too many failed attempts. Exiting." -ForegroundColor Red
    exit 1
}

function Prompt-ExistingPasswordAction
{
    Write-Host ""
    Write-Host "  Password:" -ForegroundColor Cyan
    Write-Host "  Existing password found in secure storage." -ForegroundColor Green
    Write-Host ""
    Write-Host "  [1] Keep existing password (default)" -ForegroundColor Cyan
    Write-Host "      - No password change" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  [2] Set a new password now" -ForegroundColor Cyan
    Write-Host "      - Replaces the existing password" -ForegroundColor Gray
    Write-Host ""

    $maxAttempts = 3
    for ($i = 0; $i -lt $maxAttempts; $i++)
    {
        $choice = Read-Host "  Your choice [1/2]"

        if ([string]::IsNullOrWhiteSpace($choice) -or $choice -eq "1")
        {
            return "Preserve"
        }

        if ($choice -eq "2")
        {
            return "Replace"
        }

        Write-Host "  Error: Please enter 1 or 2." -ForegroundColor Red
        if ($i -lt $maxAttempts - 1)
        {
            Write-Host "  Please try again." -ForegroundColor Yellow
        }
        else
        {
            Write-Host "  Using default: keep existing password." -ForegroundColor Yellow
        }
    }

    return "Preserve"
}

function Test-ExistingCertificate
{
    param(
        [string]$SettingsDir
    )

    $certPath = Join-Path $SettingsDir "midterm.pem"
    $keyPath = Join-Path (Join-Path $SettingsDir "keys") "midterm.dpapi"

    # Check if both cert and key exist
    if (-not (Test-Path $certPath))
    {
        return $null
    }

    if (-not (Test-Path $keyPath))
    {
        Write-Host "  Warning: Certificate exists but private key is missing" -ForegroundColor Yellow
        return $null
    }

    try
    {
        # Load and validate the certificate
        $certContent = Get-Content $certPath -Raw
        $base64 = $certContent -replace "-----BEGIN CERTIFICATE-----", "" -replace "-----END CERTIFICATE-----", "" -replace "`n", "" -replace "`r", ""
        $certBytes = [Convert]::FromBase64String($base64)
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(,$certBytes)

        # Check if cert is still valid (not expired, and has at least 30 days left)
        $now = Get-Date
        if ($cert.NotAfter -lt $now)
        {
            Write-Host "  Warning: Existing certificate has expired" -ForegroundColor Yellow
            return $null
        }

        if ($cert.NotAfter -lt $now.AddDays(30))
        {
            Write-Host "  Warning: Existing certificate expires in less than 30 days" -ForegroundColor Yellow
            return $null
        }

        return @{
            Path = $certPath
            Certificate = $cert
            Thumbprint = $cert.Thumbprint
            NotAfter = $cert.NotAfter
        }
    }
    catch
    {
        Write-Host "  Warning: Could not validate existing certificate: $_" -ForegroundColor Yellow
        return $null
    }
}

function Remove-OldMidTermCertificates
{
    param(
        [string]$ExceptThumbprint = $null
    )

    try
    {
        $rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
        $rootStore.Open("ReadWrite")

        $oldCerts = $rootStore.Certificates | Where-Object { $_.Subject -eq $CertificateSubject }
        $removed = 0

        foreach ($old in $oldCerts)
        {
            if ($ExceptThumbprint -and $old.Thumbprint -eq $ExceptThumbprint)
            {
                continue  # Keep the current cert
            }

            try
            {
                $rootStore.Remove($old)
                $removed++
                Write-Host "  Removed old certificate: $($old.Thumbprint.Substring(0, 8))..." -ForegroundColor Gray
            }
            catch
            {
                Write-Host "  Warning: Could not remove old certificate: $_" -ForegroundColor Yellow
            }
        }

        $rootStore.Close()

        if ($removed -gt 0)
        {
            Write-Host "  Cleaned up $removed old MidTerm certificate(s) from trusted store" -ForegroundColor Green
        }
    }
    catch
    {
        Write-Host "  Warning: Could not clean up old certificates: $_" -ForegroundColor Yellow
    }
}

function Show-CertificateFingerprint
{
    param(
        [string]$CertPath
    )

    if (-not $CertPath -or -not (Test-Path $CertPath))
    {
        return
    }

    try
    {
        # Load the PEM certificate
        $certContent = Get-Content $CertPath -Raw
        $base64 = $certContent -replace "-----BEGIN CERTIFICATE-----", "" -replace "-----END CERTIFICATE-----", "" -replace "`n", "" -replace "`r", ""
        $certBytes = [Convert]::FromBase64String($base64)

        # Compute SHA-256 fingerprint
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        $hash = $sha256.ComputeHash($certBytes)
        $fingerprint = [BitConverter]::ToString($hash) -replace "-", ":"

        Write-Host ""
        Write-Host "  ================================================" -ForegroundColor Cyan
        Write-Host "  CERTIFICATE FINGERPRINT - SAVE THIS!" -ForegroundColor Cyan
        Write-Host "  ================================================" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "  $fingerprint" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  When connecting from other devices, verify the" -ForegroundColor Gray
        Write-Host "  fingerprint in your browser matches this one." -ForegroundColor Gray
        Write-Host "  (Click padlock icon > Certificate > SHA-256)" -ForegroundColor Gray
        Write-Host ""
        Write-Host "  Never enter passwords if fingerprints don't match." -ForegroundColor White
        Write-Host ""
    }
    catch
    {
        Write-Host "  Could not compute certificate fingerprint: $_" -ForegroundColor Yellow
    }
}

function Generate-Certificate
{
    param(
        [string]$InstallDir,
        [string]$SettingsDir,
        [bool]$IsService = $false,
        [bool]$TrustCert = $false
    )

    Write-Log "Generating certificate: InstallDir=$InstallDir, SettingsDir=$SettingsDir, IsService=$IsService"

    # First check if a valid certificate already exists
    $existingCert = Test-ExistingCertificate -SettingsDir $SettingsDir
    if ($existingCert)
    {
        Write-Log "Existing valid certificate found: $($existingCert.Path), expires $($existingCert.NotAfter)"
        Write-Host "  Existing valid certificate found (expires $($existingCert.NotAfter.ToString('yyyy-MM-dd')))" -ForegroundColor Green
        $certPath = $existingCert.Path
        $certThumbprint = $existingCert.Thumbprint
        $wasGenerated = $false
    }
    else
    {
        Write-Log "No valid certificate found, generating new one..."
        Write-Host "  Generating HTTPS certificate with OS-protected private key..." -ForegroundColor Gray

        $mtPath = Join-Path $InstallDir "mt.exe"
        if (-not (Test-Path $mtPath))
        {
            Write-Log "mt.exe not found at $mtPath" "ERROR"
            Write-Host "  Error: mt.exe not found at $mtPath" -ForegroundColor Red
            return $null
        }

        try
        {
            # Use mt.exe --generate-cert to generate certificate with DPAPI-protected key
            # Pass --service-mode for service installs so it uses ProgramData instead of user profile
            # Pass --force to regenerate since we already checked validity above
            $certArgs = if ($IsService) { @("--generate-cert", "--service-mode", "--force") } else { @("--generate-cert", "--force") }
            $output = & $mtPath @certArgs 2>&1
            $exitCode = $LASTEXITCODE

            if ($exitCode -ne 0)
            {
                Write-Host "  Failed to generate certificate: $output" -ForegroundColor Red
                return $null
            }

            # Parse output for certificate path
            $certPath = $null
            foreach ($line in $output)
            {
                if ($line -match "Location:\s*(.+\.pem)")
                {
                    $certPath = $Matches[1].Trim()
                }
            }

            if (-not $certPath)
            {
                # Default path (matches what mt.exe generates)
                $certPath = Join-Path $SettingsDir "midterm.pem"
            }

            Write-Host "  Certificate generated with DPAPI-protected private key" -ForegroundColor Green
            $wasGenerated = $true

            # Get the thumbprint of the new cert
            $certContent = Get-Content $certPath -Raw
            $base64 = $certContent -replace "-----BEGIN CERTIFICATE-----", "" -replace "-----END CERTIFICATE-----", "" -replace "`n", "" -replace "`r", ""
            $certBytes = [Convert]::FromBase64String($base64)
            $newCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(,$certBytes)
            $certThumbprint = $newCert.Thumbprint
        }
        catch
        {
            Write-Host "  Failed to generate certificate: $_" -ForegroundColor Red
            return $null
        }
    }

    # Trust the certificate if requested (decision made before elevation)
    if ($TrustCert)
    {
        # First, remove ALL old MidTerm certs from trusted store to avoid accumulation
        Remove-OldMidTermCertificates -ExceptThumbprint $null  # Remove all, we'll add the current one

        Write-Host "  Adding certificate to trusted root store..." -ForegroundColor Gray
        try
        {
            # Load the PEM certificate - extract base64 and create cert via constructor (not Import)
            $certContent = Get-Content $certPath -Raw
            $base64 = $certContent -replace "-----BEGIN CERTIFICATE-----", "" -replace "-----END CERTIFICATE-----", "" -replace "`n", "" -replace "`r", ""
            $certBytes = [Convert]::FromBase64String($base64)
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(,$certBytes)

            # Import to Trusted Root - requires admin
            $rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
            $rootStore.Open("ReadWrite")
            $rootStore.Add($cert)
            $rootStore.Close()
            Write-Host "  Certificate trusted successfully" -ForegroundColor Green
        }
        catch
        {
            Write-Host "  Could not trust certificate: $_" -ForegroundColor Yellow
            Write-Host "  You may see browser warnings until manually trusted" -ForegroundColor Gray
        }
    }

    return $certPath
}

function Prompt-NetworkConfig
{
    Write-Host ""
    Write-Host "  Network Configuration:" -ForegroundColor Cyan
    Write-Host ""

    # Port configuration with validation and retry
    $maxAttempts = 3
    $port = 2000
    for ($i = 0; $i -lt $maxAttempts; $i++)
    {
        $portInput = Read-Host "  Port number [2000]"
        if ([string]::IsNullOrWhiteSpace($portInput))
        {
            $port = 2000
            break
        }

        if ($portInput -match '^\d+$')
        {
            $portNum = [int]$portInput
            if ($portNum -ge 1 -and $portNum -le 65535)
            {
                $port = $portNum
                break
            }
            else
            {
                Write-Host "  Error: Port must be between 1 and 65535." -ForegroundColor Red
            }
        }
        else
        {
            Write-Host "  Error: Port must be a number." -ForegroundColor Red
        }

        if ($i -lt $maxAttempts - 1)
        {
            Write-Host "  Please try again." -ForegroundColor Yellow
        }
        else
        {
            Write-Host "  Using default port 2000." -ForegroundColor Yellow
            $port = 2000
        }
    }

    Write-Host ""
    Write-Host "  Network binding:" -ForegroundColor White
    Write-Host "  [1] Accept connections from anywhere (default)" -ForegroundColor Cyan
    Write-Host "      - Access from other devices on your network" -ForegroundColor Gray
    Write-Host "      - Required for remote access" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  [2] Localhost only" -ForegroundColor Cyan
    Write-Host "      - Only accessible from this computer" -ForegroundColor Gray
    Write-Host "      - More secure, no network exposure" -ForegroundColor Green
    Write-Host ""

    # Binding choice with validation and retry
    $bindAddress = "*"
    for ($i = 0; $i -lt $maxAttempts; $i++)
    {
        $bindChoice = Read-Host "  Your choice [1/2]"

        if ([string]::IsNullOrWhiteSpace($bindChoice) -or $bindChoice -eq "1")
        {
            $bindAddress = "*"
            Write-Host ""
            Write-Host "  Security Warning:" -ForegroundColor Yellow
            Write-Host "  MidTerm will accept connections from any device on your network." -ForegroundColor Yellow
            Write-Host "  Ensure your password is strong and consider firewall rules." -ForegroundColor Yellow
            break
        }
        elseif ($bindChoice -eq "2")
        {
            $bindAddress = "localhost"
            Write-Host "  Binding to localhost only" -ForegroundColor Gray
            break
        }
        else
        {
            Write-Host "  Error: Please enter 1 or 2." -ForegroundColor Red
            if ($i -lt $maxAttempts - 1)
            {
                Write-Host "  Please try again." -ForegroundColor Yellow
            }
            else
            {
                Write-Host "  Using default: accept connections from anywhere." -ForegroundColor Yellow
                $bindAddress = "*"
            }
        }
    }

    # Always HTTPS - certificate will be generated after binary install
    Write-Host ""
    Write-Host "  HTTPS: Enabled (self-signed certificate with OS-protected key)" -ForegroundColor Green

    return @{
        Port = $port
        BindAddress = $bindAddress
    }
}

function Get-LatestRelease
{
    param(
        [bool]$DevChannel = $false
    )

    if ($DevChannel)
    {
        Write-Host "Fetching latest dev release..." -ForegroundColor Gray
        $apiUrl = "https://api.github.com/repos/$RepoOwner/$RepoName/releases"
        $releases = Invoke-CompatibleRestMethod -Uri $apiUrl -Headers @{ "User-Agent" = "MidTerm-Installer" }

        # Find the first prerelease
        $release = $releases | Where-Object { $_.prerelease -eq $true } | Select-Object -First 1

        if (-not $release)
        {
            Write-Host "  No dev releases found, falling back to latest stable..." -ForegroundColor Yellow
            $apiUrl = "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest"
            $release = Invoke-CompatibleRestMethod -Uri $apiUrl -Headers @{ "User-Agent" = "MidTerm-Installer" }
        }

        return $release
    }
    else
    {
        Write-Host "Fetching latest release..." -ForegroundColor Gray
        $apiUrl = "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest"
        $release = Invoke-CompatibleRestMethod -Uri $apiUrl -Headers @{ "User-Agent" = "MidTerm-Installer" }
        return $release
    }
}

function Test-NetworkBinding
{
    param(
        [string]$BindAddress
    )

    return $BindAddress -ne "localhost" -and $BindAddress -ne "127.0.0.1" -and $BindAddress -ne "::1"
}

function Prompt-FirewallConfig
{
    param(
        [string]$BindAddress,
        [int]$Port
    )

    if (-not (Test-NetworkBinding -BindAddress $BindAddress))
    {
        return $false
    }

    Write-Host ""
    Write-Host "  Windows Firewall:" -ForegroundColor Cyan
    Write-Host "  Allow other PCs to reach MidTerm on TCP port $Port?" -ForegroundColor Yellow
    Write-Host "  (Creates or updates the inbound rule named 'MidTerm HTTPS')" -ForegroundColor Gray
    $choice = Read-Host "  Add firewall rule? [Y/n]"
    return ($choice -ne "n" -and $choice -ne "N")
}

function Ensure-FirewallRule
{
    param(
        [int]$Port,
        [string]$InstallDir
    )

    $displayName = "MidTerm HTTPS"  # Sync with WindowsFirewallService.ManagedRuleName
    $programPath = Join-Path $InstallDir $WebBinaryName

    try
    {
        Get-NetFirewallRule -DisplayName $displayName -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue

        New-NetFirewallRule `
            -DisplayName $displayName `
            -Group "MidTerm" `
            -Direction Inbound `
            -Action Allow `
            -Enabled True `
            -Profile Any `
            -Protocol TCP `
            -LocalPort $Port `
            -Program $programPath `
            -Description "Allows inbound HTTPS access to MidTerm." | Out-Null

        Write-Log "Windows firewall rule ensured for TCP port $Port"
        Write-Host "  Firewall: added rule '$displayName' for TCP port $Port" -ForegroundColor Gray
    }
    catch
    {
        Write-Log "Failed to configure Windows firewall rule: $_" "WARN"
        Write-Host "  Warning: Failed to configure Windows firewall rule: $_" -ForegroundColor Yellow
    }
}

function Get-AssetUrl
{
    param($Release)
    $asset = $Release.assets | Where-Object { $_.name -eq $AssetPattern }
    if (-not $asset)
    {
        throw "Could not find $AssetPattern in release assets"
    }
    return $asset.browser_download_url
}

function Write-ServiceSettings
{
    param(
        [string]$InstallDir,
        [string]$Username,
        [string]$UserSid,
        [string]$PasswordHash,
        [int]$Port = 2000,
        [string]$BindAddress = "*",
        [string]$CertPath = $null
    )

    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    $configDir = $WIN_SERVICE_SETTINGS_DIR
    $settingsPath = Join-Path $configDir "settings.json"
    $mergePath = Join-Path $configDir "merge-settings.json"

    if (-not (Test-Path $configDir))
    {
        New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    }

    # Build install-time settings for merge. These are installer-owned knobs
    # such as service identity and certificate location, not user preferences.
    $settings = @{
        runAsUser = $Username
        runAsUserSid = $UserSid
        authenticationEnabled = $true
        isServiceInstall = $true
    }

    if ($CertPath)
    {
        $settings.certificatePath = $CertPath
        $settings.keyProtection = "osProtected"
    }

    $json = $settings | ConvertTo-Json -Depth 10

    if (Test-Path $settingsPath)
    {
        # Reinstall: write merge file, let mt handle merging
        Set-Content -Path $mergePath -Value $json -Encoding UTF8
        Write-Host "  Settings: merge file written for mt" -ForegroundColor Gray
    }
    else
    {
        # Fresh install: write settings.json directly
        Set-Content -Path $settingsPath -Value $json -Encoding UTF8
        Write-Host "  Settings: $settingsPath" -ForegroundColor Gray
    }

    # Store password hash in secure storage (DPAPI-protected secrets.bin).
    # Use --service-mode so it lands in ProgramData instead of the invoking
    # admin profile. This is intentionally fatal if it fails.
    if ($PasswordHash)
    {
        $mtPath = Join-Path $InstallDir "mt.exe"
        $secretsPath = "$WIN_SERVICE_SETTINGS_DIR\$WIN_SECRETS_FILENAME"
        try
        {
            $PasswordHash | & $mtPath --write-secret password_hash --service-mode 2>&1 | Out-Null
            Write-Host "  Password: stored in $secretsPath" -ForegroundColor Gray
        }
        catch
        {
            throw "Failed to store password in secure storage at $secretsPath. Installation aborted to avoid an insecure state. $_"
        }
    }

    Write-Host "  Terminal user: $Username" -ForegroundColor Gray
    Write-Host "  Port: $Port" -ForegroundColor Gray
    Write-Host "  Binding: $(if ($BindAddress -eq 'localhost') { 'localhost only' } else { 'all interfaces' })" -ForegroundColor Gray
    if ($CertPath) { Write-Host "  Certificate: $CertPath" -ForegroundColor Gray }
}

function Install-MidTerm
{
    param(
        [bool]$AsService,
        [string]$Version,
        [string]$RunAsUser,
        [string]$RunAsUserSid,
        [string]$PasswordHash,
        [int]$Port = 2000,
        [string]$BindAddress = "*",
        [bool]$ConfigureFirewall = $false,
        [bool]$TrustCert = $false
    )

    # Initialize logging
    $mode = if ($AsService) { "service" } else { "user" }
    Initialize-Log -Mode $mode
    Write-Log "Starting installation: Version=$Version, AsService=$AsService, RunAsUser=$RunAsUser"

    if ($AsService)
    {
        # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
        $installDir = $WIN_SERVICE_INSTALL_DIR
        Write-Log "Install directory: $installDir"

        # Stop and remove old two-service architecture if present
        $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        $oldHostService = Get-Service -Name $OldHostServiceName -ErrorAction SilentlyContinue

        if ($existingService)
        {
            Write-Host "Stopping existing service..." -ForegroundColor Gray
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue

            # Kill any remaining processes
            Get-Process -Name "mt-host", "mthost", "mtagenthost", "mt" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

            # Wait for processes to fully exit (file handles released)
            $maxWait = 10
            $waited = 0
            while ($waited -lt $maxWait)
            {
                $procs = Get-Process -Name "mt-host", "mthost", "mtagenthost", "mt" -ErrorAction SilentlyContinue
                if (-not $procs) { break }
                Start-Sleep -Milliseconds 500
                $waited++
            }

            if ($waited -ge $maxWait)
            {
                Write-Host "  Warning: Some processes may still be running" -ForegroundColor Yellow
            }
        }

        # Migration: remove old MidTermHost service from v2.1.x
        if ($oldHostService)
        {
            Write-Host "Migrating from old two-service architecture..." -ForegroundColor Yellow
            Stop-Service -Name $OldHostServiceName -Force -ErrorAction SilentlyContinue
            Get-Process -Name "mt-host" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
            sc.exe delete $OldHostServiceName | Out-Null
        }
    }
    else
    {
        # Uses PATH_CONSTANTS defined above
        $installDir = $WIN_USER_INSTALL_DIR
    }

    # Create install directory
    if (-not (Test-Path $installDir))
    {
        New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    }

    # Download and extract
    $tempZip = Join-Path $env:TEMP "mt-download.zip"
    $tempExtract = Join-Path $env:TEMP "mt-extract"

    Write-Log "=== PHASE 1: Downloading binaries ==="
    Write-Host "Downloading..." -ForegroundColor Gray
    $assetUrl = Get-AssetUrl -Release $script:release
    Write-Log "Downloading from: $assetUrl"
    Invoke-CompatibleWebRequest -Uri $assetUrl -OutFile $tempZip
    Unblock-DownloadedPath -Path $tempZip
    Write-Log "Download complete"

    Write-Host "Extracting..." -ForegroundColor Gray
    Write-Log "Extracting to: $tempExtract"
    if (Test-Path $tempExtract) { Remove-Item $tempExtract -Recurse -Force }
    Expand-Archive -Path $tempZip -DestinationPath $tempExtract
    Unblock-DownloadedTree -Path $tempExtract
    Write-Log "Extraction complete"

    Write-Log "=== PHASE 2: Installing binaries ==="
    # Copy binaries
    $sourceWebBinary = Join-Path $tempExtract $WebBinaryName
    $sourceConHostBinary = Join-Path $tempExtract $TtyHostBinaryName
    $sourceAgentHostBinary = Join-Path $tempExtract $AgentHostBinaryName
    $destWebBinary = Join-Path $installDir $WebBinaryName
    $destConHostBinary = Join-Path $installDir $TtyHostBinaryName
    $destAgentHostBinary = Join-Path $installDir $AgentHostBinaryName

    Write-Host "Installing binaries to $installDir..." -ForegroundColor Gray
    Write-Log "Installing binaries to $installDir"

    if (-not (Test-Path $sourceWebBinary) -or -not (Test-Path $sourceConHostBinary) -or -not (Test-Path $sourceAgentHostBinary))
    {
        throw "Downloaded release archive is incomplete. Expected $WebBinaryName, $TtyHostBinaryName, and $AgentHostBinaryName."
    }

    # Retry logic for file copy (handles may take time to release)
    $maxRetries = 15
    $retryDelay = 500

    # Copy mt.exe with retry
    $copied = $false
    for ($i = 0; $i -lt $maxRetries; $i++)
    {
        try
        {
            Copy-Item $sourceWebBinary $destWebBinary -Force -ErrorAction Stop
            Write-Host "  Installed: $destWebBinary" -ForegroundColor Gray
            $copied = $true
            break
        }
        catch
        {
            if ($i -eq 0)
            {
                Write-Host "  Waiting for $WebBinaryName to be released..." -ForegroundColor Yellow
            }
            Start-Sleep -Milliseconds $retryDelay
        }
    }
    if (-not $copied)
    {
        Write-Host "  Failed to copy $WebBinaryName after $maxRetries attempts - file is locked" -ForegroundColor Red
        Write-Host "  Try manually stopping the MidTerm service or process" -ForegroundColor Red
        throw "Failed to install $WebBinaryName - file locked"
    }

    # Copy mthost.exe with retry
    if (Test-Path $sourceConHostBinary)
    {
        $copied = $false
        for ($i = 0; $i -lt $maxRetries; $i++)
        {
            try
            {
                Copy-Item $sourceConHostBinary $destConHostBinary -Force -ErrorAction Stop
                Write-Host "  Installed: $destConHostBinary" -ForegroundColor Gray
                $copied = $true
                break
            }
            catch
            {
                if ($i -eq 0)
                {
                    Write-Host "  Waiting for $TtyHostBinaryName to be released..." -ForegroundColor Yellow
                }
                Start-Sleep -Milliseconds $retryDelay
            }
        }
        if (-not $copied)
        {
            Write-Host "  Failed to copy $TtyHostBinaryName after $maxRetries attempts - file is locked" -ForegroundColor Red
            throw "Failed to install $TtyHostBinaryName - file locked"
        }
    }

    # Copy mtagenthost.exe with retry
    if (Test-Path $sourceAgentHostBinary)
    {
        $copied = $false
        for ($i = 0; $i -lt $maxRetries; $i++)
        {
            try
            {
                Copy-Item $sourceAgentHostBinary $destAgentHostBinary -Force -ErrorAction Stop
                Write-Host "  Installed: $destAgentHostBinary" -ForegroundColor Gray
                $copied = $true
                break
            }
            catch
            {
                if ($i -eq 0)
                {
                    Write-Host "  Waiting for $AgentHostBinaryName to be released..." -ForegroundColor Yellow
                }
                Start-Sleep -Milliseconds $retryDelay
            }
        }
        if (-not $copied)
        {
            Write-Host "  Failed to copy $AgentHostBinaryName after $maxRetries attempts - file is locked" -ForegroundColor Red
            throw "Failed to install $AgentHostBinaryName - file locked"
        }
    }

    # Remove legacy mt-host.exe if present from previous installs
    $legacyHostPath = Join-Path $installDir $LegacyHostBinaryName
    if (Test-Path $legacyHostPath)
    {
        Remove-Item $legacyHostPath -Force -ErrorAction SilentlyContinue
        Write-Host "  Removed legacy: $LegacyHostBinaryName" -ForegroundColor Gray
    }

    # Copy version manifest
    $sourceVersionJson = Join-Path $tempExtract "version.json"
    if (Test-Path $sourceVersionJson)
    {
        Copy-Item $sourceVersionJson (Join-Path $installDir "version.json") -Force
    }

    # Cleanup temp files
    Remove-Item $tempZip -Force -ErrorAction SilentlyContinue
    Remove-Item $tempExtract -Recurse -Force -ErrorAction SilentlyContinue

    Write-Log "=== PHASE 3: Password configuration ==="
    # Hash pending password now that mt.exe is installed
    if ($PasswordHash -and $PasswordHash.StartsWith("__PENDING__:"))
    {
        Write-Log "Hashing pending password..."
        $plainPassword = $PasswordHash.Substring(12)
        try
        {
            $hash = $plainPassword | & $destWebBinary --hash-password 2>&1
            if ($hash -match '^\$PBKDF2\$')
            {
                $PasswordHash = $hash
                Write-Log "Password hashed successfully"
                Write-Host "  Password: hashed" -ForegroundColor Gray
            }
            else
            {
                Write-Log "Password hashing failed, using fallback" "WARN"
                Write-Host "  Warning: Password hashing failed, using fallback" -ForegroundColor Yellow
            }
        }
        catch
        {
            Write-Log "Could not hash password: $_" "WARN"
            Write-Host "  Warning: Could not hash password: $_" -ForegroundColor Yellow
        }
    }
    elseif ($PasswordHash)
    {
        Write-Log "Using existing password hash"
    }
    else
    {
        Write-Log "No password hash provided (existing password will be preserved)"
    }

    Write-Log "=== PHASE 4: Certificate configuration ==="
    # Always generate certificate now that mt.exe is installed (always HTTPS)
    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    $settingsDir = if ($AsService) { $WIN_SERVICE_SETTINGS_DIR } else { $WIN_USER_SETTINGS_DIR }
    Write-Log "Settings directory: $settingsDir"
    $CertPath = Generate-Certificate -InstallDir $installDir -SettingsDir $settingsDir -IsService $AsService -TrustCert $TrustCert
    if (-not $CertPath)
    {
        Write-Host "  Warning: Certificate generation failed. App will use fallback certificate." -ForegroundColor Yellow
    }
    else
    {
        # Show fingerprint so user can verify connections from other devices
        Show-CertificateFingerprint -CertPath $CertPath
    }

    Write-Log "=== PHASE 5: Service/App installation ==="
    if ($AsService)
    {
        # Write settings with runAsUser info and password
        if ($RunAsUser -and $RunAsUserSid)
        {
            Write-Log "Writing service settings..."
            Write-ServiceSettings -InstallDir $installDir -Username $RunAsUser -UserSid $RunAsUserSid -PasswordHash $PasswordHash -Port $Port -BindAddress $BindAddress -CertPath $CertPath
        }

        Write-Log "Installing as Windows service..."
        Install-AsService -InstallDir $installDir -Version $Version -Port $Port -BindAddress $BindAddress

        if ($ConfigureFirewall -and (Test-NetworkBinding -BindAddress $BindAddress))
        {
            Ensure-FirewallRule -Port $Port -InstallDir $installDir
        }

        # Wait for mt.exe to spawn
        Start-Sleep -Seconds 2

        # Show final status
        Write-Section "Status"
        $serviceStatus = (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status
        $mmProc = Get-Process -Name "mt" -ErrorAction SilentlyContinue

        if ($serviceStatus -eq "Running") { Write-StatusLine "Service" "Running" Green }
        else { Write-StatusLine "Service" "$serviceStatus" Red }

        if ($mmProc) { Write-StatusLine "mt (web)" "Running (PID $($mmProc.Id))" Green }
        else { Write-StatusLine "mt (web)" "Starting..." Yellow }

        # Check health endpoint (HTTPS with self-signed cert requires SkipCertificateCheck)
        try
        {
            if ($PSVersionTable.PSVersion.Major -ge 6)
            {
                $health = Invoke-RestMethod -Uri "https://localhost:$Port/api/health" -TimeoutSec 5 -SkipCertificateCheck -ErrorAction Stop
            }
            else
            {
                # PS 5.1 workaround for self-signed certs
                try
                {
                    Add-Type @"
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public class TrustAllCerts {
    public static void Ignore() {
        ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
    }
    public static void Restore() {
        ServicePointManager.ServerCertificateValidationCallback = null;
    }
}
"@ -ErrorAction SilentlyContinue
                    [TrustAllCerts]::Ignore()
                    $health = Invoke-RestMethod -Uri "https://localhost:$Port/api/health" -TimeoutSec 5 -ErrorAction Stop
                    [TrustAllCerts]::Restore()
                }
                catch
                {
                    $health = $null
                }
            }

            if ($health)
            {
                if ($health.healthy) { Write-StatusLine "Health" "Healthy" Green }
                else { Write-StatusLine "Health" "Unhealthy" Red; if ($health.hostError) { Write-StatusLine "Error" "$($health.hostError)" Red } }
                Write-StatusLine "Version" "$($health.version)" Gray
            }
        }
        catch
        {
            Write-StatusLine "Health" "Could not connect to https://localhost:$Port" Yellow
        }
    }
    else
    {
        # Write user settings
        # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
        $userSettingsDir = $WIN_USER_SETTINGS_DIR
        $userSettingsPath = Join-Path $userSettingsDir "settings.json"
        $userMergePath = Join-Path $userSettingsDir "merge-settings.json"
        if (-not (Test-Path $userSettingsDir)) { New-Item -ItemType Directory -Path $userSettingsDir -Force | Out-Null }

        # Build install-time settings
        $userSettings = @{
            authenticationEnabled = $true
            isServiceInstall = $false
        }
        if ($CertPath) {
            $userSettings.certificatePath = $CertPath
            $userSettings.keyProtection = "osProtected"
        }

        if (Test-Path $userSettingsPath)
        {
            # Reinstall: write merge file, let mt handle merging
            $userSettings | ConvertTo-Json | Set-Content -Path $userMergePath -Encoding UTF8
            Write-Host "  Settings: merge file written for mt" -ForegroundColor Gray
        }
        else
        {
            # Fresh install: write settings.json directly
            $userSettings | ConvertTo-Json | Set-Content -Path $userSettingsPath -Encoding UTF8
            Write-Host "  Settings: $userSettingsPath" -ForegroundColor Gray
        }

        # Store password hash in secure storage (DPAPI-protected secrets.bin)
        # User mode - no --service-mode flag, stores in user profile
        if ($PasswordHash)
        {
            $mtPath = Join-Path $installDir "mt.exe"
            try
            {
                $PasswordHash | & $mtPath --write-secret password_hash 2>&1 | Out-Null
                Write-Host "  Password: stored in secure storage ($userSettingsDir\secrets.bin)" -ForegroundColor Gray
            }
            catch
            {
                throw "Failed to store password in secure storage at $userSettingsDir\secrets.bin. Installation aborted to avoid an insecure state. $_"
            }
        }

        Install-AsUserApp -InstallDir $installDir -Version $Version
    }

    Write-Log "=========================================="
    Write-Log "INSTALLATION COMPLETE"
    Write-Log "  Location: $installDir"
    Write-Log "  URL: https://localhost:$Port"
    Write-Log "  Settings: $settingsDir"
    Write-Log "=========================================="

    Write-Section "Complete"
    Write-Host "  Installation complete" -ForegroundColor Green
    Write-Host ""
    Write-StatusLine "Location" "$installDir" Gray
    Write-StatusLine "URL" "https://localhost:$Port" Cyan
    Write-StatusLine "Note" "Browser may show certificate warning until trusted" Yellow
    if ($AsService -and (Test-NetworkBinding -BindAddress $BindAddress) -and -not $ConfigureFirewall)
    {
        Write-StatusLine "Network" "Windows Firewall may still block other PCs from reaching this port" Yellow
    }
    Write-Host ""
}

function Install-AsService
{
    param(
        [string]$InstallDir,
        [string]$Version,
        [int]$Port = 2000,
        [string]$BindAddress = "*"
    )

    $webBinaryPath = Join-Path $InstallDir $WebBinaryName

    function Wait-ServiceDeleted
    {
        param(
            [string]$Name,
            [int]$TimeoutSeconds = 20
        )

        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        do
        {
            $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
            if (-not $service)
            {
                return
            }

            Start-Sleep -Milliseconds 250
        } while ((Get-Date) -lt $deadline)

        $status = (Get-Service -Name $Name -ErrorAction SilentlyContinue).Status
        $statusText = if ($status) { " (status: $status)" } else { "" }
        throw "Service '$Name' is still present after delete request$statusText."
    }

    function Get-MidTermServiceDiagnostics
    {
        $details = New-Object System.Collections.Generic.List[string]

        try
        {
            if (Test-Path $webBinaryPath)
            {
                $exe = Get-Item $webBinaryPath -ErrorAction Stop
                $details.Add("Executable: $webBinaryPath ($($exe.Length) bytes, last write $($exe.LastWriteTime))")
            }
            else
            {
                $details.Add("Executable missing: $webBinaryPath")
            }
        }
        catch
        {
            $details.Add("Executable inaccessible: $webBinaryPath ($_)")
        }

        try
        {
            $svc = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
            if ($svc)
            {
                $details.Add("Service state: $($svc.State), exit code: $($svc.ExitCode), service-specific exit code: $($svc.ServiceSpecificExitCode)")
                $details.Add("Service path: $($svc.PathName)")
            }
        }
        catch { }

        try
        {
            $recentSystem = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; StartTime = (Get-Date).AddMinutes(-5) } -ErrorAction SilentlyContinue |
                Where-Object { $_.Message -match $ServiceName } |
                Select-Object -First 3
            foreach ($event in $recentSystem)
            {
                $details.Add("SCM $($event.Id): $($event.Message)")
            }
        }
        catch { }

        try
        {
            $recentApp = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'MidTerm'; StartTime = (Get-Date).AddMinutes(-5) } -ErrorAction SilentlyContinue |
                Select-Object -First 5
            foreach ($event in $recentApp)
            {
                $message = ([string]$event.Message).Trim()
                if ($message)
                {
                    $details.Add("MidTerm event: $message")
                }
            }
        }
        catch { }

        return $details
    }

    # Remove existing service if present
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existingService)
    {
        Write-Host "Removing existing service..." -ForegroundColor Gray
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Get-Process -Name "mt-host", "mthost", "mtagenthost", "mt" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

        # Wait for processes to exit
        $maxWait = 10
        for ($i = 0; $i -lt $maxWait; $i++)
        {
            $procs = Get-Process -Name "mt-host", "mthost", "mtagenthost", "mt" -ErrorAction SilentlyContinue
            if (-not $procs) { break }
            Start-Sleep -Milliseconds 500
        }

        sc.exe delete $ServiceName | Out-Null
        Wait-ServiceDeleted -Name $ServiceName
    }

    # Convert bind address for command line
    $bindArg = if ($BindAddress -eq "localhost") { "127.0.0.1" } else { "0.0.0.0" }

    # Create service - mt.exe spawns mthost per terminal session
    Write-Log "Creating Windows service..."
    Write-Host "Creating MidTerm service..." -ForegroundColor Gray
    $binPath = "`"$webBinaryPath`" --port $Port --bind $bindArg"
    Write-Log "Service binPath: $binPath"
    $scCreateOutput = sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "$DisplayName" 2>&1
    Write-Log "sc.exe create output: $scCreateOutput"
    if ($LASTEXITCODE -ne 0)
    {
        $message = "Failed to create Windows service (sc.exe exit code $LASTEXITCODE): $scCreateOutput"
        Write-Log $message "ERROR"
        Write-Host "  $message" -ForegroundColor Red
        throw $message
    }
    sc.exe description $ServiceName "Web-based terminal multiplexer for AI coding agents and TUI apps" | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

    # Start service
    Write-Log "Starting service..."
    Write-Host "Starting service..." -ForegroundColor Gray
    try
    {
        Start-Service -Name $ServiceName -ErrorAction Stop
        Write-Log "Service started successfully"
    }
    catch
    {
        Write-Log "Failed to start service: $_" "ERROR"
        Write-Host "  Failed to start service: $_" -ForegroundColor Red

        $diagnostics = Get-MidTermServiceDiagnostics
        foreach ($line in $diagnostics)
        {
            Write-Log $line "ERROR"
            Write-Host "  $line" -ForegroundColor DarkGray
        }

        throw
    }

    # Verify service is running
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc)
    {
        Write-Log "Service status: $($svc.Status)"
    }

    # Register in Add/Remove Programs
    Register-Uninstall -InstallDir $InstallDir -Version $Version -IsService $true

    # Create uninstall script
    Create-UninstallScript -InstallDir $InstallDir -IsService $true
}

function Install-AsUserApp
{
    param(
        [string]$InstallDir,
        [string]$Version
    )

    # Add to user PATH
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($userPath -notlike "*$InstallDir*")
    {
        Write-Host "Adding to PATH..." -ForegroundColor Gray
        [Environment]::SetEnvironmentVariable("Path", "$userPath;$InstallDir", "User")
    }

    # Register in Add/Remove Programs (user scope)
    Register-Uninstall -InstallDir $InstallDir -Version $Version -IsService $false

    # Create uninstall script
    Create-UninstallScript -InstallDir $InstallDir -IsService $false

    Write-Host ""
    Write-Host "Run 'mt' to start MidTerm" -ForegroundColor Yellow
}

function Register-Uninstall
{
    param(
        [string]$InstallDir,
        [string]$Version,
        [bool]$IsService
    )

    $uninstallScript = Join-Path $InstallDir "uninstall.ps1"

    if ($IsService)
    {
        $regPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MidTerm"
    }
    else
    {
        $regPath = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MidTerm"
    }

    $regValues = @{
        DisplayName = $DisplayName
        DisplayVersion = $Version
        Publisher = $Publisher
        InstallLocation = $InstallDir
        UninstallString = "`"$(Get-WindowsPowerShellPath)`" -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
        DisplayIcon = Join-Path $InstallDir $WebBinaryName
        NoModify = 1
        NoRepair = 1
    }

    if (-not (Test-Path $regPath))
    {
        New-Item -Path $regPath -Force | Out-Null
    }

    foreach ($key in $regValues.Keys)
    {
        Set-ItemProperty -Path $regPath -Name $key -Value $regValues[$key]
    }
}

function Create-UninstallScript
{
    param(
        [string]$InstallDir,
        [bool]$IsService
    )

    $uninstallScript = Join-Path $InstallDir "uninstall.ps1"

    # Keep the local uninstall stub tiny so it always delegates to the latest
    # published uninstaller instead of freezing old removal logic on disk.
    $content = @"
# MidTerm Uninstaller
`$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

`$scriptUrl = 'https://get.tlbx.ai/uninstall.ps1'
if (`$PSVersionTable.PSVersion.Major -lt 6)
{
    `$scriptContent = Invoke-RestMethod -Uri `$scriptUrl -UseBasicParsing
}
else
{
    `$scriptContent = Invoke-RestMethod -Uri `$scriptUrl
}
`$scriptBlock = [ScriptBlock]::Create(`$scriptContent)

& `$scriptBlock
"@

    Set-Content -Path $uninstallScript -Value $content
}

# Main

# If we're being called with ServiceMode flag, we're the elevated process (runs hidden)
if ($ServiceMode)
{
    if ($ReplayFile)
    {
        Import-ElevatedReplayFile -Path $ReplayFile
    }

    # If log file specified, redirect all output there for streaming to original terminal
    if ($LogFile)
    {
        # Clear log file
        "" | Set-Content $LogFile -Force

        # Run the install with all output captured to file
        & {
            Write-Host ""
            Write-Host "  Running with administrator privileges..." -ForegroundColor Cyan
            Write-Host ""
            $script:release = Get-LatestRelease -DevChannel $Dev
            $version = $script:release.tag_name -replace "^v", ""
            $channelLabel = if ($Dev) { "dev" } else { "stable" }
            Write-Host "  Latest $channelLabel version: $version" -ForegroundColor White
            Write-Host ""
            Install-MidTerm -AsService $true -Version $version -RunAsUser $RunAsUser -RunAsUserSid $RunAsUserSid -PasswordHash $PasswordHash -Port $Port -BindAddress $BindAddress -ConfigureFirewall:$ConfigureFirewall -TrustCert:$TrustCert
        } *>&1 | ForEach-Object {
            $line = $_.ToString()
            Write-Host $_
            Add-Content -Path $LogFile -Value $line
        }
    }
    else
    {
        Write-Host ""
        Write-Host "  Running with administrator privileges..." -ForegroundColor Cyan
        Write-Host ""
        $script:release = Get-LatestRelease -DevChannel $Dev
        $version = $script:release.tag_name -replace "^v", ""
        $channelLabel = if ($Dev) { "dev" } else { "stable" }
        Write-Host "  Latest $channelLabel version: $version" -ForegroundColor White
        Write-Host ""
        Install-MidTerm -AsService $true -Version $version -RunAsUser $RunAsUser -RunAsUserSid $RunAsUserSid -PasswordHash $PasswordHash -Port $Port -BindAddress $BindAddress -ConfigureFirewall:$ConfigureFirewall -TrustCert:$TrustCert
    }
    return
}

# Capture current user info BEFORE any potential elevation
$currentUser = Get-CurrentUserInfo

Write-Header

# Show channel info
if ($Dev)
{
    Write-Host "  Channel: dev (prereleases)" -ForegroundColor Yellow
    Write-Host ""
}

# Fetch release info first
$script:release = Get-LatestRelease -DevChannel $Dev
$version = $script:release.tag_name -replace "^v", ""
$channelLabel = if ($Dev) { "dev" } else { "stable" }

Write-Host "  Latest $channelLabel version: $version" -ForegroundColor White
Write-Host ""

# Prompt for install mode with validation
Write-Host "  How would you like to install MidTerm?" -ForegroundColor White
Write-Host ""
Write-Host "  [1] System service (recommended for always-on access)" -ForegroundColor Cyan
Write-Host "      - Runs in background, starts on boot" -ForegroundColor Gray
Write-Host "      - Available before you log in" -ForegroundColor Gray
Write-Host "      - Installs to Program Files" -ForegroundColor Gray
Write-Host "      - Terminals run as: $($currentUser.Name)" -ForegroundColor Gray
Write-Host "      - Will prompt for admin elevation if needed" -ForegroundColor Yellow
Write-Host ""
Write-Host "  [2] User install (no admin required)" -ForegroundColor Cyan
Write-Host "      - You start it manually when needed" -ForegroundColor Gray
Write-Host "      - Only available after you log in" -ForegroundColor Gray
Write-Host "      - Installs to your AppData folder" -ForegroundColor Gray
Write-Host "      - No special permissions needed" -ForegroundColor Green
Write-Host ""

$asService = $null
$maxAttempts = 3
for ($i = 0; $i -lt $maxAttempts; $i++)
{
    $choice = Read-Host "  Your choice [1/2]"

    if ([string]::IsNullOrWhiteSpace($choice) -or $choice -eq "1")
    {
        $asService = $true
        break
    }
    elseif ($choice -eq "2")
    {
        $asService = $false
        break
    }
    else
    {
        Write-Host "  Error: Please enter 1 or 2." -ForegroundColor Red
        if ($i -lt $maxAttempts - 1)
        {
            Write-Host "  Please try again." -ForegroundColor Yellow
        }
        else
        {
            Write-Host "  Using default: System service." -ForegroundColor Yellow
            $asService = $true
        }
    }
}

if ($asService)
{
    Assert-NoCrossModeConflict -AsService $true

    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    $installDir = $WIN_SERVICE_INSTALL_DIR

    # Check for existing password in secure storage so reinstall/update keeps the
    # current auth state unless the user explicitly chooses Replace.
    if (Test-ExistingPassword)
    {
        $passwordAction = Prompt-ExistingPasswordAction
        if ($passwordAction -eq "Replace")
        {
            $passwordHash = Prompt-Password -InstallDir $installDir
        }
        else
        {
            Write-Host ""
            Write-Host "  Existing password found in secure storage - preserving..." -ForegroundColor Green
            $passwordHash = $null  # Don't overwrite - existing secrets.bin will be preserved
        }
    }
    else
    {
        # New install - prompt for password
        $passwordHash = Prompt-Password -InstallDir $installDir
    }

    # Prompt for network configuration
    $networkConfig = Prompt-NetworkConfig
    $port = $networkConfig.Port
    $bindAddress = $networkConfig.BindAddress

    # Ask about certificate trust BEFORE elevation (all interactive prompts in original terminal)
    Write-Host ""
    Write-Host "  Certificate Trust:" -ForegroundColor Cyan
    Write-Host "  Trust the certificate to remove browser warnings?" -ForegroundColor Yellow
    Write-Host "  (Adds self-signed certificate to Windows trusted root store)" -ForegroundColor Gray
    $trustChoice = Read-Host "  Trust certificate? [Y/n]"
    $trustCert = ($trustChoice -ne "n" -and $trustChoice -ne "N")
    $configureFirewall = Prompt-FirewallConfig -BindAddress $bindAddress -Port $port

    # Check if we need to elevate
    if (-not (Test-Administrator))
    {
        Write-Host ""
        Write-Host "Requesting administrator privileges..." -ForegroundColor Yellow
        Write-Host ""

        # Write a replayable elevated leg into an ACL-controlled handoff directory.
        $psExe = Get-WindowsPowerShellPath
        # Elevate with UAC and stream output via a temp log file.
        # Use Windows PowerShell for the elevated leg because it is present on
        # supported Windows systems. Per-user or Store pwsh aliases can fail
        # after UAC when the elevated account cannot resolve the user's alias.
        $handoffDir = New-ElevationHandoffDirectory -UserSid $currentUser.Sid
        $tempScript = Join-Path $handoffDir "mt-install-elevated.ps1"
        $tempLogFile = Join-Path $handoffDir "mt-install-log.txt"
        $replayFile = Join-Path $handoffDir "mt-install-replay.json"

        Set-Content -Path $tempScript -Value (Get-CurrentInstallerScriptContent) -Encoding UTF8 -Force
        "" | Set-Content -Path $tempLogFile -Encoding UTF8 -Force

        $replay = @{
            runAsUser = $currentUser.Name
            runAsUserSid = $currentUser.Sid
            passwordHash = $passwordHash
            port = $port
            bindAddress = $bindAddress
            configureFirewall = [bool]$configureFirewall
            trustCert = [bool]$trustCert
            dev = [bool]$Dev
        }
        $replay | ConvertTo-Json -Depth 5 | Set-Content -Path $replayFile -Encoding UTF8 -Force

        $runAsArguments = @(
            "-NoProfile"
            "-ExecutionPolicy", "Bypass"
            "-File", $tempScript
            "-ServiceMode"
            "-ReplayFile", $replayFile
            "-LogFile", $tempLogFile
        )

        try
        {
            $elevatedProcess = Start-Process $psExe -ArgumentList (Join-ProcessArguments -Arguments $runAsArguments) -Verb RunAs -WindowStyle Minimized -PassThru
            $elevated = $true

            # Stream output from log file to original terminal
            $linesRead = 0
            while (-not $elevatedProcess.HasExited)
            {
                Start-Sleep -Milliseconds 200
                if (Test-Path $tempLogFile)
                {
                    $lines = Get-Content $tempLogFile -ErrorAction SilentlyContinue
                    if ($lines -and $lines.Count -gt $linesRead)
                    {
                        $lines[$linesRead..($lines.Count - 1)] | ForEach-Object { Write-Host $_ }
                        $linesRead = $lines.Count
                    }
                }
            }

            # Final read to catch any remaining output
            Start-Sleep -Milliseconds 300
            if (Test-Path $tempLogFile)
            {
                $lines = Get-Content $tempLogFile -ErrorAction SilentlyContinue
                if ($lines -and $lines.Count -gt $linesRead)
                {
                    $lines[$linesRead..($lines.Count - 1)] | ForEach-Object { Write-Host $_ }
                    $linesRead = $lines.Count
                }
            }

            $elevatedProcess.WaitForExit()
            if ($elevatedProcess.ExitCode -ne 0)
            {
                Write-Host ""
                Write-Host "  Elevated installer exited with code $($elevatedProcess.ExitCode)." -ForegroundColor Red
                Remove-Item $handoffDir -Recurse -Force -ErrorAction SilentlyContinue
                exit $elevatedProcess.ExitCode
            }

            Remove-Item $handoffDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        catch
        {
            Write-Host ""
            Write-Host "  ERROR: Could not obtain administrator privileges." -ForegroundColor Red
            Write-Host ""
            Write-Host "  This can happen when:" -ForegroundColor Yellow
            Write-Host "    - UAC is disabled and you're not an administrator" -ForegroundColor Gray
            Write-Host "    - Running in a non-interactive session (SSH, container)" -ForegroundColor Gray
            Write-Host "    - The UAC prompt was cancelled" -ForegroundColor Gray
            Write-Host ""
            Write-Host "  Options:" -ForegroundColor White
            Write-Host "    1. Run this script from an elevated (Admin) terminal" -ForegroundColor White
            Write-Host "    2. Re-run the installer and choose [2] (user install, no admin needed)" -ForegroundColor White
            Write-Host ""
            if ($handoffDir)
            {
                Remove-Item $handoffDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            exit 1
        }

        # Cleanup
        Remove-Item $handoffDir -Recurse -Force -ErrorAction SilentlyContinue
        return
    }

    # Already admin, proceed with install
    Install-MidTerm -AsService $true -Version $version -RunAsUser $currentUser.Name -RunAsUserSid $currentUser.Sid -PasswordHash $passwordHash -Port $port -BindAddress $bindAddress -ConfigureFirewall:$configureFirewall -TrustCert $trustCert
}
else
{
    Assert-NoCrossModeConflict -AsService $false

    # User install - still require password
    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    $userSettingsDir = $WIN_USER_SETTINGS_DIR
    $userSecretsPath = Join-Path $userSettingsDir $WIN_SECRETS_FILENAME

    # Check for existing password in secure storage so user-mode reinstalls keep
    # the current auth state unless the user explicitly chooses Replace.
    $hasExistingPassword = $false
    if (Test-Path $userSecretsPath)
    {
        try
        {
            $secrets = Get-Content $userSecretsPath -Raw | ConvertFrom-Json
            if ($secrets.password_hash -and $secrets.password_hash.Length -gt 10)
            {
                $hasExistingPassword = $true
            }
        }
        catch { }
    }

    if ($hasExistingPassword)
    {
        $passwordAction = Prompt-ExistingPasswordAction
        if ($passwordAction -eq "Replace")
        {
            $tempDir = Join-Path $env:TEMP "MidTerm-Install"
            $passwordHash = Prompt-Password -InstallDir $tempDir
        }
        else
        {
            Write-Host ""
            Write-Host "  Existing password found in secure storage - preserving..." -ForegroundColor Green
            $passwordHash = $null  # Don't overwrite - existing secrets.bin will be preserved
        }
    }
    else
    {
        # Prompt for password - need a temp location for mt.exe to hash
        $tempDir = Join-Path $env:TEMP "MidTerm-Install"
        $passwordHash = Prompt-Password -InstallDir $tempDir
    }

    # Prompt for network configuration
    $networkConfig = Prompt-NetworkConfig

    Install-MidTerm -AsService $false -Version $version -RunAsUser "" -RunAsUserSid "" -PasswordHash $passwordHash
}
