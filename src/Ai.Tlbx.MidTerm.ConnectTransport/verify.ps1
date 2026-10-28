$ErrorActionPreference = 'Stop'
$hostRid = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) {
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -eq 'Arm64') { 'osx-arm64' } else { 'osx-x64' }
} else {
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -eq 'Arm64') { 'linux-arm64' } else { 'linux-x64' }
}
. (Join-Path $PSScriptRoot 'build.ps1') -RuntimeIdentifier $hostRid -SkipTests | Out-Null
Push-Location $PSScriptRoot
try {
    & $go test ./...
    if ($LASTEXITCODE -ne 0) { throw 'Connect transport protocol tests failed.' }
    & $go vet ./...
    if ($LASTEXITCODE -ne 0) { throw 'Connect transport go vet failed.' }
}
finally { Pop-Location }
$global:LASTEXITCODE=0
