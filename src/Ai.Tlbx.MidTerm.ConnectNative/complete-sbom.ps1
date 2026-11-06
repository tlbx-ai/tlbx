param([Parameter(Mandatory)][string]$SbomPath)
$ErrorActionPreference = 'Stop'
& python (Join-Path $PSScriptRoot 'complete-sbom.py') --sbom-path $SbomPath
if ($LASTEXITCODE -ne 0) { throw 'Native Connect SBOM completion failed.' }
