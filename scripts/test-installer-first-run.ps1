# Behavioral first-install contracts. Compatible with Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'install.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw "Installer parser errors: $($errors.Message -join '; ')" }
$names = @('New-InstallerPasswordHash', 'ConvertFrom-InstallerSecureString', 'Prompt-Password', 'Prompt-FirewallConfig', 'Read-InstallerYesNo', 'Test-NetworkBinding', 'Invoke-CompatibleWebRequest', 'Invoke-CompatibleRestMethod')
foreach ($name in $names) {
    $definition = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name}, $true)
    if (-not $definition) { throw "Installer function missing: $name" }
    Invoke-Expression $definition.Extent.Text
}

$samples = @('1234567890', ('a' * 1024), ('Unicode_' + [char]0x03a9 + [char]0x00e4 + '\ $`" tail '))
foreach ($password in $samples) {
    $hash = New-InstallerPasswordHash $password
    $parts = $hash.Split('$')
    if ($parts.Length -ne 5 -or $parts[1] -cne 'PBKDF2' -or $parts[2] -ne '600000' -or
        [Convert]::FromBase64String($parts[3]).Length -ne 32 -or [Convert]::FromBase64String($parts[4]).Length -ne 32) {
        throw 'First-install password hash is not the native PBKDF2-SHA256 contract.'
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($password)
    $derive = [Security.Cryptography.Rfc2898DeriveBytes]::new($bytes, [Convert]::FromBase64String($parts[3]), 600000, [Security.Cryptography.HashAlgorithmName]::SHA256)
    try { if ([Convert]::ToBase64String($derive.GetBytes(32)) -cne $parts[4]) { throw 'Password bytes changed while hashing.' } }
    finally { $derive.Dispose(); [Array]::Clear($bytes, 0, $bytes.Length) }
}
if ((New-InstallerPasswordHash $samples[0]) -ceq (New-InstallerPasswordHash $samples[0])) { throw 'Password salt was reused.' }
foreach ($password in @('123456789', ('a' * 1025))) {
    $rejected = $false
    try { New-InstallerPasswordHash $password | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Installer accepted an out-of-range password.' }
}
$secret = ConvertTo-SecureString $samples[2] -AsPlainText -Force
try { if ((ConvertFrom-InstallerSecureString $secret) -cne $samples[2]) { throw 'Secure password conversion changed Unicode or whitespace.' } }
finally { $secret.Dispose() }

& {
    # Differing case must not count as confirmation. Retry and accept ten chars.
    $script:answers = New-Object 'Collections.Generic.Queue[string]'
    foreach ($answer in @('Abcdefghij', 'abcdefghij', '1234567890', '1234567890')) { $script:answers.Enqueue($answer) }
    function Read-Host { param($Prompt, [switch]$AsSecureString) return ConvertTo-SecureString ($script:answers.Dequeue()) -AsPlainText -Force }
    $result = Prompt-Password
    if ($script:answers.Count -ne 0 -or $result -notmatch '^\$PBKDF2\$600000\$') { throw 'Password confirmation/retry contract failed.' }
}
& {
    $script:shown = New-Object 'Collections.Generic.List[string]'
    function Write-Host { param([Parameter(Position=0)]$Object, $ForegroundColor) $script:shown.Add([string]$Object) }
    function Read-Host { param($Prompt) return 'y' }
    if (-not (Prompt-FirewallConfig -Port 2443 -BindAddress '0.0.0.0')) { throw 'Firewall consent was lost.' }
    if (-not @($script:shown | Where-Object { $_.Contains('2443?') }).Count) { throw 'Firewall prompt omitted the configured port.' }
}
& {
    $script:attempts = 0
    function Start-Sleep { param($Seconds) }
    function Invoke-WebRequest { param($Uri, $OutFile, $TimeoutSec, [switch]$UseBasicParsing) $script:attempts++; if ($script:attempts -lt 3) { throw 'Temporary download outage' }; return 'downloaded' }
    if ((Invoke-CompatibleWebRequest -Uri 'https://example.test/release.zip' -OutFile 'unused.zip') -ne 'downloaded' -or $script:attempts -ne 3) { throw 'Download retry failed.' }
    $script:attempts = 0
    function Invoke-RestMethod { param($Uri, $Headers, $TimeoutSec, [switch]$UseBasicParsing) $script:attempts++; throw 'Persistent outage' }
    $rejected = $false
    try { Invoke-CompatibleRestMethod -Uri 'https://example.test/releases' | Out-Null } catch { $rejected = $true }
    if (-not $rejected -or $script:attempts -ne 3) { throw 'Network retries were unbounded or failure was swallowed.' }
}
Write-Host 'First-install password, confirmation, configured port and bounded download contracts passed.' -ForegroundColor Green