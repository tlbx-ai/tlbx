# tlbx Windows Installer
# Usage: irm https://get.tlbx.ai/install.ps1 | iex
# Dev:   & ([scriptblock]::Create((irm https://get.tlbx.ai/install.ps1))) -Dev
#
# Design goals:
# - install only official tlbx release artifacts into known locations
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
        $logDir = $WIN_SERVICE_SETTINGS_DIR
    }
    else
    {
        $logDir = $WIN_USER_SETTINGS_DIR
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
    Write-Log "tlbx Install Script Starting"
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

$ServiceName = "tlbx"
$OldHostServiceName = "tlbxHost"
$DisplayName = "tlbx"
$Publisher = "tlbx-ai"
$RepoOwner = "tlbx-ai"
$RepoName = "tlbx"
$WebBinaryName = "mt.exe"
$TtyHostBinaryName = "mthost.exe"
$AgentHostBinaryName = "mtagenthost.exe"
$TmuxShimBinaryName = "mttmux.exe"
$LegacyHostBinaryName = "mt-host.exe"
$Is64BitWindows = [Environment]::Is64BitOperatingSystem
$AssetArchitecture = if ($Is64BitWindows) { "x64" } else { "x86" }
$AssetPattern = "mt-win-$AssetArchitecture.zip"
$ExpectedReleasePlatform = "win-$AssetArchitecture"
# Certificate subject CN - must match CertificateGenerator.CertificateSubject in C#
$CertificateSubject = "CN=tlbx"
$CertificateFileName = "tlbx.pem"
$CertificateKeyId = "tlbx"
$UninstallRegistryName = "tlbx"
$FirewallDisplayName = "tlbx HTTPS"

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
# New installations use tlbx. Existing MidTerm layouts are selected explicitly
# later and remain updateable in place.
$TLBX_SERVICE_SETTINGS_DIR = "$env:ProgramData\tlbx"
$TLBX_SERVICE_INSTALL_DIR = "$env:ProgramFiles\tlbx"
$TLBX_USER_INSTALL_DIR = "$env:LOCALAPPDATA\tlbx"
$TLBX_USER_SETTINGS_DIR = "$env:USERPROFILE\.tlbx"
$LEGACY_SERVICE_SETTINGS_DIR = "$env:ProgramData\MidTerm"
$LEGACY_SERVICE_INSTALL_DIR = "$env:ProgramFiles\MidTerm"
$LEGACY_USER_INSTALL_DIR = "$env:LOCALAPPDATA\MidTerm"
$LEGACY_USER_SETTINGS_DIR = "$env:USERPROFILE\.midterm"
$WIN_SERVICE_SETTINGS_DIR = $TLBX_SERVICE_SETTINGS_DIR
$WIN_SERVICE_INSTALL_DIR = $TLBX_SERVICE_INSTALL_DIR
$WIN_USER_INSTALL_DIR = $TLBX_USER_INSTALL_DIR
$WIN_USER_SETTINGS_DIR = $TLBX_USER_SETTINGS_DIR
# Secrets file (secrets.bin on Windows, secrets.json on Unix)
$WIN_SECRETS_FILENAME = "secrets.bin"
# ============================================================================

function Test-ServiceInstallIdentity
{
    param(
        [string]$InstallDir,
        [string]$SettingsDir,
        [string]$RegistryName,
        [string[]]$ServiceNames
    )

    if ((Test-Path (Join-Path $InstallDir $WebBinaryName)) -or
        (Test-MeaningfulSettingsDirectory -Path $SettingsDir) -or
        (Test-Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$RegistryName"))
    {
        return $true
    }

    foreach ($name in $ServiceNames)
    {
        if (Get-Service -Name $name -ErrorAction SilentlyContinue)
        {
            return $true
        }
    }

    return $false
}

function Test-UserInstallIdentity
{
    param(
        [string]$InstallDir,
        [string]$SettingsDir,
        [string]$RegistryName
    )

    return ((Test-Path (Join-Path $InstallDir $WebBinaryName)) -or
        (Test-MeaningfulSettingsDirectory -Path $SettingsDir) -or
        (Test-Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$RegistryName"))
}

function Test-MeaningfulSettingsDirectory
{
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $false }
    $entry = Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notin @("Logs", "logs", "update.log", "startup-debug.log", ".write-check") } |
        Select-Object -First 1
    return [bool]$entry
}

function Select-InstallIdentity
{
    param([bool]$AsService)

    $currentExists = if ($AsService)
    {
        Test-ServiceInstallIdentity -InstallDir $TLBX_SERVICE_INSTALL_DIR -SettingsDir $TLBX_SERVICE_SETTINGS_DIR -RegistryName "tlbx" -ServiceNames @("tlbx", "tlbxHost")
    }
    else
    {
        Test-UserInstallIdentity -InstallDir $TLBX_USER_INSTALL_DIR -SettingsDir $TLBX_USER_SETTINGS_DIR -RegistryName "tlbx"
    }

    $legacyExists = if ($AsService)
    {
        Test-ServiceInstallIdentity -InstallDir $LEGACY_SERVICE_INSTALL_DIR -SettingsDir $LEGACY_SERVICE_SETTINGS_DIR -RegistryName "MidTerm" -ServiceNames @("MidTerm", "MidTermHost")
    }
    else
    {
        Test-UserInstallIdentity -InstallDir $LEGACY_USER_INSTALL_DIR -SettingsDir $LEGACY_USER_SETTINGS_DIR -RegistryName "MidTerm"
    }

    if ($currentExists -and $legacyExists)
    {
        throw "Both tlbx and legacy MidTerm $($(if ($AsService) { 'service' } else { 'user' })) installations were found. Uninstall one copy before continuing."
    }

    if ($legacyExists)
    {
        $script:ServiceName = "MidTerm"
        $script:OldHostServiceName = "MidTermHost"
        $script:DisplayName = "MidTerm"
        $script:CertificateSubject = "CN=ai.tlbx.midterm"
        $script:CertificateFileName = "midterm.pem"
        $script:CertificateKeyId = "midterm"
        $script:UninstallRegistryName = "MidTerm"
        $script:FirewallDisplayName = "MidTerm HTTPS"
        $script:WIN_SERVICE_SETTINGS_DIR = $LEGACY_SERVICE_SETTINGS_DIR
        $script:WIN_SERVICE_INSTALL_DIR = $LEGACY_SERVICE_INSTALL_DIR
        $script:WIN_USER_INSTALL_DIR = $LEGACY_USER_INSTALL_DIR
        $script:WIN_USER_SETTINGS_DIR = $LEGACY_USER_SETTINGS_DIR
        Write-Host "  Existing MidTerm installation detected; updating it in place." -ForegroundColor Yellow
        return
    }

    $script:ServiceName = "tlbx"
    $script:OldHostServiceName = "tlbxHost"
    $script:DisplayName = "tlbx"
    $script:CertificateSubject = "CN=tlbx"
    $script:CertificateFileName = "tlbx.pem"
    $script:CertificateKeyId = "tlbx"
    $script:UninstallRegistryName = "tlbx"
    $script:FirewallDisplayName = "tlbx HTTPS"
    $script:WIN_SERVICE_SETTINGS_DIR = $TLBX_SERVICE_SETTINGS_DIR
    $script:WIN_SERVICE_INSTALL_DIR = $TLBX_SERVICE_INSTALL_DIR
    $script:WIN_USER_INSTALL_DIR = $TLBX_USER_INSTALL_DIR
    $script:WIN_USER_SETTINGS_DIR = $TLBX_USER_SETTINGS_DIR
}

$script:StatusLabelWidth = 12

function Write-Banner
{
    Write-Host ""
    Write-Host "       _   _ _            " -ForegroundColor White
    Write-Host "      | |_| | |__  __  __ " -ForegroundColor White
    Write-Host "      | __| | '_ \ \ \/ / " -ForegroundColor White
    Write-Host "      | |_| | |_) | >  <  " -ForegroundColor White
    Write-Host "       \__|_|_.__/ /_/\_\ " -ForegroundColor White
    Write-Host "      tlbx.ai - https://github.com/tlbx-ai/tlbx" -ForegroundColor Green
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
        $script:InstallerScriptDefinition.Contains("# tlbx Windows Installer"))
    {
        return $script:InstallerScriptDefinition
    }

    $scriptUrl = "https://get.tlbx.ai/install.ps1"
    return Invoke-CompatibleRestMethod -Uri $scriptUrl -Headers @{ "User-Agent" = "tlbx-Installer" }
}

function New-ElevationHandoffDirectory
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$UserSid
    )

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("tlbx-install-" + [Guid]::NewGuid().ToString("N"))
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
        (Test-ServiceInstallIdentity -InstallDir $TLBX_SERVICE_INSTALL_DIR -SettingsDir $TLBX_SERVICE_SETTINGS_DIR -RegistryName "tlbx" -ServiceNames @("tlbx", "tlbxHost")) -or
        (Test-ServiceInstallIdentity -InstallDir $LEGACY_SERVICE_INSTALL_DIR -SettingsDir $LEGACY_SERVICE_SETTINGS_DIR -RegistryName "MidTerm" -ServiceNames @("MidTerm", "MidTermHost"))
    )
}

function Test-ExistingUserInstall
{
    return (
        (Test-UserInstallIdentity -InstallDir $TLBX_USER_INSTALL_DIR -SettingsDir $TLBX_USER_SETTINGS_DIR -RegistryName "tlbx") -or
        (Test-UserInstallIdentity -InstallDir $LEGACY_USER_INSTALL_DIR -SettingsDir $LEGACY_USER_SETTINGS_DIR -RegistryName "MidTerm")
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
        Write-Host "  User traces: $TLBX_USER_INSTALL_DIR, $TLBX_USER_SETTINGS_DIR, or their legacy MidTerm equivalents" -ForegroundColor Gray
        exit 1
    }

    if (-not $AsService -and (Test-ExistingServiceInstall))
    {
        Write-Host ""
        Write-Host "  Cannot install in user mode while a system service install still exists." -ForegroundColor Red
        Write-Host "  Uninstall the service-mode copy first, then rerun the installer." -ForegroundColor Gray
        Write-Host "  Service traces: $TLBX_SERVICE_INSTALL_DIR, $TLBX_SERVICE_SETTINGS_DIR, or their legacy MidTerm equivalents" -ForegroundColor Gray
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
    Write-Host "  tlbx exposes terminal access over the network." -ForegroundColor Gray
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

        if ($pwPlain.Length -lt 15 -or $pwPlain.Length -gt 1024)
        {
            Write-Host "  Password must be between 15 and 1024 characters." -ForegroundColor Red
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

    $certPath = Join-Path $SettingsDir $CertificateFileName
    $keyPath = Join-Path (Join-Path $SettingsDir "keys") "$CertificateKeyId.dpapi"

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

function Remove-PreviousTlbxCertificates
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
            Write-Host "  Cleaned up $removed old $DisplayName certificate(s) from trusted store" -ForegroundColor Green
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
            $modeArg = if ($IsService) { "--service-mode" } else { "--user-mode" }
            $certArgs = @("--generate-cert", $modeArg, "--settings-dir", $SettingsDir, "--force")
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
                $certPath = Join-Path $SettingsDir $CertificateFileName
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
        # First, remove old certificates for the selected install identity.
        Remove-PreviousTlbxCertificates -ExceptThumbprint $null  # Remove all, we'll add the current one

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
            Write-Host "  tlbx will accept connections from any device on your network." -ForegroundColor Yellow
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

    Write-Host "Fetching latest compatible release ($AssetPattern)..." -ForegroundColor Gray
    $apiUrl = "https://api.github.com/repos/$RepoOwner/$RepoName/releases?per_page=50"
    $releases = Invoke-CompatibleRestMethod -Uri $apiUrl -Headers @{ "User-Agent" = "tlbx-Installer" }
    $compatible = @($releases | Where-Object {
        -not $_.draft -and @($_.assets | Where-Object { $_.name -eq $AssetPattern -and $_.browser_download_url }).Count -gt 0
    })
    if ($DevChannel)
    {
        $release = $compatible | Where-Object { $_.prerelease -eq $true } | Select-Object -First 1
        if ($release) { return $release }
        Write-Host "  No compatible dev release found, using stable..." -ForegroundColor Yellow
    }
    $release = $compatible | Where-Object { -not $_.prerelease } | Select-Object -First 1
    if (-not $release) { throw "No published release contains $AssetPattern." }
    return $release
}

function Test-NetworkBinding
{
    param(
        [string]$BindAddress
    )

    return $BindAddress -ne "localhost" -and $BindAddress -ne "127.0.0.1" -and $BindAddress -ne "::1"
}

function Test-TailscaleIpv4Address
{
    param([string]$Address)

    $parsed = $null
    if (-not [Net.IPAddress]::TryParse($Address, [ref]$parsed))
    {
        return $false
    }

    $bytes = $parsed.GetAddressBytes()
    return $bytes.Length -eq 4 -and $bytes[0] -eq 100 -and $bytes[1] -ge 64 -and $bytes[1] -le 127
}

function Get-TailscaleIpv4Addresses
{
    $addresses = @()
    $tailscale = Get-Command tailscale -ErrorAction SilentlyContinue
    if ($tailscale)
    {
        try
        {
            $addresses += @(& $tailscale.Source ip -4 2>$null)
        }
        catch
        {
            Write-Log "Could not query the Tailscale CLI: $_" "WARN"
        }
    }

    try
    {
        $addresses += @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | ForEach-Object { $_.IPAddress })
    }
    catch
    {
        Write-Log "Could not inspect Windows network addresses for Tailscale: $_" "WARN"
    }

    return @($addresses |
        ForEach-Object { if ($null -ne $_) { $_.ToString().Trim() } } |
        Where-Object { Test-TailscaleIpv4Address $_ } |
        Sort-Object -Unique)
}

function Format-TlbxUrl
{
    param(
        [string]$HostName,
        [int]$Port
    )

    $urlHost = $HostName
    if ($urlHost.Contains(":") -and -not $urlHost.StartsWith("["))
    {
        $urlHost = "[$urlHost]"
    }
    return "https://${urlHost}:$Port"
}

function Get-TlbxAccessUrls
{
    param(
        [int]$Port,
        [string]$BindAddress,
        [AllowNull()]
        [string[]]$TailscaleAddresses = $null
    )

    $wildcardBindings = @("*", "0.0.0.0", "::", "[::]")
    $localBindings = @("", "localhost", "127.0.0.1", "::1", "[::1]")
    $primaryHost = if ($BindAddress -in $wildcardBindings -or $BindAddress -in $localBindings)
    {
        "localhost"
    }
    else
    {
        $BindAddress
    }

    $urls = @((Format-TlbxUrl -HostName $primaryHost -Port $Port))
    if ($BindAddress -in $wildcardBindings)
    {
        if ($null -eq $TailscaleAddresses)
        {
            $TailscaleAddresses = @(Get-TailscaleIpv4Addresses)
        }
        foreach ($address in $TailscaleAddresses)
        {
            if (Test-TailscaleIpv4Address $address)
            {
                $urls += Format-TlbxUrl -HostName $address -Port $Port
            }
        }
    }
    elseif (Test-TailscaleIpv4Address $BindAddress)
    {
        $urls += Format-TlbxUrl -HostName $BindAddress -Port $Port
    }

    return @($urls | Select-Object -Unique)
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
    Write-Host "  Allow other PCs to reach tlbx on TCP port $Port?" -ForegroundColor Yellow
    Write-Host "  (Creates or updates the inbound rule named '$FirewallDisplayName')" -ForegroundColor Gray
    $choice = Read-Host "  Add firewall rule? [Y/n]"
    return ($choice -ne "n" -and $choice -ne "N")
}

function Ensure-FirewallRule
{
    param(
        [int]$Port,
        [string]$InstallDir
    )

    $displayName = $FirewallDisplayName
    $programPath = Join-Path $InstallDir $WebBinaryName

    try
    {
        Get-NetFirewallRule -DisplayName $displayName -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue

        New-NetFirewallRule `
            -DisplayName $displayName `
            -Group $DisplayName `
            -Direction Inbound `
            -Action Allow `
            -Enabled True `
            -Profile Any `
            -Protocol TCP `
            -LocalPort $Port `
            -Program $programPath `
            -Description "Allows inbound HTTPS access to $DisplayName." | Out-Null

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

function Convert-DerEcdsaSignatureToP1363
{
    param(
        [Parameter(Mandatory=$true)][byte[]]$Signature,
        [int]$CoordinateSize = 48
    )

    function Read-DerLength([byte[]]$Data, [ref]$Offset)
    {
        $first = $Data[$Offset.Value]
        $Offset.Value++
        if (($first -band 0x80) -eq 0) { return [int]$first }
        $count = $first -band 0x7f
        if ($count -lt 1 -or $count -gt 4) { throw "Invalid DER length." }
        $length = 0
        for ($i = 0; $i -lt $count; $i++)
        {
            $length = ($length -shl 8) -bor $Data[$Offset.Value]
            $Offset.Value++
        }
        return $length
    }

    function Copy-DerInteger([byte[]]$Integer, [byte[]]$Destination, [int]$DestinationOffset, [int]$Size)
    {
        $sourceOffset = 0
        while ($sourceOffset -lt ($Integer.Length - 1) -and $Integer[$sourceOffset] -eq 0)
        {
            $sourceOffset++
        }
        $length = $Integer.Length - $sourceOffset
        if ($length -gt $Size) { throw "ECDSA signature coordinate is too large." }
        [Array]::Copy($Integer, $sourceOffset, $Destination, $DestinationOffset + $Size - $length, $length)
    }

    $offset = 0
    if ($Signature[$offset++] -ne 0x30) { throw "Invalid ECDSA DER sequence." }
    $null = Read-DerLength $Signature ([ref]$offset)
    if ($Signature[$offset++] -ne 0x02) { throw "Invalid ECDSA DER R value." }
    $rLength = Read-DerLength $Signature ([ref]$offset)
    $r = [byte[]]::new($rLength)
    [Array]::Copy($Signature, $offset, $r, 0, $rLength)
    $offset += $rLength
    if ($Signature[$offset++] -ne 0x02) { throw "Invalid ECDSA DER S value." }
    $sLength = Read-DerLength $Signature ([ref]$offset)
    $s = [byte[]]::new($sLength)
    [Array]::Copy($Signature, $offset, $s, 0, $sLength)

    $p1363 = [byte[]]::new($CoordinateSize * 2)
    Copy-DerInteger $r $p1363 0 $CoordinateSize
    Copy-DerInteger $s $p1363 $CoordinateSize $CoordinateSize
    return $p1363
}

function Test-ReleaseMetadataSignature
{
    param(
        [Parameter(Mandatory=$true)][byte[]]$PayloadBytes,
        [Parameter(Mandatory=$true)][byte[]]$SignatureBytes,
        [Parameter(Mandatory=$true)][byte[]]$PublicKeyBytes
    )

    if ($PSVersionTable.PSVersion.Major -ge 7)
    {
        $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
        try
        {
            $bytesRead = 0
            $ecdsa.ImportSubjectPublicKeyInfo($PublicKeyBytes, [ref]$bytesRead)
            return $ecdsa.VerifyData(
                $PayloadBytes,
                $SignatureBytes,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
        }
        finally
        {
            $ecdsa.Dispose()
        }
    }

    # Windows PowerShell 5.1 lacks ImportSubjectPublicKeyInfo. Import the
    # P-384 public point through CNG and convert the DER signature to P1363.
    if (-not $IsWindows -and $env:OS -ne "Windows_NT")
    {
        throw "Windows PowerShell 5.1 signature verification is only supported on Windows."
    }
    if ($PublicKeyBytes.Length -lt 97 -or $PublicKeyBytes[$PublicKeyBytes.Length - 97] -ne 0x04)
    {
        throw "Release public key is not an uncompressed P-384 point."
    }

    $blob = [byte[]]::new(8 + 96)
    [Array]::Copy([BitConverter]::GetBytes([uint32]0x33534345), 0, $blob, 0, 4)
    [Array]::Copy([BitConverter]::GetBytes([uint32]48), 0, $blob, 4, 4)
    [Array]::Copy($PublicKeyBytes, $PublicKeyBytes.Length - 96, $blob, 8, 96)
    $cngKey = [System.Security.Cryptography.CngKey]::Import(
        $blob,
        [System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    $ecdsaCng = New-Object -TypeName System.Security.Cryptography.ECDsaCng -ArgumentList @(,$cngKey)
    try
    {
        $p1363Signature = Convert-DerEcdsaSignatureToP1363 -Signature $SignatureBytes
        return $ecdsaCng.VerifyData(
            $PayloadBytes,
            $p1363Signature,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    }
    finally
    {
        $ecdsaCng.Dispose()
        $cngKey.Dispose()
    }
}

function Assert-SignedRelease
{
    param(
        [Parameter(Mandatory=$true)][string]$ExtractDir,
        [Parameter(Mandatory=$true)][string]$ExpectedVersion,
        [Parameter(Mandatory=$true)][string]$ExpectedPlatform,
        [Parameter(Mandatory=$true)][string]$ExpectedChannel
    )

    $publicKeyBase64 = "MHYwEAYHKoZIzj0CAQYFK4EEACIDYgAE9txOtWhrtgO7q8Hlpe7tzv8ARMHaLYpO1JFm9psIc6LyBMLgwgz0GXfL+kU7iDVK0GyE6q2nsz7AEhKfwfbQY7d+k/WKPDEvV6OzYIYStxW4v2mAKNY1XHyuOntapcb/"
    $manifestPath = Join-Path $ExtractDir "version.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf))
    {
        throw "Downloaded release is missing its signed version.json manifest."
    }

    try
    {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.signatureVersion -ne 2 -or
            [string]::IsNullOrWhiteSpace([string]$manifest.signedPayload) -or
            [string]::IsNullOrWhiteSpace([string]$manifest.metadataSignature))
        {
            throw "Downloaded release does not use the required signed manifest-v2 format."
        }

        $payloadBytes = [Convert]::FromBase64String([string]$manifest.signedPayload)
        $signatureBytes = [Convert]::FromBase64String([string]$manifest.metadataSignature)
        $publicKeyBytes = [Convert]::FromBase64String($publicKeyBase64)
        $signatureValid = Test-ReleaseMetadataSignature -PayloadBytes $payloadBytes -SignatureBytes $signatureBytes -PublicKeyBytes $publicKeyBytes

        if (-not $signatureValid)
        {
            throw "Downloaded release metadata signature is invalid."
        }

        $payloadJson = [System.Text.Encoding]::UTF8.GetString($payloadBytes)
        $payload = $payloadJson | ConvertFrom-Json
        $expectedVersionNormalized = $ExpectedVersion.Trim().TrimStart('v', 'V')
        $payloadVersionNormalized = ([string]$payload.web).Trim().TrimStart('v', 'V')
        $metadataMatches =
            $payload.signatureVersion -eq 2 -and
            [string]$payload.web -ceq [string]$manifest.web -and
            [string]$payload.pty -ceq [string]$manifest.pty -and
            [int]$payload.protocol -eq [int]$manifest.protocol -and
            [string]$payload.minCompatiblePty -ceq [string]$manifest.minCompatiblePty -and
            [bool]$payload.webOnly -eq [bool]$manifest.webOnly -and
            [string]$payload.platform -ieq $ExpectedPlatform -and
            [string]$manifest.platform -ieq $ExpectedPlatform -and
            [string]$payload.channel -ieq $ExpectedChannel -and
            [string]$manifest.channel -ieq $ExpectedChannel -and
            $payloadVersionNormalized -ieq $expectedVersionNormalized
        if (-not $metadataMatches)
        {
            throw "Downloaded release metadata does not match the selected version, channel, or platform."
        }

        $signedChecksums = @($payload.checksums.PSObject.Properties)
        $manifestChecksums = @($manifest.checksums.PSObject.Properties)
        if ($signedChecksums.Count -eq 0 -or $signedChecksums.Count -ne $manifestChecksums.Count)
        {
            throw "Downloaded release has missing or inconsistent signed checksums."
        }

        foreach ($checksum in $signedChecksums)
        {
            $fileName = [string]$checksum.Name
            $expectedHash = ([string]$checksum.Value).ToLowerInvariant()
            $normalizedFileName = $fileName.Replace('\', '/')
            $segments = @($normalizedFileName.Split('/'))
            if ([string]::IsNullOrWhiteSpace($fileName) -or
                [System.IO.Path]::IsPathRooted($fileName) -or
                $segments.Count -eq 0 -or
                @($segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..') }).Count -gt 0 -or
                $expectedHash -notmatch '^[0-9a-f]{64}$')
            {
                throw "Downloaded release contains an unsafe checksum entry: $fileName"
            }

            $manifestProperty = $manifest.checksums.PSObject.Properties[$fileName]
            if ($null -eq $manifestProperty -or ([string]$manifestProperty.Value).ToLowerInvariant() -ne $expectedHash)
            {
                throw "Downloaded release checksum metadata is inconsistent for $fileName."
            }

            $relativePlatformPath = $normalizedFileName.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
            $filePath = [System.IO.Path]::GetFullPath((Join-Path $ExtractDir $relativePlatformPath))
            $extractRoot = [System.IO.Path]::GetFullPath($ExtractDir).TrimEnd([char[]]@('\', '/'))
            if (-not $filePath.StartsWith($extractRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
            {
                throw "Downloaded release contains an unsafe checksum entry: $fileName"
            }
            if (-not (Test-Path -LiteralPath $filePath -PathType Leaf))
            {
                throw "Downloaded release is missing signed file $fileName."
            }

            $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualHash -ne $expectedHash)
            {
                throw "Downloaded release checksum verification failed for $fileName."
            }
        }
    }
    catch
    {
        throw
    }
}

function Get-WindowsReleaseFileNames
{
    $files = @($WebBinaryName, $TtyHostBinaryName, $AgentHostBinaryName, $TmuxShimBinaryName, "version.json", "THIRD-PARTY-LICENSES.txt")
    if ($ExpectedReleasePlatform -in @("win-x64", "win-x86"))
    {
        $files += @("conpty.dll", "x64/OpenConsole.exe", "arm64/OpenConsole.exe")
        if ($ExpectedReleasePlatform -eq "win-x86")
        {
            $files += "x86/OpenConsole.exe"
        }
    }
    return $files
}

function Assert-SafeReleaseArchive
{
    param([Parameter(Mandatory=$true)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try
    {
        $required = @(Get-WindowsReleaseFileNames)
        $allowed = @($required + "SHA256SUMS.txt")
        $allowedDirectories = @("x64/", "arm64/")
        if ($ExpectedReleasePlatform -eq "win-x86")
        {
            $allowedDirectories += "x86/"
        }
        $seen = @{}
        foreach ($entry in $archive.Entries)
        {
            $normalizedName = $entry.FullName.Replace('\', '/')
            if ($normalizedName.EndsWith('/'))
            {
                if ($normalizedName -notin $allowedDirectories -or $entry.Length -ne 0)
                {
                    throw "Downloaded release archive contains unexpected or unsafe entry '$($entry.FullName)'."
                }
                continue
            }
            if ($normalizedName -notin $allowed -or [string]::IsNullOrWhiteSpace($entry.Name))
            {
                throw "Downloaded release archive contains unexpected or unsafe entry '$($entry.FullName)'."
            }
            $unixFileType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
            if (($unixFileType -ne 0) -and ($unixFileType -ne 0x8000))
            {
                throw "Downloaded release archive entry '$($entry.FullName)' is not a regular file."
            }
            if ($entry.Length -le 0)
            {
                throw "Downloaded release archive entry '$($entry.FullName)' is empty."
            }
            if ($seen.ContainsKey($normalizedName))
            {
                throw "Downloaded release archive contains duplicate entry '$($entry.FullName)'."
            }
            $seen[$normalizedName] = $true
        }
        foreach ($name in $required)
        {
            if (-not $seen.ContainsKey($name))
            {
                throw "Downloaded release archive is missing '$name'."
            }
        }
    }
    finally
    {
        $archive.Dispose()
    }
}

function Stop-ExistingInstallProcesses
{
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $oldHostService = Get-Service -Name $OldHostServiceName -ErrorAction SilentlyContinue

    if ($existingService)
    {
        Write-Host "Stopping existing service..." -ForegroundColor Gray
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Get-Process -Name "mt-host", "mthost", "mtagenthost", "mt" -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue

        for ($waited = 0; $waited -lt 10; $waited++)
        {
            if (-not (Get-Process -Name "mt-host", "mthost", "mtagenthost", "mt" -ErrorAction SilentlyContinue))
            {
                break
            }
            Start-Sleep -Milliseconds 500
        }
    }

    if ($oldHostService)
    {
        Write-Host "Migrating from old two-service architecture..." -ForegroundColor Yellow
        Stop-Service -Name $OldHostServiceName -Force -ErrorAction SilentlyContinue
        Get-Process -Name "mt-host" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
        sc.exe delete $OldHostServiceName | Out-Null
    }
}

function Install-VerifiedBinarySet
{
    param(
        [Parameter(Mandatory=$true)][string]$ExtractDir,
        [Parameter(Mandatory=$true)][string]$InstallDir,
        [Parameter(Mandatory=$true)][string]$BackupRoot
    )

    $fileNames = @(Get-WindowsReleaseFileNames)
    $stagingDir = Join-Path $InstallDir (".tlbx-install-" + [Guid]::NewGuid().ToString("N"))
    $backupDir = Join-Path $BackupRoot "backup"
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

    foreach ($fileName in $fileNames)
    {
        $source = Join-Path $ExtractDir $fileName
        if (-not (Test-Path -LiteralPath $source -PathType Leaf))
        {
            throw "Verified release is missing $fileName."
        }
        $stagedPath = Join-Path $stagingDir $fileName
        New-Item -ItemType Directory -Path (Split-Path -Parent $stagedPath) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $stagedPath -Force
    }

    $backedUp = New-Object System.Collections.Generic.List[string]
    try
    {
        foreach ($fileName in $fileNames)
        {
            $destination = Join-Path $InstallDir $fileName
            if (Test-Path -LiteralPath $destination)
            {
                $backupPath = Join-Path $backupDir $fileName
                New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force | Out-Null
                Copy-Item -LiteralPath $destination -Destination $backupPath -Force
                $backedUp.Add($fileName)
            }
        }

        foreach ($fileName in $fileNames)
        {
            $destination = Join-Path $InstallDir $fileName
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Move-Item -LiteralPath (Join-Path $stagingDir $fileName) -Destination $destination -Force
            Write-Host "  Installed: $(Join-Path $InstallDir $fileName)" -ForegroundColor Gray
        }
    }
    catch
    {
        foreach ($fileName in $fileNames)
        {
            Remove-Item -LiteralPath (Join-Path $InstallDir $fileName) -Force -ErrorAction SilentlyContinue
        }
        foreach ($fileName in $backedUp)
        {
            $destination = Join-Path $InstallDir $fileName
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $backupDir $fileName) -Destination $destination -Force -ErrorAction SilentlyContinue
        }
        throw "Installing the verified binary set failed; the previous files were restored. $_"
    }
    finally
    {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Restore-PreviousBinarySet
{
    param(
        [Parameter(Mandatory=$true)][string]$InstallDir,
        [Parameter(Mandatory=$true)][string]$BackupRoot
    )

    $backupDir = Join-Path $BackupRoot "backup"
    foreach ($fileName in @(Get-WindowsReleaseFileNames))
    {
        Remove-Item -LiteralPath (Join-Path $InstallDir $fileName) -Force -ErrorAction SilentlyContinue
        $backupPath = Join-Path $backupDir $fileName
        if (Test-Path -LiteralPath $backupPath -PathType Leaf)
        {
            $destination = Join-Path $InstallDir $fileName
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $backupPath -Destination $destination -Force -ErrorAction Stop
        }
    }
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
            $PasswordHash | & $mtPath --write-secret password_hash --service-mode --settings-dir $configDir 2>&1 | Out-Null
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

function Invoke-TlbxBootstrapRequest
{
    param([string]$Url)

    if ($PSVersionTable.PSVersion.Major -ge 6)
    {
        return Invoke-RestMethod -Uri "$Url/api/bootstrap/login" -TimeoutSec 5 -SkipCertificateCheck -ErrorAction Stop
    }

    Add-Type @"
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public class TlbxInstallerTrustAllCerts {
    public static void Ignore() {
        ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
    }
    public static void Restore() {
        ServicePointManager.ServerCertificateValidationCallback = null;
    }
}
"@ -ErrorAction SilentlyContinue

    try
    {
        [TlbxInstallerTrustAllCerts]::Ignore()
        return Invoke-RestMethod -Uri "$Url/api/bootstrap/login" -TimeoutSec 5 -ErrorAction Stop
    }
    finally
    {
        [TlbxInstallerTrustAllCerts]::Restore()
    }
}

function Get-TlbxProcess
{
    param(
        [string]$ExecutablePath,
        [int]$ExpectedProcessId = 0
    )

    if ($ExpectedProcessId -gt 0)
    {
        return Get-Process -Id $ExpectedProcessId -ErrorAction SilentlyContinue
    }

    $expectedFullPath = [IO.Path]::GetFullPath($ExecutablePath)
    $processes = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExecutablePath)) -ErrorAction SilentlyContinue)
    foreach ($process in $processes)
    {
        try
        {
            if ($process.Path -and [IO.Path]::GetFullPath($process.Path) -eq $expectedFullPath)
            {
                return $process
            }
        }
        catch
        {
            # Process path access can race with startup/exit; retry on the next poll.
        }
    }
    return $null
}

function Wait-TlbxReady
{
    param(
        [string]$ExecutablePath,
        [int]$Port,
        [string]$BindAddress,
        [bool]$AsService,
        [int]$ExpectedProcessId = 0
    )

    $bootstrapUrl = @(Get-TlbxAccessUrls -Port $Port -BindAddress $BindAddress -TailscaleAddresses @())[0]
    $lastBootstrap = $null
    $process = $null
    $serviceStatus = if ($AsService) { "Unknown" } else { "Not applicable" }

    for ($attempt = 1; $attempt -le 15; $attempt++)
    {
        $process = Get-TlbxProcess -ExecutablePath $ExecutablePath -ExpectedProcessId $ExpectedProcessId
        if ($AsService)
        {
            $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            $serviceStatus = if ($service) { $service.Status.ToString() } else { "Not installed" }
        }

        try
        {
            $lastBootstrap = Invoke-TlbxBootstrapRequest -Url $bootstrapUrl
        }
        catch
        {
            $lastBootstrap = $null
        }

        $serviceReady = -not $AsService -or $serviceStatus -eq "Running"
        if ($process -and $serviceReady -and $lastBootstrap -and $lastBootstrap.certificate.fingerprint)
        {
            return [pscustomobject]@{
                Version = (& $ExecutablePath --version | Out-String).Trim()
                Ready = $true
                Process = $process
                ServiceStatus = $serviceStatus
                Bootstrap = $lastBootstrap
                BootstrapUrl = $bootstrapUrl
            }
        }

        if ($ExpectedProcessId -gt 0 -and -not $process)
        {
            break
        }
        if ($attempt -lt 15)
        {
            Start-Sleep -Seconds 1
        }
    }

    return [pscustomobject]@{
        Ready = $false
        Process = $process
        ServiceStatus = $serviceStatus
        Bootstrap = $lastBootstrap
        BootstrapUrl = $bootstrapUrl
    }
}

function Start-TlbxUserProcess
{
    param(
        [string]$ExecutablePath,
        [string]$InstallDir,
        [string]$SettingsDir,
        [int]$Port,
        [string]$BindAddress
    )

    $bindArg = if ($BindAddress -eq "localhost") { "127.0.0.1" } else { $BindAddress }
    $stdoutLog = Join-Path $SettingsDir "tlbx-user.stdout.log"
    $stderrLog = Join-Path $SettingsDir "tlbx-user.stderr.log"
    $arguments = Join-ProcessArguments -Arguments @(
        "--user-mode",
        "--settings-dir", $SettingsDir,
        "--port", $Port.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--bind", $bindArg)

    Write-Log "Starting user-mode tlbx with stdout=$stdoutLog and stderr=$stderrLog"
    return Start-Process -FilePath $ExecutablePath -ArgumentList $arguments -WorkingDirectory $InstallDir `
        -WindowStyle Hidden -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru
}

function Install-Tlbx
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

    $tempRoot = $null
    $installDir = $null
    $binarySwapCompleted = $false
    $installationCompleted = $false
    $serviceExistedBefore = $false
    $startedUserProcess = $null
    trap
    {
        $installError = $_
        if ($startedUserProcess -and -not $startedUserProcess.HasExited)
        {
            Stop-Process -Id $startedUserProcess.Id -Force -ErrorAction SilentlyContinue
        }
        if ($binarySwapCompleted -and -not $installationCompleted -and $installDir -and $tempRoot)
        {
            Write-Host "  Installation failed; restoring the previous tlbx binary set..." -ForegroundColor Yellow
            try
            {
                if ($AsService)
                {
                    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
                }
                Restore-PreviousBinarySet -InstallDir $installDir -BackupRoot $tempRoot
                if ($AsService -and $serviceExistedBefore)
                {
                    Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
                }
                elseif ($AsService -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue))
                {
                    sc.exe delete $ServiceName | Out-Null
                }
                Write-Log "Previous binary set restored after installation failure" "WARN"
            }
            catch
            {
                Write-Log "Rollback failed: $_" "ERROR"
                Write-Host "  Automatic rollback also failed. See $script:UpdateLogFile." -ForegroundColor Red
            }
        }
        if ($tempRoot -and (Test-Path -LiteralPath $tempRoot))
        {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw $installError
    }

    # Initialize logging
    $mode = if ($AsService) { "service" } else { "user" }
    Initialize-Log -Mode $mode
    Write-Log "Starting installation: Version=$Version, AsService=$AsService, RunAsUser=$RunAsUser"

    if ($AsService)
    {
        # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
        $installDir = $WIN_SERVICE_INSTALL_DIR
        Write-Log "Install directory: $installDir"
    }
    else
    {
        # Uses PATH_CONSTANTS defined above
        $installDir = $WIN_USER_INSTALL_DIR
    }

    # Download and extract
    $tempRoot = Join-Path $env:TEMP ("tlbx-install-" + [Guid]::NewGuid().ToString("N"))
    $tempZip = Join-Path $tempRoot "release.zip"
    $tempExtract = Join-Path $tempRoot "extract"
    New-Item -ItemType Directory -Path $tempExtract -Force | Out-Null

    Write-Log "=== PHASE 1: Downloading binaries ==="
    Write-Host "Downloading..." -ForegroundColor Gray
    $assetUrl = Get-AssetUrl -Release $script:release
    Write-Log "Downloading from: $assetUrl"
    Invoke-CompatibleWebRequest -Uri $assetUrl -OutFile $tempZip
    Unblock-DownloadedPath -Path $tempZip
    Write-Log "Download complete"

    Write-Host "Extracting..." -ForegroundColor Gray
    Write-Log "Extracting to: $tempExtract"
    Assert-SafeReleaseArchive -Path $tempZip
    Expand-Archive -Path $tempZip -DestinationPath $tempExtract
    Assert-SignedRelease -ExtractDir $tempExtract -ExpectedVersion $version -ExpectedPlatform $ExpectedReleasePlatform -ExpectedChannel $channelLabel
    Unblock-DownloadedTree -Path $tempExtract
    Write-Log "Extraction and release-signature verification complete"

    # Do not interrupt a working installation until the complete download has
    # passed archive, metadata-signature, and checksum verification.
    if ($AsService)
    {
        $serviceExistedBefore = [bool](Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
        Stop-ExistingInstallProcesses
    }
    if (-not (Test-Path $installDir))
    {
        New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    }

    Write-Log "=== PHASE 2: Installing binaries ==="
    $destWebBinary = Join-Path $installDir $WebBinaryName

    Write-Host "Installing binaries to $installDir..." -ForegroundColor Gray
    Write-Log "Installing binaries to $installDir"
    try
    {
        Install-VerifiedBinarySet -ExtractDir $tempExtract -InstallDir $installDir -BackupRoot $tempRoot
        $binarySwapCompleted = $true
    }
    catch
    {
        if ($AsService -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue))
        {
            Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
        }
        throw
    }

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

        # Monitor both the supervised process and the configured HTTPS endpoint.
        Write-Section "Status"
        Write-StatusLine "Startup" "Waiting for process and https endpoint..." Yellow
        $readiness = Wait-TlbxReady -ExecutablePath $destWebBinary -Port $Port -BindAddress $BindAddress -AsService $true

        if ($readiness.ServiceStatus -eq "Running") { Write-StatusLine "Service" "Running" Green }
        else { Write-StatusLine "Service" "$($readiness.ServiceStatus)" Red }

        if ($readiness.Process) { Write-StatusLine "tlbx process" "Running (PID $($readiness.Process.Id))" Green }
        else { Write-StatusLine "tlbx process" "Not running" Red }

        if ($readiness.Ready)
        {
            Write-StatusLine "HTTPS" "Login endpoint ready" Green
            Write-StatusLine "Version" "$($readiness.Version)" Gray
        }
        elseif ($readiness.Bootstrap)
        {
            Write-StatusLine "Readiness" "Login endpoint responded, but process/service is not ready" Red
        }
        else
        {
            Write-StatusLine "HTTPS" "Could not connect to $($readiness.BootstrapUrl)" Red
        }

        if (-not $readiness.Ready)
        {
            throw "tlbx was installed but did not pass the service and health verification. See $script:UpdateLogFile."
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

        $runtimeSettings = @{
            port = $Port
            bindAddress = if ($BindAddress -eq "localhost") { "127.0.0.1" } else { "0.0.0.0" }
        }
        $runtimeSettings | ConvertTo-Json | Set-Content -Path (Join-Path $userSettingsDir "runtime.json") -Encoding UTF8

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
                $PasswordHash | & $mtPath --write-secret password_hash --user-mode --settings-dir $userSettingsDir 2>&1 | Out-Null
                Write-Host "  Password: stored in secure storage ($userSettingsDir\secrets.bin)" -ForegroundColor Gray
            }
            catch
            {
                throw "Failed to store password in secure storage at $userSettingsDir\secrets.bin. Installation aborted to avoid an insecure state. $_"
            }
        }

        Install-AsUserApp -InstallDir $installDir -Version $Version -Port $Port -BindAddress $BindAddress

        Write-Section "Status"
        Write-StatusLine "Startup" "Starting tlbx and waiting for https endpoint..." Yellow
        $startedUserProcess = Start-TlbxUserProcess -ExecutablePath $destWebBinary -InstallDir $installDir `
            -SettingsDir $userSettingsDir -Port $Port -BindAddress $BindAddress
        $readiness = Wait-TlbxReady -ExecutablePath $destWebBinary -Port $Port -BindAddress $BindAddress `
            -AsService $false -ExpectedProcessId $startedUserProcess.Id

        if ($readiness.Process) { Write-StatusLine "tlbx process" "Running (PID $($readiness.Process.Id))" Green }
        else { Write-StatusLine "tlbx process" "Not running" Red }

        if ($readiness.Ready)
        {
            Write-StatusLine "HTTPS" "Login endpoint ready" Green
            Write-StatusLine "Version" "$($readiness.Version)" Gray
        }
        else
        {
            Write-StatusLine "HTTPS" "Could not connect to $($readiness.BootstrapUrl)" Red
            throw "tlbx was installed but the user process did not become reachable. See $userSettingsDir\tlbx-user.stderr.log."
        }
    }

    # Remove the obsolete split-host binary only after the complete install has
    # succeeded so rollback never destroys a still-working legacy deployment.
    $legacyHostPath = Join-Path $installDir $LegacyHostBinaryName
    if (Test-Path $legacyHostPath)
    {
        Remove-Item $legacyHostPath -Force -ErrorAction SilentlyContinue
        Write-Host "  Removed legacy: $LegacyHostBinaryName" -ForegroundColor Gray
    }

    $installationCompleted = $true
    Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue

    Write-Log "=========================================="
    Write-Log "INSTALLATION COMPLETE"
    Write-Log "  Location: $installDir"
    $accessUrls = @(Get-TlbxAccessUrls -Port $Port -BindAddress $BindAddress)
    foreach ($accessUrl in $accessUrls)
    {
        Write-Log "  URL: $accessUrl"
    }
    Write-Log "  Settings: $settingsDir"
    Write-Log "=========================================="

    Write-Section "Complete"
    Write-Host "  Your tlbx is ready at:" -ForegroundColor Green
    Write-Host ""
    Write-StatusLine "Location" "$installDir" Gray
    foreach ($accessUrl in $accessUrls)
    {
        Write-Host "  $accessUrl" -ForegroundColor Cyan
    }
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

    function Get-TlbxServiceDiagnostics
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
    Write-Host "Creating $DisplayName service..." -ForegroundColor Gray
    $binPath = "`"$webBinaryPath`" --service-mode --settings-dir `"$WIN_SERVICE_SETTINGS_DIR`" --service-name `"$ServiceName`" --port $Port --bind $bindArg"
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

        $diagnostics = Get-TlbxServiceDiagnostics
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
        [string]$Version,
        [int]$Port,
        [string]$BindAddress
    )

    # Add to user PATH
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($userPath -notlike "*$InstallDir*")
    {
        Write-Host "Adding to PATH..." -ForegroundColor Gray
        [Environment]::SetEnvironmentVariable("Path", "$userPath;$InstallDir", "User")
    }

    $bindArg = if ($BindAddress -eq "localhost") { "127.0.0.1" } else { "0.0.0.0" }
    [Environment]::SetEnvironmentVariable("TLBX_PORT", $Port.ToString([Globalization.CultureInfo]::InvariantCulture), "User")
    [Environment]::SetEnvironmentVariable("TLBX_BIND", $bindArg, "User")
    $env:TLBX_PORT = $Port.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:TLBX_BIND = $bindArg

    # Register in Add/Remove Programs (user scope)
    Register-Uninstall -InstallDir $InstallDir -Version $Version -IsService $false

    # Create uninstall script
    Create-UninstallScript -InstallDir $InstallDir -IsService $false

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
        $regPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$UninstallRegistryName"
    }
    else
    {
        $regPath = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$UninstallRegistryName"
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
# tlbx Uninstaller
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

    Select-InstallIdentity -AsService $true

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
            Install-Tlbx -AsService $true -Version $version -RunAsUser $RunAsUser -RunAsUserSid $RunAsUserSid -PasswordHash $PasswordHash -Port $Port -BindAddress $BindAddress -ConfigureFirewall:$ConfigureFirewall -TrustCert:$TrustCert
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
        Install-Tlbx -AsService $true -Version $version -RunAsUser $RunAsUser -RunAsUserSid $RunAsUserSid -PasswordHash $PasswordHash -Port $Port -BindAddress $BindAddress -ConfigureFirewall:$ConfigureFirewall -TrustCert:$TrustCert
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
Write-Host "  How would you like to install tlbx?" -ForegroundColor White
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
    Select-InstallIdentity -AsService $true

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
    Install-Tlbx -AsService $true -Version $version -RunAsUser $currentUser.Name -RunAsUserSid $currentUser.Sid -PasswordHash $passwordHash -Port $port -BindAddress $bindAddress -ConfigureFirewall:$configureFirewall -TrustCert $trustCert
}
else
{
    Assert-NoCrossModeConflict -AsService $false
    Select-InstallIdentity -AsService $false

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
            $tempDir = Join-Path $env:TEMP "tlbx-install"
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
        $tempDir = Join-Path $env:TEMP "tlbx-install"
        $passwordHash = Prompt-Password -InstallDir $tempDir
    }

    # Prompt for network configuration
    $networkConfig = Prompt-NetworkConfig

    Install-Tlbx -AsService $false -Version $version -RunAsUser "" -RunAsUserSid "" -PasswordHash $passwordHash -Port $networkConfig.Port -BindAddress $networkConfig.BindAddress
}
