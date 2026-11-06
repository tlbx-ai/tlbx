[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { return }
$source = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'install.ps1') -Raw -Encoding UTF8
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors.Message -join '; ') }
foreach ($name in @('Assert-InstallerPortAvailable','Invoke-TlbxBootstrapRequest')) {
    $node = $ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name}, $true)
    if (-not $node) { throw "Missing installer function $name" }
    Invoke-Expression $node.Extent.Text
}
function Write-Log { }
& {
    $script:FixtureListeners = @()
    $ServiceName = 'fixture'
    $script:FixtureService = $null
    function Get-CimInstance { return $script:FixtureService }
    function Get-NetTCPConnection { return $script:FixtureListeners }
    function Get-InstallOwnedProcesses { return @{Id=111} }
    Assert-InstallerPortAvailable -Port 24443 -BindAddress localhost -InstallDir 'C:\fixture'
    $script:FixtureListeners = @(@{LocalPort=24443;OwningProcess=111})
    Assert-InstallerPortAvailable -Port 24443 -BindAddress localhost -InstallDir 'C:\fixture'
    $script:FixtureListeners += @{LocalPort=24443;OwningProcess=222}
    $failure = $null
    try { Assert-InstallerPortAvailable -Port 24443 -BindAddress localhost -InstallDir 'C:\fixture' } catch { $failure = $_.ToString() }
    if ($failure -notmatch '24443.*PID 222' -or $failure -notmatch 'not been stopped') { throw "Conflicting listener was not rejected before teardown: $failure" }
    $script:FixtureListeners = @(@{LocalPort=24443;OwningProcess=333})
    $script:FixtureService = @{ProcessId=333;PathName='"C:\fixture\mt.exe" --service-mode'}
    Assert-InstallerPortAvailable -Port 24443 -BindAddress localhost -InstallDir 'C:\fixture'
    $script:FixtureService.PathName='"C:\other\mt.exe" --service-mode'
    $failure = $null
    try { Assert-InstallerPortAvailable -Port 24443 -BindAddress localhost -InstallDir 'C:\fixture' } catch { $failure=$_.ToString() }
    if ($failure -notmatch 'PID 333') { throw 'SCM ownership check crossed installation paths.' }
    Write-Host 'PASS: free port, existing target listener and foreign listener rejection.'
}
& {
    function Get-NetTCPConnection { throw 'fixture missing/disabled NetTCPIP module' }
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
    $listener.ExclusiveAddressUse=$true
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    try {
        $failure=$null
        try { Assert-InstallerPortAvailable -Port $port -BindAddress localhost -InstallDir 'C:\fixture' } catch { $failure=$_.ToString() }
        if ($failure -notmatch 'could not be reserved') { throw "Socket fallback accepted an occupied port: $failure" }
    } finally { $listener.Stop() }
    Assert-InstallerPortAvailable -Port $port -BindAddress localhost -InstallDir 'C:\fixture'
    Write-Host 'PASS: real loopback socket fallback rejects occupied port and accepts released port.'
}
if ($PSVersionTable.PSVersion.Major -lt 6) {
    $savedCallback=[Net.ServicePointManager]::ServerCertificateValidationCallback
    $savedProxy=[Net.WebRequest]::DefaultWebProxy
    try {
        $sentinel=[Net.Security.RemoteCertificateValidationCallback]{ return $false }
        [Net.ServicePointManager]::ServerCertificateValidationCallback=$sentinel
        [Net.WebRequest]::DefaultWebProxy=New-Object Net.WebProxy('http://127.0.0.1:1')
        $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
        $listener.Start(); $port=$listener.LocalEndpoint.Port; $listener.Stop()
        try { Invoke-TlbxBootstrapRequest -Url "https://127.0.0.1:$port" | Out-Null } catch { }
        if (-not [object]::ReferenceEquals($sentinel,[Net.ServicePointManager]::ServerCertificateValidationCallback)) { throw 'Readiness probe changed the caller global TLS callback.' }
        Write-Host 'PASS: failed Windows PowerShell HTTPS probe preserves caller TLS validation.'
    } finally {
        [Net.ServicePointManager]::ServerCertificateValidationCallback=$savedCallback
        [Net.WebRequest]::DefaultWebProxy=$savedProxy
    }
}
