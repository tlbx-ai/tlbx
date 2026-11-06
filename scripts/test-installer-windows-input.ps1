# Focused Windows installer input contracts; no installation/SCM/trust mutations.
[CmdletBinding()]
param([string]$EvidenceDirectory = '')
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { return }
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Get-Content (Join-Path $repoRoot 'install.ps1') -Raw -Encoding UTF8
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors.Message -join '; ')}
foreach($name in @('Read-InstallerChoice','Read-InstallerYesNo','Normalize-InstallerBindAddress','Assert-InstallerOptions','Import-ElevatedReplayFile','Prompt-NetworkConfig','Prompt-ExistingPasswordAction','Prompt-FirewallConfig','Test-NetworkBinding')) {
    $node=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    if(-not $node){throw "Missing input function: $name"}
    Invoke-Expression $node.Extent.Text
}
if(-not $EvidenceDirectory){$EvidenceDirectory=Join-Path $repoRoot '.artifacts/installer-windows-input'}
New-Item -ItemType Directory -Path $EvidenceDirectory -Force|Out-Null
function Set-Answers([string[]]$Values) {
    $script:answers=New-Object 'Collections.Generic.Queue[string]'
    foreach($value in $Values){$script:answers.Enqueue($value)}
}
function Read-Host { param($Prompt) if(-not $script:answers.Count){throw "Unexpected extra prompt: $Prompt"}; return $script:answers.Dequeue() }
function Assert-Rejected([scriptblock]$Action,[string]$Pattern) {
    $failure=$null;try{& $Action|Out-Null}catch{$failure=$_.Exception.Message}
    if(-not $failure -or $failure -notmatch $Pattern){throw "Expected rejection '$Pattern', got '$failure'."}
}
Set-Answers @('999999999999999999999999','2443',' 2 ')
$network=Prompt-NetworkConfig
if($network.Port -ne 2443 -or $network.BindAddress -ne 'localhost' -or $script:answers.Count){throw 'Oversized port did not retry or binding whitespace changed the choice.'}
Set-Answers @('0','65536','bogus')
Assert-Rejected {Prompt-NetworkConfig} 'invalid ports'
Set-Answers @('2443','bad','bad','bad')
Assert-Rejected {Prompt-NetworkConfig} 'invalid choices'
Set-Answers @('','')
$network=Prompt-NetworkConfig
if($network.Port -ne 2000 -or $network.BindAddress -ne '*'){throw 'Explicit empty answers changed intentional defaults.'}
foreach($choice in @(' no ','NEIN','N')) {
    Set-Answers @($choice)
    if(Prompt-FirewallConfig -Port 2443 -BindAddress '*'){throw "Firewall consent inferred from '$choice'."}
}
foreach($choice in @('yes','JA',' Y ')) {
    Set-Answers @($choice)
    if(-not (Read-InstallerYesNo -Prompt 'fixture' -DefaultValue $false)){throw "Affirmative answer lost: $choice"}
}
Set-Answers @('mistyped','no')
if(Read-InstallerYesNo -Prompt 'fixture'){throw 'Invalid answer became consent instead of retry.'}
Set-Answers @('bad','bad','bad')
Assert-Rejected {Read-InstallerYesNo -Prompt 'fixture'} 'invalid answers'
Set-Answers @('bad','bad','bad')
Assert-Rejected {Read-InstallerChoice -Prompt 'fixture' -Choices @('1','2') -DefaultValue '1'} 'invalid choices'
Set-Answers @(' 2 ')
if((Prompt-ExistingPasswordAction) -ne 'Replace'){throw 'Explicit password replace choice was not trimmed.'}
$script:probePorts=New-Object 'Collections.Generic.List[int]'
function Assert-InstallerPortAvailable {
    param($Port,$BindAddress,$InstallDir)
    $script:probePorts.Add($Port)
    if($Port -eq 2443){throw 'Fixture port 2443 is already occupied.'}
}
Set-Answers @('2443','2444','2')
$network=Prompt-NetworkConfig -InstallDir (Join-Path $EvidenceDirectory 'fixture install')
if($network.Port -ne 2444 -or $script:probePorts.Count -ne 2 -or $script:probePorts[0] -ne 2443 -or $script:probePorts[1] -ne 2444){throw 'Occupied port was not retried before further configuration.'}
foreach($bind in @('localhost','127.0.0.1','::1',' LOCALHOST ')) {
    if((Normalize-InstallerBindAddress $bind) -ne 'localhost'){throw "Loopback widened: $bind"}
}
foreach($bind in @('*','0.0.0.0','')){if((Normalize-InstallerBindAddress $bind) -ne '*'){throw 'Wildcard default changed.'}}
Assert-Rejected {Normalize-InstallerBindAddress '127.0.0.2'} 'Invalid bind'
foreach($port in @(-1,0,65536,'9999999999999999999','1.5')){Assert-Rejected {Assert-InstallerOptions -Port $port -BindAddress localhost} 'Invalid port'}
$options=Assert-InstallerOptions -Port 65535 -BindAddress '127.0.0.1'
if($options.Port -ne 65535 -or $options.BindAddress -ne 'localhost'){throw 'Valid parameter boundary/normalization failed.'}
$hash='$PBKDF2$600000$'+[Convert]::ToBase64String((New-Object byte[] 32))+'$'+[Convert]::ToBase64String((New-Object byte[] 32))
$valid=@{runAsUser=('fixture'+[char]0x00E4+[char]0x03A9);runAsUserSid='S-1-5-21-1';passwordHash=$hash;port=2443;bindAddress='::1';configureFirewall=$false;trustCert=$false;dev=$true}
$replayFile=Join-Path $EvidenceDirectory 'replay [input].json'
$valid|ConvertTo-Json|Set-Content -LiteralPath $replayFile -Encoding UTF8
Import-ElevatedReplayFile $replayFile
if($script:Port -ne 2443 -or $script:BindAddress -ne 'localhost' -or $script:TrustCert -or $script:ConfigureFirewall -or -not $script:Dev -or $script:RunAsUser -cne $valid.runAsUser -or $script:PasswordHash -cne $hash){throw 'Valid Unicode replay was changed.'}
foreach($change in @(@{configureFirewall='false'},@{trustCert='false'},@{dev='false'},@{port=0},@{port='999999999999999999'},@{bindAddress='invalid'},@{passwordHash='plaintext'},@{runAsUserSid='bad'},@{runAsUser=''})) {
    $invalid=$valid.Clone();foreach($key in $change.Keys){$invalid[$key]=$change[$key]}
    $invalid|ConvertTo-Json|Set-Content -LiteralPath $replayFile -Encoding UTF8
    $script:Port=12345
    Assert-Rejected {Import-ElevatedReplayFile $replayFile} 'Invalid|must be|requires'
    if($script:Port -ne 12345){throw 'Rejected replay partially changed installer options.'}
}
$invalid=$valid.Clone();$invalid.Remove('trustCert');$invalid|ConvertTo-Json|Set-Content -LiteralPath $replayFile -Encoding UTF8
Assert-Rejected {Import-ElevatedReplayFile $replayFile} 'missing'
# Exercise the actual direct ServiceMode entry. Every external side-effect
# boundary is guarded, so even a missing validation cannot install anything.
$guarded=$source
$nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in @('Select-InstallIdentity','Get-LatestRelease','Install-Tlbx')},$true)|Sort-Object {$_.Extent.StartOffset} -Descending)
foreach($node in $nodes){
    $body=if($node.Name -eq 'Select-InstallIdentity'){''}else{'throw "UNEXPECTED_EXTERNAL_BOUNDARY"'}
    $replacement="function $($node.Name) { $($node.Body.ParamBlock.Extent.Text)`n$body`n}"
    $guarded=$guarded.Remove($node.Extent.StartOffset,$node.Extent.EndOffset-$node.Extent.StartOffset).Insert($node.Extent.StartOffset,$replacement)
}
Assert-Rejected {& ([scriptblock]::Create($guarded)) -ServiceMode -Port 0 -BindAddress '*'} 'Invalid port'
Assert-Rejected {& ([scriptblock]::Create($guarded)) -ServiceMode -Port 2443 -BindAddress 'invalid'} 'Invalid bind'
Write-Host "PASS: Windows installer bounded choices/consent, overflow/occupied ports, loopback normalization, typed atomic replay and direct ServiceMode validation; PowerShell $($PSVersionTable.PSVersion)."
