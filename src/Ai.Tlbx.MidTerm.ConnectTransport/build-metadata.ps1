# Dependency metadata comes from the compiled executable, not merely go.mod.
function Get-ConnectLicense([string]$Directory) {
    $files = @(Get-ChildItem -LiteralPath $Directory -File |
        Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)' } | Sort-Object Name)
    if (-not $files.Count) { throw "Missing Go dependency license: $Directory" }
    $text = ($files | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
    $text = $text.Replace("`r`n", "`n")
    $id = if ($text -match 'Permission is hereby granted, free of charge' -and $text -match 'THE SOFTWARE IS PROVIDED .AS IS.') { 'MIT' }
        elseif ($text -match 'Redistribution and use in source and binary forms' -and $text -match 'Neither the name') { 'BSD-3-Clause' }
        else { 'LicenseRef-Go-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).Substring(0, 20).ToLowerInvariant() }
    return @{ id = $id; text = $text }
}

function Write-ConnectBuildMetadata {
    param([string]$Go, [string]$BinaryPath, [string]$RuntimeIdentifier)
    $output = & $Go version -m -json $BinaryPath
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect compiled Connect transport build information.' }
    $info = ($output -join "`n") | ConvertFrom-Json
    if ($info.Main.Path -ne 'github.com/tlbx-ai/tlbx/connecttransport' -or $info.GoVersion -notmatch '^go\d+\.\d+\.\d+$') {
        throw 'Unexpected Connect transport binary identity.'
    }
    $modules = @{}
    $lines = & $Go list -m -f '{{if not .Main}}{{.Path}}|{{.Version}}|{{.Dir}}{{end}}' all
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve compiled Go module licenses.' }
    foreach ($line in $lines | Where-Object { $_ }) {
        $path, $version, $directory = $line -split '\|', 3
        $modules[$path] = @{ version = $version; directory = $directory }
    }
    $dependencies = foreach ($dependency in $info.Deps) {
        $module = $modules[$dependency.Path]
        if ($dependency.Replace -or -not $module.directory -or $module.version -ne $dependency.Version -or $dependency.Sum -notmatch '^h1:[A-Za-z0-9+/]+=*$') {
            throw "Compiled Go dependency cannot be matched to its pinned source: $($dependency.Path)"
        }
        $license = Get-ConnectLicense $module.directory
        @{
            path = $dependency.Path; version = $dependency.Version; sum = $dependency.Sum
            license = $license.id; licenseText = $license.text
        }
    }
    $goRoot = & $Go env GOROOT
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve Go runtime license.' }
    $runtimeLicense = Get-ConnectLicense $goRoot
    $settings = @{}
    foreach ($setting in $info.Settings) { $settings[$setting.Key] = $setting.Value }
    $metadata = @{
        schema = 1; runtimeIdentifier = $RuntimeIdentifier
        binarySha256 = (Get-FileHash -LiteralPath $BinaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
        goVersion = $info.GoVersion; module = $info.Main.Path
        goos = $settings.GOOS; goarch = $settings.GOARCH; cgo = $settings.CGO_ENABLED
        runtimeLicense = $runtimeLicense.id; runtimeLicenseText = $runtimeLicense.text
        dependencies = @($dependencies)
    }
    $destination = Join-Path (Split-Path $BinaryPath -Parent) 'build-info.json'
    [IO.File]::WriteAllText($destination, ($metadata | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}
