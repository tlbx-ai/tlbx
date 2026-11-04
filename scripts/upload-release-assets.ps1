#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Tag,
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$SbomPath
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

foreach ($path in @($ArchivePath, $SbomPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Missing or empty release file: $path"
    }
}

# Retry only the transfer of these existing files, never compilation or a workflow.
# The caller's workflow step bounds the total upload time, including these delays.
for ($attempt = 1; $attempt -le 3; $attempt++) {
    & gh release upload $Tag $ArchivePath $SbomPath --clobber
    if ($LASTEXITCODE -eq 0) { return }
    if ($attempt -lt 3) {
        Write-Warning "Release upload attempt $attempt/3 failed; retrying the same package."
        Start-Sleep -Seconds (5 * $attempt)
    }
}
throw 'Release upload failed after 3 attempts.'
