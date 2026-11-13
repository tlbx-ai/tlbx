#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$files = @(& git -C $repo ls-files '*.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate PowerShell source files.' }
$failures = @()
foreach ($file in $files) {
    $tokens = $null; $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $file), [ref]$tokens, [ref]$errors)
    foreach ($error in $errors) { $failures += "${file}:$($error.Extent.StartLineNumber): $($error.Message)" }
}
if ($failures.Count) { throw ($failures -join "`n") }
if (-not $IsWindows) {
    foreach ($file in @(& git -C $repo ls-files '*.sh')) {
        & bash -n (Join-Path $repo $file)
        if ($LASTEXITCODE -ne 0) { throw "Shell syntax failed: $file" }
    }
}
Write-Host "Release script syntax lint passed ($($files.Count) PowerShell files)."
