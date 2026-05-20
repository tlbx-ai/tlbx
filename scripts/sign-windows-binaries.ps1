#!/usr/bin/env pwsh
# Sign Windows binaries (mt.exe, mthost.exe, mtagenthost.exe, mttmux.exe) using Certum SimplySign cloud certificate.
# Designed to run on a self-hosted GitHub Actions runner during stable releases.
# Plays a notification sound and waits for the user to authenticate in SimplySign Desktop.

param(
    [Parameter(Mandatory = $true)]
    [string]$StagingPath,

    [string]$Thumbprint,

    [string]$TimestampServer = "http://time.certum.pl",

    [int]$TimeoutMinutes = 10,

    [int]$RetryIntervalSeconds = 15
)

$ErrorActionPreference = "Stop"

# ── Resolve signtool ─────────────────────────────────────────────────────────

function Find-SignTool
{
    $fromPath = Get-Command signtool -ErrorAction SilentlyContinue
    if ($fromPath) { return $fromPath.Source }

    $sdkPaths = Get-ChildItem "C:\Program Files (x86)\Windows Kits\*\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if ($sdkPaths) { return $sdkPaths.FullName }
    throw "signtool.exe not found. Install Windows SDK or add signtool to PATH."
}

$signtool = Find-SignTool
Write-Host "Using signtool: $signtool" -ForegroundColor Cyan
Write-Host "Running as user: $([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)" -ForegroundColor Cyan

function Find-CodeSigningCertificateThumbprint
{
    $now = Get-Date
    $certs = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object {
            $_.HasPrivateKey -and
            $_.NotAfter -gt $now -and
            ($_.EnhancedKeyUsageList.FriendlyName -contains "Code Signing")
        } |
        Sort-Object NotAfter -Descending

    $cert = @($certs | Where-Object { $_.Subject -like "*Open Source Developer Johannes Schmidt*" } | Select-Object -First 1)
    if (-not $cert)
    {
        $cert = @($certs | Select-Object -First 1)
    }

    if (-not $cert)
    {
        throw "No valid code-signing certificate with a private key was found in the Windows certificate store."
    }

    Write-Host "Using code-signing certificate: $($cert.Subject)" -ForegroundColor Cyan
    return $cert.Thumbprint
}

if (-not $Thumbprint)
{
    $Thumbprint = Find-CodeSigningCertificateThumbprint
}

# ── Validate staging path and binaries ───────────────────────────────────────

$mtPath = Join-Path $StagingPath "mt.exe"
$mthostPath = Join-Path $StagingPath "mthost.exe"
$mtagenthostPath = Join-Path $StagingPath "mtagenthost.exe"
$mttmuxPath = Join-Path $StagingPath "mttmux.exe"

if (-not (Test-Path $mtPath))
{
    throw "mt.exe not found at: $mtPath"
}

$binaries = @($mtPath)
$hasMthost = Test-Path $mthostPath
if ($hasMthost)
{
    $binaries += $mthostPath
}
else
{
    Write-Host "mthost.exe not found (web-only release), continuing with the other Windows binaries" -ForegroundColor Yellow
}

if (Test-Path $mtagenthostPath)
{
    $binaries += $mtagenthostPath
}
else
{
    Write-Host "mtagenthost.exe not found in staging" -ForegroundColor Yellow
}

if (Test-Path $mttmuxPath)
{
    $binaries += $mttmuxPath
}
else
{
    Write-Host "mttmux.exe not found in staging" -ForegroundColor Yellow
}

# ── Build signtool arguments ─────────────────────────────────────────────────

$signArgs = @("sign")
$signArgs += "/sha1", $Thumbprint
$signArgs += "/tr", $TimestampServer
$signArgs += "/td", "sha256"
$signArgs += "/fd", "sha256"

# ── Notification: sound + console message ────────────────────────────────────

function Send-Notification
{
    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║                                                              ║" -ForegroundColor Magenta
    Write-Host "║   MIDTERM RELEASE — WINDOWS CODE SIGNING REQUIRED            ║" -ForegroundColor Magenta
    Write-Host "║                                                              ║" -ForegroundColor Magenta
    Write-Host "║   1. Open SimplySign Desktop                                 ║" -ForegroundColor Magenta
    Write-Host "║   2. Authenticate with the mobile app                        ║" -ForegroundColor Magenta
    Write-Host "║   3. Signing will proceed automatically                      ║" -ForegroundColor Magenta
    Write-Host "║                                                              ║" -ForegroundColor Magenta
    Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Magenta
    Write-Host ""

    # Play system sounds to get attention (multiple beeps)
    for ($i = 0; $i -lt 5; $i++)
    {
        [System.Media.SystemSounds]::Exclamation.Play()
        Start-Sleep -Milliseconds 400
    }

    # Try Windows toast notification (best-effort)
    try
    {
        $xml = @"
<toast>
  <visual>
    <binding template="ToastGeneric">
      <text>MidTerm Release</text>
      <text>Windows code signing required — authenticate in SimplySign Desktop</text>
    </binding>
  </visual>
  <audio src="ms-winsoundevent:Notification.Looping.Alarm" loop="true" />
</toast>
"@
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null
        $doc = [Windows.Data.Xml.Dom.XmlDocument]::new()
        $doc.LoadXml($xml)
        $toast = [Windows.UI.Notifications.ToastNotification]::new($doc)
        $notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier("MidTerm.Release")
        $notifier.Show($toast)
    }
    catch
    {
        # Toast notification is best-effort; console output + sound is primary
    }
}

# ── Ensure SimplySign Desktop is running ─────────────────────────────────────

$simplySign = Get-Process -Name "SimplySign*" -ErrorAction SilentlyContinue
if (-not $simplySign)
{
    Write-Host "SimplySign Desktop is not running. Attempting to launch..." -ForegroundColor Yellow
    $simplySignPaths = @(
        "${env:ProgramFiles}\Certum\SimplySign Desktop\SimplySign Desktop.exe",
        "${env:ProgramFiles(x86)}\Certum\SimplySign Desktop\SimplySign Desktop.exe",
        "${env:LocalAppData}\Programs\SimplySign Desktop\SimplySign Desktop.exe"
    )
    $launched = $false
    foreach ($path in $simplySignPaths)
    {
        if (Test-Path $path)
        {
            Start-Process $path
            Write-Host "Launched SimplySign Desktop from: $path"
            Start-Sleep -Seconds 3
            $launched = $true
            break
        }
    }
    if (-not $launched)
    {
        Write-Host "Could not find SimplySign Desktop. Please start it manually." -ForegroundColor Red
    }
}
else
{
    Write-Host "SimplySign Desktop is running." -ForegroundColor Green
}

# ── Send notification ────────────────────────────────────────────────────────

Send-Notification

# ── Attempt signing with retries ─────────────────────────────────────────────

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$attempt = 0
$signed = $false

while ((Get-Date) -lt $deadline)
{
    $attempt++
    Write-Host "Signing attempt $attempt..." -ForegroundColor Cyan

    $allSigned = $true
    foreach ($binary in $binaries)
    {
        $name = Split-Path $binary -Leaf
        $args = $signArgs + @($binary)

        $proc = Start-Process -FilePath $signtool -ArgumentList $args -NoNewWindow -Wait -PassThru 2>&1
        if ($proc.ExitCode -ne 0)
        {
            Write-Host "  $name — signing failed (SimplySign may need authentication)" -ForegroundColor Yellow
            $allSigned = $false
            break
        }
        else
        {
            Write-Host "  $name — signed successfully" -ForegroundColor Green
        }
    }

    if ($allSigned)
    {
        $signed = $true
        break
    }

    Write-Host "Retrying in $RetryIntervalSeconds seconds... (timeout at $($deadline.ToString('HH:mm:ss')))" -ForegroundColor Gray

    # Reminder beep every retry
    [System.Media.SystemSounds]::Exclamation.Play()
    Start-Sleep -Seconds $RetryIntervalSeconds
}

if (-not $signed)
{
    throw "Signing timed out after $TimeoutMinutes minutes. Authenticate in SimplySign Desktop and re-run the release."
}

# ── Verify signatures ────────────────────────────────────────────────────────

Write-Host ""
Write-Host "Verifying signatures..." -ForegroundColor Cyan

foreach ($binary in $binaries)
{
    $name = Split-Path $binary -Leaf

    $proc = Start-Process -FilePath $signtool -ArgumentList @("verify", "/pa", "/v", $binary) -NoNewWindow -Wait -PassThru 2>&1
    if ($proc.ExitCode -ne 0)
    {
        throw "Signature verification failed for $name"
    }

    $sig = Get-AuthenticodeSignature $binary
    if ($sig.Status -ne "Valid")
    {
        throw "Authenticode verification failed for ${name}: $($sig.StatusMessage)"
    }
    Write-Host "  $name — VALID ($($sig.SignerCertificate.Subject))" -ForegroundColor Green
}

Write-Host ""
Write-Host "Windows code signing complete." -ForegroundColor Green
