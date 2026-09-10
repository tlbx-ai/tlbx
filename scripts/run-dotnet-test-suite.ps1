#!/usr/bin/env pwsh

param(
    [string]$Configuration = "Release",
    [switch]$WarnAsError,
    [ValidateSet('server', 'all')][string]$Suite = 'all'
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

$projects = @(
    @{
        Name = "Ai.Tlbx.MidTerm.Tests"
        Path = "src/Ai.Tlbx.MidTerm.Tests/Ai.Tlbx.MidTerm.Tests.csproj"
        ExtraArgs = @("-p:SkipFrontendBuild=true", "-p:ContinuousIntegrationBuild=true")
    },
    @{
        Name = "Ai.Tlbx.MidTerm.UnitTests"
        Path = "src/Ai.Tlbx.MidTerm.UnitTests/Ai.Tlbx.MidTerm.UnitTests.csproj"
        ExtraArgs = @("-p:SkipFrontendBuild=true", "-p:ContinuousIntegrationBuild=true")
    },
    @{
        Name = "Ai.Tlbx.MidTerm.AgentHost.UnitTests"
        Path = "src/Ai.Tlbx.MidTerm.AgentHost.UnitTests/Ai.Tlbx.MidTerm.AgentHost.UnitTests.csproj"
        ExtraArgs = @("-p:ContinuousIntegrationBuild=true")
    }
)

if ($Suite -eq 'server') {
    $projects = @($projects | Where-Object Name -ne 'Ai.Tlbx.MidTerm.AgentHost.UnitTests')
}

Push-Location $RepoRoot
try {
    foreach ($project in $projects) {
        Write-Host ""
        Write-Host "Running .NET tests: $($project.Name)" -ForegroundColor Cyan
        $args = @(
            "test",
            $project.Path,
            "-c", $Configuration,
            "--nologo",
            "--verbosity", "minimal"
        ) + $project.ExtraArgs

        if ($WarnAsError) {
            $args += "-warnaserror"
        }

        & dotnet @args
        if ($LASTEXITCODE -ne 0) {
            throw ".NET test suite failed for $($project.Name)"
        }
    }
}
finally {
    Pop-Location
}
