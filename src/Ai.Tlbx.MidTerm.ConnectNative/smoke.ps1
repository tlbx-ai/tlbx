[CmdletBinding()]
param([Parameter(Mandatory)][string]$RuntimeIdentifier, [Parameter(Mandatory)][string]$NativeDirectory, [Parameter(Mandatory)][string]$NativePrefix)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$build = Join-Path $repoRoot ".artifacts/connect-dotnet-transport/load-smoke/$RuntimeIdentifier"
$source = Join-Path $PSScriptRoot 'smoke'
$configure = @('-S', $source, '-B', $build, '-DCMAKE_BUILD_TYPE=Release', "-DNATIVE_DIRECTORY=$NativeDirectory", "-DNATIVE_PREFIX=$NativePrefix")
if ($IsWindows) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json)[0]
    $major = ([Version]$vs.installationVersion).Major
    $generator = if ($major -eq 18) { 'Visual Studio 18 2026' } elseif ($major -eq 17) { 'Visual Studio 17 2022' } else { throw 'Unsupported installed MSVC generator for native load proof.' }
    $configure += @('-G', $generator, '-A', $(if ($RuntimeIdentifier -eq 'win-x86') { 'Win32' } else { 'x64' }))
} elseif ($IsMacOS) {
    $configure += @('-DCMAKE_OSX_DEPLOYMENT_TARGET=12.0', "-DCMAKE_OSX_ARCHITECTURES=$(if ($RuntimeIdentifier -eq 'osx-arm64') { 'arm64' } else { 'x86_64' })")
}
& cmake @configure
if ($LASTEXITCODE -ne 0) { throw 'Native load-proof configuration failed.' }
& cmake --build $build --config Release --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'Native load-proof compilation failed.' }
$runner = Join-Path $build $(if ($IsWindows) { 'Release/connect-native-load.exe' } else { 'connect-native-load' })
$parser = Join-Path $build $(if ($IsWindows) { 'Release/connect-native-parse.exe' } else { 'connect-native-parse' })
Get-ChildItem -LiteralPath $NativeDirectory -File | Copy-Item -Destination (Split-Path $parser -Parent)
if (-not $IsWindows) {
    # The C++ constructor test links by SONAME. Reuse the verified, possibly signed
    # bundle bytes under the installed SONAME aliases only in this build-test folder.
    $entryFile = Join-Path $NativeDirectory $(if ($IsMacOS) { 'libdatachannel.dylib' } else { 'libdatachannel.so' })
    Get-ChildItem (Join-Path $NativePrefix 'lib') -Filter 'libdatachannel*' -File |
        Where-Object { $_.Name -match '(\.so(\..*)?|\.dylib)$' } |
        ForEach-Object { Copy-Item -LiteralPath $entryFile -Destination (Join-Path (Split-Path $parser -Parent) $_.Name) }
}
if ($IsMacOS -and $env:GITHUB_ACTIONS -eq 'true') {
    & codesign --force --sign 'Developer ID Application: Johannes Schmidt (FK7G5C74WH)' --options runtime --timestamp $runner
    if ($LASTEXITCODE -ne 0) { throw 'Hardened native load-proof signing failed.' }
    & codesign --verify --strict $runner
    if ($LASTEXITCODE -ne 0) { throw 'Hardened native load-proof signature invalid.' }
    & codesign --force --sign 'Developer ID Application: Johannes Schmidt (FK7G5C74WH)' --options runtime --timestamp $parser
    if ($LASTEXITCODE -ne 0) { throw 'Hardened native parser-proof signing failed.' }
}
$apiSource = Get-Content (Join-Path $repoRoot 'src/Ai.Tlbx.MidTerm/Services/Connect/ConnectNativeApi.cs') -Raw
$exports = @([regex]::Matches($apiSource, 'NativeLibrary\.GetExport\(\s*library,\s*"(rtc[A-Za-z0-9]+)"\s*\)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($exports.Count -lt 20) { throw 'Native API export inventory is incomplete.' }
$entry = if ($IsWindows) { 'datachannel.dll' } elseif ($IsMacOS) { 'libdatachannel.dylib' } else { 'libdatachannel.so' }
$library = [IO.Path]::GetFullPath((Join-Path $NativeDirectory $entry))
$oldLd = $env:LD_LIBRARY_PATH
$oldDyld = $env:DYLD_LIBRARY_PATH
try {
    $env:LD_LIBRARY_PATH = $null
    $env:DYLD_LIBRARY_PATH = $null
    $start = [Diagnostics.ProcessStartInfo]::new($runner)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($library)
    foreach ($symbol in $exports) { $start.ArgumentList.Add($symbol) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $finished = $process.WaitForExit(30000)
        if (-not $finished) { $process.Kill($true); $process.WaitForExit() }
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        Set-Content -LiteralPath (Join-Path $build 'load.stdout.log') -Value $output
        Set-Content -LiteralPath (Join-Path $build 'load.stderr.log') -Value $errorOutput
        Write-Host $output
        if (-not $finished -or $process.ExitCode -ne 0) {
            throw "Native $RuntimeIdentifier dependency closure or exact C API export proof failed: $errorOutput"
        }
    } finally { $process.Dispose() }
    $parserStart = [Diagnostics.ProcessStartInfo]::new($parser)
    $parserStart.UseShellExecute = $false
    $parserStart.CreateNoWindow = $true
    $parserStart.RedirectStandardOutput = $true
    $parserStart.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($parserStart)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $finished = $process.WaitForExit(30000)
        if (-not $finished) { $process.Kill($true); $process.WaitForExit() }
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        Set-Content -LiteralPath (Join-Path $build 'parse.stdout.log') -Value $output
        Set-Content -LiteralPath (Join-Path $build 'parse.stderr.log') -Value $errorOutput
        Write-Host $output
        if (-not $finished -or $process.ExitCode -ne 0) { throw "Native $RuntimeIdentifier URL constructor proof failed: $errorOutput" }
    } finally { $process.Dispose() }
} finally { $env:LD_LIBRARY_PATH = $oldLd; $env:DYLD_LIBRARY_PATH = $oldDyld }
