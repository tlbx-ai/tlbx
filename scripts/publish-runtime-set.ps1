#!/usr/bin/env pwsh

param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Rid,

    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
. "$PSScriptRoot/runtime-reuse.ps1"
$version = Get-Content (Join-Path $RepoRoot 'src/version.json') -Raw | ConvertFrom-Json
$sdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the .NET SDK version.' }
$fingerprint = Get-RuntimeInputFingerprint $RepoRoot $Rid $Configuration $version.pty $sdkVersion
$hostsReused = Restore-ReleasedHostRuntimes $RepoRoot $Rid $Configuration $version $fingerprint
$metadataDir = Join-Path $RepoRoot ".artifacts/runtime-inputs/$Rid"
New-Item -ItemType Directory -Force $metadataDir | Out-Null
@{ schema = 1; fingerprint = $fingerprint; rid = $Rid; pty = $version.pty; sdk = $sdkVersion } |
    ConvertTo-Json | Set-Content (Join-Path $metadataDir 'runtime-inputs.json')
if ($env:GITHUB_ENV) { "TLBX_HOSTS_REUSED=$($hostsReused.ToString().ToLowerInvariant())" | Add-Content $env:GITHUB_ENV }
$logRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("midterm-publish-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

$projects = @(
    @{
        Name = "mt"
        Path = "src/Ai.Tlbx.MidTerm/Ai.Tlbx.MidTerm.csproj"
        ExtraArgs = @("-p:IsPublishing=true", "-p:SkipFrontendBuild=true", "-p:ContinuousIntegrationBuild=true")
    },
    @{
        Name = "mthost"
        Path = "src/Ai.Tlbx.MidTerm.TtyHost/Ai.Tlbx.MidTerm.TtyHost.csproj"
        ExtraArgs = @(
            "-f", $(if ($Rid.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)) { "net10.0-windows10.0.19041.0" } else { "net10.0" }),
            "-p:IsPublishing=true",
            "-p:ContinuousIntegrationBuild=true"
        )
    },
    @{
        Name = "mtagenthost"
        Path = "src/Ai.Tlbx.MidTerm.AgentHost/Ai.Tlbx.MidTerm.AgentHost.csproj"
        ExtraArgs = @("-p:IsPublishing=true", "-p:ContinuousIntegrationBuild=true")
    },
    @{
        Name = "mttmux"
        Path = "src/Ai.Tlbx.MidTerm.TmuxShim/Ai.Tlbx.MidTerm.TmuxShim.csproj"
        ExtraArgs = @("-p:IsPublishing=true", "-p:ContinuousIntegrationBuild=true")
    }
)

if (-not $Rid.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)) {
    $projects = @($projects | Where-Object { $_.Name -ne "mttmux" })
}

$processes = @()
if ($hostsReused) { $projects = @($projects | Where-Object Name -eq 'mt') }
Push-Location $RepoRoot
try {
    foreach ($project in $projects) {
        # Each project declares and locks every RID it publishes. Restore that complete
        # graph once so the selected-RID publish below cannot rewrite dependency state.
        & dotnet restore $project.Path --locked-mode --verbosity minimal
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed for $($project.Name)"
        }
    }

    & dotnet build "src/Ai.Tlbx.MidTerm.Common/Ai.Tlbx.MidTerm.Common.csproj" `
        -c $Configuration `
        -r $Rid `
        --no-restore `
        --verbosity minimal `
        -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for Ai.Tlbx.MidTerm.Common"
    }

    foreach ($project in $projects) {
        $stdoutPath = Join-Path $logRoot "$($project.Name).stdout.log"
        $stderrPath = Join-Path $logRoot "$($project.Name).stderr.log"
        $argumentList = @(
            "publish",
            $project.Path,
            "-c", $Configuration,
            "-r", $Rid,
            "--verbosity", "minimal",
            "--no-restore",
            "-p:BuildProjectReferences=false"
        ) + $project.ExtraArgs

        $startInfo = @{
            FilePath = "dotnet"
            ArgumentList = $argumentList
            WorkingDirectory = $RepoRoot
            RedirectStandardOutput = $stdoutPath
            RedirectStandardError = $stderrPath
            PassThru = $true
        }
        if ($IsWindows) {
            $startInfo.WindowStyle = "Hidden"
        }

        $process = Start-Process @startInfo

        $processes += @{
            Name = $project.Name
            Process = $process
            StdoutPath = $stdoutPath
            StderrPath = $stderrPath
        }
    }

    foreach ($entry in $processes) {
        $null = $entry.Process.WaitForExit()
    }

    $failed = $processes | Where-Object { $_.Process.ExitCode -ne 0 }
    if ($failed.Count -gt 0) {
        foreach ($entry in $processes) {
            Write-Host ""
            Write-Host "=== $($entry.Name) stdout ===" -ForegroundColor Yellow
            if (Test-Path $entry.StdoutPath) {
                Get-Content $entry.StdoutPath
            }
            Write-Host ""
            Write-Host "=== $($entry.Name) stderr ===" -ForegroundColor Yellow
            if (Test-Path $entry.StderrPath) {
                Get-Content $entry.StderrPath
            }
        }

        $failedNames = ($failed | ForEach-Object { $_.Name }) -join ", "
        throw "Parallel dotnet publish failed for: $failedNames"
    }

    if ($Rid -in @("win-x64", "win-x86")) {
        $mthostPublishDir = Join-Path $RepoRoot "src/Ai.Tlbx.MidTerm.TtyHost/bin/$Configuration/net10.0-windows10.0.19041.0/$Rid/publish"
        $requiredConptyFiles = @("conpty.dll", "x64/OpenConsole.exe", "arm64/OpenConsole.exe")
        if ($Rid -eq "win-x86") {
            $requiredConptyFiles += "x86/OpenConsole.exe"
        }
        foreach ($relativePath in $requiredConptyFiles) {
            $path = Join-Path $mthostPublishDir $relativePath
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Published $Rid mthost runtime is missing $relativePath"
            }
        }
    }
}
finally {
    Pop-Location
    if (Test-Path $logRoot) {
        Remove-Item -LiteralPath $logRoot -Recurse -Force
    }
}
