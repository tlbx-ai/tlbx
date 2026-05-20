#!/usr/bin/env pwsh

param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Rid,

    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
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
        ExtraArgs = @("-p:IsPublishing=true", "-p:ContinuousIntegrationBuild=true")
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

$processes = @()
Push-Location $RepoRoot
try {
    foreach ($project in $projects) {
        & dotnet restore $project.Path -r $Rid --verbosity minimal
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed for $($project.Name)"
        }
    }

    & dotnet build "src/Ai.Tlbx.MidTerm.Common/Ai.Tlbx.MidTerm.Common.csproj" `
        -c $Configuration `
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
}
finally {
    Pop-Location
    if (Test-Path $logRoot) {
        Remove-Item -LiteralPath $logRoot -Recurse -Force
    }
}
