using System.Reflection;
using System.Globalization;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Startup;

using Ai.Tlbx.MidTerm.Services.Certificates;
namespace Ai.Tlbx.MidTerm.Services.Updates;

/// <summary>
/// Generates update scripts for Windows and Linux.
/// macOS uses the launcher shim approach instead (see EndpointSetup.cs).
/// Scripts include: process termination, file lock waiting,
/// copy verification, rollback on failure, and detailed logging.
///
/// SYNC: Generated scripts use paths that MUST match:
///   - SettingsService.cs (GetSettingsPath method)
///   - LogPaths.cs (constants and GetSettingsDirectory method)
///   - install.sh (PATH_CONSTANTS section)
///   - install.ps1 (Path Constants section)
/// </summary>
public static class UpdateScriptGenerator
{
    private const string AgentHostBinaryName = "mtagenthost";
    private const string TmuxShimBinaryName = "mttmux";
    private const int MaxRetries = 30;
    private const int RetryDelaySeconds = 1;

    public static string GenerateUpdateScript(
        string extractedDir,
        string currentBinaryPath,
        string settingsDirectory,
        UpdateType updateType = UpdateType.Full,
        bool deleteSourceAfter = true)
    {
        return GenerateUpdateScript(
            extractedDir,
            currentBinaryPath,
            settingsDirectory,
            MidTermServiceIdentity.FromEnvironment(),
            updateType,
            deleteSourceAfter);
    }

    public static string GenerateUpdateScript(
        string extractedDir,
        string currentBinaryPath,
        string settingsDirectory,
        MidTermServiceIdentity serviceIdentity,
        UpdateType updateType = UpdateType.Full,
        bool deleteSourceAfter = true)
    {
        if (OperatingSystem.IsWindows())
        {
            return GenerateWindowsScript(extractedDir, currentBinaryPath, settingsDirectory, serviceIdentity, updateType, deleteSourceAfter);
        }

        return GenerateUnixScript(extractedDir, currentBinaryPath, settingsDirectory, serviceIdentity, updateType, deleteSourceAfter);
    }

    private static string GenerateWindowsScript(string extractedDir, string currentBinaryPath, string settingsDirectory, MidTermServiceIdentity serviceIdentity, UpdateType updateType, bool deleteSourceAfter)
    {
        // IMPORTANT: Binary dir != Settings dir on Windows
        // Binaries: C:\Program Files\MidTerm (installDir)
        // Settings: C:\ProgramData\MidTerm (settingsDir) for service mode, or user profile for user mode
        //
        // SYNC: These paths MUST match:
        //   - install.ps1 (Path Constants section)
        //   - SettingsService.cs (GetSettingsPath method)
        //   - LogPaths.cs (GetSettingsDirectory method)
        var installDir = Path.GetDirectoryName(currentBinaryPath) ?? currentBinaryPath;
        var settingsDir = settingsDirectory;
        var newMtPath = Path.Combine(extractedDir, "mt.exe");
        var newMthostPath = Path.Combine(extractedDir, "mthost.exe");
        var newAgentHostPath = Path.Combine(extractedDir, $"{AgentHostBinaryName}.exe");
        var newTmuxShimPath = Path.Combine(extractedDir, $"{TmuxShimBinaryName}.exe");
        var newVersionJsonPath = Path.Combine(extractedDir, "version.json");
        var currentMthostPath = Path.Combine(installDir, "mthost.exe");
        var currentAgentHostPath = Path.Combine(installDir, $"{AgentHostBinaryName}.exe");
        var currentTmuxShimPath = Path.Combine(installDir, $"{TmuxShimBinaryName}.exe");
        var currentVersionJsonPath = Path.Combine(installDir, "version.json");
        // Log and result files go in settings directory so they're accessible after update
        var resultFilePath = Path.Combine(settingsDir, "update-result.json");
        var logFilePath = Path.Combine(settingsDir, "update.log");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"mt-update-{Guid.NewGuid():N}.ps1");
        var serviceName = EscapeForPowerShell(serviceIdentity.WindowsServiceName);

        var isWebOnly = updateType != UpdateType.Full;

        var script = $@"
# MidTerm Update Script (Windows)
# Type: {(isWebOnly ? "Web-only (sessions preserved)" : "Full (sessions will restart)")}
# Generated: {DateTime.UtcNow:O}
#
# IMPORTANT: InstallDir (binaries) != SettingsDir (config/certs)
# - InstallDir: C:\Program Files\MidTerm (or user install location)
# - SettingsDir: C:\ProgramData\MidTerm (service) or %APPDATA%\MidTerm (user)

$ErrorActionPreference = 'Stop'

# === Configuration ===
# IMPORTANT: These directories are DIFFERENT - don't confuse them!
$InstallDir = '{EscapeForPowerShell(installDir)}'           # Binaries: mt.exe, mthost.exe, {AgentHostBinaryName}.exe
$SettingsDir = '{EscapeForPowerShell(settingsDir)}'         # Settings, secrets, certs
$CurrentMt = '{EscapeForPowerShell(currentBinaryPath)}'
$CurrentMthost = '{EscapeForPowerShell(currentMthostPath)}'
$CurrentAgentHost = '{EscapeForPowerShell(currentAgentHostPath)}'
$CurrentTmuxShim = '{EscapeForPowerShell(currentTmuxShimPath)}'
$CurrentVersionJson = '{EscapeForPowerShell(currentVersionJsonPath)}'
$NewMt = '{EscapeForPowerShell(newMtPath)}'
$NewMthost = '{EscapeForPowerShell(newMthostPath)}'
$NewAgentHost = '{EscapeForPowerShell(newAgentHostPath)}'
$NewTmuxShim = '{EscapeForPowerShell(newTmuxShimPath)}'
$NewVersionJson = '{EscapeForPowerShell(newVersionJsonPath)}'
$ExtractedDir = '{EscapeForPowerShell(extractedDir)}'
$LogFile = '{EscapeForPowerShell(logFilePath)}'
$ResultFile = '{EscapeForPowerShell(resultFilePath)}'
$MaxRetries = {MaxRetries}
$IsWebOnly = ${(isWebOnly ? "true" : "false")}
$DeleteSource = ${(deleteSourceAfter ? "true" : "false")}
$ServiceName = '{serviceName}'

# === Helper Functions ===

function Log {{
    param([string]$Message, [string]$Level = 'INFO')
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'
    $line = ""[$timestamp] [$Level] $Message""
    Write-Host $line
    try {{ Add-Content -Path $LogFile -Value $line -ErrorAction SilentlyContinue }} catch {{}}
}}

function WriteResult {{
    param([bool]$Success, [string]$Message, [string]$Details = '')
    $result = @{{
        success = $Success
        message = $Message
        details = $Details
        timestamp = (Get-Date -Format 'o')
        logFile = $LogFile
    }}
    try {{
        $result | ConvertTo-Json -Depth 3 | Set-Content -Path $ResultFile -Encoding UTF8
    }} catch {{
        Log ""Failed to write result file: $_"" 'ERROR'
    }}
}}

function WaitForFileWritable {{
    param([string]$Path, [int]$Retries = $MaxRetries)

    for ($i = 1; $i -le $Retries; $i++) {{
        if (-not (Test-Path $Path)) {{
            Log ""File does not exist (OK): $Path""
            return $true
        }}

        try {{
            $stream = [System.IO.File]::Open($Path, 'Open', 'ReadWrite', 'None')
            $stream.Close()
            $stream.Dispose()
            Log ""File is writable: $Path""
            return $true
        }} catch {{
            Log ""File locked (attempt $i/$Retries): $Path"" 'WARN'
            if ($i -lt $Retries) {{
                Start-Sleep -Seconds {RetryDelaySeconds}
            }}
        }}
    }}

    Log ""File still locked after $Retries attempts: $Path"" 'ERROR'
    return $false
}}

function KillProcessByPath {{
    param([string]$FullPath)

    $Name = [System.IO.Path]::GetFileNameWithoutExtension($FullPath)
    $procs = Get-Process -Name $Name -ErrorAction SilentlyContinue | Where-Object {{
        try {{ $_.Path -eq $FullPath }} catch {{ $false }}
    }}
    if ($procs) {{
        foreach ($proc in $procs) {{
            Log ""Killing $Name (PID: $($proc.Id), Path: $FullPath)...""
            try {{
                $proc.Kill()
                $proc.WaitForExit(5000)
            }} catch {{
                Log ""Failed to kill $Name (PID: $($proc.Id)): $_"" 'WARN'
            }}
        }}
        Start-Sleep -Milliseconds 500
    }}

    # Double-check
    $remaining = Get-Process -Name $Name -ErrorAction SilentlyContinue | Where-Object {{
        try {{ $_.Path -eq $FullPath }} catch {{ $false }}
    }}
    if ($remaining) {{
        foreach ($proc in $remaining) {{
            Log ""Force killing $Name (PID: $($proc.Id))..."" 'WARN'
            try {{ $proc.Kill() }} catch {{}}
        }}
        Start-Sleep -Seconds 1
    }}
}}

function VerifyCopy {{
    param([string]$Source, [string]$Dest)

    if (-not (Test-Path $Dest)) {{
        throw ""Copy verification failed: destination does not exist: $Dest""
    }}

    $srcSize = (Get-Item $Source).Length
    $dstSize = (Get-Item $Dest).Length

    if ($srcSize -ne $dstSize) {{
        throw ""Copy verification failed: size mismatch for $Dest (expected $srcSize bytes, got $dstSize bytes)""
    }}

    Log ""Verified: $Dest ($dstSize bytes)""
}}

function SafeCopy {{
    param([string]$Source, [string]$Dest, [string]$Description)

    Log ""Copying $Description...""
    Log ""  From: $Source""
    Log ""  To: $Dest""

    if (-not (Test-Path $Source)) {{
        throw ""Source file does not exist: $Source""
    }}

    Copy-Item -Path $Source -Destination $Dest -Force -ErrorAction Stop
    VerifyCopy $Source $Dest

    Log ""$Description copied successfully""
}}

# === Main Script ===

# Clear previous logs
Remove-Item $LogFile -Force -ErrorAction SilentlyContinue
Remove-Item $ResultFile -Force -ErrorAction SilentlyContinue

Log '=========================================='
Log 'MidTerm Update Script Starting'
Log ""Update type: $(if ($IsWebOnly) {{ 'Web-only' }} else {{ 'Full' }})""
Log '=========================================='

# Log version before update
if (Test-Path $CurrentVersionJson) {{
    try {{
        $vj = Get-Content $CurrentVersionJson -Raw | ConvertFrom-Json
        Log ""Version before update: web=$($vj.web), pty=$($vj.pty)""
    }} catch {{
        Log 'Could not read current version.json' 'WARN'
    }}
}} else {{
    Log 'No version.json found (fresh install?)' 'WARN'
}}

$rollbackNeeded = $false
$startedOk = $false

try {{
    # ============================================
    # PHASE 1: Stop all processes
    # ============================================
    Log ''
    Log '=== PHASE 1: Stopping processes ==='

    # Stop Windows service if running
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -eq 'Running') {{
        Log ""Stopping MidTerm service '$ServiceName'...""
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2

        # Verify service stopped
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq 'Running') {{
            Log 'Service did not stop gracefully, forcing...' 'WARN'
        }}
    }}

    # Kill mt.exe processes (by full path to avoid killing unrelated processes)
    Log 'Killing mt.exe processes...'
    KillProcessByPath $CurrentMt

    # Kill mthost.exe processes (only for full updates)
    if (-not $IsWebOnly) {{
        Log 'Killing mthost.exe processes...'
        KillProcessByPath $CurrentMthost

        if (Test-Path $CurrentAgentHost) {{
            Log 'Killing {AgentHostBinaryName}.exe processes...'
            KillProcessByPath $CurrentAgentHost
        }}
    }}

    Log 'All processes stopped'

    # ============================================
    # PHASE 2: Wait for file handles to release
    # ============================================
    Log ''
    Log '=== PHASE 2: Waiting for file handles ==='

    if (-not (WaitForFileWritable $CurrentMt)) {{
        throw ""mt.exe is still locked after $MaxRetries retries. Another process may be using it.""
    }}

    if ((-not $IsWebOnly) -and (Test-Path $CurrentMthost)) {{
        if (-not (WaitForFileWritable $CurrentMthost)) {{
            throw ""mthost.exe is still locked after $MaxRetries retries. Another process may be using it.""
        }}
    }}

    if ((-not $IsWebOnly) -and (Test-Path $CurrentAgentHost)) {{
        if (-not (WaitForFileWritable $CurrentAgentHost)) {{
            throw ""{AgentHostBinaryName}.exe is still locked after $MaxRetries retries. Another process may be using it.""
        }}
    }}

    Log 'All file handles released'

    # ============================================
    # PHASE 3: Create backups
    # ============================================
    Log ''
    Log '=== PHASE 3: Creating backups ==='

    if (Test-Path $CurrentMt) {{
        Log 'Backing up mt.exe...'
        Copy-Item $CurrentMt ""$CurrentMt.bak"" -Force -ErrorAction Stop
        Log 'mt.exe backed up'
    }}

    if (Test-Path $CurrentTmuxShim) {{
        Log 'Backing up {TmuxShimBinaryName}.exe...'
        Copy-Item $CurrentTmuxShim ""$CurrentTmuxShim.bak"" -Force -ErrorAction Stop
        Log '{TmuxShimBinaryName}.exe backed up'
    }}

    if ((-not $IsWebOnly) -and (Test-Path $CurrentMthost)) {{
        Log 'Backing up mthost.exe...'
        Copy-Item $CurrentMthost ""$CurrentMthost.bak"" -Force -ErrorAction Stop
        Log 'mthost.exe backed up'
    }}

    if ((-not $IsWebOnly) -and (Test-Path $CurrentAgentHost)) {{
        Log 'Backing up {AgentHostBinaryName}.exe...'
        Copy-Item $CurrentAgentHost ""$CurrentAgentHost.bak"" -Force -ErrorAction Stop
        Log '{AgentHostBinaryName}.exe backed up'
    }}

    if (Test-Path $CurrentVersionJson) {{
        Log 'Backing up version.json...'
        Copy-Item $CurrentVersionJson ""$CurrentVersionJson.bak"" -Force -ErrorAction Stop
        Log 'version.json backed up'
    }}

    # Backup credential files (critical for security persistence)
    # IMPORTANT: These are in SettingsDir, NOT InstallDir!
    # SYNC: secrets.bin on Windows, secrets.json on Unix - must match SettingsService.cs!
    $settingsPath = Join-Path $SettingsDir 'settings.json'
    $secretsPath = Join-Path $SettingsDir 'secrets.bin'
    $certPath = Join-Path $SettingsDir 'midterm.pem'
    $keysDir = Join-Path $SettingsDir 'keys'

    if (Test-Path $settingsPath) {{
        Log 'Backing up settings.json...'
        Copy-Item $settingsPath ""$settingsPath.bak"" -Force -ErrorAction Stop
        Log 'settings.json backed up'
    }}
    if (Test-Path $secretsPath) {{
        Log 'Backing up secrets.bin...'
        Copy-Item $secretsPath ""$secretsPath.bak"" -Force -ErrorAction Stop
        Log 'secrets.bin backed up'
    }}
    if (Test-Path $certPath) {{
        Log 'Backing up midterm.pem...'
        Copy-Item $certPath ""$certPath.bak"" -Force -ErrorAction Stop
        Log 'midterm.pem backed up'
    }}
    if (Test-Path $keysDir) {{
        Log 'Backing up keys directory...'
        Copy-Item $keysDir ""$keysDir.bak"" -Recurse -Force -ErrorAction Stop
        Log 'keys directory backed up'
    }}

    $rollbackNeeded = $true
    Log 'All backups created (including credentials)'

    # === CERTIFICATE DIAGNOSTICS ===
    Log ''
    Log '=== Certificate Diagnostics ==='

    # Check cert file
    if (Test-Path $certPath) {{
        $certInfo = Get-Item $certPath
        Log ""  midterm.pem: Size=$($certInfo.Length) bytes, Modified=$($certInfo.LastWriteTime)""

        # Get cert thumbprint
        try {{
            $content = Get-Content $certPath -Raw
            $base64 = $content -replace ""-----BEGIN CERTIFICATE-----"","""" -replace ""-----END CERTIFICATE-----"","""" -replace ""`n"","""" -replace ""`r"",""""
            $bytes = [Convert]::FromBase64String($base64)
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(,$bytes)
            Log ""  Thumbprint: $($cert.Thumbprint)""
            Log ""  Subject: $($cert.Subject)""
            Log ""  NotAfter: $($cert.NotAfter)""
            Log ""  NotBefore: $($cert.NotBefore)""
        }} catch {{
            Log ""  WARNING: Could not parse certificate: $_"" 'WARN'
        }}
    }} else {{
        Log '  WARNING: midterm.pem does NOT exist!' 'WARN'
    }}

    # Check key file
    $keyFile = Join-Path $keysDir 'midterm.dpapi'
    if (Test-Path $keyFile) {{
        $keyInfo = Get-Item $keyFile
        Log ""  midterm.dpapi: Size=$($keyInfo.Length) bytes, Modified=$($keyInfo.LastWriteTime)""
    }} else {{
        Log '  WARNING: midterm.dpapi does NOT exist!' 'WARN'
    }}

    # Check settings.json for cert config
    if (Test-Path $settingsPath) {{
        try {{
            $settingsJson = Get-Content $settingsPath -Raw | ConvertFrom-Json
            Log ""  settings.certificatePath: $($settingsJson.certificatePath)""
            Log ""  settings.keyProtection: $($settingsJson.keyProtection)""
            Log ""  settings.isServiceInstall: $($settingsJson.isServiceInstall)""
            Log ""  settings.certificateThumbprint: $($settingsJson.certificateThumbprint)""
        }} catch {{
            Log ""  WARNING: Could not parse settings.json: $_"" 'WARN'
        }}
    }}

    # List MidTerm certs in Root store
    Log '  Trusted MidTerm certificates in Root store:'
    try {{
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(""Root"",""LocalMachine"")
        $store.Open(""ReadOnly"")
        $midtermCerts = $store.Certificates | Where-Object {{ $_.Subject -eq ""{CertificateGenerator.CertificateSubject}"" }}
        foreach ($c in $midtermCerts) {{
            Log ""    - $($c.Thumbprint.Substring(0,8))... Expires: $($c.NotAfter)""
        }}
        if ($midtermCerts.Count -eq 0) {{
            Log '    (none found)'
        }}
        $store.Close()
    }} catch {{
        Log ""  WARNING: Could not enumerate Root store: $_"" 'WARN'
    }}

    # ============================================
    # PHASE 4: Install new files
    # ============================================
    Log ''
    Log '=== PHASE 4: Installing new files ==='

    SafeCopy $NewMt $CurrentMt 'mt.exe'

    if (Test-Path $NewTmuxShim) {{
        SafeCopy $NewTmuxShim $CurrentTmuxShim '{TmuxShimBinaryName}.exe'
    }}

    if ((-not $IsWebOnly) -and (Test-Path $NewMthost)) {{
        SafeCopy $NewMthost $CurrentMthost 'mthost.exe'
    }}

    if ((-not $IsWebOnly) -and (Test-Path $NewAgentHost)) {{
        SafeCopy $NewAgentHost $CurrentAgentHost '{AgentHostBinaryName}.exe'
    }}

    if (Test-Path $NewVersionJson) {{
        SafeCopy $NewVersionJson $CurrentVersionJson 'version.json'
    }}

    Log 'All files installed'

    # Settings file fate
    $settingsCheck = Join-Path $SettingsDir 'settings.json'
    if (Test-Path $settingsCheck) {{
        Log 'settings.json: preserved (not modified by update)'
    }} else {{
        Log 'settings.json: not present' 'WARN'
    }}

    # Log version after update
    if (Test-Path $CurrentVersionJson) {{
        try {{
            $vj = Get-Content $CurrentVersionJson -Raw | ConvertFrom-Json
            Log ""Version after update: web=$($vj.web), pty=$($vj.pty)""
        }} catch {{
            Log 'Could not read new version.json' 'WARN'
        }}
    }}

    # ============================================
    # PHASE 5: Start the new version
    # ============================================
    Log ''
    Log '=== PHASE 5: Starting new version ==='

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {{
        Log ""Starting MidTerm service '$ServiceName'...""
        Start-Service -Name $ServiceName -ErrorAction Stop
        Start-Sleep -Seconds 8

        $service = Get-Service -Name $ServiceName
        if ($service.Status -ne 'Running') {{
            throw ""Service failed to start. Status: $($service.Status)""
        }}
        Log ""Service started successfully (Status: $($service.Status))""
        $startedOk = $true
    }} else {{
        Log 'Starting mt.exe directly...'
        $proc = Start-Process -FilePath $CurrentMt -WindowStyle Hidden -PassThru
        Start-Sleep -Seconds 8

        # Verify process is running
        $running = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if (-not $running -or $running.HasExited) {{
            throw 'mt.exe started but exited immediately'
        }}
        Log ""mt.exe started successfully (PID: $($proc.Id))""
        $startedOk = $true
    }}

    # === POST-UPDATE CERTIFICATE VERIFICATION ===
    Log ''
    Log '=== Post-Update Certificate Verification ==='

    # Check if cert file still exists and is valid
    if (Test-Path $certPath) {{
        try {{
            $content = Get-Content $certPath -Raw
            $base64 = $content -replace ""-----BEGIN CERTIFICATE-----"","""" -replace ""-----END CERTIFICATE-----"","""" -replace ""`n"","""" -replace ""`r"",""""
            $bytes = [Convert]::FromBase64String($base64)
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(,$bytes)
            Log ""  Certificate OK: $($cert.Thumbprint.Substring(0,8))... expires $($cert.NotAfter)""
        }} catch {{
            Log ""  WARNING: Certificate verification failed: $_"" 'WARN'
        }}
    }} else {{
        Log '  WARNING: Certificate file missing after update!' 'WARN'
    }}

    # Check if key file still exists
    if (Test-Path $keyFile) {{
        $keyInfo = Get-Item $keyFile
        Log ""  Key file OK: $($keyInfo.Length) bytes""
    }} else {{
        Log '  WARNING: Key file missing after update!' 'WARN'
    }}

    # ============================================
    # PHASE 6: Cleanup
    # ============================================
    Log ''
    Log '=== PHASE 6: Cleanup ==='

    Remove-Item ""$CurrentMt.bak"" -Force -ErrorAction SilentlyContinue
    Remove-Item ""$CurrentMthost.bak"" -Force -ErrorAction SilentlyContinue
    Remove-Item ""$CurrentAgentHost.bak"" -Force -ErrorAction SilentlyContinue
    Remove-Item ""$CurrentVersionJson.bak"" -Force -ErrorAction SilentlyContinue

    # Clean up credential backups (in SettingsDir, not InstallDir!)
    $settingsPath = Join-Path $SettingsDir 'settings.json'
    $secretsPath = Join-Path $SettingsDir 'secrets.bin'
    $certPath = Join-Path $SettingsDir 'midterm.pem'
    $keysDir = Join-Path $SettingsDir 'keys'
    Remove-Item ""$settingsPath.bak"" -Force -ErrorAction SilentlyContinue
    Remove-Item ""$secretsPath.bak"" -Force -ErrorAction SilentlyContinue
    Remove-Item ""$certPath.bak"" -Force -ErrorAction SilentlyContinue
    Remove-Item ""$keysDir.bak"" -Recurse -Force -ErrorAction SilentlyContinue

    if ($DeleteSource) {{
        Remove-Item -Path $ExtractedDir -Recurse -Force -ErrorAction SilentlyContinue
    }}

    Log 'Cleanup complete'

    # ============================================
    # SUCCESS
    # ============================================
    Log ''
    Log '=========================================='
    Log 'UPDATE COMPLETED SUCCESSFULLY'
    Log '=========================================='

    WriteResult $true 'Update completed successfully'

}} catch {{
    $errorMessage = $_.Exception.Message
    Log '' 'ERROR'
    Log '==========================================' 'ERROR'
    Log ""UPDATE FAILED: $errorMessage"" 'ERROR'
    Log '==========================================' 'ERROR'

    if ($rollbackNeeded -and -not $startedOk) {{
        Log ''
        Log '=== ROLLBACK ===' 'WARN'

        # Stop any partially started process
        KillProcessByPath $CurrentMt

        # Restore backups
        if (Test-Path ""$CurrentMt.bak"") {{
            Log 'Restoring mt.exe from backup...'
            try {{
                Copy-Item ""$CurrentMt.bak"" $CurrentMt -Force -ErrorAction Stop
                Log 'mt.exe restored'
            }} catch {{
                Log ""Failed to restore mt.exe: $_"" 'ERROR'
            }}
        }}

        if (Test-Path ""$CurrentTmuxShim.bak"") {{
            Log 'Restoring {TmuxShimBinaryName}.exe from backup...'
            try {{
                Copy-Item ""$CurrentTmuxShim.bak"" $CurrentTmuxShim -Force -ErrorAction Stop
                Log '{TmuxShimBinaryName}.exe restored'
            }} catch {{
                Log ""Failed to restore {TmuxShimBinaryName}.exe: $_"" 'ERROR'
            }}
        }}

        if (Test-Path ""$CurrentMthost.bak"") {{
            Log 'Restoring mthost.exe from backup...'
            try {{
                Copy-Item ""$CurrentMthost.bak"" $CurrentMthost -Force -ErrorAction Stop
                Log 'mthost.exe restored'
            }} catch {{
                Log ""Failed to restore mthost.exe: $_"" 'ERROR'
            }}
        }}

        if (Test-Path ""$CurrentAgentHost.bak"") {{
            Log 'Restoring {AgentHostBinaryName}.exe from backup...'
            try {{
                Copy-Item ""$CurrentAgentHost.bak"" $CurrentAgentHost -Force -ErrorAction Stop
                Log '{AgentHostBinaryName}.exe restored'
            }} catch {{
                Log ""Failed to restore {AgentHostBinaryName}.exe: $_"" 'ERROR'
            }}
        }}

        if (Test-Path ""$CurrentVersionJson.bak"") {{
            Log 'Restoring version.json from backup...'
            try {{
                Copy-Item ""$CurrentVersionJson.bak"" $CurrentVersionJson -Force -ErrorAction Stop
                Log 'version.json restored'
            }} catch {{
                Log ""Failed to restore version.json: $_"" 'ERROR'
            }}
        }}

        # Restore credential files from SettingsDir (not InstallDir!)
        $settingsPath = Join-Path $SettingsDir 'settings.json'
        $secretsPath = Join-Path $SettingsDir 'secrets.bin'
        $certPath = Join-Path $SettingsDir 'midterm.pem'
        $keysDir = Join-Path $SettingsDir 'keys'

        if (Test-Path ""$settingsPath.bak"") {{
            Log 'Restoring settings.json from backup...'
            try {{
                Copy-Item ""$settingsPath.bak"" $settingsPath -Force -ErrorAction Stop
                Log 'settings.json restored'
            }} catch {{
                Log ""Failed to restore settings.json: $_"" 'ERROR'
            }}
        }}
        if (Test-Path ""$secretsPath.bak"") {{
            Log 'Restoring secrets.bin from backup...'
            try {{
                Copy-Item ""$secretsPath.bak"" $secretsPath -Force -ErrorAction Stop
                Log 'secrets.bin restored'
            }} catch {{
                Log ""Failed to restore secrets.bin: $_"" 'ERROR'
            }}
        }}
        if (Test-Path ""$certPath.bak"") {{
            Log 'Restoring midterm.pem from backup...'
            try {{
                Copy-Item ""$certPath.bak"" $certPath -Force -ErrorAction Stop
                Log 'midterm.pem restored'
            }} catch {{
                Log ""Failed to restore midterm.pem: $_"" 'ERROR'
            }}
        }}
        if (Test-Path ""$keysDir.bak"") {{
            Log 'Restoring keys directory from backup...'
            try {{
                Remove-Item $keysDir -Recurse -Force -ErrorAction SilentlyContinue
                Copy-Item ""$keysDir.bak"" $keysDir -Recurse -Force -ErrorAction Stop
                Log 'keys directory restored'
            }} catch {{
                Log ""Failed to restore keys directory: $_"" 'ERROR'
            }}
        }}

        # Try to restart previous version
        Log 'Attempting to restart previous version...'
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($service) {{
            try {{
                Start-Service -Name $ServiceName -ErrorAction Stop
                Log 'Previous version service started'
            }} catch {{
                Log ""Failed to start service: $_"" 'ERROR'
            }}
        }} else {{
            try {{
                Start-Process -FilePath $CurrentMt -WindowStyle Hidden
                Log 'Previous version started'
            }} catch {{
                Log ""Failed to start mt.exe: $_"" 'ERROR'
            }}
        }}

        Log 'Rollback complete'
    }}

    WriteResult $false $errorMessage
}}

# Self-cleanup
Start-Sleep -Seconds 1
Remove-Item $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
";

        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    private static string GenerateUnixScript(string extractedDir, string currentBinaryPath, string settingsDirectory, MidTermServiceIdentity serviceIdentity, UpdateType updateType, bool deleteSourceAfter)
    {
        // IMPORTANT: Binary and config directories are DIFFERENT on Unix:
        // - Binaries: /usr/local/bin/ (service) or ~/.local/bin/ (user)
        // - Config/secrets: /usr/local/etc/midterm/ (service) or ~/.midterm/ (user)
        // - Logs: /usr/local/var/log/ (service) or ~/.midterm/ (user)
        // The settingsDirectory parameter tells us which mode we're in.
        //
        // SYNC: These paths come from LogPaths.cs and MUST also match:
        //   - install.sh (PATH_CONSTANTS section)
        //   - SettingsService.cs (GetSettingsPath method)
        var installDir = Path.GetDirectoryName(currentBinaryPath) ?? "/usr/local/bin";
        var configDir = settingsDirectory;

        // Determine log directory based on install mode using centralized LogPaths
        var isServiceMode = configDir.StartsWith("/usr/local", StringComparison.Ordinal);
        var logDir = LogPaths.GetLogDirectory(false, isServiceMode);
        var newMtPath = Path.Combine(extractedDir, "mt");
        var newMthostPath = Path.Combine(extractedDir, "mthost");
        var newAgentHostPath = Path.Combine(extractedDir, AgentHostBinaryName);
        var newVersionJsonPath = Path.Combine(extractedDir, "version.json");
        var currentMthostPath = Path.Combine(installDir, "mthost");
        var currentAgentHostPath = Path.Combine(installDir, AgentHostBinaryName);
        var currentVersionJsonPath = Path.Combine(installDir, "version.json");
        var resultFilePath = Path.Combine(configDir, "update-result.json");
        var logFilePath = LogPaths.GetUpdateLogPath(false, isServiceMode, configDir);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"mt-update-{Guid.NewGuid():N}.sh");

        var isWebOnly = updateType != UpdateType.Full;
        var generatingVersion = typeof(UpdateScriptGenerator).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plusIdx = generatingVersion.IndexOf('+', StringComparison.Ordinal);
        if (plusIdx > 0) generatingVersion = generatingVersion[..plusIdx];

        // macOS uses the launcher shim approach (staging + launchd respawn) — see EndpointSetup.cs.
        // This script is only generated for Linux now.
        var systemdService = EscapeForBash(serviceIdentity.SystemdServiceName);
        var stopServiceCmd = $"systemctl stop '{systemdService}' 2>/dev/null || true";
        var startServiceCmd = $"systemctl start '{systemdService}' 2>/dev/null || true";

        var script = $@"#!/bin/bash
# MidTerm Update Script (Linux)
# Type: {(isWebOnly ? "Web-only (sessions preserved)" : "Full (sessions will restart)")}
# Generated: {DateTime.UtcNow:O}
#
# NOTE: macOS uses the launcher shim approach (staging + launchd respawn).
# This script is only used on Linux.
# - Binary dir (/usr/local/bin) != Config dir (/usr/local/etc/midterm)
# - File ownership must be preserved for user-mode service access

set -euo pipefail

# Detach from inherited file descriptors immediately.
# When launched via .NET Process with redirected stdio, the parent (mt) creates
# pipes for stdin/stdout/stderr. When mt exits, those pipes break and any write
# to stdout/stderr sends SIGPIPE which kills this script. Redirect everything
# to the log file BEFORE any output happens.
_early_log_dir='{EscapeForBash(Path.GetDirectoryName(logFilePath) ?? logDir)}'
mkdir -p ""$_early_log_dir"" 2>/dev/null || true
exec > ""{EscapeForBash(logFilePath)}"" 2>&1 < /dev/null

# === Configuration ===
# IMPORTANT: These directories are DIFFERENT - don't confuse them!
INSTALL_DIR='{EscapeForBash(installDir)}'           # Binaries: mt, mthost, {AgentHostBinaryName}
CONFIG_DIR='{EscapeForBash(configDir)}'             # Settings, secrets, certs
LOG_DIR='{EscapeForBash(logDir)}'                   # Log files
CURRENT_MT='{EscapeForBash(currentBinaryPath)}'
CURRENT_MTHOST='{EscapeForBash(currentMthostPath)}'
CURRENT_AGENTHOST='{EscapeForBash(currentAgentHostPath)}'
CURRENT_VERSION_JSON='{EscapeForBash(currentVersionJsonPath)}'
NEW_MT='{EscapeForBash(newMtPath)}'
NEW_MTHOST='{EscapeForBash(newMthostPath)}'
NEW_AGENTHOST='{EscapeForBash(newAgentHostPath)}'
NEW_VERSION_JSON='{EscapeForBash(newVersionJsonPath)}'
EXTRACTED_DIR='{EscapeForBash(extractedDir)}'
LOG_FILE='{EscapeForBash(logFilePath)}'
RESULT_FILE='{EscapeForBash(resultFilePath)}'
BACKUP_DIR='{EscapeForBash(Path.Combine(configDir, "update-backup"))}'
MAX_RETRIES={MaxRetries}
IS_WEB_ONLY={( isWebOnly ? "true" : "false")}
DELETE_SOURCE={( deleteSourceAfter ? "true" : "false")}

ROLLBACK_NEEDED=false
STARTED_OK=false

# Detect service user from existing config file ownership
# On macOS, launchd service runs as user (via UserName in plist), not root
# On Linux, systemd typically runs as root, but we preserve existing ownership
SERVICE_USER=""""
if [[ -f ""$CONFIG_DIR/settings.json"" ]]; then
    SERVICE_USER=$(stat -c '%U' ""$CONFIG_DIR/settings.json"" 2>/dev/null || echo """")
fi

# === Helper Functions ===

log() {{
    local level=""${{2:-INFO}}""
    local timestamp=$(date '+%Y-%m-%d %H:%M:%S.%3N')
    echo ""[$timestamp] [$level] $1""
}}

write_result() {{
    local success=""$1""
    local message=""$2""
    local details=""${{3:-}}""
    cat > ""$RESULT_FILE"" << RESULT_EOF
{{
    ""success"": $success,
    ""message"": ""$message"",
    ""details"": ""$details"",
    ""timestamp"": ""$(date -u '+%Y-%m-%dT%H:%M:%SZ')"",
    ""logFile"": ""$LOG_FILE""
}}
RESULT_EOF
}}

describe_source_file() {{
    local file_path=""$1""
    local description=""$2""
    local required=""${{3:-true}}""

    if [[ ! -f ""$file_path"" ]]; then
        if [[ ""$required"" == ""true"" ]]; then
            log ""Missing required update payload file: $description at $file_path"" ""ERROR""
            return 1
        fi

        log ""Optional update payload file not present: $description at $file_path""
        return 0
    fi

    local file_size
    file_size=$(stat -c%s ""$file_path"" 2>/dev/null || echo ""unknown"")
    log ""Found staged $description at $file_path ($file_size bytes)""
    return 0
}}

wait_for_file_writable() {{
    local file_path=""$1""
    local retries=""${{2:-$MAX_RETRIES}}""

    if [[ ! -f ""$file_path"" ]]; then
        log ""File does not exist (OK): $file_path""
        return 0
    fi

    for ((i=1; i<=retries; i++)); do
        if [[ -w ""$file_path"" ]]; then
            # Try to actually open for write
            if ( exec 3>>""$file_path"" ) 2>/dev/null; then
                exec 3>&-
                log ""File is writable: $file_path""
                return 0
            fi
        fi
        log ""File locked (attempt $i/$retries): $file_path"" ""WARN""
        if [[ $i -lt $retries ]]; then
            sleep {RetryDelaySeconds}
        fi
    done

    log ""File still locked after $retries attempts: $file_path"" ""ERROR""
    return 1
}}

kill_process_by_path() {{
    local full_path=""$1""
    local name
    name=$(basename ""$full_path"")
    local pids

    pids=$(pgrep -fx ""$full_path"" 2>/dev/null || pgrep -f ""$full_path"" 2>/dev/null || true)
    if [[ -n ""$pids"" ]]; then
        for pid in $pids; do
            log ""Killing $name (PID: $pid, Path: $full_path)...""
            kill -9 ""$pid"" 2>/dev/null || true
        done
        sleep 1
    fi

    # Double-check that processes are gone
    pids=$(pgrep -fx ""$full_path"" 2>/dev/null || pgrep -f ""$full_path"" 2>/dev/null || true)
    if [[ -n ""$pids"" ]]; then
        log ""Force killing remaining $name processes..."" ""WARN""
        pkill -9 -f ""$full_path"" 2>/dev/null || true
        sleep 1
    fi
}}

verify_copy() {{
    local src=""$1""
    local dst=""$2""

    if [[ ! -f ""$dst"" ]]; then
        echo ""Copy verification failed: destination does not exist: $dst""
        return 1
    fi

    local src_size=$(stat -c%s ""$src"" 2>/dev/null)
    local dst_size=$(stat -c%s ""$dst"" 2>/dev/null)

    if [[ ""$src_size"" != ""$dst_size"" ]]; then
        echo ""Copy verification failed: size mismatch for $dst (expected $src_size bytes, got $dst_size bytes)""
        return 1
    fi

    log ""Verified: $dst ($dst_size bytes)""
    return 0
}}

safe_copy() {{
    local src=""$1""
    local dst=""$2""
    local desc=""$3""

    log ""Copying $desc...""
    log ""  From: $src""
    log ""  To: $dst""

    if [[ ! -f ""$src"" ]]; then
        echo ""Source file does not exist: $src""
        return 1
    fi

    # Atomic temp+rename (systemd runs as root, has dir write)
    local tmp_dst=""$dst.new""
    cp ""$src"" ""$tmp_dst""
    chmod +x ""$tmp_dst""
    mv -f ""$tmp_dst"" ""$dst""

    if ! verify_copy ""$src"" ""$dst""; then
        return 1
    fi

    log ""$desc copied successfully""
    return 0
}}

cleanup() {{
    log """"
    if [[ ""$ROLLBACK_NEEDED"" == ""true"" ]] && [[ ""$STARTED_OK"" != ""true"" ]]; then
        log ""=== ROLLBACK ==="" ""WARN""

        # Stop any partially started process
        kill_process_by_path ""$CURRENT_MT""

        # Restore binary backups
        if [[ -f ""$BACKUP_DIR/mt.bak"" ]]; then
            log ""Restoring mt from backup...""
            cp -f ""$BACKUP_DIR/mt.bak"" ""$CURRENT_MT"" 2>/dev/null || log ""Failed to restore mt"" ""ERROR""
            chmod +x ""$CURRENT_MT"" 2>/dev/null || true
        fi

        if [[ -f ""$BACKUP_DIR/mthost.bak"" ]]; then
            log ""Restoring mthost from backup...""
            cp -f ""$BACKUP_DIR/mthost.bak"" ""$CURRENT_MTHOST"" 2>/dev/null || log ""Failed to restore mthost"" ""ERROR""
            chmod +x ""$CURRENT_MTHOST"" 2>/dev/null || true
        fi

        if [[ -f ""$BACKUP_DIR/{AgentHostBinaryName}.bak"" ]]; then
            log ""Restoring {AgentHostBinaryName} from backup...""
            cp -f ""$BACKUP_DIR/{AgentHostBinaryName}.bak"" ""$CURRENT_AGENTHOST"" 2>/dev/null || log ""Failed to restore {AgentHostBinaryName}"" ""ERROR""
            chmod +x ""$CURRENT_AGENTHOST"" 2>/dev/null || true
        fi

        if [[ -f ""$BACKUP_DIR/version.json.bak"" ]]; then
            log ""Restoring version.json from backup...""
            cp -f ""$BACKUP_DIR/version.json.bak"" ""$CURRENT_VERSION_JSON"" 2>/dev/null || log ""Failed to restore version.json"" ""ERROR""
        fi

        # Restore credential files from CONFIG_DIR (not INSTALL_DIR!)
        SETTINGS_PATH=""$CONFIG_DIR/settings.json""
        SECRETS_PATH=""$CONFIG_DIR/secrets.json""
        CERT_PATH=""$CONFIG_DIR/midterm.pem""
        KEY_ENC_PATH=""$CONFIG_DIR/midterm.key.enc""

        if [[ -f ""$SETTINGS_PATH.bak"" ]]; then
            log ""Restoring settings.json from backup...""
            cp -f ""$SETTINGS_PATH.bak"" ""$SETTINGS_PATH"" 2>/dev/null || log ""Failed to restore settings.json"" ""ERROR""
        fi
        if [[ -f ""$SECRETS_PATH.bak"" ]]; then
            log ""Restoring secrets.json from backup...""
            cp -f ""$SECRETS_PATH.bak"" ""$SECRETS_PATH"" 2>/dev/null || log ""Failed to restore secrets.json"" ""ERROR""
        fi
        if [[ -f ""$CERT_PATH.bak"" ]]; then
            log ""Restoring midterm.pem from backup...""
            cp -f ""$CERT_PATH.bak"" ""$CERT_PATH"" 2>/dev/null || log ""Failed to restore midterm.pem"" ""ERROR""
        fi
        if [[ -f ""$KEY_ENC_PATH.bak"" ]]; then
            log ""Restoring midterm.key.enc from backup...""
            cp -f ""$KEY_ENC_PATH.bak"" ""$KEY_ENC_PATH"" 2>/dev/null || log ""Failed to restore midterm.key.enc"" ""ERROR""
        fi

        # Try to restart previous version
        log ""Attempting to restart previous version...""
        {startServiceCmd}

        if ! pgrep -f ""$CURRENT_MT"" > /dev/null 2>&1; then
            nohup ""$CURRENT_MT"" > /dev/null 2>&1 &
        fi

        log ""Rollback complete""
    fi

    # Self-cleanup
    sleep 1
    rm -f ""$0"" 2>/dev/null || true
}}

trap cleanup EXIT

# === Main Script ===

# Ensure log directory exists and has correct ownership
mkdir -p ""$LOG_DIR"" 2>/dev/null || true
mkdir -p ""$BACKUP_DIR"" 2>/dev/null || true

# Log file is already truncated by exec > redirect at script start
rm -f ""$RESULT_FILE"" 2>/dev/null || true

log '=========================================='
log 'MidTerm Update Script v{generatingVersion}'
log ""Running as: $(whoami) (SERVICE_USER=${{SERVICE_USER:-unknown}})""
log ""Update type: $(if $IS_WEB_ONLY; then echo 'Web-only'; else echo 'Full'; fi)""
log ""Platform: Linux""
log '=========================================='

# Log version before update
if [[ -f ""$CURRENT_VERSION_JSON"" ]]; then
    if command -v jq &> /dev/null; then
        _web_ver=$(jq -r '.web // ""unknown""' ""$CURRENT_VERSION_JSON"" 2>/dev/null)
        _pty_ver=$(jq -r '.pty // ""unknown""' ""$CURRENT_VERSION_JSON"" 2>/dev/null)
        log ""Version before update: web=$_web_ver, pty=$_pty_ver""
    else
        log ""version.json exists but jq not available for parsing""
    fi
else
    log ""No version.json found (fresh install?)"" ""WARN""
fi

log ""Update source directory: $EXTRACTED_DIR""
if [[ ! -d ""$EXTRACTED_DIR"" ]]; then
    log ""Update source directory is missing before shutdown: $EXTRACTED_DIR"" ""ERROR""
    write_result false ""Update payload directory is missing before install"" ""$EXTRACTED_DIR""
    exit 1
fi

if ! describe_source_file ""$NEW_MT"" ""mt""; then
    write_result false ""Update payload is missing mt"" ""$NEW_MT""
    exit 1
fi

if [[ ""$IS_WEB_ONLY"" == ""false"" ]]; then
    if ! describe_source_file ""$NEW_MTHOST"" ""mthost""; then
        write_result false ""Update payload is missing mthost"" ""$NEW_MTHOST""
        exit 1
    fi
fi

if ! describe_source_file ""$NEW_VERSION_JSON"" ""version.json""; then
    write_result false ""Update payload is missing version.json"" ""$NEW_VERSION_JSON""
    exit 1
fi

# ============================================
# PHASE 1: Stop all processes
# ============================================
log """"
log '=== PHASE 1: Stopping processes ==='

log ""Stopping service...""
{stopServiceCmd}

# Wait for process to actually exit (up to 5s) before force-killing
for _i in $(seq 1 10); do
    pgrep -f ""/${{CURRENT_MT##*/}}$"" >/dev/null 2>&1 || break
    sleep 0.5
done

# Kill mt processes (by full path to avoid killing unrelated processes)
log ""Killing mt processes...""
kill_process_by_path ""$CURRENT_MT""

# Kill mthost processes (only for full updates)
if [[ ""$IS_WEB_ONLY"" == ""false"" ]]; then
    log ""Killing mthost processes...""
    kill_process_by_path ""$CURRENT_MTHOST""
    if [[ -f ""$CURRENT_AGENTHOST"" ]]; then
        log ""Killing {AgentHostBinaryName} processes...""
        kill_process_by_path ""$CURRENT_AGENTHOST""
    fi
fi

log ""All processes stopped""

# ============================================
# PHASE 2: Wait for file handles to release
# ============================================
log """"
log '=== PHASE 2: Waiting for file handles ==='

if ! wait_for_file_writable ""$CURRENT_MT""; then
    log ""mt is still locked after $MAX_RETRIES retries"" ""ERROR""
    write_result false ""mt is still locked. Another process may be using it.""
    exit 1
fi

if [[ ""$IS_WEB_ONLY"" == ""false"" ]] && [[ -f ""$CURRENT_MTHOST"" ]]; then
    if ! wait_for_file_writable ""$CURRENT_MTHOST""; then
        log ""mthost is still locked after $MAX_RETRIES retries"" ""ERROR""
        write_result false ""mthost is still locked. Another process may be using it.""
        exit 1
    fi
fi

if [[ ""$IS_WEB_ONLY"" == ""false"" ]] && [[ -f ""$CURRENT_AGENTHOST"" ]]; then
    if ! wait_for_file_writable ""$CURRENT_AGENTHOST""; then
        log ""{AgentHostBinaryName} is still locked after $MAX_RETRIES retries"" ""ERROR""
        write_result false ""{AgentHostBinaryName} is still locked. Another process may be using it.""
        exit 1
    fi
fi

log ""All file handles released""

# ============================================
# PHASE 3: Create backups
# ============================================
log """"
log '=== PHASE 3: Creating backups ==='

if [[ -f ""$CURRENT_MT"" ]]; then
    log ""Backing up mt...""
    cp -f ""$CURRENT_MT"" ""$BACKUP_DIR/mt.bak""
    log ""mt backed up""
fi

if [[ ""$IS_WEB_ONLY"" == ""false"" ]] && [[ -f ""$CURRENT_MTHOST"" ]]; then
    log ""Backing up mthost...""
    cp -f ""$CURRENT_MTHOST"" ""$BACKUP_DIR/mthost.bak""
    log ""mthost backed up""
fi

if [[ ""$IS_WEB_ONLY"" == ""false"" ]] && [[ -f ""$CURRENT_AGENTHOST"" ]]; then
    log ""Backing up {AgentHostBinaryName}...""
    cp -f ""$CURRENT_AGENTHOST"" ""$BACKUP_DIR/{AgentHostBinaryName}.bak""
    log ""{AgentHostBinaryName} backed up""
fi

if [[ -f ""$CURRENT_VERSION_JSON"" ]]; then
    log ""Backing up version.json...""
    cp -f ""$CURRENT_VERSION_JSON"" ""$BACKUP_DIR/version.json.bak""
    log ""version.json backed up""
fi

# Backup credential files (critical for security persistence)
# IMPORTANT: These are in CONFIG_DIR (/usr/local/etc/midterm), NOT INSTALL_DIR!
# Common mistake: looking for settings in /usr/local/bin/ - that's wrong.
SETTINGS_PATH=""$CONFIG_DIR/settings.json""
SECRETS_PATH=""$CONFIG_DIR/secrets.json""
CERT_PATH=""$CONFIG_DIR/midterm.pem""
KEY_ENC_PATH=""$CONFIG_DIR/midterm.key.enc""

if [[ -f ""$SETTINGS_PATH"" ]]; then
    log ""Backing up settings.json...""
    cp -f ""$SETTINGS_PATH"" ""$SETTINGS_PATH.bak""
    log ""settings.json backed up""
fi
if [[ -f ""$SECRETS_PATH"" ]]; then
    log ""Backing up secrets.json...""
    cp -f ""$SECRETS_PATH"" ""$SECRETS_PATH.bak""
    log ""secrets.json backed up""
fi
if [[ -f ""$CERT_PATH"" ]]; then
    log ""Backing up midterm.pem...""
    cp -f ""$CERT_PATH"" ""$CERT_PATH.bak""
    log ""midterm.pem backed up""
fi
if [[ -f ""$KEY_ENC_PATH"" ]]; then
    log ""Backing up midterm.key.enc...""
    cp -f ""$KEY_ENC_PATH"" ""$KEY_ENC_PATH.bak""
    log ""midterm.key.enc backed up""
fi

ROLLBACK_NEEDED=true
log ""All backups created (including credentials)""

# === CERTIFICATE DIAGNOSTICS ===
log """"
log '=== Certificate Diagnostics ==='

# Check cert file
if [[ -f ""$CERT_PATH"" ]]; then
    cert_size=$(stat -c%s ""$CERT_PATH"" 2>/dev/null)
    cert_mtime=$(stat -c%Y ""$CERT_PATH"" 2>/dev/null)
    log ""  midterm.pem: Size=$cert_size bytes""

    # Get cert info using openssl
    if command -v openssl &> /dev/null; then
        thumbprint=$(openssl x509 -in ""$CERT_PATH"" -noout -fingerprint -sha1 2>/dev/null | cut -d= -f2 | tr -d ':')
        subject=$(openssl x509 -in ""$CERT_PATH"" -noout -subject 2>/dev/null)
        not_after=$(openssl x509 -in ""$CERT_PATH"" -noout -enddate 2>/dev/null | cut -d= -f2)
        not_before=$(openssl x509 -in ""$CERT_PATH"" -noout -startdate 2>/dev/null | cut -d= -f2)
        log ""  Thumbprint: $thumbprint""
        log ""  Subject: $subject""
        log ""  NotAfter: $not_after""
        log ""  NotBefore: $not_before""
    else
        log ""  WARNING: openssl not available for cert parsing"" ""WARN""
    fi
else
    log '  WARNING: midterm.pem does NOT exist!' ""WARN""
fi

# Check key file
if [[ -f ""$KEY_ENC_PATH"" ]]; then
    key_size=$(stat -c%s ""$KEY_ENC_PATH"" 2>/dev/null)
    log ""  midterm.key.enc: Size=$key_size bytes""
else
    log '  WARNING: midterm.key.enc does NOT exist!' ""WARN""
fi

# Check settings.json for cert config
if [[ -f ""$SETTINGS_PATH"" ]]; then
    if command -v jq &> /dev/null; then
        cert_path_setting=$(jq -r '.certificatePath // empty' ""$SETTINGS_PATH"" 2>/dev/null)
        key_protection=$(jq -r '.keyProtection // empty' ""$SETTINGS_PATH"" 2>/dev/null)
        is_service=$(jq -r '.isServiceInstall // empty' ""$SETTINGS_PATH"" 2>/dev/null)
        cert_thumbprint=$(jq -r '.certificateThumbprint // empty' ""$SETTINGS_PATH"" 2>/dev/null)
        log ""  settings.certificatePath: $cert_path_setting""
        log ""  settings.keyProtection: $key_protection""
        log ""  settings.isServiceInstall: $is_service""
        log ""  settings.certificateThumbprint: $cert_thumbprint""
    else
        log ""  (jq not available for settings parsing)""
    fi
fi

# ============================================
# PHASE 4: Install new files
# ============================================
log """"
log '=== PHASE 4: Installing new files ==='

if ! safe_copy ""$NEW_MT"" ""$CURRENT_MT"" ""mt""; then
    write_result false ""Failed to install mt""
    exit 1
fi

if [[ ""$IS_WEB_ONLY"" == ""false"" ]] && [[ -f ""$NEW_MTHOST"" ]]; then
    if ! safe_copy ""$NEW_MTHOST"" ""$CURRENT_MTHOST"" ""mthost""; then
        write_result false ""Failed to install mthost""
        exit 1
    fi
fi

if [[ ""$IS_WEB_ONLY"" == ""false"" ]] && [[ -f ""$NEW_AGENTHOST"" ]]; then
    if ! safe_copy ""$NEW_AGENTHOST"" ""$CURRENT_AGENTHOST"" ""{AgentHostBinaryName}""; then
        write_result false ""Failed to install {AgentHostBinaryName}""
        exit 1
    fi
fi

if [[ -f ""$NEW_VERSION_JSON"" ]]; then
    log ""Copying version.json...""
    cp -f ""$NEW_VERSION_JSON"" ""$CURRENT_VERSION_JSON""
    log ""version.json copied""
fi

log ""All files installed""

# Settings file fate
if [[ -f ""$SETTINGS_PATH"" ]]; then
    log ""settings.json: preserved (not modified by update)""
else
    log ""settings.json: not present"" ""WARN""
fi

# Log version after update
if [[ -f ""$CURRENT_VERSION_JSON"" ]]; then
    if command -v jq &> /dev/null; then
        _web_ver=$(jq -r '.web // ""unknown""' ""$CURRENT_VERSION_JSON"" 2>/dev/null)
        _pty_ver=$(jq -r '.pty // ""unknown""' ""$CURRENT_VERSION_JSON"" 2>/dev/null)
        log ""Version after update: web=$_web_ver, pty=$_pty_ver""
    else
        log ""version.json exists but jq not available for parsing""
    fi
fi

# ============================================
# PHASE 5: Start the new version
# ============================================
log """"
log '=== PHASE 5: Starting new version ==='

# Ensure main service log file has correct ownership BEFORE starting service
# Without this, the service (running as user) can't write to root-owned log
MAIN_LOG=""$LOG_DIR/MidTerm.log""
touch ""$MAIN_LOG"" 2>/dev/null || true
if [[ -n ""$SERVICE_USER"" ]]; then
    chown ""$SERVICE_USER"" ""$MAIN_LOG"" 2>/dev/null || true
    log ""Set $MAIN_LOG ownership to $SERVICE_USER""
fi

# Try to start service
log ""Starting service...""
{startServiceCmd}
sleep 5

# Check if process is running (pgrep is reliable, unlike launchctl print)
MT_PID=$(pgrep -f ""$CURRENT_MT"" 2>/dev/null | head -1 || true)

if [[ -n ""$MT_PID"" ]]; then
    log ""Service started successfully (PID: $MT_PID)""
    STARTED_OK=true
else
    # Service command didn't start the process - try direct start as fallback
    log ""Service not running, starting mt directly..."" ""WARN""
    nohup ""$CURRENT_MT"" > ""$MAIN_LOG"" 2>&1 &
    sleep 3

    MT_PID=$(pgrep -f ""$CURRENT_MT"" 2>/dev/null | head -1 || true)
    if [[ -n ""$MT_PID"" ]]; then
        log ""mt started directly (PID: $MT_PID)""
        STARTED_OK=true
    fi
fi

if [[ ""$STARTED_OK"" != ""true"" ]]; then
    log ""mt failed to start"" ""ERROR""

    # Diagnostics
    log ""=== Startup Failure Diagnostics ===""
    log ""Binary exists: $([ -f ""$CURRENT_MT"" ] && echo 'yes' || echo 'NO')""
    log ""Binary executable: $([ -x ""$CURRENT_MT"" ] && echo 'yes' || echo 'NO')""
    if [[ -f ""$MAIN_LOG"" ]]; then
        log ""Last 10 lines of $MAIN_LOG:""
        tail -10 ""$MAIN_LOG"" 2>/dev/null | while read -r line; do
            log ""  $line""
        done
    fi

    write_result false ""mt failed to start - check $MAIN_LOG for details""
    exit 1
fi

# === POST-UPDATE CERTIFICATE VERIFICATION ===
log """"
log '=== Post-Update Certificate Verification ==='

# Check if cert file still exists and is valid
if [[ -f ""$CERT_PATH"" ]]; then
    if command -v openssl &> /dev/null; then
        thumbprint=$(openssl x509 -in ""$CERT_PATH"" -noout -fingerprint -sha1 2>/dev/null | cut -d= -f2 | tr -d ':')
        not_after=$(openssl x509 -in ""$CERT_PATH"" -noout -enddate 2>/dev/null | cut -d= -f2)
        log ""  Certificate OK: ${{thumbprint:0:8}}... expires $not_after""
    else
        log ""  Certificate exists (openssl not available for verification)""
    fi
else
    log '  WARNING: Certificate file missing after update!' ""WARN""
fi

# Check if key file still exists
if [[ -f ""$KEY_ENC_PATH"" ]]; then
    key_size=$(stat -c%s ""$KEY_ENC_PATH"" 2>/dev/null)
    log ""  Key file OK: $key_size bytes""
else
    log '  WARNING: Key file missing after update!' ""WARN""
fi

# ============================================
# PHASE 6: Cleanup
# ============================================
log """"
log '=== PHASE 6: Cleanup ==='

# Clean up binary backups
rm -rf ""$BACKUP_DIR"" 2>/dev/null || true

# Clean up credential backups (in CONFIG_DIR, not INSTALL_DIR!)
rm -f ""$CONFIG_DIR/settings.json.bak"" 2>/dev/null || true
rm -f ""$CONFIG_DIR/secrets.json.bak"" 2>/dev/null || true
rm -f ""$CONFIG_DIR/midterm.pem.bak"" 2>/dev/null || true
rm -f ""$CONFIG_DIR/midterm.key.enc.bak"" 2>/dev/null || true

if [[ ""$DELETE_SOURCE"" == ""true"" ]]; then
    rm -rf ""$EXTRACTED_DIR"" 2>/dev/null || true
fi

log ""Cleanup complete""

# ============================================
# SUCCESS
# ============================================
log """"
log '=========================================='
log 'UPDATE COMPLETED SUCCESSFULLY'
log '=========================================='

write_result true ""Update completed successfully""
";

        File.WriteAllText(scriptPath, script);

        // Set executable permission (Unix only)
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch
            {
            }
        }

        return scriptPath;
    }

    public static void ExecuteUpdateScript(string scriptPath, bool runOutsideServiceCgroup = false)
    {
        if (OperatingSystem.IsWindows())
        {
            // Find pwsh.exe - check common locations
            var pwshPath = FindPowerShellPath();

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pwshPath,
                Arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            using var detachedProcess = System.Diagnostics.Process.Start(psi);
        }
        else
        {
            if (runOutsideServiceCgroup && OperatingSystem.IsLinux())
            {
                var unitName = $"midterm-update-{Guid.NewGuid():N}";
                var useSudo = CanRunSudoWithoutPrompt();
                var systemdRunPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = useSudo ? "/usr/bin/sudo" : "/usr/bin/systemd-run",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                if (useSudo)
                {
                    systemdRunPsi.ArgumentList.Add("-n");
                    systemdRunPsi.ArgumentList.Add("/usr/bin/systemd-run");
                }

                systemdRunPsi.ArgumentList.Add("--unit");
                systemdRunPsi.ArgumentList.Add(unitName);
                systemdRunPsi.ArgumentList.Add("--collect");
                systemdRunPsi.ArgumentList.Add("--no-block");
                systemdRunPsi.ArgumentList.Add("/bin/bash");
                systemdRunPsi.ArgumentList.Add(scriptPath);

                using var process = System.Diagnostics.Process.Start(systemdRunPsi)
                    ?? throw new InvalidOperationException("Failed to start systemd-run for the Linux service updater.");

                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("Timed out waiting for systemd-run to launch the Linux service updater.");
                }

                var stdout = process.StandardOutput.ReadToEnd().Trim();
                var stderr = process.StandardError.ReadToEnd().Trim();
                if (process.ExitCode != 0)
                {
                    var details = string.Join(" | ", new[] { stderr, stdout }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (string.IsNullOrWhiteSpace(details))
                    {
                        details = string.Create(CultureInfo.InvariantCulture, $"exit code {process.ExitCode}");
                    }

                    throw new InvalidOperationException(
                        "Failed to launch the Linux service updater outside the MidTerm service cgroup: " + details);
                }

                return;
            }

            // Linux: setsid creates a new session so the script survives mt's exit.
            //
            // CRITICAL: Do NOT use RedirectStandard* here! It creates pipes between mt and
            // the script. When mt exits (Environment.Exit), the pipes break, and SIGPIPE
            // kills the update script. The script handles its own stdio redirection via
            // exec > logfile 2>&1 < /dev/null at the top.
            //
            // Note: macOS uses the launcher shim approach and never calls this method.
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/setsid",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("--fork");
            psi.ArgumentList.Add("/bin/bash");
            psi.ArgumentList.Add(scriptPath);

            using var detachedProcess = System.Diagnostics.Process.Start(psi);
        }
    }

    private static bool CanRunSudoWithoutPrompt()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/sudo"))
        {
            return false;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/sudo",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("true");

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string FindPowerShellPath()
    {
        // Use Windows PowerShell — guaranteed present on all Windows versions
        // The generated update script uses only PS 5.1-compatible syntax
        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        if (File.Exists(windowsPowerShell))
        {
            return windowsPowerShell;
        }

        return "powershell.exe";
    }

    private static string EscapeForPowerShell(string value)
    {
        // Escape single quotes for PowerShell single-quoted strings
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string EscapeForBash(string value)
    {
        // Escape single quotes for bash single-quoted strings
        return value.Replace("'", "'\\''", StringComparison.Ordinal);
    }
}
