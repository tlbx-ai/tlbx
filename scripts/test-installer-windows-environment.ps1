[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { return }
$source=Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'install.ps1') -Raw -Encoding UTF8
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ($errors.Message -join '; ') }
foreach ($name in @('Test-InstallerPbkdf2Support','Assert-InstallerPrerequisites','Get-InstallerEnvironmentValue','Get-InstallerSpecialFolder','Get-InstallerNativeProgramFiles','Resolve-InstallerDirectory','Resolve-InstallerEnvironmentPaths','Invoke-InstallerVersionProbe','Assert-VerifiedWindowsRuntime')) {
 $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
 if (-not $node) { throw "Missing installer function $name" }; Invoke-Expression $node.Extent.Text
}
$script:Passed=0
function Assert-True($Value,[string]$Message) { if (-not $Value) { throw $Message }; $script:Passed++ }
function Assert-Fails([scriptblock]$Action,[string]$Pattern) { $failure=$null; try { & $Action } catch { $failure=$_.ToString() }; Assert-True ($failure -match $Pattern) "Expected '$Pattern'; got '$failure'" }
Assert-InstallerPrerequisites
Assert-True (Test-InstallerPbkdf2Support) 'Current shell must support PBKDF2-SHA256.'
Assert-Fails { Assert-InstallerPrerequisites -WindowsPlatform $false } 'Windows only'
Assert-Fails { Assert-InstallerPrerequisites -PowerShellVersion ([version]'5.0') } 'requires Windows PowerShell 5.1'
Assert-Fails { Assert-InstallerPrerequisites -LanguageMode ConstrainedLanguage } 'requires PowerShell FullLanguage'
& { function Test-InstallerPbkdf2Support { return $false }; Assert-Fails { Assert-InstallerPrerequisites } 'lacks PBKDF2-HMAC-SHA256' }
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('tlbx-environment-'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($fixture)
try {
 & {
  $script:FixtureEnv=@{}
  $script:FixtureFolders=@{UserProfile=(Join-Path $fixture '[profile] ä');LocalApplicationData=(Join-Path $fixture 'Local');Windows=(Join-Path $fixture 'Windows');CommonApplicationData=(Join-Path $fixture 'Data');ProgramFiles=(Join-Path $fixture 'Programs')}
  foreach ($dir in $script:FixtureFolders.Values) { $null=[IO.Directory]::CreateDirectory($dir) }
  foreach ($dir in @('NativePrograms','Programs-native','Programs-x86')) { $null=[IO.Directory]::CreateDirectory((Join-Path $fixture $dir)) }
  function Get-InstallerEnvironmentValue([string]$Name) { return $script:FixtureEnv[$Name] }
  function Get-InstallerSpecialFolder([string]$Name) { return $script:FixtureFolders[$Name] }
  function Get-InstallerNativeProgramFiles { return (Join-Path $fixture 'NativePrograms') }
  $beforeTemp=$env:TEMP
  $paths=Resolve-InstallerEnvironmentPaths -Is64BitOperatingSystem $true -Is64BitProcess $true
  Assert-True ($paths.UserProfile -eq $script:FixtureFolders.UserProfile) 'Missing USERPROFILE must use OS profile, preserving brackets and Unicode.'
  Assert-True ($paths.ProgramData -eq $script:FixtureFolders.CommonApplicationData) 'Missing ProgramData must use OS data.'
  Assert-True ($paths.ProgramFiles -eq $script:FixtureFolders.ProgramFiles) 'Missing ProgramFiles must use OS programs.'
  Assert-True ($paths.TempRoot -eq (Join-Path $script:FixtureFolders.LocalApplicationData 'Temp')) 'Missing TEMP/TMP must use writable profile temp.'
  Assert-True ($env:TEMP -eq $beforeTemp) 'Resolver must not mutate caller environment.'
  $script:FixtureEnv.USERPROFILE=Join-Path $fixture 'nonexistent'
  Assert-True ((Resolve-InstallerEnvironmentPaths).UserProfile -eq $script:FixtureFolders.UserProfile) 'Nonexistent environment profile must fall back to OS profile.'
  $script:FixtureEnv.ProgramFiles=Join-Path $fixture 'Programs-x86';$script:FixtureEnv.ProgramW6432=Join-Path $fixture 'Programs-native'
  $paths=Resolve-InstallerEnvironmentPaths -Is64BitOperatingSystem $true -Is64BitProcess $false
  Assert-True ($paths.ProgramFiles -eq $script:FixtureEnv.ProgramW6432) '32-bit shell must choose native ProgramW6432.'
  $script:FixtureEnv.Remove('ProgramW6432')
  Assert-True ((Resolve-InstallerEnvironmentPaths -Is64BitOperatingSystem $true -Is64BitProcess $false).ProgramFiles -eq (Join-Path $fixture 'NativePrograms')) 'Missing ProgramW6432 must use native registry fallback.'
  $file=Join-Path $fixture 'not-directory';[IO.File]::WriteAllText($file,'fixture');$script:FixtureEnv.TEMP=$file;$script:FixtureEnv.TMP='relative-temp'
  $paths=Resolve-InstallerEnvironmentPaths
  Assert-True ($paths.TempRoot -eq (Join-Path $script:FixtureFolders.LocalApplicationData 'Temp')) 'Invalid temp paths must fall back.'
  Assert-Fails { Resolve-InstallerDirectory -Name Fixture -Candidates @('relative',$file) } 'Could not resolve'
  Assert-True (@(Get-ChildItem -LiteralPath $paths.TempRoot -Force).Count -eq 0) 'Writable probe must remove sentinel.'
 }
 $program=@"
using System; using System.IO; using System.Threading;
public static class InstallerVersionFixture {
 public static int Main(string[] args) {
  if(args.Length!=1 || args[0]!="--version") return 99;
  string mode=Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]);
  if(mode=="hang") { Thread.Sleep(10000);return 0; }
  if(mode=="failure") { Console.Error.WriteLine("fixture native loader rejected");return 7; }
  if(mode=="flood") { Console.Out.Write(new string('x',160000));Console.Error.Write(new string('e',160000));return 0; }
  Console.WriteLine(mode=="wrong" ? "9.9.9" : "1.2.3");return 0;
 }
}
"@
 [IO.File]::WriteAllText((Join-Path $fixture 'probe.cs'),$program)
 $compile=Join-Path $fixture 'compile.ps1'
 [IO.File]::WriteAllText($compile,'param($Source,$Output) $ErrorActionPreference="Stop"; Add-Type -TypeDefinition ([IO.File]::ReadAllText($Source)) -Language CSharp -OutputAssembly $Output -OutputType ConsoleApplication')
 $windowsPowerShell=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'System32\WindowsPowerShell\v1.0\powershell.exe'
 & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $compile (Join-Path $fixture 'probe.cs') (Join-Path $fixture 'success.exe')
 if ($LASTEXITCODE -ne 0) { throw 'Inert fixture compilation failed.' }
 foreach ($mode in @('wrong','failure','hang','flood')) { Copy-Item -LiteralPath (Join-Path $fixture 'success.exe') -Destination (Join-Path $fixture ($mode+'.exe')) }
 Assert-True ((Invoke-InstallerVersionProbe -ExecutablePath (Join-Path $fixture 'success.exe') -ExpectedVersion '1.2.3') -eq '1.2.3') 'Exact version must succeed.'
 Assert-Fails { Invoke-InstallerVersionProbe -ExecutablePath (Join-Path $fixture 'wrong.exe') -ExpectedVersion '1.2.3' } 'version mismatch'
 Assert-Fails { Invoke-InstallerVersionProbe -ExecutablePath (Join-Path $fixture 'failure.exe') -ExpectedVersion '1.2.3' } 'exit 7.*fixture native loader rejected'
 $watch=[Diagnostics.Stopwatch]::StartNew()
 Assert-Fails { Invoke-InstallerVersionProbe -ExecutablePath (Join-Path $fixture 'hang.exe') -ExpectedVersion '1.2.3' -TimeoutMilliseconds 150 } 'timed out'
 Assert-True ($watch.Elapsed.TotalSeconds -lt 5) 'Hung probe must terminate within a bound.'
 Assert-Fails { Invoke-InstallerVersionProbe -ExecutablePath (Join-Path $fixture 'flood.exe') -ExpectedVersion '1.2.3' -TimeoutMilliseconds 5000 } 'version mismatch'
 Assert-Fails { Invoke-InstallerVersionProbe -ExecutablePath (Join-Path $fixture 'missing.exe') -ExpectedVersion '1.2.3' } 'could not start'
 & {
  $script:ProbeCalls=@();function Write-Log { }
  function Invoke-InstallerVersionProbe { param($ExecutablePath,$ExpectedVersion) $script:ProbeCalls+=@{File=(Split-Path $ExecutablePath -Leaf);Version=$ExpectedVersion} }
  foreach ($file in @('mtagenthost.exe','mthost.exe','mttmux.exe')) { [IO.File]::WriteAllText((Join-Path $fixture $file),'inert') }
  [IO.File]::WriteAllText((Join-Path $fixture 'version.json'),'{"pty":"2.3.4"}')
  Assert-VerifiedWindowsRuntime -ExtractDir $fixture -ExpectedVersion '3.4.5-dev'
  Assert-True (($script:ProbeCalls.File -join ',') -eq 'mt.exe,mtagenthost.exe,mthost.exe,mttmux.exe') 'Probe all native binaries.'
  Assert-True (($script:ProbeCalls.Version -join ',') -eq '3.4.5-dev,2.3.4,2.3.4,3.5a-midterm') 'Use correct web and bundled host versions.'
 }
 $prefix=$source.Substring(0,$source.IndexOf('$script:InstallerPaths = Resolve-InstallerEnvironmentPaths'))
 $prefixPath=Join-Path $fixture 'guard-prefix.ps1';[IO.File]::WriteAllText($prefixPath,$prefix)
 $guard=Join-Path $fixture 'constrained.ps1'
 $guardSource="param(`$Prefix)`r`n`$ErrorActionPreference='Stop';`$text=[IO.File]::ReadAllText(`$Prefix);`$ExecutionContext.SessionState.LanguageMode='ConstrainedLanguage';try { Invoke-Expression `$text;throw 'unexpected acceptance' } catch { if(`$_.ToString() -notmatch 'requires PowerShell FullLanguage') { throw };Write-Output 'guard rejected' }"
 [IO.File]::WriteAllText($guard,$guardSource)
 $result=& $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $guard $prefixPath 2>&1
 Assert-True ($LASTEXITCODE -eq 0 -and ($result -join ' ') -match 'guard rejected') "Early constrained guard failed: $result"
 Write-Host "PASS: $script:Passed Windows environment/native startup checks (PowerShell $($PSVersionTable.PSVersion))."
} finally {
 if ($fixture -and $fixture.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}