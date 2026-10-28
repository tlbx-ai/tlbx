. (Join-Path $PSScriptRoot '../../scripts/advisory-regression.ps1')

function Read-ConnectGoAuditReport([string]$Path, [string]$RuntimeIdentifier) {
    if (-not ('TlbxConnect.GoAuditJson' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
namespace TlbxConnect {
    public static class GoAuditJson {
        public static string[] Read(string text) {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            int offset = 0;
            var records = new List<string>();
            while (offset < bytes.Length) {
                while (offset < bytes.Length && (bytes[offset] == 32 || bytes[offset] == 9 || bytes[offset] == 10 || bytes[offset] == 13)) offset++;
                if (offset == bytes.Length) break;
                var reader = new Utf8JsonReader(bytes.AsSpan(offset), true, default(JsonReaderState));
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new FormatException("Expected a Go audit JSON object.");
                using (var document = JsonDocument.ParseValue(ref reader)) records.Add(document.RootElement.GetRawText());
                offset += checked((int)reader.BytesConsumed);
            }
            return records.ToArray();
        }
    }
}
'@
    }
    $records = @([TlbxConnect.GoAuditJson]::Read([IO.File]::ReadAllText($Path)) | ForEach-Object { $_ | ConvertFrom-Json })
    $configs = @($records | Where-Object config)
    $sboms = @($records | Where-Object SBOM)
    if ($configs.Count -ne 1 -or $configs[0].config.protocol_version -ne 'v1.0.0' -or
        $configs[0].config.scanner_name -ne 'govulncheck' -or $configs[0].config.scan_level -ne 'package' -or
        $sboms.Count -ne 1 -or -not $sboms[0].SBOM.modules.Count -or @($records | Where-Object error).Count) {
        throw 'govulncheck did not return a complete package-level advisory report.'
    }
    $findings = @{}
    $moduleOnly = @{}
    foreach ($record in $records | Where-Object finding) {
        $finding = $record.finding
        $frame = @($finding.trace)[0]
        if (-not $finding.osv -or -not $frame.module) { throw 'Malformed Go advisory finding.' }
        if (-not $frame.package) {
            $moduleOnly["$($frame.module)|$($finding.osv)"] = "$($frame.module) $($finding.osv)"
            continue
        }
        $value = New-AdvisoryFinding $frame.module $finding.osv unknown "runtime-$RuntimeIdentifier"
        # Count distinct affected imported packages, so newly exposed packages
        # are regressions even if their module/advisory already existed.
        $key = "$($value.Key)|$($frame.package)"
        $findings[$key] = $value
    }
    $merged = @{}
    foreach ($finding in $findings.Values) {
        if ($merged.ContainsKey($finding.Key)) { $merged[$finding.Key].Count++ }
        else { $merged[$finding.Key] = $finding }
    }
    return @{ Findings = @($merged.Values); ModuleOnly = @($moduleOnly.Values) }
}

function Invoke-ConnectGoAudit {
    param([string]$Executable,[string]$WorkingDirectory,[string]$RuntimeIdentifier,[string]$OutputDirectory)
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    $stdout = Join-Path $OutputDirectory "$RuntimeIdentifier.json"
    $stderr = Join-Path $OutputDirectory "$RuntimeIdentifier.stderr.log"
    Push-Location $WorkingDirectory
    try {
        & $Executable -scan package -format json ./... 1> $stdout 2> $stderr
        if ($LASTEXITCODE -notin @(0,3)) { throw "Go advisory query failed for $RuntimeIdentifier; see $stderr" }
        return Read-ConnectGoAuditReport $stdout $RuntimeIdentifier
    }
    finally { Pop-Location }
}

function Invoke-ConnectGoAdvisoryGate {
    param([string]$RepoRoot,[hashtable]$Baseline)
    $source = Join-Path $RepoRoot 'src/Ai.Tlbx.MidTerm.ConnectTransport'
    . (Join-Path $source 'build.ps1') -RuntimeIdentifier $(
        if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { 'linux-x64' }
    ) -SkipTests | Out-Null
    $goExecutable = $go
    $savedGoos = $env:GOOS; $savedGoarch = $env:GOARCH; $savedGobin = $env:GOBIN; $savedPath = $env:PATH
    $auditRoot = Join-Path $source '.artifacts/advisories'
    $relative = 'src/Ai.Tlbx.MidTerm.ConnectTransport'
    $oldSource = if ($Baseline) { Join-Path $Baseline.Root $relative } else { $null }
    $exported = $false
    $moduleAdvisories = @{}
    try {
        $env:PATH = (Split-Path $goExecutable -Parent) + [IO.Path]::PathSeparator + $savedPath
        $env:GOOS = $null; $env:GOARCH = $null
        $toolDirectory = Join-Path $RepoRoot '.dev/tools/govulncheck-v1.9.0'
        $executable = Join-Path $toolDirectory $(if ($IsWindows) { 'govulncheck.exe' } else { 'govulncheck' })
        if (-not (Test-Path -LiteralPath $executable)) {
            New-Item -ItemType Directory -Force $toolDirectory | Out-Null
            $env:GOBIN = $toolDirectory
            & $goExecutable install golang.org/x/vuln/cmd/govulncheck@v1.9.0
            if ($LASTEXITCODE -ne 0) { throw 'Pinned govulncheck installation failed.' }
        }
        $targets = @(
            @{rid='win-x64';os='windows';arch='amd64'}, @{rid='win-x86';os='windows';arch='386'},
            @{rid='linux-x64';os='linux';arch='amd64'}, @{rid='linux-arm64';os='linux';arch='arm64'},
            @{rid='osx-x64';os='darwin';arch='amd64'}, @{rid='osx-arm64';os='darwin';arch='arm64'}
        )
        foreach ($target in $targets) {
            $env:GOOS=$target.os; $env:GOARCH=$target.arch
            $report = Invoke-ConnectGoAudit $executable $source $target.rid (Join-Path $auditRoot 'candidate')
            foreach ($advisory in $report.ModuleOnly) { $moduleAdvisories[$advisory] = $true }
            $previous = @()
            if ($report.Findings.Count -gt 0 -and $Baseline) {
                if (-not $exported) {
                    # Source-level package exposure requires the old Go imports,
                    # alongside the old locks. Export only this small transport
                    # module into the existing tag-keyed audit cache, no checkout.
                    $paths = @(& git -C $RepoRoot ls-tree -r --name-only $Baseline.Commit -- $relative)
                    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate Go advisory baseline inputs.' }
                    foreach ($path in $paths | Where-Object { $_ -match '/(go\.(mod|sum)|[^/]+\.go)$' -and $_ -notlike '*_test.go' }) {
                        if (-not (Export-AdvisoryBaselineFile $RepoRoot $Baseline $path)) { throw "Cannot export Go baseline $path" }
                    }
                    $exported=$true
                }
                if (Test-Path -LiteralPath (Join-Path $oldSource 'go.mod')) {
                    $old = Invoke-ConnectGoAudit $executable $oldSource $target.rid (Join-Path $auditRoot 'baseline')
                    $previous = $old.Findings
                }
            }
            Assert-NoAdvisoryRegression $report.Findings $previous "Go $($target.rid)"
        }
        foreach ($advisory in $moduleAdvisories.Keys | Sort-Object) {
            Write-Warning "Go advisory in an unimported package (not linked into the transport): $advisory"
        }
    }
    finally { $env:GOOS=$savedGoos; $env:GOARCH=$savedGoarch; $env:GOBIN=$savedGobin; $env:PATH=$savedPath }
}
