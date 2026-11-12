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

function Test-InstallerPbkdf2Support
{
    try
    {
        return $null -ne [Security.Cryptography.Rfc2898DeriveBytes].GetConstructor(
            [type[]]@([byte[]], [byte[]], [int], [Security.Cryptography.HashAlgorithmName]))
    }
    catch { return $false }
}

function Assert-InstallerPrerequisites
{
    param(
        [version]$PowerShellVersion = $PSVersionTable.PSVersion,
        [string]$LanguageMode = $ExecutionContext.SessionState.LanguageMode,
        [bool]$WindowsPlatform = ($env:OS -eq 'Windows_NT' -or $PSVersionTable.PSEdition -eq 'Desktop' -or $PSVersionTable.Platform -eq 'Win32NT')
    )
    if (-not $WindowsPlatform) { throw 'This installer supports Windows only. Use install.sh on Linux or macOS.' }
    if ($PowerShellVersion.Major -lt 5 -or ($PowerShellVersion.Major -eq 5 -and $PowerShellVersion.Minor -lt 1))
    { throw 'The tlbx installer requires Windows PowerShell 5.1 or PowerShell 7.' }
    if ($LanguageMode -ne 'FullLanguage')
    { throw "The tlbx installer requires PowerShell FullLanguage; this session is $LanguageMode. Use an administrator-approved FullLanguage session." }
    if (-not (Test-InstallerPbkdf2Support))
    { throw 'This PowerShell runtime lacks PBKDF2-HMAC-SHA256 support. Windows PowerShell needs .NET Framework 4.7.2 or later; alternatively use a supported PowerShell 7 installation. No runtime is installed automatically.' }
}

function Get-InstallerEnvironmentValue([string]$Name) { return [Environment]::GetEnvironmentVariable($Name) }
function Get-InstallerSpecialFolder([string]$Name) { return [Environment]::GetFolderPath([Environment+SpecialFolder]$Name) }
function Get-InstallerNativeProgramFiles
{
    try { $key = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64) } catch { return $null }
    try
    {
        $version = $key.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion')
        try { if ($version) { return [string]$version.GetValue('ProgramFilesDir') } }
        finally { if ($version) { $version.Dispose() } }
    }
    finally { $key.Dispose() }
}

function Resolve-InstallerDirectory
{
    param([string]$Name, [string[]]$Candidates, [switch]$Writable)
    foreach ($candidate in $Candidates)
    {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $created = $false; $check = $null; $stream = $null
        try
        {
            $expanded = [Environment]::ExpandEnvironmentVariables($candidate)
            if ($expanded -notmatch '^(?:[a-zA-Z]:[\\/]|\\\\[^\\]+\\[^\\]+)') { continue }
            $path = [IO.Path]::GetFullPath($expanded)
            if ([IO.File]::Exists($path)) { continue }
            if (-not $Writable -and -not [IO.Directory]::Exists($path)) { continue }
            if ($Writable)
            {
                $null = [IO.Directory]::CreateDirectory($path)
                $check = Join-Path $path ('.tlbx-write-check-' + [Guid]::NewGuid().ToString('N'))
                $stream = [IO.File]::Open($check, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                $created = $true
            }
            return $path
        }
        catch { } # Try the OS-owned fallback instead of a missing/invalid environment path.
        finally
        {
            if ($stream) { $stream.Dispose() }
            if ($created) { [IO.File]::Delete($check) }
        }
    }
    throw "Could not resolve a usable Windows $Name directory. Check this account's Windows profile/environment before retrying."
}

function Resolve-InstallerEnvironmentPaths
{
    param([bool]$Is64BitOperatingSystem = [Environment]::Is64BitOperatingSystem, [bool]$Is64BitProcess = [Environment]::Is64BitProcess)
    $profile = Resolve-InstallerDirectory -Name UserProfile -Candidates @((Get-InstallerEnvironmentValue USERPROFILE), (Get-InstallerSpecialFolder UserProfile))
    $local = Resolve-InstallerDirectory -Name LocalAppData -Writable -Candidates @((Get-InstallerEnvironmentValue LOCALAPPDATA), (Get-InstallerSpecialFolder LocalApplicationData), (Join-Path $profile 'AppData\Local'))
    $windows = Resolve-InstallerDirectory -Name Windows -Candidates @((Get-InstallerEnvironmentValue SystemRoot), (Get-InstallerEnvironmentValue windir), (Get-InstallerSpecialFolder Windows))
    $data = Resolve-InstallerDirectory -Name ProgramData -Candidates @((Get-InstallerEnvironmentValue ProgramData), (Get-InstallerSpecialFolder CommonApplicationData))
    if ($Is64BitOperatingSystem -and -not $Is64BitProcess)
    {
        $programs = Resolve-InstallerDirectory -Name NativeProgramFiles -Candidates @((Get-InstallerEnvironmentValue ProgramW6432), (Get-InstallerNativeProgramFiles))
    }
    else
    {
        $programs = Resolve-InstallerDirectory -Name ProgramFiles -Candidates @((Get-InstallerEnvironmentValue ProgramFiles), (Get-InstallerSpecialFolder ProgramFiles))
    }
    $temp = Resolve-InstallerDirectory -Name Temp -Writable -Candidates @((Get-InstallerEnvironmentValue TEMP), (Get-InstallerEnvironmentValue TMP), (Join-Path $local 'Temp'), (Join-Path $windows 'Temp'))
    return [pscustomobject]@{ UserProfile=$profile; LocalAppData=$local; ProgramData=$data; ProgramFiles=$programs; WindowsRoot=$windows; TempRoot=$temp }
}

# Reject unsupported shells before TLS, cryptography or filesystem operations.
Assert-InstallerPrerequisites
$script:InstallerPaths = Resolve-InstallerEnvironmentPaths
$script:InstallerTempRoot = $script:InstallerPaths.TempRoot
# Ensure TLS 1.2 for HTTPS build distribution (PS 5.1 defaults to TLS 1.0)
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
    if ($Uri.StartsWith("https://builds.tlbx.ai/", [StringComparison]::OrdinalIgnoreCase)) { $params.MaximumRedirection = 0 }
    if ($PSBoundParameters.ContainsKey("TimeoutSec")) { $params.TimeoutSec = $TimeoutSec }

    if ($PSVersionTable.PSVersion.Major -lt 6)
    {
        $params.UseBasicParsing = $true
    }
    elseif ($SkipCertificateCheck)
    {
        $params.SkipCertificateCheck = $true
    }

    if (-not $params.ContainsKey("TimeoutSec")) { $params.TimeoutSec = 30 }
    for ($attempt = 1; $attempt -le 3; $attempt++)
    {
        try { return Invoke-RestMethod @params }
        catch
        {
            if ($attempt -eq 3) { throw }
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
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

    if ($Uri.StartsWith("https://builds.tlbx.ai/", [StringComparison]::OrdinalIgnoreCase)) { $params.MaximumRedirection = 0 }
    $params.TimeoutSec = 600
    for ($attempt = 1; $attempt -le 3; $attempt++)
    {
        try { return Invoke-WebRequest @params }
        catch
        {
            if ($attempt -eq 3) { throw }
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

function Unblock-DownloadedPath
{
    param(
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path))
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

    if (-not (Test-Path -LiteralPath $Path))
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

    if (-not (Test-Path -LiteralPath $logDir))
    {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }

    $script:UpdateLogFile = Join-Path $logDir "update.log"

    # Clear previous log
    "" | Set-Content -LiteralPath $script:UpdateLogFile -Encoding UTF8 -Force -ErrorAction SilentlyContinue

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
        Add-Content -LiteralPath $script:UpdateLogFile -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
    }
}

$ServiceName = "tlbx"
$OldHostServiceName = "tlbxHost"
$DisplayName = "tlbx"
$Publisher = "tlbx-ai"
$BuildsOrigin = "https://builds.tlbx.ai"
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

# ============================================================================
# PATH CONSTANTS - SYNC: These paths MUST match:
#   - SettingsService.cs (GetSettingsPath method)
#   - LogPaths.cs (constants and GetSettingsDirectory method)
#   - UpdateScriptGenerator.cs (SettingsDir variable in generated scripts)
#   - install.sh (PATH_CONSTANTS section)
# ============================================================================
# New installations use tlbx. Existing MidTerm layouts are selected explicitly
# later and remain updateable in place.
$TLBX_SERVICE_SETTINGS_DIR = (Join-Path $script:InstallerPaths.ProgramData 'tlbx')
$TLBX_SERVICE_INSTALL_DIR = (Join-Path $script:InstallerPaths.ProgramFiles 'tlbx')
$TLBX_USER_INSTALL_DIR = (Join-Path $script:InstallerPaths.LocalAppData 'tlbx')
$TLBX_USER_SETTINGS_DIR = (Join-Path $script:InstallerPaths.UserProfile '.tlbx')
$LEGACY_SERVICE_SETTINGS_DIR = (Join-Path $script:InstallerPaths.ProgramData 'MidTerm')
$LEGACY_SERVICE_INSTALL_DIR = (Join-Path $script:InstallerPaths.ProgramFiles 'MidTerm')
$LEGACY_USER_INSTALL_DIR = (Join-Path $script:InstallerPaths.LocalAppData 'MidTerm')
$LEGACY_USER_SETTINGS_DIR = (Join-Path $script:InstallerPaths.UserProfile '.midterm')
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

    if ((Test-Path -LiteralPath (Join-Path $InstallDir $WebBinaryName)) -or
        (Test-MeaningfulSettingsDirectory -Path $SettingsDir) -or
        (Test-Path -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$RegistryName"))
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

    return ((Test-Path -LiteralPath (Join-Path $InstallDir $WebBinaryName)) -or
        (Test-MeaningfulSettingsDirectory -Path $SettingsDir) -or
        (Test-Path -LiteralPath "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$RegistryName"))
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
    $systemRoot = $script:InstallerPaths.WindowsRoot
    if ([string]::IsNullOrWhiteSpace($systemRoot))
    {
        $systemRoot = $env:windir
    }

    if (-not [string]::IsNullOrWhiteSpace($systemRoot))
    {
        $candidate = Join-Path $systemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
        if (Test-Path -LiteralPath $candidate)
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
    if ($script:InstallerScriptPath -and (Test-Path -LiteralPath $script:InstallerScriptPath))
    {
        return Get-Content -LiteralPath $script:InstallerScriptPath -Raw -Encoding UTF8
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

    $root = Join-Path $script:InstallerTempRoot ("tlbx-install-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $root -Force | Out-Null

    $grants = @()
    $grants += "*$UserSid`:(OI)(CI)F"
    $grants += "*S-1-5-32-544:(OI)(CI)F"
    $grants += "*S-1-5-18:(OI)(CI)F"

    $output = & icacls.exe $root /inheritance:r 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
        throw "Could not disable inherited ACLs on elevated installer handoff directory: $output"
    }

    foreach ($grant in $grants)
    {
        $output = & icacls.exe $root /grant:r $grant 2>&1
        if ($LASTEXITCODE -ne 0)
        {
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
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

function Read-InstallerChoice
{
    param([string]$Prompt, [string[]]$Choices, [string]$DefaultValue)
    for ($attempt = 0; $attempt -lt 3; $attempt++)
    {
        $answer = ([string](Read-Host $Prompt)).Trim()
        if ([string]::IsNullOrWhiteSpace($answer)) { return $DefaultValue }
        if ($answer -in $Choices) { return $answer }
        Write-Host "  Invalid choice. Please enter $($Choices -join ' or ')." -ForegroundColor Red
    }
    throw "Too many invalid choices. Installation cancelled before making changes."
}

function Read-InstallerYesNo
{
    param([string]$Prompt, [bool]$DefaultValue = $true)
    for ($attempt = 0; $attempt -lt 3; $attempt++)
    {
        $answer = ([string](Read-Host $Prompt)).Trim().ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($answer)) { return $DefaultValue }
        if ($answer -in @('y','yes','j','ja')) { return $true }
        if ($answer -in @('n','no','nein')) { return $false }
        Write-Host "  Invalid answer. Please enter yes or no." -ForegroundColor Red
    }
    throw "Too many invalid answers. Installation cancelled before making changes."
}

function Normalize-InstallerBindAddress
{
    param([AllowNull()][string]$BindAddress)
    if ([string]::IsNullOrWhiteSpace($BindAddress)) { return '*' }
    switch ($BindAddress.Trim().ToLowerInvariant())
    {
        '*' { return '*' }
        '0.0.0.0' { return '*' }
        'localhost' { return 'localhost' }
        '127.0.0.1' { return 'localhost' }
        '::1' { return 'localhost' }
        default { throw "Invalid bind address. Use localhost, 127.0.0.1, ::1, * or 0.0.0.0." }
    }
}

function Assert-InstallerOptions
{
    param($Port, [AllowNull()][string]$BindAddress)
    $parsedPort = 0
    if (-not [int]::TryParse([string]$Port, [ref]$parsedPort) -or $parsedPort -lt 1 -or $parsedPort -gt 65535)
    {
        throw "Invalid port. Enter a whole number between 1 and 65535."
    }
    return @{ Port = $parsedPort; BindAddress = (Normalize-InstallerBindAddress $BindAddress) }
}

function Import-ElevatedReplayFile
{
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Elevated installer replay file not found: $Path" }
    $replay = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $replay -or $replay -is [Array]) { throw "Invalid installer replay object." }
    foreach ($field in @('runAsUser','runAsUserSid','passwordHash','port','bindAddress','configureFirewall','trustCert','dev'))
    {
        if ($field -notin $replay.PSObject.Properties.Name) { throw "Installer replay is missing '$field'." }
    }
    foreach ($field in @('configureFirewall','trustCert','dev'))
    {
        if ($replay.$field -isnot [bool]) { throw "Installer replay '$field' must be a JSON boolean." }
    }
    if ($replay.runAsUser -isnot [string] -or [string]::IsNullOrWhiteSpace($replay.runAsUser) -or
        $replay.runAsUserSid -isnot [string] -or [string]::IsNullOrWhiteSpace($replay.runAsUserSid))
    {
        throw "Installer replay requires a user name and SID."
    }
    try { $sid = New-Object Security.Principal.SecurityIdentifier($replay.runAsUserSid) }
    catch { throw "Invalid installer replay user SID." }
    if ($replay.bindAddress -isnot [string]) { throw "Installer replay bind address must be a string." }
    $options = Assert-InstallerOptions -Port $replay.port -BindAddress $replay.bindAddress
    if ($null -ne $replay.passwordHash)
    {
        if ($replay.passwordHash -isnot [string] -or
            $replay.passwordHash -cnotmatch '^\$PBKDF2\$600000\$([A-Za-z0-9+/]{43}=)\$([A-Za-z0-9+/]{43}=)$')
        {
            throw "Invalid installer replay password hash."
        }
        if ([Convert]::FromBase64String($Matches[1]).Length -ne 32 -or [Convert]::FromBase64String($Matches[2]).Length -ne 32)
        {
            throw "Invalid installer replay password hash."
        }
    }
    # Assign only after the complete replay has passed validation.
    $script:RunAsUser = $replay.runAsUser
    $script:RunAsUserSid = $sid.Value
    $script:PasswordHash = $replay.passwordHash
    $script:Port = $options.Port
    $script:BindAddress = $options.BindAddress
    $script:ConfigureFirewall = $replay.configureFirewall
    $script:TrustCert = $replay.trustCert
    $script:Dev = $replay.dev
}

function Test-ExistingPassword
{
    # Check if password exists in secure storage (secrets.bin)
    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    $secretsPath = "$WIN_SERVICE_SETTINGS_DIR\$WIN_SECRETS_FILENAME"
    if (Test-Path -LiteralPath $secretsPath)
    {
        try
        {
            $secrets = Get-Content -LiteralPath $secretsPath -Raw | ConvertFrom-Json
            if ($secrets.password_hash -and $secrets.password_hash.Length -gt 10)
            {
                return $true
            }
        }
        catch { }
    }

    # Legacy: check settings.json (old broken path - will be migrated)
    $settingsPath = "$WIN_SERVICE_SETTINGS_DIR\settings.json"
    if (Test-Path -LiteralPath $settingsPath)
    {
        try
        {
            $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
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
        throw "Installation mode conflicts with an existing tlbx installation."
    }

    if (-not $AsService -and (Test-ExistingServiceInstall))
    {
        Write-Host ""
        Write-Host "  Cannot install in user mode while a system service install still exists." -ForegroundColor Red
        Write-Host "  Uninstall the service-mode copy first, then rerun the installer." -ForegroundColor Gray
        Write-Host "  Service traces: $TLBX_SERVICE_INSTALL_DIR, $TLBX_SERVICE_SETTINGS_DIR, or their legacy MidTerm equivalents" -ForegroundColor Gray
        throw "Installation mode conflicts with an existing tlbx installation."
    }
}

function New-InstallerPasswordHash
{
    param([Parameter(Mandatory=$true)][string]$Password)

    if ($Password.Length -lt 10 -or $Password.Length -gt 1024)
    {
        throw "Password must be between 10 and 1024 characters."
    }

    # Windows PowerShell 5.1/.NET Framework and PowerShell 7 both provide this
    # SHA256 overload. No downloaded executable or extra runtime is needed.
    $salt = New-Object byte[] 32
    $passwordBytes = [Text.Encoding]::UTF8.GetBytes($Password)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $derivation = $null
    try
    {
        $rng.GetBytes($salt)
        $derivation = [Security.Cryptography.Rfc2898DeriveBytes]::new(
            $passwordBytes, $salt, 600000, [Security.Cryptography.HashAlgorithmName]::SHA256)
        $hash = $derivation.GetBytes(32)
        return '$PBKDF2$600000$' + [Convert]::ToBase64String($salt) + '$' + [Convert]::ToBase64String($hash)
    }
    finally
    {
        if ($derivation) { $derivation.Dispose() }
        $rng.Dispose()
        [Array]::Clear($passwordBytes, 0, $passwordBytes.Length)
    }
}

function ConvertFrom-InstallerSecureString
{
    param([Parameter(Mandatory=$true)][Security.SecureString]$Value)
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Prompt-Password
{
    Write-Host ""
    Write-Host "  Security Notice:" -ForegroundColor Yellow
    Write-Host "  tlbx exposes terminal access over the network." -ForegroundColor Gray
    Write-Host "  A password is required to prevent unauthorized access." -ForegroundColor Gray
    Write-Host ""

    for ($i = 0; $i -lt 3; $i++)
    {
        $password = Read-Host "  Enter password" -AsSecureString
        $confirm = Read-Host "  Confirm password" -AsSecureString
        try
        {
            $pwPlain = ConvertFrom-InstallerSecureString $password
            $confirmPlain = ConvertFrom-InstallerSecureString $confirm
            if ($pwPlain -cne $confirmPlain)
            {
                Write-Host "  Passwords do not match. Try again." -ForegroundColor Red
                continue
            }
            if ($pwPlain.Length -lt 10 -or $pwPlain.Length -gt 1024)
            {
                Write-Host "  Password must be between 10 and 1024 characters." -ForegroundColor Red
                continue
            }
            return New-InstallerPasswordHash -Password $pwPlain
        }
        finally
        {
            $pwPlain = $null
            $confirmPlain = $null
            $password.Dispose()
            $confirm.Dispose()
        }
    }
    throw "Too many failed password attempts."
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

    $choice = Read-InstallerChoice -Prompt "  Your choice [1/2]" -Choices @('1','2') -DefaultValue '1'
    if ($choice -eq '2') { return 'Replace' }
    return 'Preserve'
}

function Test-ExistingCertificate
{
    param([string]$SettingsDir, [bool]$IsService = $false)
    $certPath = Join-Path $SettingsDir $CertificateFileName
    $keyPath = Join-Path (Join-Path $SettingsDir 'keys') "$CertificateKeyId.dpapi"
    if (-not (Test-Path -LiteralPath $certPath -PathType Leaf)) { return $null }
    if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf))
    {
        Write-Host '  Warning: Certificate exists but private key is missing' -ForegroundColor Yellow
        return $null
    }
    $cert = $null; $privateBytes = $null; $key = $null; $privateKey = $null; $publicKey = $null
    $accepted = $false
    try
    {
        $certContent = Get-Content -LiteralPath $certPath -Raw
        $base64 = $certContent -replace '-----BEGIN CERTIFICATE-----','' -replace '-----END CERTIFICATE-----','' -replace '\s',''
        $cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(,[Convert]::FromBase64String($base64))
        $now = Get-Date
        if ($cert.NotBefore -gt $now -or $cert.NotAfter -lt $now.AddDays(30))
        {
            Write-Host '  Warning: Existing certificate is not valid now or expires in less than 30 days' -ForegroundColor Yellow
            return $null
        }
        Add-Type -AssemblyName System.Security
        $scope = if ($IsService) { [Security.Cryptography.DataProtectionScope]::LocalMachine } else { [Security.Cryptography.DataProtectionScope]::CurrentUser }
        $privateBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyPath), $null, $scope)
        $key = [Security.Cryptography.CngKey]::Import($privateBytes, [Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
        $challenge = New-Object byte[] 32
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($challenge) } finally { $rng.Dispose() }
        $matches = $false
        if ($key.AlgorithmGroup -eq [Security.Cryptography.CngAlgorithmGroup]::ECDsa -or
            $key.AlgorithmGroup -eq [Security.Cryptography.CngAlgorithmGroup]::ECDiffieHellman)
        {
            $privateKey = New-Object Security.Cryptography.ECDsaCng -ArgumentList $key
            $publicKey = [Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPublicKey($cert)
            if ($publicKey)
            {
                $signature = $privateKey.SignData($challenge, [Security.Cryptography.HashAlgorithmName]::SHA256)
                $matches = $publicKey.VerifyData($challenge, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256)
            }
        }
        elseif ($key.AlgorithmGroup -eq [Security.Cryptography.CngAlgorithmGroup]::Rsa)
        {
            $privateKey = New-Object Security.Cryptography.RSACng -ArgumentList $key
            $publicKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($cert)
            if ($publicKey)
            {
                $signature = $privateKey.SignData($challenge, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
                $matches = $publicKey.VerifyData($challenge, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            }
        }
        if (-not $matches) { throw 'The protected private key does not match the HTTPS certificate.' }
        $accepted = $true
        return @{ Path=$certPath; Certificate=$cert; Thumbprint=$cert.Thumbprint; NotAfter=$cert.NotAfter }
    }
    catch
    {
        Write-Host "  Warning: Could not validate existing certificate/key pair: $_" -ForegroundColor Yellow
        return $null
    }
    finally
    {
        if ($privateBytes) { [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
        if ($publicKey) { $publicKey.Dispose() }
        if ($privateKey) { $privateKey.Dispose() }
        if ($key) { $key.Dispose() }
        if ($cert -and -not $accepted) { $cert.Dispose() }
    }
}


function Show-CertificateFingerprint
{
    param(
        [string]$CertPath
    )

    if (-not $CertPath -or -not (Test-Path -LiteralPath $CertPath))
    {
        return
    }

    try
    {
        # Load the PEM certificate
        $certContent = Get-Content -LiteralPath $CertPath -Raw
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
        [bool]$IsService = $false
    )

    Write-Log "Generating certificate: InstallDir=$InstallDir, SettingsDir=$SettingsDir, IsService=$IsService"

    # First check if a valid certificate already exists
    $existingCert = Test-ExistingCertificate -SettingsDir $SettingsDir -IsService $IsService
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
        if (-not (Test-Path -LiteralPath $mtPath))
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
            $certContent = Get-Content -LiteralPath $certPath -Raw
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

    return $certPath
}

function Trust-InstallerCertificate
{
    param([Parameter(Mandatory=$true)][string]$CertPath)
    $rootStore = $null
    $cert = $null
    try
    {
        Write-Host "  Adding certificate to trusted root store..." -ForegroundColor Gray
        $content = Get-Content -LiteralPath $CertPath -Raw
        $base64 = $content -replace "-----BEGIN CERTIFICATE-----", "" -replace "-----END CERTIFICATE-----", "" -replace "\s", ""
        $bytes = [Convert]::FromBase64String($base64)
        $cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(,$bytes)
        $rootStore = New-Object Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
        $rootStore.Open("ReadWrite")
        # Other tlbx hosts/installations can have the same certificate subject.
        # Add this verified instance only; never remove unrelated trusted roots.
        $rootStore.Add($cert)
        Write-Host "  Certificate trusted successfully" -ForegroundColor Green
    }
    catch
    {
        Write-Host "  Could not trust certificate: $_" -ForegroundColor Yellow
        Write-Host "  You may see browser warnings until manually trusted" -ForegroundColor Gray
    }
    finally
    {
        if ($rootStore) { $rootStore.Close() }
        if ($cert) { $cert.Dispose() }
    }
}

function Prompt-NetworkConfig
{
    param([string]$InstallDir = '')
    Write-Host ""
    Write-Host "  Network Configuration:" -ForegroundColor Cyan
    Write-Host ""

    # Port configuration with validation and retry
    $maxAttempts = 3
    $port = 2000
    $validPort = $false
    for ($i = 0; $i -lt $maxAttempts; $i++)
    {
        $portInput = ([string](Read-Host "  Port number [2000]")).Trim()
        $parsedPort = 2000
        if (-not [string]::IsNullOrWhiteSpace($portInput) -and
            (-not [int]::TryParse($portInput, [ref]$parsedPort) -or $parsedPort -lt 1 -or $parsedPort -gt 65535))
        {
            Write-Host "  Error: Port must be a whole number between 1 and 65535." -ForegroundColor Red
            continue
        }
        try
        {
            if ($InstallDir) { Assert-InstallerPortAvailable -Port $parsedPort -BindAddress localhost -InstallDir $InstallDir }
            $port = $parsedPort; $validPort = $true; break
        }
        catch { Write-Host "  $($_.Exception.Message)" -ForegroundColor Red }
    }
    if (-not $validPort) { throw "Too many invalid ports. Installation cancelled before making changes." }
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

    $bindChoice = Read-InstallerChoice -Prompt "  Your choice [1/2]" -Choices @('1','2') -DefaultValue '1'
    $bindAddress = if ($bindChoice -eq '2') { 'localhost' } else { '*' }
    if ($bindAddress -eq '*')
    {
        Write-Host "  tlbx will accept connections from other devices. Use a strong password." -ForegroundColor Yellow
    }
    else { Write-Host "  Binding to localhost only" -ForegroundColor Gray }
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
    $channels = if ($DevChannel) { @("dev", "main") } else { @("main") }
    foreach ($channel in $channels)
    {
        $releases = Invoke-CompatibleRestMethod -Uri "$BuildsOrigin/v1/channels/$channel/releases.json" -TimeoutSec 10 -Headers @{ "User-Agent" = "tlbx-Installer" }
        $release = $releases | Where-Object {
            -not $_.draft -and ([bool]$_.prerelease -eq ($channel -eq "dev")) -and
                @($_.assets | Where-Object { $_.name -eq $AssetPattern }).Count -gt 0
        } | Select-Object -First 1
        if ($release)
        {
            Get-AssetUrl -Release $release | Out-Null
            return $release
        }
        if ($channel -eq "dev") { Write-Host "  No compatible dev release found, using stable..." -ForegroundColor Yellow }
    }
    throw "No published build contains $AssetPattern."
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
    param([string]$BindAddress, [int]$Port = 2000)
    if (-not (Test-NetworkBinding -BindAddress $BindAddress)) { return $false }
    Write-Host ""
    Write-Host "  Windows Firewall:" -ForegroundColor Cyan
    Write-Host "  Allow other PCs to reach tlbx on TCP port ${Port}?" -ForegroundColor Yellow
    Write-Host "  (Creates or updates the inbound rule named '$FirewallDisplayName')" -ForegroundColor Gray
    return Read-InstallerYesNo -Prompt "  Add firewall rule? [Y/n]" -DefaultValue $true
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

function Get-ReleaseChannel
{
    param($Release)
    if ([bool]$Release.prerelease) { return "dev" }
    return "stable"
}

function Get-AssetUrl
{
    param($Release)
    $assets = @($Release.assets | Where-Object { $_.name -ceq $AssetPattern })
    if ($assets.Count -ne 1) { throw "Expected one $AssetPattern in build assets." }
    $asset = $assets[0]
    $tag = [string]$Release.tag_name
    if ($tag -cnotmatch '^v?[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?$') { throw "Invalid build tag." }
    $version = $tag -creplace '^v', ''
    $channel = if ($version.Contains('-')) { 'dev' } else { 'main' }
    $expected = "$BuildsOrigin/$channel/$version/$ExpectedReleasePlatform/$AssetPattern"
    $uri = $null
    if (-not [uri]::TryCreate([string]$asset.browser_download_url, [UriKind]::Absolute, [ref]$uri) -or
        $uri.AbsoluteUri -cne $expected -or [long]$asset.size -le 0 -or
        [bool]$Release.prerelease -ne $version.Contains('-') -or
        [string]$asset.digest -cnotmatch '^sha256:[0-9a-fA-F]{64}$')
    {
        throw "Invalid build URL, size or SHA256 for $AssetPattern."
    }
    return $uri.AbsoluteUri
}

function Assert-BuildArchive
{
    param([Parameter(Mandatory=$true)][string]$Path, [Parameter(Mandatory=$true)]$Release)
    Get-AssetUrl -Release $Release | Out-Null
    $asset = @($Release.assets | Where-Object { $_.name -ceq $AssetPattern })[0]
    if ((Get-Item -LiteralPath $Path).Length -ne [long]$asset.size -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine ([string]$asset.digest).Substring(7))
    {
        throw "Downloaded build archive does not match its catalog size/SHA256."
    }
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
        $allowed = @($required + "SHA256SUMS.txt" + "runtime-inputs.json")
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

function Get-InstallExecutablePaths
{
    param([Parameter(Mandatory=$true)][string]$InstallDir)

    foreach ($name in @("mt.exe", "mthost.exe", "mtagenthost.exe", "mt-host.exe"))
    {
        [IO.Path]::GetFullPath((Join-Path $InstallDir $name))
    }
}

function Get-InstallOwnedProcesses
{
    param([Parameter(Mandatory=$true)][string]$InstallDir)

    $paths = @(Get-InstallExecutablePaths -InstallDir $InstallDir)
    foreach ($candidate in @(Get-Process -Name "mt-host", "mthost", "mtagenthost", "mt" -ErrorAction SilentlyContinue))
    {
        try
        {
            if ($candidate.Path -and [IO.Path]::GetFullPath($candidate.Path) -in $paths)
            {
                $candidate
            }
        }
        catch { } # Never stop a process whose executable cannot be identified.
    }
}

function Assert-InstallerCanStopTarget
{
    param(
        [Parameter(Mandatory=$true)][string]$InstallDir,
        [string]$TargetServiceName = ""
    )

    $paths = @(Get-InstallExecutablePaths -InstallDir $InstallDir)
    $targetServiceProcessId = 0
    if ($TargetServiceName)
    {
        $escapedName = $TargetServiceName.Replace("'", "''")
        $serviceInfo = Get-CimInstance -ClassName Win32_Service -Filter "Name='$escapedName'" -ErrorAction Stop
        if ($serviceInfo) { $targetServiceProcessId = [int]$serviceInfo.ProcessId }
    }
    $ancestorId = $PID
    $seen = @{}
    while ($ancestorId -gt 0 -and -not $seen.ContainsKey($ancestorId))
    {
        $seen[$ancestorId] = $true
        $ancestor = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$ancestorId" -ErrorAction Stop
        if (-not $ancestor) { break }
        $ownedPath = $ancestor.ExecutablePath -and [IO.Path]::GetFullPath($ancestor.ExecutablePath) -in $paths
        if ($ownedPath -or ($targetServiceProcessId -gt 0 -and $ancestorId -eq $targetServiceProcessId))
        {
            throw "This terminal is hosted by the tlbx installation being replaced. Run the installer from an external Windows Terminal or PowerShell window; stopping this installation would terminate the installer."
        }
        $ancestorId = [int]$ancestor.ParentProcessId
    }
}

function Stop-ExistingInstallProcesses
{
    param([Parameter(Mandatory=$true)][string]$InstallDir)

    Assert-InstallerCanStopTarget -InstallDir $InstallDir -TargetServiceName $ServiceName
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $oldHostService = Get-Service -Name $OldHostServiceName -ErrorAction SilentlyContinue

    if ($existingService -or $oldHostService)
    {
        Write-Host "Stopping existing installation..." -ForegroundColor Gray
        if ($existingService) { Stop-Service -Name $ServiceName -Force -ErrorAction Stop }
        if ($oldHostService) { Stop-Service -Name $OldHostServiceName -Force -ErrorAction Stop }
        Get-InstallOwnedProcesses -InstallDir $InstallDir | Stop-Process -Force -ErrorAction SilentlyContinue
        for ($waited = 0; $waited -lt 10; $waited++)
        {
            if (-not (Get-InstallOwnedProcesses -InstallDir $InstallDir)) { break }
            Start-Sleep -Milliseconds 500
        }
    }

    if ($oldHostService)
    {
        Write-Host "Migrating from old two-service architecture..." -ForegroundColor Yellow
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
    $touched = New-Object System.Collections.Generic.List[string]
    try
    {
        New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        foreach ($fileName in $fileNames)
        {
            $source = Join-Path $ExtractDir $fileName
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Verified release is missing $fileName." }
            $stagedPath = Join-Path $stagingDir $fileName
            New-Item -ItemType Directory -Path (Split-Path -Parent $stagedPath) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $stagedPath -Force -ErrorAction Stop
        }

        # Complete every backup before changing any installed file. A failed
        # backup must never remove originals that have not been secured yet.
        foreach ($fileName in $fileNames)
        {
            $destination = Join-Path $InstallDir $fileName
            if (Test-Path -LiteralPath $destination)
            {
                $backupPath = Join-Path $backupDir $fileName
                New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force | Out-Null
                Copy-Item -LiteralPath $destination -Destination $backupPath -Force -ErrorAction Stop
            }
        }
        foreach ($fileName in $fileNames)
        {
            $destination = Join-Path $InstallDir $fileName
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            # Include the current operation: a failed Move-Item may already
            # have replaced its destination before reporting the failure.
            $touched.Add($fileName)
            Move-Item -LiteralPath (Join-Path $stagingDir $fileName) -Destination $destination -Force -ErrorAction Stop
            Write-Host "  Installed: $destination" -ForegroundColor Gray
        }
    }
    catch
    {
        $installFailure = $_
        if ($touched.Count -gt 0)
        {
            try
            {
                Restore-PreviousBinarySet -InstallDir $InstallDir -BackupRoot $BackupRoot -FileNames $touched.ToArray()
            }
            catch
            {
                $failure = [InvalidOperationException]::new("Installing the verified binary set failed: $($installFailure.Exception.Message) Rollback also failed: $($_.Exception.Message) Backup retained at $BackupRoot.", $installFailure.Exception)
                $failure.Data['TlbxPreserveBackupRoot'] = $BackupRoot
                throw $failure
            }
        }
        throw $installFailure
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
        [Parameter(Mandatory=$true)][string]$BackupRoot,
        [string[]]$FileNames = @(Get-WindowsReleaseFileNames)
    )

    $backupDir = Join-Path $BackupRoot "backup"
    try
    {
        foreach ($fileName in $FileNames)
        {
            $destination = Join-Path $InstallDir $fileName
            $backupPath = Join-Path $backupDir $fileName
            if (Test-Path -LiteralPath $backupPath -PathType Leaf)
            {
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                Copy-Item -LiteralPath $backupPath -Destination $destination -Force -ErrorAction Stop
            }
            elseif (Test-Path -LiteralPath $destination)
            {
                Remove-Item -LiteralPath $destination -Force -ErrorAction Stop
            }
        }
    }
    catch
    {
        $failure = [InvalidOperationException]::new("Could not restore the previous binary set. Backup retained at $BackupRoot. $($_.Exception.Message)", $_.Exception)
        $failure.Data['TlbxPreserveBackupRoot'] = $BackupRoot
        throw $failure
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

    if (-not (Test-Path -LiteralPath $configDir))
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

    if (Test-Path -LiteralPath $settingsPath)
    {
        # Reinstall: write merge file, let mt handle merging
        Set-Content -LiteralPath $mergePath -Value $json -Encoding UTF8
        Write-Host "  Settings: merge file written for mt" -ForegroundColor Gray
    }
    else
    {
        # Fresh install: write settings.json directly
        Set-Content -LiteralPath $settingsPath -Value $json -Encoding UTF8
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
            if ($LASTEXITCODE -ne 0) { throw "Native secret storage failed (exit $LASTEXITCODE)." }
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
        return Invoke-RestMethod -Uri "$Url/api/bootstrap/login" -TimeoutSec 5 -SkipCertificateCheck -NoProxy -ErrorAction Stop
    }

    if (-not ('TlbxInstallerHttpsProbe' -as [type]))
    {
        Add-Type @"
using System.Net;
using System.IO;
public static class TlbxInstallerHttpsProbe {
    public static string Get(string url) {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Proxy = null;
        request.Timeout = 5000;
        request.ReadWriteTimeout = 5000;
        request.ServerCertificateValidationCallback = delegate { return true; };
        using (var response = request.GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream())) {
            return reader.ReadToEnd();
        }
    }
}
"@ -ErrorAction Stop
    }
    # Accept only this local instance's self-signed certificate. Do not alter
    # the calling shell's global TLS validation callback or use its HTTP proxy.
    return [TlbxInstallerHttpsProbe]::Get("$Url/api/bootstrap/login") | ConvertFrom-Json
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

function Invoke-InstallerVersionProbe
{
    param([Parameter(Mandatory=$true)][string]$ExecutablePath, [Parameter(Mandatory=$true)][string]$ExpectedVersion, [int]$TimeoutMilliseconds = 15000)
    if ($TimeoutMilliseconds -lt 1 -or $TimeoutMilliseconds -gt 60000) { throw 'Invalid native compatibility probe timeout.' }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = [IO.Path]::GetFullPath($ExecutablePath)
    $info.Arguments = '--version'
    $info.WorkingDirectory = Split-Path -Parent $info.FileName
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    $started = $false
    try
    {
        try { $started = $process.Start() }
        catch
        { throw "The verified Windows binary '$ExecutablePath' could not start: $($_.Exception.Message). Check Windows compatibility and antivirus/application-control or missing native loader dependencies. The running installation has not been stopped." }
        if (-not $started) { throw "The verified Windows binary '$ExecutablePath' did not start." }
        # Drain both redirected pipes concurrently, including loader diagnostics.
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $finished = $process.WaitForExit($TimeoutMilliseconds)
        if (-not $finished) { $process.Kill(); $null = $process.WaitForExit(5000) }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000))
        { throw "Native compatibility probe did not close its diagnostic streams: $ExecutablePath" }
        $output = $stdout.GetAwaiter().GetResult().Trim()
        $errorOutput = $stderr.GetAwaiter().GetResult().Trim()
        $detail = ("stdout: $output; stderr: $errorOutput")
        if ($detail.Length -gt 4096) { $detail = $detail.Substring(0, 4096) }
        if (-not $finished) { throw "Native compatibility probe timed out after ${TimeoutMilliseconds}ms: $ExecutablePath. $detail" }
        if ($process.ExitCode -ne 0) { throw "Native compatibility probe failed (exit $($process.ExitCode)): $ExecutablePath. $detail" }
        $pattern = '^(?:(?:mtagenthost|mthost|tmux)\s+)?v?' + [regex]::Escape($ExpectedVersion.TrimStart('v')) + '(?:\+[A-Za-z0-9.-]+)?$'
        if ($output -notmatch $pattern) { throw "Native binary version mismatch for $ExecutablePath (expected $ExpectedVersion). $detail" }
        return $output
    }
    finally
    {
        if ($started -and -not $process.HasExited) { try { $process.Kill() } catch { } }
        $process.Dispose()
    }
}

function Assert-VerifiedWindowsRuntime
{
    param([Parameter(Mandatory=$true)][string]$ExtractDir, [Parameter(Mandatory=$true)][string]$ExpectedVersion)
    try
    {
        $null = Invoke-InstallerVersionProbe -ExecutablePath (Join-Path $ExtractDir 'mt.exe') -ExpectedVersion $ExpectedVersion
        # All four version-only commands return before host/session startup.
        $manifest = Get-Content -LiteralPath (Join-Path $ExtractDir 'version.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($manifest.pty -and (Test-Path -LiteralPath (Join-Path $ExtractDir 'mtagenthost.exe')))
        { $null = Invoke-InstallerVersionProbe -ExecutablePath (Join-Path $ExtractDir 'mtagenthost.exe') -ExpectedVersion ([string]$manifest.pty) }
        if ($manifest.pty -and (Test-Path -LiteralPath (Join-Path $ExtractDir 'mthost.exe')))
        { $null = Invoke-InstallerVersionProbe -ExecutablePath (Join-Path $ExtractDir 'mthost.exe') -ExpectedVersion ([string]$manifest.pty) }
        if (Test-Path -LiteralPath (Join-Path $ExtractDir 'mttmux.exe'))
        { $null = Invoke-InstallerVersionProbe -ExecutablePath (Join-Path $ExtractDir 'mttmux.exe') -ExpectedVersion '3.5a-midterm' }
        Write-Log "Verified Windows native startup/version compatibility before stopping the current installation."
    }
    catch
    {
        Write-Log $_.Exception.Message 'ERROR'
        throw
    }
}
function Stop-InstallerUserProcesses
{
    param([Parameter(Mandatory=$true)][string]$InstallDir)

    Assert-InstallerCanStopTarget -InstallDir $InstallDir
    $processes = @(Get-InstallOwnedProcesses -InstallDir $InstallDir)
    $webPath = [IO.Path]::GetFullPath((Join-Path $InstallDir $WebBinaryName))
    $wasRunning = @($processes | Where-Object { $_.Path -and [IO.Path]::GetFullPath($_.Path) -eq $webPath }).Count -gt 0
    $processes | Stop-Process -Force -ErrorAction Stop
    for ($attempt = 0; $attempt -lt 20; $attempt++)
    {
        if (-not (Get-InstallOwnedProcesses -InstallDir $InstallDir)) { return @{ WasRunning = $wasRunning } }
        Start-Sleep -Milliseconds 250
    }
    throw "The previous user installation did not stop. No installed files have been replaced."
}

function Start-TlbxPreviousUserProcess
{
    param([string]$ExecutablePath, [string]$InstallDir, [string]$SettingsDir)

    # The restored runtime.json owns the previous port/bind. Do not let the
    # installer's inherited process environment override those saved values.
    $savedEnvironment = @{}
    foreach ($name in @('TLBX_PORT', 'TLBX_BIND', 'MIDTERM_PORT', 'MIDTERM_BIND'))
    {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    try
    {
        foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
        $arguments = Join-ProcessArguments -Arguments @('--user-mode', '--settings-dir', $SettingsDir)
        return Start-Process -FilePath $ExecutablePath -ArgumentList $arguments -WorkingDirectory $InstallDir -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $SettingsDir 'tlbx-user.stdout.log') -RedirectStandardError (Join-Path $SettingsDir 'tlbx-user.stderr.log') -PassThru -ErrorAction Stop
    }
    finally
    {
        foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    }
}

function Assert-InstallerUserLaunchPaths
{
    param([string]$InstallDir, [string]$SettingsDir)
    # Start-Process treats these valid filename characters as wildcards in
    # WorkingDirectory AND redirected log paths, including in PowerShell 7.
    # Refuse before teardown instead of damaging an existing installation.
    if ($InstallDir.IndexOfAny([char[]]'[]') -ge 0 -or $SettingsDir.IndexOfAny([char[]]'[]') -ge 0)
    {
        throw 'Windows PowerShell cannot launch this background user installation because its install/settings path contains square brackets. Choose system service mode instead. The existing installation has not been stopped or replaced.'
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

function Assert-InstallerPortAvailable
{
    param([int]$Port, [string]$BindAddress, [string]$InstallDir)

    $connections = $null
    if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)
    {
        try { $connections = @(Get-NetTCPConnection -State Listen -ErrorAction Stop) }
        catch { Write-Log "Could not inspect TCP listeners; checking the local socket instead: $_" "WARN" }
    }
    if ($null -ne $connections)
    {
        $ownedIds = @(Get-InstallOwnedProcesses -InstallDir $InstallDir | ForEach-Object { $_.Id })
        # A non-elevated PowerShell cannot always read a LocalSystem process's
        # executable path. SCM still exposes the exact registered binary path.
        try
        {
            $escapedName = $ServiceName.Replace("'", "''")
            $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$escapedName'" -ErrorAction Stop
            if ($service.ProcessId -gt 0 -and $service.PathName -match '^"([^"]+)"(?:\s|$)')
            {
                if ([IO.Path]::GetFullPath($Matches[1]) -eq [IO.Path]::GetFullPath((Join-Path $InstallDir 'mt.exe')))
                { $ownedIds += $service.ProcessId }
            }
        }
        catch { Write-Log "Could not read the selected SCM process identity: $_" "WARN" }
        $conflicts = @($connections | Where-Object { $_.LocalPort -eq $Port -and $_.OwningProcess -notin $ownedIds })
        if ($conflicts.Count)
        {
            $ownerIds = @($conflicts | ForEach-Object { $_.OwningProcess } | Select-Object -Unique) -join ', '
            throw "TCP port $Port is already in use by another process (PID $ownerIds). Choose another port or stop that application. The existing tlbx installation has not been stopped."
        }
        return
    }

    # The NetTCPIP module can be absent/disabled on stripped-down Windows.
    # Use only loopback for this brief probe; it must not create a firewall prompt.
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, $Port)
    try
    {
        $listener.ExclusiveAddressUse = $true
        $listener.Start()
    }
    catch
    {
        throw "TCP port $Port could not be reserved and Windows could not identify its owner. Choose a free port, or stop the existing service from an external terminal before retrying. The existing installation has not been changed. $($_.Exception.Message)"
    }
    finally { $listener.Stop() }
}

function Get-InstallerServiceSnapshot
{
    param([Parameter(Mandatory=$true)][string]$Name)

    $escapedName = $Name.Replace("'", "''")
    $serviceInfo = Get-CimInstance -ClassName Win32_Service -Filter "Name='$escapedName'" -ErrorAction Stop
    if (-not $serviceInfo) { throw "Existing service '$Name' could not be read before installation." }
    return @{
        Name = $Name
        ServiceInfo = $serviceInfo
        WasRunning = $serviceInfo.State -eq "Running"
        Arguments = @{
            PathName = $serviceInfo.PathName
            DisplayName = $serviceInfo.DisplayName
            StartMode = if ($serviceInfo.StartMode -eq "Auto") { "Automatic" } else { $serviceInfo.StartMode }
        }
    }
}

function Restore-InstallerServiceSnapshot
{
    param([Parameter(Mandatory=$true)]$Snapshot)

    $restored = Invoke-CimMethod -InputObject $Snapshot.ServiceInfo -MethodName Change -Arguments $Snapshot.Arguments -ErrorAction Stop
    if ($restored.ReturnValue -ne 0) { throw "Could not restore Windows service '$($Snapshot.Name)' configuration (SCM result $($restored.ReturnValue))." }
    if ($Snapshot.WasRunning) { Start-Service -Name $Snapshot.Name -ErrorAction Stop }
}

function Get-InstallerSettingsSnapshot
{
    param(
        [Parameter(Mandatory=$true)][string]$SettingsDir,
        [Parameter(Mandatory=$true)][string]$BackupRoot
    )

    $files = @("settings.json", "merge-settings.json", "runtime.json", "secrets.bin", $CertificateFileName, "keys/$CertificateKeyId.dpapi")
    $existing = New-Object System.Collections.Generic.List[string]
    foreach ($file in $files)
    {
        $source = Join-Path $SettingsDir $file
        if (Test-Path -LiteralPath $source)
        {
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Installer settings path is not a file: $source" }
            $backup = Join-Path (Join-Path $BackupRoot "settings-backup") $file
            New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $backup -Force -ErrorAction Stop
            $existing.Add($file)
        }
    }
    return @{ SettingsDir = $SettingsDir; BackupRoot = $BackupRoot; Files = $files; ExistingFiles = $existing.ToArray() }
}

function Restore-InstallerSettingsSnapshot
{
    param([Parameter(Mandatory=$true)]$Snapshot)

    try
    {
        foreach ($file in $Snapshot.Files)
        {
            $destination = Join-Path $Snapshot.SettingsDir $file
            if ($file -in $Snapshot.ExistingFiles)
            {
                $backup = Join-Path (Join-Path $Snapshot.BackupRoot "settings-backup") $file
                if (-not (Test-Path -LiteralPath $backup -PathType Leaf)) { throw "Settings backup is missing: $backup" }
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                Copy-Item -LiteralPath $backup -Destination $destination -Force -ErrorAction Stop
            }
            elseif (Test-Path -LiteralPath $destination)
            {
                if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw "Installer settings path is not a file: $destination" }
                Remove-Item -LiteralPath $destination -Force -ErrorAction Stop
            }
        }
    }
    catch
    {
        $failure = [InvalidOperationException]::new("Could not restore installer settings. Backup retained at $($Snapshot.BackupRoot). $($_.Exception.Message)", $_.Exception)
        $failure.Data['TlbxPreserveBackupRoot'] = $Snapshot.BackupRoot
        throw $failure
    }
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
    $serviceSnapshot = $null
    $settingsSnapshot = $null
    $settingsSnapshotCreated = $false
    $targetStopped = $false
    $userSnapshot = $null
    $startedUserProcess = $null
    trap
    {
        $installError = $_
        Write-Log "Installation failed: $($installError.Exception.Message)" "ERROR"
        $preserveBackup = [bool]$installError.Exception.Data['TlbxPreserveBackupRoot']
        if ($startedUserProcess -and -not $startedUserProcess.HasExited)
        {
            Stop-Process -Id $startedUserProcess.Id -Force -ErrorAction SilentlyContinue
            $null = $startedUserProcess.WaitForExit(5000)
        }
        if ($binarySwapCompleted -and -not $installationCompleted -and $installDir -and $tempRoot)
        {
            Write-Host "  Installation failed; restoring the previous tlbx binary set..." -ForegroundColor Yellow
            try
            {
                if ($AsService)
                {
                    Stop-ExistingInstallProcesses -InstallDir $installDir
                }
                Restore-PreviousBinarySet -InstallDir $installDir -BackupRoot $tempRoot
                if ($settingsSnapshotCreated) { Restore-InstallerSettingsSnapshot -Snapshot $settingsSnapshot }
                if ($AsService -and $serviceExistedBefore)
                {
                    Restore-InstallerServiceSnapshot -Snapshot $serviceSnapshot
                }
                elseif ($AsService -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue))
                {
                    sc.exe delete $ServiceName | Out-Null
                }
                if (-not $AsService -and $userSnapshot -and $userSnapshot.WasRunning)
                {
                    $null = Start-TlbxPreviousUserProcess -ExecutablePath (Join-Path $installDir $WebBinaryName) -InstallDir $installDir -SettingsDir $WIN_USER_SETTINGS_DIR
                }
                Write-Log "Previous binary set restored after installation failure" "WARN"
            }
            catch
            {
                $preserveBackup = $true
                Write-Log "Rollback failed: $_" "ERROR"
                Write-Host "  Automatic rollback also failed. Backup retained at $tempRoot. See $script:UpdateLogFile." -ForegroundColor Red
            }
        }
        if (-not $binarySwapCompleted -and $targetStopped -and $serviceSnapshot -and -not $preserveBackup)
        {
            try { Restore-InstallerServiceSnapshot -Snapshot $serviceSnapshot }
            catch
            {
                $preserveBackup = $true
                Write-Log "Could not restart the unchanged previous service: $_" "ERROR"
            }
        }
        if (-not $binarySwapCompleted -and $userSnapshot -and $userSnapshot.WasRunning -and -not $preserveBackup)
        {
            try { $null = Start-TlbxPreviousUserProcess -ExecutablePath (Join-Path $installDir $WebBinaryName) -InstallDir $installDir -SettingsDir $WIN_USER_SETTINGS_DIR }
            catch { $preserveBackup = $true; Write-Log "Could not restart the unchanged previous user installation: $_" "ERROR" }
        }
        if ($preserveBackup)
        {
            Write-Host "  Recovery files retained at $tempRoot." -ForegroundColor Yellow
        }
        elseif ($tempRoot -and (Test-Path -LiteralPath $tempRoot))
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
        Assert-InstallerUserLaunchPaths -InstallDir $installDir -SettingsDir $WIN_USER_SETTINGS_DIR
    }

    $targetService = if ($AsService) { $ServiceName } else { "" }
    Assert-InstallerCanStopTarget -InstallDir $installDir -TargetServiceName $targetService

    # Download and extract
    $tempRoot = Join-Path $script:InstallerTempRoot ("tlbx-install-" + [Guid]::NewGuid().ToString("N"))
    $tempZip = Join-Path $tempRoot "release.zip"
    $tempExtract = Join-Path $tempRoot "extract"
    New-Item -ItemType Directory -Path $tempExtract -Force | Out-Null

    Write-Log "=== PHASE 1: Downloading binaries ==="
    Write-Host "Downloading..." -ForegroundColor Gray
    $assetUrl = Get-AssetUrl -Release $script:release
    Write-Log "Downloading from: $assetUrl"
    Invoke-CompatibleWebRequest -Uri $assetUrl -OutFile $tempZip
    Assert-BuildArchive -Path $tempZip -Release $script:release
    Unblock-DownloadedPath -Path $tempZip
    Write-Log "Download complete"

    Write-Host "Extracting..." -ForegroundColor Gray
    Write-Log "Extracting to: $tempExtract"
    Assert-SafeReleaseArchive -Path $tempZip
    Expand-Archive -LiteralPath $tempZip -DestinationPath $tempExtract
    Assert-SignedRelease -ExtractDir $tempExtract -ExpectedVersion $version -ExpectedPlatform $ExpectedReleasePlatform -ExpectedChannel (Get-ReleaseChannel -Release $script:release)
    Unblock-DownloadedTree -Path $tempExtract
    Write-Log "Extraction and release-signature verification complete"
    Assert-VerifiedWindowsRuntime -ExtractDir $tempExtract -ExpectedVersion $version
    Assert-InstallerPortAvailable -Port $Port -BindAddress $BindAddress -InstallDir $installDir

    # Do not interrupt a working installation until the complete download has
    # passed archive, metadata-signature, and checksum verification.
    if ($AsService)
    {
        $serviceExistedBefore = [bool](Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
        if ($serviceExistedBefore) { $serviceSnapshot = Get-InstallerServiceSnapshot -Name $ServiceName }
        Stop-ExistingInstallProcesses -InstallDir $installDir
        $targetStopped = $true
    }
    else
    {
        $userSnapshot = Stop-InstallerUserProcesses -InstallDir $installDir
    }
    if (-not (Test-Path -LiteralPath $installDir))
    {
        New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    }

    $settingsDir = if ($AsService) { $WIN_SERVICE_SETTINGS_DIR } else { $WIN_USER_SETTINGS_DIR }
    $settingsSnapshot = Get-InstallerSettingsSnapshot -SettingsDir $settingsDir -BackupRoot $tempRoot
    $settingsSnapshotCreated = $true

    Write-Log "=== PHASE 2: Installing binaries ==="
    $destWebBinary = Join-Path $installDir $WebBinaryName

    Write-Host "Installing binaries to $installDir..." -ForegroundColor Gray
    Write-Log "Installing binaries to $installDir"
    Install-VerifiedBinarySet -ExtractDir $tempExtract -InstallDir $installDir -BackupRoot $tempRoot
    $binarySwapCompleted = $true

    Write-Log "=== PHASE 3: Password configuration ==="
    Write-Log "=== PHASE 4: Certificate configuration ==="
    # Always generate certificate now that mt.exe is installed (always HTTPS)
    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    $settingsDir = if ($AsService) { $WIN_SERVICE_SETTINGS_DIR } else { $WIN_USER_SETTINGS_DIR }
    Write-Log "Settings directory: $settingsDir"
    $CertPath = Generate-Certificate -InstallDir $installDir -SettingsDir $settingsDir -IsService $AsService
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
        if (-not (Test-Path -LiteralPath $userSettingsDir)) { New-Item -ItemType Directory -Path $userSettingsDir -Force | Out-Null }

        $runtimeSettings = @{
            port = $Port
            bindAddress = if ($BindAddress -eq "localhost") { "127.0.0.1" } else { "0.0.0.0" }
        }
        $runtimeSettings | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $userSettingsDir "runtime.json") -Encoding UTF8

        # Build install-time settings
        $userSettings = @{
            authenticationEnabled = $true
            isServiceInstall = $false
        }
        if ($CertPath) {
            $userSettings.certificatePath = $CertPath
            $userSettings.keyProtection = "osProtected"
        }

        if (Test-Path -LiteralPath $userSettingsPath)
        {
            # Reinstall: write merge file, let mt handle merging
            $userSettings | ConvertTo-Json | Set-Content -LiteralPath $userMergePath -Encoding UTF8
            Write-Host "  Settings: merge file written for mt" -ForegroundColor Gray
        }
        else
        {
            # Fresh install: write settings.json directly
            $userSettings | ConvertTo-Json | Set-Content -LiteralPath $userSettingsPath -Encoding UTF8
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
                if ($LASTEXITCODE -ne 0) { throw "Native secret storage failed (exit $LASTEXITCODE)." }
                Write-Host "  Password: stored in secure storage ($userSettingsDir\secrets.bin)" -ForegroundColor Gray
            }
            catch
            {
                throw "Failed to store password in secure storage at $userSettingsDir\secrets.bin. Installation aborted to avoid an insecure state. $_"
            }
        }

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

    if ($AsService)
    {
        Register-Uninstall -InstallDir $installDir -Version $Version -IsService $true
        Create-UninstallScript -InstallDir $installDir -IsService $true
    }
    else { Install-AsUserApp -InstallDir $installDir -Version $Version -Port $Port -BindAddress $BindAddress }
    if ($TrustCert -and $CertPath) { Trust-InstallerCertificate -CertPath $CertPath }
    if ($AsService -and $ConfigureFirewall -and (Test-NetworkBinding -BindAddress $BindAddress))
    {
        Ensure-FirewallRule -Port $Port -InstallDir $installDir
    }

    # Remove the obsolete split-host binary only after the complete install has
    # succeeded so rollback never destroys a still-working legacy deployment.
    $legacyHostPath = Join-Path $installDir $LegacyHostBinaryName
    if (Test-Path -LiteralPath $legacyHostPath)
    {
        Remove-Item -LiteralPath $legacyHostPath -Force -ErrorAction SilentlyContinue
        Write-Host "  Removed legacy: $LegacyHostBinaryName" -ForegroundColor Gray
    }

    $installationCompleted = $true
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue

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

    function Get-TlbxServiceDiagnostics
    {
        $details = New-Object System.Collections.Generic.List[string]

        try
        {
            if (Test-Path -LiteralPath $webBinaryPath)
            {
                $exe = Get-Item -LiteralPath $webBinaryPath -ErrorAction Stop
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

    Assert-InstallerCanStopTarget -InstallDir $InstallDir -TargetServiceName $ServiceName

    # Keep an existing SCM entry intact so failed configuration can restart
    # the previous binary set during rollback.
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existingService)
    {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        Get-InstallOwnedProcesses -InstallDir $InstallDir | Stop-Process -Force -ErrorAction SilentlyContinue
        for ($i = 0; $i -lt 10; $i++)
        {
            if (-not (Get-InstallOwnedProcesses -InstallDir $InstallDir)) { break }
            Start-Sleep -Milliseconds 500
        }
    }

    # Convert bind address for command line
    $bindArg = if ((Normalize-InstallerBindAddress $BindAddress) -eq "localhost") { "127.0.0.1" } else { "0.0.0.0" }

    # Create service - mt.exe spawns mthost per terminal session
    Write-Log "Creating Windows service..."
    Write-Host "Creating $DisplayName service..." -ForegroundColor Gray
    $binPath = "`"$webBinaryPath`" --service-mode --settings-dir `"$WIN_SERVICE_SETTINGS_DIR`" --service-name `"$ServiceName`" --port $Port --bind $bindArg"
    Write-Log "Service binPath: $binPath"
    # Use typed service APIs: native argument reparsing in Windows PowerShell
    # otherwise loses the embedded quotes in this logical SCM command line.
    if ($existingService)
    {
        $escapedName = $ServiceName.Replace("'", "''")
        $serviceInfo = Get-CimInstance -ClassName Win32_Service -Filter "Name='$escapedName'" -ErrorAction Stop
        if (-not $serviceInfo) { throw "Existing service '$ServiceName' could not be read for configuration." }
        $changed = Invoke-CimMethod -InputObject $serviceInfo -MethodName Change -Arguments @{
            PathName = $binPath
            DisplayName = $DisplayName
            StartMode = "Automatic"
        } -ErrorAction Stop
        if ($changed.ReturnValue -ne 0) { throw "Could not configure Windows service '$ServiceName' (SCM result $($changed.ReturnValue))." }
    }
    else
    {
        New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName $DisplayName -StartupType Automatic -ErrorAction Stop | Out-Null
    }
    sc.exe description $ServiceName "Web-based terminal multiplexer for AI coding agents and TUI apps" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not configure service description." }
    sc.exe failure $ServiceName reset= 86400 actions= restart/1000/restart/5000/restart/30000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not configure service crash recovery." }
    sc.exe failureflag $ServiceName 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not enable service failure recovery." }

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

    $bindArg = if ((Normalize-InstallerBindAddress $BindAddress) -eq "localhost") { "127.0.0.1" } else { "0.0.0.0" }
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

    if (-not (Test-Path -LiteralPath $regPath))
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

    Set-Content -LiteralPath $uninstallScript -Value $content
}

function Save-ElevatedInstallerFailureLog
{
    param([Parameter(Mandatory=$true)][string]$LogPath)

    $logDirectory = Join-Path $script:InstallerPaths.LocalAppData "tlbx\logs"
    New-Item -ItemType Directory -Path $logDirectory -Force -ErrorAction Stop | Out-Null
    $savedPath = Join-Path $logDirectory ("install-elevated-failure-" + [Guid]::NewGuid().ToString("N") + ".log")
    Copy-Item -LiteralPath $LogPath -Destination $savedPath -ErrorAction Stop
    return $savedPath
}

function Read-ElevatedInstallerLog
{
    param([Parameter(Mandatory=$true)][string]$LogPath)

    # The child keeps its writer open. Both handles must explicitly permit
    # concurrent reading/writing, including on Windows PowerShell 5.1.
    $stream = [IO.File]::Open($LogPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
    try
    {
        while (-not $reader.EndOfStream) { $reader.ReadLine() }
    }
    finally { $reader.Dispose() }
}

function Invoke-ElevatedInstaller
{
    param(
        [Parameter(Mandatory=$true)][string]$PowerShellPath,
        [Parameter(Mandatory=$true)][string[]]$Arguments,
        [Parameter(Mandatory=$true)][string]$HandoffDirectory,
        [Parameter(Mandatory=$true)][string]$LogPath
    )

    $HandoffDirectory = [IO.Path]::GetFullPath($HandoffDirectory)
    $LogPath = [IO.Path]::GetFullPath($LogPath)
    $handoffPrefix = $HandoffDirectory.TrimEnd('\') + '\'
    if (-not $LogPath.StartsWith($handoffPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        ((Get-Item -LiteralPath $HandoffDirectory -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint))
    {
        throw "Invalid elevated installer handoff directory or log path."
    }

    # Only Start-Process errors mean UAC/launch failed. A started installer has
    # its own failure, exit status and diagnostics; never relabel it as UAC.
    try
    {
        $child = Start-Process -FilePath $PowerShellPath -ArgumentList (Join-ProcessArguments -Arguments $Arguments) -Verb RunAs -WindowStyle Hidden -PassThru -ErrorAction Stop
    }
    catch
    {
        $launchFailure = "Could not start the administrator installer: $($_.Exception.Message). Run from an external administrator PowerShell window, or choose a user install."
        $savedLog = $LogPath
        try
        {
            Add-Content -LiteralPath $LogPath -Value $launchFailure -Encoding UTF8 -ErrorAction Stop
            $savedLog = Save-ElevatedInstallerFailureLog -LogPath $LogPath
        }
        catch { } # Preserve the private original log if copying diagnostics failed.
        Remove-Item -LiteralPath (Join-Path $HandoffDirectory "mt-install-replay.json") -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $HandoffDirectory "mt-install-elevated.ps1") -Force -ErrorAction SilentlyContinue
        if ($savedLog -ne $LogPath)
        {
            Remove-Item -LiteralPath $HandoffDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-Host "  Installer diagnostics: $savedLog" -ForegroundColor Yellow
        throw "$launchFailure Installer diagnostics: $savedLog"
    }

    $completed = $false
    $savedLog = $null
    try
    {
        # Keep the real process handle before polling: Windows PowerShell 5.1
        # can otherwise lose ExitCode when a fast child has already exited.
        $null = $child.Handle
        $linesRead = 0
        do
        {
            $lines = @(Read-ElevatedInstallerLog -LogPath $LogPath)
            if ($lines.Count -gt $linesRead)
            {
                $lines[$linesRead..($lines.Count - 1)] | ForEach-Object { Write-Host $_ }
                $linesRead = $lines.Count
            }
            if ($child.HasExited) { break }
            Start-Sleep -Milliseconds 200
        } while ($true)
        $child.WaitForExit()
        # Drain the last lines after the child has closed its log handle.
        $lines = @(Read-ElevatedInstallerLog -LogPath $LogPath)
        if ($lines.Count -gt $linesRead)
        {
            $lines[$linesRead..($lines.Count - 1)] | ForEach-Object { Write-Host $_ }
        }
        if ($child.ExitCode -ne 0)
        {
            throw "Elevated installation failed (exit code $($child.ExitCode))."
        }
        $completed = $true
    }
    catch
    {
        $failure = $_
        try { $savedLog = Save-ElevatedInstallerFailureLog -LogPath $LogPath }
        catch { $savedLog = $LogPath }
        Write-Host "  Installer diagnostics: $savedLog" -ForegroundColor Yellow
        throw "$($failure.Exception.Message) Installer diagnostics: $savedLog"
    }
    finally
    {
        # Never retain the password-hash replay file after the child is done.
        if ($child.HasExited)
        {
            Remove-Item -LiteralPath (Join-Path $HandoffDirectory "mt-install-replay.json") -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath (Join-Path $HandoffDirectory "mt-install-elevated.ps1") -Force -ErrorAction SilentlyContinue
            if ($completed -or ($savedLog -and $savedLog -ne $LogPath))
            {
                Remove-Item -LiteralPath $HandoffDirectory -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        $child.Dispose()
    }
}

# Main

# If we're being called with ServiceMode flag, we're the elevated process (runs hidden)
if ($ServiceMode)
{
    try
    {
        if ($ReplayFile)
        {
            Import-ElevatedReplayFile -Path $ReplayFile
        }

        $validatedOptions = Assert-InstallerOptions -Port $Port -BindAddress $BindAddress
        $script:Port = $validatedOptions.Port
        $script:BindAddress = $validatedOptions.BindAddress
        Select-InstallIdentity -AsService $true

        # If log file specified, redirect all output there for streaming to original terminal
        if ($LogFile)
        {
            # One writer owns the log for the whole pipeline. Reopening it for
            # each record races with the parent's polling reader in PS 5.1.
            $logStream = [IO.File]::Open($LogFile, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
            $logWriter = [IO.StreamWriter]::new($logStream, [Text.UTF8Encoding]::new($false))
            $logWriter.AutoFlush = $true
            try
            {
                & {
                    Write-Host ""
                    Write-Host "  Running with administrator privileges..." -ForegroundColor Cyan
                    Write-Host ""
                    $script:release = Get-LatestRelease -DevChannel $Dev
                    $version = $script:release.tag_name -replace "^v", ""
                    $channelLabel = Get-ReleaseChannel -Release $script:release
                    Write-Host "  Latest $channelLabel version: $version" -ForegroundColor White
                    Write-Host ""
                    Install-Tlbx -AsService $true -Version $version -RunAsUser $RunAsUser -RunAsUserSid $RunAsUserSid -PasswordHash $PasswordHash -Port $Port -BindAddress $BindAddress -ConfigureFirewall:$ConfigureFirewall -TrustCert:$TrustCert
                } *>&1 | ForEach-Object {
                    $line = $_.ToString()
                    Write-Host $_
                    $logWriter.WriteLine($line)
                }
            }
            finally { $logWriter.Dispose() }
        }
        else
        {
            Write-Host ""
            Write-Host "  Running with administrator privileges..." -ForegroundColor Cyan
            Write-Host ""
            $script:release = Get-LatestRelease -DevChannel $Dev
            $version = $script:release.tag_name -replace "^v", ""
            $channelLabel = Get-ReleaseChannel -Release $script:release
            Write-Host "  Latest $channelLabel version: $version" -ForegroundColor White
            Write-Host ""
            Install-Tlbx -AsService $true -Version $version -RunAsUser $RunAsUser -RunAsUserSid $RunAsUserSid -PasswordHash $PasswordHash -Port $Port -BindAddress $BindAddress -ConfigureFirewall:$ConfigureFirewall -TrustCert:$TrustCert
        }
    }
    catch
    {
        if ($LogFile)
        {
            Add-Content -LiteralPath $LogFile -Value ("ERROR: " + $_.ToString()) -Encoding UTF8 -ErrorAction SilentlyContinue
        }
        throw
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
$channelLabel = Get-ReleaseChannel -Release $script:release

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

$asService = (Read-InstallerChoice -Prompt "  Your choice [1/2]" -Choices @('1','2') -DefaultValue '1') -eq '1'

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
            $passwordHash = Prompt-Password
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
        $passwordHash = Prompt-Password
    }

    # Prompt for network configuration
    $networkConfig = Prompt-NetworkConfig -InstallDir $WIN_SERVICE_INSTALL_DIR
    $port = $networkConfig.Port
    $bindAddress = $networkConfig.BindAddress

    # Ask about certificate trust BEFORE elevation (all interactive prompts in original terminal)
    Write-Host ""
    Write-Host "  Certificate Trust:" -ForegroundColor Cyan
    Write-Host "  Trust the certificate to remove browser warnings?" -ForegroundColor Yellow
    Write-Host "  (Adds self-signed certificate to Windows trusted root store)" -ForegroundColor Gray
    $trustCert = Read-InstallerYesNo -Prompt "  Trust certificate? [Y/n]" -DefaultValue $true
    $configureFirewall = Prompt-FirewallConfig -BindAddress $bindAddress -Port $port

    Assert-InstallerCanStopTarget -InstallDir $WIN_SERVICE_INSTALL_DIR -TargetServiceName $ServiceName

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

        [IO.File]::WriteAllText($tempScript, (Get-CurrentInstallerScriptContent), [Text.UTF8Encoding]::new($true))
        "" | Set-Content -LiteralPath $tempLogFile -Encoding UTF8 -Force

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
        $replay | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $replayFile -Encoding UTF8 -Force

        $runAsArguments = @(
            "-NoProfile"
            "-ExecutionPolicy", "Bypass"
            "-File", $tempScript
            "-ServiceMode"
            "-ReplayFile", $replayFile
            "-LogFile", $tempLogFile
        )

        Invoke-ElevatedInstaller -PowerShellPath $psExe -Arguments $runAsArguments -HandoffDirectory $handoffDir -LogPath $tempLogFile

        return
    }

    # Already admin, proceed with install
    Install-Tlbx -AsService $true -Version $version -RunAsUser $currentUser.Name -RunAsUserSid $currentUser.Sid -PasswordHash $passwordHash -Port $port -BindAddress $bindAddress -ConfigureFirewall:$configureFirewall -TrustCert $trustCert
}
else
{
    Assert-NoCrossModeConflict -AsService $false
    Select-InstallIdentity -AsService $false
    Assert-InstallerUserLaunchPaths -InstallDir $WIN_USER_INSTALL_DIR -SettingsDir $WIN_USER_SETTINGS_DIR

    # User install - still require password
    # Uses PATH_CONSTANTS defined above - keep in sync with SettingsService.cs!
    $userSettingsDir = $WIN_USER_SETTINGS_DIR
    $userSecretsPath = Join-Path $userSettingsDir $WIN_SECRETS_FILENAME

    # Check for existing password in secure storage so user-mode reinstalls keep
    # the current auth state unless the user explicitly chooses Replace.
    $hasExistingPassword = $false
    if (Test-Path -LiteralPath $userSecretsPath)
    {
        try
        {
            $secrets = Get-Content -LiteralPath $userSecretsPath -Raw | ConvertFrom-Json
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
            $tempDir = Join-Path $script:InstallerTempRoot "tlbx-install"
            $passwordHash = Prompt-Password
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
        # Hash locally before download or elevation; only the hash is replayed.
        $tempDir = Join-Path $script:InstallerTempRoot "tlbx-install"
        $passwordHash = Prompt-Password
    }

    # Prompt for network configuration
    $networkConfig = Prompt-NetworkConfig -InstallDir $WIN_USER_INSTALL_DIR

    Install-Tlbx -AsService $false -Version $version -RunAsUser "" -RunAsUserSid "" -PasswordHash $passwordHash -Port $networkConfig.Port -BindAddress $networkConfig.BindAddress
}
