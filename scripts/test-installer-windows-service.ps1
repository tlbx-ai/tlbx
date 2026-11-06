[CmdletBinding()]
param(
    [switch]$ServiceSmoke,
    [ValidateSet('', 'service-create', 'uac-launch', 'elevated-child')][string]$CallerCase = '',
    [string]$EvidenceDirectory = '',
    [string]$ArchivePath = ''
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { return }
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Get-Content (Join-Path $repoRoot 'install.ps1') -Raw -Encoding UTF8
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors.Message -join '; ') }
function Get-InstallerDefinition([string]$Name) {
    $node = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name }, $true)
    if (-not $node) { throw "Missing installer function: $Name" }
    return $node.Extent.Text
}
if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path $repoRoot '.artifacts/installer-windows-service' }
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
if ($CallerCase) {
    $script:FixtureRoot = Join-Path $EvidenceDirectory ($CallerCase + '-' + $PSVersionTable.PSVersion.Major)
    New-Item -ItemType Directory -Path $script:FixtureRoot -Force | Out-Null
    $env:LOCALAPPDATA = Join-Path $script:FixtureRoot 'local app data'
    $script:FixtureCase = $CallerCase
    $script:FixturePasswordHash = '$PBKDF2$600000$' + [Convert]::ToBase64String((New-Object byte[] 32)) + '$' + [Convert]::ToBase64String((New-Object byte[] 32))
    $script:UnicodeDiagnostic = 'Gr' + [char]0x00FC + [char]0x00DF + 'e ' + [char]0x03A9
    # Execute the actual IEX main flow. Only interactive/network/OS boundaries
    # are replaced; the elevation orchestration and failure handlers stay real.
    $overrides = @{
        'Get-CurrentUserInfo' = 'return @{Name=("fixture"+[char]0x00E4+[char]0x03A9);Sid="S-1-5-21-1"}'
        'Get-CurrentInstallerScriptContent' = 'return $script:FixtureInstallerDefinition'
        'Write-Header' = ''
        'Get-LatestRelease' = 'return @{tag_name="v10.25.33";prerelease=$false;assets=@()}'
        'Select-InstallIdentity' = '$script:WIN_SERVICE_INSTALL_DIR=Join-Path $script:FixtureRoot "binary path"; $script:WIN_SERVICE_SETTINGS_DIR=Join-Path $script:FixtureRoot "settings path"'
        'Assert-NoCrossModeConflict' = ''
        'Test-ExistingPassword' = 'return $false'
        'Prompt-Password' = 'return $script:FixturePasswordHash'
        'Prompt-NetworkConfig' = 'return @{Port=44444;BindAddress="localhost"}'
        'Prompt-FirewallConfig' = 'return $false'
        'Test-Administrator' = 'return ($script:FixtureCase -eq "service-create")'
        'New-ElevationHandoffDirectory' = '$p=Join-Path $script:FixtureRoot "handoff";New-Item -ItemType Directory -Path $p -Force|Out-Null;return $p'
        'Install-Tlbx' = 'Initialize-Log -Mode service;Install-AsService -InstallDir $WIN_SERVICE_INSTALL_DIR -Version "10.25.33" -Port 44444 -BindAddress localhost'
    }
    $replacements = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $overrides.ContainsKey($n.Name) }, $true) | Sort-Object { $_.Extent.StartOffset } -Descending)
    foreach ($node in $replacements) {
        $replacement = "function $($node.Name) { $($node.Body.ParamBlock.Extent.Text)`n$($overrides[$node.Name])`n}"
        $source = $source.Remove($node.Extent.StartOffset, $node.Extent.EndOffset - $node.Extent.StartOffset).Insert($node.Extent.StartOffset, $replacement)
    }
    function Read-Host { param($Prompt) if ($Prompt -match 'Your choice') { return '1' }; return 'n' }
    function New-Service { throw 'SCM fixture create failure 1639' }
    function Get-Service { return $null }
    function Get-CimInstance { return $null }
    function Start-Process {
        param($FilePath, $ArgumentList, $Verb, $WindowStyle, [switch]$PassThru)
        $handoff = Join-Path $script:FixtureRoot 'handoff'
        $logPath = Join-Path $handoff 'mt-install-log.txt'
        $bytes = [IO.File]::ReadAllBytes((Join-Path $handoff 'mt-install-elevated.ps1'))
        if ($bytes.Length -lt 3 -or $bytes[0] -ne 239 -or $bytes[1] -ne 187 -or $bytes[2] -ne 191) { throw 'PowerShell 5.1 handoff script lacks its UTF-8 BOM.' }
        ($script:UnicodeDiagnostic + ' child exit 37') | Set-Content -Encoding UTF8 $logPath
        if ($script:FixtureCase -eq 'uac-launch') { throw 'UAC fixture launch cancelled 1223' }
        # Run a genuine bounded failed child without requesting elevation. This
        # preserves Windows PowerShell's real Process/ExitCode behavior.
        $replayPath = (Join-Path $handoff 'mt-install-replay.json').Replace("'","''")
        $command = (Get-InstallerDefinition 'Import-ElevatedReplayFile') + (Get-InstallerDefinition 'Normalize-InstallerBindAddress') + (Get-InstallerDefinition 'Assert-InstallerOptions') + "`nImport-ElevatedReplayFile -Path '$replayPath';" + 'if($script:RunAsUser -cne ("fixture"+[char]0x00E4+[char]0x03A9)){throw "Unicode replay was changed"};[Threading.Thread]::Sleep(200);exit 37'
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        return Microsoft.PowerShell.Management\Start-Process -FilePath (Get-WindowsPowerShellPath) -ArgumentList "-NoProfile -NonInteractive -EncodedCommand $encoded" -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $script:FixtureRoot 'child.stdout.log') -RedirectStandardError (Join-Path $script:FixtureRoot 'child.stderr.log')
    }
    $failure = $null
    $source = '# ' + $script:UnicodeDiagnostic + "`r`n" + $source
    $script:FixtureInstallerDefinition = $source
    try { $source | Invoke-Expression } catch { $failure = $_.ToString() }
    # Host-level exit prevents this observable marker after invocation.
    'CALLER_SURVIVED' | Set-Content (Join-Path $script:FixtureRoot 'caller-marker.txt')
    $failure | Set-Content (Join-Path $script:FixtureRoot 'failure.txt')
    if (-not $failure) { throw 'Installer failure was swallowed.' }
    if ($CallerCase -eq 'service-create' -and $failure -notmatch '1639') { throw "Wrong service error: $failure" }
    if ($CallerCase -eq 'uac-launch' -and $failure -notmatch '1223|administrator|elevat') { throw "Wrong launch error: $failure" }
    if ($CallerCase -eq 'elevated-child' -and ($failure -notmatch '37' -or $failure -match 'Could not obtain administrator privileges')) { throw "Child failure was misclassified: $failure" }
    $logs = @(Get-ChildItem -LiteralPath $script:FixtureRoot -Recurse -File | Where-Object { $_.Extension -eq '.log' -or $_.Name -eq 'mt-install-log.txt' })
    if (-not $logs.Count) { throw 'Installer failure discarded every diagnostic log.' }
    if ($CallerCase -eq 'elevated-child' -and -not @(Select-String -LiteralPath $logs.FullName -SimpleMatch -Pattern ($script:UnicodeDiagnostic + ' child exit 37') -Encoding UTF8).Count) { throw 'Unicode elevated child diagnostics were not retained.' }
    Write-Host "PASS: IEX $CallerCase; caller survived; original error and diagnostic log retained."
    return
}
if (-not $ServiceSmoke) {
    & {
        foreach ($name in @('Install-AsService','Normalize-InstallerBindAddress','Create-UninstallScript','Write-Log','Get-InstallExecutablePaths','Assert-InstallerCanStopTarget','Get-InstallOwnedProcesses','Stop-ExistingInstallProcesses')) { Invoke-Expression (Get-InstallerDefinition $name) }
        $InstallDir = Join-Path $EvidenceDirectory 'binary directory with spaces'
        $WIN_SERVICE_SETTINGS_DIR = Join-Path $EvidenceDirectory 'settings directory with spaces'
        New-Item -ItemType Directory -Path $InstallDir,$WIN_SERVICE_SETTINGS_DIR -Force | Out-Null
        $WebBinaryName = 'mt.exe'; $ServiceName = 'fixture-service'; $DisplayName = 'Fixture service'
        $script:LogInitialized = $false; $script:createdPath = $null; $script:started = $false; $script:existing = $false
        $script:ancestorMode = 'external'; $script:stopCalls = 0; $script:scCalls = @()
        function Get-CimInstance {
            param($ClassName,$Filter)
            if ($ClassName -eq 'Win32_Service') { if ($script:existing) { return @{ProcessId=712;Name='fixture-service'} }; return $null }
            if ($Filter -eq "ProcessId=$PID") { return @{ExecutablePath='C:\external\powershell.exe';ParentProcessId=712} }
            if ($Filter -eq 'ProcessId=712') {
                $path = if ($script:ancestorMode -eq 'target') { Join-Path $InstallDir 'mthost.exe' } else { 'C:\external\host.exe' }
                return @{ExecutablePath=$path;ParentProcessId=0}
            }
        }
        function Get-Process { return $script:candidates }
        function Get-Service { if ($script:existing) { return @{Status='Stopped'} }; return $null }
        function Stop-Service { $script:stopCalls++ }
        function Stop-Process { $script:stopCalls++ }
        function New-Service {
            param([string]$Name,[string]$BinaryPathName,[string]$DisplayName,[string]$StartupType)
            if ($StartupType -ne 'Automatic' -or $Name -ne 'fixture-service') { throw 'Typed SCM fields changed.' }
            $script:createdPath = $BinaryPathName
        }
        function Start-Service { $script:started = $true }
        function Register-Uninstall { }
        function sc.exe { $script:scCalls += ($args -join ' '); $global:LASTEXITCODE = 0 }
        function Invoke-CimMethod { return @{ReturnValue=5} }
        $script:candidates = @(@{Id=1;Path=(Join-Path $InstallDir 'mt.exe')},@{Id=2;Path=(Join-Path ($InstallDir+' other') 'mt.exe')},@{Id=3;Path=$null})
        $owned = @(Get-InstallOwnedProcesses -InstallDir $InstallDir)
        if ($owned.Count -ne 1 -or $owned[0].Id -ne 1) { throw 'Process selection crossed the exact install-directory boundary.' }
        $script:candidates = @()
        $script:ancestorMode = 'target'
        $rejected = $false
        try { Stop-ExistingInstallProcesses -InstallDir $InstallDir } catch { $rejected = $_ -match 'external' }
        if (-not $rejected -or $script:stopCalls) { throw 'Self-hosted installer did not reject before stopping processes/services.' }
        $script:ancestorMode = 'external'
        Assert-InstallerCanStopTarget -InstallDir $InstallDir -TargetServiceName $ServiceName
        Install-AsService -InstallDir $InstallDir -Version 'fixture' -Port 24443 -BindAddress localhost
        $expected = '"{0}" --service-mode --settings-dir "{1}" --service-name "fixture-service" --port 24443 --bind 127.0.0.1' -f (Join-Path $InstallDir 'mt.exe'), $WIN_SERVICE_SETTINGS_DIR
        if ($script:createdPath -cne $expected -or -not $script:started) { throw "Typed SCM BinaryPath/Start contract failed: $script:createdPath" }
        $script:existing = $true
        # The service-PID ancestry match must reject even without matching paths.
        $rejected = $false
        try { Assert-InstallerCanStopTarget -InstallDir $InstallDir -TargetServiceName $ServiceName } catch { $rejected = $_ -match 'external' }
        if (-not $rejected) { throw 'Service-PID ancestor was not protected.' }
        # Use an unrelated service PID for the in-place configuration failure.
        function Get-CimInstance { param($ClassName,$Filter) if ($ClassName -eq 'Win32_Service') { return @{ProcessId=999;Name='fixture-service'} }; return $null }
        $rejected = $false; $script:started = $false; $script:scCalls = @()
        try { Install-AsService -InstallDir $InstallDir -Version 'fixture' -Port 24443 -BindAddress localhost } catch { $rejected = $_ -match 'SCM result 5' }
        if (-not $rejected -or $script:started -or $script:scCalls.Count) { throw 'Failed SCM Change was swallowed or existing service entry was deleted.' }
        Write-Host 'PASS: exact typed SCM arguments, process-path boundary, self-host/PID protection and failed in-place Change.'
    }
    & {
        foreach ($name in @('Install-Tlbx','Install-AsService','Normalize-InstallerBindAddress','Get-InstallerServiceSnapshot','Restore-InstallerServiceSnapshot','Get-InstallerSettingsSnapshot','Restore-InstallerSettingsSnapshot','Initialize-Log','Write-Log')) { Invoke-Expression (Get-InstallerDefinition $name) }
        $WIN_SERVICE_INSTALL_DIR = Join-Path $EvidenceDirectory 'rollback binary path'
        $WIN_SERVICE_SETTINGS_DIR = Join-Path $EvidenceDirectory 'rollback settings path'
        $WebBinaryName = 'mt.exe'; $ServiceName = 'fixture-rollback'; $DisplayName = 'New fixture display'
        $WIN_SECRETS_FILENAME = 'secrets.bin'; $CertificateFileName = 'tlbx.pem'; $CertificateKeyId = 'tlbx'
        $script:InstallerTempRoot = [IO.Path]::GetTempPath()
        $script:release = @{tag_name='v10.25.33';prerelease=$false}
        $oldPath = '"{0}" --service-mode --settings-dir "{1}" --port 2000 --bind 127.0.0.1' -f (Join-Path $WIN_SERVICE_INSTALL_DIR 'mt.exe'), $WIN_SERVICE_SETTINGS_DIR
        function Assert-InstallerCanStopTarget { }
        function Get-AssetUrl { return 'https://fixture.invalid/release.zip' }
        function Invoke-CompatibleWebRequest { }
        function Assert-BuildArchive { }
        function Unblock-DownloadedPath { }
        function Assert-SafeReleaseArchive { }
        function Expand-Archive { }
        function Assert-SignedRelease { }
        function Assert-InstallerPortAvailable { }
        function Assert-VerifiedWindowsRuntime { }
        function Unblock-DownloadedTree { }
        function Get-ReleaseChannel { return 'stable' }
        function Install-VerifiedBinarySet { $script:rollbackEvents.Add('new-binaries') }
        function Restore-PreviousBinarySet { $script:rollbackEvents.Add('old-binaries') }
        function Generate-Certificate {
            foreach ($relative in $script:settingsFixtureFiles) {
                Set-Content -LiteralPath (Join-Path $WIN_SERVICE_SETTINGS_DIR $relative) -Value 'new installer state'
            }
            return 'fixture-certificate.pem'
        }
        function Register-Uninstall { }
        function Create-UninstallScript { }
        function Show-CertificateFingerprint { }
        function Trust-InstallerCertificate { throw 'Certificate trust changed before health verification.' }
        function Ensure-FirewallRule { throw 'Firewall changed before health verification.' }
        function Write-Section { }
        function Write-StatusLine { }
        function Wait-TlbxReady { return @{Ready=$false;ServiceStatus='Running';Process=$null;Bootstrap=$null;BootstrapUrl='https://localhost:2443'} }
        function Get-InstallOwnedProcesses { return $null }
        function Get-WinEvent { return $null }
        function Get-CimInstance { return $script:fixtureService }
        function Get-Service { return @{Status=$script:fixtureService.State} }
        function Stop-Service { $script:fixtureService.State='Stopped'; $script:rollbackEvents.Add('stop') }
        function Stop-ExistingInstallProcesses { Stop-Service }
        function Invoke-CimMethod {
            param($InputObject,$MethodName,$Arguments)
            $script:fixtureService.PathName=$Arguments.PathName
            $script:fixtureService.DisplayName=$Arguments.DisplayName
            $script:fixtureService.StartMode=$Arguments.StartMode
            if ($Arguments.PathName -eq $oldPath) { $script:rollbackEvents.Add('old-config') } else { $script:rollbackEvents.Add('new-config') }
            return @{ReturnValue=0}
        }
        function sc.exe { $global:LASTEXITCODE=0 }
        function Start-Service {
            if ($script:fixtureService.PathName -match '--port 2443 ') {
                if ($script:failurePhase -eq 'start') { throw 'fixture occupied port 2443' }
                $script:fixtureService.State='Running'; return
            }
            if ($script:fixtureService.PathName -cne $oldPath -or $script:fixtureService.DisplayName -cne 'Old fixture display' -or $script:fixtureService.StartMode -cne 'Manual') { throw 'Old service started before its original SCM configuration was restored.' }
            if ($script:rollbackEvents.IndexOf('old-binaries') -lt 0 -or $script:rollbackEvents.IndexOf('old-config') -lt $script:rollbackEvents.IndexOf('old-binaries')) { throw 'Rollback restart preceded binary/configuration restoration.' }
            foreach ($relative in $script:settingsFixtureFiles) {
                if ((Get-Content -LiteralPath (Join-Path $WIN_SERVICE_SETTINGS_DIR $relative) -Raw).Trim() -cne 'old installer state') { throw "Old service restarted with modified $relative." }
            }
            $script:rollbackEvents.Add('old-start'); $script:fixtureService.State='Running'
        }
        $script:settingsFixtureFiles = @('settings.json','merge-settings.json','runtime.json','secrets.bin','tlbx.pem','keys/tlbx.dpapi')
        foreach ($oldState in @('Running','Stopped')) { foreach ($script:failurePhase in @('start','health')) {
            foreach ($relative in $script:settingsFixtureFiles) {
                $path=Join-Path $WIN_SERVICE_SETTINGS_DIR $relative
                New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
                Set-Content -LiteralPath $path -Value 'old installer state'
            }
            $script:fixtureService = @{Name=$ServiceName;PathName=$oldPath;DisplayName='Old fixture display';StartMode='Manual';State=$oldState;ProcessId=999}
            $script:rollbackEvents = New-Object 'Collections.Generic.List[string]'
            $failure = $null
            try { Install-Tlbx -AsService $true -Version '10.25.33' -Port 2443 -BindAddress '*' -ConfigureFirewall $true -TrustCert $true } catch { $failure=$_.ToString() }
            $expectedFailure = if ($script:failurePhase -eq 'start') { 'fixture occupied port 2443' } else { 'did not pass the service and health verification' }
            if ($failure -notmatch $expectedFailure) { throw "Original $($script:failurePhase) failure did not propagate: $failure" }
            if ($script:fixtureService.PathName -cne $oldPath -or $script:fixtureService.DisplayName -cne 'Old fixture display' -or $script:fixtureService.StartMode -cne 'Manual') { throw 'Install-Tlbx trap did not restore original SCM configuration.' }
            if ($script:rollbackEvents.IndexOf('old-config') -le $script:rollbackEvents.IndexOf('old-binaries')) { throw 'Rollback configuration was not restored after old binaries.' }
            if (($oldState -eq 'Running') -ne $script:rollbackEvents.Contains('old-start')) { throw 'Rollback did not preserve the previous Running/Stopped state.' }
            foreach ($relative in $script:settingsFixtureFiles) {
                if ((Get-Content -LiteralPath (Join-Path $WIN_SERVICE_SETTINGS_DIR $relative) -Raw).Trim() -cne 'old installer state') { throw "Rollback left modified $relative." }
            }
            Write-Host "PASS: actual Install-Tlbx $($script:failurePhase) failure restores old binaries/settings/password/cert/config before restart; previous $oldState preserved."
        } }
    }
    foreach ($case in @('service-create', 'uac-launch', 'elevated-child')) {
        $stdout = Join-Path $EvidenceDirectory "$case-$($PSVersionTable.PSVersion.Major).stdout.log"
        $stderr = Join-Path $EvidenceDirectory "$case-$($PSVersionTable.PSVersion.Major).stderr.log"
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = (Get-Process -Id $PID).Path
        $info.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -CallerCase {1} -EvidenceDirectory "{2}"' -f $PSCommandPath, $case, $EvidenceDirectory
        $info.UseShellExecute = $false; $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        $child = [Diagnostics.Process]::Start($info)
        $outTask = $child.StandardOutput.ReadToEndAsync(); $errTask = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(60000)) { $child.Kill(); throw "Caller fixture timed out: $case" }
        [IO.File]::WriteAllText($stdout,$outTask.GetAwaiter().GetResult()); [IO.File]::WriteAllText($stderr,$errTask.GetAwaiter().GetResult())
        if ($child.ExitCode -ne 0) { throw "Caller fixture failed ($case, exit $($child.ExitCode)); see $stderr" }
        $marker = Join-Path (Join-Path $EvidenceDirectory "$case-$($PSVersionTable.PSVersion.Major)") 'caller-marker.txt'
        if (-not (Test-Path -LiteralPath $marker)) { throw "IEX terminated its caller: $case" }
    }
    Write-Host "Windows PowerShell $($PSVersionTable.PSVersion) installer caller contracts passed."
    return
}

# Actual SCM mutations are only permitted on disposable administrative CI runners.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Real SCM fixture requires an administrative GitHub Actions runner; never run it on the installed host.' }
foreach ($name in @('Install-AsService','Normalize-InstallerBindAddress','Create-UninstallScript','Write-Log','Get-InstallExecutablePaths','Assert-InstallerCanStopTarget','Get-InstallOwnedProcesses','Assert-SafeReleaseArchive','Get-WindowsReleaseFileNames','Assert-SignedRelease','Test-ReleaseMetadataSignature','Convert-DerEcdsaSignatureToP1363','Invoke-TlbxBootstrapRequest','Get-TlbxAccessUrls','Format-TlbxUrl','Test-TailscaleIpv4Address','Get-TlbxProcess','Wait-TlbxReady','Invoke-InstallerVersionProbe','Assert-VerifiedWindowsRuntime','Test-ExistingCertificate')) { Invoke-Expression (Get-InstallerDefinition $name) }
function Register-Uninstall { param($InstallDir,$Version,$IsService) }
$rid = 'win-x64'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$catalog = Invoke-RestMethod -Uri 'https://builds.tlbx.ai/v1/channels/main/releases.json'
$release = $catalog | Where-Object { -not $_.draft -and -not $_.prerelease -and @($_.assets | Where-Object name -eq "mt-$rid.zip").Count } | Select-Object -First 1
$version = $release.tag_name -replace '^v',''
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid stable fixture version.' }
$asset = $release.assets | Where-Object name -eq "mt-$rid.zip" | Select-Object -First 1
if (-not $asset -or $asset.browser_download_url -cne "https://builds.tlbx.ai/main/$version/$rid/mt-$rid.zip" -or $asset.digest -notmatch '^sha256:[a-f0-9]{64}$') { throw 'Signed stable fixture is not available from the trusted catalog.' }
if (-not $ArchivePath) { $ArchivePath = Join-Path $EvidenceDirectory "mt-$version-$rid.zip" }
if (-not (Test-Path -LiteralPath $ArchivePath)) { Invoke-WebRequest -UseBasicParsing -Uri $asset.browser_download_url -OutFile $ArchivePath }
if ((Get-Item -LiteralPath $ArchivePath).Length -ne $asset.size -or (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ine $asset.digest.Substring(7)) { throw 'Stable33 archive size/digest mismatch.' }
$fixtureRoot = Join-Path $env:RUNNER_TEMP ('tlbx service fixture ' + [Guid]::NewGuid().ToString('N'))
$InstallDir = Join-Path $fixtureRoot 'binary directory with spaces'
$WIN_SERVICE_SETTINGS_DIR = Join-Path $fixtureRoot 'settings directory with spaces'
$script:ServiceName = 'tlbx-installer-fixture-' + [Guid]::NewGuid().ToString('N')
$DisplayName = $ServiceName; $WebBinaryName = 'mt.exe'; $UninstallRegistryName = $ServiceName
$TtyHostBinaryName = 'mthost.exe'; $AgentHostBinaryName = 'mtagenthost.exe'; $TmuxShimBinaryName = 'mttmux.exe'
$CertificateFileName = 'tlbx.pem'; $CertificateKeyId = 'tlbx'; $ExpectedReleasePlatform = $rid
$script:UpdateLogFile = Join-Path $EvidenceDirectory "service-$($PSVersionTable.PSVersion.Major).log"; $script:LogInitialized = $true
$listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$serviceCreated = $false
try {
    New-Item -ItemType Directory -Path $InstallDir,$WIN_SERVICE_SETTINGS_DIR -Force | Out-Null
    Assert-SafeReleaseArchive -Path $ArchivePath
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $InstallDir
    Assert-SignedRelease -ExtractDir $InstallDir -ExpectedVersion $version -ExpectedPlatform $rid -ExpectedChannel stable
    foreach ($file in @('mt.exe','mthost.exe','mtagenthost.exe','mttmux.exe')) { if ((Get-AuthenticodeSignature -LiteralPath (Join-Path $InstallDir $file)).Status -ne 'Valid') { throw "Authenticode verification failed: $file" } }
    Assert-VerifiedWindowsRuntime -ExtractDir $InstallDir -ExpectedVersion $version
    # mt creates a private self-signed certificate in this explicit settings path.
    # No trust-store, firewall, or global uninstall changes.
    $serviceCreated = $true
    Install-AsService -InstallDir $InstallDir -Version $version -Port $port -BindAddress localhost
    $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $expected = '"{0}" --service-mode --settings-dir "{1}" --service-name "{2}" --port {3} --bind 127.0.0.1' -f (Join-Path $InstallDir 'mt.exe'), $WIN_SERVICE_SETTINGS_DIR, $ServiceName, $port
    if ($service.PathName -cne $expected) { throw "SCM BinaryPath changed: expected=$expected; actual=$($service.PathName)" }
    if ($service.State -ne 'Running' -or -not $service.ProcessId) { throw "SCM state is $($service.State)" }
    # A corporate/system proxy and a pre-existing TLS callback must not break
    # loopback readiness or be overwritten by the installer in an IEX shell.
    $savedProxy = [Net.WebRequest]::DefaultWebProxy
    $savedCallback = [Net.ServicePointManager]::ServerCertificateValidationCallback
    $savedHttpsProxy = $env:HTTPS_PROXY; $savedNoProxy = $env:NO_PROXY
    try {
        $sentinel = [Net.Security.RemoteCertificateValidationCallback]{ return $false }
        [Net.ServicePointManager]::ServerCertificateValidationCallback = $sentinel
        [Net.WebRequest]::DefaultWebProxy = New-Object Net.WebProxy('http://127.0.0.1:1')
        $env:HTTPS_PROXY = 'http://127.0.0.1:1'; $env:NO_PROXY = ''
        $readiness = Wait-TlbxReady -ExecutablePath (Join-Path $InstallDir 'mt.exe') -Port $port -BindAddress localhost -AsService $true -ExpectedProcessId $service.ProcessId
        if (-not [object]::ReferenceEquals($sentinel,[Net.ServicePointManager]::ServerCertificateValidationCallback)) { throw 'Installer changed global TLS validation.' }
    } finally {
        [Net.WebRequest]::DefaultWebProxy = $savedProxy
        [Net.ServicePointManager]::ServerCertificateValidationCallback = $savedCallback
        $env:HTTPS_PROXY = $savedHttpsProxy; $env:NO_PROXY = $savedNoProxy
    }
    if (-not $readiness.Ready -or $readiness.ServiceStatus -ne 'Running' -or -not $readiness.Bootstrap.certificate.fingerprint) { throw 'SCM service failed real HTTPS bootstrap health.' }
    $existingCertificate = Test-ExistingCertificate -SettingsDir $WIN_SERVICE_SETTINGS_DIR -IsService $true
    if (-not $existingCertificate) { throw 'Real LocalSystem service certificate/private key pair did not validate.' }
    $existingCertificate.Certificate.Dispose()
    [pscustomobject]@{shell=$PSVersionTable.PSVersion.ToString();service=$ServiceName;binaryPath=$service.PathName;state=$service.State;pid=$service.ProcessId;port=$port;version=$readiness.Version;https=$readiness.BootstrapUrl;fingerprint=$readiness.Bootstrap.certificate.fingerprint;sha256=(Get-FileHash $ArchivePath).Hash} | ConvertTo-Json | Set-Content (Join-Path $EvidenceDirectory "service-$($PSVersionTable.PSVersion.Major)-proof.json")
    Write-Host "PASS: real SCM create/query exact BinaryPath/start/Running/HTTPS; PowerShell $($PSVersionTable.PSVersion); signed stable $version archive."
} finally {
    if ($serviceCreated) {
        $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
        if ($service) {
            if ($service.PathName -notlike ('"' + $InstallDir + '\*')) { throw 'Refusing to remove fixture service with unexpected BinaryPath.' }
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            $controller = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if ($controller) { $controller.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20)); $controller.Dispose() }
            # Release unrooted ServiceController handles before SCM deletion.
            [GC]::Collect(); [GC]::WaitForPendingFinalizers()
            & sc.exe delete $ServiceName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Fixture service deletion failed.' }
            for ($attempt=0; $attempt -lt 40; $attempt++) {
                if (-not (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue)) { break }
                Start-Sleep -Milliseconds 250
            }
            if (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue) { throw 'Fixture service is still registered after deletion.' }
        }
    }
    if (Test-Path -LiteralPath $WIN_SERVICE_SETTINGS_DIR) {
        $diagnosticDir = Join-Path $EvidenceDirectory "settings-$($PSVersionTable.PSVersion.Major)"
        New-Item -ItemType Directory -Path $diagnosticDir -Force | Out-Null
        Get-ChildItem -LiteralPath $WIN_SERVICE_SETTINGS_DIR -Recurse -File | Where-Object { $_.Extension -in @('.log','.jsonl') } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $diagnosticDir -Force }
    }
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $runnerRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($runnerRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^tlbx service fixture [a-f0-9]{32}$') { throw 'Invalid fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
