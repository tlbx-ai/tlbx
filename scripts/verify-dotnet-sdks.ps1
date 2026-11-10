param(
    [ValidateSet('x64', 'x86', 'arm64')]
    [string]$Architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant(),
    [string[]]$SdkVersions = @('10.0.402', '11.0.100')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$requiredSdk = (Get-Content (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
Push-Location $repo
try {
    $selected = dotnet --version
    if ($LASTEXITCODE -ne 0 -or $selected -ne $requiredSdk) {
        throw "Repository SDK selection changed: expected $requiredSdk, got $selected."
    }
} finally {
    Pop-Location
}

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('tlbx-dotnet-sdks-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
Push-Location $fixture
try {
    foreach ($sdk in $SdkVersions) {
        $major = ([version]$sdk).Major
        @{ sdk = @{ version = $sdk; rollForward = 'disable'; allowPrerelease = $false } } |
            ConvertTo-Json -Depth 3 | Set-Content global.json
        $selected = dotnet --version
        if ($LASTEXITCODE -ne 0 -or $selected -ne $sdk) {
            throw "SDK $sdk is unavailable (selected: $selected)."
        }
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net$major.0</TargetFramework>
  </PropertyGroup>
</Project>
"@ | Set-Content Smoke.csproj
        @'
using System;
using System.Runtime.InteropServices;
Console.WriteLine($"{Environment.Version.Major}:{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}");
'@ | Set-Content Program.cs
        dotnet build Smoke.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "SDK $sdk could not compile the smoke fixture." }
        $actual = dotnet "bin/Release/net$major.0/Smoke.dll"
        if ($LASTEXITCODE -ne 0 -or $actual -ne "${major}:$Architecture") {
            throw "SDK $sdk runtime/architecture mismatch: expected ${major}:$Architecture, got $actual."
        }
        Write-Host "PASS: SDK $sdk compiled and executed net$major.0 on $Architecture; repository stays on $requiredSdk."
    }
} finally {
    Pop-Location
    # Only remove the unique fixture directory created above, under the OS temp root.
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ([IO.Path]::GetFullPath($fixture).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
