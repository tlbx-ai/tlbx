#Requires -Version 7
<#
.SYNOPSIS
    Build MidTerm AOT for Windows x64

.PARAMETER Reproducible
    Enable reproducible build mode (ContinuousIntegrationBuild=true).
    Use this when building for verification/audit purposes.
#>
param(
    [switch]$Reproducible
)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try
{
    $ciFlag = if ($Reproducible) { '/p:ContinuousIntegrationBuild=true' } else { '' }

    Write-Host "Building tlbx AOT for Windows x64..."
    if ($Reproducible) { Write-Host "  (Reproducible build mode enabled)" }

    dotnet publish -c Release -r win-x64 /p:IsPublishing=true $ciFlag

    $outPath = "bin\Release\net10.0\win-x64\publish\mt.exe"
    if (Test-Path $outPath)
    {
        $size = (Get-Item $outPath).Length / 1MB
        Write-Host "`nBuild complete!"
        Write-Host "Output: $outPath ($([math]::Round($size, 2)) MB)"

        if ($Reproducible)
        {
            $hash = (Get-FileHash $outPath -Algorithm SHA256).Hash
            Write-Host "SHA256: $hash"
        }
    }
}
finally
{
    Pop-Location
}
