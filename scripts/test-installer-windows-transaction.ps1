[CmdletBinding()]
param([string]$EvidenceDirectory = '')
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { return }
$repoRoot = Split-Path -Parent $PSScriptRoot
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'install.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors.Message -join '; ') }
foreach ($name in @('Get-WindowsReleaseFileNames', 'Install-VerifiedBinarySet', 'Restore-PreviousBinarySet', 'Get-InstallerSettingsSnapshot', 'Restore-InstallerSettingsSnapshot')) {
    $node = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    if (-not $node) { throw "Missing installer function $name" }
    Invoke-Expression $node.Extent.Text
}
$script:ExpectedReleasePlatform = 'win-x64'
$script:WebBinaryName = 'mt.exe'; $script:TtyHostBinaryName = 'mthost.exe'
$script:AgentHostBinaryName = 'mtagenthost.exe'; $script:TmuxShimBinaryName = 'mttmux.exe'
$script:CertificateFileName = 'tlbx.pem'; $script:CertificateKeyId = 'tlbx'
$script:Files = @(Get-WindowsReleaseFileNames)
if (-not $EvidenceDirectory) { $EvidenceDirectory = [IO.Path]::GetTempPath() }
$fixtureRoot = Join-Path $EvidenceDirectory ('tlbx-transaction-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$script:Cases = 0
function Assert-Contract([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function New-TransactionFixture([string]$Name, [string]$AbsentOriginal = '') {
    $base = Join-Path $fixtureRoot $Name
    $fixture = @{ Install = (Join-Path $base 'install path'); Extract = (Join-Path $base 'extract path'); Backup = (Join-Path $base 'transaction path') }
    New-Item -ItemType Directory -Path $fixture.Install, $fixture.Extract, $fixture.Backup -Force | Out-Null
    foreach ($file in $script:Files) {
        foreach ($kind in @('Install', 'Extract')) {
            if ($kind -eq 'Install' -and $file -eq $AbsentOriginal) { continue }
            $path = Join-Path $fixture[$kind] $file
            New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
            [IO.File]::WriteAllText($path, "$kind/$file", [Text.Encoding]::UTF8)
        }
    }
    $script:Current = $fixture
    $script:Fault = ''; $script:CopyCount = 0; $script:MoveCount = 0
    $script:Restored = New-Object System.Collections.Generic.List[string]
    return $fixture
}
function Assert-OriginalFiles($Fixture, [string]$AbsentOriginal = '') {
    foreach ($file in $script:Files) {
        $path = Join-Path $Fixture.Install $file
        if ($file -eq $AbsentOriginal) { Assert-Contract (-not (Test-Path -LiteralPath $path)) "Unexpected newly introduced file $file" }
        else { Assert-Contract ((Test-Path -LiteralPath $path) -and [IO.File]::ReadAllText($path) -ceq "Install/$file") "Original changed/lost: $file" }
    }
    Assert-Contract (@(Get-ChildItem -LiteralPath $Fixture.Install -Force -Directory | Where-Object Name -like '.tlbx-install-*').Count -eq 0) 'Staging directory leaked after failure.'
}
function Copy-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$Destination, [switch]$Force)
    $backup = Join-Path $script:Current.Backup 'backup'
    $settingsBackup = Join-Path $script:Current.Backup 'settings-backup'
    if ($script:Fault -eq 'settings-backup' -and $Destination.StartsWith($settingsBackup + '\', [StringComparison]::OrdinalIgnoreCase)) {
        $script:CopyCount++
        if ($script:CopyCount -eq 2) { throw 'fixture settings backup failure' }
    }
    if ($script:Fault -eq 'settings-restore' -and $LiteralPath.StartsWith($settingsBackup + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'fixture settings restore failure' }
    if ($LiteralPath.StartsWith($backup + '\', [StringComparison]::OrdinalIgnoreCase)) {
        $script:Restored.Add($LiteralPath.Substring($backup.Length + 1))
        if ($script:Fault -in @('rollback', 'restore')) { throw 'fixture restore failure' }
    }
    elseif (($script:Fault -eq 'backup' -and $Destination.StartsWith($backup + '\', [StringComparison]::OrdinalIgnoreCase)) -or
            ($script:Fault -eq 'stage' -and $LiteralPath.StartsWith($script:Current.Extract + '\', [StringComparison]::OrdinalIgnoreCase))) {
        $script:CopyCount++
        if ($script:CopyCount -eq 2) { throw "fixture $($script:Fault) failure" }
    }
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force -ErrorAction Stop
}
function Move-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$Destination, [switch]$Force)
    $script:MoveCount++
    Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force -ErrorAction Stop
    # Deliberately throw AFTER mutation to prove the current failed operation
    # participates in rollback as well as earlier successful moves.
    if ($script:Fault -in @('move', 'rollback') -and $script:MoveCount -eq 2) { throw 'fixture partial move failure' }
}
function Invoke-TransactionFailure($Fixture) {
    $failure = $null
    try { Install-VerifiedBinarySet -ExtractDir $Fixture.Extract -InstallDir $Fixture.Install -BackupRoot $Fixture.Backup }
    catch { $failure = $_.Exception }
    Assert-Contract ($null -ne $failure) 'Injected transaction failure was swallowed.'
    return $failure
}
try {
    $fixture = New-TransactionFixture 'backup failure'
    $script:Fault = 'backup'
    $failure = Invoke-TransactionFailure $fixture
    Assert-Contract ($failure.Message -match 'fixture backup failure') 'Original backup error was lost.'
    Assert-Contract ($script:MoveCount -eq 0) 'Swap began before all backups succeeded.'
    Assert-OriginalFiles $fixture
    $script:Cases++

    $fixture = New-TransactionFixture 'stage failure'
    $script:Fault = 'stage'
    $null = Invoke-TransactionFailure $fixture
    Assert-Contract ($script:MoveCount -eq 0) 'Swap began after incomplete staging.'
    Assert-OriginalFiles $fixture
    $script:Cases++

    $absent = $script:Files[1]
    $fixture = New-TransactionFixture 'partial move' $absent
    $script:Fault = 'move'
    $failure = Invoke-TransactionFailure $fixture
    Assert-Contract ($failure.Message -match 'partial move failure') 'Original move error was lost.'
    Assert-OriginalFiles $fixture $absent
    Assert-Contract ($script:Restored.Count -eq 1 -and $script:Restored[0] -eq $script:Files[0]) 'Rollback touched an unchanged file.'
    $script:Cases++

    $fixture = New-TransactionFixture 'failed rollback'
    $script:Fault = 'rollback'
    $failure = Invoke-TransactionFailure $fixture
    Assert-Contract ($failure.Data['TlbxPreserveBackupRoot'] -eq $fixture.Backup) 'Recovery backup retention marker is missing.'
    Assert-Contract ($failure.Message -match 'partial move failure' -and $failure.Message -match 'restore failure') 'Primary or rollback error was lost.'
    foreach ($file in $script:Files) {
        Assert-Contract ([IO.File]::ReadAllText((Join-Path (Join-Path $fixture.Backup 'backup') $file)) -ceq "Install/$file") "Recovery backup lost $file"
    }
    $script:Cases++

    $fixture = New-TransactionFixture 'restore copy failure'
    Install-VerifiedBinarySet -ExtractDir $fixture.Extract -InstallDir $fixture.Install -BackupRoot $fixture.Backup
    $script:Fault = 'restore'
    $failure = $null
    try { Restore-PreviousBinarySet -InstallDir $fixture.Install -BackupRoot $fixture.Backup }
    catch { $failure = $_.Exception }
    Assert-Contract ($null -ne $failure -and $failure.Data['TlbxPreserveBackupRoot'] -eq $fixture.Backup) 'Restore failure was not recoverable.'
    Assert-Contract ([IO.File]::ReadAllText((Join-Path $fixture.Install $script:Files[0])) -ceq "Extract/$($script:Files[0])") 'Failed restore prematurely deleted the destination.'
    $script:Cases++

    $fixture = New-TransactionFixture 'successful swap and restore' $absent
    Install-VerifiedBinarySet -ExtractDir $fixture.Extract -InstallDir $fixture.Install -BackupRoot $fixture.Backup
    foreach ($file in $script:Files) {
        Assert-Contract ([IO.File]::ReadAllText((Join-Path $fixture.Install $file)) -ceq "Extract/$file") "Swap did not install $file"
    }
    Restore-PreviousBinarySet -InstallDir $fixture.Install -BackupRoot $fixture.Backup
    Assert-OriginalFiles $fixture $absent
    $script:Cases++
    $fixture = New-TransactionFixture 'settings snapshot restore'
    $settings = Join-Path (Split-Path -Parent $fixture.Install) 'settings path'
    $existingSettings = @('settings.json', 'secrets.bin', 'keys/tlbx.dpapi')
    foreach ($file in $existingSettings) {
        $path = Join-Path $settings $file
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, "original/$file", [Text.Encoding]::UTF8)
    }
    $database = Join-Path $settings 'action-graphs.db'
    [IO.File]::WriteAllText($database, 'session data')
    $snapshot = Get-InstallerSettingsSnapshot -SettingsDir $settings -BackupRoot $fixture.Backup
    foreach ($file in $snapshot.Files) { [IO.File]::WriteAllText((Join-Path $settings $file), "changed/$file", [Text.Encoding]::UTF8) }
    Restore-InstallerSettingsSnapshot -Snapshot $snapshot
    foreach ($file in $snapshot.Files) {
        $path = Join-Path $settings $file
        if ($file -in $existingSettings) { Assert-Contract ([IO.File]::ReadAllText($path) -ceq "original/$file") "Settings were not restored: $file" }
        else { Assert-Contract (-not (Test-Path -LiteralPath $path)) "New settings file survived rollback: $file" }
    }
    Assert-Contract ([IO.File]::ReadAllText($database) -ceq 'session data') 'Settings rollback touched the session database.'
    $script:Cases++

    $fixture = New-TransactionFixture 'settings backup failure'
    $script:Fault = 'settings-backup'
    $failure = $null
    try { $null = Get-InstallerSettingsSnapshot -SettingsDir $settings -BackupRoot $fixture.Backup }
    catch { $failure = $_.Exception }
    Assert-Contract ($null -ne $failure -and $failure.Message -match 'settings backup failure') 'Settings backup failure was swallowed.'
    foreach ($file in $existingSettings) { Assert-Contract ([IO.File]::ReadAllText((Join-Path $settings $file)) -ceq "original/$file") "Failed settings backup changed $file" }
    $script:Cases++

    $fixture = New-TransactionFixture 'settings restore failure'
    $snapshot = Get-InstallerSettingsSnapshot -SettingsDir $settings -BackupRoot $fixture.Backup
    $script:Fault = 'settings-restore'
    $failure = $null
    try { Restore-InstallerSettingsSnapshot -Snapshot $snapshot }
    catch { $failure = $_.Exception }
    Assert-Contract ($null -ne $failure -and $failure.Data['TlbxPreserveBackupRoot'] -eq $fixture.Backup) 'Failed settings restore lost its recovery marker.'
    foreach ($file in $existingSettings) { Assert-Contract ([IO.File]::ReadAllText((Join-Path (Join-Path $fixture.Backup 'settings-backup') $file)) -ceq "original/$file") "Failed settings restore lost backup $file" }
    $script:Cases++
    Write-Host "Windows binary transaction: $script:Cases behavioral cases passed."
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}
